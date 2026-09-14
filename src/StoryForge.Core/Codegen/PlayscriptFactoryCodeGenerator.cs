using System.Globalization;
using OfficeOpenXml;

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

public sealed class CodeGenerationError
{
    public CodeGenerationError(string playscriptName, Exception exception)
    {
        PlayscriptName = playscriptName;
        Exception = exception;
    }

    public string PlayscriptName { get; }
    public Exception Exception { get; }
    public string Message => $"{PlayscriptName}: {Exception.Message}";
}

public sealed class CodeGenerationResult
{
    public CodeGenerationResult(List<string> outputPaths, List<CodeGenerationError> errors)
    {
        OutputPaths = outputPaths;
        Errors = errors;
    }

    public List<string> OutputPaths { get; }
    public List<CodeGenerationError> Errors { get; }
    public bool HasErrors => Errors.Count > 0;
}

public static class PlayscriptFactoryCodeGenerator
{
    private static readonly HttpClient HttpClient = new();

    public static async Task<CodeGenerationResult> GenerateSelectedAsync(
        string playscriptIndexPath,
        IEnumerable<string> selectedPlayscripts,
        EnumLabelMaps enumLabelMaps,
        string outputRoot,
        Action<int, int, string>? onProgress = null)
    {
        var selected = selectedPlayscripts
            .Select(CodegenTextUtility.SanitizeText)
            .Where(x => !string.IsNullOrEmpty(x))
            .ToHashSet();

        if (selected.Count == 0)
            throw new InvalidOperationException("No playscripts selected.");

        var rows = FindIndexRows(playscriptIndexPath, selected, out var missing);
        var errors = missing
            .Select(x => new CodeGenerationError(x, new InvalidOperationException("PlayscriptIndex row not found.")))
            .ToList();
        var outputs = new List<GeneratedPlayscriptOutput>();

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            onProgress?.Invoke(i, rows.Count, row.PlayscriptName);

            try
            {
                outputs.Add(await GenerateOutputAsync(row, enumLabelMaps, outputRoot));
            }
            catch (Exception e)
            {
                errors.Add(new CodeGenerationError(row.PlayscriptName, e));
            }
        }

        var outputPaths = new List<string>();
        foreach (var output in outputs)
        {
            try
            {
                WriteGeneratedOutput(output);
                outputPaths.Add(output.OutputPath);
            }
            catch (Exception e)
            {
                errors.Add(new CodeGenerationError(output.PlayscriptName, e));
            }
        }

