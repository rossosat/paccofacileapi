using System.Globalization;
using System.Text;
using System.Text.Json;
using Paccofacile.Cli;
using Paccofacile.Client;

Console.OutputEncoding = Encoding.UTF8;
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

var args0 = args.ToList();
if (args0.Count == 0 || args0[0] is "-h" or "--help" or "help")
{
    PrintHelp();
    return 0;
}

var command = args0[0].ToLowerInvariant();
var flags = ParseFlags(args0.Skip(1));
var verbose = flags.ContainsKey("verbose") || flags.ContainsKey("v");

var cfg = AppConfig.Load(AppContext.BaseDirectory);
if (flags.TryGetValue("mode", out var mode)) cfg.Paccofacile.Mode = mode;
try
{
    cfg.SelectSender(flags.TryGetValue("sender", out var snd) ? snd : flags.TryGetValue("mittente", out var mit) ? mit : null);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"Errore: {ex.Message}");
    return 2;
}

PaccofacileClient api;
try
{
    api = new PaccofacileClient(cfg.Paccofacile);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"Configurazione mancante: {ex.Message}");
    Console.Error.WriteLine($"Compila {Path.Combine(AppContext.BaseDirectory, "appsettings.Local.json")} (Token, ApiKey, AccountNumber) oppure le variabili PACCOFACILE_TOKEN / PACCOFACILE_API_KEY / PACCOFACILE_ACCOUNT_NUMBER.");
    return 2;
}
if (verbose)
    api.Trace = (ep, reqBody, status, resBody) =>
    {
        Console.WriteLine($"--> {ep}");
        if (reqBody is not null) Console.WriteLine(reqBody);
        Console.WriteLine($"<-- {status}");
        Console.WriteLine(resBody.Length > 4000 ? resBody[..4000] + "…" : resBody);
    };

Console.WriteLine($"Paccofacile [{cfg.Paccofacile.Mode}] account {cfg.Paccofacile.AccountNumber}"
    + (cfg.Senders.Count > 0 ? $"  mittente: {cfg.ActiveSenderName} ({cfg.ActiveSender.HeaderName})" : ""));

try
{
    switch (command)
    {
        case "check": return await Check();
        case "carriers": return await Carriers();
        case "quote": return await Quote();
        case "ship": return await Ship();
        case "amazon": return await ImportAndShip(AmazonOrders.Load, "amazon");
        case "csv": return await ImportAndShip(GenericOrders.Load, "csv");
        case "buy": return await Buy();
        case "raw": return await Raw();
        case "webhook-register": return await WebhookRegister();
        case "tracking": return Tracking();
        case "confirm": return Confirm();
        default:
            Console.Error.WriteLine($"Comando sconosciuto: {command}");
            PrintHelp();
            return 1;
    }
}
catch (PaccofacileException ex)
{
    Console.Error.WriteLine(ex.Message);
    if (verbose) Console.Error.WriteLine(ex.RawBody);
    return 3;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Errore: {ex.Message}");
    if (verbose) Console.Error.WriteLine(ex);
    return 4;
}

// ---------------------------------------------------------------- commands

async Task<int> Check()
{
    // key_validation also (re)registers the push endpoints: resend the configured ones so a plain "check" never blanks them
    var registered = !string.IsNullOrWhiteSpace(cfg.Webhook.PublicBaseUrl) && !string.IsNullOrWhiteSpace(cfg.Webhook.Secret);
    var kv = await api.ValidateKeysAsync(registered ? WebhookUrls.RegistrationRequest(cfg.Webhook.PublicBaseUrl, cfg.Webhook.Secret) : null);
    var ok = kv.StatusCode != 401;
    if (registered) Console.WriteLine($"webhook registrato: {cfg.Webhook.PublicBaseUrl}");
    Console.WriteLine($"key_validation: HTTP {kv.StatusCode} -> {(ok ? "credenziali ACCETTATE" : "credenziali RIFIUTATE")}");
    if (!ok)
    {
        Console.WriteLine(kv.Raw.ValueKind == JsonValueKind.Undefined ? "" : kv.Raw.GetRawText());
        return 3;
    }

    try
    {
        var credit = await api.GetCreditAsync();
        var value = credit.Data?.Credit?.Value ?? ParseDecimal(JsonHelpers.AsString(JsonHelpers.FindProperty(credit.Raw, "value")));
        Console.WriteLine($"credito residuo: {(value.HasValue ? value.Value.ToString("0.00") + " EUR" : credit.DataRaw?.GetRawText())}");
    }
    catch (PaccofacileException ex) { Console.WriteLine("credito: " + ex.Message); }

    var carriers = await api.GetCarriersAsync();
    Console.WriteLine($"servizi attivi: {carriers.Data?.Count ?? 0}");
    PrintCarriers(carriers.Data);

    var problems = cfg.SenderProblems();
    if (problems.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("ATTENZIONE: indirizzo mittente incompleto in appsettings.json, mancano:");
        foreach (var p in problems) Console.WriteLine("  - " + p);
    }
    else Console.WriteLine($"mittente attivo: {cfg.SenderAddress}");
    if (cfg.Senders.Count > 0)
    {
        Console.WriteLine("mittenti disponibili (--sender NOME):");
        foreach (var sp in cfg.Senders)
            Console.WriteLine($"  {sp.Key,-12} {sp.Value.HeaderName} - {sp.Value.Email}{(string.Equals(sp.Key, cfg.DefaultSender, StringComparison.OrdinalIgnoreCase) ? "  [default]" : "")}");
    }
    return 0;
}

