# Clicky Windows Test Pyramid

`dotnet test windows\tests\Clicky.Tests\Clicky.Tests.csproj -c Release` is the CI-safe suite. It uses fake provider boundaries and synthetic screenshots; it must not require a visible desktop, WPF compositor assertions, API keys, microphone input, or speaker output.

High-signal layers:

- Unit tests cover parsers, stores, provider request translation, tokenization, DPI math, and small UI view-model rules.
- Deterministic end-to-end tests cover the full Clicky turn with fake boundaries. `FullTurnEndToEndTests` wires the real `CompanionManager` response orchestration to fake capture, LLM, TTS, and overlay dispatch, then asserts state transitions, response text, TTS start, point conversion, overlay dispatch, cancellation, and user-facing error states.
- Timing instrumentation tests are CI-safe when they use `TurnTimingRecordTests` and fake providers. They validate `[TURN]` log formatting and diagnostic plumbing without calling real APIs.
- Desktop-only smoke tests cover the compositor and real monitor last mile. Use the tray `Desktop Smoke Test` fixture, then inspect `%APPDATA%\Clicky\point-debug\*-raw.jpg`, `%APPDATA%\Clicky\point-debug\*-annotated.png`, and `%APPDATA%\Clicky\debug.log` `[SMOKE]` / `[POINT]` lines.

When a desktop smoke test fails, first compare the raw and annotated artifacts to answer whether the screenshot-space point landed inside the intended target. If the annotated point is correct but the live blue cursor is wrong, debug WPF/DPI/overlay placement rather than LLM or screenshot coordinate math.

For real provider timing, use the tray `Provider Timing Diagnostics` item. It runs one text-only LLM probe, one screenshot LLM probe, and one TTS probe with the current keys, then writes `[TURN]` lines to `%APPDATA%\Clicky\debug.log`. Compare `diagnostic-text-llm-*`, `diagnostic-image-llm-*`, `capture-*`, and `diagnostic-tts-*` before blaming overlay rendering or model vision.

## Semantic Pointing Fixtures

Semantic model accuracy is tested with real screenshot-style fixture images under `Fixtures/pointing/`. Each fixture has:

- a PNG screenshot image
- `fixtures.json` metadata with the target text, allowed region polygons, and distractor polygons
- canned model outputs in `canned-model-outputs.json` for CI-safe evaluator tests

Use these fixtures for map and dense-UI failures. A model point passes only when it lands inside an allowed region and outside every distractor region. For map cases such as Egypt and Algeria, `[POINT:none]` is a valid safe result when the fixture allows `PointOrNone`; a point in a neighboring country or water is a failure.

To add a fixture from a failed user report:

1. Save the raw screenshot PNG in `Fixtures/pointing/`.
2. Add an entry to `fixtures.json` with the exact user target text.
3. Define allowed polygons around the acceptable target region.
4. Define distractor polygons for plausible wrong answers, especially neighboring countries, water, labels, or nearby controls.
5. Add at least one passing and one failing canned output to `canned-model-outputs.json`.
6. Run `dotnet test windows\tests\Clicky.Tests\Clicky.Tests.csproj -c Release --filter SemanticPointingEvaluationTests`.

For optional live-provider evaluation with the current Clicky settings and DPAPI-protected keys, run:

```powershell
powershell -ExecutionPolicy Bypass -File windows\scripts\run-semantic-pointing-eval.ps1
```

The script saves JSON and CSV reports under `%APPDATA%\Clicky\point-debug\semantic-eval` with provider, model, target, returned point tag, pass/fail, latency, and raw response. This is desktop/local evidence only; default CI must keep using canned outputs and must not call real provider APIs.