        onProgress?.Invoke(rows.Count, rows.Count, string.Empty);
        return new CodeGenerationResult(outputPaths, errors);
    }

    private static async Task<GeneratedPlayscriptOutput> GenerateOutputAsync(
        IndexRow row, EnumLabelMaps enumLabelMaps, string outputRoot)
    {
        var code = PlayscriptTextReplacementUtility.ApplyCommonPlayscriptCharacterReplacements(
            await GenerateFromSpreadsheetAsync(row, enumLabelMaps));
        var outputPath = GetOutputPath(row.PlayscriptName, outputRoot);
        return new GeneratedPlayscriptOutput(row.PlayscriptName, outputPath, code);
    }

    private static void WriteGeneratedOutput(GeneratedPlayscriptOutput output)
    {
        var directory = Path.GetDirectoryName(output.OutputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(output.OutputPath, output.Code, System.Text.Encoding.UTF8);
    }

    private static List<IndexRow> FindIndexRows(
        string playscriptIndexPath, HashSet<string> selectedPlayscripts, out List<string> missing)
    {
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
        var rows = new List<IndexRow>();

        using (var package = new ExcelPackage(new FileInfo(playscriptIndexPath)))
        {
            foreach (var sheet in package.Workbook.Worksheets)
            {
                if (sheet?.Dimension == null
                    || string.Equals(sheet.Name, "CONFIG", StringComparison.OrdinalIgnoreCase))
                    continue;

                var columns = GetColumns(sheet, "劇本名", "Spreadsheet ID", "連結");
                for (var row = 2; row <= sheet.Dimension.End.Row; row++)
                {
                    var playscriptName = CodegenTextUtility.SanitizeText(sheet.Cells[row, columns["劇本名"]].Text);
                    if (!selectedPlayscripts.Contains(playscriptName))
                        continue;

                    var linkCell = sheet.Cells[row, columns["連結"]];
                    rows.Add(new IndexRow(
                        playscriptName,
                        CodegenTextUtility.SanitizeText(sheet.Cells[row, columns["Spreadsheet ID"]].Text),
                        CodegenTextUtility.SanitizeText(linkCell.Text),
                        CodegenTextUtility.SanitizeText(linkCell.Hyperlink?.OriginalString)));
                }
            }
        }

        var found = rows.Select(x => x.PlayscriptName).ToHashSet();
        missing = selectedPlayscripts.Where(x => !found.Contains(x)).ToList();
        return rows;
    }

    private static async Task<string> GenerateFromSpreadsheetAsync(IndexRow indexRow, EnumLabelMaps enumLabelMaps)
    {
        var spreadsheetId = GetSpreadsheetId(indexRow);
        if (string.IsNullOrEmpty(spreadsheetId))
            throw new InvalidOperationException($"{indexRow.PlayscriptName} 缺少 Spreadsheet ID。");

        var tempPath = await DownloadSpreadsheetAsync(spreadsheetId);
        try
        {
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
            using var package = new ExcelPackage(new FileInfo(tempPath));
            var sheet = package.Workbook.Worksheets.FirstOrDefault();
            if (sheet?.Dimension == null)
                throw new InvalidOperationException($"{indexRow.PlayscriptName} 的劇本表沒有內容。");

            return GenerateCode(indexRow.PlayscriptName, sheet, enumLabelMaps);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static async Task<string> DownloadSpreadsheetAsync(string spreadsheetId)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.xlsx");
        var url = $"https://docs.google.com/spreadsheets/d/{spreadsheetId}/export?format=xlsx";
        await using var response = await HttpClient.GetStreamAsync(url);
        await using var fileStream = File.Create(tempPath);
        await response.CopyToAsync(fileStream);
        return tempPath;
    }

    private static string GenerateCode(string playscriptLink, ExcelWorksheet sheet, EnumLabelMaps enumLabelMaps)
    {
        var parsed = PlayscriptNameParser.Parse(playscriptLink);
        var nameSpace = parsed.NamespaceName;
        var columns = GetColumns(sheet, "名字", "表情", "配音情緒", "台詞", "功能", "備註");
        ValidateSheetRows(parsed.PlayscriptName, sheet, columns, enumLabelMaps);

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
            "            StartRpgRole();",
        };

        var currentPortrait = string.Empty;
        var currentFace = string.Empty;
        var dialogueOpened = false;
        var rpgRoleEnabled = true;
        var choiceLabels = new HashSet<string>();
        var autoKey = 100;

        for (var row = 2; row <= sheet.Dimension.End.Row; row++)
        {
            var name = CodegenTextUtility.SanitizeText(sheet.Cells[row, columns["名字"]].Text);
            var expression = CodegenTextUtility.SanitizeText(sheet.Cells[row, columns["表情"]].Text);
            if (string.IsNullOrEmpty(expression))
                expression = "一般";

            var voiceMood = CodegenTextUtility.SanitizeText(sheet.Cells[row, columns["配音情緒"]].Text);
            var text = CodegenTextUtility.SanitizeText(sheet.Cells[row, columns["台詞"]].Text);
            var function = CodegenTextUtility.SanitizeText(sheet.Cells[row, columns["功能"]].Text);
            var note = CodegenTextUtility.SanitizeText(sheet.Cells[row, columns["備註"]].Text);
            var functionLower = function.ToLowerInvariant();
            var parameter = GetParameter(sheet, row, columns);

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
                while (row <= sheet.Dimension.End.Row)
                {
                    var choiceFunction = CodegenTextUtility.SanitizeText(sheet.Cells[row, columns["功能"]].Text);
                    if (!IsChoiceFunction(choiceFunction))
                        break;

                    var choiceText = CodegenTextUtility.SanitizeText(sheet.Cells[row, columns["台詞"]].Text);
                    var choiceLabel = ResolveChoiceLabel(sheet, row, columns);
                    if (string.IsNullOrEmpty(choiceText) || string.IsNullOrEmpty(choiceLabel))
                        throw new InvalidOperationException($"第 {row} 列選項缺少台詞或 Label。");

                    choiceLabels.Add(choiceLabel);
                    lines.Add(
                        $"            Choice().Button({choiceKey}, \"{CodegenTextUtility.EscapeCSharpString(choiceText)}\", \"{CodegenTextUtility.EscapeCSharpString(choiceLabel)}\");");
                    choiceKey += 100;
                    row++;
                }

                lines.Add("            Choice().Stop();");
                AddBlankLine(lines);
                row--;
                continue;
            }

            if (function == "Label")
            {
                lines.Add(choiceLabels.Contains(text)
                    ? $"            Choice().Label(\"{CodegenTextUtility.EscapeCSharpString(text)}\");"
                    : $"            Label(\"{CodegenTextUtility.EscapeCSharpString(text)}\");");
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
                    throw new InvalidOperationException($"第 {row} 列 WaitSeconds 的參數欄不是合法秒數：「{parameter}」。");
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

            if (functionLower == "lobby")
            {
                lines.Add(
                    $"            Chat().TryAddChatLobby({textLiteral}, \"{CodegenTextUtility.EscapeCSharpString(parsed.ClassName)}\");");
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

        lines.Add(parsed.ShouldEndRpgRoleWithoutFinish
            ? "            EndRpgRoleWithoutFinish();"
            : parsed.IsSideStory
                ? "            EndRpgRole(false);"
                : "            EndRpgRole();");

        lines.Add("        }");
        lines.Add("    }");
        lines.Add("}");

        return string.Join("\n", lines);
    }

    private static void ValidateSheetRows(
        string playscriptName, ExcelWorksheet sheet, Dictionary<string, int> columns, EnumLabelMaps enumLabelMaps)
    {
        var errors = new List<string>();
        var rpgRoleEnabled = true;

        for (var row = 2; row <= sheet.Dimension.End.Row; row++)
        {
            var name = CodegenTextUtility.SanitizeText(sheet.Cells[row, columns["名字"]].Text);
            var expression = CodegenTextUtility.SanitizeText(sheet.Cells[row, columns["表情"]].Text);
            if (string.IsNullOrEmpty(expression))
                expression = "一般";

            var text = CodegenTextUtility.SanitizeText(sheet.Cells[row, columns["台詞"]].Text);
            var function = CodegenTextUtility.SanitizeText(sheet.Cells[row, columns["功能"]].Text);
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
                while (row <= sheet.Dimension.End.Row)
                {
                    var choiceFunction = CodegenTextUtility.SanitizeText(sheet.Cells[row, columns["功能"]].Text);
                    if (!IsChoiceFunction(choiceFunction))
                        break;

                    var choiceText = CodegenTextUtility.SanitizeText(sheet.Cells[row, columns["台詞"]].Text);
                    var choiceLabel = ResolveChoiceLabel(sheet, row, columns);
                    if (string.IsNullOrEmpty(choiceText) || string.IsNullOrEmpty(choiceLabel))
                        errors.Add($"Row {row}: Choice requires text and label.");

                    row++;
                }

                row--;
                continue;
            }

            if (function == "Label")
                continue;

            if (functionLower is "fadeondark" or "fadeoff" or "fadeonwhite" or "opencg" or "diff" or "closecg"
                or "closedialogue" or "chat" or "lobby")
                continue;

            if (functionLower is "upblock" or "downblock" or "leftblock" or "rightblock")
            {
                if (!string.IsNullOrEmpty(name) && !enumLabelMaps.DialogNameMap.ContainsKey(name))
                    errors.Add($"Row {row}: DialogName not found for name '{name}'.");
                continue;
            }

            if (functionLower == "waitseconds")
            {
                var waitParameter = GetParameter(sheet, row, columns);
                if (!float.TryParse(waitParameter, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    errors.Add($"Row {row}: WaitSeconds 的參數欄不是合法秒數：「{waitParameter}」。");
                continue;
            }

            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(name))
                continue;

            if (!enumLabelMaps.DialogNameMap.ContainsKey(name))
                errors.Add($"Row {row}: DialogName not found for name '{name}'.");

            if (rpgRoleEnabled && enumLabelMaps.PortraitMap.ContainsKey(name) &&
                !enumLabelMaps.FaceMap.ContainsKey(expression))
                errors.Add($"Row {row}: Face not found for expression '{expression}'.");
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

    private static Dictionary<string, int> GetColumns(ExcelWorksheet sheet, params string[] requiredNames)
    {
        var result = new Dictionary<string, int>();
        for (var col = 1; col <= sheet.Dimension.End.Column; col++)
        {
            var header = CodegenTextUtility.SanitizeText(sheet.Cells[1, col].Text);
            if (!string.IsNullOrEmpty(header))
                result[header] = col;
        }

        foreach (var requiredName in requiredNames)
        {
            if (!result.ContainsKey(requiredName))
                throw new InvalidOperationException($"{sheet.Name} 缺少欄位：{requiredName}");
        }

        return result;
    }

    private static string GetOutputPath(string playscriptLink, string outputRoot)
    {
        var parsed = PlayscriptNameParser.Parse(playscriptLink);
        return Path.Combine(outputRoot, parsed.OutputFolderName, $"{parsed.ClassName}.cs");
    }

    private static string GetSpreadsheetId(IndexRow indexRow)
    {
        var fromLink = ExtractSpreadsheetId(indexRow.LinkUrl);
        return string.IsNullOrEmpty(fromLink) ? indexRow.SpreadsheetId : fromLink;
    }

    private static string ExtractSpreadsheetId(string? url)
    {
        var clean = CodegenTextUtility.SanitizeText(url);
        const string marker = "/d/";
        var markerIndex = clean.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
            return string.Empty;

        var startIndex = markerIndex + marker.Length;
        var endIndex = clean.IndexOf('/', startIndex);
        if (endIndex < 0)
            endIndex = clean.IndexOf('?', startIndex);
        if (endIndex < 0)
            endIndex = clean.Length;

        return clean.Substring(startIndex, endIndex - startIndex);
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

    // "參數" is an optional column (see GooglePlayscriptSheetClient.DownloadScriptTableAsync) added after
    // the "功能" dropdown's existing values were already in use — a playscript spreadsheet created before
    // it exists simply doesn't have the column.
    private static string GetParameter(ExcelWorksheet sheet, int row, Dictionary<string, int> columns)
    {
        return columns.TryGetValue("參數", out var parameterColumn)
            ? CodegenTextUtility.SanitizeText(sheet.Cells[row, parameterColumn].Text)
            : string.Empty;
    }

    // Button/Choice's jump-target label used to be written into "備註" (there was nowhere else to put it);
    // that still works for a row with no "參數" value, but "參數" wins when both are present.
    private static string ResolveChoiceLabel(ExcelWorksheet sheet, int row, Dictionary<string, int> columns)
    {
        var parameter = GetParameter(sheet, row, columns);
        return !string.IsNullOrEmpty(parameter)
            ? parameter
            : CodegenTextUtility.SanitizeText(sheet.Cells[row, columns["備註"]].Text);
    }

    private static string BuildChatNameLiteral(string name, EnumLabelMaps enumLabelMaps)
    {
        return enumLabelMaps.ChatNameMap.TryGetValue(name, out var chatName)
            ? $"ChatName.{chatName}"
            : $"\"{CodegenTextUtility.EscapeCSharpString(name)}\"";
    }

    private sealed class IndexRow
    {
        public IndexRow(string playscriptName, string spreadsheetId, string linkText, string linkUrl)
        {
            PlayscriptName = playscriptName;
            SpreadsheetId = spreadsheetId;
            LinkText = string.IsNullOrEmpty(linkText) ? playscriptName : linkText;
            LinkUrl = linkUrl;
        }

        public string PlayscriptName { get; }
        public string SpreadsheetId { get; }
        public string LinkText { get; }
        public string LinkUrl { get; }
    }
}
