# Paccofacile API – automazione spedizioni

Strumento .NET 8 per creare e pagare spedizioni su **Paccofacile.it PRO** senza passare dal sito,
partendo dagli ordini Amazon (report Seller Central) o da un CSV qualsiasi (eBay, gestionale, Excel).

```
src/Paccofacile.Client   libreria: client tipizzato dell'API REST Paccofacile
src/Paccofacile.Cli      eseguibile `paccofacile` a riga di comando
samples/                 file ordini di esempio (Amazon e CSV generico)
```

## Setup

1. `src/Paccofacile.Cli/appsettings.Local.json` (ignorato da git) contiene Token, Api Key, Account Number
   e l'indirizzo mittente. Le chiavi si generano su paccofacile.it > Integrazioni > API Paccofacile.it > API KEY LIVE.
   In alternativa: variabili d'ambiente `PACCOFACILE_TOKEN`, `PACCOFACILE_API_KEY`, `PACCOFACILE_ACCOUNT_NUMBER`.
2. In `appsettings.json` imposta `DefaultParcel` (collo usato quando non si sa altro) e, per ogni SKU che vendi,
   le misure in `SkuParcels` (un collo per unità). Così dal report Amazon, che non ha misure, escono preventivi giusti.
3. Opzionale: `PreferredServiceIds: [2]` = ordine di preferenza dei servizi (vedi `paccofacile carriers`; 2 = SDA nazionale a domicilio):
   il primo disponibile per la destinazione vince, altrimenti viene scelto il **più economico a domicilio**. `HomeDeliveryOnly: true` (default) scarta i servizi
   "POINT"/locker (InPost point-to-point, Poste PDB home-point…); `ExcludedServiceIds` esclude altri service_id.
   `--service ID` sulla riga di comando ignora i filtri.

```powershell
cd src\Paccofacile.Cli
dotnet build
dotnet run -- check          # credenziali, credito, servizi attivi, mittente
```

Eseguibile pronto (senza `dotnet run`): `dotnet publish -c Release -r win-x64 --self-contained false -o ..\..\dist`
produce `dist\paccofacile.exe`; usa `paccofacile.exe check`, `paccofacile.exe amazon --file ...` ecc.
La cartella `dist` contiene anche `appsettings.Local.json` con le credenziali: non condividerla.

## Flusso Amazon (senza SP-API)

