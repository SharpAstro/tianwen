# Plan: Rework TUI Live Session Tab Layout

## Current State

Everything is dumped into a single `MarkdownWidget` — no proper panel layout, no progress bars, Sixel preview broken.

## Target Layout

```
┌─────────────────────────────────────────────────────────────┐
│ [Phase] Activity text                 Obs:1/2 F:3/~27 Exp:6m│ TextBar (Dock Top, 1)
│ Guiding: Total 0.3" Ra 0.2" Dec 0.2" Peak 0.5"             │ TextBar (Dock Top, 1)
├───────────────────────────────┬─────────────────────────────┤
│ ## Fake Camera 1 (IMX294C)    │                             │
│ Sensor: -3°C → -3°C  57%     │                             │
│ Focus: 91  (14.9°C)          │      Preview                │
│ L #3  ████████████░░░ 78/120s │      (Sixel or Canvas)      │ Dock Left ~40 cols
│                               │                             │   + Fill for preview
│ ## Fake Mount 1  Tracking  W  │                             │
│ RA 12:43:52  HA -2.59h       │                             │
│ Dec -11:46:04                │                             │
│ → Sombrero Galaxy             │                             │
│                               │                             │
│ ## Focus                      │                             │
│ 01:03 #0  2.6" @91           │                             │
│                               │                             │
│ ## Exposures                  │                             │
│ 00:58 Sombrero  L  2.4" 847★ │                             │
│ 01:01 Sombrero  L  2.5" 823★ │                             │
├───────────────────────────────┴─────────────────────────────┤
│ Escape:abort  Q:quit                       Session started  │ TextBar (Dock Bottom, 1)
└─────────────────────────────────────────────────────────────┘
```

## Changes

### 1. Panel Layout (CreateWidgets)

Replace single `MarkdownWidget` fill with:
- `Dock(Top, 1)` — phase/activity TextBar
- `Dock(Top, 1)` — guide RMS TextBar
- `Dock(Bottom, 1)` — status bar TextBar
- `Dock(Left, 40)` — info MarkdownWidget (camera, mount, focus, exposures)
- `Fill()` — preview area (Canvas for Sixel, or MarkdownWidget placeholder)

### 2. ASCII Progress Bar for Exposure

In the per-OTA section, when `State == Exposing`:
```
L #3  ████████████░░░░░░░ 78/120s
```

Use Unicode block chars: `█` for filled, `░` for empty. Width ~20 chars.
```csharp
var progress = elapsedSec / total;
var barWidth = 20;
var filled = (int)(progress * barWidth);
var bar = new string('█', filled) + new string('░', barWidth - filled);
sb.AppendLine($"{cs.FilterName} #{cs.FrameNumber}  {bar} {elapsedSec:F0}/{total:F0}s");
```

### 3. Preview (Right Panel)

**Sixel path** (terminal.HasSixelSupport):
- On new frame: kick off async `AstroImageDocument.CreateFromImageAsync` → stretch → Sixel encode
- Size to fit the fill viewport's pixel dimensions
- Write Sixel bytes directly to terminal after panel render

**Non-Sixel fallback**:
- Show frame metadata: resolution, HFD, star count, filter, gain
- Could render a simple ASCII star map (top-N stars as dots in a character grid)

### 4. Image Processing for Preview

The current code calls `AstroImageDocument.CreateFromImageAsync(latestImage)` which handles:
- Normalization
- Debayer (for RGGB sensors)
- Background estimation + stretch stats

Then `ConsoleImageRenderer.RenderImage(doc, viewerState)` applies MTF stretch.
Then `EncodeSixel` produces the Sixel byte stream.

**Issue**: `ConsoleImageRenderer` may not exist yet in Console.Lib, or the API may differ.
Check what's available and adapt. Alternatively, render to an `RgbaImage` and use
`Canvas<RgbaImage>` which Console.Lib already supports for the planner chart.

### 5. HandleInput Convention

The TUI `HandleInput` returns `true` = quit app, `false` = continue.
This is the OPPOSITE of the GUI convention. Currently all tabs return `false`.
The live session tab needs to "consume" keys (prevent Q from quitting during session)
via the `tabConsumed` flag mechanism: set `NeedsRedraw = true` to signal consumption.

## Files to Modify

| File | Change |
|---|---|
| `TuiLiveSessionTab.cs` | Full rewrite: panel layout, progress bar, preview area |
| `TuiTabBase.cs` | May need to support sub-panels within tabs |

## Files Unchanged

- `LiveSessionState.cs` — already has all needed data
- `LiveSessionActions.cs` — format helpers already exist
- `AppSignalHandler.cs` — session start/abort logic unchanged