async Task<int> Carriers()
{
    var carriers = await api.GetCarriersAsync();
    PrintCarriers(carriers.Data);
    if (flags.ContainsKey("json")) Console.WriteLine(carriers.DataRaw?.GetRawText());
    return 0;
}

void PrintCarriers(List<Carrier>? list)
{
    if (list is null || list.Count == 0) { Console.WriteLine("  (nessun servizio attivo: attivali su paccofacile.it > Corrieri)"); return; }
    Console.WriteLine($"  {"service_id",10}  {"corriere",-18} {"servizio",-30} {"tempi",-20}");
    foreach (var c in list)
        Console.WriteLine($"  {c.ServiceId,10}  {c.CarrierName,-18} {c.ServiceName,-30} {c.CarrierShipTime,-20}");
}

async Task<int> Quote()
{
    RequireSender();
    var dest = new QuoteAddress
    {
        IsoCode = Flag("country", "IT").ToUpperInvariant(),
        PostalCode = Require("zip"),
        City = Require("city"),
        StateOrProvinceCode = Flag("country", "IT") == "IT" ? (ItalianProvinces.ToCode(Require("prov")) ?? Require("prov")) : Require("prov"),
    };
    var parcels = ParseParcels() ?? new List<Parcel> { cfg.DefaultParcelObj };
    var wf = new ShipmentWorkflow(api, cfg, Console.Out);

    var tmp = new ShipmentAddress { IsoCode = dest.IsoCode, PostalCode = dest.PostalCode, City = dest.City, StateOrProvinceCode = dest.StateOrProvinceCode };
    var warnings = new List<string>();
    var (okLoc, locError) = await wf.Localities.ResolveAsync(tmp, warnings);
    foreach (var w in warnings) Console.WriteLine("  ! " + w);
    if (!okLoc) throw new InvalidOperationException(locError);
    dest = tmp.ToQuoteAddress();

    var quote = await wf.QuoteAsync(dest, parcels, IntFlag("service"), CancellationToken.None);

    Console.WriteLine($"destinazione: {dest.PostalCode} {dest.City} ({dest.StateOrProvinceCode}) {dest.IsoCode}");
    Console.WriteLine($"colli: {string.Join(", ", parcels)}");
    Console.WriteLine($"  {"service_id",10}  {"servizio",-40} {"prezzo",10} {"imponibile",10}  ritiro");
    foreach (var s in quote.ServicesAvailable.OrderBy(s => s.PriceTotal?.Amount ?? decimal.MaxValue))
    {
        var tags = (s.CustomsRequired != 0 ? "  [dogana]" : "")
                 + (!AppConfig.IsHomeDelivery(s) ? "  [punto di ritiro]" : "")
                 + (!cfg.IsEligible(s) ? "  (escluso dalla scelta automatica)" : "");
        Console.WriteLine($"  {s.ServiceId,10}  {s.DisplayName,-40} {Fmt(s.PriceTotal?.Amount),10} {Fmt(s.PriceTotal?.TaxableAmount),10}  {s.PickupDate?.FirstDate} {s.PickupDate?.FirstDateRange}{tags}");
    }
    if (quote.ServicesAvailable.Count == 0) Console.WriteLine("  nessun servizio disponibile");
    return 0;
}

