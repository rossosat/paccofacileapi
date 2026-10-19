using System.Globalization;
using System.Text.RegularExpressions;
using Paccofacile.Client;

namespace Paccofacile.Cli;

/// <summary>One line of an order (SKU + quantity).</summary>
public sealed record OrderLine(string Sku, string Description, int Quantity, string? OrderItemId, decimal? UnitPrice);

/// <summary>Marketplace-agnostic order to ship.</summary>
public sealed class OutboundOrder
{
    public string OrderId { get; set; } = "";
    public string Source { get; set; } = "manual";
    public ShipmentAddress Destination { get; set; } = new();
    public List<OrderLine> Lines { get; set; } = new();
    public decimal? GoodsValue { get; set; }
    public string? Notes { get; set; }

    /// <summary>Parcels given by the source file itself (override SKU map / default parcel).</summary>
    public List<Parcel>? ExplicitParcels { get; set; }

    /// <summary>Raw values that could not be mapped cleanly (shown to the operator).</summary>
    public List<string> Warnings { get; } = new();

    public override string ToString() => $"{Source} {OrderId} -> {Destination}";
}

public static class AddressParser
{
    private static readonly Regex TrailingNumber = new(@"^(?<street>.+?)[\s,]+(?<num>\d{1,5}\s*[A-Za-z]?(?:\s*/\s*[A-Za-z0-9]{1,4})?(?:\s*bis)?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LeadingNumber = new(@"^(?<num>\d{1,5}[A-Za-z]?)[\s,]+(?<street>.+)$", RegexOptions.Compiled);

    /// <summary>Splits "Via Roma 12/A" into ("Via Roma", "12/A"). Falls back to building number "SNC".</summary>
    public static (string Street, string Number) Split(string line1, string? line2 = null, string? line3 = null)
    {
        var lines = new[] { line1, line2, line3 }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => Regex.Replace(s!.Trim(), @"\s+", " "))
            .ToList();
        if (lines.Count == 0) return ("", "SNC");

        // 1) look for the number line by line: "Corso Italia, 45/B" + "Scala C" -> street "Corso Italia Scala C", number "45/B"
        for (var i = 0; i < lines.Count; i++)
        {
            var (street, number) = SplitSingle(lines[i]);
            if (number is null) continue;
            var rest = lines.Where((_, j) => j != i);
            var fullStreet = string.Join(" ", new[] { street }.Concat(rest)).Trim(' ', ',');
            return (Regex.Replace(fullStreet, @"\s+", " "), number);
        }

        // 2) nothing matched: keep everything as the street
        return (string.Join(" ", lines).Trim(' ', ','), "SNC");
    }

    private static (string Street, string? Number) SplitSingle(string line)
    {
        var m = TrailingNumber.Match(line);
        if (m.Success) return (m.Groups["street"].Value.Trim(' ', ','), Regex.Replace(m.Groups["num"].Value, @"\s+", "").ToUpperInvariant());

        m = LeadingNumber.Match(line);
        if (m.Success) return (m.Groups["street"].Value.Trim(' ', ','), m.Groups["num"].Value.ToUpperInvariant());

        // number in the middle: "Via Roma 12 Scala B int 4"
        m = Regex.Match(line, @"^(?<street>.+?)[\s,]+(?<num>\d{1,5}(?:\s*/\s*[A-Za-z0-9]{1,4}|[A-Za-z])?)\b(?<rest>.*)$");
        if (m.Success) return ((m.Groups["street"].Value + " " + m.Groups["rest"].Value.Trim()).Trim(' ', ','), Regex.Replace(m.Groups["num"].Value, @"\s+", "").ToUpperInvariant());

        return (line, null);
    }

    public static string NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return "";
        var p = Regex.Replace(phone, @"[^\d+]", "");
        if (p.StartsWith("00")) p = "+" + p[2..];
        return p;
    }

    public static decimal? ParseDecimal(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().Replace("EUR", "", StringComparison.OrdinalIgnoreCase).Replace("€", "").Trim();
        if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) return d;
        if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.GetCultureInfo("it-IT"), out d)) return d;
        return null;
    }
}
