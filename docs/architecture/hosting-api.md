# The hosting API: `TianWen.Hosting` + `TianWen.Server`

Moved out of `CLAUDE.md` (2026-08-22), which keeps the invariants a caller trips over and points
here for the rest. The headless node serves a REST + WebSocket surface plus an ASCOM Alpaca device
plane, all on one ASP.NET Core host, AOT-published as `tianwen-server`.

Related: [../plans/remote-profile.md](../plans/remote-profile.md) (what a client does with this
surface), [stacking-render-pipeline.md](stacking-render-pipeline.md) (what the enhance endpoint
runs), [../plans/server-enhance-job-model.md](../plans/server-enhance-job-model.md) (the multi-job
model this deliberately is *not* yet).

## Two API layers on one host

- **Native v1** (`/api/v1/`): multi-OTA, camelCase JSON, POST for mutations. This is the session
  plane.
- **ninaAPI v2 shim** (`/v2/api/`): single-OTA (maps to OTA[0]), PascalCase JSON, GET for everything.

`IHostedSession` holds `ISession?`, `ActiveProfileId`, `PendingTargets` (pre-session queue, drained
into `ScheduledObservation[]` at `/session/start`), `PendingSchedule`, the outstanding
`PendingPrompt`, and a `Notifications` ring. `EventBroadcaster` (`BackgroundService`) subscribes to
`PhaseChanged` / `FrameWritten` / `PlateSolveCompleted` / `ScoutCompleted` / `GuiderStateChanged` /
`PromptRequested` and pushes through the dual pool of `EventHub`; it is also the node's
**notification recorder** (it already watches every session event, so it writes what it broadcasts
into the ring).

Run: `dotnet run --project TianWen.Server` or `tianwen-server [--port 1888]`.

## Three invariants on the session plane

1. **A pushed schedule beats the target queue.** `POST /session/schedule` takes
   `ScheduledObservationDto[]` and preserves per-filter plans, the planner's altitude-optimised
   `Start`, and `AcrossMeridian`; `PendingTarget` carries none of those and `/session/start` stamps
   `Start = now` on whatever it drains. `/session/start` drains the schedule first and only falls
   back to the queue, so never route a real schedule through `/targets`.
2. **Subscribing to `PromptRequested` takes over the session's unattended answer.** A session answers
   a prompt itself only while *nothing* is subscribed, which is what keeps unattended runs from
   blocking on a step nobody will perform. `EventBroadcaster` is a subscriber, so it restores the
   guarantee: **no WebSocket client attached -> answer immediately with
   `SessionPromptEventArgs.DefaultIfUnanswerable`** (the session's own policy, carried on the prompt
   so it cannot drift); **one attached -> hold indefinitely**, with no timer, because guessing after
   an arbitrary interval fabricates a decision rather than fixing an unresponsive client. The only
   bound is liveness -- if the last observer disconnects while a prompt is outstanding the poll loop
   resolves it. Any new subscriber on a headless path owes the same.
3. **The JSON contract uses numeric enums.** No `JsonStringEnumConverter` is configured on
   `HostingJsonContext`, so every enum crosses as its ordinal. A request DTO with a `required` enum
   is therefore hostile to hand-written callers -- default it (as `ScheduledObservationDto.Priority`
   does) rather than forcing a caller to guess the number.

## Previews go through the shared stretch, never a private one

`PreviewEncoder` (`Api/`) is the one JPEG preview encoder, used by `GET
/api/v1/preview/{otaIndex}` (per-OTA, with an `X-Frame-Number` change token) *and* the nina
`prepared-image`. It runs `StretchSolver` + `Image.RenderStretchedRgba` -- the same pipeline as the
GPU viewer and the CPU/TUI renderer. The shim previously divided by `Image.MaxValue` and called it an
auto-stretch, which renders a linear sub near-black; do not reintroduce a private normalisation here.
It also only ever *reads* the session's frame (`DebayerAsync(normalizeToUnit: false)`), because
`LastCapturedImages` pins a recycled camera buffer.

## The ASCOM Alpaca device plane

`/api/v1/{deviceType}/{n}/{member}` + `/management/...`, wired by `MapAlpacaApi`, so a remote TianWen
consumes this node's devices with the existing `AddAlpaca()` and no new client code.

- **It is a device plane and cannot become the session plane.** Alpaca has no vocabulary for session
  lifecycle, schedule, phase, prompts, notifications, autofocus or flats -- and no Guider device type
  at all. Native v1 stays the session plane by necessity.
- **Ownership is the hub lease, not an Alpaca policy.** Actuation and `Connected=false` answer
  `0x40B` with `DeviceOwnershipGate.Describe()`; reads and `Connected=true` always pass. Never make
  the plane read-only during a session -- every standard client PUTs `Connected=true` before reading,
  so that would make a running rig unreadable.
