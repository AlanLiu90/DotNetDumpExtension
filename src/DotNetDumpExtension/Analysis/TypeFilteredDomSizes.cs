using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DotNetDumpExtension.Analysis;

/// <summary>
/// Computes per-node retained sizes restricted to a CLR type-name regex pattern,
/// mirroring the <c>HeapDomSizes(heapDom, typeSet)</c> overload in MemorySnapshotAnalyzer.
///
/// Semantics:
///   - A node whose type name matches the pattern is "selected".
///     Its retained size equals the full (unfiltered) retained size of that node,
///     because once a matching type is found the filter stops propagating downward —
///     all dominated memory is attributed to it.
///   - A node that does not match contributes 0 exclusive size; its filtered retained
///     size is the sum of its children's filtered retained sizes.
///
/// This answers questions such as "how much Dictionary&lt;&gt; memory does each root own?"
/// </summary>
public sealed class TypeFilteredDomSizes : IDomSizes
{
    private readonly ulong[] _retained;
    private readonly ulong[] _exclusive;

    public TypeFilteredDomSizes(DomTree tree, DomHeapGraph graph, DomNodeSizes fullSizes, Regex typePattern)
    {
        _retained  = new ulong[graph.NumberOfNodes];
        _exclusive = new ulong[graph.NumberOfNodes];
        Compute(tree, graph, fullSizes, typePattern);
    }

    public ulong RetainedSize(int nodeIndex)  => _retained[nodeIndex];
    public ulong ExclusiveSize(int nodeIndex) => _exclusive[nodeIndex];

    // -------------------------------------------------------------------------
    //  Bottom-up computation (iterative post-order DFS over the dominator tree)
    // -------------------------------------------------------------------------

    private void Compute(DomTree tree, DomHeapGraph graph, DomNodeSizes fullSizes, Regex typePattern)
    {
        var stack = new Stack<(int nodeIndex, bool finish)>();
        stack.Push((tree.RootNodeIndex, false));

        while (stack.Count > 0)
        {
            var (nodeIndex, finish) = stack.Pop();
            List<int> children;

            if (finish)
            {
                // Check whether this node's type matches the filter.
                string typeName = graph.TypeName(nodeIndex);
                if (typePattern.IsMatch(typeName))
                {
                    // Matched: count the full dominated subtree.  The filter does
                    // not propagate further — all descendants are included via fullSizes.
                    _retained[nodeIndex]  = fullSizes.RetainedSize(nodeIndex);
                    _exclusive[nodeIndex] = graph.ExclusiveSize(nodeIndex);
                }
                else
                {
                    // Not matched: own contribution is 0; accumulate children's filtered sizes.
                    ulong sum = 0;
                    children = tree.GetChildren(nodeIndex);
                    if (children != null)
                    {
                        foreach (int child in children)
                            sum += _retained[child];
                    }

                    _retained[nodeIndex]  = sum;
                    _exclusive[nodeIndex] = 0;
                }

                continue;
            }

            stack.Push((nodeIndex, true));

            children = tree.GetChildren(nodeIndex);
            if (children == null)
                continue;

            foreach (int child in children)
                stack.Push((child, false));
        }
    }
}
