using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Paccofacile.Client;

/// <summary>
/// Typed client for the Paccofacile.it PRO REST API
/// (base https://paccofacile.tecnosogima.cloud/{live|sandbox}/v1/service/).
/// Endpoints and payload shapes were taken from the official "Paccofacile.it for WooCommerce" plugin.
/// </summary>
public sealed class PaccofacileClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly PaccofacileOptions _options;

    /// <summary>Optional sink for raw request/response logging (endpoint, request body, status, response body).</summary>
    public Action<string, string?, int, string>? Trace { get; set; }

    public PaccofacileClient(PaccofacileOptions options, HttpClient? http = null)
    {
        options.Validate();
        _options = options;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = options.Timeout };
        _http.BaseAddress = new Uri(options.BaseUrl);
        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Account-Number", options.AccountNumber);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("api-key", options.ApiKey);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Session-Id", "paccofacile-dotnet");
    }

    public string BaseUrl => _options.BaseUrl;

    // ---------------- Account ----------------

    /// <summary>POST key_validation. Returns true when the credentials are accepted (anything but 401).</summary>
    public async Task<ApiResponse<JsonElement>> ValidateKeysAsync(KeyValidationRequest? request = null, CancellationToken ct = default)
        => await SendAsync<JsonElement>(HttpMethod.Post, "key_validation", request ?? new KeyValidationRequest(), ct, throwOnError: false);

    /// <summary>GET customers/credit.</summary>
    public Task<ApiResponse<CreditData>> GetCreditAsync(CancellationToken ct = default)
        => SendAsync<CreditData>(HttpMethod.Get, "customers/credit", null, ct);

    /// <summary>GET billing-address.</summary>
    public Task<ApiResponse<List<BillingAddress>>> GetBillingAddressesAsync(CancellationToken ct = default)
        => SendAsync<List<BillingAddress>>(HttpMethod.Get, "billing-address", null, ct);

    /// <summary>GET address-book.</summary>
    public Task<ApiResponse<JsonElement>> GetAddressBookAsync(CancellationToken ct = default)
        => SendAsync<JsonElement>(HttpMethod.Get, "address-book", null, ct);

    // ---------------- Carriers & packaging ----------------

    /// <summary>GET carriers: the services enabled on the account.</summary>
    public Task<ApiResponse<List<Carrier>>> GetCarriersAsync(CancellationToken ct = default)
        => SendAsync<List<Carrier>>(HttpMethod.Get, "carriers", null, ct);

    public Task<ApiResponse<JsonElement>> GetPackagingListAsync(CancellationToken ct = default)
        => SendAsync<JsonElement>(HttpMethod.Get, "packaging/list", null, ct);

    // ---------------- Quotes & shipments ----------------

    /// <summary>POST shipment/quote?field_pickup_date=1. Returns the services available with prices and first pickup date.</summary>
    public Task<ApiResponse<QuoteData>> QuoteAsync(QuoteRequest request, CancellationToken ct = default)
        => SendAsync<QuoteData>(HttpMethod.Post, "shipment/quote?field_pickup_date=1", request, ct);

    /// <summary>POST shipment/save: creates the shipment (unpaid) and returns its shipment_id.</summary>
    public Task<ApiResponse<SaveShipmentData>> SaveShipmentAsync(SaveShipmentRequest request, CancellationToken ct = default)
        => SendAsync<SaveShipmentData>(HttpMethod.Post, "shipment/save", request, ct);

    /// <summary>POST shipment/buy: pays one or more saved shipments with account credit.</summary>
    public Task<ApiResponse<BuyData>> BuyAsync(BuyRequest request, CancellationToken ct = default)
        => SendAsync<BuyData>(HttpMethod.Post, "shipment/buy", request, ct);

    public Task<ApiResponse<BuyData>> BuyAsync(long shipmentId, CancellationToken ct = default)
        => BuyAsync(new BuyRequest { Shipments = { shipmentId } }, ct);

    // ---------------- Lockers & localities ----------------

    public Task<ApiResponse<JsonElement>> GetLockerAsync(string lockerId, CancellationToken ct = default)
        => SendAsync<JsonElement>(HttpMethod.Get, $"lockers/{Uri.EscapeDataString(lockerId)}", null, ct);

    public Task<ApiResponse<JsonElement>> ValidateLocalityAsync(object payload, CancellationToken ct = default)
        => SendAsync<JsonElement>(HttpMethod.Post, "locality/validation", payload, ct);

    /// <summary>POST locality/validation { iso_code, search }: search accepts a city/frazione name or a postal code.</summary>
    public async Task<List<LocalityItem>> SearchLocalitiesAsync(string isoCode, string search, CancellationToken ct = default)
    {
        var res = await SendAsync<LocalityItems>(HttpMethod.Post, "locality/validation", new { iso_code = isoCode, search }, ct, throwOnError: false);
        if (res.StatusCode >= 500 || res.StatusCode == 401 || res.StatusCode == 403)
            throw new PaccofacileException((System.Net.HttpStatusCode)res.StatusCode, "locality/validation", res.Raw.ValueKind == JsonValueKind.Undefined ? "" : res.Raw.GetRawText(), res.Raw.ValueKind == JsonValueKind.Undefined ? null : res.Raw);
        return res.Data?.Items ?? new List<LocalityItem>();   // 4xx "not found" style answers = no match
    }

    public Task<ApiResponse<JsonElement>> SearchLocalityAsync(object payload, CancellationToken ct = default)
        => SendAsync<JsonElement>(HttpMethod.Post, "locality/search", payload, ct);

    // ---------------- Generic ----------------

    /// <summary>Raw call for endpoints not wrapped above (e.g. "packaging/list_types").</summary>
    public Task<ApiResponse<JsonElement>> RawAsync(HttpMethod method, string endpoint, object? body = null, bool throwOnError = true, CancellationToken ct = default)
        => SendAsync<JsonElement>(method, endpoint, body, ct, throwOnError);

    private async Task<ApiResponse<T>> SendAsync<T>(HttpMethod method, string endpoint, object? body, CancellationToken ct, bool throwOnError = true)
    {
        using var req = new HttpRequestMessage(method, endpoint);
        string? json = null;
        if (body is not null)
        {
            json = body is string s ? s : JsonSerializer.Serialize(body, JsonHelpers.Options);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var raw = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        Trace?.Invoke(endpoint, json, (int)res.StatusCode, raw);

        JsonElement root = default;
        var parsed = false;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                root = doc.RootElement.Clone();
                parsed = true;
            }
            catch (JsonException) { /* non-JSON body, handled below */ }
        }

        if (!res.IsSuccessStatusCode && throwOnError)
            throw new PaccofacileException(res.StatusCode, endpoint, raw, parsed ? root : null);

        JsonElement? header = null, data = null;
        if (parsed && root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("header", out var h)) header = h;
            if (root.TryGetProperty("data", out var d)) data = d;
        }

        T? typed = default;
        if (data.HasValue && data.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            try { typed = data.Value.Deserialize<T>(JsonHelpers.Options); }
            catch (JsonException) { /* keep raw only */ }
        }
        else if (typeof(T) == typeof(JsonElement) && parsed)
        {
            typed = (T)(object)root;
        }

        return new ApiResponse<T>
        {
            StatusCode = (int)res.StatusCode,
            Raw = root,
            Header = header,
            DataRaw = data,
            Data = typed,
        };
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
