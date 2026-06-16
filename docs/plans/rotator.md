# Rotator device type (per-OTA field rotation)

## Painpoint

TianWen has no field-rotator support: no `IRotatorDriver`, no `DeviceType.Rotator` (only WCS
position-angle math exists). Users with a motorised rotator cannot (a) frame a target to a chosen
sky position angle automatically, or (b) preserve framing across a meridian flip (a GEM flip rotates
the field 180deg). Both are manual today.

ASCOM already defines the device class (`IRotatorV4`: `Position` / `MechanicalPosition` / `IsMoving` /
`MoveAbsolute` / `Sync` / `Reverse` / `Halt`) and Alpaca mirrors it. There are **no vendor-native
rotator SDKs** (even NINA ships only ASCOM/Alpaca/Manual for rotators), so ASCOM + Alpaca is *full*
coverage for this device class -- this is a wrap-the-existing-interop job, not a protocol project.

## Design decision: per-OTA, not a mount singleton

This is the one place the rotator design diverges from every other app. In TianWen a rotator belongs
to **each OTA** (`Setup.Telescopes[i]`), next to that tube's camera / filter wheel / focuser -- a
multi-OTA rig frames each tube independently. The slot lives on the `OTA` record (`OTA.cs`) and the
persisted `OTAData` DTO (`ProfileDto.cs`), exactly parallel to the existing `Cover` slot. It is NOT a
single device hung off `Setup.Mount`. Single-mount / multi-OTA invariant (see `Setup.cs` xmldoc): one
mount, N tubes; the rotator is per-tube.

Consequence: `$$ROTATORANGLE$$` / the FITS rotator keyword is per-OTA, and the framing + post-flip
re-rotate logic fans out over `Setup.Telescopes`, the same shape as auto-focus and the obstruction
scout.

## Validation without hardware

No physical rotator is available, but the repo already runs entirely on fakes + simulators, so this
is a non-issue for everything except the final hardware sign-off:

- **Unit + functional tests** run against a new `FakeRotatorDriver` (deterministic settle model via
  `TimeProvider.CreateTimer`, mirroring `FakeCoverDriver`). This covers the interface contract,
  sync/offset math, 360deg wrap-around, reverse, and the per-OTA framing + post-flip re-rotate
  integration end-to-end under `FakeTimeProvider`.
- **Manual integration** uses the ASCOM Platform **Rotator Simulator** (ships with the Platform) over
  COM, and the **Alpaca OmniSimulator** rotator over HTTP -- exercises the real `AscomRotatorDriver` /
  `AlpacaRotatorDriver` code paths on Windows without owning a rotator.
- **Real-hardware verification is deferred** (flagged in the phasing table), the same way the
  Skywatcher "verify on real hardware" items are parked until the gear is in hand.

## Driver model

`IRotatorDriver : IDeviceDriver`, following the `ICoverDriver` shape (ValueTask getters, `Task
Begin*` actions, a higher-level wait helper as a default interface method):

