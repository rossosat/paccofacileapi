using System.Globalization;
using System.Text;
using System.Text.Json;
using Paccofacile.Client;

namespace Paccofacile.Cli;

public sealed class ShipmentResult
{
    public required OutboundOrder Order { get; init; }
    public bool Success { get; set; }
    public string? Error { get; set; }
    public ServiceAvailable? Service { get; set; }
    public List<Parcel> Parcels { get; set; } = new();
    public long? ShipmentId { get; set; }
    public long? PaccofacileOrderId { get; set; }
    public bool Paid { get; set; }
    public string? TrackingNumber { get; set; }
    public decimal? Price { get; set; }
}

public sealed class WorkflowOptions
{
    public int? ServiceId { get; set; }
    public bool Buy { get; set; }
    public bool DryRun { get; set; }
    public List<Parcel>? ParcelOverride { get; set; }
    public bool Verbose { get; set; }
}

/// <summary>quote -> pick service -> shipment/save -> (shipment/buy) -> best-effort tracking lookup.</summary>
public sealed class ShipmentWorkflow
{
    private readonly PaccofacileClient _api;
    private readonly AppConfig _cfg;
    private readonly TextWriter _out;
    private readonly LocalityResolver _localities;

    public ShipmentWorkflow(PaccofacileClient api, AppConfig cfg, TextWriter output)
    {
        _api = api;
        _cfg = cfg;
        _out = output;
        _localities = new LocalityResolver(api);
    }

    public LocalityResolver Localities => _localities;

    public List<Parcel> BuildParcels(OutboundOrder order, WorkflowOptions opt)
    {
        if (opt.ParcelOverride is { Count: > 0 }) return opt.ParcelOverride.Select(Clone).ToList();
        if (order.ExplicitParcels is { Count: > 0 }) return order.ExplicitParcels.Select(Clone).ToList();

        var parcels = new List<Parcel>();
        foreach (var line in order.Lines)
        {
            if (!string.IsNullOrWhiteSpace(line.Sku) && _cfg.TryGetSkuParcel(line.Sku, out var p))
                for (var i = 0; i < Math.Max(1, line.Quantity); i++) parcels.Add(Clone(p));
            else
                order.Warnings.Add($"SKU '{line.Sku}' non in SkuParcels: uso DefaultParcel {_cfg.DefaultParcelObj}");
        }
        if (parcels.Count == 0) parcels.Add(_cfg.DefaultParcelObj);
        return parcels;
    }

    private static Parcel Clone(Parcel p) => new() { ShipmentType = p.ShipmentType, Dim1 = p.Dim1, Dim2 = p.Dim2, Dim3 = p.Dim3, Weight = p.Weight };

    public async Task<QuoteData> QuoteAsync(QuoteAddress destination, List<Parcel> parcels, int? serviceId, CancellationToken ct)
    {
        var req = new QuoteRequest
        {
            ShipmentService = new QuoteShipmentService { Parcels = parcels, ServiceId = serviceId },
            Pickup = _cfg.SenderAddress.ToQuoteAddress(),
            Destination = destination,
        };
        var res = await _api.QuoteAsync(req, ct);
        return res.Data ?? new QuoteData();
    }

