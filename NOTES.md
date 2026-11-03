# heyDanis – Projektnotizen

Persönlicher KI-Buddy für Windows (Klon von HeyClicky). Basis: Windows-Fork
https://github.com/rickkdev/clicky (MIT, ursprünglich https://github.com/farzaa/clicky).

## ▶ AKTUELLER STAND – hier in einer neuen Session weitermachen

**Arbeitsweise mit dem Nutzer:** Deutsch, kurz und konkret, er ist HTL-Schüler, kein Profi. Kosten
minimieren hat Priorität. Direkt auf `main` committen und pushen; nach jedem Push die GitHub Action
abwarten (`gh run list --repo SpeedPeed/heyDanis`), dann dem Nutzer sagen, **welches Update-Paket**
er laden soll (meist `heyDanis-UPDATE-klein`, siehe „Updates beim Nutzer“). Nutzer hat langsames
Internet, zwei Monitore, Windows 11, deutsche Tastatur, Pixel 8 Pro.
Beim Testen hilft: Hauptfenster-Knöpfe **„Diagnose kopieren“** (Logzeilen + Monitorinfo in die
Zwischenablage) und **„Monitor-Test“** (Kalibrierkreise auf jedem Monitor).

**Funktioniert laut Nutzer:** Sprechen (Strg+Alt) mit Whisper → Haiku → Piper „Thorsten“, Sprechblase,
Zeigen auf Monitor 1 (mit Zoom-Verfeinerung), Text-Eingabe (Strg+Alt+Leertaste), Diktat (Strg+Shift),
Schritt-für-Schritt-Zeigen, Kostenanzeige.

**Gebaut, vom Nutzer noch NICHT getestet:**
1. **Erklär-Tafel** (siehe Abschnitt unten) – braucht die Claude-CLI.
2. **Mixed-DPI-Fix für Monitor 2** (Overlay per `SetWindowPos` in physischen Pixeln) + **Monitor-Test**.
3. **OCR-Snapping** fürs Markieren (Kreise auf Wörter per Windows-OCR); zuletzt „näher, aber nicht exakt“.

**Offene Rückmeldungen vom Nutzer (zuerst abfragen):**
- Claude-CLI: **installiert, Version 2.1.317** ✅. Offen: `echo $env:ANTHROPIC_API_KEY` (soll leer sein)
  und ob `claude` einmal gestartet + mit Team-Konto angemeldet wurde.
- Ergebnis **Monitor-Test** und **Windows-Skalierung + Auflösung** beider Monitore.
- Erster Test der **Tafel** („Zeichne mir den Satz des Pythagoras an die Tafel“, „Zeig mir das in 3D“).
- Ob kostenpflichtige Upgrades gewünscht sind (A Sonnet nur fürs Zeigen ~3 $/Monat, B Sonnet für alles
  ~8–10 $/Monat, C AssemblyAI-Live-Spracherkennung ~Cent/Monat, D ElevenLabs ~5 $/Monat). Noch nicht
  entschieden; Nutzer hat bisher nur „Tafel über das Abo“ gewählt.

**Nächste Schritte (vom Nutzer so bestätigt: Tafel → Aktionen → Agent):**
1. Tafel beim Nutzer zum Laufen bringen (CLI-Install begleiten, Fehler per „Diagnose kopieren“).
2. **Aktionen**: Websites/Programme/Ordner öffnen, im Web suchen („öffne YouTube“, „such mir Videos zu
   Integralen“). Haiku reicht; über ein neues Feld im strukturierten JSON (wie `showBoard`). Alles, was
   etwas sendet/löscht/kauft, nur mit Bestätigungsdialog.
3. **M5 Agent-Modus** über `claude -p` (Team-Abo), Codewort „hey agent danis“ (unscharf matchen),
   mehrere Agenten parallel, Status im Panel, Windows-Benachrichtigung, Bestätigung riskanter Aktionen
   (PreToolUse-Hook). `ClaudeCodeCli.RunPrintModeAsync` ist schon da und wiederverwendbar.
4. Danach: Apps steuern (Computer Use, am Schluss), M6 (Autostart, Installer, eingebauter Updater,
   Rest der UI auf Deutsch), M7 Android.

## Verlauf (Details zu früheren Tests)
- Phase 0 + 1 erledigt, Plan vom Nutzer freigegeben (direkt auf `main` arbeiten).
- **M1 erledigt:** Code importiert, Ballast entfernt, umbenannt, eigenes Icon, CI-Build.
- **M2 teilweise:** Exe startet beim Nutzer. Claude-Code-CLI war noch **nicht installiert**
  (Nutzer kannte nur Desktop-App/Web) → Installationsanleitung gegeben; Rückmeldung zu
  `claude --version` und `echo %ANTHROPIC_API_KEY%` steht noch aus (wird erst in M5 gebraucht).
