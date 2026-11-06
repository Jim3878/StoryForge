using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StoryForge.Core.CharacterCard;
using StoryForge.Core.Codegen;

namespace StoryForge.Core.Cloud;

// The 功能 values PlayscriptFactoryCodeGenerator accepts, with the 參數 hint a writer sees. This is the third
// place the function list lives (with the generator itself and AGENTS.md — see CLAUDE.md's "參數" section):
// adding/removing/changing a 功能 means updating this list in the same change.
public static class PlayscriptFunctionCatalog
{
    public sealed record Function(string Name, string? Param = null, bool WithText = false, bool Legacy = false);

    public static readonly IReadOnlyList<Function> All = new Function[]
    {
        new("OpenCg", "CG 名稱;備註（備註可省略）"),
        new("Diff", "差分名稱;備註（備註可省略）"),
        new("CloseCg"),
        new("CloseDialogue"),
        new("WaitSeconds", "秒數"),
        new("FadeOnDark"),
        new("FadeOff"),
        new("FadeOnWhite"),
        new("Label", "標籤名稱"),
        new("Button", "跳轉目標 Label", WithText: true),
        new("TryAddLobby", "大廳名稱;目標劇本名稱"),
        new("SetMessageLobby"),
        new("PlayPlayscript", "目標劇本名稱"),
        new("Chat", WithText: true),
        new("UpBlock", WithText: true),
        new("DownBlock", WithText: true),
        new("LeftBlock", WithText: true),
        new("RightBlock", WithText: true),
        new("Small", WithText: true),
        new("Large", WithText: true),
        new("Break"),
        new("PlayBgm", "檔名;音量（音量可省略，0～1）"),
        new("PushBgm", "檔名;音量（音量可省略，0～1）"),
        new("PopBgm"),
        new("ReturnLevelBgm"),
        new("MuteBgm"),
        new("PlaySfx", "檔名;音量（音量可省略，0～1）"),
        new("SetBoolFlag", "旗標;true或false"),
        new("BoolFlagJump", "旗標;true或false;Label"),
        new("SetIntFlag", "旗標;整數"),
        new("AddIntFlag", "旗標;整數"),
        new("IntFlagJump", "旗標;比較符號;整數;Label"),
        new("LevelTransport", "TransportTarget iid（可加 ;偏移量）"),
        new("DoorLevelTransport", "TransportTarget iid"),
        new("DoorLevelTransportWithoutFadeOff", "TransportTarget iid"),
        new("NightTime"),
        new("DayTime"),
        new("SleepFade"),
        new("SetGuidePath", "起點;終點"),
        new("ClearGuidePath"),
        new("NoRole", WithText: true, Legacy: true),
    };

    // Old spellings the generator still accepts (see IsChoiceFunction / IsLargeFunction).
    public static readonly IReadOnlyDictionary<string, string> Aliases = new Dictionary<string, string>
    {
        ["選項"] = "Button",
        ["Choice"] = "Button",
        ["大字"] = "Large",
        ["LargeText"] = "Large",
    };
}

// Builds the rules JSON uploaded to db$<ws>$sf.rules: function list, 名字 lists (the same enum labels the
// codegen checks) and each character's own 表情 (character cards' ChineseLabel).
public static class CloudRulesExport
{
    public static (string Json, string Hash) Build(EnumLabelMaps maps, CharacterCardSettings cards)
    {
        var facesByName = new JObject();
        foreach (var card in cards.Cards.OrderBy(c => c.ChineseName, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(card.ChineseName))
                continue;
            var labels = card.ExpressionNotesByFileName.Values
                .Select(n => n.ChineseLabel.Trim())
                .Where(l => l.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (labels.Count > 0)
                facesByName[card.ChineseName] = new JArray(labels);
        }

        var root = new JObject
        {
            ["version"] = 1,
            ["functions"] = new JArray(PlayscriptFunctionCatalog.All.Select(f =>
            {
                var o = new JObject { ["name"] = f.Name };
                if (f.Param != null) o["param"] = f.Param;
                if (f.WithText) o["withText"] = true;
                if (f.Legacy) o["legacy"] = true;
                return o;
            })),
            ["aliases"] = JObject.FromObject(PlayscriptFunctionCatalog.Aliases),
            ["dialogNames"] = new JArray(maps.DialogNameMap.Keys),
            ["chatNames"] = new JArray(maps.ChatNameMap.Keys),
            ["faces"] = new JArray(maps.FaceMap.Keys),
            ["facesByName"] = facesByName,
        };
        var json = root.ToString(Formatting.None);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        return (json, hash);
    }
}
