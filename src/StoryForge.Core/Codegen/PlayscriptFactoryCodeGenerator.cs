using System.Globalization;
using StoryForge.Core.Audio;
using StoryForge.Core.Ldtk;
using StoryForge.Core.Pipeline;

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
        string playscriptName, IReadOnlyList<string[]> rows, EnumLabelMaps enumLabelMaps,
        PlayscriptAudioClips audioClips, LdtkTransportIndex? transports, string outputRoot)
    {
        var code = PlayscriptTextReplacementUtility.ApplyCommonPlayscriptCharacterReplacements(
            GenerateCode(playscriptName, rows, enumLabelMaps, audioClips, transports));
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

    private static string GenerateCode(
        string playscriptName, IReadOnlyList<string[]> rows, EnumLabelMaps enumLabelMaps, PlayscriptAudioClips audioClips,
        LdtkTransportIndex? transports)
    {
        var parsed = PlayscriptNameParser.Parse(playscriptName);
        var nameSpace = parsed.NamespaceName;
        ValidateSheetRows(parsed.PlayscriptName, rows, enumLabelMaps, audioClips, transports);

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
        var choiceLabels = new HashSet<string>();
        var sheetLabels = CollectLabels(rows);
        var autoKey = 100;

        for (var i = 0; i < rows.Count; i++)
        {
            var cells = rows[i];
            var name = Cell(cells, ColName);
            var expression = Cell(cells, ColExpression);
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

            // NoRole 只作用在它自己這一列（見 AGENTS.md）：這句台詞不顯示立繪，下一列有名字的台詞照常恢復 RpgRole。
            // 一般台詞列（功能空白或只有 Small/Large）表情留空也等同 NoRole，好讓 NoRole 跟字級能同時用。
            var rpgRoleEnabled = IsRpgRoleEnabled(functionLower, expression);
            if (functionLower == "norole" || (!rpgRoleEnabled && IsPlainDialogueRow(function, name, text)))
            {
                if (!string.IsNullOrEmpty(currentPortrait))
                    lines.Add("            ClearRpgRole();");
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

            // BreakNode 只會直接結束劇本，檔尾的收尾（解凍玩家、清立繪、關對話框）不會跑到——所以 Break 前要先補
            // 同一套收尾。前一列是 PlayPlayscript 時照手寫劇本慣例不補，交給接手的劇本自己開場。
            if (function == "Break")
            {
                if (!TryParseIsFinish(parameter, out var isFinish))
                    throw new InvalidOperationException($"第 {i + 1} 列 Break 的參數欄只能填 true、false 或留空：「{parameter}」。");
                lines.AddRange(BuildBreakEndingLines(parsed, isFinish, PreviousContentFunctionIs(rows, i, "playplayscript")));
                lines.Add("            Break();");
                AddBlankLine(lines);
                dialogueOpened = false;
                currentPortrait = string.Empty;
                currentFace = string.Empty;
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
                if (!TrySplitValueAndMemo(openCgArg, "CG 名稱", out var cgId, out var cgMemo, out var cgError))
                    throw new InvalidOperationException($"第 {i + 1} 列 OpenCg：{cgError}");
                lines.Add($"            Gallery({BuildDialogueTextLiteral(cgId, function)}).OpenCg();");
                AddMemoLine(lines, cgMemo);
                continue;
            }

            if (functionLower == "diff")
            {
                CloseDialogueIfNeeded(lines, ref dialogueOpened, ref currentPortrait, ref currentFace);
                var diffArg = !string.IsNullOrEmpty(parameter) ? parameter : text;
                if (!TrySplitValueAndMemo(diffArg, "差分名稱", out var diffId, out var diffMemo, out var diffError))
                    throw new InvalidOperationException($"第 {i + 1} 列 Diff：{diffError}");
                lines.Add($"            Gallery().SetDiff({BuildDialogueTextLiteral(diffId, function)});");
                AddMemoLine(lines, diffMemo);
                continue;
            }

            // Both fade to black, move the player and fade back in by themselves — the dialogue box has to go first.
            if (IsTransportFunction(functionLower))
            {
                CloseDialogueIfNeeded(lines, ref dialogueOpened, ref currentPortrait, ref currentFace);
                if (!TryBuildTransportCall(functionLower, parameter, transports, out var transportCall, out var transportError))
                    throw new InvalidOperationException($"第 {i + 1} 列 {function}：{transportError}");
                lines.Add($"            {transportCall}");
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

            // Just set the "night" bool flag, so like the flag rows below they leave the dialogue box alone.
            if (functionLower is "nighttime" or "daytime")
            {
                lines.Add(functionLower == "nighttime" ? "            NightTime();" : "            DayTime();");
                continue;
            }

            // The guide line is a map overlay, not a cutscene step — like the flag rows, it leaves the dialogue box alone.
            if (functionLower == "setguidepath")
            {
                if (!TryBuildGuidePathCall(parameter, transports, out var guidePathCall, out var guidePathError))
                    throw new InvalidOperationException($"第 {i + 1} 列 SetGuidePath：{guidePathError}");
                lines.Add($"            {guidePathCall}");
                continue;
            }

            if (functionLower == "clearguidepath")
            {
                lines.Add("            ClearGuidePath();");
                continue;
            }

            // Unity's SleepFade() already does CloseDialogue + FadeOnDark + DayTime + WaitSeconds + FadeOff itself,
            // so no extra CloseDialogue() here — only the bookkeeping of a closed dialogue box.
            if (functionLower == "sleepfade")
            {
                lines.Add("            SleepFade();");
                dialogueOpened = false;
                currentPortrait = string.Empty;
                currentFace = string.Empty;
                continue;
            }

            // Flag rows don't close the dialogue box either — they're invisible to the player.
            if (IsFlagFunction(functionLower))
            {
                if (!TryBuildFlagCall(functionLower, parameter, sheetLabels, out var flagCall, out var flagError))
                    throw new InvalidOperationException($"第 {i + 1} 列 {function}：{flagError}");
                lines.Add($"            {flagCall}");
                continue;
            }

            // Audio rows don't close the dialogue box — a sound effect mid-conversation is the normal case.
            if (IsAudioFunction(functionLower))
            {
                if (!TryBuildAudioCall(functionLower, parameter, audioClips, out var audioCall, out var audioError))
                    throw new InvalidOperationException($"第 {i + 1} 列 {function}：{audioError}");
                lines.Add($"            {audioCall}");
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

            if (functionLower == "playplayscript")
            {
                var target = PlayscriptNaming.Normalize(parameter);
                if (string.IsNullOrEmpty(target))
                    throw new InvalidOperationException($"第 {i + 1} 列 PlayPlayscript 缺少「參數」欄的目標劇本名稱。");
                lines.Add($"            PlayPlayscript(\"{CodegenTextUtility.EscapeCSharpString(target)}\");");
                continue;
            }

            if (functionLower is "upblock" or "downblock" or "leftblock" or "rightblock")
            {
                // Block 會自己 Say 這一列的台詞——台詞必須跟 Block 同一列，單獨一列的 Block 只會講一句空字串。
                if (string.IsNullOrEmpty(text))
                    throw new InvalidOperationException($"第 {i + 1} 列 {function} 缺少台詞——要跟擋門台詞放在同一列。");
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

        lines.Add(BuildDefaultEndingLine(parsed));

        lines.Add("        }");
        lines.Add("    }");
        lines.Add("}");

        return string.Join("\n", lines);
    }

    private static void ValidateSheetRows(
        string playscriptName, IReadOnlyList<string[]> rows, EnumLabelMaps enumLabelMaps, PlayscriptAudioClips audioClips,
        LdtkTransportIndex? transports)
    {
        var errors = new List<string>();
        var sheetLabels = CollectLabels(rows);

        for (var i = 0; i < rows.Count; i++)
        {
            var cells = rows[i];
            var name = Cell(cells, ColName);
            var expression = Cell(cells, ColExpression);
            var text = Cell(cells, ColText);
            var function = Cell(cells, ColFunction);
            var functionLower = function.ToLowerInvariant();

            var rpgRoleEnabled = IsRpgRoleEnabled(functionLower, expression);
            if (functionLower == "norole" && string.IsNullOrEmpty(name) && string.IsNullOrEmpty(text))
                continue;

            if (string.IsNullOrEmpty(function) && string.IsNullOrEmpty(text))
                continue;

            if (function == "Break")
            {
                var breakParameter = GetParameter(cells);
                if (!TryParseIsFinish(breakParameter, out _))
                    errors.Add($"Row {i + 1}: Break 的參數欄只能填 true、false 或留空：「{breakParameter}」。");
                continue;
            }

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

            if (functionLower is "opencg" or "diff")
            {
                var value = GetParameter(cells);
                if (string.IsNullOrEmpty(value))
                    value = text;
                if (!TrySplitValueAndMemo(value, functionLower == "opencg" ? "CG 名稱" : "差分名稱", out _, out _, out var memoError))
                    errors.Add($"Row {i + 1}: {function} {memoError}");
                continue;
            }

            if (functionLower is "fadeondark" or "fadeoff" or "fadeonwhite" or "closecg"
                or "closedialogue" or "chat" or "setmessagelobby" or "nighttime" or "daytime" or "sleepfade" or "clearguidepath")
                continue;

            if (functionLower == "setguidepath")
            {
                if (!TryBuildGuidePathCall(GetParameter(cells), transports, out _, out var guidePathError))
                    errors.Add($"Row {i + 1}: {function} {guidePathError}");
                continue;
            }

            if (functionLower == "tryaddlobby")
            {
                if (SplitParameterValues(GetParameter(cells)).Length != 2)
                    errors.Add($"Row {i + 1}: TryAddLobby 的參數欄格式應為「大廳名稱;目標劇本名稱」（以分號分隔）。");
                continue;
            }

            // 只擋空白；目標劇本的格式／是否存在交給匯出批次（PipelineStatusState）詢問使用者，不在這裡報錯
            if (functionLower == "playplayscript")
            {
                if (string.IsNullOrEmpty(PlayscriptNaming.Normalize(GetParameter(cells))))
                    errors.Add($"Row {i + 1}: PlayPlayscript 缺少「參數」欄的目標劇本名稱。");
                continue;
            }

            if (functionLower is "upblock" or "downblock" or "leftblock" or "rightblock")
            {
                if (string.IsNullOrEmpty(text))
                    errors.Add($"Row {i + 1}: {function} 缺少台詞——要跟擋門台詞放在同一列。");
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

            if (IsTransportFunction(functionLower))
            {
                if (!TryBuildTransportCall(functionLower, GetParameter(cells), transports, out _, out var transportError))
                    errors.Add($"Row {i + 1}: {function} {transportError}");
                continue;
            }

            if (IsFlagFunction(functionLower))
            {
                if (!TryBuildFlagCall(functionLower, GetParameter(cells), sheetLabels, out _, out var flagError))
                    errors.Add($"Row {i + 1}: {function} {flagError}");
                continue;
            }

            if (IsAudioFunction(functionLower))
            {
                if (!TryBuildAudioCall(functionLower, GetParameter(cells), audioClips, out _, out var audioError))
                    errors.Add($"Row {i + 1}: {function} {audioError}");
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

    private static string BuildDefaultEndingLine(ParsedPlayscriptName parsed)
    {
        return parsed.IsHScene
            ? "            EndVideoAndFadeOff();"
            : parsed.ShouldEndRpgRoleWithoutFinish
                ? "            EndRpgRoleWithoutFinish();"
                : parsed.IsSideStory
                    ? "            EndRpgRole(false);"
                    : "            EndRpgRole();";
    }

    // isFinish 留空 = 照這支劇本檔尾的預設收尾。有填時，一般劇本直接用 EndRpgRole(isFinish)；H 場景／World 劇本
    // 的收尾方法本身不帶 isFinish，就保留原收尾再補一行 SetFinish(isFinish)。
    private static IEnumerable<string> BuildBreakEndingLines(ParsedPlayscriptName parsed, bool? isFinish, bool afterPlayPlayscript)
    {
        if (afterPlayPlayscript)
        {
            if (isFinish.HasValue)
                yield return $"            SetFinish({FormatBool(isFinish.Value)});";
            yield break;
        }

        if (!isFinish.HasValue)
        {
            yield return BuildDefaultEndingLine(parsed);
            yield break;
        }

        if (parsed.IsHScene || parsed.ShouldEndRpgRoleWithoutFinish)
        {
            yield return BuildDefaultEndingLine(parsed);
            yield return $"            SetFinish({FormatBool(isFinish.Value)});";
            yield break;
        }

        yield return $"            EndRpgRole({FormatBool(isFinish.Value)});";
    }

    private static bool TryParseIsFinish(string value, out bool? isFinish)
    {
        isFinish = null;
        if (string.IsNullOrEmpty(value))
            return true;
        if (!bool.TryParse(value, out var parsed))
            return false;
        isFinish = parsed;
        return true;
    }

    private static string FormatBool(bool value) => value ? "true" : "false";

    // 往上找最近一列有內容（功能或台詞非空）的列，跳過空白列／只有備註的列。
    private static bool PreviousContentFunctionIs(IReadOnlyList<string[]> rows, int index, string functionLower)
    {
        for (var j = index - 1; j >= 0; j--)
        {
            var function = Cell(rows[j], ColFunction);
            if (string.IsNullOrEmpty(function) && string.IsNullOrEmpty(Cell(rows[j], ColText)))
                continue;
            return function.ToLowerInvariant() == functionLower;
        }

        return false;
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

    private static bool IsRpgRoleEnabled(string functionLower, string expression)
    {
        return functionLower != "norole" && !string.IsNullOrEmpty(expression);
    }

    // 會走到 Say/AsideSay 的一般台詞列——Chat、Block 等功能列有自己的立繪處理，表情留空不該替它們清立繪。
    private static bool IsPlainDialogueRow(string function, string name, string text)
    {
        return !string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(text)
            && (string.IsNullOrEmpty(function) || IsSmallTextFunction(function) || IsLargeTextFunction(function));
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

    // 每一列 PlayPlayscript 的目標劇本（已正規化），給匯出批次在寫檔前檢查格式與是否存在
    public static IReadOnlyList<(int Row, string Target)> FindPlayPlayscriptTargets(IReadOnlyList<string[]> rows)
    {
        var targets = new List<(int Row, string Target)>();
        for (var i = 0; i < rows.Count; i++)
        {
            if (Cell(rows[i], ColFunction).Equals("PlayPlayscript", StringComparison.OrdinalIgnoreCase))
                targets.Add((i + 1, PlayscriptNaming.Normalize(GetParameter(rows[i]))));
        }
        return targets;
    }

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

    // OpenCg / Diff accept an optional memo after one semicolon — "CG;備註" / "差分;備註" — emitted as
    // Gallery().SetMemo(...), which does nothing at runtime and only feeds the Unity CG import tool's placeholder
    // images. Unlike SplitParameterValues, a memo may not contain a half-width ';' (full-width '；' is fine), and
    // "名稱;" with an empty memo just means no memo.
    private static bool TrySplitValueAndMemo(string raw, string valueLabel, out string value, out string memo, out string error)
    {
        var parts = (raw ?? string.Empty).Split(';');
        value = CodegenTextUtility.SanitizeText(parts[0]);
        memo = parts.Length > 1 ? CodegenTextUtility.SanitizeText(parts[1]) : string.Empty;
        error = null;

        if (parts.Length > 2)
            error = "備註不能含半形分號「;」（全形「；」可以）。";
        else if (parts.Length == 2 && string.IsNullOrEmpty(value))
            error = $"「;」前面缺少{valueLabel}。";

        return error == null;
    }

    private static void AddMemoLine(List<string> lines, string memo)
    {
        if (!string.IsNullOrEmpty(memo))
            lines.Add($"            Gallery().SetMemo(\"{CodegenTextUtility.EscapeCSharpString(memo)}\");");
    }

    // Button/Choice's jump-target label used to be written into "備註" (there was nowhere else to put it);
    // that still works for a row with no "參數" value, but "參數" wins when both are present.
    private static string ResolveChoiceLabel(string[] cells)
    {
        var parameter = GetParameter(cells);
        return !string.IsNullOrEmpty(parameter) ? parameter : Cell(cells, ColNote);
    }

    private static bool IsTransportFunction(string functionLower) =>
        functionLower is "leveltransport" or "doorleveltransport" or "doorleveltransportwithoutfadeoff";

    // LevelTransport: "目標iid" or "目標iid;x;y" (the (string, float, float) overload). DoorLevelTransport:
    // "門iid" or "門iid;目標iid" — the target defaults to the door's own LDtk "target" field, and Unity's
    // DoorLevelTransport has no offset overload. Every iid is checked against Company.ldtk, since a wrong one
    // only shows up at runtime as a failed or misplaced transport. A door whose appearance is None has no
    // open animation, so DoorLevelTransport on it would only play a stray door sound.
    // DoorLevelTransportWithoutFadeOff takes the same parameters; it just leaves the screen dark for the sheet's
    // own later FadeOff row.
    private static bool TryBuildTransportCall(
        string functionLower, string parameter, LdtkTransportIndex? transports, out string? call, out string? error)
    {
        call = null;
        error = null;

        if (transports == null)
        {
            error = "找不到 Company.ldtk，無法檢查 iid。";
            return false;
        }

        var parts = SplitParameterValues(parameter);
        if (functionLower == "leveltransport")
        {
            if (parts.Length is not (1 or 3))
            {
                error = "參數欄格式應為「目標iid」或「目標iid;x偏移;y偏移」（以分號分隔）。";
                return false;
            }

            if (!TryCheckTransportTarget(parts[0], transports, out error))
                return false;

            var targetLiteral = $"\"{CodegenTextUtility.EscapeCSharpString(parts[0])}\"";
            if (parts.Length == 1)
            {
                call = $"LevelTransport({targetLiteral});";
                return true;
            }

            if (!TryParseOffset(parts[1], out var offsetX, out error) || !TryParseOffset(parts[2], out var offsetY, out error))
                return false;

            call = $"LevelTransport({targetLiteral}, {offsetX}f, {offsetY}f);";
            return true;
        }

        if (parts.Length is not (1 or 2))
        {
            error = "參數欄格式應為「門iid」或「門iid;目標iid」（以分號分隔）。";
            return false;
        }

        var doorIid = parts[0];
        if (!transports.TryGetTransport(doorIid, out var door))
        {
            error = $"門「{doorIid}」是{transports.DescribeIid(doorIid)}，不是 Transport。";
            return false;
        }

        if (!door.IsDoor)
        {
            error = $"「{doorIid}」（{door.Level}）的 appearance 是 None，不是門，請改用 LevelTransport。";
            return false;
        }

        var targetIid = parts.Length == 2 ? parts[1] : door.TargetIid;
        if (string.IsNullOrEmpty(targetIid))
        {
            error = $"門「{doorIid}」在 LDtk 沒有設定 target，請在參數欄補上「門iid;目標iid」。";
            return false;
        }

        if (!TryCheckTransportTarget(targetIid, transports, out error))
            return false;

        var doorMethod = functionLower == "doorleveltransportwithoutfadeoff"
            ? "DoorLevelTransportWithoutFadeOff"
            : "DoorLevelTransport";
        call = $"{doorMethod}(\"{CodegenTextUtility.EscapeCSharpString(doorIid)}\", \"{CodegenTextUtility.EscapeCSharpString(targetIid)}\");";
        return true;
    }

    // SetGuidePath: "起點;終點", each either a LocalEnum.GuidePathNodeKey name (emitted as GuidePathNodeKey.X) or a
    // PathPoint/TransportPathPoint iid (emitted as a string) — Unity has all four (key|iid, key|iid) overloads.
    // Both are checked against Company.ldtk: a key no point carries, or an iid that isn't a guide point, would
    // only fail at runtime as a guide line that never appears.
    private static bool TryBuildGuidePathCall(
        string parameter, LdtkTransportIndex? transports, out string? call, out string? error)
    {
        call = null;
        error = null;

        if (transports == null)
        {
            error = "找不到 Company.ldtk，無法檢查引導點。";
            return false;
        }

        var parts = SplitParameterValues(parameter);
        if (parts.Length != 2)
        {
            error = "參數欄格式應為「起點;終點」（以分號分隔，各自填 GuidePathNodeKey 名稱或引導點 iid）。";
            return false;
        }

        var args = new string[2];
        for (var i = 0; i < 2; i++)
        {
            var value = parts[i];
            if (transports.TryGetGuidePathNodeByKey(value, out _))
                args[i] = $"GuidePathNodeKey.{value}";
            else if (transports.TryGetGuidePathNode(value, out _))
                args[i] = $"\"{CodegenTextUtility.EscapeCSharpString(value)}\"";
            else
            {
                error = $"「{value}」不是 LDtk 裡有設定的 GuidePathNodeKey（{string.Join("、", transports.GuidePathNodeKeys)}），" +
                        $"也不是引導點的 iid（它是{transports.DescribeIid(value)}）。";
                return false;
            }
        }

        call = $"SetGuidePath({args[0]}, {args[1]});";
        return true;
    }

    private static bool TryCheckTransportTarget(string iid, LdtkTransportIndex transports, out string? error)
    {
        error = transports.TryGetTarget(iid, out _)
            ? null
            : $"目標「{iid}」是{transports.DescribeIid(iid)}，不是 TransportTarget。";
        return error == null;
    }

    private static bool TryParseOffset(string text, out string? literal, out string? error)
    {
        literal = null;
        error = null;
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            error = $"偏移量「{text}」不是數字。";
            return false;
        }

        literal = value.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    // Every Label row's name in this sheet. A jump to a label that isn't here throws a KeyNotFoundException
    // at runtime (Unity's jump nodes index nodeData.labelMap directly), so BoolFlagJump/IntFlagJump check it.
    // Label() and Choice().Label() both emit the same LabelNode, so either kind is a valid jump target.
    private static HashSet<string> CollectLabels(IReadOnlyList<string[]> rows)
    {
        var labels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cells in rows)
        {
            if (Cell(cells, ColFunction) == "Label" && !string.IsNullOrEmpty(GetParameter(cells)))
                labels.Add(GetParameter(cells));
        }

        return labels;
    }

    private static bool IsFlagFunction(string functionLower) =>
        functionLower is "setboolflag" or "boolflagjump" or "setintflag" or "addintflag" or "intflagjump";

    // 參數 formats: SetBoolFlag "旗標;true|false", BoolFlagJump "旗標;true|false;Label", SetIntFlag/AddIntFlag
    // "旗標;整數", IntFlagJump "旗標;比較;整數;Label". The flag name itself isn't checked against anything —
    // plenty of real flags (night, compile, ...) never appear on the flow graph, so there's no full list.
    private static bool TryBuildFlagCall(
        string functionLower, string parameter, HashSet<string> sheetLabels, out string? call, out string? error)
    {
        call = null;
        error = null;

        var (expectedFormat, expectedCount) = functionLower switch
        {
            "setboolflag" => ("旗標;true或false", 2),
            "boolflagjump" => ("旗標;true或false;Label", 3),
            "setintflag" or "addintflag" => ("旗標;整數", 2),
            _ => ("旗標;比較;整數;Label", 4),
        };

        var parts = SplitParameterValues(parameter);
        if (parts.Length != expectedCount)
        {
            error = $"參數欄格式應為「{expectedFormat}」（以分號分隔）。";
            return false;
        }

        var flagLiteral = $"\"{CodegenTextUtility.EscapeCSharpString(parts[0])}\"";

        if (functionLower is "setboolflag" or "boolflagjump")
        {
            if (!TryParseBoolValue(parts[1], out var boolValue))
            {
                error = $"「{parts[1]}」不是 true 或 false。";
                return false;
            }

            if (functionLower == "setboolflag")
            {
                call = $"BoolFlag({flagLiteral}).SetValue({BoolLiteral(boolValue)});";
                return true;
            }

            if (!TryCheckJumpLabel(parts[2], sheetLabels, out error))
                return false;

            call = $"BoolFlag({flagLiteral}).IfJump({BoolLiteral(boolValue)}, \"{CodegenTextUtility.EscapeCSharpString(parts[2])}\");";
            return true;
        }

        var valueText = functionLower == "intflagjump" ? parts[2] : parts[1];
        if (!int.TryParse(valueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
        {
            error = $"「{valueText}」不是整數。";
            return false;
        }

        var intLiteral = intValue.ToString(CultureInfo.InvariantCulture);
        if (functionLower == "setintflag")
        {
            call = $"IntFlag({flagLiteral}).SetValue({intLiteral});";
            return true;
        }

        if (functionLower == "addintflag")
        {
            call = $"IntFlag({flagLiteral}).AddValue({intLiteral});";
            return true;
        }

        var compare = parts[1] switch
        {
            "=" or "==" => "Equal",
            "!=" => "NotEqual",
            ">" => "Greater",
            ">=" => "GreaterOrEqual",
            "<" => "Less",
            "<=" => "LessOrEqual",
            _ => null,
        };
        if (compare == null)
        {
            error = $"比較符號「{parts[1]}」只能是 = != > >= < <= 其中之一。";
            return false;
        }

        if (!TryCheckJumpLabel(parts[3], sheetLabels, out error))
            return false;

        call = $"IntFlag({flagLiteral}).IfJump(Compare.{compare}, {intLiteral}, \"{CodegenTextUtility.EscapeCSharpString(parts[3])}\");";
        return true;
    }

    private static bool TryParseBoolValue(string text, out bool value)
    {
        value = string.Equals(text, "true", StringComparison.OrdinalIgnoreCase);
        return value || string.Equals(text, "false", StringComparison.OrdinalIgnoreCase);
    }

    private static string BoolLiteral(bool value) => value ? "true" : "false";

    private static bool TryCheckJumpLabel(string label, HashSet<string> sheetLabels, out string? error)
    {
        error = sheetLabels.Contains(label) ? null : $"跳轉目標 Label「{label}」在這份劇本裡找不到對應的 Label 列。";
        return error == null;
    }

    // PlayBgm/PushBgm/PlaySfx take "檔名[;音量]" in 參數; the rest take nothing. BGM volume always defaults to
    // 0.3 and is always emitted explicitly — Unity's own PushBgm default is 1, and writing it out keeps the
    // sheet's rule true no matter what the Unity-side defaults are. fadeDuration is deliberately not exposed:
    // Unity's ReturnLevelBgm and PushBgmNode currently ignore it.
    private const float DefaultBgmVolume = 0.3f;

    private static bool IsAudioFunction(string functionLower) =>
        functionLower is "playbgm" or "pushbgm" or "playsfx" or "popbgm" or "returnlevelbgm" or "mutebgm";

    private static bool TryBuildAudioCall(
        string functionLower, string parameter, PlayscriptAudioClips audioClips, out string? call, out string? error)
    {
        call = null;
        error = null;

        var noArgMethod = functionLower switch
        {
            "popbgm" => "PopBgm",
            "returnlevelbgm" => "ReturnLevelBgm",
            "mutebgm" => "MuteBgm",
            _ => null,
        };
        if (noArgMethod != null)
        {
            if (!string.IsNullOrEmpty(parameter))
            {
                error = "不需要填參數欄。";
                return false;
            }

            call = $"Audio().{noArgMethod}();";
            return true;
        }

        var isSfx = functionLower == "playsfx";
        var method = functionLower switch
        {
            "playbgm" => "PlayBgm",
            "pushbgm" => "PushBgm",
            _ => "PlaySfx",
        };

        var parts = SplitParameterValues(parameter);
        if (parts.Length is < 1 or > 2)
        {
            error = "參數欄格式應為「檔名」或「檔名;音量」。";
            return false;
        }

        var clip = parts[0];
        var known = isSfx ? audioClips.Sfx : audioClips.Bgm;
        if (!known.Contains(clip))
        {
            var folder = isSfx ? AudioAssetScanner.SfxFolderRelativePath : AudioAssetScanner.BgmFolderRelativePath;
            var caseMatch = known.FirstOrDefault(x => string.Equals(x, clip, StringComparison.OrdinalIgnoreCase));
            error = caseMatch != null
                ? $"檔名「{clip}」大小寫不符，應為「{caseMatch}」。"
                : $"{folder} 裡找不到檔名「{clip}」（不含副檔名）。";
            return false;
        }

        float? volume = isSfx ? null : DefaultBgmVolume;
        if (parts.Length == 2)
        {
            if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedVolume)
                || parsedVolume < 0 || parsedVolume > 1)
            {
                error = $"音量「{parts[1]}」不是 0～1 之間的數字。";
                return false;
            }

            volume = parsedVolume;
        }

        var clipLiteral = $"\"{CodegenTextUtility.EscapeCSharpString(clip)}\"";
        call = volume.HasValue
            ? $"Audio().{method}({clipLiteral}, {volume.Value.ToString(CultureInfo.InvariantCulture)}f);"
            : $"Audio().{method}({clipLiteral});";
        return true;
    }

    private static string BuildChatNameLiteral(string name, EnumLabelMaps enumLabelMaps)
    {
        return enumLabelMaps.ChatNameMap.TryGetValue(name, out var chatName)
            ? $"ChatName.{chatName}"
            : $"\"{CodegenTextUtility.EscapeCSharpString(name)}\"";
    }
}