async Task<int> Ship()
{
    RequireSender();
    var country = Flag("country", "IT").ToUpperInvariant();
    var rawProv = Require("prov");
    var (street, number) = flags.ContainsKey("number")
        ? (Require("address"), Flag("number"))
        : AddressParser.Split(Require("address"));
    var order = new OutboundOrder
    {
        OrderId = Flag("order", DateTime.Now.ToString("yyyyMMddHHmmss")),
        Source = "manual",
        Destination = new ShipmentAddress
        {
            IsoCode = country,
            PostalCode = Require("zip"),
            City = Require("city"),
            StateOrProvinceCode = country == "IT" ? (ItalianProvinces.ToCode(rawProv) ?? rawProv) : rawProv,
            HeaderName = Require("name"),
            Address = street,
            BuildingNumber = number,
            Phone = AddressParser.NormalizePhone(Flag("phone")),
            Email = Flag("email"),
            Note = Flag("note"),
        },
        GoodsValue = ParseDecimal(Flag("value")),
    };
    order.Lines.Add(new OrderLine(Flag("sku"), Flag("desc", "Merce"), 1, null, order.GoodsValue));

    var wf = new ShipmentWorkflow(api, cfg, Console.Out);
    var opt = new WorkflowOptions { ServiceId = IntFlag("service"), Buy = flags.ContainsKey("buy"), DryRun = flags.ContainsKey("dry-run"), ParcelOverride = ParseParcels(), Verbose = verbose };
    Console.WriteLine($"spedizione a {order.Destination}");
    var r = await wf.ProcessAsync(order, opt);
    return r.Success ? 0 : 5;
}

async Task<int> ImportAndShip(Func<string, List<OutboundOrder>> loader, string kind)
{
    RequireSender();
    var file = Require("file");
    if (!File.Exists(file)) throw new FileNotFoundException("File non trovato", file);
    var orders = loader(file);
    Console.WriteLine($"{orders.Count} ordini letti da {file}");

    var only = Flag("only");
    if (!string.IsNullOrWhiteSpace(only))
    {
        var set = only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        orders = orders.Where(o => set.Contains(o.OrderId)).ToList();
    }

    var wf = new ShipmentWorkflow(api, cfg, Console.Out);
    var opt = new WorkflowOptions { ServiceId = IntFlag("service"), Buy = flags.ContainsKey("buy"), DryRun = flags.ContainsKey("dry-run"), ParcelOverride = ParseParcels(), Verbose = verbose };
    if (opt.DryRun) Console.WriteLine("DRY RUN: solo preventivi, nessuna spedizione creata");
    else if (!opt.Buy) Console.WriteLine("Le spedizioni vengono create ma NON pagate (aggiungi --buy per pagarle con il credito)");

    var results = new List<ShipmentResult>();
    var i = 0;
    foreach (var order in orders)
    {
        i++;
        Console.WriteLine();
        Console.WriteLine($"[{i}/{orders.Count}] {order.OrderId}: {order.Destination}");
        foreach (var l in order.Lines) Console.WriteLine($"  {l.Quantity} x {l.Sku} {Truncate(l.Description, 50)}");
        var r = await wf.ProcessAsync(order, opt);
        foreach (var w in order.Warnings.Distinct()) Console.WriteLine("  ! " + w);
        results.Add(r);
    }

    Console.WriteLine();
    var ok = results.Count(r => r.Success);
    Console.WriteLine($"Completati {ok}/{results.Count}. Totale {Fmt(results.Where(r => r.Success).Sum(r => r.Price ?? 0))} EUR. Log: {cfg.ShipmentLog}");
    foreach (var r in results.Where(r => !r.Success)) Console.WriteLine($"  KO {r.Order.OrderId}: {r.Error}");

    if (kind == "amazon" && !opt.DryRun)
    {
        // tracking may already have been pushed to the webhook store (same machine / shared folder)
        var store = TryOpenStore();
        if (store is not null)
            foreach (var r in results.Where(r => r.Success && string.IsNullOrWhiteSpace(r.TrackingNumber)))
                r.TrackingNumber = LookupTracking(store, r.Order.OrderId, r.PaccofacileOrderId)?.TrackingNumber;

        var outPath = Flag("out", Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file))!, $"amazon-conferma-spedizione-{DateTime.Now:yyyyMMdd-HHmm}.txt"));
        AmazonOrders.WriteShippingConfirmation(outPath, results);
        Console.WriteLine($"File conferma spedizione Amazon scritto: {outPath}");
        if (results.Any(r => r.Success && string.IsNullOrWhiteSpace(r.TrackingNumber)))
            Console.WriteLine($"  (alcune righe non hanno ancora il tracking: quando il webhook lo riceve, lancia  paccofacile confirm --file \"{outPath}\"  per completarlo)");
    }
    return results.All(r => r.Success) ? 0 : 5;
}

