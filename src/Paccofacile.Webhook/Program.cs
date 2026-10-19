using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Paccofacile.Client;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables("PACCOFACILE_");

var hook = builder.Configuration.GetSection("Webhook").Get<WebhookSettings>() ?? new WebhookSettings();
if (string.IsNullOrWhiteSpace(hook.Secret))
    throw new InvalidOperationException("Webhook:Secret mancante in appsettings.json (una stringa casuale lunga, uguale a quella nella CLI).");

// relative DataDir = next to the project (dotnet run) or next to the published exe
var store = new TrackingStore(Path.IsPathRooted(hook.DataDir) ? hook.DataDir : Path.Combine(builder.Environment.ContentRootPath, hook.DataDir));
builder.Services.AddSingleton(store);
builder.Services.AddSingleton(hook);

var app = builder.Build();
var log = app.Logger;
log.LogInformation("Dati tracking in {dir}", store.DataDir);

static IResult Success(object? extra = null) => Results.Json(new { status = "SUCCESS", data = extra });

bool CheckSecret(string secret) => string.Equals(secret, hook.Secret, StringComparison.Ordinal);

app.MapGet("/", () => Results.Text("Paccofacile webhook attivo. Endpoint: POST /hooks/{secret}/order_update, POST /hooks/{secret}/order_documents/{order_id}"));

// ---- tracking push (registered as endpoint_push_tracking) ----
app.MapPost("/hooks/{secret}/order_update", async (string secret, HttpRequest req) =>
{
    if (!CheckSecret(secret)) return Results.NotFound();
    var body = await ReadBodyAsync(req);
    store.SaveRaw("order_update", body);

    JsonElement root;
    try { root = JsonDocument.Parse(body).RootElement.Clone(); }
    catch (JsonException ex)
    {
        log.LogWarning("order_update: body non JSON ({err})", ex.Message);
        return Results.BadRequest(new { status = "FAILURE", message = "invalid json" });
    }

    var rec = TrackingRecord.Parse(root);
    if (rec.PaccofacileOrderId == 0)
    {
        log.LogWarning("order_update senza order_id: salvato solo in raw");
        return Success(new { stored = false });
    }
    rec = store.Upsert(rec);
    log.LogInformation("tracking ordine {id} (ext {ext}) tracking {tn}: {msg}", rec.PaccofacileOrderId, rec.ExternalOrderId, rec.TrackingNumber, rec.LastMessage);
    return Success(new { order_id = rec.PaccofacileOrderId, tracking_number = rec.TrackingNumber, checkpoints = rec.Checkpoints.Count });
});

// ---- documents push (registered as endpoint_push_documents/{order_id}) ----
app.MapPost("/hooks/{secret}/order_documents/{orderId}", async (string secret, string orderId, HttpRequest req) =>
{
    if (!CheckSecret(secret)) return Results.NotFound();
    var body = await ReadBodyAsync(req);
    store.SaveRaw($"order_documents_{Sanitize(orderId)}", body);

    JsonElement root;
    try { root = JsonDocument.Parse(body).RootElement.Clone(); }
    catch (JsonException) { return Results.BadRequest(new { status = "FAILURE", message = "invalid json" }); }

    var id = long.TryParse(orderId, out var o) ? o
           : long.TryParse(JsonHelpers.AsString(JsonHelpers.FindProperty(root, "order_id")), out o) ? o : 0;
    var idWaybill = JsonHelpers.AsString(JsonHelpers.FindProperty(root, "id_waybill"));
    var waybill = JsonHelpers.AsString(JsonHelpers.FindProperty(root, "waybill", "pdf", "document", "file"));

    if (id == 0 || string.IsNullOrWhiteSpace(waybill))
    {
        log.LogWarning("order_documents {orderId}: manca order_id o waybill", orderId);
        return Success(new { stored = false });
    }
    try
    {
        var path = store.SaveWaybill(id, idWaybill, waybill);
        log.LogInformation("LDV ordine {id} salvata in {path}", id, path);
        return Success(new { order_id = id, id_waybill = idWaybill, file = Path.GetFileName(path) });
    }
    catch (FormatException)
    {
        log.LogWarning("order_documents {orderId}: waybill non è base64", orderId);
        return Results.BadRequest(new { status = "FAILURE", message = "waybill not base64" });
    }
});

// ---- read side (same secret) ----
app.MapGet("/hooks/{secret}/tracking", (string secret) =>
    CheckSecret(secret) ? Results.Json(store.All().Select(Summary), JsonHelpers.Pretty) : Results.NotFound());

app.MapGet("/hooks/{secret}/tracking/{orderId:long}", (string secret, long orderId) =>
{
    if (!CheckSecret(secret)) return Results.NotFound();
    var r = store.Get(orderId);
    return r is null ? Results.NotFound() : Results.Json(r, JsonHelpers.Pretty);
});

app.MapGet("/hooks/{secret}/documents/{orderId:long}/{file}", (string secret, long orderId, string file) =>
{
    if (!CheckSecret(secret)) return Results.NotFound();
    var path = Path.Combine(store.DocumentsDir, Path.GetFileName(file));
    return File.Exists(path) && Path.GetFileName(path).StartsWith(orderId + "_") ? Results.File(path, "application/pdf") : Results.NotFound();
});

// ---- optional self-registration with Paccofacile at startup ----
var pf = builder.Configuration.GetSection("Paccofacile").Get<PaccofacileOptions>();
if (!string.IsNullOrWhiteSpace(hook.PublicBaseUrl) && pf is not null && !string.IsNullOrWhiteSpace(pf.Token))
{
    app.Lifetime.ApplicationStarted.Register(async () =>
    {
        try
        {
            using var api = new PaccofacileClient(pf);
            var res = await api.ValidateKeysAsync(WebhookUrls.RegistrationRequest(hook.PublicBaseUrl, hook.Secret));
            log.LogInformation("Registrazione webhook su Paccofacile: HTTP {code} {body}", res.StatusCode, res.Raw.ValueKind == JsonValueKind.Undefined ? "" : res.Raw.GetRawText());
        }
        catch (Exception ex) { log.LogError(ex, "Registrazione webhook fallita"); }
    });
}
else log.LogInformation("Registrazione automatica disattivata (serve Webhook:PublicBaseUrl + credenziali). Usa: paccofacile webhook-register --url https://...");

app.Run();

static async Task<string> ReadBodyAsync(HttpRequest req)
{
    using var reader = new StreamReader(req.Body);
    return await reader.ReadToEndAsync();
}

static string Sanitize(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray());

static object Summary(TrackingRecord r) => new
{
    r.PaccofacileOrderId, r.ExternalOrderId, r.ShipmentId, r.TrackingNumber, r.TrackingUrl, r.CarrierCode, r.ServiceCode,
    r.OrderStatusId, r.RecipientName, r.RecipientCity, last = r.LastMessage, checkpoints = r.Checkpoints.Count, waybills = r.Waybills.Count, r.ReceivedAt,
};

public sealed class WebhookSettings
{
    public string Secret { get; set; } = "";
    public string DataDir { get; set; } = "data";
    /// <summary>Public https URL where Paccofacile can reach this service (e.g. https://hooks.miodominio.it). Enables self-registration.</summary>
    public string? PublicBaseUrl { get; set; }
}
