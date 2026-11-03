<p align="center"><img src="docs/icon.png" width="96" alt="heyDanis Icon"></p>

# heyDanis

Dein persönlicher KI-Buddy für Windows. Halte **Strg + Alt** gedrückt, stell eine Frage,
und heyDanis sieht deinen Bildschirm, antwortet mit Stimme und zeigt mit einem eigenen
Cursor direkt auf die Stelle, die du suchst.

> Status: **in Entwicklung** – Fortschritt und offene Punkte stehen in [NOTES.md](NOTES.md).

## Funktionen (Ziel)

| Funktion | Bedienung |
|---|---|
| Frage per Stimme + Bildschirm | **Strg + Alt** halten, sprechen, loslassen |
| Frage per Text | **Strg + Alt + Leertaste**, tippen, Enter |
| Diktat ins aktive Textfeld | **Strg + Shift** halten, sprechen, loslassen |
| Hintergrund-Agent | „**hey agent danis**, …“ sagen |
| Zeigen auf UI-Elemente | automatisch, auch über mehrere Monitore („Genauer zeigen“ mit Zoom) |
| Markieren | „Kreis mir die Fehler ein“ → Kreise/Pfeile auf dem Bildschirm |

## Kosten

heyDanis ist auf möglichst geringe Kosten ausgelegt:

- **Fragen:** Claude Haiku 5.5 über deinen eigenen Anthropic-API-Key – ca. 0,07 Cent pro Frage.
- **Agent-Modus:** läuft über deine installierte Claude-Code-CLI und dein Claude-Abo.
- **Sprache rein/raus:** lokal auf deinem PC (Whisper / Piper) – kostenlos.

## Installation (für Nutzer)

1. Auf GitHub unter **Actions → Build heyDanis →** neuester grüner Lauf das Artefakt
   **heyDanis-win-x64** herunterladen und in einen festen Ordner entpacken
   (z. B. `C:\Users\<du>\heyDanis`). Den Ordner nicht auseinanderreißen –
   die Spracherkennung braucht die Dateien neben der Exe.
2. `heyDanis.exe` starten. Windows SmartScreen warnt bei unsignierten Programmen:
   **Weitere Informationen → Trotzdem ausführen**.
3. Im Einstellungsfenster deinen Anthropic-API-Key eintragen. Beim ersten Start lädt
   heyDanis die Sprachmodelle (~260 MB) einmalig herunter – der Fortschritt steht im
   Hauptfenster.

### Updates (klein, ~1 MB)

Für Updates reicht meistens das Artefakt **heyDanis-UPDATE-klein**: heyDanis beenden,
ZIP herunterladen und **über den bestehenden heyDanis-Ordner** entpacken (Dateien
ersetzen). Das große Paket **heyDanis-win-x64** brauchst du nur beim ersten Mal – oder
wenn in den Release-Notizen steht, dass sich Bibliotheken geändert haben.
Die Sprachmodelle in `%APPDATA%\heyDanis\models` werden nie erneut geladen.

API-Keys werden mit Windows DPAPI verschlüsselt unter `%APPDATA%\heyDanis` gespeichert
und nie hochgeladen.

## Entwicklung

Voraussetzung: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
.\run.cmd                         # App aus dem Quellcode starten
dotnet build HeyDanis.sln         # bauen
dotnet test HeyDanis.sln          # Tests
.\scripts\release.ps1             # App-Ordner (+ Installer, falls Inno Setup installiert)
```

Projektaufbau:

```text
src/
  HeyDanis.App/        WPF-Host: Tray-Icon, Fenster, Einstellungen
  HeyDanis.Companion/  Zustandsmaschine (Push-to-Talk → Screenshot → LLM → Stimme), Settings, Secrets
  HeyDanis.Audio/      Mikrofonaufnahme, Resampling
  HeyDanis.Capture/    Screenshots aller Monitore
  HeyDanis.Hotkey/     globaler Tastatur-Hook
  HeyDanis.Overlay/    transparentes Overlay mit Buddy-Cursor und Markierungen
  HeyDanis.Api/        Claude-Client, Schnittstellen für Sprache, AssemblyAI/ElevenLabs (optional)
  HeyDanis.LocalSpeech/ Whisper (Spracherkennung) und Piper (Stimme), lokal & gratis
  HeyDanis.Pointing/   Parser für [POINT:…]-Tags
tests/HeyDanis.Tests/
```

## Herkunft & Lizenz

heyDanis basiert auf [Clicky](https://github.com/farzaa/clicky) von Farza und dem
Windows-Port von [rickkdev/clicky](https://github.com/rickkdev/clicky). Beide stehen
unter der MIT-Lizenz; der Lizenzhinweis ist in [LICENSE](LICENSE) erhalten.
heyDanis ist ein eigenständiges, privates Projekt und nicht mit Clicky verbunden.
