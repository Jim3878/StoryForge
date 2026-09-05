using System.Text;
using Newtonsoft.Json;
using OfficeOpenXml;
using StoryForge.Core.Pipeline;

namespace StoryForge.Core.GoogleSheet;

public static class GooglePlayscriptSheetClient
{
    private const string GooglePlayscriptIndexSpreadsheetId = "15pAPnQnZoEkXs8Zf345i-CzREtjC88-BizjuYguexOM";

    public const string GooglePlayscriptExportUrl =
        "https://docs.google.com/spreadsheets/d/" + GooglePlayscriptIndexSpreadsheetId + "/export?format=xlsx";

    // Used by "查看遠端Sheet檔案" to open the master index spreadsheet directly in the browser.
    public const string GooglePlayscriptIndexEditUrl =
        "https://docs.google.com/spreadsheets/d/" + GooglePlayscriptIndexSpreadsheetId + "/edit";

    // Same Apps Script Web App endpoint the Unity-side "Graph本機 → GoogleSheet（上傳新劇本）" button already
    // uses — a plain webhook, not full Sheets API/OAuth, so an HTTP POST is all that's needed here too.
    public const string AppsScriptUrl =
        "https://script.google.com/macros/s/AKfycbxa896BqCJsy-qFBFaPoVnzio90kd3MSFEW-CQivz2jb7Dra-5SJNw5cO6Rd9741n6zaQ/exec";

    public const string GooglePlayscriptFolder = "11.Other/Sheet/Playscript";
    public const string GooglePlayscriptFileName = "PlayscriptIndex.xlsx";
    private const string ConfigSheetName = "CONFIG";
    private const string PlayscriptNameHeader = "劇本名";
    private const string SpreadsheetIdHeader = "Spreadsheet ID";
    private const string LinkHeader = "連結";

    // Different chapter tabs use different header text for the same "is this playscript actually empty"
    // check column — some sheets (e.g. A1~A4) have no such column at all, in which case there's simply
    // nothing to disqualify a row, so it's treated the same as "not marked empty".
    private static readonly string[] EmptyCheckHeaders = { "檢查空劇本", "空欄位檢查" };
    private const string EmptyMarkerValue = "空";

    private static readonly HttpClient HttpClient = new();

    public static async Task<string> DownloadAsync(string projectAssetsRoot)
    {
        var folderPath = Path.Combine(projectAssetsRoot, GooglePlayscriptFolder);
        Directory.CreateDirectory(folderPath);

        var excelPath = Path.Combine(folderPath, GooglePlayscriptFileName);
        await using var response = await HttpClient.GetStreamAsync(GooglePlayscriptExportUrl);
        await using var fileStream = File.Create(excelPath);
        await response.CopyToAsync(fileStream);

        return excelPath;
    }

    public static HashSet<string> LoadPlayscriptNames(string excelPath)
    {
        return new HashSet<string>(LoadEntries(excelPath).Keys, StringComparer.Ordinal);
    }

    // Richer read than LoadPlayscriptNames — also captures each playscript's own dedicated spreadsheet
    // link for "在GoogleSheet中打開".
    public static Dictionary<string, GooglePlayscriptSheetEntry> LoadEntries(string excelPath)
    {
        var result = new Dictionary<string, GooglePlayscriptSheetEntry>(StringComparer.Ordinal);
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

        using var package = new ExcelPackage(new FileInfo(excelPath));
        foreach (var sheet in package.Workbook.Worksheets)
        {
            if (sheet == null
                || sheet.Dimension == null
                || string.Equals(sheet.Name, ConfigSheetName, StringComparison.OrdinalIgnoreCase))
                continue;

            var hasHeader = IsPlayscriptNameHeader(sheet.Cells[1, 1].Text);
            var startRow = hasHeader ? 2 : 1;
            var idColumn = hasHeader ? FindHeaderColumn(sheet, SpreadsheetIdHeader) : 0;
            var linkColumn = hasHeader ? FindHeaderColumn(sheet, LinkHeader) : 0;
            var emptyCheckColumn = hasHeader ? FindAnyHeaderColumn(sheet, EmptyCheckHeaders) : 0;

            for (var row = startRow; row <= sheet.Dimension.End.Row; row++)
            {
                var playscript = PlayscriptNaming.Normalize(sheet.Cells[row, 1].Text);
                if (string.IsNullOrEmpty(playscript))
                    continue;

                var link = ResolveSpreadsheetLink(sheet, row, idColumn, linkColumn);
                var isMarkedEmpty = emptyCheckColumn > 0 &&
                    string.Equals(sheet.Cells[row, emptyCheckColumn].Text?.Trim(), EmptyMarkerValue, StringComparison.Ordinal);

                // A playscript can legitimately appear on more than one worksheet (e.g. an overview sheet
                // plus its own chapter sheet) — never let a later sheet's missing link overwrite one
                // already found on an earlier sheet.
                if (link == null && result.TryGetValue(playscript, out var existing) && existing.SpreadsheetLink != null)
                    continue;

                result[playscript] = new GooglePlayscriptSheetEntry
                {
                    PlayscriptName = playscript,
                    SpreadsheetLink = link,
                    IsMarkedEmpty = isMarkedEmpty,
                };
            }
        }

        return result;
    }

