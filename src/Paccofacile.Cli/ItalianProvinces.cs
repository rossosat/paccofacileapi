using System.Globalization;
using System.Text;

namespace Paccofacile.Cli;

/// <summary>Maps Italian province names (as marketplaces export them) to the two-letter code Paccofacile wants.</summary>
public static class ItalianProvinces
{
    private static readonly Dictionary<string, string> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["agrigento"] = "AG", ["alessandria"] = "AL", ["ancona"] = "AN", ["aosta"] = "AO", ["valle d'aosta"] = "AO", ["valle d aosta"] = "AO",
        ["arezzo"] = "AR", ["ascoli piceno"] = "AP", ["asti"] = "AT", ["avellino"] = "AV", ["bari"] = "BA",
        ["barletta-andria-trani"] = "BT", ["barletta andria trani"] = "BT", ["belluno"] = "BL", ["benevento"] = "BN", ["bergamo"] = "BG",
        ["biella"] = "BI", ["bologna"] = "BO", ["bolzano"] = "BZ", ["bozen"] = "BZ", ["brescia"] = "BS", ["brindisi"] = "BR",
        ["cagliari"] = "CA", ["caltanissetta"] = "CL", ["campobasso"] = "CB", ["caserta"] = "CE", ["catania"] = "CT",
        ["catanzaro"] = "CZ", ["chieti"] = "CH", ["como"] = "CO", ["cosenza"] = "CS", ["cremona"] = "CR", ["crotone"] = "KR",
        ["cuneo"] = "CN", ["enna"] = "EN", ["fermo"] = "FM", ["ferrara"] = "FE", ["firenze"] = "FI", ["florence"] = "FI",
        ["foggia"] = "FG", ["forli-cesena"] = "FC", ["forlì-cesena"] = "FC", ["forli cesena"] = "FC", ["forlì"] = "FC", ["forli"] = "FC",
        ["frosinone"] = "FR", ["genova"] = "GE", ["genoa"] = "GE", ["gorizia"] = "GO", ["grosseto"] = "GR", ["imperia"] = "IM",
        ["isernia"] = "IS", ["l'aquila"] = "AQ", ["l aquila"] = "AQ", ["laquila"] = "AQ", ["la spezia"] = "SP", ["latina"] = "LT",
        ["lecce"] = "LE", ["lecco"] = "LC", ["livorno"] = "LI", ["lodi"] = "LO", ["lucca"] = "LU", ["macerata"] = "MC",
        ["mantova"] = "MN", ["mantua"] = "MN", ["massa-carrara"] = "MS", ["massa carrara"] = "MS", ["massa"] = "MS", ["matera"] = "MT",
        ["messina"] = "ME", ["milano"] = "MI", ["milan"] = "MI", ["modena"] = "MO", ["monza e della brianza"] = "MB", ["monza e brianza"] = "MB",
        ["monza brianza"] = "MB", ["monza"] = "MB", ["napoli"] = "NA", ["naples"] = "NA", ["novara"] = "NO", ["nuoro"] = "NU",
        ["oristano"] = "OR", ["padova"] = "PD", ["padua"] = "PD", ["palermo"] = "PA", ["parma"] = "PR", ["pavia"] = "PV",
        ["perugia"] = "PG", ["pesaro e urbino"] = "PU", ["pesaro-urbino"] = "PU", ["pesaro urbino"] = "PU", ["pesaro"] = "PU",
        ["pescara"] = "PE", ["piacenza"] = "PC", ["pisa"] = "PI", ["pistoia"] = "PT", ["pordenone"] = "PN", ["potenza"] = "PZ",
        ["prato"] = "PO", ["ragusa"] = "RG", ["ravenna"] = "RA", ["reggio calabria"] = "RC", ["reggio di calabria"] = "RC",
        ["reggio emilia"] = "RE", ["reggio nell'emilia"] = "RE", ["reggio nell emilia"] = "RE", ["rieti"] = "RI", ["rimini"] = "RN",
        ["roma"] = "RM", ["rome"] = "RM", ["rovigo"] = "RO", ["salerno"] = "SA", ["sassari"] = "SS", ["savona"] = "SV",
        ["siena"] = "SI", ["siracusa"] = "SR", ["syracuse"] = "SR", ["sondrio"] = "SO", ["sud sardegna"] = "SU", ["taranto"] = "TA",
        ["teramo"] = "TE", ["terni"] = "TR", ["torino"] = "TO", ["turin"] = "TO", ["trapani"] = "TP", ["trento"] = "TN",
        ["treviso"] = "TV", ["trieste"] = "TS", ["udine"] = "UD", ["varese"] = "VA", ["venezia"] = "VE", ["venice"] = "VE",
        ["verbano-cusio-ossola"] = "VB", ["verbano cusio ossola"] = "VB", ["verbania"] = "VB", ["vercelli"] = "VC", ["verona"] = "VR",
        ["vibo valentia"] = "VV", ["vicenza"] = "VI", ["viterbo"] = "VT",
    };

    private static readonly HashSet<string> Codes = new(ByName.Values, StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the two-letter code, or null if it cannot be resolved.</summary>
    public static string? ToCode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (s.Length == 2 && Codes.Contains(s)) return s.ToUpperInvariant();
        if (ByName.TryGetValue(s, out var code)) return code;
        var norm = RemoveDiacritics(s).Replace("provincia di ", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (ByName.TryGetValue(norm, out code)) return code;
        // "Roma (RM)" or "RM - Roma"
        var paren = System.Text.RegularExpressions.Regex.Match(s, @"\b([A-Za-z]{2})\b");
        while (paren.Success)
        {
            if (Codes.Contains(paren.Value)) return paren.Value.ToUpperInvariant();
            paren = paren.NextMatch();
        }
        return null;
    }

    private static string RemoveDiacritics(string text)
    {
        var formD = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in formD)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