- **M3 gebaut, wartet auf Test durch den Nutzer:**
  - Lokale Spracherkennung (Whisper Small, `HeyDanis.LocalSpeech`), lokale Stimme (Piper
    „Thorsten“ via sherpa-onnx), Windows-Stimme als Fallback, solange Piper lädt.
  - Modelle werden beim Start im Hintergrund nach `%APPDATA%\heyDanis\models` geladen
    (Fortschritt im Hauptfenster). Nur noch der Anthropic-Key ist Pflicht.
  - Deutscher, lockerer Buddy-Prompt; Haiku ohne Extended Thinking, Sonnet mit effort low.
  - Kostenanzeige „Kosten diesen Monat“ (`usage.json`).
  - AltGr-Schutz + Abbruch bei Strg+Alt+<Taste>.
  - Build ist jetzt ein App-**Ordner** (nicht Single-File), weil Whisper seine nativen DLLs
    in `runtimes/` neben der Exe sucht.
- **Erster M3-Test beim Nutzer:** Einstellungsfenster ging bei jeder Frage auf (Claude antwortete
  401 = Key abgelehnt) und Auswahlfelder waren weiß/unlesbar. Behoben: echte Anthropic-Fehlermeldung,
  Fenster nur einmal, Key-Format-Prüfung (Abo-Token vs. API-Key), dunkles ComboBox-Template.
  Ursache des 401 noch unklar → Nutzer prüft mit „Test“-Button.
- **Zweiter M3-Test:** Antworten kommen an (Sprechblase funktioniert), aber **keine Stimme**
  auf keinem Ausgabegerät, und der Cursor **zeigt nicht** (nur Beschreibung). Gegenmaßnahmen:
  Structured Outputs (json_schema) für das Pointing-Protokoll + strengere Zeige-Regel im Prompt;
  „Stimme testen“-Button + `[TTS]`/`[AUDIO]`-Logging; NAudio-Wiedergabefehler werden nicht mehr
  verschluckt. Ursache Stimme noch offen → Ergebnis vom Stimmtest abwarten.
- **Ursache „keine Stimme“ gefunden (in Linux reproduziert):** sherpa-onnx 2.x hat espeak-ng entfernt →
  Piper erzeugte 0 Samples. Gepinnt auf **1.13.8** (nicht upgraden!). Außerdem parallele Downloads
  desselben Modells verhindert, Downloads setzen nach Abbruch fort (HTTP Range).
  Round-Trip-Test Piper → Whisper Base im Container: Text korrekt erkannt.
- **Dritter M3-Test:** Thorsten spricht ✅, Cursor fliegt ✅, zeigt aber ungenau (Textdatei: nannte
  Stelle A, zeigte auf B). Maßnahmen: Bildgröße exakt nach Claudes Resize-Regel
  (`ClaudeImageSizing`, High-Res-Tier 2576/4784), `oversized_image: "error"` + automatischer
  Fallback auf Standard-Tier, Zoom-Verfeinerung pro Punkt (Einstellung „Genauer zeigen“, Standard an),
  Antwort wird zuerst gesprochen, Zeiger parallel verfeinert. Ergebnis offen.
  Falls immer noch ungenau: Option „Zeigen mit Sonnet“ anbieten (teurer).
- **M3 abgeschlossen** (Zeigen passt laut Nutzer nach Zoom-Verfeinerung).
- **M4 gebaut:** `HeyDanisHotkeyEngine` (Strg+Alt = Sprechen, Strg+Alt+Leertaste = Textbox, Leertaste
  wird verschluckt; Strg+Shift = Diktat; Akkorde schließen sich gegenseitig aus), `TextQuestionWindow`,
  Diktat via Whisper → Zwischenablage + Strg+V (`FocusedTextInserter`, alte Zwischenablage wird
  wiederhergestellt, injizierte Tasten ignoriert der Hook), Kreise/Pfeile auch außerhalb des Gamer-Modus.
  Multi-Monitor-Zeigen war schon im Fork vorhanden (screenN) – vom Nutzer noch nicht getestet.
- **M4-Test:** Textbox ✅, Diktat ✅, Schritt-für-Schritt ✅, Markieren ❌ (Kreise ein Wort daneben),
  zweiter Monitor: sieht ihn, Cursor bleibt aber auf dem Maus-Monitor. Fix: Zoom-Verfeinerung liefert
  jetzt einen Rahmen (Kreise/Pfeile/Punkte), `screen` im Schema Pflicht, Bild-Beschriftung VOR dem Bild.
  Falls Monitor 2 weiter nicht klappt: `[POINT] overlay: matched overlay index=…`-Zeilen aus debug.log prüfen.
