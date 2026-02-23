using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DotNetDumpExtension.Analysis;
using Microsoft.Diagnostics.DebugServices;
using Microsoft.Diagnostics.Runtime;

namespace DotNetDumpExtension.Commands;

[Command(
    Name = "heapdom",
    Help = "Displays the managed heap dominator tree, showing retained sizes of objects.")]
public class HeapDomCommand : CommandBase
{
    [ServiceImport]
    public HeapDomCacheService DomCache { get; set; }

    [ServiceImport(Optional = true)]
    public ITarget Target { get; set; }

    [FilterInvoke(Message = "No CLR runtime found. Make sure a dump is loaded and the DAC is available.")]
    public static bool FilterInvoke([ServiceImport(Optional = true)] ClrRuntime runtime) => runtime != null;

    // -------------------------------------------------------------------------
    //  Options
    // -------------------------------------------------------------------------

    [Option(Name = "-minsize", Help = "Minimum retained size in bytes to include (default: 0).")]
    public ulong MinSize { get; set; }

    [Option(Name = "-type", Help = "Regex pattern on CLR type name. Recomputes retained sizes to count only matching-type objects in each node's dominated subtree.")]
    public string TypeFilter { get; set; }

    [Option(Name = "-depth", Help = "Maximum tree depth to render in -tree or -output mode (default: 128, 0 = unlimited).")]
    public int MaxDepth { get; set; } = 128;

    [Option(Name = "-rootwidth", Help = "Maximum children of the root node in -tree or -output mode (default: 0 = unlimited). Use a larger value than -width.")]
    public int MaxRootWidth { get; set; }

    [Option(Name = "-width", Help = "Maximum children per non-root node in -tree or -output mode (default: 0 = unlimited).")]
    public int MaxWidth { get; set; }

    [Option(Name = "-text", Help = "Render an indented text tree.")]
    public bool TextMode { get; set; }

    [Option(Name = "-output", Help = "Write an HTML treemap to this file path.")]
    public string OutputFile { get; set; }

    [Option(Name = "-addr", Help = "Hex address of an object to use as the display root for -tree and -output modes (default: synthetic heap root).")]
    public string RootAddress { get; set; }

    // -------------------------------------------------------------------------
    //  Entry point
    // -------------------------------------------------------------------------

    // Resolved once per Invoke() and used by all rendering methods.
    private int _displayRoot;
    private ulong _effectiveMinSize;

    public override void Invoke()
    {
        DomHeapGraph graph = DomCache.Graph;
        DomTree      tree  = DomCache.Tree;

        IDomSizes sizes;
        if (TypeFilter != null)
        {
            Regex typeRegex;
            try
            {
                typeRegex = new Regex(TypeFilter, RegexOptions.Compiled | RegexOptions.CultureInvariant);
            }
            catch (ArgumentException ex)
            {
                WriteLineError($"Invalid regex '{TypeFilter}': {ex.Message}");
                return;
            }
            sizes = new TypeFilteredDomSizes(tree, graph, DomCache.Sizes, typeRegex);
        }
        else
        {
            sizes = DomCache.Sizes;
        }

        // Resolve -addr, or fall back to the synthetic heap root.
        _displayRoot = tree.RootNodeIndex;
        if (RootAddress != null)
        {
            if (!TryParseHex(RootAddress, out ulong addr) || !graph.TryGetNodeIndex(addr, out _displayRoot))
            {
                WriteLineError($"Address '{RootAddress}' not found in the heap graph.");
                return;
            }
            WriteLine($"Display root: {graph.TypeName(_displayRoot)} @ 0x{graph.ObjectAddress(_displayRoot):x}");
        }

        _effectiveMinSize = TypeFilter != null ? Math.Max(MinSize, 1UL) : MinSize;

        WriteLine();

        if (OutputFile is not null)
        {
            WriteHtml(graph, tree, sizes);
            return;
        }

        if (TextMode)
        {
            WriteTextTree(graph, tree, sizes);
            return;
        }

        WriteLineError("Specify an output mode: -text to render an indented tree, or -output <path> to write an HTML treemap.");
    }

    // -------------------------------------------------------------------------
    //  Text tree output (-text)
    // -------------------------------------------------------------------------

    private void WriteTextTree(DomHeapGraph graph, DomTree tree, IDomSizes sizes)
    {
        WriteTreeNode(graph, tree, sizes, _displayRoot, depth: 0, indentPrefix: "");
    }

