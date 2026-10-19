# Paccofacile API – istruzioni per Claude Code

Strumento per creare spedizioni su Paccofacile.it PRO (account 88456) dagli ordini Amazon e da altri canali.
L'utente spesso chiede: "crea la spedizione per questo indirizzo" (anche da screenshot). Procedura:

1. Estrai dall'indirizzo: nome/ragione sociale, via + civico, CAP, città, provincia, telefono (email facoltativa).
   Se manca il civico usa `--number SNC` solo come ultima risorsa; se manca il telefono chiedilo.
2. Collo: se l'utente non dà misure/peso, usa il default (30x20x15 cm, 1 kg) e dillo esplicitamente nel riepilogo.
3. Esegui (da qualsiasi cartella, `dist` è nel PATH utente; in alternativa `dotnet run --project src/Paccofacile.Cli --`):

   ```powershell
   paccofacile ship --name "..." --address "Via ... 12" --zip 00100 --city Roma --prov RM --phone 333... --email ... --parcel 30x20x15:1 --order <riferimento>
   ```

   Prima di creare, se c'è un dubbio sull'indirizzo, fai `--dry-run` (solo preventivo). Il tool normalizza CAP/città contro il
   database Paccofacile e rifiuta i CAP generici (es. 80100) elencando quelli validi: in quel caso chiedi il CAP esatto.
4. NON usare `--buy`: l'utente paga a mano sul sito (le spedizioni restano "da pagare" in Spedizioni configurate).
   Il pagamento via API funziona solo con credito e l'utente non lo vuole come default.
5. Riporta all'utente: shipment_id, corriere/servizio, prezzo, data ritiro, e il collo usato.

Regole di scelta corriere (già in `src/Paccofacile.Cli/appsettings.Local.json`, non cambiarle senza chiedere):
- Mittente: Assistenza Bs Planet, Via Vittorio Veneto 78, 52010 Capolona (AR), tel 0575420339.
- Priorità SDA nazionale a domicilio (`PreferredServiceIds: [2]`); se non disponibile il più economico a domicilio.
- Niente punti di ritiro/locker (`HomeDeliveryOnly: true`). `--service ID` forza un servizio specifico (vedi `paccofacile carriers`).

Altri comandi: `paccofacile check`, `carriers`, `quote`, `amazon --file <report Seller Central>`, `csv --file`, `tracking`,
`confirm --file <conferma amazon>`, `webhook-register --url`. Dettagli e tabella endpoint API nel README.md.

Credenziali e mittente stanno in `src/Paccofacile.Cli/appsettings.Local.json` (git-ignored): non stamparle e non committarle.
Dopo modifiche al codice: `dotnet build Paccofacileapi.slnx`, poi `dotnet publish src/Paccofacile.Cli/Paccofacile.Cli.csproj -c Release -r win-x64 --self-contained false -o dist` per aggiornare l'exe nel PATH.