1. Seller Central > Ordini > **Report ordini** > scarica "Ordini non spediti" (file .txt tab-delimited).
2. `dotnet run -- amazon --file "C:\...\ordini-non-spediti.txt" --dry-run` → mostra corriere e prezzo per ogni ordine, non crea nulla.
3. `dotnet run -- amazon --file "..." ` → crea le spedizioni (restano "da pagare" nell'area riservata).
4. `dotnet run -- amazon --file "..." --buy` → crea **e paga** con il credito (serve credito sul conto).
5. Viene scritto `amazon-conferma-spedizione-<data>.txt`: caricalo su Seller Central > Ordini > Carica file ordini
   > **Conferma spedizione**. Se la colonna `tracking-number` è vuota (il corriere lo assegna dopo l'etichetta),
   compilala dall'area riservata Paccofacile prima del caricamento.

Opzioni utili: `--only 403-...,408-...` (solo alcuni ordini), `--service 108` (forza GLS Express),
`--parcel 30x20x15:1.2` (collo unico per tutti), `--verbose` (log HTTP completo).

## Altri canali (CSV)

```powershell
dotnet run -- csv --file ..\..\samples\ordini-generici.esempio.csv --dry-run
```

Colonne riconosciute (nomi case-insensitive, separatore `;` `,` o TAB):
`order_id, name, address, building_number, postal_code, city, province, country, phone, email, note,
sku, description, quantity, value, dim1, dim2, dim3, weight`. Righe con lo stesso `order_id` = una spedizione.
Se `dim1..3`+`weight` sono presenti si usa quel collo, altrimenti `SkuParcels`/`DefaultParcel`.

## Spedizione singola e altri comandi

```powershell
dotnet run -- quote --zip 20121 --city Milano --prov MI --parcel 30x20x15:1.2
dotnet run -- ship  --name "Mario Rossi" --address "Via Verdi 12" --zip 20121 --city Milano --prov MI --phone 3331234567 --email m@r.it --buy
dotnet run -- buy   --shipments 123456,123457
dotnet run -- raw   --endpoint customers/credit
dotnet run -- raw   --method POST --endpoint shipment/quote --body quote.json
```

Ogni spedizione creata viene aggiunta a `shipments-log.csv` (ordine, servizio, prezzo, shipment_id, ordine Paccofacile, tracking).

## API Paccofacile (ricavata dal plugin ufficiale WooCommerce e dalla libreria Perl Net::PaccoFacile)

Base URL: `https://paccofacile.tecnosogima.cloud/live/v1/service/` (sandbox: `.../sandbox/v1/service/`).
Header su ogni chiamata: `Authorization: Bearer <token>`, `api-key: <api key>`, `Account-Number: <n>`, `Content-Type: application/json`.
Risposta sempre `{ "header": { "status", "notification": { "code", "messages" } }, "data": ... }`.

| Metodo | Endpoint | Scopo |
|---|---|---|
| POST | `key_validation` | verifica credenziali, registra opzionalmente gli URL webhook (`endpoint_push_tracking`, `endpoint_push_documents`) |
| GET | `carriers` | servizi attivi (`service_id`, `carrier_name`, `service_name`, `carrier_ship_time`) |
| GET | `customers/credit` | credito residuo |
| GET | `billing-address` | anagrafiche di fatturazione (`address_id`, `is_default`) |
| GET | `address-book` | rubrica (categorie `DEPARTURE` / `ARRIVAL`) |
| POST | `shipment/quote?field_pickup_date=1` | preventivo: `services_available[]` con `price_total.amount`, `pickup_date.first_date` / `first_date_range`, `customes_required` |
| POST | `shipment/save` | crea la spedizione → `data.shipment.shipment_id` |
| POST | `shipment/buy` | paga: `{ shipments:[id], fattura:"1", data_fattura:"", address_id_select:"", payment_method:"CREDIT" }` → `data.order.order_id` |
| GET/POST/PUT/DELETE | `packaging/list`, `packaging/list_types`, `packaging/{id}` | imballi salvati |
| POST | `bin-packing` | ottimizzazione colli (`articolo_list`) |
| POST | `locality/validation`, `locality/search` | validazione CAP/città |
| GET | `lockers`, `lockers/{id}` | locker InPost/Poste |

Tracking e lettere di vettura (PDF) arrivano **in push** agli URL registrati con `key_validation`
(payload `shipment.tracking.elenco.checkpoints[]`, e `{ order_id, id_waybill, waybill: "data:application/pdf;base64,..." }`).
Il tipo collo `shipment_type`: 1 pacco, 2 busta, 5 pallet. Dimensioni in cm, peso in kg.

## Webhook tracking e documenti (`src/Paccofacile.Webhook`)

Servizio ASP.NET Core che riceve i push di Paccofacile e li salva in `data/`:

```
data/tracking/<ordine paccofacile>.json   stato, numero tracking, checkpoint, riferimento ordine esterno
data/documents/<ordine>_<id>.pdf           lettere di vettura
data/raw/<timestamp>_<tipo>.json           ogni push così com'è arrivato (per debug)
```

Endpoint (il `secret` nel percorso fa da autenticazione, Paccofacile non firma i push):

| Metodo | Percorso | Uso |
|---|---|---|
| POST | `/hooks/{secret}/order_update` | registrato come `endpoint_push_tracking` |
| POST | `/hooks/{secret}/order_documents/{order_id}` | registrato come `endpoint_push_documents` |
| GET | `/hooks/{secret}/tracking` e `/tracking/{id}` | consultazione |
| GET | `/hooks/{secret}/documents/{id}/{file.pdf}` | download LDV |

Setup:

1. `src/Paccofacile.Webhook/appsettings.Local.json` (git-ignored) contiene `Webhook.Secret` (già generato) e le credenziali.
   Lo stesso secret è in `src/Paccofacile.Cli/appsettings.Local.json` insieme a `Webhook.DataDir` (cartella letta dalla CLI).
2. Avvio: `dotnet run --project src\Paccofacile.Webhook` (ascolta su `http://0.0.0.0:5055`, cambia `Urls` in appsettings).
   Per girare come servizio Windows: `dotnet publish -c Release -o C:\paccofacile-webhook` e `sc create` / NSSM.
3. Esposizione pubblica in **https** (obbligatoria per ricevere i push): reverse proxy (IIS/Caddy/nginx) sul server,
   oppure un tunnel tipo `cloudflared tunnel --url http://localhost:5055` / `ngrok http 5055` per provare.
   Il "quick tunnel" di cloudflared genera un URL `*.trycloudflare.com` diverso a ogni avvio: ogni volta va rifatto
   `paccofacile webhook-register --url <nuovo url>`. Per un URL stabile usa un tunnel con nome sul tuo dominio Cloudflare:
   `cloudflared tunnel login`, `cloudflared tunnel create paccofacile`, `cloudflared tunnel route dns paccofacile hooks.tuodominio.it`,
   poi `cloudflared tunnel run --url http://localhost:5055 paccofacile` (installabile come servizio con `cloudflared service install`).
4. Registrazione su Paccofacile: `paccofacile webhook-register --url https://hooks.miodominio.it`
   (oppure imposta `Webhook.PublicBaseUrl` nel webhook: si registra da solo a ogni avvio).
5. Da quel momento: `paccofacile tracking` mostra gli aggiornamenti; `paccofacile confirm --file <conferma amazon>.txt`
   completa la colonna `tracking-number` del file di conferma, pronto per Seller Central.

Test locale senza Paccofacile: `curl -X POST http://localhost:5055/hooks/<secret>/order_update -H "Content-Type: application/json" -d @samples/webhook-order_update.esempio.json`.
