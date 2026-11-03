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
| Frage per Text | **Strg + Alt + Leertaste** |
| Diktat ins aktive Textfeld | **Strg + Shift** halten |
| Hintergrund-Agent | „**hey agent danis**, …“ sagen |
| Zeigen auf UI-Elemente | automatisch, auch über mehrere Monitore |

## Kosten

heyDanis ist auf möglichst geringe Kosten ausgelegt:

- **Fragen:** Claude Haiku 5.5 über deinen eigenen Anthropic-API-Key – ca. 0,07 Cent pro Frage.
- **Agent-Modus:** läuft über deine installierte Claude-Code-CLI und dein Claude-Abo.
- **Sprache rein/raus:** lokal auf deinem PC (Whisper / Piper) – kostenlos. *(ab Meilenstein 3)*

## Installation (für Nutzer)

1. Auf GitHub unter **Actions → Build heyDanis →** neuester grüner Lauf das Artefakt
   **heyDanis-win-x64** herunterladen und entpacken.
2. `heyDanis.exe` starten. Windows SmartScreen warnt bei unsignierten Programmen:
   **Weitere Informationen → Trotzdem ausführen**.
3. Im Einstellungsfenster deinen Anthropic-API-Key eintragen.

API-Keys werden mit Windows DPAPI verschlüsselt unter `%APPDATA%\heyDanis` gespeichert
und nie hochgeladen.

## Entwicklung

Voraussetzung: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
.\run.cmd                         # App aus dem Quellcode starten
dotnet build HeyDanis.sln         # bauen
dotnet test HeyDanis.sln          # Tests
.\scripts\release.ps1             # Single-File-Exe (+ Installer, falls Inno Setup installiert)
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
  HeyDanis.Api/        Claude-, Spracherkennungs- und Stimmen-Clients
  HeyDanis.Pointing/   Parser für [POINT:…]-Tags
tests/HeyDanis.Tests/
```

## Herkunft & Lizenz

heyDanis basiert auf [Clicky](https://github.com/farzaa/clicky) von Farza und dem
Windows-Port von [rickkdev/clicky](https://github.com/rickkdev/clicky). Beide stehen
unter der MIT-Lizenz; der Lizenzhinweis ist in [LICENSE](LICENSE) erhalten.
heyDanis ist ein eigenständiges, privates Projekt und nicht mit Clicky verbunden.