- **Device numbers come from the ACTIVE PROFILE, in profile order** -- never from discovery, whose
  order varies between scans; a number that moved would point a client at different hardware
  mid-session.

Failures are **HTTP 200 with a non-zero ErrorNumber** (the spec reserves 4xx for malformed
requests). Each payload type needs its own `AlpacaResponse<T>` registration in
`AlpacaServerJsonContext` (the generic-envelope form of the no-`ResponseEnvelope<object>` rule below).
Pinned by `AlpacaServerRoundTripTests`, which drives our own `AlpacaClient` against our own server.

## The enhance endpoint (shipped shape: single-flight)

`TianWen.Server` calls `AddRcAstroAi()` (registers `SharpenPipeline`; the RC-vs-SAS probe stays
deferred, so startup spawns no `rc-astro`). The single-flight `HostedImageEnhancer` (an `Interlocked`
gate) runs `ProcessAsync` on a background task tied to **`ApplicationStopping`, not the request** (so
it outlives the POST and dies only on shutdown), with a **synchronous** `IProgress` relay that swaps
an immutable `EnhanceStatusDto` snapshot atomically (lock-free read; `Progress<T>` would post
out-of-order and could clobber the terminal status).

- `POST /api/v1/image/enhance` -- path-in/path-out via `EnhanceRequestDto`, mirroring `image sharpen`
  rather than uploading pixels. Returns `Enhance started` / `409 already running` / `404` /
  parse-error.
- `GET /api/v1/image/enhance/status` -- returns the concrete `EnhanceStatusDto`.
- `ENHANCE-PROGRESS` + `ENHANCE-COMPLETED` push through `EventBroadcaster` -> `EventHub` on the same
  `WebSocketEventDto` + `Dictionary<string,object?>` path as the session events.

AOT: the three DTOs (`EnhanceRequestDto`, `EnhanceStatusDto`,
`ResponseEnvelope<EnhanceStatusDto>`) are registered in `HostingJsonContext`. Verify by
**publishing** `win-arm64` and smoke-testing the binary -- body binding and the concrete status DTO
are the AOT-fragile parts -- not just building.

## Native-AOT correctness (`tianwen-server` is `PublishAot=true`)

Three things keep the minimal API working under AOT; none are optional, and a normal `dotnet build`
will NOT flag a regression (the IL2026/IL3050 trim/AOT warnings only surface on `dotnet publish -r
<rid>`):

1. **RDG runs in `TianWen.Hosting`, not just the server.** The Request Delegate Generator only
   intercepts `Map*` call sites in the project where it is enabled, and all the endpoints live in the
   `TianWen.Hosting` *library*. So `TianWen.Hosting.csproj` sets
   `<IsAotCompatible>true</IsAotCompatible>` +
   `<EnableRequestDelegateGenerator>true</EnableRequestDelegateGenerator>`. Without this the AOT
   publish emitted ~130 IL2026/IL3050 warnings (one pair per `Map*`) and the endpoints fell back to
   reflection-based delegates. `IsAotCompatible` also turns the trim/AOT analyzers on for the Hosting
   code itself, catching regressions at library-build time.
2. **Both JSON source-gen contexts are registered via `ConfigureHttpJsonOptions`** (in
   `AddHostedSession`): `HostingJsonContext` (camelCase) then `NinaApiJsonContext` (PascalCase) on the
   `TypeInfoResolverChain`. This is what makes **request-body binding** AOT-safe; the POST/PUT
   endpoints that take a complex body (`CreateProfileRequest`, `PendingTarget`, `SetProfileRequest`)
   would otherwise throw `NotSupportedException` at runtime. Responses do not depend on it; every
   `Results.Json(...)` passes an explicit `JsonTypeInfo`.
3. **No `ResponseEnvelope<object>` payloads.** A polymorphic `object` payload cannot be resolved by a
   source-gen context under AOT (it needs the runtime type's metadata). The two offenders were
   replaced with concrete types: `GET /api/v1/session/targets` -> `ResponseEnvelope<PendingTarget[]>`,
   and the ninaAPI `list-devices`/`rescan` anonymous types -> `NinaDeviceListItemDto[]`. **Never
   reintroduce a `ResponseEnvelope<object>` or an anonymous-type payload**; register a concrete DTO in
   the relevant `JsonSerializerContext` instead.

Verify after any endpoint change by *publishing* (not just building) and smoke-testing the binary:
`dotnet publish TianWen.Server -c Release -r win-arm64`, then run `tianwen-server.exe --port <p>` and
`curl` a GET, a complex-body POST, and a previously-`object` endpoint. The only expected publish
warnings are 2 third-party rollups (IL2104/IL3053) from `LibUsbDotNet` (optional Canon-over-USB
discovery; the lib ships no AOT annotations and we do not mask the warning).
