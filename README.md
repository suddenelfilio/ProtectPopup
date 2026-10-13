# ProtectPopup

Toont een klein videovenster rechtsboven op je Mac wanneer UniFi Protect meldt dat
er wordt aangebeld of dat een camera een persoon, voertuig of pakket ziet. Het
venster sluit je met de rode knop, of het verdwijnt vanzelf na 60 seconden.

Een .NET-consoleapp luistert via de officiele Protect Integration API naar events
en opent per camera een `mpv`-venster met de live RTSPS-stream.

## Vereisten

- UniFi Protect 5.3 of nieuwer (Integration API)
- .NET SDK 8 of nieuwer
- mpv: `brew install mpv`

## Installatie

1. Maak in Protect een API-key: Settings > Control Plane > Integrations.
2. Zet de configuratie klaar:

   ```sh
   mkdir -p ~/.config/protect-popup
   cp config.example.json ~/.config/protect-popup/config.json
   ```

   Vul `host` en `apiKey` in.
3. Kies de camera's (macOS-keuzelijst, Cmd-klik voor meerdere; alles aanvinken = alle camera's):

   ```sh
   dotnet run -- --cameras
   ```

4. Test de video (opent meteen een popup van de eerste camera):

   ```sh
   dotnet run -- --test
   ```

5. Laat het daarna gewoon draaien met `dotnet run` en bel aan.

## Automatisch starten bij aanmelden

```sh
dotnet publish -c Release -r osx-arm64 --self-contained false -o ~/Applications/protect-popup
# Intel-Mac: -r osx-x64

# Pas JOUWNAAM aan in het plist-bestand, daarna:
cp be.visuasoft.protect-popup.plist ~/Library/LaunchAgents/
launchctl load ~/Library/LaunchAgents/be.visuasoft.protect-popup.plist
```

Log: `/tmp/protect-popup.log`. Stoppen: `launchctl unload ~/Library/LaunchAgents/be.visuasoft.protect-popup.plist`.

## Gedrag

- Een event opent hoogstens een popup; komt er een nieuw event van dezelfde camera
  terwijl het venster openstaat, dan begint de minuut opnieuw.
- Meerdere camera's tegelijk worden onder elkaar gestapeld.
- Bij het openen knippert er 5 s een rode rand rond het beeld en speelt een geluid (`flashSeconds`, `flashColor`, `sound`). Grootte via `popupWidth`.
- Bij verbindingsverlies wordt automatisch opnieuw verbonden.
- De app maakt de RTSPS-stream (kwaliteit uit `quality`) zelf aan als die nog niet
  aanstaat op de camera.

## Problemen

- Geen popup bij een event: zet `logAllEvents` op `true` en kijk welk `type` en welke
  `smartDetectTypes` je console doorstuurt.
- mpv opent en sluit meteen: test de url uit het log los met `mpv <url>`.
- HTTP 401: de API-key klopt niet of is ingetrokken.
- "No route to host" na installatie: sta `protect-popup` toe bij Systeeminstellingen > Privacy en beveiliging > Lokaal netwerk.