// ---------------------------------------------------------------- webhook / tracking

async Task<int> WebhookRegister()
{
    var url = Flag("url", cfg.Webhook.PublicBaseUrl);
    var secret = Flag("secret", cfg.Webhook.Secret);
    if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("serve --url https://... (URL pubblico del webhook) o Webhook.PublicBaseUrl in appsettings");
    if (string.IsNullOrWhiteSpace(secret)) throw new ArgumentException("serve --secret o Webhook.Secret in appsettings (uguale a quello del servizio webhook)");
    if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) Console.WriteLine("ATTENZIONE: l'URL non è https; Paccofacile potrebbe rifiutarlo o non consegnare i push.");

    var req = WebhookUrls.RegistrationRequest(url, secret);
    Console.WriteLine($"tracking  -> {req.EndpointPushTracking}");
    Console.WriteLine($"documenti -> {req.EndpointPushDocuments}");
    var res = await api.ValidateKeysAsync(req);
    Console.WriteLine($"key_validation: HTTP {res.StatusCode} {(res.Raw.ValueKind == JsonValueKind.Undefined ? "" : res.Raw.GetRawText())}");
    return res.StatusCode is >= 200 and < 300 ? 0 : 3;
}

int Tracking()
{
    var store = TryOpenStore() ?? throw new InvalidOperationException($"cartella dati webhook non trovata: {cfg.ResolvedWebhookDataDir} (imposta Webhook.DataDir)");
    var only = Flag("order");
    var records = store.All().ToList();
    if (!string.IsNullOrWhiteSpace(only))
        records = records.Where(r => r.PaccofacileOrderId.ToString() == only || string.Equals(r.ExternalOrderId, only, StringComparison.OrdinalIgnoreCase)).ToList();
    if (records.Count == 0) { Console.WriteLine($"nessun tracking ricevuto (cartella {store.TrackingDir})"); return 0; }

    foreach (var r in records)
    {
        Console.WriteLine($"ordine Paccofacile {r.PaccofacileOrderId}  ext {r.ExternalOrderId ?? "-"}  {r.CarrierCode} {r.ServiceCode}  tracking {r.TrackingNumber ?? "-"}  dest {r.RecipientName} {r.RecipientCity}  agg. {r.ReceivedAt:yyyy-MM-dd HH:mm}");
        if (flags.ContainsKey("full"))
        {
            foreach (var c in r.Checkpoints) Console.WriteLine($"    {c.Time}  {c.Message}  [{c.City}]");
            foreach (var w in r.Waybills) Console.WriteLine($"    LDV: {w}");
        }
        else if (r.LastMessage is not null) Console.WriteLine($"    ultimo: {r.Checkpoints[^1].Time} {r.LastMessage}");
    }
    return 0;
}

/// <summary>Fills the tracking-number column of an Amazon confirmation file from the webhook store.</summary>
int Confirm()
{
    var file = Require("file");
    if (!File.Exists(file)) throw new FileNotFoundException("File non trovato", file);
    var store = TryOpenStore() ?? throw new InvalidOperationException($"cartella dati webhook non trovata: {cfg.ResolvedWebhookDataDir}");

    var lines = File.ReadAllLines(file).ToList();
    if (lines.Count == 0) throw new InvalidOperationException("file vuoto");
    var header = lines[0].Split('\t').ToList();
    int idx(string n) => header.FindIndex(h => string.Equals(h.Trim(), n, StringComparison.OrdinalIgnoreCase));
    var iOrder = idx("order-id"); var iTrack = idx("tracking-number"); var iCode = idx("carrier-code"); var iName = idx("carrier-name");
    if (iOrder < 0 || iTrack < 0) throw new InvalidOperationException("il file non ha le colonne order-id / tracking-number");

    var filled = 0; var missing = new List<string>();
    for (var i = 1; i < lines.Count; i++)
    {
        if (string.IsNullOrWhiteSpace(lines[i])) continue;
        var cells = lines[i].Split('\t').ToList();
        while (cells.Count < header.Count) cells.Add("");
        if (!string.IsNullOrWhiteSpace(cells[iTrack])) continue;

        var rec = LookupTracking(store, cells[iOrder], null);
        if (rec?.TrackingNumber is null) { missing.Add(cells[iOrder]); continue; }
        cells[iTrack] = rec.TrackingNumber;
        if (iCode >= 0 && string.IsNullOrWhiteSpace(cells[iCode]) && !string.IsNullOrWhiteSpace(rec.CarrierCode))
        {
            var (code, name) = AmazonOrders.MapCarrier(rec.CarrierCode);
            cells[iCode] = code;
            if (iName >= 0 && code == "Other") cells[iName] = name;
        }
        lines[i] = string.Join('\t', cells);
        filled++;
    }

    var outPath = Flag("out", file);
    File.WriteAllLines(outPath, lines, new UTF8Encoding(false));
    Console.WriteLine($"tracking compilati: {filled}. File: {outPath}");
    foreach (var m in missing.Distinct()) Console.WriteLine($"  ancora senza tracking: {m}");
    return missing.Count == 0 ? 0 : 5;
}