- **Zweiter Markier-Test:** besser, aber „lange nicht gut genug“; Nutzer findet HeyClicky „extrem
  beeindruckend“, heyDanis wirkt dagegen „wie kaputtes Kinderspielzeug“. Ehrliche Einordnung an Nutzer:
  Qualitätslücke kommt großteils von den Spar-Entscheidungen (Haiku statt Sonnet, Piper statt ElevenLabs,
  Whisper statt Streaming-STT). Gratis-Fix: **Windows-OCR-Snapping** (`ScreenTextRecognizer` +
  `TextTargetMatcher`): Claude nennt nur den Text, OCR liefert die exakte Position. Kostenpflichtige
  Optionen (Sonnet fürs Zeigen, ElevenLabs, AssemblyAI) dem Nutzer zur Entscheidung vorgelegt.
- **Dritter Markier-Test:** Monitor 1 näher, aber nicht exakt; Monitor 2: Kreis auf richtigem Monitor,
  aber weit daneben/zu groß → Verdacht **Mixed-DPI** (WPF positioniert Overlay mit DPI von Monitor 1).
  Fix: Overlay per `SetWindowPos` in physischen Pixeln + echte Fenster-DPI. Neu im Tray: „Monitor-Test
  (Kreise)“ und „Diagnose kopieren“ (relevante Logzeilen + Monitorinfo in die Zwischenablage).
  Offene Frage an Nutzer: Skalierung beider Monitore, Ergebnis Monitor-Test, Wahl der Upgrades A–D.
- Als Nächstes: Nutzer testet M4 → danach **M5** (Agent-Modus; Claude-CLI muss installiert sein).

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
| Builds | GitHub Actions (windows-latest) baut `heyDanis.exe` als Artefakt | Nutzer muss nicht selbst kompilieren |
| Branding | Oranges Sprechblasen-Icon (`scripts/make-icon.py`), Cursor #FF6A2B | kein Clicky-Branding |
| Git | direkt auf `main` | Solo-Projekt, vom Nutzer so gewünscht |

## Bekannte Risiken / zu prüfen
- Whisper/Piper laufen bisher nur in Unit-Tests ohne echtes Modell – **echter Test nur beim
  Nutzer möglich** (Container ist Linux). Bei Problemen: `%APPDATA%\heyDanis\debug.log`.
- Haiku 5.5 mit `thinking: disabled` + `effort: low` – falls die API das ablehnt (400),
  in `AnthropicDirectClient.BuildRequestBody` anpassen.
- Abbruch während AssemblyAI-Verbindungsaufbau kann eine Session offen lassen (nur Cloud-STT).
- ~~AltGr = Strg+Alt~~ → gelöst in M3: nur linke Alt zählt, AltGr gesperrt, Abbruch bei
  weiterer Taste (`ShortcutTransition.Cancelled`).
- Strg+Shift kann bei mehreren Tastaturlayouts den Layout-Wechsel auslösen.
- Haiku zeigt evtl. ungenauer auf Elemente als Sonnet → testen, ggf. Sonnet nur fürs Zeigen.
- Ob claude.ai-Connectors (Gmail, Kalender, Notion) in `claude -p` verfügbar sind → in Phase 5 testen.
- Schema von `--permission-prompt-tool` ist nicht dokumentiert → Bestätigungen eher über PreToolUse-Hook.
- Codewort-Erkennung braucht unscharfen Abgleich (Whisper schreibt z. B. „Dennis“ statt „Danis“).

## Meilensteine
- [x] M1 Import + Umbenennung + Aufräumen + CI-Build
- [ ] M2 Setup beim Nutzer (Key, Mikrofon, Claude Code Login) – CLI-Installation offen
- [x] M3 Kernschleife: Hotkey → Whisper → Screenshot → Haiku → Piper – vom Nutzer bestätigt („passt jetzt super“)
- [~] M4 Overlay/Zeigen Multi-Monitor, Markierungen, Text-Eingabe, Diktat – Text/Diktat ✅, Monitor 2 + Markieren: Fix gebaut, Test offen
- [~] Erklär-Tafel (vorgezogen) – gebaut, Test offen (CLI fehlt noch)
- [ ] M5 Agent-Modus (parallel, Status, Benachrichtigung, Bestätigung)
- [ ] M6 Persönlichkeit, Extras, Autostart, Installer
- [ ] M7 (später, Wunsch des Nutzers) Android-Version für **Pixel 8 Pro** – erst nach M6

