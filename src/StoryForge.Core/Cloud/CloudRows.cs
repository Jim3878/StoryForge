namespace StoryForge.Core.Cloud;

// Row helpers shared by the cloud sync. A playscript table is always handled as 7-cell string rows in
// NodeScriptTableStore column order; blank rows are kept (AGENTS.md wants one at the top and bottom), except
// that a table made only of blank rows is the same as an empty table — a blank template written locally and
// an empty cloud table must never count as a difference.
public static class CloudRows
{
    public const int ColumnCount = 7;

    public static List<string[]> Normalize(IEnumerable<string[]>? rows)
    {
        var list = (rows ?? Enumerable.Empty<string[]>())
            .Select(r => Enumerable.Range(0, ColumnCount).Select(i => i < r.Length ? r[i] ?? string.Empty : string.Empty).ToArray())
            .ToList();
        return list.All(IsBlank) ? new List<string[]>() : list;
    }

    public static bool IsBlank(string[] row) => row.All(string.IsNullOrWhiteSpace);

    public static bool AreEqual(IEnumerable<string[]>? a, IEnumerable<string[]>? b)
    {
        var x = Normalize(a);
        var y = Normalize(b);
        if (x.Count != y.Count)
            return false;
        for (var i = 0; i < x.Count; i++)
            if (!x[i].SequenceEqual(y[i]))
                return false;
        return true;
    }

    public static string Key(string[] row) => string.Join("\u0001", row);
}
