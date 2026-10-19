using System.Text.Json;
using System.Text.Json.Serialization;

namespace Paccofacile.Client;

// ---------- Generic envelope ----------

/// <summary>Every API answer is { header: {...}, data: ... }. Data is kept typed + raw.</summary>
public sealed class ApiResponse<T>
{
    public int StatusCode { get; init; }
    public JsonElement Raw { get; init; }
    public JsonElement? Header { get; init; }
    public JsonElement? DataRaw { get; init; }
    public T? Data { get; init; }
}

// ---------- Requests ----------

/// <summary>A single package. Dimensions in cm, weight in kg.</summary>
public sealed class Parcel
{
    /// <summary>1 = box (pacco), 2 = envelope (busta), 5 = pallet, as used by the official plugin.</summary>
    [JsonPropertyName("shipment_type")] public int ShipmentType { get; set; } = 1;
    [JsonPropertyName("dim1")] public decimal Dim1 { get; set; }
    [JsonPropertyName("dim2")] public decimal Dim2 { get; set; }
    [JsonPropertyName("dim3")] public decimal Dim3 { get; set; }
    [JsonPropertyName("weight")] public decimal Weight { get; set; }

    public static Parcel Box(decimal l, decimal w, decimal h, decimal kg) => new() { ShipmentType = 1, Dim1 = l, Dim2 = w, Dim3 = h, Weight = kg };
    public override string ToString() => $"{Dim1}x{Dim2}x{Dim3}cm {Weight}kg (type {ShipmentType})";
}

public sealed class QuoteAddress
{
    [JsonPropertyName("iso_code")] public string IsoCode { get; set; } = "IT";
    [JsonPropertyName("postal_code")] public string PostalCode { get; set; } = "";
    [JsonPropertyName("city")] public string City { get; set; } = "";
    [JsonPropertyName("StateOrProvinceCode")] public string StateOrProvinceCode { get; set; } = "";
}

/// <summary>Full address used for pickup and destination in shipment/save.</summary>
public sealed class ShipmentAddress
{
    [JsonPropertyName("iso_code")] public string IsoCode { get; set; } = "IT";
    [JsonPropertyName("postal_code")] public string PostalCode { get; set; } = "";
    [JsonPropertyName("city")] public string City { get; set; } = "";
    [JsonPropertyName("header_name")] public string HeaderName { get; set; } = "";
    [JsonPropertyName("address")] public string Address { get; set; } = "";
    [JsonPropertyName("building_number")] public string BuildingNumber { get; set; } = "";
    [JsonPropertyName("StateOrProvinceCode")] public string StateOrProvinceCode { get; set; } = "";
    [JsonPropertyName("phone")] public string Phone { get; set; } = "";
    [JsonPropertyName("email")] public string Email { get; set; } = "";
    [JsonPropertyName("note")] public string Note { get; set; } = "";
    [JsonPropertyName("locker_id")] public string LockerId { get; set; } = "";

    public QuoteAddress ToQuoteAddress() => new() { IsoCode = IsoCode, PostalCode = PostalCode, City = City, StateOrProvinceCode = StateOrProvinceCode };
    public override string ToString() => $"{HeaderName}, {Address} {BuildingNumber}, {PostalCode} {City} ({StateOrProvinceCode}) {IsoCode}";
}

public sealed class QuoteShipmentService
{
    [JsonPropertyName("parcels")] public List<Parcel> Parcels { get; set; } = new();
    [JsonPropertyName("accessories")] public List<object> Accessories { get; set; } = new();
    [JsonPropertyName("package_content_type")] public string PackageContentType { get; set; } = "GOODS";
    [JsonPropertyName("service_id")] public int? ServiceId { get; set; }
}

public sealed class QuoteRequest
{
    [JsonPropertyName("shipment_service")] public QuoteShipmentService ShipmentService { get; set; } = new();
    [JsonPropertyName("pickup")] public QuoteAddress Pickup { get; set; } = new();
    [JsonPropertyName("destination")] public QuoteAddress Destination { get; set; } = new();
}

public sealed class SaveShipmentService
{
    [JsonPropertyName("shipment_type")] public int ShipmentType { get; set; } = 1;
    [JsonPropertyName("pickup_date")] public string? PickupDate { get; set; }
    [JsonPropertyName("pickup_range")] public string? PickupRange { get; set; }
    [JsonPropertyName("service_id")] public int ServiceId { get; set; }
    [JsonPropertyName("parcels")] public List<Parcel> Parcels { get; set; } = new();
    [JsonPropertyName("package_content_type")] public string PackageContentType { get; set; } = "GOODS";
}

public sealed class Money
{
    [JsonPropertyName("value")] public string Value { get; set; } = "0.00";
    [JsonPropertyName("currency")] public string Currency { get; set; } = "EUR";
}

public sealed class CustomsArticle
{
    [JsonPropertyName("amount")] public Money Amount { get; set; } = new();
    [JsonPropertyName("quantity")] public int Quantity { get; set; } = 1;
    [JsonPropertyName("weight")] public decimal Weight { get; set; }
    [JsonPropertyName("description")] public string Description { get; set; } = "Merce";
    [JsonPropertyName("iso_code_country_manufactured")] public string IsoCodeCountryManufactured { get; set; } = "IT";
}

public sealed class Customs
{
    [JsonPropertyName("amount")] public Money Amount { get; set; } = new();
    [JsonPropertyName("articles")] public List<CustomsArticle> Articles { get; set; } = new();
}