TrackingStore? TryOpenStore()
{
    var dir = cfg.ResolvedWebhookDataDir;
    return Directory.Exists(dir) ? new TrackingStore(dir) : null;
}

/// <summary>Finds a pushed record by marketplace order id (external_order_id) or by Paccofacile order id via shipments-log.csv.</summary>
TrackingRecord? LookupTracking(TrackingStore store, string externalOrderId, long? paccofacileOrderId)
{
    if (paccofacileOrderId.HasValue && store.Get(paccofacileOrderId.Value) is { } byId) return byId;
    if (store.GetByExternalOrderId(externalOrderId) is { } byExt) return byExt;

    var logPath = Path.IsPathRooted(cfg.ShipmentLog) ? cfg.ShipmentLog : Path.Combine(Environment.CurrentDirectory, cfg.ShipmentLog);
    if (!File.Exists(logPath)) return null;
    try
    {
        var log = DelimitedFile.Load(logPath, ';');
        foreach (var row in log.Rows.AsEnumerable().Reverse())
        {
            if (!string.Equals(DelimitedFile.Get(row, "order_id"), externalOrderId, StringComparison.OrdinalIgnoreCase)) continue;
            if (long.TryParse(DelimitedFile.Get(row, "paccofacile_order_id"), out var pid) && store.Get(pid) is { } r) return r;
            if (long.TryParse(DelimitedFile.Get(row, "shipment_id"), out var sid))
            {
                var byShipment = store.All().FirstOrDefault(x => x.ShipmentId == sid);
                if (byShipment is not null) return byShipment;
            }
        }
    }
    catch (Exception) { /* log unreadable: ignore */ }
    return null;
}

async Task<int> Buy()
{
    var ids = Require("shipments").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(long.Parse).ToList();
    var req = new BuyRequest { Shipments = ids };
    if (flags.TryGetValue("invoice-address", out var addr)) { req.Fattura = "0"; req.AddressIdSelect = addr; req.DataFattura = Flag("invoice-date", DateTime.Now.ToString("yyyy-MM-dd")); }
    var res = await api.BuyAsync(req);
    Console.WriteLine(res.DataRaw?.GetRawText() ?? res.Raw.GetRawText());
    return 0;
}

async Task<int> Raw()
{
    var method = Flag("method", "GET").ToUpperInvariant();
    var endpoint = Require("endpoint");
    object? body = flags.TryGetValue("body", out var b) ? (File.Exists(b) ? File.ReadAllText(b) : b) : null;
    var res = await api.RawAsync(new HttpMethod(method), endpoint, body, throwOnError: false);
    Console.WriteLine($"HTTP {res.StatusCode}");
    Console.WriteLine(res.Raw.ValueKind == JsonValueKind.Undefined ? "(no json)" : JsonSerializer.Serialize(res.Raw, JsonHelpers.Pretty));
    return res.StatusCode is >= 200 and < 300 ? 0 : 3;
}

// ---------------------------------------------------------------- helpers

void RequireSender()
{
    var problems = cfg.SenderProblems();
    if (problems.Count == 0) return;
    throw new InvalidOperationException("indirizzo mittente incompleto in appsettings.json: " + string.Join(", ", problems));
}

