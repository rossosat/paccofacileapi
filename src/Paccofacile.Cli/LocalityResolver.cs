using System.Globalization;
using System.Text;
using Paccofacile.Client;

namespace Paccofacile.Cli;

/// <summary>
/// Normalises an Italian destination against Paccofacile's locality database before quoting:
/// fixes city spelling/case, fills the province, and rejects generic CAPs (e.g. 80100 Napoli) with the list of valid ones.
/// </summary>
public sealed class LocalityResolver
{
    private readonly PaccofacileClient _api;
    private readonly Dictionary<string, List<LocalityItem>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public LocalityResolver(PaccofacileClient api) => _api = api;

    /// <summary>Returns true when the address is usable (possibly adjusted in place). Warnings describe adjustments; error explains a rejection.</summary>
    public async Task<(bool Ok, string? Error)> ResolveAsync(ShipmentAddress a, List<string> warnings, CancellationToken ct = default)
    {
        if (!string.Equals(a.IsoCode, "IT", StringComparison.OrdinalIgnoreCase)) return (true, null);
        if (string.IsNullOrWhiteSpace(a.PostalCode)) return (false, "CAP mancante");

        var cap = a.PostalCode.Trim();
        var byCap = (await SearchAsync(cap, ct)).Where(i => string.Equals(i.PostalCode ?? i.Cap, cap, StringComparison.Ordinal)).ToList();

        if (byCap.Count > 0)
        {
            var city = Norm(a.City);
            var match = byCap.FirstOrDefault(i => Norm(i.Locality) == city)
                     ?? byCap.FirstOrDefault(i => Norm(i.Locality).StartsWith(city) || city.StartsWith(Norm(i.Locality)))
                     ?? byCap.FirstOrDefault(i => Norm(i.Locality).Contains(city) || city.Contains(Norm(i.Locality)));

            if (match is null)
            {
                // never guess among several towns sharing a CAP (e.g. 07030 = Badesi, Bulzi, Cargeghe…): a typo would ship to the wrong place
                if (byCap.Count > 1)
                    return (false, $"città '{a.City}' non trovata tra le località del CAP {cap}: {string.Join(", ", byCap.Select(i => i.Locality))}. Correggi --city");
                match = byCap[0];
                warnings.Add($"città '{a.City}' non corrisponde al CAP {cap}: uso '{match.Locality}' ({match.StateOrProvinceCode}), unica località per questo CAP");
            }
            else if (!string.Equals(match.Locality, a.City, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"città normalizzata: '{a.City}' -> '{match.Locality}'");
            }

            a.City = match.Locality ?? a.City;
            if (!string.IsNullOrWhiteSpace(match.StateOrProvinceCode))
            {
                if (!string.IsNullOrWhiteSpace(a.StateOrProvinceCode) && !string.Equals(a.StateOrProvinceCode, match.StateOrProvinceCode, StringComparison.OrdinalIgnoreCase))
                    warnings.Add($"provincia corretta: '{a.StateOrProvinceCode}' -> '{match.StateOrProvinceCode}'");
                a.StateOrProvinceCode = match.StateOrProvinceCode;
            }
            return (true, null);
        }

        // CAP unknown: try by city to suggest valid CAPs
        var byCity = string.IsNullOrWhiteSpace(a.City) ? new List<LocalityItem>() : await SearchAsync(a.City, ct);
        if (byCity.Count == 1 && !string.IsNullOrWhiteSpace(byCity[0].PostalCode))
        {
            // single-CAP town written with a wrong CAP: safe to fix
            var only = byCity[0];
            warnings.Add($"CAP corretto: '{cap}' -> '{only.PostalCode}' ({only.Locality} {only.StateOrProvinceCode})");
            a.PostalCode = only.PostalCode!;
            a.City = only.Locality ?? a.City;
            a.StateOrProvinceCode = only.StateOrProvinceCode ?? a.StateOrProvinceCode;
            return (true, null);
        }
        if (byCity.Count > 1)
        {
            var caps = byCity.Select(i => i.PostalCode).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().OrderBy(c => c).ToList();
            return (false, $"CAP {cap} non valido per '{a.City}' (CAP generico?). Chiedi al cliente il CAP esatto; validi: {string.Join(", ", caps.Take(20))}{(caps.Count > 20 ? ", …" : "")}");
        }
        return (false, $"località non trovata: CAP {cap} / '{a.City}'");
    }

    private async Task<List<LocalityItem>> SearchAsync(string search, CancellationToken ct)
    {
        if (_cache.TryGetValue(search, out var cached)) return cached;
        var items = await _api.SearchLocalitiesAsync("IT", search, ct);
        _cache[search] = items;
        return items;
    }

    private static string Norm(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var formD = s.Trim().ToUpperInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in formD)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(c == '\'' || c == '-' ? ' ' : c);
        }
        return System.Text.RegularExpressions.Regex.Replace(sb.ToString().Normalize(NormalizationForm.FormC), @"\s+", " ").Trim();
    }
}
