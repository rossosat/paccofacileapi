using System.Globalization;
using System.Text;
using Paccofacile.Client;

namespace Paccofacile.Cli;

/// <summary>
/// Reads Amazon Seller Central order exports (Ordini > Report ordini > "Ordini non spediti" / "Nuovi ordini",
/// tab-delimited) and writes the Amazon shipping-confirmation flat file.
/// </summary>
public static class AmazonOrders
{
    public static List<OutboundOrder> Load(string path)
    {
        var file = DelimitedFile.Load(path);
        var byOrder = new Dictionary<string, OutboundOrder>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in file.Rows)
        {
            var orderId = DelimitedFile.Get(row, "order-id", "amazon-order-id", "numero-ordine", "order id");
            if (string.IsNullOrWhiteSpace(orderId)) continue;

            if (!byOrder.TryGetValue(orderId, out var order))
            {
                order = new OutboundOrder { OrderId = orderId, Source = "amazon" };
                var country = DelimitedFile.Get(row, "ship-country", "ship-country-code").ToUpperInvariant();
                if (string.IsNullOrWhiteSpace(country)) country = "IT";

                var (street, number) = AddressParser.Split(
                    DelimitedFile.Get(row, "ship-address-1"),
                    DelimitedFile.Get(row, "ship-address-2"),
                    DelimitedFile.Get(row, "ship-address-3"));

                var rawState = DelimitedFile.Get(row, "ship-state", "ship-province", "ship-region");
                var province = country == "IT" ? ItalianProvinces.ToCode(rawState) : rawState;
                if (country == "IT" && province is null)
                    order.Warnings.Add($"provincia non riconosciuta: '{rawState}' (controlla StateOrProvinceCode)");

                var recipient = DelimitedFile.Get(row, "recipient-name", "buyer-name");
                var phone = AddressParser.NormalizePhone(DelimitedFile.Get(row, "ship-phone-number", "buyer-phone-number"));
                var email = DelimitedFile.Get(row, "buyer-email");

                order.Destination = new ShipmentAddress
                {
                    IsoCode = country,
                    PostalCode = DelimitedFile.Get(row, "ship-postal-code", "ship-postcode"),
                    City = DelimitedFile.Get(row, "ship-city"),
                    HeaderName = recipient,
                    Address = street,
                    BuildingNumber = number,
                    StateOrProvinceCode = province ?? rawState,
                    Phone = phone,
                    Email = email,
                    Note = DelimitedFile.Get(row, "delivery-Instructions", "delivery-instructions"),
                };
                if (number == "SNC") order.Warnings.Add("numero civico non trovato nell'indirizzo, uso 'SNC'");

                byOrder[orderId] = order;
            }

            var qtyStr = DelimitedFile.Get(row, "quantity-to-ship", "quantity-purchased", "quantity");
            var qty = int.TryParse(qtyStr, out var q) ? q : 1;
            var price = AddressParser.ParseDecimal(DelimitedFile.Get(row, "item-price", "price"));
            order.Lines.Add(new OrderLine(
                DelimitedFile.Get(row, "sku", "seller-sku"),
                DelimitedFile.Get(row, "product-name", "title"),
                qty,
                DelimitedFile.Get(row, "order-item-id"),
                price));
            if (price.HasValue) order.GoodsValue = (order.GoodsValue ?? 0) + price.Value;
        }

        return byOrder.Values.ToList();
    }

    /// <summary>Amazon carrier-code for the confirmation file; "Other" + carrier-name when unknown.</summary>
    public static (string Code, string Name) MapCarrier(string? paccofacileCarrier)
    {
        var c = (paccofacileCarrier ?? "").ToLowerInvariant();
        if (c.Contains("brt") || c.Contains("bartolini")) return ("BRT", "BRT");
        if (c.Contains("gls")) return ("GLS", "GLS");
        if (c.Contains("sda")) return ("SDA", "SDA");
        if (c.Contains("ups")) return ("UPS", "UPS");
        if (c.Contains("tnt")) return ("TNT", "TNT");
        if (c.Contains("dhl")) return ("DHL", "DHL");
        if (c.Contains("dpd")) return ("DPD", "DPD");
        if (c.Contains("fedex")) return ("FedEx", "FedEx");
        if (c.Contains("poste")) return ("Poste Italiane", "Poste Italiane");
        if (c.Contains("inpost")) return ("InPost", "InPost");
        if (c.Contains("palletways")) return ("Other", "Palletways");
        return ("Other", paccofacileCarrier ?? "Paccofacile");
    }

    /// <summary>
    /// Writes the tab-delimited "Conferma spedizione" file you upload in Seller Central
    /// (Ordini > Carica file ordini > Conferma spedizione).
    /// </summary>
    public static void WriteShippingConfirmation(string path, IEnumerable<ShipmentResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join('\t', "order-id", "order-item-id", "quantity", "ship-date", "carrier-code", "carrier-name", "tracking-number", "ship-method"));
        foreach (var r in results)
        {
            if (r.Order.Source != "amazon" || !r.Success) continue;
            var (code, name) = MapCarrier(r.Service?.Carrier);
            var shipDate = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            foreach (var line in r.Order.Lines)
            {
                sb.AppendLine(string.Join('\t',
                    r.Order.OrderId,
                    line.OrderItemId ?? "",
                    line.Quantity.ToString(CultureInfo.InvariantCulture),
                    shipDate,
                    code,
                    code == "Other" ? name : "",
                    r.TrackingNumber ?? "",
                    r.Service?.Name ?? ""));
            }
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }
}
