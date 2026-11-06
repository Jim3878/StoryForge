using System.Text.RegularExpressions;

namespace StoryForge.Core.Graph;

// 建立節點副本時的命名：`章節/場景_字母數字_描述`（例如 A10/ReN100_s10_慎次閒話）這種名稱，副本取同一個
// 「章節/場景_ + 字母」系列目前的最大號，往上取到下一個整十（s30 → s40、s16 → s20），描述照抄。
// s 和 t 各自分開編號。不符合這個格式的名稱由呼叫端退回原本的 "_1" 規則。
public static class PlayscriptIdNumbering
{
    // scene 用 lazy 比對，取第一個「_字母數字」後面緊接著 "_" 或字串結尾的段落當編號段。
    private static readonly Regex Pattern = new(@"^(?<scene>.+?_)(?<letter>[A-Za-z])(?<num>\d+)(?<desc>_.*)?$",
        RegexOptions.Compiled);

    public static bool TryNextInSeries(string sourceId, IEnumerable<string> existingIds, out string next)
    {
        next = string.Empty;
        var source = Pattern.Match(sourceId);
        if (!source.Success)
            return false;

        var scene = source.Groups["scene"].Value;
        var letter = source.Groups["letter"].Value;
        var existing = existingIds.ToHashSet(StringComparer.Ordinal);

        var max = 0;
        foreach (var id in existing)
        {
            var m = Pattern.Match(id);
            if (m.Success && m.Groups["scene"].Value == scene && m.Groups["letter"].Value == letter &&
                int.TryParse(m.Groups["num"].Value, out var n) && n > max)
                max = n;
        }

        var width = source.Groups["num"].Value.Length;
        var number = (max / 10 + 1) * 10;
        do
        {
            next = $"{scene}{letter}{number.ToString().PadLeft(width, '0')}{source.Groups["desc"].Value}";
            number += 10;
        } while (existing.Contains(next));

        return true;
    }
}
