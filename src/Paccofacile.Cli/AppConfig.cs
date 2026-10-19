using System.Text.Json;
using System.Text.Json.Serialization;
using Paccofacile.Client;

namespace Paccofacile.Cli;

/// <summary>Friendly-named sender address (appsettings) mapped onto the API's snake_case <see cref="ShipmentAddress"/>.</summary>
public sealed class SenderConfig
{
    public string HeaderName { get; set; } = "";
    public string Address { get; set; } = "";
    public string BuildingNumber { get; set; } = "";
    public string PostalCode { get; set; } = "";
    public string City { get; set; } = "";
    public string StateOrProvinceCode { get; set; } = "";
    public string IsoCode { get; set; } = "IT";
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    public string Note { get; set; } = "";
    public string LockerId { get; set; } = "";

    public ShipmentAddress ToAddress() => new()
    {
        HeaderName = HeaderName, Address = Address, BuildingNumber = BuildingNumber, PostalCode = PostalCode, City = City,
        StateOrProvinceCode = StateOrProvinceCode, IsoCode = string.IsNullOrWhiteSpace(IsoCode) ? "IT" : IsoCode,
        Phone = Phone, Email = Email, Note = Note, LockerId = LockerId,
    };
}

/// <summary>Friendly-named parcel (appsettings): cm and kg. ShipmentType 1 pacco, 2 busta, 5 pallet.</summary>
public sealed class ParcelConfig
{
    public int ShipmentType { get; set; } = 1;
    public decimal Dim1 { get; set; }
    public decimal Dim2 { get; set; }
    public decimal Dim3 { get; set; }
    public decimal Weight { get; set; }

    public Parcel ToParcel() => new() { ShipmentType = ShipmentType, Dim1 = Dim1, Dim2 = Dim2, Dim3 = Dim3, Weight = Weight };
}

/// <summary>
/// Configuration = appsettings.json + appsettings.Local.json (git-ignored, holds the credentials)
/// + environment variables PACCOFACILE_MODE / PACCOFACILE_TOKEN / PACCOFACILE_API_KEY / PACCOFACILE_ACCOUNT_NUMBER.
/// </summary>
public sealed class AppConfig
{
    private static readonly JsonSerializerOptions ConfigJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public PaccofacileOptions Paccofacile { get; set; } = new();

    /// <summary>Single sender (legacy). When <see cref="Senders"/> is filled this is ignored.</summary>
    public SenderConfig Sender { get; set; } = new();

    /// <summary>Named sender profiles, e.g. "bsplanet", "itsnc". Chosen with --sender NAME, default = <see cref="DefaultSender"/>.</summary>
    public Dictionary<string, SenderConfig> Senders { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Key of <see cref="Senders"/> used when --sender is not given.</summary>
    public string DefaultSender { get; set; } = "";

    [JsonIgnore] public string ActiveSenderName { get; private set; } = "";

    /// <summary>Picks the sender profile for this run. Accepts the key or a prefix/substring of the key or of the header name.</summary>
    public void SelectSender(string? requested)
    {
        if (Senders.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(requested)) throw new ArgumentException("nessun profilo mittente in Senders: impossibile usare --sender");
            ActiveSenderName = "";
            return;
        }
        var name = string.IsNullOrWhiteSpace(requested) ? DefaultSender : requested.Trim();
        if (string.IsNullOrWhiteSpace(name)) name = Senders.Keys.First();
        if (Senders.ContainsKey(name)) { ActiveSenderName = Senders.Keys.First(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)); return; }

        var match = Senders.Keys.Where(k => k.Contains(name, StringComparison.OrdinalIgnoreCase)
                                         || Senders[k].HeaderName.Contains(name, StringComparison.OrdinalIgnoreCase)
                                         || Senders[k].Email.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (match.Count == 1) { ActiveSenderName = match[0]; return; }
        throw new ArgumentException($"mittente '{name}' {(match.Count == 0 ? "non trovato" : "ambiguo")}; disponibili: {string.Join(", ", Senders.Select(kv => $"{kv.Key} ({kv.Value.HeaderName})"))}");
    }