string Flag(string name, string def = "") => flags.TryGetValue(name, out var v) ? v : def;
string Require(string name) => flags.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : throw new ArgumentException($"parametro --{name} obbligatorio");
int? IntFlag(string name) => flags.TryGetValue(name, out var v) && int.TryParse(v, out var i) ? i : null;
decimal? ParseDecimal(string? s) => AddressParser.ParseDecimal(s);
string Fmt(decimal? d) => d.HasValue ? d.Value.ToString("0.00", CultureInfo.InvariantCulture) : "";
string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

/// <summary>--parcel 30x20x15:1.2 (repeatable, comma separated). Type: append /2 for envelope, /5 for pallet.</summary>
List<Parcel>? ParseParcels()
{
    if (!flags.TryGetValue("parcel", out var spec) || string.IsNullOrWhiteSpace(spec)) return null;
    var list = new List<Parcel>();
    foreach (var item in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        var type = 1;
        var s = item;
        var slash = s.LastIndexOf('/');
        if (slash > 0 && int.TryParse(s[(slash + 1)..], out var t)) { type = t; s = s[..slash]; }
        var parts = s.Split(':');
        var dims = parts[0].Split('x', 'X');
        if (dims.Length != 3 || parts.Length != 2) throw new ArgumentException($"formato collo non valido '{item}': usa LxWxH:kg es. 30x20x15:1.2");
        list.Add(new Parcel
        {
            ShipmentType = type,
            Dim1 = decimal.Parse(dims[0], CultureInfo.InvariantCulture),
            Dim2 = decimal.Parse(dims[1], CultureInfo.InvariantCulture),
            Dim3 = decimal.Parse(dims[2], CultureInfo.InvariantCulture),
            Weight = decimal.Parse(parts[1].Replace(',', '.'), CultureInfo.InvariantCulture),
        });
    }
    return list;
}

static Dictionary<string, string> ParseFlags(IEnumerable<string> items)
{
    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    string? key = null;
    foreach (var it in items)
    {
        if (it.StartsWith("--"))
        {
            if (key is not null) d[key] = "";
            var eq = it.IndexOf('=');
            if (eq > 0) { d[it[2..eq]] = it[(eq + 1)..]; key = null; }
            else key = it[2..];
        }
        else if (key is not null) { d[key] = it; key = null; }
    }
    if (key is not null) d[key] = "";
    return d;
}

static void PrintHelp()
{
    Console.WriteLine("""
Paccofacile CLI - crea e paga spedizioni Paccofacile.it PRO da riga di comando.

  check                                   verifica credenziali, credito, servizi attivi, indirizzo mittente
  carriers [--json]                       elenca i servizi corriere attivi sull'account
  quote   --zip 00135 --city Roma --prov RM [--country IT] [--parcel 30x20x15:1.2] [--service ID]
  ship    --name "Mario Rossi" --address "Via Roma 12" --zip 00135 --city Roma --prov RM
          [--phone 333..] [--email ..] [--note ..] [--parcel 30x20x15:1.2] [--service ID] [--buy] [--dry-run]
  amazon  --file "ordini-non-spediti.txt" [--service ID] [--parcel ..] [--buy] [--dry-run] [--only ORD1,ORD2] [--out conferma.txt]
          legge il report ordini di Seller Central (tab), crea/paga le spedizioni e scrive il file di conferma spedizione
  csv     --file ordini.csv [--service ID] [--buy] [--dry-run]
          colonne: order_id,name,address,building_number,postal_code,city,province,country,phone,email,sku,quantity,value,dim1,dim2,dim3,weight
  buy     --shipments 123,456 [--invoice-address ID --invoice-date 2026-10-19]
  raw     --endpoint carriers [--method POST] [--body '{...}' | --body file.json]

  webhook-register --url https://hooks.miodominio.it [--secret ...]   registra su Paccofacile gli URL del servizio Paccofacile.Webhook
  tracking [--order 403-...|<id paccofacile>] [--full]                tracking ricevuti dal webhook (cartella Webhook.DataDir)
  confirm  --file amazon-conferma-spedizione-....txt [--out ...]      completa la colonna tracking-number con i dati del webhook

Opzioni globali: --verbose (log HTTP completo)   --mode live|sandbox   --sender NOME (profilo mittente, vedi `check`; default da appsettings)
Colli: --parcel LxWxH:kg[,...]  (es. 30x20x15:1.2,20x15x10:0.5); senza --parcel usa SkuParcels/DefaultParcel di appsettings.json
Senza --buy le spedizioni restano "da pagare" nell'area riservata; --buy le paga subito con il credito (payment_method CREDIT).
""");
}
