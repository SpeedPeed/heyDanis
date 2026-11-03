# Windows Pointing Redesign

This document defines the protocol Clicky should use before rebuilding Windows semantic pointing. It intentionally separates what the vision model decides from coordinate conversion and overlay rendering, because recent failures mixed those concerns and made wrong points look like last-mile bugs.

## Current Baseline

The Windows app is back to one normal LLM response per user turn. That response may end with a single `[POINT:x,y:label:screenN]` tag or `[POINT:none]`.

The retained baseline is:

1. Capture the user's visible screen or screens.
2. Send the screenshot images, labels, system prompt, conversation history, and transcript to the configured LLM.
3. Stream spoken text to TTS as it arrives.
4. Parse the final response with `PointTagParser`.
5. Convert screenshot pixel coordinates to physical desktop coordinates using `CapturedScreen` metadata.
6. Dispatch the overlay cursor to the converted point.
7. Save point debug artifacts and `[TURN]` / `[POINT]` log evidence.

This baseline proves explicit coordinate conversion and overlay dispatch. It does not prove semantic localization. A model can still choose Egypt's neighbor, a map label, or ocean while every downstream coordinate layer behaves correctly.

## Layer Ownership

Semantic localization is owned by the vision model protocol. It decides whether the user's request needs a point, which visible target satisfies the request, whether the target is unambiguous, and which screenshot pixel coordinate represents the target. Failures in this layer include wrong country, wrong UI control, label-only guesses, hallucinated controls, or a point when the target is ambiguous.

Coordinate conversion is owned by `PointTagParser` and `CapturedScreen` metadata. It parses the model's explicit directive, chooses the intended captured screen, clamps to screenshot bounds, scales screenshot pixels to physical monitor pixels, and adds the monitor origin. Failures in this layer include wrong screen selection, DPI/scaling errors, negative-origin monitor errors, or mismatched screenshot dimensions.

Overlay rendering is owned by `Clicky.Overlay`. It receives a physical desktop point and monitor bounds, then shows the blue cursor. Failures in this layer include WPF DIP/physical-pixel conversion mistakes, transparent window placement errors, animation problems, or compositor-specific visibility issues.

Debugging must start by identifying the layer:

- Raw screenshot plus annotated point says whether semantic localization produced a reasonable screenshot-space coordinate.
- `PointTagParser` tests and conversion logs say whether screenshot-space mapped to desktop-space correctly.
- The tray `Desktop Smoke Test` fixture says whether a known explicit point reaches the live overlay on the real desktop.

## Target Behavior

For ordinary explanation questions, Clicky should answer without a visible pointer and return no point.

For navigation, "show me", "where is", "click", "find", app-control, or visual identification requests, Clicky should point only when it can identify the target inside the screenshot with enough confidence to avoid a misleading visible cursor. A skipped pointer is acceptable. A wrong pointer is not.

Timing target: when a turn requires pointing, the visible cursor should appear as soon as the point decision is available. It must not wait for long spoken prose or TTS completion. Spoken text can continue after the pointer appears.

Accuracy target for hard semantic cases:

- For country or region map prompts, the point must land inside the visible country or region polygon.
- The model must return no point when the visible target is ambiguous, occluded, too small, or not present.
- Neighboring countries, oceans, labels outside the target, and unrelated semantic regions are failures.
- For dense app UI, the point must land inside the intended control's bounds, not merely near its text label unless the label itself is the actionable target.

## Candidate Protocols

### Candidate A: One-Pass Answer With POINT

The model returns the spoken answer and appends `[POINT:x,y:label:screenN]` or `[POINT:none]` at the end.

Benefits:

- Smallest implementation change from the current baseline.
- One provider call and one image payload.
- Natural spoken answer and point decision share context.
- Existing `PointTagParser`, debug artifacts, and tests remain valid.

Costs:

- The pointer cannot be dispatched until the final tag is generated, so long prose delays visible feedback.
- The protocol mixes speech and machine-readable control data in one text stream.
- The model may produce a plausible spoken answer while choosing a bad coordinate.
- It gives no structured confidence, target description, or reason for returning no point.

This is acceptable as a fallback baseline, not as the redesigned default for semantic pointing.

### Candidate B: Two-Phase Point-First Then Answer

For pointing-like requests, the app first asks the vision model for only the point decision. After that returns, it dispatches the overlay and asks for or continues with the spoken answer.

Benefits:

- Fastest path to visible cursor dispatch.
- The point prompt can be short, strict, and optimized for localization.
- The spoken response can be generated after the cursor appears.
- It avoids waiting for TTS or long prose before showing the pointer.

Costs:

- Potentially doubles LLM calls and image-token cost.
- Adds cancellation and ordering complexity.
- The point decision and spoken answer can drift unless the first structured result is passed into the answer step.
- Recent failed locator layers resembled this shape but lacked an evaluation gate and reliable protocol contract.

This should only be implemented if fixture evaluation proves the point-only prompt improves accuracy enough to justify the cost and complexity.

### Candidate C: Structured Result With Speech And Point Intent

The model returns a machine-readable object containing spoken text and point intent, for example:

```json
{
  "spokenText": "the egypt outline is in northeast africa, above sudan.",
  "pointIntent": {
    "kind": "point",
    "x": 842,
    "y": 516,
    "screen": 1,
    "label": "egypt",
    "confidence": "high"
  }
}
```

