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
| `HeyDanis.Companion` | `CompanionManager` (zentrale Zustandsmaschine, deutsche System-Prompts), `SettingsStore` (JSON), `SecretsStore` (DPAPI), `TtsPipeline`, `UsageCostTracker` (Monatskosten) |
| `HeyDanis.Audio` | Mikrofon (NAudio/WASAPI), `PcmResampler` |
| `HeyDanis.Capture` | Multi-Monitor-Screenshots |
| `HeyDanis.Hotkey` | Low-Level-Keyboard-Hook, Push-to-Talk-Erkennung |
| `HeyDanis.Overlay` | Transparente Overlay-Fenster pro Monitor, `BuddyCursorControl`, Zeichnungen, DPI |
| `HeyDanis.Api` | `AnthropicDirectClient` (SSE, meldet Token-Usage), `ISpeechTranscriber`/`ITextToSpeechClient`, optionale Cloud-Clients AssemblyAI + ElevenLabs |
| `HeyDanis.LocalSpeech` | `WhisperLocalTranscriber` (Whisper.net), `PiperLocalSpeechClient` (sherpa-onnx), `WindowsVoiceSpeechClient`, `FallbackSpeechClient`, `LocalModelStore` (Downloads nach `%APPDATA%\heyDanis\models`) |
| `HeyDanis.Pointing` | `PointTagParser`, strukturierte Pointing-Antworten |

- LLM: Standard `claude-haiku-5-5`, optional `claude-sonnet-5-5`. Key des Nutzers, direkt an api.anthropic.com.
- Agent-Modus (geplant): Claude Code CLI (`claude -p`) mit dem Abo-Login des Nutzers.
  `ANTHROPIC_API_KEY` **muss** aus der Umgebung des Kindprozesses entfernt werden.
- Standard-Sprachkette: Whisper Small lokal → Claude Haiku 5.5 → Piper „Thorsten“ lokal.
- Hotkeys (`HeyDanisHotkeyEngine`): Strg+Alt halten = Sprechen, Strg+Alt+Leertaste = Textbox,
  Strg+Shift halten = Diktat. Nur **linke** Alt zählt (AltGr = Strg+Alt auf deutscher Tastatur!),
  weitere Taste während des Akkords → `ShortcutTransition.Cancelled`; injizierte Tasten werden ignoriert.
- Zeigen: Screenshots exakt nach Claudes Resize-Regel (`ClaudeImageSizing`), `oversized_image: "error"`,
  optional Zoom-Verfeinerung pro Punkt (`PrecisePointingEnabled`).
- Daten liegen unter `%APPDATA%\heyDanis` (settings.json, secrets.bin, usage.json, debug.log, models/).

## Build

Auf Windows: `dotnet build HeyDanis.sln` / `dotnet test HeyDanis.sln` / `.\run.cmd`.

Auf Linux (z. B. Cloud-Container) lässt sich nur **kompilieren**, nicht ausführen oder testen:

```bash
dotnet build HeyDanis.sln -p:EnableWindowsTargeting=true -c Release
```

Tests laufen in GitHub Actions (`.github/workflows/build.yml`, windows-latest). Dort
wird auch der App-Ordner mit `heyDanis.exe` als Artefakt gebaut (bewusst **kein**
Single-File: Whisper.net sucht seine nativen DLLs in `runtimes/` neben der Exe).

## Konventionen

- Sprechende, ausführliche Namen; Klarheit vor Kürze.
- Kommentare erklären das *Warum*.
- Nutzer-sichtbare Texte auf Deutsch. WPF-Projekte haben kein implizites `using System.IO;`.
- Default-Tests deterministisch halten (Fakes für LLM/Capture/Overlay), nichts, was einen
  echten Desktop braucht.
- Keine Keys, Modelle (`*.onnx`, `*.bin`) oder Build-Ausgaben committen.
- Kleine Commits, imperative englische Commit-Messages, die das Warum erklären.
