namespace StoryForge.Core.Graph;

// One Markdown line-table per 劇本 (PlayscriptProcessNode) story node — the actual dialogue content
// (name/expression/voice-emotion/line/function/note rows), matching the column layout of the playscript's
// own remote Google Sheet template ("劇本檔範本") so an AI tool can fill it in as plain text before it is
// ever converted/uploaded there. Lives at AppSettings.ExternalDataFolder\Nodes\<chapter>\<name>.md,
// alongside that same node's NodeContentStore .json file (same base name, resolved through
// NodeContentStore.ResolveBasePath so a duplicate-playscriptId node never splits across the two stores).
// Sync only ever creates a missing file — unlike NodeContentEntry's structured fields, a table's body is
// freeform text an AI is actively editing, so there is nothing safe to refresh on a file that already exists.
public static class NodeScriptTableStore
{
    // Column order matches the remote "劇本檔範本" spreadsheet exactly. "參數" is a later addition and is
    // optional on the remote side (GooglePlayscriptSheetClient.DownloadScriptTableAsync pads it with an
    // empty string for an older spreadsheet that doesn't have the column yet) — kept required here so every
    // local .md table has a stable column count regardless of which remote spreadsheet fed it.
    // The "功能" column's legal values and per-function rules are documented in the external data folder's
    // own AGENTS.md (read by the AI actually filling these files in) rather than echoed into every .md here.
    private static readonly string[] Headers = { "名字", "表情", "配音情緒", "台詞", "功能", "備註", "參數" };

    // Name-only existence check — used by PipelineStatusState's "本地劇本" column, which needs to flag a
    // leftover .md file even for a playscript name that no longer has a live graph node (and therefore no
    // guid to resolve the exact collision-suffixed path through). Ignores the rare duplicate-identity
    // collision case the same way ResolvePlainBasePath does.
    public static bool HasFile(string dataFolder, string playscriptName) =>
        File.Exists(NodeContentStore.ResolvePlainBasePath(dataFolder, playscriptName) + ".md");

    // Guid-aware existence check / path resolution, used by the "下載Sheet劇本" flow so a conflict check and
    // the write that follows it agree on the exact same path a duplicate-playscriptId collision would
    // otherwise resolve differently for. guid is null for a playscript with no live graph node (not on the
    // flow graph at all), which falls back to the plain (non-collision-suffixed) path — same as HasFile.
    public static string ResolvePath(string dataFolder, string playscriptName, string? guid) =>
        (guid != null
            ? NodeContentStore.ResolveBasePath(dataFolder, playscriptName, guid)
            : NodeContentStore.ResolvePlainBasePath(dataFolder, playscriptName)) + ".md";

    public static bool HasFileAt(string dataFolder, string playscriptName, string? guid) =>
        File.Exists(ResolvePath(dataFolder, playscriptName, guid));

    // True only when a local .md exists AND actually has real dialogue rows in it — a blank template
    // (SyncAll/WriteTemplateAtPlainPath's single placeholder row, all cells empty) reads back as zero rows
    // via ReadRows, so it counts the same as "no file" here. Used by the "下載Sheet劇本" conflict check so a
    // blank template 建立劇本檔 already created ahead of time never blocks a download that would otherwise
    // write real content straight through with no prompt.
    public static bool HasContentAt(string dataFolder, string playscriptName, string? guid) =>
        ReadRows(dataFolder, playscriptName, guid).Count > 0;

    // Unconditionally (re)writes a blank template at the PLAIN path, overwriting whatever is already
    // there — the .md counterpart to NodeContentStore.WriteAtPlainPath, used by the same 建立劇本檔
    // overwrite-confirmation flow once the user has agreed to take over a name an orphaned file occupied.
    public static void WriteTemplateAtPlainPath(string dataFolder, string playscriptName)
    {
        var basePath = NodeContentStore.ResolvePlainBasePath(dataFolder, playscriptName);
        var directory = Path.GetDirectoryName(basePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(basePath + ".md", BuildTemplate(playscriptName));
    }

    // Deletes this node's .md 劇本檔, if any — the .md counterpart to NodeContentStore.DeleteFile, called
    // for the same reason: the node is actually being deleted, so its old name never lingers as an orphan.
    public static void DeleteFile(string dataFolder, string playscriptName, string guid)
    {
        var path = ResolvePath(dataFolder, playscriptName, guid);
        if (File.Exists(path))
            File.Delete(path);
    }

    // Writes a playscript's real downloaded dialogue rows (from
    // GooglePlayscriptSheetClient.DownloadScriptTableAsync) into its .md 劇本檔, unconditionally overwriting
    // whatever is already there — unlike SyncAll below, the caller (the "下載Sheet劇本" batch flow) has
    // already resolved any overwrite conflict with the user before calling this.
    public static void WriteContent(string dataFolder, string playscriptName, string? guid, IReadOnlyList<string[]> rows)
    {
        var path = ResolvePath(dataFolder, playscriptName, guid);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, BuildContent(playscriptName, rows));
    }