    // The "Spreadsheet ID" column holds the raw Drive file ID as plain text, which is far more reliable
    // than the "連結" column's Excel hyperlink metadata — Google's own xlsx export doesn't consistently
    // attach an actual Excel hyperlink object to every cell that renders as a clickable link inside Google
    // Sheets itself (e.g. cells populated via a HYPERLINK() formula rather than a manually inserted link
    // often export with no hyperlink object at all, even though the ID is right there in plain text).
    private static string? ResolveSpreadsheetLink(ExcelWorksheet sheet, int row, int idColumn, int linkColumn)
    {
        var spreadsheetId = idColumn > 0 ? sheet.Cells[row, idColumn].Text?.Trim() : null;
        if (!string.IsNullOrEmpty(spreadsheetId))
            return $"https://docs.google.com/spreadsheets/d/{spreadsheetId}/edit";

        var link = linkColumn > 0 ? sheet.Cells[row, linkColumn].Hyperlink?.AbsoluteUri : null;
        return string.IsNullOrEmpty(link) ? null : link;
    }

    // All worksheet tab names (chapters), used to resolve which existing tab a newly-appended playscript
    // row belongs on — mirrors the Unity-side "Graph本機 → GoogleSheet" button's own resolution logic.
    public static List<string> GetSheetTabNames(string excelPath)
    {
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
        using var package = new ExcelPackage(new FileInfo(excelPath));
        return package.Workbook.Worksheets
            .Where(sheet => sheet != null && !string.Equals(sheet.Name, ConfigSheetName, StringComparison.OrdinalIgnoreCase))
            .Select(sheet => sheet.Name)
            .Where(name => !string.IsNullOrEmpty(name))
            .ToList();
    }

    public static List<GooglePlayscriptUploadRow> BuildUploadRows(List<string> sheetTabNames, IEnumerable<string> playscripts)
    {
        return playscripts
            .Select(PlayscriptNaming.Normalize)
            .Where(PlayscriptNaming.IsValidChapterPlayscriptName)
            .Distinct()
            .Select(playscript =>
            {
                var chapterKey = PlayscriptNaming.GetChapterName(playscript);
                var sheetName = ResolveSheetName(sheetTabNames, chapterKey);
                return new GooglePlayscriptUploadRow(chapterKey, sheetName, playscript);
            })
            .ToList();
    }

    public static async Task<GooglePlayscriptUploadResponse?> UploadMissingRowsAsync(List<GooglePlayscriptUploadRow> rows)
    {
        if (rows.Count == 0)
            throw new InvalidOperationException("沒有需要上傳的劇本。");

        var json = JsonConvert.SerializeObject(new GooglePlayscriptUploadRequest(rows));
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await HttpClient.PostAsync(AppsScriptUrl, content);
        var responseText = await response.Content.ReadAsStringAsync();
        return ValidateUploadResponse(responseText);
    }

