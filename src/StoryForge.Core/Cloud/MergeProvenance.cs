namespace StoryForge.Core.Cloud;

public sealed class ProvenanceReport
{
    // Rows in the merge result that appear in none of base / mine / theirs (written fresh during the merge).
    public List<(int Row, string[] Cells)> NewRows { get; } = new();
    // Rows that were in mine or theirs (and not in base) but are missing from the result: one side's work dropped.
    public List<(string Side, string[] Cells)> DroppedRows { get; } = new();

    public bool IsEmpty => NewRows.Count == 0 && DroppedRows.Count == 0;
}

// 繼續合併 check ④: where did each row of the merge result come from. Pure content matching (rows have no
// stable id in .md), counted as multisets so a duplicated line is handled correctly; blank rows are ignored.
public static class MergeProvenance
{
    public static ProvenanceReport Build(IReadOnlyList<string[]> baseRows, IReadOnlyList<string[]> mine,
        IReadOnlyList<string[]> theirs, IReadOnlyList<string[]> result)
    {
        var report = new ProvenanceReport();
        var known = Count(baseRows.Concat(mine).Concat(theirs));
        for (var i = 0; i < result.Count; i++)
        {
            if (CloudRows.IsBlank(result[i]))
                continue;
            var key = CloudRows.Key(result[i]);
            if (known.TryGetValue(key, out var n) && n > 0)
                known[key] = n - 1;
            else
                report.NewRows.Add((i + 1, result[i]));
        }

        var resultCount = Count(result);
        var baseCount = Count(baseRows);
        foreach (var (side, rows) in new[] { ("本機", mine), ("雲端", theirs) })
        {
            var sideCount = Count(rows);
            foreach (var (key, n) in sideCount)
            {
                // Only rows that side added (beyond base) and that did not make it into the result.
                var added = n - baseCount.GetValueOrDefault(key);
                var missing = Math.Min(added, n - resultCount.GetValueOrDefault(key));
                if (missing <= 0)
                    continue;
                var cells = rows.First(r => CloudRows.Key(r) == key);
                for (var k = 0; k < missing; k++)
                    report.DroppedRows.Add((side, cells));
            }
        }

        return report;
    }

    private static Dictionary<string, int> Count(IEnumerable<string[]> rows)
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            if (CloudRows.IsBlank(r))
                continue;
            var k = CloudRows.Key(r);
            d[k] = d.GetValueOrDefault(k) + 1;
        }
        return d;
    }
}