```csharp
public interface IRotatorDriver : IDeviceDriver
{
    /// <summary>Effective sky position angle in degrees [0,360), offset-adjusted (post-Sync).</summary>
    ValueTask<float> GetPositionAsync(CancellationToken cancellationToken = default);

    /// <summary>Raw mechanical angle in degrees [0,360), independent of any sync offset.</summary>
    ValueTask<float> GetMechanicalPositionAsync(CancellationToken cancellationToken = default);

    ValueTask<bool> GetIsMovingAsync(CancellationToken cancellationToken = default);

    /// <summary>True if the rotator can reverse its direction sense (IRotatorV4.CanReverse).</summary>
    bool CanReverse { get; }

    ValueTask<bool> GetReverseAsync(CancellationToken cancellationToken = default);
    Task SetReverseAsync(bool reverse, CancellationToken cancellationToken = default);

    /// <summary>Smallest reportable step in degrees, -1 if unknown.</summary>
    float StepSize { get; }

    /// <summary>Move to an absolute SKY position angle (degrees), honouring the sync offset.</summary>
    Task BeginMoveAbsolute(float skyPositionAngle, CancellationToken cancellationToken = default);

    /// <summary>Move to an absolute MECHANICAL angle (degrees), ignoring sync offset.</summary>
    Task BeginMoveMechanical(float mechanicalAngle, CancellationToken cancellationToken = default);

    /// <summary>Relative move by an offset in degrees.</summary>
    Task BeginMoveRelative(float offsetDegrees, CancellationToken cancellationToken = default);

    /// <summary>Define the current mechanical position to correspond to this sky position angle.</summary>
    Task SyncToPositionAsync(float skyPositionAngle, CancellationToken cancellationToken = default);

    Task HaltAsync(CancellationToken cancellationToken = default);

    /// <summary>Higher-level: move to a sky PA and poll IsMoving until settled (mirrors
    /// ICoverDriver.TurnOffCalibratorAndWaitAsync). Returns false on timeout/cancel.</summary>
    async ValueTask<bool> MoveToSkyPaAndWaitAsync(float skyPositionAngle, CancellationToken cancellationToken = default)
    {
        await BeginMoveAbsolute(skyPositionAngle, cancellationToken);
        var tries = 0;
        while (await GetIsMovingAsync(cancellationToken)
            && !cancellationToken.IsCancellationRequested
            && ++tries < MAX_FAILSAFE)
        {
            await TimeProvider.SleepAsync(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
        return !await GetIsMovingAsync(cancellationToken);
    }
}
```

Sequencing wrapper `Rotator.cs` mirrors `Cover.cs` exactly:

```csharp
public record Rotator(DeviceBase Device, IServiceProvider ServiceProvider)
    : ControllableDeviceBase<IRotatorDriver>(Device, ServiceProvider);
```

## Phases

| Phase | Scope | Validates with |
|------|-------|----------------|
| **0** | Device-type skeleton: `DeviceType.Rotator`, `IRotatorDriver`, `Rotator` wrapper, per-OTA slot on `OTA` + `OTAData` | compiles; `OTA.DisposeAsync` disposes rotator |
| **1** | Drivers: `AscomDispatchRotator` ([DispatchInterface]), `AscomRotatorDriver`, `AlpacaRotatorDriver`, `FakeRotatorDriver` | `FakeRotator*` unit tests |
| **2** | Discovery + factory: device-record switches + supported-type sets | discovery test + ASCOM/Alpaca simulator |
| **3** | Assignment surfaces: equipment tab / profile / CLI slot + config keys; `SessionFactory` instantiation | assign round-trip test |
| **4** | Sequencing integration: framing-angle on center, post-flip re-rotate, FITS keyword + `$$ROTATORANGLE$$` token | functional session test (fake) |
| **5** | Tests: unit + functional, both hemispheres / multi-OTA | full unit + functional suites green |
| **--** | **Real-hardware verification** | **deferred** -- ASCOM/Alpaca sim now; physical rotator later |

### Phase 0 -- skeleton

- `DeviceType.cs:5` -- add `Rotator` member (XML doc -> `IRotatorDriver`).
- New `Devices/IRotatorDriver.cs` (above).
- New `Sequencing/Rotator.cs` (above).
- `Sequencing/OTA.cs:8` -- add `Rotator? Rotator` to the record; dispose it in `DisposeAsync` (parallel
  to the `Cover` arm at `OTA.cs:24`).
- `Devices/ProfileDto.cs:38` -- add `Uri? Rotator` to `OTAData` (per-OTA persistence). No `ProfileData`
  change (not site-level).

### Phase 1 -- drivers

- New `Devices/Ascom/ComInterop/AscomDispatchRotator.cs` -- `[DispatchInterface] partial` over
  `IRotatorV4`: `Position`, `MechanicalPosition`, `IsMoving`, `CanReverse`, `Reverse` (get/set),
  `StepSize`, `TargetPosition`, `MoveAbsolute(float)`, `MoveMechanical(float)`, `Move(float)`,
  `Sync(float)`, `Halt()`. Template: `AscomDispatchCoverCalibrator.cs` (the source generator fills
  the COM late-binding bodies).
- New `Devices/Ascom/AscomRotatorDriver.cs` -- `: AscomDeviceDriverBase, IRotatorDriver`. Cache
  `CanReverse` / `StepSize` in `InitDeviceAsync` via `SafeGet`; wrap each member with `SafeGet` /
  `SafeDo` / `SafeTask`. Template: `AscomCoverCalibratorDriver.cs`.
