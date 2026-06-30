# Server image-enhance: job model (nice-to-have)

**Status: NOT STARTED** — a deferred enhancement of the Phase 3d single-flight enhance endpoint
(see [rc-astro-enhancers.md](rc-astro-enhancers.md) Phase 3d). Captured here so the design is on
record; build it only when a client actually needs concurrency, history, or per-job cancel.

## What ships today (Phase 3d)

The server enhance endpoint is **single-flight**, modelled on `POST /api/v1/session/start`:

- `POST /api/v1/image/enhance` (body `EnhanceRequestDto`, path-in/path-out) **fires-and-forgets** a
  background enhance and returns immediately — `Enhance started`, or `409 An enhance is already
  running` if one is in flight (an `Interlocked` gate in `HostedImageEnhancer`). **No job id.**
- `GET /api/v1/image/enhance/status` returns the **singleton** `EnhanceStatusDto`
  (`isEnhancing`, `currentStep`, `stepIndex/stepCount`, `percent`, `outputPath`, `succeeded`,
  `error`) — the client polls it until `isEnhancing == false && succeeded != null`.
- The WebSocket (`/api/v1/events`) pushes `ENHANCE-PROGRESS` / `ENHANCE-COMPLETED` through the same
  `EventBroadcaster` → `EventHub` path as the session events.

This matches the rig's reality (a headless mount does one thing at a time; `IHostedSession` is
already single-session) and keeps the client contract trivial: there is only ever *the* job, so
"which job?" is never ambiguous. The cost is no concurrency, no history (the singleton remembers
only the **last** run), and no targeted cancel.

Note: the *enhancer layer is already concurrency-safe* — `EnhanceOptions` is a per-call immutable
record precisely so parallel enhances can diverge without tearing. It is only the **server** that is
gated to single-flight. Lifting that gate is a contained change.

## Why you might want a job model

- **Concurrent enhances** — enqueue several files (e.g. a night's masters) in one go.
- **History** — "what did I enhance an hour ago, and where did the output land?"
- **Targeted cancel** — abort a specific in-flight or queued job by id.

## Proposed shape — job ids + a small queue

### REST

| Verb + route | Returns |
|---|---|
| `POST /api/v1/image/enhance` | `202` + `ResponseEnvelope<EnhanceJobDto>` (state `Queued`/`Running`) — replaces today's `"Enhance started"` string |
| `GET /api/v1/image/enhance/jobs` | `ResponseEnvelope<EnhanceJobDto[]>` — running + queued + a ring of recent completed |
| `GET /api/v1/image/enhance/jobs/{id}` | `ResponseEnvelope<EnhanceJobDto>` (404 if evicted from the ring) |
| `POST /api/v1/image/enhance/jobs/{id}/cancel` | `ResponseEnvelope<string>` — cancels via the job's own CTS |

Keep `GET /api/v1/image/enhance/status` as a back-compat convenience that returns the
most-recent/active job mapped onto the old `EnhanceStatusDto`, so existing poll clients don't break.

### DTO (AOT)

```csharp
public sealed class EnhanceJobDto
{
    public required string Id { get; init; }            // Guid "N"
    public required string State { get; init; }          // Queued | Running | Succeeded | Failed | Cancelled
    public string? InputPath { get; init; }
    public string? OutputPath { get; init; }
    public string? Backend { get; init; }
    public string? CurrentStep { get; init; }
    public int StepIndex { get; init; }
    public int StepCount { get; init; }
    public float Percent { get; init; }
    public string? Error { get; init; }
}
```

Register `EnhanceJobDto`, `EnhanceJobDto[]`, `ResponseEnvelope<EnhanceJobDto>`,
`ResponseEnvelope<EnhanceJobDto[]>` in `HostingJsonContext`. Both WebSocket events gain a `JobId`
key in their `Data` dictionary so a subscriber can correlate. **Every new DTO must be
publish-smoke-tested** per the AOT rule in CLAUDE.md (body binding + the concrete response are the
fragile parts), not just built.

### Implementation sketch

- `HostedImageEnhancer` grows from a single `Interlocked` gate into a **bounded worker** over a
  `System.Threading.Channels.Channel<EnhanceJob>`. **Keep the worker count tiny (1, at most 2).** AI
  enhance is GPU-bound (DirectML); running many concurrently on an integrated GPU risks a TDR
  (the same reason the integration heap is reclaimed before AI enhance — see the GPU-TDR commit).
  The job model's value is **tracking / history / cancel**, not raw parallelism.
- A `ConcurrentDictionary<Guid, EnhanceJobDto>` registry holds immutable snapshots swapped
  atomically per progress tick (the existing lock-free pattern), plus a bounded ring of the last
  *K* completed jobs for history.
- One `CancellationTokenSource` per job (linked to `ApplicationStopping`) so cancel targets a
  single job without touching the others.

## Middle ground (smallest useful step)

If concurrency is *not* needed but history/cancel are: keep single-flight, but assign each run a
`jobId`, keep an in-memory ring of the last *K* `EnhanceJobDto`, and add cancel-current. No queue,
no extra workers — just identity + a short history + one cancel path. This is the cheapest increment
and covers most of the perceived gap.

## When to build this

Trigger: a client genuinely needs to enqueue more than one enhance at a time, or needs job history /
targeted cancel. Until then the Phase 3d single-flight endpoint fully covers the headless rig's
"enhance this master, tell me when it's done" case, and the extra surface area is not worth it.
