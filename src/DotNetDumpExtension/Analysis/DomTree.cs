using System.Collections.Generic;

namespace DotNetDumpExtension.Analysis;

/// <summary>
/// Computes the dominator tree of the managed heap reference graph.
///
/// Algorithm: iterative fixed-point from
/// "A Simple, Fast Dominance Algorithm" (Cooper, Harvey, Kennedy)
/// https://www.cs.tufts.edu/~nr/cs257/archive/keith-cooper/dom14.pdf
///
/// Nodes are expected to be indexed in DFS postorder (as produced by DomHeapGraph),
/// so the root has the highest index and lower indices are further from the root.
/// </summary>
public sealed class DomTree
{
    private readonly int[]                      _doms;
    private readonly Dictionary<int, List<int>> _children;

    public DomTree(DomHeapGraph graph)
    {
        RootNodeIndex = graph.RootNodeIndex;
        _doms         = ComputeDominators(graph);
        _children     = BuildChildren(graph);
    }

    public int RootNodeIndex { get; }

    /// <summary>
    /// Returns the immediate dominator of nodeIndex.
    /// Useful for debugging; not required by the rendering pipeline.
    /// </summary>
    public int GetDominator(int nodeIndex) => _doms[nodeIndex];

    /// <summary>
    /// Returns the direct children of nodeIndex in the dominator tree,
    /// or null if nodeIndex is a leaf.
    /// </summary>
    public List<int> GetChildren(int nodeIndex)
    {
        _children.TryGetValue(nodeIndex, out List<int> children);
        return children;
    }

    // -------------------------------------------------------------------------
    //  Algorithm
    // -------------------------------------------------------------------------

    private static int[] ComputeDominators(DomHeapGraph graph)
    {
        int root        = graph.RootNodeIndex;
        int unreachable = graph.UnreachableNodeIndex;
        int count       = graph.NumberOfNodes;

        var doms = new int[count];

        // Initialise: root dominates itself; all others are "unreachable".
        for (int i = 0; i < root; i++)
            doms[i] = unreachable;

        doms[root]        = root;
        doms[unreachable] = unreachable; // sentinel dominates itself

        bool changed = true;
        while (changed)
        {
            changed = false;

            // Process in reverse postorder: root-1 down to 0.
            for (int nodeIndex = root - 1; nodeIndex >= 0; nodeIndex--)
            {
                int newIdom = unreachable;

                foreach (int pred in graph.Predecessors(nodeIndex))
                {
                    if (doms[pred] == unreachable)
                        continue;

                    if (newIdom == unreachable)
                    {
                        newIdom = pred;
                    }
                    else
                    {
                        newIdom = Intersect(pred, newIdom, doms);
                    }
                }

                if (doms[nodeIndex] != newIdom)
                {
                    doms[nodeIndex] = newIdom;
                    changed = true;
                }
            }
        }

        return doms;
    }

    /// <summary>
    /// Walk both "fingers" up the partially-built dominator tree until they meet.
    /// Exploits the postorder property: a lower index is further from the root,
    /// so advancing the lower finger moves it towards the root.
    /// </summary>
    private static int Intersect(int finger1, int finger2, int[] doms)
    {
        while (finger1 != finger2)
        {
            while (finger1 < finger2)
                finger1 = doms[finger1];

            while (finger2 < finger1)
                finger2 = doms[finger2];
        }

        return finger1;
    }

    private Dictionary<int, List<int>> BuildChildren(DomHeapGraph graph)
    {
        int root        = graph.RootNodeIndex;
        int unreachable = graph.UnreachableNodeIndex;

        // Route unreachable-sentinel children to root so they don't dangle.
        _doms[unreachable] = root;

        var children = new Dictionary<int, List<int>>();
        for (int nodeIndex = 0; nodeIndex < root; nodeIndex++)
        {
            int parent = _doms[nodeIndex];
            if (!children.TryGetValue(parent, out List<int> list))
            {
                list = [];
                children[parent] = list;
            }

            list.Add(nodeIndex);
        }

        return children;
    }
}
