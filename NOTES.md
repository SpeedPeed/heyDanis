# heyDanis – Projektnotizen

Persönlicher KI-Buddy für Windows (Klon von HeyClicky). Basis: Windows-Fork
https://github.com/rickkdev/clicky (MIT, ursprünglich https://github.com/farzaa/clicky).

## Stand
- Phase 0 erledigt (Ansprüche geklärt).
- Phase 1: Architektur erklärt, Plan vorgelegt. **Wartet auf OK.**

## Ansprüche (vom Nutzer)
- OS: Windows
- Sprache: Deutsch, Fachbegriffe auf Englisch
- Persönlichkeit: locker und kurz
- Nutzung: Schule (HTL), Programmieren, evtl. CAD und Spiele bauen
- Stimme: lokal/gratis, männlich
- STT: Claude entscheidet → Whisper lokal
- LLM: so billig wie möglich; Claude-Team-Konto nutzen, wo möglich; API-Key vorhanden
- Hotkeys: Strg+Alt (Sprechen), Strg+Alt+Leertaste (Text), Strg+Shift (Diktat)
- Agent-Codewort: „hey agent danis“
- Agent-Integrationen: so viele wie möglich
- Extras: Claude darf sinnvolle Ideen einbauen; Kosten minimieren hat Priorität

## Entscheidungen
| Thema | Entscheidung | Warum |
|---|---|---|
| Basis | Windows-Fork rickkdev/clicky (C#/.NET 8, WPF) | Tray, Hotkey, Overlay, Pointing, Multi-Monitor, DPAPI-Keys sind schon da |
| LLM Hauptschleife | Claude Haiku 5.5 über API (eigener Key) | 0,10 $/0,50 $ pro 1 Mio. Tokens → ca. 0,07 Cent pro Frage |
| LLM „genauer“ | Claude Sonnet 5.5 optional (Umschalter) | rund 20× teurer, nur bei Bedarf |
| Agent-Modus | Claude Code CLI (`claude -p`) mit Team-Login | läuft über das Team-Abo, keine API-Kosten. `ANTHROPIC_API_KEY` wird aus dem Kindprozess entfernt, sonst wird über die API abgerechnet |
| STT | Whisper lokal (Whisper.net / whisper.cpp) | gratis, offline, gutes Deutsch; AssemblyAI bleibt als optionale Alternative |
| TTS | Piper lokal, Stimme „Thorsten“ (de_DE, männlich) | gratis, offline; Fallback Windows-Stimme „Stefan“ |
| Keys | DPAPI (verschlüsselt im Windows-Benutzerkonto) | nie im Binary, nie in Git |
| Entfernt | PostHog-Analytics, WinSparkle-Updater, Codex/z.ai/OpenAI, Bridge/Hermes, Mac-Code | unnötig bzw. fremdes Branding |
| Builds | GitHub Actions (windows-latest) baut .exe/Installer | Nutzer muss nicht selbst kompilieren |

## Bekannte Risiken / zu prüfen
- **AltGr = Strg+Alt** auf deutscher Tastatur (@ € { } [ ] \ | ~). Push-to-Talk darf nur auf
  **linke** Strg + **linke** Alt reagieren, muss das künstliche Strg von AltGr ignorieren und
  abbrechen, sobald eine weitere Taste gedrückt wird.
- Strg+Shift kann bei mehreren Tastaturlayouts den Layout-Wechsel auslösen.
- Haiku zeigt evtl. ungenauer auf Elemente als Sonnet → testen, ggf. Sonnet nur fürs Zeigen.
- Ob claude.ai-Connectors (Gmail, Kalender, Notion) in `claude -p` verfügbar sind → in Phase 5 testen.
- Schema von `--permission-prompt-tool` ist nicht dokumentiert → Bestätigungen eher über PreToolUse-Hook.
- Codewort-Erkennung braucht unscharfen Abgleich (Whisper schreibt z. B. „Dennis“ statt „Danis“).

## Meilensteine
- [ ] M1 Import + Umbenennung + Aufräumen + CI-Build
- [ ] M2 Setup beim Nutzer (Key, Mikrofon, Claude Code Login)
- [ ] M3 Kernschleife: Hotkey → Whisper → Screenshot → Haiku → Piper
- [ ] M4 Overlay/Zeigen Multi-Monitor, Markierungen, Text-Eingabe, Diktat
- [ ] M5 Agent-Modus (parallel, Status, Benachrichtigung, Bestätigung)
- [ ] M6 Persönlichkeit, Extras, Autostart, Installer
