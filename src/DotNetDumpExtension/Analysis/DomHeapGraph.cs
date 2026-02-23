using System.Collections.Generic;
using Microsoft.Diagnostics.Runtime;

namespace DotNetDumpExtension.Analysis;

/// <summary>
/// Builds the reference graph needed for dominator-tree computation.
///
/// Assigns a dense integer node ID to every live object in DFS postorder from GC roots
/// so the synthetic root receives the highest index N. An unreachable sentinel sits at N+1.
/// Predecessor lists are the reverse of the object-reference edges.
/// </summary>
public sealed class DomHeapGraph
{
    private readonly ulong[]    _addresses;
    private readonly ulong[]    _sizes;
    private readonly string[]   _typeNames;
    private readonly List<int>[] _predecessors;
    private readonly Dictionary<ulong, int> _addressToId;

    public DomHeapGraph(ClrRuntime runtime)
    {
        Build(runtime,
              out _addresses,
              out _sizes,
              out _typeNames,
              out _predecessors,
              out _addressToId,
              out int rootIndex);

        RootNodeIndex        = rootIndex;
        UnreachableNodeIndex = rootIndex + 1;
        NumberOfNodes        = rootIndex + 2;
    }

    /// <summary>N — synthetic root (highest postorder index).</summary>
    public int RootNodeIndex { get; }

    /// <summary>N+1 — sentinel; initial dominator for every non-root node.</summary>
    public int UnreachableNodeIndex { get; }

    /// <summary>N+2 total nodes.</summary>
    public int NumberOfNodes { get; }

    /// <summary>Object's own (exclusive) size in bytes. Returns 0 for root/sentinel.</summary>
    public ulong ExclusiveSize(int nodeIndex) => _sizes[nodeIndex];

    /// <summary>Fully-qualified CLR type name. Returns special labels for root/sentinel.</summary>
    public string TypeName(int nodeIndex) => _typeNames[nodeIndex];

    /// <summary>
    /// Heap address. Returns 0 for the synthetic root (RootNodeIndex) and
    /// unreachable sentinel (UnreachableNodeIndex).
    /// </summary>
    public ulong ObjectAddress(int nodeIndex) => _addresses[nodeIndex];

    /// <summary>Nodes that hold a reference TO nodeIndex (reverse / predecessor edges).</summary>
    public List<int> Predecessors(int nodeIndex) => _predecessors[nodeIndex];

    /// <summary>
    /// Looks up the node index for a given heap object address.
    /// Returns false if the address is not present in the graph (e.g. not a live object).
    /// </summary>
    public bool TryGetNodeIndex(ulong address, out int nodeIndex)
        => _addressToId.TryGetValue(address, out nodeIndex);

    // -------------------------------------------------------------------------
    //  Construction
    // -------------------------------------------------------------------------

    private static void Build(
        ClrRuntime                  runtime,
        out ulong[]                 outAddresses,
        out ulong[]                 outSizes,
        out string[]                outTypeNames,
        out List<int>[]             outPredecessors,
        out Dictionary<ulong, int>  outAddressToId,
        out int                     rootIndex)
    {
        ClrHeap heap = runtime.Heap;

        // --- Step 1: collect valid, deduplicated root object addresses ----------
        var rootAddresses = new HashSet<ulong>();
        foreach (ClrRoot root in heap.EnumerateRoots())
        {
            ClrObject obj = root.Object;
            if (obj.IsValid && obj.Address != 0)
                rootAddresses.Add(obj.Address);
        }

        // --- Step 2: iterative DFS — assign postorder IDs ----------------------
        // Stack frame: (objectAddress, isFinishMarker)
        // isFinishMarker → all children already pushed; now assign the postorder ID.
        var stack    = new Stack<(ulong addr, bool finish)>();
        var visited  = new HashSet<ulong>();

        // Postorder-ordered lists (index = node ID).
        var poAddresses = new List<ulong>();
        var poSizes     = new List<ulong>();
        var poTypes     = new List<string>();

        // ALL reference edges in the heap graph: (childAddr, parentAddr).
        // Every edge is recorded regardless of whether the child was already visited,
        // so the predecessor lists are complete for the dominator algorithm.
        // Edges from the synthetic root to root objects are added separately in Step 6.
        var rawEdges = new List<(ulong childAddr, ulong parentAddr)>();

        foreach (ulong addr in rootAddresses)
        {
            if (visited.Add(addr))
                stack.Push((addr, false));
        }

        while (stack.Count > 0)
        {
            var (addr, finish) = stack.Pop();

            if (finish)
            {
                // All descendants are processed; assign this node's postorder ID.
                poAddresses.Add(addr);

                ClrObject obj = heap.GetObject(addr);
                poSizes.Add(obj.Size);
                poTypes.Add(obj.Type?.Name ?? "");

                continue;
            }

            // Push finish marker so this node is assigned its ID after all children.
            stack.Push((addr, true));

            // Enumerate all outgoing references.  Record every edge unconditionally
            // (not just spanning-tree edges) so that objects reachable via multiple
            // independent paths get all their actual predecessors in the predecessor list.
            ClrObject current = heap.GetObject(addr);

            foreach (ClrObject child in current.EnumerateReferences(carefully: true))
            {
                if (!child.IsValid || child.Address == 0)
                    continue;

                rawEdges.Add((child.Address, addr));
                if (visited.Add(child.Address))
                    stack.Push((child.Address, false));
            }
        }

        // --- Step 3: build address→ID lookup from postorder list ---------------
        int N = poAddresses.Count; // index of the synthetic root node
        var addressToId = new Dictionary<ulong, int>(N);
        for (int i = 0; i < N; i++)
            addressToId[poAddresses[i]] = i;

        // --- Step 4: allocate arrays (N objects + root(N) + sentinel(N+1)) ----
        int total      = N + 2;
        var addresses  = new ulong[total];
        var sizes      = new ulong[total];
        var typeNames  = new string[total];
        var preds      = new List<int>[total];

        for (int i = 0; i < total; i++)
            preds[i] = [];

        for (int i = 0; i < N; i++)
        {
            addresses[i] = poAddresses[i];
            sizes[i]     = poSizes[i];
            typeNames[i] = poTypes[i];
        }

        typeNames[N]     = "<root>";
        typeNames[N + 1] = "<unreachable>";
        // addresses[N] and addresses[N+1] stay 0 (sentinel value).

        // --- Step 5: convert raw address edges → predecessor ID lists ----------
        // rawEdges: (childAddr, parentAddr) means parentAddr → childAddr reference.
        // predecessor of childId is parentId.
        foreach (var (childAddr, parentAddr) in rawEdges)
        {
            if (!addressToId.TryGetValue(childAddr,  out int childId))
                continue;

            if (!addressToId.TryGetValue(parentAddr, out int parentId))
                continue;

            preds[childId].Add(parentId);
        }

        // --- Step 6: synthetic root is a predecessor of every root object ------
        foreach (ulong rootAddr in rootAddresses)
        {
            if (addressToId.TryGetValue(rootAddr, out int id))
                preds[id].Add(N);
        }

        outAddresses    = addresses;
        outSizes        = sizes;
        outTypeNames    = typeNames;
        outPredecessors = preds;
        outAddressToId  = addressToId;
        rootIndex       = N;
    }
}