`pointIntent.kind` can be `point` or `none`. For `none`, the result must include a short internal reason such as `ambiguous`, `not_visible`, or `unsafe_low_confidence`.

Benefits:

- Separates spoken output from control data without scraping free-form text.
- Allows early dispatch once `pointIntent` is parsed, even if spoken text is still streaming or synthesized later.
- Gives tests a stable contract for no-point and confidence behavior.
- Makes wrong-point prevention a first-class rule rather than a prompt suggestion.

Costs:

- Requires provider-specific support or careful prompting for structured JSON.
- Streaming JSON must be parsed conservatively; partial objects cannot be trusted until complete.
- Text-to-speech should not speak internal fields or schema errors.
- Existing `[POINT]` prompt and parser need an adapter or migration path.

This is the recommended protocol for US-049. It creates a single internal result shape, avoids competing point sources, and can still be backed by one provider call when the model reliably emits valid structured output.

## Selected Protocol For US-049

Implement Candidate C behind a feature flag.

The feature-flagged path should produce an internal result equivalent to:

```csharp
internal sealed record PointingTurnResult(
    string SpokenText,
    PointIntent PointIntent);

internal sealed record PointIntent(
    PointIntentKind Kind,
    int? X,
    int? Y,
    int? ScreenNumber,
    string? Label,
    string? NoPointReason);
```

Rules:

- There is one source of truth for a turn's point decision: `PointIntent`.
- Downstream code must not scrape multiple possible point tags, fallback coordinates, or hidden verifier outputs.
- The legacy `[POINT]` parser remains for the current baseline and for provider fallback, but the redesigned path should adapt provider output into `PointIntent` before dispatch.
- `PointIntentKind.None` is a successful result, not an error.
- Invalid schema, missing coordinates, coordinates outside the image, low-confidence language, or unknown target visibility all map to `PointIntentKind.None`.
- Dispatch can happen before TTS completion once a valid `PointIntentKind.Point` exists.

## Prompt Contract

The model prompt for the redesigned path should be strict and short. It should say:

- Decide whether a visible pointer would help answer the user.
- If the user asks to show, find, click, locate, or identify a visible thing, return a point only if the target is visible and unambiguous.
- Use screenshot pixel coordinates with origin at the top-left of the selected image.
- For map regions, choose a coordinate inside the visible filled region or border of the requested country/region. Do not choose nearby labels, neighboring countries, ocean, legends, or text outside the region.
- If unsure, return `none`.
- Return only the structured object required by the app.

The spoken answer should be generated from the same result and should not promise the pointer is correct if `PointIntentKind.None` was returned. For a no-point result, the spoken text can still help: "i can't point to egypt reliably from this map view, but it's in the northeast corner of africa above sudan."

## Provider And Model Evaluation

The configured model is acceptable for semantic pointing only if it passes the real screenshot fixture gate from US-048:

- Simple labeled UI target passes.
- Dense app/settings target passes.
- Egypt and Algeria map fixtures do not produce wrong-country or water points.
- `[POINT:none]` or structured `none` is accepted for map cases when the model cannot reliably localize the country.
- Latency is measured with `[TURN]` logs and compared against the current baseline.

Claude and GLM should be evaluated separately. Provider selection must not assume that general vision ability implies pixel-level semantic localization. A model that describes the screen well but frequently points at nearby map regions is not acceptable for default visible pointer behavior on those prompts.

If no configured model passes the map fixtures, the product behavior should be conservative: return no pointer for those semantic targets and answer verbally.

## Implementation Surface For Next Story

US-049 should include only:

- A feature flag or internal switch for the redesigned pointing path.
- A structured internal result type for spoken text plus point intent.
- Provider-output parsing/adaptation into that result type.
- CompanionManager orchestration that can dispatch a valid point before TTS finishes.
- Tests for structured result parsing, no-point behavior, blocked TTS early dispatch, cancellation, one-turn concurrency, and no duplicate overlay dispatch.
- Integration with the US-048 semantic fixture harness as the gate for enabling or comparing the feature.

US-049 should keep out of scope:

- New verifier, locator, fallback, or prompt-only correction layers.
- Coordinate conversion rewrites unless a fixture proves the existing math wrong.
- Extra providers or new model lists.
- UI changes beyond a hidden/internal switch.
- Real desktop compositor assertions in default CI.
- Heuristic map geometry, OCR-only fallbacks, or hardcoded country-specific fixes.

## Validation Evidence

CI-safe validation:

- Structured result parsing tests.
- Full-turn fake LLM/TTS/capture/overlay tests.
- Existing `PointTagParser` conversion tests.
- Semantic fixture tests that evaluate canned model outputs without calling real APIs.

Desktop-only validation:

- Tray `Desktop Smoke Test` for compositor and DPI last mile.
- Tray `Provider Timing Diagnostics` for real provider text/image/TTS timing.
- Manual fixture runs with saved raw screenshots, annotated points, raw provider responses, and `[TURN]` timelines.

The implementation should not be enabled by default until US-050 records real desktop evidence for Egypt, Algeria, one dense app/settings target, and one normal UI control. For maps, a no-point result is safer than a visible pointer in the wrong country or water.