    // Reverse of WriteContent/BuildContent — parses a local .md 劇本檔 back into row data, for the
    // "上傳補完到Sheet" flow to push local edits up to the playscript's own remote spreadsheet. A row that's
    // entirely blank after unescaping (the single placeholder row a blank template is written with) is
    // dropped, matching how DownloadScriptTableAsync itself never returns a blank row either. Returns an
    // empty list rather than throwing if the file doesn't exist — callers are expected to check
    // HasFile/HasFileAt first when "no local content" is a meaningful, non-error outcome for them.
    public static List<string[]> ReadRows(string dataFolder, string playscriptName, string? guid)
    {
        var path = ResolvePath(dataFolder, playscriptName, guid);
        if (!File.Exists(path))
            return new List<string[]>();

        var rows = new List<string[]>();
        var pastDivider = false;
        foreach (var line in File.ReadAllLines(path))
        {
            if (!pastDivider)
            {
                if (IsDividerRow(line))
                    pastDivider = true;
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
                continue;

            var cells = ParseRowCells(line);
            if (cells.All(string.IsNullOrEmpty))
                continue;

            rows.Add(cells);
        }

        return rows;
    }

    // Splits a table row line on "|" boundaries that aren't part of an escaped "\|", then drops the empty
    // leading/trailing segments the format's leading/trailing pipe always produces (see BuildContent).
    private static string[] ParseRowCells(string line)
    {
        var parts = CellSplitPattern.Split(line.Trim());
        return parts.Skip(1).Take(parts.Length - 2).Select(UnescapeCell).ToArray();
    }

    private static readonly System.Text.RegularExpressions.Regex CellSplitPattern =
        new(@"(?<!\\)\|", System.Text.RegularExpressions.RegexOptions.Compiled);

    // Matches a Markdown table divider row in ANY spacing/alignment variant — not just the compact
    // "|---|---|" this class itself writes (BuildDocument), but also a padded/aligned form like
    // "| ---------- | ---- | ... |" that a Markdown formatter (an editor's format-on-save, an AI tool
    // reflowing the table for readability) can rewrite it into. This file's whole premise is that a human
    // or AI is freely editing its table body, so the divider row cannot be assumed to stay byte-identical
    // to what this class originally wrote it as. A too-strict check here doesn't error — it just never
    // finds "past the divider", so ReadRows silently returns zero rows for a file that visibly has content.
    private static readonly System.Text.RegularExpressions.Regex DividerRowPattern =
        new(@"^\|(\s*:?-+:?\s*\|)+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool IsDividerRow(string line) => DividerRowPattern.IsMatch(line.Trim());

    // Reverse of EscapeCell.
    private static string UnescapeCell(string text) =>
        text.Trim().Replace("<br>", "\n").Replace("\\|", "|");

    public static void SyncAll(string dataFolder, IEnumerable<(string Guid, string PlayscriptName)> nodes)
    {
        foreach (var (guid, playscriptName) in nodes)
        {
            var path = NodeContentStore.ResolveBasePath(dataFolder, playscriptName, guid) + ".md";
            if (File.Exists(path))
                continue;

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(path, BuildTemplate(playscriptName));
        }
    }

    private static string BuildTemplate(string playscriptName)
    {
        var blankRow = "|" + string.Concat(Headers.Select(_ => "  |"));
        return BuildDocument(playscriptName, new[] { blankRow });
    }

    // Same document shape as the blank template (title line, table) but with the playscript's real
    // downloaded dialogue rows in place of the single blank row — so a downloaded .md looks exactly like a
    // hand-filled-in template rather than a different format the caller has to learn.
    private static string BuildContent(string playscriptName, IReadOnlyList<string[]> rows)
    {
        var bodyRows = rows.Count > 0
            ? rows.Select(r => "| " + string.Join(" | ", r.Select(EscapeCell)) + " |")
            : new[] { "|" + string.Concat(Headers.Select(_ => "  |")) };

        return BuildDocument(playscriptName, bodyRows);
    }

    private static string BuildDocument(string playscriptName, IEnumerable<string> bodyRows)
    {
        var header = "| " + string.Join(" | ", Headers) + " |";
        var divider = "|" + string.Concat(Headers.Select(_ => "---|"));

        return $"# {playscriptName}\n\n{header}\n{divider}\n{string.Join("\n", bodyRows)}\n";
    }

    // A cell's raw text can legitimately contain `|` (would be read as an extra column boundary) or
    // newlines (would break the table row onto multiple physical lines) — both are escaped rather than
    // stripped so the downloaded dialogue's actual text is never silently lost.
    private static string EscapeCell(string text) =>
        text.Replace("|", "\\|").Replace("\r\n", "\n").Replace("\n", "<br>");
}