    public async Task<ShipmentResult> ProcessAsync(OutboundOrder order, WorkflowOptions opt, CancellationToken ct = default)
    {
        var result = new ShipmentResult { Order = order };
        try
        {
            var parcels = BuildParcels(order, opt);
            result.Parcels = parcels;
            var serviceId = opt.ServiceId;   // explicit --service: quote only that one, ignore filters

            var (okLoc, locError) = await _localities.ResolveAsync(order.Destination, order.Warnings, ct);
            if (!okLoc) throw new InvalidOperationException(locError ?? "destinazione non valida");

            var quote = await QuoteAsync(order.Destination.ToQuoteAddress(), parcels, serviceId, ct);
            var candidates = quote.ServicesAvailable
                .Where(s => s.PriceTotal?.Amount is not null)
                .OrderBy(s => s.PriceTotal!.Amount)
                .ToList();
            candidates = serviceId.HasValue
                ? candidates.Where(s => s.ServiceId == serviceId.Value).ToList()
                : candidates.Where(_cfg.IsEligible).ToList();
            if (candidates.Count == 0)
                throw new InvalidOperationException(serviceId.HasValue
                    ? $"il servizio {serviceId} non è disponibile per questa destinazione"
                    : quote.ServicesAvailable.Count > 0
                        ? "nessun servizio a domicilio disponibile (solo punti di ritiro): usa --service ID o HomeDeliveryOnly=false"
                        : "nessun servizio disponibile per questa destinazione");

            // preference order (e.g. SDA first), then cheapest eligible
            ServiceAvailable? chosen = null;
            if (!serviceId.HasValue)
                foreach (var pref in _cfg.PreferenceOrder)
                {
                    chosen = candidates.FirstOrDefault(s => s.ServiceId == pref);
                    if (chosen is not null) break;
                }
            if (chosen is null)
            {
                chosen = candidates[0];
                if (!serviceId.HasValue && _cfg.PreferenceOrder.Any())
                    order.Warnings.Add($"servizio preferito ({string.Join(",", _cfg.PreferenceOrder)}) non disponibile per questa destinazione: uso il più economico a domicilio");
            }
            result.Service = chosen;
            result.Price = chosen.PriceTotal?.Amount;

            _out.WriteLine($"  servizio: {chosen.DisplayName} (id {chosen.ServiceId}) {Fmt(chosen.PriceTotal?.Amount)} EUR, ritiro {chosen.PickupDate?.FirstDate} {chosen.PickupDate?.FirstDateRange}");
            if (opt.DryRun) { result.Success = true; return result; }

            var save = new SaveShipmentRequest
            {
                ExternalOrderId = order.OrderId,
                ExternalServiceName = order.Source == "amazon" ? "amazon" : _cfg.ExternalServiceName,
                ShipmentService = new SaveShipmentService
                {
                    ShipmentType = parcels[0].ShipmentType,
                    PickupDate = chosen.PickupDate?.FirstDate,
                    PickupRange = chosen.PickupDate?.FirstDateRange,
                    ServiceId = chosen.ServiceId,
                    Parcels = parcels,
                },
                Pickup = _cfg.SenderAddress,
                Destination = order.Destination,
            };
            if (chosen.CustomsRequired != 0)
            {
                var value = (order.GoodsValue ?? 0m).ToString("0.00", CultureInfo.InvariantCulture);
                save.Customs = new Customs
                {
                    Amount = new Money { Value = value },
                    Articles = order.Lines.Select(l => new CustomsArticle
                    {
                        Amount = new Money { Value = ((l.UnitPrice ?? 0m) * l.Quantity).ToString("0.00", CultureInfo.InvariantCulture) },
                        Quantity = l.Quantity,
                        Weight = parcels.Sum(p => p.Weight) / Math.Max(1, order.Lines.Count),
                        Description = string.IsNullOrWhiteSpace(l.Description) ? "Merce" : Truncate(l.Description, 60),
                    }).ToList(),
                };
            }

            var saved = await _api.SaveShipmentAsync(save, ct);
            var shipmentId = saved.Data?.Shipment?.ShipmentId
                ?? ParseLong(JsonHelpers.AsString(JsonHelpers.FindProperty(saved.Raw, "shipment_id")))
                ?? throw new InvalidOperationException("shipment/save non ha restituito shipment_id: " + saved.Raw.GetRawText());
            result.ShipmentId = shipmentId;
            _out.WriteLine($"  spedizione creata: shipment_id {shipmentId}");

            if (opt.Buy)
            {
                var buy = await _api.BuyAsync(shipmentId, ct);
                result.PaccofacileOrderId = buy.Data?.Order?.OrderId
                    ?? ParseLong(JsonHelpers.AsString(JsonHelpers.FindProperty(buy.Raw, "order_id")));
                result.Paid = result.PaccofacileOrderId.HasValue;
                result.TrackingNumber = ExtractTracking(buy.Raw);
                _out.WriteLine($"  pagata: ordine Paccofacile {result.PaccofacileOrderId?.ToString() ?? "?"}");

                if (string.IsNullOrWhiteSpace(result.TrackingNumber))
                    result.TrackingNumber = await TryFetchTrackingAsync(shipmentId, result.PaccofacileOrderId, opt.Verbose, ct);
                if (!string.IsNullOrWhiteSpace(result.TrackingNumber))
                    _out.WriteLine($"  tracking: {result.TrackingNumber}");
                else
                    _out.WriteLine("  tracking: non ancora disponibile (arriva dal corriere; vedi area riservata o webhook)");
            }

            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            _out.WriteLine($"  ERRORE: {ex.Message}");
        }

        AppendLog(result);
        return result;
    }