    public SenderConfig ActiveSender => Senders.Count > 0 ? Senders[string.IsNullOrWhiteSpace(ActiveSenderName) ? (Senders.ContainsKey(DefaultSender) ? DefaultSender : Senders.Keys.First()) : ActiveSenderName] : Sender;

    /// <summary>Parcel used when the order gives no dimensions and the SKU is not in SkuParcels.</summary>
    public ParcelConfig DefaultParcel { get; set; } = new() { Dim1 = 30, Dim2 = 20, Dim3 = 15, Weight = 1 };

    /// <summary>Per-SKU parcel (one parcel per unit). Key = SKU as it appears in the marketplace export.</summary>
    public Dictionary<string, ParcelConfig> SkuParcels { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Legacy single preference: same as PreferredServiceIds with one entry.</summary>
    public int? PreferredServiceId { get; set; }

    /// <summary>Services to prefer, in order. The first one available for the destination wins; if none is, the cheapest home delivery is used.</summary>
    public List<int> PreferredServiceIds { get; set; } = new();

    public IEnumerable<int> PreferenceOrder => PreferredServiceId.HasValue ? new[] { PreferredServiceId.Value }.Concat(PreferredServiceIds).Distinct() : PreferredServiceIds;

    /// <summary>When true (default) services that deliver to a pickup point / locker ("POINT", "LOCKER") are skipped in the automatic choice.</summary>
    public bool HomeDeliveryOnly { get; set; } = true;

    /// <summary>service_ids never to be chosen automatically (e.g. pallet services).</summary>
    public List<int> ExcludedServiceIds { get; set; } = new();

    /// <summary>True when the service is a home delivery (not point/locker based).</summary>
    public static bool IsHomeDelivery(ServiceAvailable s)
    {
        var n = (s.Name ?? "").ToUpperInvariant();
        return !n.Contains("POINT") && !n.Contains("LOCKER") && !n.Contains("FERMOPOSTA");
    }

    public bool IsEligible(ServiceAvailable s)
    {
        if (ExcludedServiceIds.Contains(s.ServiceId)) return false;
        if (HomeDeliveryOnly && !IsHomeDelivery(s)) return false;
        return true;
    }

    /// <summary>Value sent as external_service_name for non-Amazon orders.</summary>
    public string ExternalServiceName { get; set; } = "paccofacile-cli";

    /// <summary>CSV log where every created shipment is appended.</summary>
    public string ShipmentLog { get; set; } = "shipments-log.csv";

    /// <summary>Settings shared with the Paccofacile.Webhook service (same Secret and DataDir).</summary>
    public WebhookConfig Webhook { get; set; } = new();

    public string ResolvedWebhookDataDir => Path.IsPathRooted(Webhook.DataDir) ? Webhook.DataDir : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, Webhook.DataDir));

    // ---- derived API objects ----
    [JsonIgnore] public ShipmentAddress SenderAddress => ActiveSender.ToAddress();
    [JsonIgnore] public Parcel DefaultParcelObj => DefaultParcel.ToParcel();
    public bool TryGetSkuParcel(string sku, out Parcel parcel)
    {
        if (!string.IsNullOrWhiteSpace(sku) && SkuParcels.TryGetValue(sku, out var cfg)) { parcel = cfg.ToParcel(); return true; }
        parcel = null!;
        return false;
    }

    public static AppConfig Load(string baseDir)
    {
        var cfg = new AppConfig();
        foreach (var name in new[] { "appsettings.json", "appsettings.Local.json" })
        {
            var path = Path.Combine(baseDir, name);
            if (!File.Exists(path)) continue;
            var json = File.ReadAllText(path);
            var layer = JsonSerializer.Deserialize<AppConfig>(json, ConfigJson);
            if (layer is null) continue;
            cfg.Merge(layer, json);
        }

        Env("PACCOFACILE_MODE", v => cfg.Paccofacile.Mode = v);
        Env("PACCOFACILE_TOKEN", v => cfg.Paccofacile.Token = v);
        Env("PACCOFACILE_API_KEY", v => cfg.Paccofacile.ApiKey = v);
        Env("PACCOFACILE_ACCOUNT_NUMBER", v => cfg.Paccofacile.AccountNumber = v);
        return cfg;
    }

    private static void Env(string name, Action<string> set)
    {
        var v = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(v)) set(v.Trim());
    }

    /// <summary>Overlay the sections present in <paramref name="rawJson"/> on top of this instance.</summary>
    private void Merge(AppConfig layer, string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;
        bool Has(string name) => root.EnumerateObject().Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        if (Has("Paccofacile"))
        {
            if (!string.IsNullOrWhiteSpace(layer.Paccofacile.Mode)) Paccofacile.Mode = layer.Paccofacile.Mode;
            if (!string.IsNullOrWhiteSpace(layer.Paccofacile.Token)) Paccofacile.Token = layer.Paccofacile.Token;
            if (!string.IsNullOrWhiteSpace(layer.Paccofacile.ApiKey)) Paccofacile.ApiKey = layer.Paccofacile.ApiKey;
            if (!string.IsNullOrWhiteSpace(layer.Paccofacile.AccountNumber)) Paccofacile.AccountNumber = layer.Paccofacile.AccountNumber;
        }
        if (Has("Sender")) Sender = layer.Sender;
        if (Has("Senders")) Senders = new Dictionary<string, SenderConfig>(layer.Senders, StringComparer.OrdinalIgnoreCase);   // a layer replaces the whole list
        if (Has("DefaultSender")) DefaultSender = layer.DefaultSender;
        if (Has("DefaultParcel")) DefaultParcel = layer.DefaultParcel;
        if (Has("SkuParcels"))
            foreach (var kv in layer.SkuParcels) SkuParcels[kv.Key] = kv.Value;
        if (Has("PreferredServiceId")) PreferredServiceId = layer.PreferredServiceId;
        if (Has("PreferredServiceIds")) PreferredServiceIds = layer.PreferredServiceIds;
        if (Has("HomeDeliveryOnly")) HomeDeliveryOnly = layer.HomeDeliveryOnly;
        if (Has("ExcludedServiceIds")) ExcludedServiceIds = layer.ExcludedServiceIds;
        if (Has("ExternalServiceName")) ExternalServiceName = layer.ExternalServiceName;
        if (Has("ShipmentLog")) ShipmentLog = layer.ShipmentLog;
        if (Has("Webhook"))
        {
            if (!string.IsNullOrWhiteSpace(layer.Webhook.Secret)) Webhook.Secret = layer.Webhook.Secret;
            if (!string.IsNullOrWhiteSpace(layer.Webhook.DataDir)) Webhook.DataDir = layer.Webhook.DataDir;
            if (!string.IsNullOrWhiteSpace(layer.Webhook.PublicBaseUrl)) Webhook.PublicBaseUrl = layer.Webhook.PublicBaseUrl;
        }
    }

    public List<string> SenderProblems()
    {
        var s = ActiveSender;
        var p = new List<string>();
        if (string.IsNullOrWhiteSpace(s.HeaderName)) p.Add("HeaderName (ragione sociale / nome mittente)");
        if (string.IsNullOrWhiteSpace(s.Address)) p.Add("Address (via)");
        if (string.IsNullOrWhiteSpace(s.BuildingNumber)) p.Add("BuildingNumber (numero civico)");
        if (string.IsNullOrWhiteSpace(s.PostalCode)) p.Add("PostalCode (CAP)");
        if (string.IsNullOrWhiteSpace(s.City)) p.Add("City");
        if (string.IsNullOrWhiteSpace(s.StateOrProvinceCode)) p.Add("StateOrProvinceCode (sigla provincia)");
        if (string.IsNullOrWhiteSpace(s.Phone)) p.Add("Phone");
        if (string.IsNullOrWhiteSpace(s.Email)) p.Add("Email");
        return p;
    }
}

public sealed class WebhookConfig
{
    /// <summary>Must match Webhook:Secret of the Paccofacile.Webhook service.</summary>
    public string Secret { get; set; } = "";
    /// <summary>Folder written by the webhook (tracking/, documents/, raw/). Relative paths resolve from the CLI folder.</summary>
    public string DataDir { get; set; } = "../../../../Paccofacile.Webhook/data";
    /// <summary>Public https base URL of the webhook, e.g. https://hooks.miodominio.it</summary>
    public string PublicBaseUrl { get; set; } = "";
}