    private void WriteTreeNode(
        DomHeapGraph graph, DomTree tree, IDomSizes sizes,
        int nodeIndex, int depth, string indentPrefix)
    {
        ulong retained = sizes.RetainedSize(nodeIndex);
        ulong excl     = sizes.ExclusiveSize(nodeIndex);
        ulong addr     = graph.ObjectAddress(nodeIndex);
        string typeName = graph.TypeName(nodeIndex);

        // Apply -minsize filter.
        if (_effectiveMinSize > 0 && retained < _effectiveMinSize)
            return;

        string addrStr = addr != 0 ? $"  0x{addr:x16}" : "";
        string exclStr = excl > 0 ? $"  {excl:N0} excl" : "";
        WriteLine($"{indentPrefix}{typeName}{addrStr}  {retained:N0} retained{exclStr}");

        // Depth limit.
        if (MaxDepth > 0 && depth + 1 >= MaxDepth)
            return;

        List<int> children = tree.GetChildren(nodeIndex);
        if (children == null) return;

        // Sort children by retained size descending.
        var sorted = children.OrderByDescending(c => sizes.RetainedSize(c)).ToList();

        // Use the larger root-level width limit for direct children of the display root.
        int effectiveWidth = nodeIndex == _displayRoot ? MaxRootWidth : MaxWidth;

        string childPrefix = indentPrefix + "  ";
        int rendered = 0;
        ulong elidedRetained = 0;
        int elidedCount = 0;

        for (int i = 0; i < sorted.Count; i++)
        {
            if (effectiveWidth > 0 && rendered >= effectiveWidth)
            {
                elidedRetained += sizes.RetainedSize(sorted[i]);
                elidedCount++;
                continue;
            }

            WriteTreeNode(graph, tree, sizes, sorted[i], depth + 1, childPrefix);
            rendered++;
        }

        if (elidedCount > 0)
            WriteLine($"{childPrefix}elided+{elidedCount}  {elidedRetained:N0}");

        // Intrinsic: size not accounted for by rendered children.
        ulong renderedChildrenRetained = (ulong)sorted
            .Take(rendered)
            .Sum(c => (long)sizes.RetainedSize(c));
        ulong intrinsic = retained > renderedChildrenRetained + excl
            ? retained - renderedChildrenRetained - excl
            : 0;
        if (intrinsic > 0)
            WriteLine($"{childPrefix}intrinsic  {intrinsic:N0}");
    }

    // -------------------------------------------------------------------------
    //  HTML treemap output (-output)
    // -------------------------------------------------------------------------

    private void WriteHtml(DomHeapGraph graph, DomTree tree, IDomSizes sizes)
    {
        try
        {
            // Build the JSON data blob.
            var dataSb = new StringBuilder();
            AppendJsonNode(dataSb, graph, tree, sizes, _displayRoot, currentDepth: 0, isRoot: true);

            // Splice the data inline where the external data.js script tag would be,
            // exactly as MemorySnapshotAnalyzer does at runtime.
            string html = TreemapHtml.Replace(
                "<script src=\"data.js\"></script>",
                $"<script>data={dataSb}</script>");

            File.WriteAllText(OutputFile!, html, Encoding.UTF8);
            WriteLine($"HTML treemap written to: {OutputFile}");
            WriteLine($"  Total nodes in tree: {graph.RootNodeIndex:N0}");
            WriteLine($"  Total retained size: {sizes.RetainedSize(tree.RootNodeIndex):N0} bytes");
        }
        catch (IOException ex)
        {
            WriteLineError($"Failed to write output file: {ex.Message}");
        }
    }

    /// <summary>Reconstructs the command line from the options that were set.</summary>
    private string BuildCommandLine()
    {
        var parts = new List<string> { "heapdom" };

        if (MinSize > 0)
            parts.Add($"-minsize {MinSize}");

        if (TypeFilter is not null)
            parts.Add($"-type {TypeFilter}");

        if (MaxDepth != 128)
            parts.Add($"-depth {MaxDepth}");

        if (MaxRootWidth > 0) 
            parts.Add($"-rootwidth {MaxRootWidth}");

        if (MaxWidth > 0) 
            parts.Add($"-width {MaxWidth}");

        if (TextMode) 
            parts.Add("-text");

        if (OutputFile is not null)
            parts.Add($"-output {OutputFile}");

        if (RootAddress is not null)
            parts.Add($"-addr {RootAddress}");

        return string.Join(" ", parts);
    }

