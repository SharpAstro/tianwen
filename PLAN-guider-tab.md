# Plan: Guider Tab (GUI + TUI)

## Goal

Add a 5th tab (🎯) to both GUI and TUI that provides a PHD2-like guiding monitor.
Internal guiders (FakeGuider, BuiltInGuider) get the full experience — guide camera
preview, star close-up, error graph, stats. External guiders (PHD2) get error graph
and stats only (no guide camera images).

Reference: PHD2 Guiding 2.6.13 (see screenshots in repo).

## Target Layout (GUI)

```
┌──────────────────────────────────────────────────────────────────────┐
│ [Phase]  Guiding: locked  Star (512,384) SNR 42         02:35:45   │
├────────────────────────────┬─────────────────────────────────────────┤
│                            │  ┌─ Star Close-up ──┐  ┌─ Stats ─────┐│
│   Guide Camera Preview     │  │   (zoomed guide   │  │ Total 0.35" ││
│   (full guide frame,       │  │    star + profile) │  │ RA    0.25" ││
│    crosshair on star,      │  │                    │  │ Dec   0.22" ││
│    stretch applied)        │  └────────────────────┘  │ Peak  0.80" ││
│                            │                          │             ││
│                            │  ┌─ Bullseye ────────┐  │ Exp   2.0s  ││
│                            │  │  (polar drift      │  │ Corr 14/15  ││
│                            │  │   scatter plot,    │  │             ││
│                            │  │   concentric rings)│  └─────────────┘│
│                            │  └────────────────────┘                 │
├────────────────────────────┴─────────────────────────────────────────┤
│ RA  ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━ (red line)         │
│ Dec ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━ (cyan line)        │
│     -60s                                             now    ±1.0"   │
├──────────────────────────────────────────────────────────────────────┤
│ Guide:calibrated  Dither:idle  Settle:0.3"/0.5"     Session running │
└──────────────────────────────────────────────────────────────────────┘
```

## Target Layout (TUI)

```
┌──────────────────────────────────────────────────────────────────────┐
│ 1:Equip  2:Plan  3:Session  4:Live  [5:🎯]     Test  02:35:45      │
├─────────────────────────┬────────────────────────────────────────────┤
│                         │ ## Guide Stats                             │
│   Guide Camera          │ Total: 0.35"  RA: 0.25"  Dec: 0.22"      │
│   (Sixel preview        │ Peak:  0.80"                               │
│    or placeholder       │                                            │
│    for PHD2)            │ Star: SNR 42  HFD 2.1"  Mass 1234        │
│                         │ Exp: 2.0s  Corrections: 14/15             │
│                         │                                            │
│                         │ ## RA Error (last 60s)                     │
│                         │ ▁▂▃▅▇█▇▅▃▁▂▃▅▇█▇▅▃▁▂▃▅▇█▇▅▃▁           │
│                         │  +0.5"                         -0.3"       │
│                         │                                            │
│                         │ ## Dec Error (last 60s)                    │
│                         │ ▅▃▁▂▃▅▇█▇▅▃▁▂▃▅▇█▇▅▃▁▂▃▅▇█▇            │
│                         │  +0.2"                         -0.4"       │
├─────────────────────────┴────────────────────────────────────────────┤
│ Guide:locked  Star:(512,384)                      Session running    │
└──────────────────────────────────────────────────────────────────────┘
```

## Guider Types & Data Availability

