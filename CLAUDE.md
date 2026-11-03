# heyDanis – Anweisungen für KI-Coding-Agenten

## Überblick

Windows-Tray-App (C#/.NET 8, WPF), ein persönlicher KI-Buddy (Klon von HeyClicky).
Push-to-Talk-Hotkey → Mikrofon → Transkript → Screenshot aller Monitore → Claude (Vision)
→ gestreamte Antwort → Vorlesen (TTS) und Sprechblase am Cursor → optional fliegt der
Buddy-Cursor zu `[POINT:x,y:label:screenN]`-Zielen.

Basis ist der Windows-Port https://github.com/rickkdev/clicky (MIT). Codex/z.ai/OpenAI,
PostHog-Analytics, der WinSparkle-Updater und die Hermes-Bridge wurden entfernt.

**Lies immer zuerst `NOTES.md`.** Dort stehen Stand, Entscheidungen, offene Punkte und
Meilensteine. Halte sie nach jedem Meilenstein aktuell.

## Nutzer

- Schüler (HTL), kein Profi-Entwickler → Erklärungen auf Deutsch, kurz und konkret.
- Bedienung und Antworten der App: Deutsch, Fachbegriffe Englisch.
- **Kosten minimieren hat höchste Priorität.** Vor allem, was API-Kosten verursacht, fragen.

## Architektur

| Projekt | Zweck |
|---|---|
| `HeyDanis.App` | WPF-Host, `App.xaml.cs` verdrahtet alles; Tray (`TrayIconManager`), Fenster, `SettingsWindow` |
| `HeyDanis.Companion` | `CompanionManager` (zentrale Zustandsmaschine, System-Prompts), `SettingsStore` (JSON), `SecretsStore` (DPAPI), `TtsPipeline` |
| `HeyDanis.Audio` | Mikrofon (NAudio/WASAPI), `PcmResampler` |
| `HeyDanis.Capture` | Multi-Monitor-Screenshots |
| `HeyDanis.Hotkey` | Low-Level-Keyboard-Hook, Push-to-Talk-Erkennung |
| `HeyDanis.Overlay` | Transparente Overlay-Fenster pro Monitor, `BuddyCursorControl`, Zeichnungen, DPI |
| `HeyDanis.Api` | `AnthropicDirectClient` (SSE), AssemblyAI-STT, ElevenLabs-TTS (werden in M3 durch lokale Whisper/Piper ersetzt bzw. optional) |
| `HeyDanis.Pointing` | `PointTagParser`, strukturierte Pointing-Antworten |

- LLM: Standard `claude-haiku-5-5`, optional `claude-sonnet-5-5`. Key des Nutzers, direkt an api.anthropic.com.
- Agent-Modus (geplant): Claude Code CLI (`claude -p`) mit dem Abo-Login des Nutzers.
  `ANTHROPIC_API_KEY` **muss** aus der Umgebung des Kindprozesses entfernt werden.
- Daten liegen unter `%APPDATA%\heyDanis` (settings.json, secrets.bin, debug.log).

## Build

Auf Windows: `dotnet build HeyDanis.sln` / `dotnet test HeyDanis.sln` / `.\run.cmd`.

Auf Linux (z. B. Cloud-Container) lässt sich nur **kompilieren**, nicht ausführen oder testen:

```bash
dotnet build HeyDanis.sln -p:EnableWindowsTargeting=true -c Release
```

Tests laufen in GitHub Actions (`.github/workflows/build.yml`, windows-latest). Dort
wird auch die Single-File-`heyDanis.exe` als Artefakt gebaut.

## Konventionen

- Sprechende, ausführliche Namen; Klarheit vor Kürze.
- Kommentare erklären das *Warum*.
- Nutzer-sichtbare Texte auf Deutsch.
- Default-Tests deterministisch halten (Fakes für LLM/Capture/Overlay), nichts, was einen
  echten Desktop braucht.
- Keine Keys, Modelle (`*.onnx`, `*.bin`) oder Build-Ausgaben committen.
- Kleine Commits, imperative englische Commit-Messages, die das Warum erklären.
