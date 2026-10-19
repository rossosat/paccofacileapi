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

    /// <summary>Your warehouse / pickup address. Required for quotes and shipments.</summary>
    public SenderConfig Sender { get; set; } = new();

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
    [JsonIgnore] public ShipmentAddress SenderAddress => Sender.ToAddress();
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
        var p = new List<string>();
        if (string.IsNullOrWhiteSpace(Sender.HeaderName)) p.Add("Sender.HeaderName (ragione sociale / nome mittente)");
        if (string.IsNullOrWhiteSpace(Sender.Address)) p.Add("Sender.Address (via)");
        if (string.IsNullOrWhiteSpace(Sender.BuildingNumber)) p.Add("Sender.BuildingNumber (numero civico)");
        if (string.IsNullOrWhiteSpace(Sender.PostalCode)) p.Add("Sender.PostalCode (CAP)");
        if (string.IsNullOrWhiteSpace(Sender.City)) p.Add("Sender.City");
        if (string.IsNullOrWhiteSpace(Sender.StateOrProvinceCode)) p.Add("Sender.StateOrProvinceCode (sigla provincia)");
        if (string.IsNullOrWhiteSpace(Sender.Phone)) p.Add("Sender.Phone");
        if (string.IsNullOrWhiteSpace(Sender.Email)) p.Add("Sender.Email");
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
