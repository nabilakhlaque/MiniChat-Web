# MiniChat för iPhone (och alla andra webbläsare)

Samma chatt som MiniChat på datorn, men som webbapp. Den pratar med samma molnbrygga
(ntfy över HTTPS) med samma familjekod och samma kryptering, så datorn och telefonen
förstår varandra direkt. Inget App Store, inget konto.

## Så publiceras den

Det här repot publiceras automatiskt på GitHub Pages av `.github/workflows/pages.yml` vid varje push till `main`
(flödet slår på Pages självt första gången och genererar ikonerna och de syntetiska ljuden med Python).
Adressen är `https://nabilakhlaque.github.io/MiniChat-Web/`.

Två ljud är egna inspelningar och ligger som binärfiler i `sounds/`: `prutt.wav` och `skratt.wav`. Saknas de
är appen tyst för just de två – allt annat fungerar.

## Installera på iPhone

1. Öppna adressen i **Safari** (måste vara Safari, inte Chrome).
2. Tryck på dela-knappen (fyrkanten med pilen) → **Lägg till på hemskärmen** → Lägg till.
3. Öppna MiniChat från hemskärmen, skriv namn och familjekod. Klart.

Samma sak fungerar på Android (Chrome: meny → *Lägg till på startskärmen*) och på vilken dator som helst.

## Vad som är annorlunda mot Windows-appen

- Går alltid via molnbryggan, även hemma (en webbsida kan inte prata direkt med datorn på lokala nätet).
- Inga notiser när appen är stängd – iOS stänger webbappar i bakgrunden. Nya meddelanden syns när du öppnar
  den; det som skickats medan du var borta hämtas från servern (12 timmar bakåt).
- Ljud (prutt, skratt, knack) spelas efter att du tryckt någonstans i appen första gången – det är en iOS-regel.
- Reaktioner: håll fingret på en bubbla. Knacka: fönstret skakar och blinkar.
- Filer: skicka från Bilder/Filer (max 5 MB via molnet). Mottagna filer öppnas/sparas via knappen i bubblan
  och finns kvar så länge appen är öppen; bilder sparas som miniatyr i historiken.
- Historiken sparas i Safari på telefonen (30 dagar).

## Uppdatera

Byt ut `index.html` i repot. Appen hämtar den nya versionen nästa gång den öppnas med nät
(service workern cachar bara för att kunna öppna appen utan nät).
