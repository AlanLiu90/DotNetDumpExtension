namespace DotNetDumpExtension.Analysis;

/// <summary>
/// Provides per-node size metrics over a dominator tree.
/// Implemented by <see cref="DomNodeSizes"/> (full sizes) and
/// <see cref="TypeFilteredDomSizes"/> (sizes restricted to a type-name pattern).
/// </summary>
public interface IDomSizes
{
    /// <summary>
    /// Retained (inclusive) size: the total memory dominated by this node
    /// that counts under the current filter (or all memory when unfiltered).
    /// </summary>
    ulong RetainedSize(int nodeIndex);

    /// <summary>
    /// This node's own contribution to retained size.
    /// Returns <see cref="DomHeapGraph.ExclusiveSize"/> when unfiltered,
    /// or 0 when the node's type does not match the active type filter.
    /// </summary>
    ulong ExclusiveSize(int nodeIndex);
}
