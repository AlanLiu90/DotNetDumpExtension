using Microsoft.Diagnostics.DebugServices;
using Microsoft.Diagnostics.Runtime;

namespace DotNetDumpExtension.Analysis;

/// <summary>
/// Caches the dominator-tree computation (graph, tree, retained sizes) for the lifetime
/// of a single CLR runtime session.
///
/// Decorated with <see cref="ServiceExportAttribute"/> at <see cref="ServiceScope.Runtime"/>
/// so the dotnet-dump service framework creates exactly one instance per loaded runtime and
/// reuses it across every heapdom invocation — no manual cache invalidation needed.
/// </summary>
[ServiceExport(Scope = ServiceScope.Runtime)]
public class HeapDomCacheService
{
    private DomHeapGraph _graph;
    private DomTree      _tree;
    private DomNodeSizes _sizes;

    [ServiceImport]
    public ClrRuntime Runtime { get; set; }

    [ServiceImport]
    public IConsoleService Console { get; set; }

    // -------------------------------------------------------------------------
    //  Public API
    // -------------------------------------------------------------------------

    /// <summary>The full heap reference graph with predecessor lists.</summary>
    public DomHeapGraph Graph
    {
        get { EnsureBuilt(); return _graph; }
    }

    /// <summary>The dominator tree computed from <see cref="Graph"/>.</summary>
    public DomTree Tree
    {
        get { EnsureBuilt(); return _tree; }
    }

    /// <summary>Retained (inclusive) sizes for every node in <see cref="Tree"/>.</summary>
    public DomNodeSizes Sizes
    {
        get { EnsureBuilt(); return _sizes; }
    }

    // -------------------------------------------------------------------------
    //  Build
    // -------------------------------------------------------------------------

    private void EnsureBuilt()
    {
        if (_graph is not null)
            return;

        Build();
    }

    private void Build()
    {
        Console.WriteLine("Building heap reference graph...");
        var graph = new DomHeapGraph(Runtime);
        Console.WriteLine($"  {graph.RootNodeIndex:N0} live objects indexed.");

        Console.WriteLine("Computing dominator tree...");
        var tree = new DomTree(graph);

        Console.WriteLine("Computing retained sizes...");
        var sizes = new DomNodeSizes(tree, graph);

        _graph = graph;
        _tree  = tree;
        _sizes = sizes;
    }
}
