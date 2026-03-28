using System;
using System.Collections.Generic;
using System.Globalization;
using DotNetDumpExtension.Analysis;
using Microsoft.Diagnostics.DebugServices;
using Microsoft.Diagnostics.Runtime;

namespace DotNetDumpExtension.Commands;

[Command(
    Name = "heaprefs",
    Help = "Shows which types hold direct references to live objects of a given type, weighted by reference share.")]
public class HeapRefsCommand : CommandBase
{
    [ServiceImport(Optional = true)]
    public ClrRuntime Runtime { get; set; }

    [ServiceImport]
    public HeapRefCacheService HeapRefCache { get; set; }

    [FilterInvoke(Message = "No CLR runtime found. Make sure a dump is loaded and the DAC is available.")]
    public static bool FilterInvoke([ServiceImport(Optional = true)] ClrRuntime runtime) => runtime != null;

    // -------------------------------------------------------------------------
    //  Options
    // -------------------------------------------------------------------------

    [Option(Name = "-mt", Help = "Method table address (hex) of the target type (preferred over -type).")]
    public string MethodTable { get; set; }

    [Option(Name = "-type", Help = "Substring filter on CLR type name.")]
    public string TypeFilter { get; set; }

    [Option(Name = "-top", Help = "Number of top parent types to display (default: 20, 0 = all).")]
    public int Top { get; set; } = 20;

    // -------------------------------------------------------------------------
    //  Entry point
    // -------------------------------------------------------------------------

    public override void Invoke()
    {
        if (MethodTable is null && TypeFilter is null)
        {
            WriteLineError("Specify either -mt <method-table-address> or -type <type-name-substring>.");
            return;
        }

        ClrHeap heap = Runtime!.Heap;

        // Resolve -mt to an exact type name. -mt takes precedence over -type.
        string resolvedTypeName = TypeFilter;
        bool exactMatch = false;

        if (MethodTable is not null)
        {
            if (!TryParseHex(MethodTable, out ulong mt))
            {
                WriteLineError($"Invalid method table address: '{MethodTable}'. Expected a hex value, e.g. 00007f1234abcd.");
                return;
            }

            ClrType clrType = heap.GetTypeByMethodTable(mt);
            if (clrType is null)
            {
                WriteLineError($"No type found for method table 0x{mt:x}.");
                return;
            }

            if (TypeFilter is not null)
                WriteLine("Note: -type ignored because -mt was specified.");

            resolvedTypeName = clrType.Name ?? "";
            exactMatch = true;
            WriteLine($"Resolved method table 0x{mt:x} \u2192 {resolvedTypeName}");
        }

        Dictionary<ulong, string>       typeNames   = HeapRefCache.TypeNames;
        Dictionary<ulong, List<ulong>>  reverseRefs = HeapRefCache.ReverseRefs;

        // -------------------------------------------------------------------------
        //  Phase 2: filter target objects, calculate weighted tallies per parent type.
        //
        //  For each target object with N distinct parent objects, each parent type
        //  receives a weight of 1/N. Weights are summed across all target objects.
        //  Percentages are computed against the total weight sum so they sum to 100%.
        // -------------------------------------------------------------------------

        var tallies        = new Dictionary<string, double>();
        int totalMatching  = 0;
        int withParents    = 0;
        int withoutParents = 0;

        foreach (KeyValuePair<ulong, string> kvp in typeNames)
        {
            ulong  addr     = kvp.Key;
            string typeName = kvp.Value;

            bool isMatch = exactMatch
                ? string.Equals(typeName, resolvedTypeName, StringComparison.Ordinal)
                : typeName.Contains(resolvedTypeName);

            if (!isMatch)
                continue;

            totalMatching++;

            if (!reverseRefs.TryGetValue(addr, out List<ulong> parentList) || parentList.Count == 0)
            {
                withoutParents++;
                continue;
            }

            double weight = 1.0 / parentList.Count;
            withParents++;

            foreach (ulong parentAddr in parentList)
            {
                string parentType = typeNames.TryGetValue(parentAddr, out string pt) ? pt : "";
                if (!tallies.TryGetValue(parentType, out double current))
                    current = 0.0;

                tallies[parentType] = current + weight;
            }
        }

        if (totalMatching == 0)
        {
            WriteLine($"No live objects found matching '{resolvedTypeName}'.");
            return;
        }

        // -------------------------------------------------------------------------
        //  Output
        // -------------------------------------------------------------------------

        var sorted = new List<KeyValuePair<string, double>>(tallies);
        sorted.Sort((a, b) => b.Value.CompareTo(a.Value));

        int  totalTypes = sorted.Count;
        bool truncated  = Top > 0 && sorted.Count > Top;

        double totalTally = 0.0;
        foreach (KeyValuePair<string, double> kv in tallies)
            totalTally += kv.Value;

        // Compute the combined weight of the omitted rows before truncating.
        double omittedWeight = 0.0;
        int    omittedCount  = 0;
        if (truncated)
        {
            for (int i = Top; i < sorted.Count; i++)
                omittedWeight += sorted[i].Value;
            omittedCount = sorted.Count - Top;
            sorted = sorted.GetRange(0, Top);
        }

        string filterLabel = exactMatch ? resolvedTypeName : $"*{resolvedTypeName}*";
        WriteLine();
        WriteLine($"Direct parent types of '{filterLabel}':");
        WriteLine();
        WriteLine($"{"Parent Type",-80}  {"Weight",10}  {"Percent",8}");
        WriteLine($"{new string('-', 80),-80}  {new string('-', 10),10}  {new string('-', 8),8}");

        foreach (KeyValuePair<string, double> kv in sorted)
        {
            double percent  = totalTally > 0 ? kv.Value / totalTally * 100.0 : 0.0;
            string typeName = kv.Key;
            string typeStr  = typeName.Length > 80
                ? typeName.Substring(typeName.Length - 80)
                : typeName;
            WriteLine($"{typeStr,-80}  {kv.Value,10:F2}  {percent,7:F1}%");
        }

        if (truncated)
        {
            double omittedPercent = totalTally > 0 ? omittedWeight / totalTally * 100.0 : 0.0;
            string omittedLabel   = $"({omittedCount} more types...)";
            WriteLine($"{omittedLabel,-80}  {omittedWeight,10:F2}  {omittedPercent,7:F1}%");
        }

        WriteLine();
        WriteLine($"Total matching objects:       {totalMatching:N0}");
        WriteLine($"  With direct parents:        {withParents:N0}");
        WriteLine($"  Without parents (GC roots): {withoutParents:N0}");
    }

    // -------------------------------------------------------------------------
    //  Helpers
    // -------------------------------------------------------------------------

    private static bool TryParseHex(string s, out ulong value)
    {
        string clean = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? s.Substring(2)
            : s;
        return ulong.TryParse(
            clean,
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out value);
    }
}
