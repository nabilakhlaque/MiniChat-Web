# MiniChat

En liten krypterad chatt för familjen med Skype-lik premiumdesign. Ingen server, inget konto, ingen installation.
En enda `MiniChat.exe` som ligger vid klockan.

- WinForms, .NET Framework 4.8 (finns redan i Windows 10/11)
- Mjuk, rundad design: bubblor med skuggor, profilbilder, färgemojis, SVG-ikoner och små animationer
- Skriv **\*prutt\*** eller **\*skratt\*** så spelas ljudet upp hos de andra – syns inte i chatten. Egna `.wav`/`.mp3` i mappen `ljud\` bredvid exe:n blir `*filnamn*` (max 3 MB, spelas hos de andra)
- Status: Tillgänglig / Upptagen (tyst, utom prutt/skratt/knackningar) och automatiskt Ej vid datorn efter 10 min
- Historik 30 dagar (krypterad med Windows DPAPI), bilder som miniatyrer i bubblan, "skriver…", knacka, reaktioner och "Sett"
- Filer: dra in i fönstret, tryck **+** eller klistra in med **Ctrl+V** (även bilder från urklipp)
- Alltid krypterat: varje förbindelse gör ett ECDH-nyckelutbyte (P-256) och all trafik går som AES-256 + HMAC
- Systemfältsikon som **blinkar** tills du sett nya meddelanden, diskreta aviseringar, ljud på/av
- Autostart som val, inga administratörsrättigheter (ingen UAC, autostart via HKCU)
- Namnet du skriver första gången är ditt användarnamn och sparas. Byt under ⚙ → *Byt namn…*
- Peer-to-peer: datorerna pratar direkt med varandra, och vidarebefordrar åt varandra om de är fler än två

## Bygga

**Alternativ 1 – inget behöver installeras.** Dubbelklicka på `build.cmd`.
Den använder C#-kompilatorn som redan följer med Windows (`csc.exe` i .NET Framework) och lägger
`MiniChat.exe` i samma mapp. Koden är skriven i C# 5 just för att den kompilatorn ska räcka.

**Alternativ 2 – Visual Studio / .NET SDK.** `dotnet build -c Release` → `bin\Release\net48\MiniChat.exe`.

Kopiera sedan `MiniChat.exe` dit du vill ha den. Det är allt.

> Första gången du startar en okänd exe kan Windows SmartScreen varna: *Mer information → Kör ändå*.

## Använda

1. Starta `MiniChat.exe` och skriv ditt namn. Klart – du är inne.
2. Gör samma sak på den andra datorn. På samma nätverk hittar ni varandra inom några sekunder.
3. På olika nätverk: ⚙ → **Anslut till adress…** och skriv den andras IP (se nedan).
4. Enter skickar, Shift+Enter ger ny rad. Smiley-knappen öppnar Windows emojipanel (samma som Win + .).
   Skriv `*prutt*` eller `*skratt*` (ensamt eller mitt i en mening) så hörs ljudet hos de andra utan att synas.
5. Klicka på din bild uppe till vänster (eller ⚙ → *Välj profilbild…*) för att sätta en profilbild.
6. Krysset gömmer fönstret till systemfältet. **Avsluta** finns i högerklicksmenyn på ikonen.

Mottagna filer hamnar i `Hämtade filer\MiniChat` (⚙ → *Mottagna filer*). Bilder visas direkt i bubblan.
Håll musen över en bubbla för att reagera med en emoji. Klockan uppe till höger "knackar" – fönstret skakar
och plockas fram hos de andra. Historiken (30 dagar) ligger krypterad i `%APPDATA%\MiniChat\history.dat`.
Som standard tas filer emot automatiskt; stäng av under ⚙ så får du istället *Ta emot / Avböj* i bubblan.

Inställningar sparas i `%APPDATA%\MiniChat\settings.txt` (profilbilden som `avatar.jpg` bredvid).
Autostart läggs i `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` och startar programmet dolt (`--tray`).

## Så hittar datorerna varandra

| Läge | Hur |
|---|---|
| Samma nätverk | UDP-broadcast på port **48556**. Automatiskt. |
| Olika nätverk | ⚙ → *Anslut till adress…* med den andras IP (`192.0.2.10` eller `hem.exempel.se:443`). Sparas och återansluts automatiskt. |
| Flera deltagare | Alla kopplar ihop sig med alla och vidarebefordrar meddelanden och filer, så två som inte når varandra direkt når varandra via en tredje. |

Standard-TCP-port är **48555** (⚙ → *Lyssnarport…*).

### Enklast över internet: familjekod (molnbrygga)

⚙ → **Familjekod (internet)…** – skriv samma kod på alla era datorer (går även att fylla i på välkomstskärmen).
När ingen direkt förbindelse finns skickas meddelanden, ljud, reaktioner och filer (≤ 5 MB) som krypterade
paket via en publik [ntfy](https://ntfy.sh)-server över HTTPS på port 443 – samma port som vanlig webb, så det
går igenom de flesta skol- och jobbnät, även via proxy. Nyckeln härleds ur koden på era datorer; servern ser
bara krypterade blobbar. Meddelanden som skickas när den andra är offline ligger kvar hos servern i ca 12 timmar
och levereras när den kommer in. På samma nät används fortfarande den direkta vägen, och molnet står tyst.

Begränsningar: ntfy.sh:s gratisnivå (ungefär 250 meddelanden per dag och dator, filer upp till några MB) och att
en tredje part transporterar trafiken. ⚙ → *Molnserver…* pekar om till en egen ntfy-server om ni vill slippa både.

### iPhone, Android och andra datorer: webbversionen

Mappen `web/` innehåller samma chatt som webbapp. Lägg upp den på en https-adress (GitHub Pages, gratis),
öppna i Safari och välj *Lägg till på hemskärmen*. Den använder samma familjekod och samma kryptering som
molnbryggan, så datorn och telefonen chattar med varandra direkt. Se `web/README.md`.

### Skola ↔ arbetsplats utan mellanhand – var ärlig med dig själv här

Två datorer som båda sitter bakom en skolas respektive en arbetsgivares brandvägg kan **inte** ringa upp
varandra direkt – ingen av dem går att nå utifrån. Peer-to-peer kräver att minst en ände är nåbar.
Tre sätt som funkar utan tredjepartsserver:

1. **En tredje MiniChat hemma (rekommenderat).** Låt en dator hemma köra MiniChat dold vid klockan.
   Vidarebefordra i hemroutern port **443 → hemdatorn:48555** och skriv in `din-hem-ip:443` (eller ett
   DynDNS-namn) under *Anslut till adress…* på både jobb- och skoldatorn. Hemdatorn blir länken – det är
   fortfarande bara samma lilla program, ingen server. Port 443 väljs för att den nästan alltid släpps ut.
2. **Nätverksöverlagring (Tailscale, ZeroTier).** Ger datorerna privata IP:n som alltid når varandra.
   Kräver att du får installera det på båda datorerna – ofta inte möjligt på en skoldator.
3. **Port framåt direkt till din jobbdator** – bara om IT tillåter det, vilket de sällan gör.

Obs: vissa skolnät släpper bara ut riktig HTTPS på 443 (de inspekterar trafiken). Då stoppas MiniChat,
och då stoppas i praktiken allt som inte är webb. Testa helt enkelt.

### Brandväggen i Windows

Första gången frågar Windows-brandväggen om MiniChat får ta emot trafik. Svara **Tillåt** om du kan.
Kan du inte (standardanvändare utan admin, typiskt skoldator) fungerar det ändå så länge *den andra*
sidan kan ta emot: den brandväggade datorn är alltid den som ringer upp. Hemdatorn i alternativ 1 ovan
måste alltså tillåta inkommande trafik.

## Säkerhet, kort

- Varje TCP-förbindelse förhandlar en egen nyckel med ECDH (P-256). Ingen kod att dela, inget att komma ihåg.
- Trafiken skyddas med AES-256-CBC + HMAC-SHA256. Filer går samma väg som text.
- Det finns ingen identitetskontroll av den andra parten (ingen certifikatkedja), så någon som sitter
  *mitt i* förbindelsen vid uppkopplingen kan i teorin byta ut nycklarna. Mot avlyssning av skol-/jobbnät
  räcker det gott; mot en riktad attack är det inte Signal.
- Vem som helst med MiniChat på samma lokala nät kan dyka upp i chatten – det är så "inget konto" fungerar.
  Du ser alla som är online i raden överst och kan stänga av automatisk filmottagning.

## Resursanvändning

Händelsestyrt (asynkron I/O). Det som tickar är ett UDP-paket var tredje sekund på LAN och en ping var
15:e sekund per kompis. I vila 0 % CPU. Gränssnittet ritas av Windows inbyggda webbkomponent i IE11-läge
(det är den som ger färgemojis, rundade hörn, SVG-ikoner och animationer), vilket kostar ca 40–60 MB RAM
totalt – fortfarande en bråkdel av Teams.

## Testa på en dator

Starta en andra instans med `MiniChat.exe --multi` (normalt tillåts bara en instans per användare;
en andra start öppnar bara fönstret på den första). Båda använder samma sparade namn, det är ok för test.

## Filer

| Fil | Innehåll |
|---|---|
| `Program.cs` | Start, en-instans-kontroll, `--tray` / `--multi`, IE11-läge för webbkomponenten |
| `MainForm.cs` | Tunt WinForms-skal: webbvyn, systemfältsikon (blink), aviseringar, ljud, filer, dialoger, brygga till app.html |
| `app.html` | Hela gränssnittet: välkomstvy, header, roster, bubblor, composer, meny, modaler, animationer. Bäddas in i exe-filen |
| `Network.cs` | UDP-upptäckt, TCP-mesh, ECDH + AES-ramar, roster, filöverföring med vidarebefordran |
| `Settings.cs` | settings.txt (inkl. beständigt id och vald status), avatar.jpg, autostart, IE11-registernyckel |
| `History.cs` | Lokal historik, varje post DPAPI-krypterad, 30 dagar / 2000 poster |
| `CloudBridge.cs` | Molnbryggan: ntfy över HTTPS, krypterade ramar och bilagor, återanslutning, 12 h brevlåda |
| `web/` | Webbappen för iPhone/Android/webbläsare: `index.html`, `manifest.json`, `sw.js`, ikoner, egen README |
| `app.manifest` | asInvoker (ingen UAC), DPI-medveten |
| `app.ico`, `alert.ico`, `ding.wav`, `prutt.wav`, `skratt.wav`, `knock.wav` | ikoner och ljud, bäddas in |
| `build.cmd`, `MiniChat.csproj` | de två sätten att bygga |
