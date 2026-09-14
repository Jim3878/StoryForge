namespace StoryForge.Core;

// Orders embedded digit runs by numeric value instead of lexically, so chapter keys like "A1".."A10" sort
// as A1, A2, ..., A9, A10 instead of the ordinal A1, A10, A2, ..., A9. Used anywhere chapter names (or
// other user-facing "prefix + number" identifiers) are sorted for display — plain StringComparer.Ordinal
// is still correct and intentional for dictionary keys/equality checks, which don't care about display order.
public sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        x ??= string.Empty;
        y ??= string.Empty;

        int ix = 0, iy = 0;
        while (ix < x.Length && iy < y.Length)
        {
            if (char.IsDigit(x[ix]) && char.IsDigit(y[iy]))
            {
                var startX = ix;
                var startY = iy;
                while (ix < x.Length && char.IsDigit(x[ix])) ix++;
                while (iy < y.Length && char.IsDigit(y[iy])) iy++;

                var digitsX = x.AsSpan(startX, ix - startX).TrimStart('0');
                var digitsY = y.AsSpan(startY, iy - startY).TrimStart('0');

                // Same digit-length numbers compare the same lexically as numerically; a shorter run of
                // (leading-zero-trimmed) digits is always the smaller number regardless of its digits.
                if (digitsX.Length != digitsY.Length)
                    return digitsX.Length - digitsY.Length;

                var numericCompare = digitsX.CompareTo(digitsY, StringComparison.Ordinal);
                if (numericCompare != 0)
                    return numericCompare;
            }
            else if (x[ix] != y[iy])
            {
                return x[ix] < y[iy] ? -1 : 1;
            }
            else
            {
                ix++;
                iy++;
            }
        }

        return (x.Length - ix) - (y.Length - iy);
    }
}