    private void AppendJsonNode(
        StringBuilder sb,
        DomHeapGraph graph, DomTree tree, IDomSizes sizes,
        int nodeIndex, int currentDepth, bool isRoot = false)
    {
        ulong retained  = sizes.RetainedSize(nodeIndex);
        ulong addr      = graph.ObjectAddress(nodeIndex);
        string typeName = graph.TypeName(nodeIndex);
        string name     = addr != 0 ? $"{typeName}@0x{addr:x}" : typeName;

        sb.Append("{\"name\":");
        sb.Append(JsonEncodeString(name));

        // Root-level metadata fields consumed by the treemap HTML footer.
        if (isRoot)
        {
            string filename = Target is not null
                ? (Target.GetType().GetProperty("DumpPath")?.GetValue(Target) as string
                   ?? Target.ToString() ?? "unknown")
                : "unknown";

            sb.Append(",\"filename\":");
            sb.Append(JsonEncodeString(filename));
            sb.Append(",\"heapDomCommandLine\":");
            sb.Append(JsonEncodeString(BuildCommandLine()));
            sb.Append(",\"context\":");
            sb.Append(JsonEncodeString(
                $"Live objects: {graph.RootNodeIndex:N0}\n" +
                $"Total retained: {retained:N0} bytes\n" +
                $"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}"));
        }

        List<int> children = tree.GetChildren(nodeIndex);
        bool depthExceeded  = MaxDepth > 0 && currentDepth >= MaxDepth;

        if (children == null || depthExceeded)
        {
            sb.Append(",\"value\":");
            sb.Append(retained);
            sb.Append('}');
            return;
        }

        // Filter and sort children.
        var sorted = children
            .Where(c => _effectiveMinSize == 0 || sizes.RetainedSize(c) >= _effectiveMinSize)
            .OrderByDescending(c => sizes.RetainedSize(c))
            .ToList();

        // Use the larger root-level width limit for direct children of the display root.
        int effectiveWidth = nodeIndex == _displayRoot ? MaxRootWidth : MaxWidth;
        int renderCount = effectiveWidth > 0 ? Math.Min(effectiveWidth, sorted.Count) : sorted.Count;
        var toRender    = sorted.Take(renderCount).ToList();
        var toElide     = sorted.Skip(renderCount).ToList();

        sb.Append(",\"children\":[");
        ulong renderedRetained = 0;
        bool needComma = false;

        foreach (int child in toRender)
        {
            if (needComma) sb.Append(',');
            AppendJsonNode(sb, graph, tree, sizes, child, currentDepth + 1);
            renderedRetained += sizes.RetainedSize(child);
            needComma = true;
        }

        // Elided bucket: children cut by -width.
        if (toElide.Count > 0)
        {
            ulong elidedSize = toElide.Aggregate(0UL, (acc, c) => acc + sizes.RetainedSize(c));
            if (needComma) sb.Append(',');
            sb.Append($"{{\"name\":\"elided+{toElide.Count}\",\"value\":{elidedSize}}}");
            renderedRetained += elidedSize;
            needComma = true;
        }

        // Intrinsic bucket: this node's own exclusive size not accounted for by any child.
        ulong intrinsic = retained > renderedRetained ? retained - renderedRetained : 0;
        if (intrinsic > 0)
        {
            if (needComma) sb.Append(',');
            sb.Append($"{{\"name\":\"intrinsic\",\"value\":{intrinsic}}}");
        }

        sb.Append("]}");
    }

    // -------------------------------------------------------------------------
    //  Embedded treemap HTML (MemorySnapshotAnalyzer style)
    //  Loaded once from the manifest resource Resources/treemap.html.
    //  The placeholder "<script src="data.js"></script>" is replaced at runtime
    //  with an inline <script>data={...}</script> block.
    // -------------------------------------------------------------------------

    // -------------------------------------------------------------------------
    //  Helpers
    // -------------------------------------------------------------------------

    private static bool TryParseHex(string s, out ulong value)
    {
        string clean = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s.Substring(2) : s;
        return ulong.TryParse(clean, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    private static string JsonEncodeString(string value)
    {
        if (value is null)
            return "null";

        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (c < 0x20)
                        sb.Append($"\\u{(int)c:x4}");
                    else
                        sb.Append(c);

                    break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }

    private static string _treemapHtml;

    private static string TreemapHtml
    {
        get
        {
            if (_treemapHtml is not null)
                return _treemapHtml;

            var assembly = typeof(HeapDomCommand).Assembly;
            // MSBuild embeds the resource as "<default namespace>.<relative path with dots>".
            const string resourceName = "DotNetDumpExtension.Resources.treemap.html";
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException(
                    $"Embedded resource '{resourceName}' not found. " +
                    $"Available: {string.Join(", ", assembly.GetManifestResourceNames())}");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return _treemapHtml = reader.ReadToEnd();
        }
    }
}
