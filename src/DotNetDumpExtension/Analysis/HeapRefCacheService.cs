using System.Collections.Generic;
using Microsoft.Diagnostics.DebugServices;
using Microsoft.Diagnostics.Runtime;

namespace DotNetDumpExtension.Analysis;

/// <summary>
/// Caches a full heap traversal (live object type names and reverse reference map)
/// for the lifetime of a single CLR runtime session.
///
/// Decorated with <see cref="ServiceExportAttribute"/> at <see cref="ServiceScope.Runtime"/>
/// so the dotnet-dump service framework creates exactly one instance per loaded runtime and
/// reuses it across every command invocation — no manual cache invalidation needed.
/// </summary>
[ServiceExport(Scope = ServiceScope.Runtime)]
public class HeapRefCacheService
{
    private Dictionary<ulong, string>       _typeNames;
    private Dictionary<ulong, List<ulong>>  _reverseRefs;

    [ServiceImport]
    public ClrRuntime Runtime { get; set; }

    [ServiceImport]
    public IConsoleService Console { get; set; }

    // -------------------------------------------------------------------------
    //  Public API
    // -------------------------------------------------------------------------

    /// <summary>Address → CLR type name for every live object.</summary>
    public Dictionary<ulong, string> TypeNames
    {
        get { EnsureBuilt(); return _typeNames; }
    }

    /// <summary>
    /// Child address → sorted, deduplicated list of parent addresses that
    /// directly reference it.
    /// </summary>
    public Dictionary<ulong, List<ulong>> ReverseRefs
    {
        get { EnsureBuilt(); return _reverseRefs; }
    }

    // -------------------------------------------------------------------------
    //  Build
    // -------------------------------------------------------------------------

    private void EnsureBuilt()
    {
        if (_typeNames is not null)
            return;

        Build();
    }

    private void Build()
    {
        ClrHeap heap = Runtime.Heap;

        Console.WriteLine("Traversing live objects...");

        var typeNames   = new Dictionary<ulong, string>();
        var reverseRefs = new Dictionary<ulong, List<ulong>>();
        var visited     = new HashSet<ulong>();
        var stack       = new Stack<ulong>();

        // Seed the DFS from every GC root.
        foreach (ClrRoot root in heap.EnumerateRoots())
        {
            Console.CancellationToken.ThrowIfCancellationRequested();
            ClrObject obj = root.Object;
            if (obj.IsValid && obj.Address != 0 && visited.Add(obj.Address))
                stack.Push(obj.Address);
        }

        while (stack.Count > 0)
        {
            Console.CancellationToken.ThrowIfCancellationRequested();

            ulong addr = stack.Pop();
            ClrObject obj = heap.GetObject(addr);
            typeNames[addr] = obj.Type?.Name ?? "";

            foreach (ClrObject child in obj.EnumerateReferences(carefully: true))
            {
                if (!child.IsValid || child.Address == 0)
                    continue;

                // Record addr as a direct parent of child.
                // Cross-edges (child already visited) are included so that every
                // parent-child relationship in the heap is captured.
                if (!reverseRefs.TryGetValue(child.Address, out List<ulong> parents))
                    reverseRefs[child.Address] = parents = new List<ulong>();

                parents.Add(addr);

                if (visited.Add(child.Address))
                    stack.Push(child.Address);
            }
        }

        foreach (List<ulong> parents in reverseRefs.Values)
        {
            if (parents.Count > 1)
            {
                parents.Sort();

                int write = 1;
                for (int read = 1; read < parents.Count; read++)
                {
                    if (parents[read] != parents[write - 1])
                        parents[write++] = parents[read];
                }

                parents.RemoveRange(write, parents.Count - write);
            }
        }

        Console.WriteLine($"  {typeNames.Count:N0} live objects found.");

        _typeNames   = typeNames;
        _reverseRefs = reverseRefs;
    }
}
