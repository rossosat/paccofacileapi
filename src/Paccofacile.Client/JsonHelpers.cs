using System.Text;
using System.Text.Json;

namespace Paccofacile.Client;

public static class JsonHelpers
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
        WriteIndented = false,
    };

    public static readonly JsonSerializerOptions Pretty = new(Options) { WriteIndented = true };

    /// <summary>Walks header.notification.messages.* and collects human readable error strings.</summary>
    public static string? ExtractErrorMessages(JsonElement root)
    {
        var sb = new StringBuilder();
        void Walk(JsonElement el, string path)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var p in el.EnumerateObject()) Walk(p.Value, path.Length == 0 ? p.Name : path + "." + p.Name);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in el.EnumerateArray()) Walk(item, path);
                    break;
                case JsonValueKind.String:
                    if (path.Contains("message", StringComparison.OrdinalIgnoreCase) || path.Contains("error", StringComparison.OrdinalIgnoreCase))
                        sb.Append(path).Append(": ").Append(el.GetString()).Append("; ");
                    break;
            }
        }
        Walk(root, "");
        return sb.Length == 0 ? null : sb.ToString().TrimEnd(' ', ';');
    }

    /// <summary>Depth-first search of the first property whose name matches (case-insensitive).</summary>
    public static JsonElement? FindProperty(JsonElement root, params string[] names)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in root.EnumerateObject())
                if (names.Any(n => string.Equals(n, p.Name, StringComparison.OrdinalIgnoreCase))) return p.Value;
            foreach (var p in root.EnumerateObject())
            {
                var found = FindProperty(p.Value, names);
                if (found.HasValue) return found;
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                var found = FindProperty(item, names);
                if (found.HasValue) return found;
            }
        }
        return null;
    }

    public static string? AsString(JsonElement? el)
    {
        if (!el.HasValue) return null;
        var e = el.Value;
        return e.ValueKind switch
        {
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }
}
