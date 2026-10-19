using System.Text.Json;
using System.Text.Json.Serialization;

namespace Paccofacile.Client;

/// <summary>One tracking event as pushed by Paccofacile (shipment.tracking.elenco.checkpoints[]).</summary>
public sealed class TrackingCheckpoint
{
    [JsonPropertyName("checkpoint_time")] public string? Time { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("city")] public string? City { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>What we keep for every Paccofacile order that pushed tracking to the webhook.</summary>
public sealed class TrackingRecord
{
    public long PaccofacileOrderId { get; set; }
    public string? ExternalOrderId { get; set; }
    public string? ExternalServiceName { get; set; }
    public long? ShipmentId { get; set; }
    public string? TrackingNumber { get; set; }
    public string? TrackingUrl { get; set; }
    public string? CarrierCode { get; set; }
    public string? ServiceCode { get; set; }
    public int? ServiceId { get; set; }
    public int? OrderStatusId { get; set; }
    public string? RecipientName { get; set; }
    public string? RecipientCity { get; set; }
    public List<TrackingCheckpoint> Checkpoints { get; set; } = new();
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset FirstReceivedAt { get; set; }
    public List<string> Waybills { get; set; } = new();
    /// <summary>Last raw payload, kept because the push format is only partially documented.</summary>
    public JsonElement? Raw { get; set; }

    public string? LastMessage => Checkpoints.LastOrDefault()?.Message;

    /// <summary>Builds a record from the body POSTed to endpoint_push_tracking (order_update).</summary>
    public static TrackingRecord Parse(JsonElement root)
    {
        var shipment = JsonHelpers.FindProperty(root, "shipment") ?? root;
        var rec = new TrackingRecord { Raw = root, ReceivedAt = DateTimeOffset.Now };

        rec.PaccofacileOrderId = ParseLong(Str(Prop(shipment, "order_id")))
            ?? ParseLong(Str(JsonHelpers.FindProperty(root, "order_id")))
            ?? 0;
        rec.ShipmentId = ParseLong(Str(Prop(shipment, "shipment_id") ?? JsonHelpers.FindProperty(root, "shipment_id")));
        rec.ExternalOrderId = Str(JsonHelpers.FindProperty(root, "external_order_id", "external_id", "order_reference", "riferimento"));
        rec.ExternalServiceName = Str(JsonHelpers.FindProperty(root, "external_service_name"));
        rec.CarrierCode = Str(Prop(shipment, "carrier_code") ?? JsonHelpers.FindProperty(root, "carrier_code", "carrier_name", "carrier"));

        var servizio = Prop(shipment, "servizio");
        if (servizio.HasValue)
        {
            rec.ServiceCode = Str(Prop(servizio.Value, "codice_servizio_corriere"));
            rec.ServiceId = (int?)ParseLong(Str(Prop(servizio.Value, "servizio_id")));
        }
        var order = Prop(shipment, "order");
        if (order.HasValue) rec.OrderStatusId = (int?)ParseLong(Str(Prop(order.Value, "order_status_id")));

        var recipient = Prop(shipment, "recipient");
        if (recipient.HasValue)
        {
            rec.RecipientName = Str(JsonHelpers.FindProperty(recipient.Value, "person_name", "name"));
            rec.RecipientCity = Str(JsonHelpers.FindProperty(recipient.Value, "city"));
        }

        var tracking = Prop(shipment, "tracking") ?? JsonHelpers.FindProperty(root, "tracking");
        if (tracking.HasValue)
        {
            rec.TrackingNumber = Str(JsonHelpers.FindProperty(tracking.Value, "tracking_number", "tracking_code", "numero_tracking", "numero_spedizione", "ldv", "waybill_number", "codice"));
            rec.TrackingUrl = Str(JsonHelpers.FindProperty(tracking.Value, "tracking_url", "url"));
            var checkpoints = JsonHelpers.FindProperty(tracking.Value, "checkpoints");
            if (checkpoints is { ValueKind: JsonValueKind.Array })
            {
                try { rec.Checkpoints = checkpoints.Value.Deserialize<List<TrackingCheckpoint>>(JsonHelpers.Options) ?? new(); }
                catch (JsonException) { }
            }
        }
        if (string.IsNullOrWhiteSpace(rec.TrackingNumber))
        {
            // shipment responses expose "tracking_numbers": [] and parcels[].tracking_number
            var arr = JsonHelpers.FindProperty(root, "tracking_numbers");
            if (arr is { ValueKind: JsonValueKind.Array })
                foreach (var item in arr.Value.EnumerateArray())
                {
                    var s = item.ValueKind == JsonValueKind.Object ? Str(JsonHelpers.FindProperty(item, "tracking_number", "number", "code")) : Str(item);
                    if (!string.IsNullOrWhiteSpace(s)) { rec.TrackingNumber = s; break; }
                }
        }
        if (string.IsNullOrWhiteSpace(rec.TrackingNumber))
        {
            var parcels = JsonHelpers.FindProperty(root, "parcels");
            if (parcels is { ValueKind: JsonValueKind.Array })
                foreach (var p in parcels.Value.EnumerateArray())
                {
                    var s = Str(JsonHelpers.FindProperty(p, "tracking_number"));
                    if (!string.IsNullOrWhiteSpace(s)) { rec.TrackingNumber = s; break; }
                }
        }
        if (string.IsNullOrWhiteSpace(rec.TrackingNumber))
            rec.TrackingNumber = Str(JsonHelpers.FindProperty(root, "tracking_number", "tracking_code", "numero_tracking", "ldv", "waybill_number"));
        if (string.IsNullOrWhiteSpace(rec.TrackingNumber)) rec.TrackingNumber = null;
        rec.TrackingUrl ??= Str(JsonHelpers.FindProperty(root, "tracking_url"));
        return rec;
    }

    private static JsonElement? Prop(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? v : null;
    private static string? Str(JsonElement? el) => JsonHelpers.AsString(el);
    private static long? ParseLong(string? s) => long.TryParse(s, out var v) ? v : null;
}

/// <summary>
/// File based store shared by the webhook (writer) and the CLI (reader):
///   {DataDir}/tracking/{paccofacile_order_id}.json
///   {DataDir}/documents/{paccofacile_order_id}_{id_waybill}.pdf
///   {DataDir}/raw/{timestamp}_{kind}.json   (every push, verbatim)
/// </summary>
public sealed class TrackingStore
{
    private static readonly JsonSerializerOptions Json = new(JsonHelpers.Pretty) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly object _lock = new();

    public string DataDir { get; }
    public string TrackingDir => Path.Combine(DataDir, "tracking");
    public string DocumentsDir => Path.Combine(DataDir, "documents");
    public string RawDir => Path.Combine(DataDir, "raw");

    public TrackingStore(string dataDir)
    {
        DataDir = Path.GetFullPath(dataDir);
        Directory.CreateDirectory(TrackingDir);
        Directory.CreateDirectory(DocumentsDir);
        Directory.CreateDirectory(RawDir);
    }

    public string SaveRaw(string kind, string body)
    {
        var path = Path.Combine(RawDir, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}_{kind}.json");
        File.WriteAllText(path, body);
        return path;
    }

    /// <summary>Merges a fresh push into the stored record (keeps first-seen time and waybill list).</summary>
    public TrackingRecord Upsert(TrackingRecord incoming)
    {
        lock (_lock)
        {
            var existing = Get(incoming.PaccofacileOrderId);
            if (existing is not null)
            {
                incoming.FirstReceivedAt = existing.FirstReceivedAt;
                incoming.Waybills = existing.Waybills;
                incoming.TrackingNumber ??= existing.TrackingNumber;
                incoming.TrackingUrl ??= existing.TrackingUrl;
                incoming.ExternalOrderId ??= existing.ExternalOrderId;
                incoming.ShipmentId ??= existing.ShipmentId;
                if (incoming.Checkpoints.Count < existing.Checkpoints.Count) incoming.Checkpoints = existing.Checkpoints;
            }
            else incoming.FirstReceivedAt = incoming.ReceivedAt;
            Write(incoming);
            return incoming;
        }
    }

    public string SaveWaybill(long paccofacileOrderId, string? idWaybill, string dataUrlOrBase64)
    {
        var comma = dataUrlOrBase64.IndexOf(',');
        var b64 = dataUrlOrBase64.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0 ? dataUrlOrBase64[(comma + 1)..] : dataUrlOrBase64;
        var bytes = Convert.FromBase64String(b64.Trim());
        var safeId = string.IsNullOrWhiteSpace(idWaybill) ? DateTime.Now.ToString("yyyyMMddHHmmss") : new string(idWaybill.Where(char.IsLetterOrDigit).ToArray());
        var path = Path.Combine(DocumentsDir, $"{paccofacileOrderId}_{safeId}.pdf");
        File.WriteAllBytes(path, bytes);

        lock (_lock)
        {
            var rec = Get(paccofacileOrderId) ?? new TrackingRecord { PaccofacileOrderId = paccofacileOrderId, ReceivedAt = DateTimeOffset.Now, FirstReceivedAt = DateTimeOffset.Now };
            if (!rec.Waybills.Contains(path)) rec.Waybills.Add(path);
            Write(rec);
        }
        return path;
    }

    public TrackingRecord? Get(long paccofacileOrderId)
    {
        var path = PathFor(paccofacileOrderId);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<TrackingRecord>(File.ReadAllText(path), Json); }
        catch (JsonException) { return null; }
    }

    public TrackingRecord? GetByExternalOrderId(string externalOrderId)
        => All().FirstOrDefault(r => string.Equals(r.ExternalOrderId, externalOrderId, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<TrackingRecord> All()
    {
        foreach (var f in Directory.EnumerateFiles(TrackingDir, "*.json").OrderByDescending(File.GetLastWriteTimeUtc))
        {
            TrackingRecord? r = null;
            try { r = JsonSerializer.Deserialize<TrackingRecord>(File.ReadAllText(f), Json); } catch (JsonException) { }
            if (r is not null) yield return r;
        }
    }

    private string PathFor(long id) => Path.Combine(TrackingDir, $"{id}.json");
    private void Write(TrackingRecord r) => File.WriteAllText(PathFor(r.PaccofacileOrderId), JsonSerializer.Serialize(r, Json));
}

/// <summary>Builds the URLs registered with key_validation; {order_id} is substituted by Paccofacile when pushing documents.</summary>
public static class WebhookUrls
{
    public static string Tracking(string publicBaseUrl, string secret) => $"{publicBaseUrl.TrimEnd('/')}/hooks/{secret}/order_update";
    public static string Documents(string publicBaseUrl, string secret) => $"{publicBaseUrl.TrimEnd('/')}/hooks/{secret}/order_documents/{{order_id}}";

    public static KeyValidationRequest RegistrationRequest(string publicBaseUrl, string secret) => new()
    {
        Domain = publicBaseUrl.TrimEnd('/'),
        ApiVersion = "v1",
        EndpointPushTracking = Tracking(publicBaseUrl, secret),
        EndpointPushDocuments = Documents(publicBaseUrl, secret),
    };
}
