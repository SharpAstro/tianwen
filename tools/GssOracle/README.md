# GssOracle — SkyWatcher protocol oracle from GSServer's own client

Headless harness that drives GSServer's (Green Swamp Server) `GS.SkyWatcher`
classes — the de-facto reference client for the SkyWatcher motor-controller
protocol — against a scripted serial port, and records the exact wire bytes GSS
emits per scenario. No ASCOM, no COM registration, no GSServer process: the
scripted `ISerialPort` is handed straight to `SkyQueue.Start(...)` and scenarios
are issued through the same public command objects (`SkyAxisSlew`,
`SkyAxisPulse`, `SkyAxisGoToTarget`, `SkySetSouthernHemisphere`, ...) that
GS.SkyApi uses.

The recorded transcript is committed as
`src/TianWen.Lib.Tests/Data/gss-oracle-transcripts.json` and consumed by
`SkywatcherGssOracleTests`:

- our `SkywatcherProtocol.EncodeMotionMode` must round-trip every `:G` payload
  GSS produced (north and south),
- per-scenario pinned payloads (tracking, pulses, gotos, fast slews),
- RA pulses must be live `:I`-only rate changes (no stop/start),
- gotos must order `:H` → `:M` (break-point steps) → `:J`,
- and the full transcript is replayed into `FakeSkywatcherSerialDevice` — every
  GSS command must be accepted with a grammar-matching reply, byte-identical
  for the static EQ6 parameter queries ("GSS could drive our fake").

## Regenerating the fixture

Requires the GSServer checkout as a sibling of the `sharpastro` folder
(`../../other/GSServer` relative to this repo). The project targets net472 and
builds x86 to match GSS's Debug libraries; it runs fine on win-arm64 under
emulation.

```bash
cd tools/GssOracle
dotnet build
bin/Debug/net472/GssOracle.exe ../../src/TianWen.Lib.Tests/Data/gss-oracle-transcripts.json
```

Re-run after pulling a GSServer update, then re-run
`dotnet test TianWen.Lib.Tests --filter FullyQualifiedName~Skywatcher` and
reconcile any pinned values that legitimately changed.

The scripted port (`ScriptedSerialPort`) models the same canned EQ6 as
TianWen's `FakeSkywatcherSerialDevice` — CPR 9 024 000 (`0x89B200`), timer
1 500 000, high-speed ratio 16, worm 50 133 steps, firmware 3.39 — so the
static query replies of the two fakes must stay byte-identical. GOTOs arrive
instantly so GSS's FullStop poll loops terminate.

## The real `:G` (set motion mode) wire format

Exactly 2 data chars `<func><dir>` (GSS `Commands.SetMotionMode`,
`GS.SkyWatcher/Commands.cs` ~1103). Note the speed bit INVERTS meaning between
GOTO and slew modes:

| func | meaning |
|------|---------|
| `0`  | high-speed GOTO |
| `1`  | low-speed slew (constant speed = tracking / guiding) |
| `2`  | low-speed GOTO |
| `3`  | high-speed slew |

Dir char bits: bit 0 = reverse (0 = forward/CW), bit 1 = southern hemisphere
(`0→2`, `1→3` in the south). Motion follows bit 0; the hemisphere bit is
informational (GSS's pulse direction-change path omits it entirely) but the
firmware uses it when auto-resuming tracking after a GOTO.

Single source of truth on our side: `SkywatcherProtocol.EncodeMotionMode` /
`TryDecodeMotionMode`.

## Other GSS reference points the transcripts encode

- **Southern hemisphere**: tracking gets the NEGATED rate (`SkyServer.SetTracking`,
  `TrackingMode.EqS`) — the RA worm physically reverses — and the axis↔sky
  mapping mirrors with it (`Axes.AxesAppToMount`: `a[0] = 180 - a[0]` for
  GermanPolar SkyWatcher). Both must flip together.
- **RA pulse guiding** (`SkyWatcher.AxisPulse`): same-direction pulses change
  only the step period (`:I` with `trackingRate + guideRate`, then `:I` back);
  stop/`:G`/`:I`/`:J` only when the combined rate changes sign. The f = 1.0
  East zero-rate edge commands `SiderealRate/1000` ("looks stopped") instead of
  halting.
- **`:G` mid-motion is rejected** by real firmware with `!2` ("Motor not
  Stopped") — GSS stops and polls FullStop first (25 ms interval, stop
  re-issued every 5 polls, 3.5 s cap). Our fake emulates the rejection.
- **`:f` axis status** is 3 ASCII nibbles, not 6 hex chars: nibble 0 =
  constant-speed-vs-GOTO / reverse / high-speed bits, nibble 1 = running,
  nibble 2 = init done (GSS `Commands.GetAxisStatus`).
- **GOTO speed tier by distance**: low-speed GOTO within the margin of 640
  sidereal-seconds of steps (~2.7 deg), high-speed beyond; break-point steps
  (`:M`) 3500 for high-speed, 0 for low-speed.
- **Dec micro-GOTO pulses** (`DecPulseGoTo`): duration → exact steps
  (arcsec × steps-per-arcsec) → relative low-speed GOTO, polled to FullStop
  (3.5 s cap). Opt-in in TianWen via `?decPulseGoto=true` on the mount URI.
- **Minimum pulse duration**: 20 ms (`MinPulseDurationRa/Dec`) — below serial
  round-trip latency a pulse is noise.

Not ported (yet): GSS converts configured Dec backlash steps into extra pulse
duration, capped +1000 ms (`SkyWatcher.cs` ~500). See TODO.md § "Mount /
Skywatcher Protocol" for per-item status.
