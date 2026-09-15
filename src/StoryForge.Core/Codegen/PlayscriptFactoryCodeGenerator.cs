using System.Globalization;

namespace StoryForge.Core.Codegen;

public sealed class GeneratedPlayscriptOutput
{
    public GeneratedPlayscriptOutput(string playscriptName, string outputPath, string code)
    {
        PlayscriptName = playscriptName;
        OutputPath = outputPath;
        Code = code;
    }

    public string PlayscriptName { get; }
    public string OutputPath { get; }
    public string Code { get; }
}

// 「匯出成C#」的核心邏輯——輸入是 NodeScriptTableStore 本機 .md 劇本檔已經讀出來的列資料（欄位順序固定比照
// NodeScriptTableStore.Headers：名字/表情/配音情緒/台詞/功能/備註/參數），不再像 WinForms 版那樣即時從
// Google重新下載一份 xlsx——StoryForge 已經有自己的本機快取（下載Sheet劇本／上傳補完到Sheet 用的同一份
// .md），沒有理由為了產生 C# 再重打一次網路、也不需要依賴 PlaylistIndex.xlsx 的 Spreadsheet ID 欄位。
// Generate 本身是純函式（不寫檔）——呼叫端（PipelineStatusState）先呼叫這個算出程式碼與目標路徑，自行決定
// 目標檔案已存在時要不要跳覆蓋確認，確定要寫入才呼叫 WriteGeneratedOutput。
public static class PlayscriptFactoryCodeGenerator
{
    private const int ColName = 0;
    private const int ColExpression = 1;
    private const int ColVoiceMood = 2;
    private const int ColText = 3;
    private const int ColFunction = 4;
    private const int ColNote = 5;
    private const int ColParameter = 6;

    public static GeneratedPlayscriptOutput Generate(
        string playscriptName, IReadOnlyList<string[]> rows, EnumLabelMaps enumLabelMaps, string outputRoot)
    {
        var code = PlayscriptTextReplacementUtility.ApplyCommonPlayscriptCharacterReplacements(
            GenerateCode(playscriptName, rows, enumLabelMaps));
        var outputPath = GetOutputPath(playscriptName, outputRoot);
        return new GeneratedPlayscriptOutput(playscriptName, outputPath, code);
    }

