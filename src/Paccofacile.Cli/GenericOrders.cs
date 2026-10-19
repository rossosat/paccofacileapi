using Paccofacile.Client;

namespace Paccofacile.Cli;

/// <summary>
/// Generic CSV importer for "altre parti" (eBay export, Excel, gestionale...).
/// Recognised columns (case-insensitive, any delimiter):
///   order_id, name, address, building_number, postal_code, city, province, country, phone, email, note,
///   sku, description, quantity, value, dim1, dim2, dim3, weight
/// Rows sharing the same order_id become one shipment with several lines. Dimensions are optional:
/// when present a parcel is built from them, otherwise SKU map / default parcel apply.
/// </summary>
public static class GenericOrders
{
    public static List<OutboundOrder> Load(string path)
    {
        var file = DelimitedFile.Load(path);
        var byOrder = new Dictionary<string, OutboundOrder>(StringComparer.OrdinalIgnoreCase);
        var auto = 0;

        foreach (var row in file.Rows)
        {
            var orderId = DelimitedFile.Get(row, "order_id", "order-id", "ordine", "id", "numero ordine", "sales record number", "numero di registrazione vendita");
            if (string.IsNullOrWhiteSpace(orderId)) orderId = $"ROW{++auto}";

            if (!byOrder.TryGetValue(orderId, out var order))
            {
                order = new OutboundOrder { OrderId = orderId, Source = "csv" };
                var country = DelimitedFile.Get(row, "country", "iso_code", "nazione", "paese", "ship-country").ToUpperInvariant();
                if (string.IsNullOrWhiteSpace(country)) country = "IT";

                var street = DelimitedFile.Get(row, "address", "indirizzo", "via", "ship-address-1", "address 1");
                var number = DelimitedFile.Get(row, "building_number", "civico", "numero civico", "n");
                if (string.IsNullOrWhiteSpace(number))
                {
                    (street, number) = AddressParser.Split(street, DelimitedFile.Get(row, "address2", "ship-address-2", "address 2"));
                    if (number == "SNC") order.Warnings.Add("numero civico non trovato, uso 'SNC'");
                }

                var rawProv = DelimitedFile.Get(row, "province", "provincia", "state", "StateOrProvinceCode", "ship-state");
                var prov = country == "IT" ? ItalianProvinces.ToCode(rawProv) : rawProv;
                if (country == "IT" && prov is null) order.Warnings.Add($"provincia non riconosciuta: '{rawProv}'");

                order.Destination = new ShipmentAddress
                {
                    IsoCode = country,
                    PostalCode = DelimitedFile.Get(row, "postal_code", "cap", "zip", "postcode", "ship-postal-code"),
                    City = DelimitedFile.Get(row, "city", "citta", "città", "comune", "ship-city"),
                    HeaderName = DelimitedFile.Get(row, "name", "nome", "destinatario", "recipient", "recipient-name", "buyer name"),
                    Address = street,
                    BuildingNumber = number,
                    StateOrProvinceCode = prov ?? rawProv,
                    Phone = AddressParser.NormalizePhone(DelimitedFile.Get(row, "phone", "telefono", "tel", "buyer-phone-number")),
                    Email = DelimitedFile.Get(row, "email", "e-mail", "buyer-email"),
                    Note = DelimitedFile.Get(row, "note", "notes", "delivery-instructions"),
                };
                order.Notes = order.Destination.Note;
                byOrder[orderId] = order;
            }

            var qty = int.TryParse(DelimitedFile.Get(row, "quantity", "qty", "quantita", "quantità"), out var q) ? q : 1;
            var val = AddressParser.ParseDecimal(DelimitedFile.Get(row, "value", "valore", "price", "prezzo", "total"));
            order.Lines.Add(new OrderLine(
                DelimitedFile.Get(row, "sku", "codice", "code"),
                DelimitedFile.Get(row, "description", "descrizione", "product", "prodotto", "title"),
                qty, null, val));
            if (val.HasValue) order.GoodsValue = (order.GoodsValue ?? 0) + val.Value;

            var d1 = AddressParser.ParseDecimal(DelimitedFile.Get(row, "dim1", "length", "lunghezza", "l"));
            var d2 = AddressParser.ParseDecimal(DelimitedFile.Get(row, "dim2", "width", "larghezza", "w"));
            var d3 = AddressParser.ParseDecimal(DelimitedFile.Get(row, "dim3", "height", "altezza", "h"));
            var kg = AddressParser.ParseDecimal(DelimitedFile.Get(row, "weight", "peso", "kg"));
            if (d1.HasValue && d2.HasValue && d3.HasValue && kg.HasValue)
            {
                order.ExplicitParcels ??= new List<Parcel>();
                for (var i = 0; i < qty; i++) order.ExplicitParcels.Add(Parcel.Box(d1.Value, d2.Value, d3.Value, kg.Value));
            }
        }

        return byOrder.Values.ToList();
    }
}
