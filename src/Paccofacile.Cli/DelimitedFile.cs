using System.Text;

namespace Paccofacile.Cli;

/// <summary>Minimal CSV/TSV reader with header lookup. Handles quotes, auto-detects ',' ';' or TAB.</summary>
public sealed class DelimitedFile
{
    public char Delimiter { get; }
    public List<string> Headers { get; } = new();
    public List<Dictionary<string, string>> Rows { get; } = new();

    private DelimitedFile(char delimiter) => Delimiter = delimiter;

    public static DelimitedFile Load(string path, char? delimiter = null)
    {
        var text = File.ReadAllText(path, DetectEncoding(path));
        var lines = SplitLines(text);
        if (lines.Count == 0) throw new InvalidOperationException($"File vuoto: {path}");

        var delim = delimiter ?? Detect(lines[0]);
        var file = new DelimitedFile(delim);
        var headers = ParseLine(lines[0], delim).Select(h => h.Trim().TrimStart('﻿')).ToList();
        file.Headers.AddRange(headers);

        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cells = ParseLine(line, delim);
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < headers.Count; i++)
                row[headers[i]] = i < cells.Count ? cells[i].Trim() : "";
            file.Rows.Add(row);
        }
        return file;
    }

    public static string Get(Dictionary<string, string> row, params string[] names)
    {
        foreach (var n in names)
            if (row.TryGetValue(n, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
        return "";
    }

    private static Encoding DetectEncoding(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8;
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode;
        try { new UTF8Encoding(false, true).GetString(bytes); return Encoding.UTF8; }
        catch { return Encoding.Latin1; }
    }

    private static char Detect(string header)
    {
        var counts = new[] { ('\t', header.Count(c => c == '\t')), (';', header.Count(c => c == ';')), (',', header.Count(c => c == ',')) };
        return counts.OrderByDescending(c => c.Item2).First().Item1;
    }

    private static List<string> SplitLines(string text)
    {
        // quote-aware line split (fields may contain newlines)
        var lines = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"') inQuotes = !inQuotes;
            if (!inQuotes && (c == '\n' || c == '\r'))
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                lines.Add(sb.ToString());
                sb.Clear();
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) lines.Add(sb.ToString());
        return lines;
    }

    private static List<string> ParseLine(string line, char delim)
    {
        var cells = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == delim) { cells.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        cells.Add(sb.ToString());
        return cells;
    }

    public static string Escape(string? value, char delim)
    {
        value ??= "";
        if (value.IndexOfAny(new[] { delim, '"', '\n', '\r' }) >= 0) return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }
}