- New `Devices/Alpaca/AlpacaRotatorDriver.cs` -- `: AlpacaDeviceDriverBase, IRotatorDriver`. GET
  `position`/`mechanicalposition`/`ismoving`/`canreverse`/`reverse`/`stepsize`; PUT
  `moveabsolute`/`movemechanical`/`move`/`sync`/`halt`/`reverse`. Template:
  `AlpacaCoverCalibratorDriver.cs`.
- New `Devices/Fake/FakeRotatorDriver.cs` -- `: FakeDeviceDriverBase, IRotatorDriver`. State:
  `_mechanical`, `_syncOffset`, `_reverse`, `_targetSky`, `_isMoving`. `Position = (mechanical +
  syncOffset) mod 360` (negated when reversed). `BeginMoveAbsolute` sets `_isMoving`, schedules a
  settle via `TimeProvider.CreateTimer` (configurable `SlewRateDegPerSec`, default e.g. 4 deg/s ->
  duration from angular delta), lands `_mechanical` at the target. Deterministic under
  `FakeTimeProvider`. Template: `FakeCoverDriver.cs`.

### Phase 2 -- discovery + factory

- `Devices/Ascom/AscomDevice.cs:23` -- `DeviceType.Rotator => new AscomRotatorDriver(this, sp)`.
- `Devices/Alpaca/AlpacaDevice.cs:53` -- `AlpacaDeviceType`: `DeviceType.Rotator => "rotator"`.
- `Devices/Alpaca/AlpacaDevice.cs:69` -- `DeviceType.Rotator => new AlpacaRotatorDriver(this, sp)`.
- `Devices/Ascom/AscomDeviceIterator.cs:51` -- `AscomRegistryKeyName`: `DeviceType.Rotator =>
  "Rotator"`; add `DeviceType.Rotator` to `_allSupportedDeviceTypes` (line ~59) so it auto-enumerates.
- `Devices/Alpaca/AlpacaDeviceSource.cs:23` -- add `DeviceType.Rotator` to `SupportedDeviceTypes`.
- `Devices/Fake/FakeDevice.cs:97` -- `DeviceType.Rotator => new FakeRotatorDriver(this, sp)`.
- `Devices/Fake/FakeDeviceSource.cs:11` -- add `DeviceType.Rotator` to `RegisteredDeviceTypes` so a
  fake rotator is discoverable in the GUI (Cover is currently omitted there; for the rotator we want
  it visible so the feature is demoable without hardware).
- No DI change: `AddAscom` / `AddAlpaca` / `AddFake` register the sources generically.

### Phase 3 -- assignment surfaces

- `Sequencing/SessionFactory.cs:67` -- `var rotator = otaData.Rotator is { } uri ? new Rotator(...) :
  null`; pass into the `OTA(...)` ctor (line ~72); add to borrow-logging (line ~116).
- `UI.Abstractions/AssignTarget.cs:27` -- `OTALevel { Field: "Rotator" } => DeviceType.Rotator`.
- `UI.Abstractions/EquipmentActions.cs` -- mirror every `Cover` arm with a `Rotator` arm: assign
  (281), is-assigned (309), unassign (357), resolve-current (386-387, 414), reconcile-uri (834).
- `UI.Abstractions/EquipmentContent.cs:122` -- add `Rotator` to `GetOtaSummaries`.
- `UI.Gui/.../EquipmentTab.cs` -- render a `"  Rotator"` OTA sub-slot after `"  Cover"` (605-608);
  badge text `DeviceType.Rotator => "Rotator"` (2239).
- `Cli/.../ProfileSubCommand.cs:830` -- `DeviceType.Rotator => ota with { Rotator = uri }`.
- `Devices/DeviceQueryKey.cs` -- add `RotatorSkyPaOffset` (`"rotatorSkyPaOffset"`) and `RotatorReverse`
  (`"rotatorReverse"`) following the `FocuserBacklashIn` pattern (enum member + `Key` switch arm; NOT
  transport keys). These let a profile pin a per-OTA mechanical-vs-sky offset and the reverse sense.

### Phase 4 -- sequencing integration (the only novel logic)