## Plan M7: Android-Version (Pixel 8 Pro)
Eigenes Projekt (Kotlin + Jetpack Compose), eigener Ordner `android/` oder eigenes Repo.
Vom PC übernehmbar: Prompts, Pointing-Protokoll (JSON), Kostenlogik.
- **Auslösen:** heyDanis als **Standard-Assistent** registrieren (VoiceInteractionService) →
  Ecke wischen / Power lang drücken. Vorteil: Android liefert dabei selbst einen Screenshot
  (`onHandleScreenshot`), ohne MediaProjection-Dialog. Zusätzlich optional ein schwebender
  Push-to-Talk-Button (Overlay-Berechtigung).
- **Zeigen:** Overlay-Fenster (`SYSTEM_ALERT_WINDOW`) mit dem orangen Buddy-Cursor über anderen Apps.
- **Sprache gratis & lokal:** Android `SpeechRecognizer` (on-device, Deutsch) und
  `TextToSpeech` (Google-Stimmen, männlich Deutsch, offline) – kein Whisper/Piper nötig.
- **LLM:** Claude Haiku 5.5 mit dem eigenen API-Key, gespeichert im Android Keystore.
- **Verteilung:** GitHub Actions baut die APK, Installation per Sideload (kein Play Store, gratis).
- Kein iPhone-Support geplant (Apple erlaubt kein Overlay/fremde Screenshots).

## Updates beim Nutzer
- Nutzer hat **langsames Internet**. CI baut zusätzlich `heyDanis-UPDATE-klein` (~0,8 MB, nur
  heyDanis' eigene Dateien). Dem Nutzer immer sagen, **ob das kleine Update reicht** oder ob
  sich NuGet-Pakete geändert haben (dann volles Paket `heyDanis-win-x64`, ~85 MB).
- Drittes Artefakt `heyDanis-UPDATE-mit-Sprachbibliotheken` (~10 MB): heyDanis + Whisper/sherpa/onnxruntime,
  wenn sich nur Sprach-Pakete geändert haben.
- Idee für M6: eingebauter Updater, der nur geänderte Dateien von GitHub Releases lädt.

## Erklär-Tafel (gebaut, Test durch Nutzer offen)
- Haiku setzt `showBoard`/`boardTopic`/`boardMode` im strukturierten JSON; App öffnet
  `ExplanationBoardWindow` (WebView2) und lässt `IBoardGenerator` eine HTML-Seite schreiben
  (SVG-Animationen, KaTeX, three.js). Standard: `ClaudeCodeCliBoardGenerator` = `claude -p --output-format json
  --model sonnet --tools Read --allowedTools Read --system-prompt-file … --no-session-persistence --max-turns 4`,
  Prompt über stdin, Screenshot als `bildschirm.jpg` im Arbeitsordner `%LOCALAPPDATA%\heyDanis\board`.
  **Kein `--bare`** (liest keinen Abo-Login). Alternative: `ApiBoardGenerator` (Sonnet, ~2–5 Cent).
- Im Container mit echter CLI getestet: Flags ok, JSON-Form ok, Read-Tool ohne Rückfrage ok, zwei Beispiel-
  Tafeln (Pythagoras 29 s, 3D-Kreuzprodukt 43 s) gerendert, keine JS-Fehler.
- Nutzer hat die **CLI noch nicht installiert** → ohne CLI zeigt die Tafel eine Installationsanleitung.

## Neue Anforderung (nach M4): „Kompetenz wie HeyClicky“
Nutzer war von HeyClicky v. a. von der **Kompetenz und den Möglichkeiten** beeindruckt: Seiten öffnen,
mit Seiten/Apps interagieren, Overlays erstellen, live Notizfelder öffnen und dort Erklärungen zeichnen,
sogar 3D-Modelle zum Erklären erzeugen. Vorschlag an Nutzer (Entscheidung offen):
- **Aktionen**: Websites/Programme/Dateien öffnen, im Web suchen (günstig, Haiku reicht).
- **Erklär-Tafel**: WebView2-Fenster, Claude erzeugt HTML/SVG/KaTeX/three.js (Zeichnungen, Formeln,
  3D) – braucht starkes Modell → Sonnet per API (~2–5 Cent/Erklärung) oder Team-Abo über `claude -p`.
- **Agent-Modus** (M5) über Claude-Code-CLI/Team-Abo für längere Aufgaben im Hintergrund.
- **App-Steuerung** (klicken/tippen für den Nutzer, Computer Use): teuer + riskant, ganz am Schluss,
  nur mit Bestätigung.

## Hinweise für neue Sessions
- Upstream-Code zum Nachschauen: `git clone --depth 1 https://github.com/rickkdev/clicky`
  (Windows-Code in `windows/`) bzw. https://github.com/farzaa/clicky (macOS-Original).
- .NET 8 SDK im Linux-Container: `curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0`