    /// <summary>The official plugin only receives tracking by push; we probe a few plausible read endpoints and give up silently.</summary>
    private async Task<string?> TryFetchTrackingAsync(long shipmentId, long? orderId, bool verbose, CancellationToken ct)
    {
        var endpoints = new List<string> { $"shipment/{shipmentId}", $"shipments/{shipmentId}", $"shipment/tracking/{shipmentId}" };
        if (orderId.HasValue) endpoints.AddRange(new[] { $"orders/{orderId}", $"order/{orderId}", $"shipment/order/{orderId}" });
        foreach (var ep in endpoints)
        {
            try
            {
                var res = await _api.RawAsync(HttpMethod.Get, ep, null, throwOnError: false, ct);
                if (verbose) _out.WriteLine($"  [probe GET {ep}] {res.StatusCode}");
                if (res.StatusCode is < 200 or >= 300) continue;
                var tn = JsonHelpers.AsString(JsonHelpers.FindProperty(res.Raw, "tracking_number", "tracking_code", "numero_tracking", "waybill_number", "ldv", "tracking"));
                if (!string.IsNullOrWhiteSpace(tn) && !tn.StartsWith('{') && !tn.StartsWith('[')) return tn;
            }
            catch (Exception ex) when (verbose)
            {
                _out.WriteLine($"  [probe GET {ep}] {ex.Message}");
            }
        }
        return null;
    }

    private void AppendLog(ShipmentResult r)
    {
        try
        {
            var path = Path.IsPathRooted(_cfg.ShipmentLog) ? _cfg.ShipmentLog : Path.Combine(Environment.CurrentDirectory, _cfg.ShipmentLog);
            var isNew = !File.Exists(path);
            using var w = new StreamWriter(path, append: true, Encoding.UTF8);
            if (isNew) w.WriteLine("timestamp;source;order_id;recipient;city;service_id;service;price;parcels;shipment_id;paccofacile_order_id;paid;tracking;status;error");
            w.WriteLine(string.Join(';', new[]
            {
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), r.Order.Source, r.Order.OrderId, r.Order.Destination.HeaderName, r.Order.Destination.City,
                r.Service?.ServiceId.ToString() ?? "", r.Service?.DisplayName ?? "", Fmt(r.Price), string.Join("|", r.Parcels.Select(p => p.ToString())),
                r.ShipmentId?.ToString() ?? "", r.PaccofacileOrderId?.ToString() ?? "", r.Paid ? "1" : "0", r.TrackingNumber ?? "",
                r.Success ? "OK" : "KO", r.Error ?? "",
            }.Select(v => DelimitedFile.Escape(v, ';'))));
        }
        catch (Exception ex)
        {
            _out.WriteLine($"  (log non scritto: {ex.Message})");
        }
    }

    /// <summary>shipment/save and buy answers carry "tracking_numbers": [] and parcels[].tracking_number (empty until the label is issued).</summary>
    private static string? ExtractTracking(JsonElement root)
    {
        var arr = JsonHelpers.FindProperty(root, "tracking_numbers");
        if (arr is { ValueKind: JsonValueKind.Array })
            foreach (var item in arr.Value.EnumerateArray())
            {
                var s = item.ValueKind == JsonValueKind.Object ? JsonHelpers.AsString(JsonHelpers.FindProperty(item, "tracking_number", "number", "code")) : JsonHelpers.AsString(item);
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        var tn = JsonHelpers.AsString(JsonHelpers.FindProperty(root, "tracking_number", "tracking_code", "waybill_number", "ldv"));
        return string.IsNullOrWhiteSpace(tn) ? null : tn;
    }

    private static string Fmt(decimal? d) => d.HasValue ? d.Value.ToString("0.00", CultureInfo.InvariantCulture) : "";
    private static long? ParseLong(string? s) => long.TryParse(s, out var v) ? v : null;
    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];
}