public sealed class SaveShipmentRequest
{
    [JsonPropertyName("external_order_id")] public string? ExternalOrderId { get; set; }
    [JsonPropertyName("external_service_name")] public string? ExternalServiceName { get; set; }
    [JsonPropertyName("shipment_service")] public SaveShipmentService ShipmentService { get; set; } = new();
    [JsonPropertyName("pickup")] public ShipmentAddress Pickup { get; set; } = new();
    [JsonPropertyName("destination")] public ShipmentAddress Destination { get; set; } = new();
    /// <summary>Serialized as an empty array when no customs are needed (mirrors the official plugin).</summary>
    [JsonPropertyName("customs")] public object Customs { get; set; } = Array.Empty<object>();
}

public sealed class BuyRequest
{
    [JsonPropertyName("shipments")] public List<long> Shipments { get; set; } = new();
    /// <summary>"1" = default invoice handling (what the official plugin sends); "0" plus data_fattura/address_id_select to invoice a specific billing address.</summary>
    [JsonPropertyName("fattura")] public string Fattura { get; set; } = "1";
    [JsonPropertyName("data_fattura")] public string DataFattura { get; set; } = "";
    [JsonPropertyName("address_id_select")] public string AddressIdSelect { get; set; } = "";
    [JsonPropertyName("payment_method")] public string PaymentMethod { get; set; } = "CREDIT";
}

public sealed class KeyValidationRequest
{
    [JsonPropertyName("domain")] public string Domain { get; set; } = "";
    [JsonPropertyName("api_version")] public string ApiVersion { get; set; } = "v1";
    [JsonPropertyName("endpoint_push_tracking")] public string EndpointPushTracking { get; set; } = "";
    [JsonPropertyName("endpoint_push_documents")] public string EndpointPushDocuments { get; set; } = "";
}

// ---------- Responses ----------

public sealed class Carrier
{
    [JsonPropertyName("service_id")] public int ServiceId { get; set; }
    [JsonPropertyName("carrier_id")] public int? CarrierId { get; set; }
    [JsonPropertyName("carrier_name")] public string? CarrierName { get; set; }
    [JsonPropertyName("service_name")] public string? ServiceName { get; set; }
    [JsonPropertyName("pickup_type")] public JsonElement? PickupType { get; set; }
    [JsonPropertyName("box_type")] public JsonElement? BoxType { get; set; }
    [JsonPropertyName("image_url")] public string? ImageUrl { get; set; }
    [JsonPropertyName("carrier_ship_time")] public string? CarrierShipTime { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class PriceTotal
{
    [JsonPropertyName("amount")] public decimal? Amount { get; set; }
    [JsonPropertyName("taxable_amount")] public decimal? TaxableAmount { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class PickupDateInfo
{
    [JsonPropertyName("first_date")] public string? FirstDate { get; set; }
    [JsonPropertyName("first_date_range")] public string? FirstDateRange { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class ServiceAvailable
{
    [JsonPropertyName("service_id")] public int ServiceId { get; set; }
    [JsonPropertyName("carrier")] public string? Carrier { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("price_total")] public PriceTotal? PriceTotal { get; set; }
    [JsonPropertyName("pickup_date")] public PickupDateInfo? PickupDate { get; set; }
    [JsonPropertyName("customes_required")] public int CustomsRequired { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public string DisplayName => $"{Carrier} {Name}".Trim();
}

public sealed class QuoteData
{
    [JsonPropertyName("services_available")] public List<ServiceAvailable> ServicesAvailable { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class Consolidation
{
    [JsonPropertyName("is_service_consolidation")] public int IsServiceConsolidation { get; set; }
    [JsonPropertyName("is_consolidato")] public int IsConsolidato { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class SavedShipment
{
    [JsonPropertyName("shipment_id")] public long ShipmentId { get; set; }
    [JsonPropertyName("consolidation")] public Consolidation? Consolidation { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class SaveShipmentData
{
    [JsonPropertyName("shipment")] public SavedShipment? Shipment { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class BuyOrder
{
    [JsonPropertyName("order_id")] public long OrderId { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class BuyData
{
    [JsonPropertyName("order")] public BuyOrder? Order { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class CreditValue
{
    [JsonPropertyName("value")] public decimal? Value { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class CreditData
{
    [JsonPropertyName("credit")] public CreditValue? Credit { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class BillingAddress
{
    [JsonPropertyName("address_id")] public JsonElement? AddressId { get; set; }
    [JsonPropertyName("is_default")] public int IsDefault { get; set; }
    [JsonPropertyName("company")] public string? Company { get; set; }
    [JsonPropertyName("firstname")] public string? FirstName { get; set; }
    [JsonPropertyName("lastname")] public string? LastName { get; set; }
    [JsonPropertyName("tax_id")] public string? TaxId { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

// ---------- Localities ----------

public sealed class LocalityItem
{
    [JsonPropertyName("cap")] public string? Cap { get; set; }
    [JsonPropertyName("postal_code")] public string? PostalCode { get; set; }
    [JsonPropertyName("locality")] public string? Locality { get; set; }
    [JsonPropertyName("StateOrProvinceCode")] public string? StateOrProvinceCode { get; set; }
    [JsonPropertyName("iso_code")] public string? IsoCode { get; set; }
    [JsonPropertyName("locality_id")] public long? LocalityId { get; set; }
    [JsonPropertyName("latitude")] public string? Latitude { get; set; }
    [JsonPropertyName("longitude")] public string? Longitude { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class LocalityItems
{
    [JsonPropertyName("items")] public List<LocalityItem> Items { get; set; } = new();
}