    public static void WriteGeneratedOutput(GeneratedPlayscriptOutput output)
    {
        var directory = Path.GetDirectoryName(output.OutputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(output.OutputPath, output.Code, System.Text.Encoding.UTF8);
    }

    // Exposed so callers (PipelineStatusState's conflict-count estimate) can find out whether a playscript's
    // generated file already exists on disk without generating its code first — the output path only depends
    // on the playscript name, not on its row content.
    public static string GetOutputPath(string playscriptName, string outputRoot)
    {
        var parsed = PlayscriptNameParser.Parse(playscriptName);
        return Path.Combine(outputRoot, parsed.OutputFolderName, $"{parsed.ClassName}.cs");
    }

    private static string GenerateCode(string playscriptName, IReadOnlyList<string[]> rows, EnumLabelMaps enumLabelMaps)
    {
        var parsed = PlayscriptNameParser.Parse(playscriptName);
        var nameSpace = parsed.NamespaceName;
        ValidateSheetRows(parsed.PlayscriptName, rows, enumLabelMaps);

        var lines = new List<string>
        {
            "using Playscript;",
            "using Utility;",
            "",
            $"namespace {nameSpace}",
            "{",
            $"    public class {parsed.ClassName} : CommonPlayscriptFactory",
            "    {",
            "        public override string GetPlayscriptName()",
            "        {",
            $"            return \"{CodegenTextUtility.EscapeCSharpString(parsed.PlayscriptName)}\";",
            "        }",
            "",
            "        public override void Playscript()",
            "        {",
            parsed.IsHScene ? "            StartVideoAndFadeOn();" : "            StartRpgRole();",
        };

        var currentPortrait = string.Empty;
        var currentFace = string.Empty;
        var dialogueOpened = false;
        var rpgRoleEnabled = true;
        var choiceLabels = new HashSet<string>();
        var autoKey = 100;

        for (var i = 0; i < rows.Count; i++)
        {
            var cells = rows[i];
            var name = Cell(cells, ColName);
            var expression = Cell(cells, ColExpression);
            if (string.IsNullOrEmpty(expression))
                expression = "一般";

            var voiceMood = Cell(cells, ColVoiceMood);
            var text = Cell(cells, ColText);
            var function = Cell(cells, ColFunction);
            var note = Cell(cells, ColNote);
            var functionLower = function.ToLowerInvariant();
            var parameter = GetParameter(cells);

            // Button/Choice借用備註欄放跳轉 Label 是舊資料的相容路徑（見 ResolveChoiceLabel）——只有這種情況才
            // 不能把備註當一般註解印出來；一旦該列有填「參數」欄，備註就恢復成單純註解。
            var noteHoldsChoiceLabel = IsChoiceFunction(function) && string.IsNullOrEmpty(parameter);
            if (!string.IsNullOrEmpty(note) && !noteHoldsChoiceLabel)
            {
                CloseDialogueIfNeeded(lines, ref dialogueOpened, ref currentPortrait, ref currentFace);
                lines.Add($"            // {CodegenTextUtility.EscapeComment(note)}");
            }

            if (functionLower == "norole")
            {
                rpgRoleEnabled = false;
                currentPortrait = string.Empty;
                currentFace = string.Empty;

                if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(text))
                    continue;
            }

            if (string.IsNullOrEmpty(function) && string.IsNullOrEmpty(text))
            {
                AddBlankLine(lines);
                continue;
            }

            if (function == "Break")
            {
                lines.Add("            Break();");
                AddBlankLine(lines);
                continue;
            }

            if (IsChoiceFunction(function))
            {
                var choiceKey = 100;
                while (i < rows.Count)
                {
                    var choiceCells = rows[i];
                    var choiceFunction = Cell(choiceCells, ColFunction);
                    if (!IsChoiceFunction(choiceFunction))
                        break;

                    var choiceText = Cell(choiceCells, ColText);
                    var choiceLabel = ResolveChoiceLabel(choiceCells);
                    if (string.IsNullOrEmpty(choiceText) || string.IsNullOrEmpty(choiceLabel))
                        throw new InvalidOperationException($"第 {i + 1} 列選項缺少台詞或 Label。");

                    choiceLabels.Add(choiceLabel);
                    lines.Add(
                        $"            Choice().Button({choiceKey}, \"{CodegenTextUtility.EscapeCSharpString(choiceText)}\", \"{CodegenTextUtility.EscapeCSharpString(choiceLabel)}\");");
                    choiceKey += 100;
                    i++;
                }

                lines.Add("            Choice().Stop();");
                AddBlankLine(lines);
                i--;
                continue;
            }

            if (function == "Label")
            {
                if (string.IsNullOrEmpty(parameter))
                    throw new InvalidOperationException($"第 {i + 1} 列 Label 缺少「參數」欄的 label 名稱。");
                lines.Add(choiceLabels.Contains(parameter)
                    ? $"            Choice().Label(\"{CodegenTextUtility.EscapeCSharpString(parameter)}\");"
                    : $"            Label(\"{CodegenTextUtility.EscapeCSharpString(parameter)}\");");
                AddBlankLine(lines);
                continue;
            }

            var textLiteral = BuildDialogueTextLiteral(text, function);

            if (functionLower == "fadeondark")
            {
                CloseDialogueIfNeeded(lines, ref dialogueOpened, ref currentPortrait, ref currentFace);
                lines.Add("            FadeOnDark();");
                continue;
            }

            if (functionLower == "fadeoff")
            {
                CloseDialogueIfNeeded(lines, ref dialogueOpened, ref currentPortrait, ref currentFace);
                lines.Add("            FadeOff();");
                continue;
            }

            if (functionLower == "fadeonwhite")
            {
                CloseDialogueIfNeeded(lines, ref dialogueOpened, ref currentPortrait, ref currentFace);
                lines.Add("            FadeOnWhite();");
                continue;
            }

            if (functionLower == "opencg")
            {
                CloseDialogueIfNeeded(lines, ref dialogueOpened, ref currentPortrait, ref currentFace);
                var openCgArg = !string.IsNullOrEmpty(parameter) ? parameter : text;
                lines.Add($"            Gallery({BuildDialogueTextLiteral(openCgArg, function)}).OpenCg();");
                continue;
            }

            if (functionLower == "diff")
            {
                CloseDialogueIfNeeded(lines, ref dialogueOpened, ref currentPortrait, ref currentFace);
                var diffArg = !string.IsNullOrEmpty(parameter) ? parameter : text;
                lines.Add($"            Gallery().SetDiff({BuildDialogueTextLiteral(diffArg, function)});");
                continue;
            }

            if (functionLower == "closecg")
            {
                CloseDialogueIfNeeded(lines, ref dialogueOpened, ref currentPortrait, ref currentFace);
                lines.Add("            Gallery().CloseCg();");
                continue;
            }

            if (functionLower == "closedialogue")
            {
                lines.Add("            CloseDialogue();");
                dialogueOpened = false;
                currentPortrait = string.Empty;
                currentFace = string.Empty;
                continue;
            }

            if (functionLower == "waitseconds")
            {
                CloseDialogueIfNeeded(lines, ref dialogueOpened, ref currentPortrait, ref currentFace);
                if (!float.TryParse(parameter, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                    throw new InvalidOperationException($"第 {i + 1} 列 WaitSeconds 的參數欄不是合法秒數：「{parameter}」。");
                lines.Add($"            WaitSeconds({seconds.ToString(CultureInfo.InvariantCulture)}f);");
                continue;
            }

            if (functionLower == "chat")
            {
                lines.Add(
                    $"            Chat().AddMessage({BuildChatNameLiteral(name, enumLabelMaps)}, {autoKey}, {textLiteral});");
                autoKey += 100;
                continue;
            }

            if (functionLower == "tryaddlobby")
            {
                var lobbyParts = SplitParameterValues(parameter);
                if (lobbyParts.Length != 2)
                    throw new InvalidOperationException(
                        $"第 {i + 1} 列 TryAddLobby 的參數欄格式應為「大廳名稱;目標劇本名稱」（以分號分隔）。");
                lines.Add(
                    $"            TryAddLobby(\"{CodegenTextUtility.EscapeCSharpString(lobbyParts[0])}\", \"{CodegenTextUtility.EscapeCSharpString(lobbyParts[1])}\");");
                continue;
            }

            if (functionLower == "setmessagelobby")
            {
                lines.Add("            Chat().SetMessageLobby();");
                continue;
            }

            if (functionLower is "upblock" or "downblock" or "leftblock" or "rightblock")
            {
                CloseDialogueIfNeeded(lines, ref dialogueOpened, ref currentPortrait, ref currentFace);
                var blockMethod = functionLower switch
                {
                    "upblock" => "UpBlock",
                    "downblock" => "DownBlock",
                    "leftblock" => "LeftBlock",
                    _ => "RightBlock",
                };

                var dialogNameArg = string.Empty;
                if (!string.IsNullOrEmpty(name))
                {
                    if (!enumLabelMaps.DialogNameMap.TryGetValue(name, out var blockDialogEnum))
                        throw new InvalidOperationException($"名字「{name}」沒有設定 DialogName 對應。");
                    dialogNameArg = $"DialogName.{blockDialogEnum}, ";
                }

                lines.Add($"            {blockMethod}({dialogNameArg}{textLiteral}, {autoKey});");
                autoKey += 100;
                currentPortrait = string.Empty;
                currentFace = string.Empty;
                continue;
            }

            if (string.IsNullOrEmpty(text))
                continue;

            if (string.IsNullOrEmpty(name))
            {
                if (rpgRoleEnabled && !string.IsNullOrEmpty(currentPortrait))
                {
                    lines.Add("            ClearRpgRole();");
                    currentPortrait = string.Empty;
                    currentFace = string.Empty;
                }

                lines.Add($"            AsideSay({autoKey}, {textLiteral});");
                autoKey += 100;
                dialogueOpened = true;
                continue;
            }

            if (!enumLabelMaps.DialogNameMap.TryGetValue(name, out var dialogEnum))
                throw new InvalidOperationException($"名字「{name}」沒有設定 DialogName 對應。");

            if (rpgRoleEnabled)
                ApplyRpgRole(lines, name, expression, ref currentPortrait, ref currentFace, enumLabelMaps);

            lines.Add(string.IsNullOrEmpty(voiceMood)
                ? $"            Say(DialogName.{dialogEnum}, {autoKey}, {textLiteral});"
                : $"            Say(DialogName.{dialogEnum}, {autoKey}, \"{CodegenTextUtility.EscapeCSharpString(voiceMood)}\", {textLiteral});");

            autoKey += 100;
            dialogueOpened = true;
        }

        lines.Add(parsed.IsHScene
            ? "            EndVideoAndFadeOff();"
            : parsed.ShouldEndRpgRoleWithoutFinish
                ? "            EndRpgRoleWithoutFinish();"
                : parsed.IsSideStory
                    ? "            EndRpgRole(false);"
                    : "            EndRpgRole();");

        lines.Add("        }");
        lines.Add("    }");
        lines.Add("}");

        return string.Join("\n", lines);
    }

    private static void ValidateSheetRows(string playscriptName, IReadOnlyList<string[]> rows, EnumLabelMaps enumLabelMaps)
    {
        var errors = new List<string>();
        var rpgRoleEnabled = true;

        for (var i = 0; i < rows.Count; i++)
        {
            var cells = rows[i];
            var name = Cell(cells, ColName);
            var expression = Cell(cells, ColExpression);
            if (string.IsNullOrEmpty(expression))
                expression = "一般";

            var text = Cell(cells, ColText);
            var function = Cell(cells, ColFunction);
            var functionLower = function.ToLowerInvariant();

            if (functionLower == "norole")
            {
                rpgRoleEnabled = false;
                if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(text))
                    continue;
            }

            if (string.IsNullOrEmpty(function) && string.IsNullOrEmpty(text))
                continue;

            if (function == "Break")
                continue;

            if (IsChoiceFunction(function))
            {
                while (i < rows.Count)
                {
                    var choiceCells = rows[i];
                    var choiceFunction = Cell(choiceCells, ColFunction);
                    if (!IsChoiceFunction(choiceFunction))
                        break;

                    var choiceText = Cell(choiceCells, ColText);
                    var choiceLabel = ResolveChoiceLabel(choiceCells);
                    if (string.IsNullOrEmpty(choiceText) || string.IsNullOrEmpty(choiceLabel))
                        errors.Add($"Row {i + 1}: Choice requires text and label.");

                    i++;
                }

                i--;
                continue;
            }

            if (function == "Label")
            {
                if (string.IsNullOrEmpty(GetParameter(cells)))
                    errors.Add($"Row {i + 1}: Label 缺少「參數」欄的 label 名稱。");
                continue;
            }

            if (functionLower is "fadeondark" or "fadeoff" or "fadeonwhite" or "opencg" or "diff" or "closecg"
                or "closedialogue" or "chat" or "setmessagelobby")
                continue;

            if (functionLower == "tryaddlobby")
            {
                if (SplitParameterValues(GetParameter(cells)).Length != 2)
                    errors.Add($"Row {i + 1}: TryAddLobby 的參數欄格式應為「大廳名稱;目標劇本名稱」（以分號分隔）。");
                continue;
            }

            if (functionLower is "upblock" or "downblock" or "leftblock" or "rightblock")
            {
                if (!string.IsNullOrEmpty(name) && !enumLabelMaps.DialogNameMap.ContainsKey(name))
                    errors.Add($"Row {i + 1}: DialogName not found for name '{name}'.");
                continue;
            }

            if (functionLower == "waitseconds")
            {
                var waitParameter = GetParameter(cells);
                if (!float.TryParse(waitParameter, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    errors.Add($"Row {i + 1}: WaitSeconds 的參數欄不是合法秒數：「{waitParameter}」。");
                continue;
            }

            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(name))
                continue;

            if (!enumLabelMaps.DialogNameMap.ContainsKey(name))
                errors.Add($"Row {i + 1}: DialogName not found for name '{name}'.");

            if (rpgRoleEnabled && enumLabelMaps.PortraitMap.ContainsKey(name) &&
                !enumLabelMaps.FaceMap.ContainsKey(expression))
                errors.Add($"Row {i + 1}: Face not found for expression '{expression}'.");
        }

        if (errors.Count > 0)
            throw new InvalidOperationException($"{playscriptName} has {errors.Count} problem(s):\n{string.Join("\n", errors)}");
    }

    private static void ApplyRpgRole(
        List<string> lines, string name, string expression,
        ref string currentPortrait, ref string currentFace, EnumLabelMaps enumLabelMaps)
    {
        if (!enumLabelMaps.PortraitMap.TryGetValue(name, out var portraitSuffix))
        {
            if (!string.IsNullOrEmpty(currentPortrait))
                lines.Add("            ClearRpgRole();");

            currentPortrait = string.Empty;
            currentFace = string.Empty;
            return;
        }

        if (!enumLabelMaps.FaceMap.TryGetValue(expression, out var faceSuffix))
            throw new InvalidOperationException($"表情「{expression}」沒有設定 Face 對應。");

        var portrait = $"Portrait.{portraitSuffix}";
        var face = $"Face.{faceSuffix}";

        if (portrait != currentPortrait)
        {
            lines.Add($"            RpgRole({portrait}, {face});");
            currentPortrait = portrait;
            currentFace = face;
        }
        else if (face != currentFace)
        {
            lines.Add($"            ChangeRpgRole({portrait}, {face});");
            currentFace = face;
        }
    }

    // Never adds a blank line directly after another blank line — a sheet with a stretch of trailing
    // entirely-empty rows past the real content (a common Google Sheets artifact) would otherwise add one
    // blank output line per empty row, ballooning into hundreds of blank lines in the generated file.
    private static void AddBlankLine(List<string> lines)
    {
        if (lines.Count == 0 || lines[^1] != string.Empty)
            lines.Add(string.Empty);
    }

    private static void CloseDialogueIfNeeded(
        List<string> lines, ref bool dialogueOpened, ref string currentPortrait, ref string currentFace)
    {
        if (!dialogueOpened)
            return;

        lines.Add("            CloseDialogue();");
        dialogueOpened = false;
        currentPortrait = string.Empty;
        currentFace = string.Empty;
    }

    // "選項"/"Choice" are legacy aliases from before the dropdown had a real option for this — the sheet's
    // actual "功能" dropdown value for a multi-row choice block is "Button".
    private static bool IsChoiceFunction(string value)
    {
        return value == "選項"
            || string.Equals(value, "Choice", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "Button", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLargeTextFunction(string value)
    {
        return value == "大字"
            || string.Equals(value, "LargeText", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "Large", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSmallTextFunction(string value)
    {
        return string.Equals(value, "Small", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildDialogueTextLiteral(string text, string function)
    {
        var escaped = CodegenTextUtility.EscapeCSharpString(text);
        if (IsLargeTextFunction(function))
            return $"$\"<size={{CoreConfig.FontLarge}}>{escaped}</size>\"";
        if (IsSmallTextFunction(function))
            return $"$\"<size={{CoreConfig.FontSmall}}>{escaped}</size>\"";
        return $"\"{escaped}\"";
    }

    private static string Cell(string[] cells, int index) =>
        index >= 0 && index < cells.Length ? CodegenTextUtility.SanitizeText(cells[index]) : string.Empty;

    // "參數" is an optional column on the remote sheet template (see GooglePlayscriptSheetClient.
    // DownloadScriptTableAsync) but NodeScriptTableStore always writes/reads a fixed 7-column local .md, so
    // it's always at ColParameter here regardless of whether the remote sheet that originally fed it had the
    // column at all.
    private static string GetParameter(string[] cells) => Cell(cells, ColParameter);

    // Functions that need more than one value (currently only TryAddLobby) pack them all into "參數" as
    // "value1;value2" rather than splitting across "台詞"/"參數" — 分號 (semicolon) separated.
    private static string[] SplitParameterValues(string parameter)
    {
        return (parameter ?? string.Empty)
            .Split(';')
            .Select(CodegenTextUtility.SanitizeText)
            .Where(x => !string.IsNullOrEmpty(x))
            .ToArray();
    }

    // Button/Choice's jump-target label used to be written into "備註" (there was nowhere else to put it);
    // that still works for a row with no "參數" value, but "參數" wins when both are present.
    private static string ResolveChoiceLabel(string[] cells)
    {
        var parameter = GetParameter(cells);
        return !string.IsNullOrEmpty(parameter) ? parameter : Cell(cells, ColNote);
    }

    private static string BuildChatNameLiteral(string name, EnumLabelMaps enumLabelMaps)
    {
        return enumLabelMaps.ChatNameMap.TryGetValue(name, out var chatName)
            ? $"ChatName.{chatName}"
            : $"\"{CodegenTextUtility.EscapeCSharpString(name)}\"";
    }
}