    private static GooglePlayscriptUploadResponse? ValidateUploadResponse(string? response)
    {
        var trimmed = response?.Trim();
        if (string.IsNullOrEmpty(trimmed) || string.Equals(trimmed, "OK", StringComparison.OrdinalIgnoreCase))
            return null;

        // Apps Script's /exec endpoint always executes doPost() synchronously — rows get written to the
        // Sheet regardless — but the HTTP response that follows execution can, for reasons outside this
        // tool's control (a Google-side redirect quirk after execution), come back as Google's own generic
        // "找不到以下指令碼函式：doGet" HTML error page instead of the script's real JSON/"OK" response. This
        // has been directly confirmed to happen even though the write already succeeded, so an HTML-shaped
        // response is treated as "can't verify from here, but nothing indicates the write failed" rather
        // than a hard error — a genuine {"errors": [...]} JSON response from the script still throws below.
        if (trimmed.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
            return null;

        GooglePlayscriptUploadResponse? parsed;
        try
        {
            parsed = JsonConvert.DeserializeObject<GooglePlayscriptUploadResponse>(trimmed);
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException($"Apps Script 回應格式異常：{trimmed}", e);
        }

        if (parsed?.Errors is { Count: > 0 })
            throw new InvalidOperationException(string.Join("\n", parsed.Errors));

        return parsed;
    }

    // Exact match on a tab's (cleaned-up) name first; falls back to a "chapter + trailing non-digit"
    // prefix match (e.g. chapter "A2" matching tab "A2打鬧") when there's no exact tab for this chapter yet.
    private static string ResolveSheetName(List<string> sheetTabNames, string chapterKey)
    {
        var exactMatches = sheetTabNames
            .Where(name => string.Equals(NormalizeSheetName(name), chapterKey, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (exactMatches.Count == 1)
            return exactMatches[0];

        var prefixMatches = sheetTabNames.Where(name => IsSheetNameForChapterKey(name, chapterKey)).ToList();
        if (prefixMatches.Count == 1)
            return prefixMatches[0];
        if (prefixMatches.Count == 0)
            return chapterKey;

        throw new InvalidOperationException($"章節「{chapterKey}」對應到多個分頁：{string.Join(", ", prefixMatches)}");
    }

    private static bool IsSheetNameForChapterKey(string sheetName, string chapterKey)
    {
        var normalized = NormalizeSheetName(sheetName);
        if (!normalized.StartsWith(chapterKey, StringComparison.OrdinalIgnoreCase))
            return false;
        if (normalized.Length == chapterKey.Length)
            return true;

        return !char.IsDigit(normalized[chapterKey.Length]);
    }

    private static string NormalizeSheetName(string sheetName)
    {
        return PlayscriptNaming.RemoveInvisibleFormatCharacters(sheetName ?? string.Empty).Trim();
    }

    private static int FindHeaderColumn(ExcelWorksheet sheet, string header)
    {
        var endColumn = sheet.Dimension?.End.Column ?? 0;
        for (var column = 1; column <= endColumn; column++)
        {
            if (string.Equals(sheet.Cells[1, column].Text?.Trim(), header, StringComparison.Ordinal))
                return column;
        }

        return 0;
    }

    private static int FindAnyHeaderColumn(ExcelWorksheet sheet, IEnumerable<string> headers)
    {
        foreach (var header in headers)
        {
            var column = FindHeaderColumn(sheet, header);
            if (column > 0)
                return column;
        }

        return 0;
    }

    private static bool IsPlayscriptNameHeader(string? value)
    {
        return string.Equals(value?.Trim(), PlayscriptNameHeader, StringComparison.Ordinal);
    }
}

public sealed class GooglePlayscriptSheetEntry
{
    public string PlayscriptName { get; init; } = string.Empty;
    public string? SpreadsheetLink { get; init; }

    // True when the row's own "empty check" column (header text varies per chapter tab) is literally
    // marked "空" — meaning the playscript is known to still be empty despite already having its own
    // spreadsheet link.
    public bool IsMarkedEmpty { get; init; }
}

public sealed class GooglePlayscriptUploadRequest
{
    public GooglePlayscriptUploadRequest(List<GooglePlayscriptUploadRow> rows)
    {
        Rows = rows;
    }

    [JsonProperty("action")]
    public string Action { get; } = "appendPlayscriptIndexRows";

    [JsonProperty("createMissingSheets")]
    public bool CreateMissingSheets { get; } = true;

    [JsonProperty("skipExisting")]
    public bool SkipExisting { get; } = true;

    [JsonProperty("headers")]
    public List<string> Headers { get; } = new() { "劇本名", "Spreadsheet ID", "連結", "備註" };

    [JsonProperty("rows")]
    public List<GooglePlayscriptUploadRow> Rows { get; }
}

// Mirrors the Unity-side "Graph本機 → GoogleSheet" button's append-row payload exactly (same Apps Script
// endpoint, so the shape has to match what it already expects) — including the "in劇本編輯器"="完成" key,
// which isn't declared in Headers above but the working Unity payload sends it anyway.
public sealed class GooglePlayscriptUploadRow
{
    public GooglePlayscriptUploadRow(string chapterKey, string sheetName, string playscriptName)
    {
        ChapterKey = chapterKey;
        SheetName = sheetName;
        PlayscriptName = playscriptName;
        Values = new List<string> { playscriptName, string.Empty, string.Empty, "Graph" };
        ValuesByHeader = new Dictionary<string, string>
        {
            ["劇本名"] = playscriptName,
            ["Spreadsheet ID"] = string.Empty,
            ["連結"] = string.Empty,
            ["備註"] = "Graph",
            ["in劇本編輯器"] = "完成",
        };
    }

    [JsonProperty("chapterKey")]
    public string ChapterKey { get; }

    [JsonProperty("sheetName")]
    public string SheetName { get; }

    [JsonProperty("playscriptName")]
    public string PlayscriptName { get; }

    [JsonProperty("values")]
    public List<string> Values { get; }

    [JsonProperty("valuesByHeader")]
    public Dictionary<string, string> ValuesByHeader { get; }
}

public sealed class GooglePlayscriptUploadResponse
{
    [JsonProperty("success")]
    public bool? Success { get; set; }

    [JsonProperty("status")]
    public string? Status { get; set; }

    [JsonProperty("result")]
    public string? Result { get; set; }

    [JsonProperty("message")]
    public string? Message { get; set; }

    [JsonProperty("errors")]
    public List<string>? Errors { get; set; }
}
