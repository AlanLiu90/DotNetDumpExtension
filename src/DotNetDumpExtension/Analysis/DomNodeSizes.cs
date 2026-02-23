using System.Collections.Generic;

namespace DotNetDumpExtension.Analysis;

/// <summary>
/// Computes the retained (inclusive) size of every node in the dominator tree.
///
/// Retained size of a node = its own exclusive size (ClrObject.Size)
///                          + sum of retained sizes of all its dominator-tree children.
///
/// The bottom-up pass uses an explicit stack to avoid StackOverflowException
/// on arbitrarily deep dominator trees.
/// </summary>
public sealed class DomNodeSizes : IDomSizes
{
    private readonly ulong[] _retained;
    private readonly DomHeapGraph _graph;

    public DomNodeSizes(DomTree tree, DomHeapGraph graph)
    {
        _graph    = graph;
        _retained = new ulong[graph.NumberOfNodes];
        Compute(tree, graph);
    }

    /// <summary>
    /// Retained (inclusive) size: the exclusive size of this node plus the
    /// retained sizes of all its dominator-tree descendants.
    /// Equivalent to "TreeSize" in MemorySnapshotAnalyzer.
    /// </summary>
    public ulong RetainedSize(int nodeIndex) => _retained[nodeIndex];

    /// <summary>This node's own (exclusive) size; delegates to the graph.</summary>
    public ulong ExclusiveSize(int nodeIndex) => _graph.ExclusiveSize(nodeIndex);

    // -------------------------------------------------------------------------
    //  Bottom-up computation using an explicit post-order traversal
    // -------------------------------------------------------------------------

    private void Compute(DomTree tree, DomHeapGraph graph)
    {
        // Iterative post-order DFS over the dominator tree.
        // Stack entry: (nodeIndex, isFinish)
        // On first visit (isFinish=false): push finish marker, then push children.
        // On finish (isFinish=true): accumulate sizes bottom-up.
        var stack = new Stack<(int nodeIndex, bool finish)>();
        stack.Push((tree.RootNodeIndex, false));

        while (stack.Count > 0)
        {
            var (nodeIndex, finish) = stack.Pop();
            List<int> children;

            if (finish)
            {
                ulong retained = graph.ExclusiveSize(nodeIndex);

                children = tree.GetChildren(nodeIndex);
                if (children != null)
                {
                    foreach (int child in children)
                        retained += _retained[child];
                }

                _retained[nodeIndex] = retained;
                continue;
            }

            // Push finish marker before children so we process children first.
            stack.Push((nodeIndex, true));

            children = tree.GetChildren(nodeIndex);
            if (children is null)
                continue;

            foreach (int child in children)
                stack.Push((child, false));
        }
    }
}
