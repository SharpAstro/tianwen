# TODO -- Guider

Part of the TianWen TODO set. See [TODO.md](../../TODO.md) for the index and the active/high-priority list.

## Guider

- [ ] `appState` parameter should probably be an enum (`GuiderStateChangedEventArgs.cs:34`)
- [ ] Decide whether to ship a pretrained neural guide model (or train from scratch per-mount)
- [ ] Guider profile should use profile id (not name) for model persistence and lookup
- [ ] Write guide logs (CSV) into folder next to model weights for post-session analysis
- [ ] Investigate if increasing neural model parameters (wider/deeper MLP) improves guide accuracy
- [ ] Investigate improving pretrained model with real-time mount telemetry data
- [x] Built-in guider receives same mount driver instance via `IMountDependentGuider` wiring in `SessionFactory`
- [x] Support ST-4 guide port as guiding output — `PulseGuideRouter` + `PulseGuideSource` (`?pulseGuideSource=Auto|Camera|Mount` on the guider URI) routes corrections through `ICameraDriver.PulseGuideAsync`. `Auto` prefers the mount (commit 8a08691): camera `CanPulseGuide` only proves an ST-4 *socket* exists (`HasST4Port`), not that a cable is connected
- [ ] Support snap/shutter-release port for external camera triggering