- **Target carries a desired PA.** Add `double? PositionAngleDeg` to `Target` (and surface on
  `ProposedObservation`). Mosaic panels already compute a position angle in `MosaicGenerator` -- wire
  that through so each panel's PA flows to the rotator. Null = don't manage rotation (today's
  behaviour).
- **Frame on center.** In `Session.Focus.cs:876` `CenterOnTargetAsync`, after the final plate-solve +
  sync succeeds: if `Setup.Telescopes[telescopeIndex].Rotator is { } rot` and `target.PositionAngleDeg
  is { } pa`, read the solved field sky-PA from the plate-solve WCS (`WCS` rotation), `Sync` the
  rotator to that current PA, then `MoveToSkyPaAndWaitAsync(pa)`. Wrapped in `ResilientInvokeAsync`
  (`AbsoluteMove` preset -- target is absolute, re-issue is safe). Per-OTA.
- **Re-rotate after flip.** In `Session.Imaging.cs:1127` `PerformMeridianFlipAsync`, after the
  post-flip re-center: for each OTA with a rotator + a managed PA, re-apply `MoveToSkyPaAndWaitAsync`
  against the freshly solved PA, so framing survives the 180deg field rotation. (Today nothing drives
  a physical rotator across a flip.)
- **FITS output.** In `Session.IO.cs` write the per-OTA rotator sky PA to a header keyword
  (`ROTATANG`, degrees) the same way `FOCALLEN` / `FOCUSPOS` are written; expose `$$ROTATORANGLE$$`
  for the (separately-tracked) configurable path/filename template.

### Phase 5 -- tests

- `FakeRotatorDriverTests` (unit): move/settle under `FakeTimeProvider`; `Position` honours sync
  offset; 360deg wrap (move 350 -> 10 goes the short way / lands correctly); reverse flips sense;
  `MoveToSkyPaAndWaitAsync` returns true on settle, false on cancel.
- `SessionRotatorTests` (functional, `[Collection("Session")]`, cooperative time pump): an OTA with a
  fake rotator + a target PA -> after center the rotator sits at the target PA; after a forced
  meridian flip the rotator re-applies the PA; a second OTA with no rotator is unaffected (multi-OTA
  isolation). Both hemispheres via `[Theory]`.
- Profile round-trip: assign rotator to OTA, save, reload, assert `OTAData.Rotator` persists; assign /
  unassign / reconcile-uri parity with the `Cover` tests.

## Files

New: `IRotatorDriver.cs`, `Sequencing/Rotator.cs`, `Ascom/ComInterop/AscomDispatchRotator.cs`,
`Ascom/AscomRotatorDriver.cs`, `Alpaca/AlpacaRotatorDriver.cs`, `Fake/FakeRotatorDriver.cs`,
`FakeRotatorDriverTests.cs`, `SessionRotatorTests.cs`.

Edited: `DeviceType.cs`, `OTA.cs`, `ProfileDto.cs`, `AscomDevice.cs`, `AlpacaDevice.cs`,
`AscomDeviceIterator.cs`, `AlpacaDeviceSource.cs`, `FakeDevice.cs`, `FakeDeviceSource.cs`,
`SessionFactory.cs`, `AssignTarget.cs`, `EquipmentActions.cs`, `EquipmentContent.cs`, `EquipmentTab.cs`,
`ProfileSubCommand.cs`, `DeviceQueryKey.cs`, `Target` + `ProposedObservation`, `Session.Focus.cs`,
`Session.Imaging.cs`, `Session.IO.cs`.

## Out of scope / deferred

- **Real-hardware verification** -- no rotator on hand; sign off against ASCOM/Alpaca simulators and
  defer the physical check (same posture as the Skywatcher "verify on real hardware" items).
- **Manual rotator** (software-only, user types the angle) -- NINA ships one; low value here until a
  user asks. The fake covers the demo path.
- **Configurable path/filename template** -- `$$ROTATORANGLE$$` is emitted, but the token-driven
  template itself is the separate item in `docs/todo/sequencing.md`.
- **Dome / SafetyMonitor** -- the other two missing device types; tracked separately in
  `docs/todo/drivers.md` + `TODO.md`. Dome reuses this same device-type-addition recipe but is a
  per-site singleton.