| Feature                    | Internal (Fake/BuiltIn)          | External (PHD2)              |
|----------------------------|----------------------------------|------------------------------|
| Guide camera image         | Yes — IGuiderDriver.GetImageAsync | No                           |
| Guide star position        | Yes — star detection result      | No (PHD2 doesn't expose)    |
| Star SNR / HFD / Mass      | Yes — from star detection        | Partial — PHD2 sends SNR    |
| RA/Dec error samples        | Yes — GuideSamples               | Yes — GuideSamples           |
| RMS stats                  | Yes — LastGuideStats             | Yes — LastGuideStats         |
| Correction pulse counts     | Yes — from pulse guide commands  | Partial                      |
| Settle progress            | Yes — SettlePixel vs actual      | Yes — via PHD2 events        |
| Calibration data            | Yes — internal cal state         | Yes — PHD2 cal data          |
| Dither state                | Yes — from Session               | Yes — from Session           |

### Placeholder states (both GUI and TUI)

- **No session** → "No session running"
- **Session running, guider not connected** → "Guider not connected"
- **Calibrating** → "Calibrating guider..." (could show cal step count)
- **Guiding active** → full display
- **Guide star lost** → "Guide star lost — recovering..."
- **Dithering** → "Dithering... settling 1.2"/0.5""
- **Paused** (meridian flip etc.) → "Guiding paused"

## Architecture

### 1. Shared State: `GuiderTabState` (TianWen.UI.Abstractions)

New class alongside `LiveSessionState`. Polled each frame from `ISession`.

```
GuiderTabState
├── GuidePhase (enum: NotConnected, Calibrating, Guiding, Lost, Settling, Paused)
├── IsInternalGuider (bool — controls whether camera preview is available)
├── LastGuideImage (Image? — null for external guiders)
├── GuideStarPosition ((float X, float Y)?)
├── GuideStarMetrics (SNR, HFD, Mass)
├── GuideSamples (ImmutableArray<GuideErrorSample> — last N for graph)
├── LastGuideStats (GuideStats — RMS totals)
├── GuideExposure (TimeSpan)
├── CorrectionCounts (RA succeeded/total, Dec succeeded/total)
├── SettleProgress (current arcsec, target arcsec)
├── DitherState (Idle, Active, Settling)
├── CalibrationStep (int? — null when not calibrating)
└── NeedsRedraw (bool)
```

### 2. Shared Content: `GuiderContent` (TianWen.UI.Abstractions)

Static helpers like `LiveSessionActions` — format strings, compute graph data.

- `FormatGuidePhase(phase)` → "Guiding", "Calibrating...", etc.
- `FormatStarInfo(metrics)` → "SNR 42  HFD 2.1\"  Mass 1234"
- `FormatSettleProgress(current, target)` → "0.3\"/0.5\""
- `BuildErrorSparkline(samples, axis, width)` → Unicode sparkline string
- `GetErrorGraphPoints(samples, axis, timeWindow)` → points for GPU graph
- `GetBullseyePoints(samples, count)` → (ra, dec) scatter points

### 3. GPU Tab: `GuiderTab<TSurface>` (TianWen.UI.Abstractions)

Extends `PixelWidgetBase<TSurface>`. Rendered by `VkGuiRenderer` as the 5th tab.

- **Guide camera panel** (left ~50%): renders guide image via the same stretch
  pipeline as the live session mini viewer. Crosshair overlay on guide star.
  For external guiders: dark panel with "PHD2 Connected" text.
- **Star close-up** (top right): 64×64 pixel crop around guide star, upscaled.
  Star profile (1D intensity cross-section) rendered as mini bar chart.
- **Bullseye** (middle right): scatter plot of last N guide samples,
  concentric rings at 0.5", 1.0", 2.0". RA = X, Dec = Y.
- **Stats panel** (right): RMS, peak, star info, exposure, corrections.
- **Error graph** (bottom strip): scrolling line graph, RA (red) + Dec (cyan),
  time axis (last 60s or 120s), amplitude axis (auto-scaled ±max).
- **Status bar**: guide phase, star position, settle progress.

### 4. TUI Tab: `TuiGuiderTab` (TianWen.Lib.CLI/Tui)

- **Guide camera** (left): Canvas<RgbaImage> for Sixel (internal guiders),
  MarkdownWidget placeholder for external.
- **Stats + sparklines** (right): MarkdownWidget with formatted stats and
  Unicode sparkline error graphs (bipolar, center = 0).
- **Status bar**: TextBar with guide state.
- Keyboard: no special keys needed (read-only monitoring).

### 5. Tab Registration

**GuiTab enum**: add `Guider` value.

**Tab bar**: 5 tabs. TUI: `5:Guider` (text, consistent with `1:Equip` etc.). GUI sidebar: 🎯 icon.

**Keyboard**: `5` / `F5` switches to guider tab (both GUI and TUI).

**Auto-switch**: when guider calibration starts, auto-switch to guider tab
(same pattern as `StartSessionSignal` → LiveSession).

### 6. ISession Extensions

Need to expose guide camera state. Check what's available:

- `ISession.GuideSamples` — already exists (ImmutableArray<GuideErrorSample>)
- `ISession.LastGuideStats` — already exists (GuideStats?)
- `ISession.GuideExposure` — may need to add
- `ISession.LastGuideImage` — may need to add (Image? from guide camera)
- `ISession.GuideStarPosition` — may need to add
- `ISession.IsInternalGuider` — derive from guider type

Guide camera images: during `CalibrateGuiderAsync` and the guiding loop,
the BuiltInGuider takes exposures via `IGuiderDriver`. These images need
to be surfaced to the UI. Add `Image? LastGuideImage { get; }` to ISession,
updated each guide exposure cycle.

## Implementation Order

1. **`GuiderTabState`** + **`GuiderContent`** in Abstractions
2. **`GuiTab.Guider`** enum value + tab bar update (both GUI and TUI)
3. **`TuiGuiderTab`** — stats + sparklines (no camera preview yet)
4. **`GuiderTab<TSurface>`** — GPU version with stats + error graph
5. **Guide camera plumbing** — expose `LastGuideImage` on ISession
6. **Guide camera preview** — Sixel (TUI) and Vulkan (GUI)
7. **Star close-up + bullseye** — GPU only
8. **Auto-switch on calibration start**

## Files to Create

| File | Description |
|------|-------------|
| `TianWen.UI.Abstractions/GuiderTabState.cs` | Shared guider tab state |
| `TianWen.UI.Abstractions/GuiderContent.cs` | Format helpers, sparkline builders |
| `TianWen.UI.Abstractions/GuiderTab.cs` | GPU renderer-agnostic guider tab |
| `TianWen.Lib.CLI/Tui/TuiGuiderTab.cs` | TUI guider tab |

## Files to Modify

| File | Change |
|------|--------|
| `TianWen.UI.Abstractions/GuiAppState.cs` | Add `Guider` to `GuiTab` enum |
| `TianWen.UI.Abstractions/GuiSignals.cs` | Add `SwitchToGuiderSignal` if needed |
| `TianWen.Lib.CLI/TuiSubCommand.cs` | Register 5th tab, F5 key binding |
| `TianWen.Lib.CLI/Tui/TuiTabBar.cs` | Add 🎯 tab to bar |
| `TianWen.UI.Gui/Program.cs` | Register GPU guider tab |
| `TianWen.UI.Abstractions/VkGuiRenderer.cs` | Add guider tab to sidebar |
| `TianWen.Lib/Sequencing/ISession.cs` | Add `LastGuideImage`, `GuideExposure` |
| `TianWen.Lib/Sequencing/Session.cs` | Implement new ISession members |
| `TianWen.UI.Abstractions/AppSignalHandler.cs` | Auto-switch on calibration |
