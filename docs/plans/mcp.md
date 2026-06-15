# TianWen MCP server for AI-driven debugging

## Goal

Expose TianWen's catalog/imaging/stacking operations as MCP (Model Context Protocol) tools so an AI assistant (Claude Code, or any MCP-aware client) can directly inspect FITS frames, run star detection, plate-solve, query the catalog, dump SPCC funnel diagnostics, etc. — without having to ask the human to copy-paste log output or run CLI commands.

Concretely: when Claude is debugging a SPCC blue-deficit problem, it should be able to call `spcc.match(path)` and get back a structured per-gate funnel breakdown (detect → no-catalog → tol-miss → photometry-miss → kappa-rejected → accepted). Today the only way to extract that data is for a human to run `tianwen stack`, grep the log, and paste the numbers.

## Scope

| In | Out |
|---|---|
| Read-only analysis of FITS, catalog, stacking outputs | Mount / camera / focuser **control** (slew, expose, move) |
| Trigger stack/solve operations against existing files | Capture orchestration (sessions, scheduling) |
| Render stretched previews + return file paths | Live GUI / TUI integration |
| Tail/grep log files | Mutate session profiles or planner state |
| **Enumerate** devices via all `IDeviceSource<T>` implementations | **Connect to** devices (Initialize / SetConnected) |
| **List + read** profiles | **Write / edit** profiles |
| Surface AppData paths + init timings | — |

Device control + session orchestration deliberately stay off-limits to the AI client — they're action surfaces that need a human in the loop. Discovery (USB SDK enumerate, Alpaca UDP discovery, mDNS scan for OnStep, COM-port list) is read-only -- it tells the AI "here are the devices that would be available if a session were started" without opening serial ports or initialising drivers. Profile read-only access lets the AI see how the user's equipment is configured (which is essential context when debugging a session log) without giving it edit power.

## Project layout

```
src/TianWen.AI.MCP/
├── TianWen.AI.MCP.csproj             # OutputType=Exe, AssemblyName=tianwen-mcp
│                                      # PublishAot=true, IsAotCompatible=true, IsTrimmable=true
├── Program.cs                         # Host.CreateApplicationBuilder + AddMcpServer
├── mcp-template.json                  # Per-Drawboard convention: claude_code-style registration template
├── register.ps1 / register.sh         # Helper scripts to add this server to a Claude Code mcp.json
├── Tools/
│   ├── Fits/
│   │   ├── FitsHeaderTool.cs          # fits.header(path)
│   │   ├── FitsStatsTool.cs           # fits.stats(path, channel?)
│   │   ├── FitsFindStarsTool.cs       # fits.find_stars(path, snr_min?, max?)
│   │   ├── FitsPlateSolveTool.cs      # fits.plate_solve(path, hint_ra?, ...)
│   │   ├── FitsRenderPngTool.cs       # fits.render_png(path, out?, stretch?)
│   │   └── FitsPixelsTool.cs          # fits.pixels(path, points[])
│   ├── Stars/
│   │   ├── StarProfileTool.cs         # stars.profile(path, x, y, size?)
│   │   ├── StarRadialProfileTool.cs   # stars.radial_profile(path, x, y, size?)
│   │   └── StarGalleryPngTool.cs      # stars.gallery_png(path, n?, grid?)
│   ├── Catalog/
│   │   ├── CatalogLookupTool.cs       # catalog.lookup("TYC 1799-1441-1")
│   │   ├── CatalogSpatialTool.cs      # catalog.spatial(ra, dec, radius)
│   │   └── CatalogTycPrefixTool.cs    # catalog.tyc_prefix("425")
│   ├── Spcc/
│   │   └── SpccMatchTool.cs           # spcc.match(path) -> funnel diagnostic
│   ├── Stack/
│   │   └── StackSummaryTool.cs        # stack.summary(output_dir)
│   ├── Profile/
│   │   ├── ProfileListTool.cs         # profile.list()
│   │   └── ProfileGetTool.cs          # profile.get(id)
│   ├── Devices/
│   │   ├── DevicesDiscoverTool.cs     # devices.discover(source?)
│   │   └── DevicesCapabilitiesTool.cs # devices.capabilities(uri)
│   ├── App/
│   │   ├── AppPathsTool.cs            # app.paths()
│   │   └── AppCatalogTimingTool.cs    # app.catalog_init_timing()
│   └── Log/
│       ├── LogTailTool.cs             # log.tail(path, lines?)
│       └── LogGrepTool.cs             # log.grep(path, pattern)
└── (no separate Json/ directory needed — ModelContextProtocol package handles
   schema generation from [Description] attributes; tool output is plain `string`
   in the simple case or a domain type the package serializes for us)
```

`TianWen.AI.MCP` references `TianWen.Lib` directly. No reference to `TianWen.UI.*` (the MCP tool surface stays headless — `MasterPreviewRenderer`'s SPCC path moves through the lib by hand if needed, or the tool launches a sub-process call into `tianwen stack` for rendering).

## Wire protocol

Use the first-party **`ModelContextProtocol` NuGet package** (Microsoft + Anthropic, v1.1.0 at time of writing). Mature pattern — there are multiple sibling MCP servers in adjacent repos using it, including at least one that ships AOT-published with the same `DIR.Lib` + `SharpAstro.Png` dependency set TianWen would use.

**Bootstrap** (canonical, copied from `seq-mcp/Program.cs`):

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(options =>
{
    // stderr only -- stdout is reserved for JSON-RPC.
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services.AddSingleton<ICelestialObjectDB, CelestialObjectDB>();   // init lazily on first tool call
builder.Services.AddSingleton<IPlateSolverFactory, PlateSolverFactory>();
// ...other TianWen.Lib services

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInstructions = """
            TianWen MCP server: read-only debugging tools for FITS frames, catalogs,
            stacking outputs, and device discovery. Examples:
              - "Inspect master_SoL_120s.fits -- show stats + star count + plate-solve result"
              - "Run spcc.match and tell me which funnel gate is dropping the most stars"
              - "What's in profile id 'imaging-home-obs'?"
              - "Discover all ASCOM/ZWO/QHY/Meade devices currently visible"
            """;
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();  // <-- discovers [McpServerToolType] classes

await builder.Build().RunAsync();
```

**Tool definition** (canonical, from `seq-mcp/Tools/QueryEventsTools.cs`):

```csharp
[McpServerToolType]
public class FitsTools
{
    [McpServerTool, Description("Read FITS header keywords from a file.")]
    public static async Task<string> Header(
        IFileSystem fs,
        [Description("Absolute path to a FITS file.")] string path,
        CancellationToken ct)
    {
        // ...
    }
}
```

The package handles JSON-RPC framing, `initialize` / `tools/list` / `tools/call` dispatch, parameter schema generation from `[Description]` attributes, and exception → JSON-RPC error mapping. We just write the tool method bodies.

**AOT is a hard requirement** (matching every other TianWen-shipped binary: `tianwen`, `tianwen-server`, `tianwen-gui`, `tianwen-fits` all set `PublishAot=true`). Sibling-repo precedent confirms `ModelContextProtocol` 1.x publishes AOT-clean when used with the explicit `WithTools<T>()` registration pattern and `static` tool methods.

**The AOT-friendly registration pattern**:

```csharp
// Per-tool class -- one .WithTools<T>() call each, chained.
// This is the AOT-friendly registration; do NOT use WithToolsFromAssembly().
builder.Services
    .AddMcpServer(options => { options.ServerInstructions = "..."; })
    .WithStdioServerTransport()
    .WithTools<FitsTools>()
    .WithTools<CatalogTools>()
    .WithTools<SpccTool>()
    .WithTools<StackTool>()
    .WithTools<ProfileTools>()
    .WithTools<DevicesTools>()
    .WithTools<AppTools>()
    .WithTools<LogTools>();
```

Tool methods are **`static`** -- the package supports both static and instance, but static keeps AOT trim sets smaller and avoids per-call instance allocation. Parameters carry `[Description]`; the package source-generates the JSON schema. Return type is anything `System.Text.Json` can serialise -- usually `string` for human-readable output, `record` types for structured returns. The package handles serialisation through its own JsonSerializerContext.

No hand-rolled pump. No JIT fallback. Phase A's first commit IS the AOT publish working.

## Tool surface (v1)

| Tool | Inputs | Output | Side effects |
|---|---|---|---|
| `fits.header` | path | KV map of all FITS keywords | none |
| `fits.stats` | path, channel? | min/max/mean/median/MAD/percentiles per channel | none |
| `fits.find_stars` | path, snr_min?, max_stars? | array of {x, y, hfd, fwhm, ecc, flux, snr} | none |
| `fits.plate_solve` | path, hint_ra?, hint_dec?, radius?, scale? | WCS solution + match counts | none |
| `fits.render_png` | path, out_path?, stretch? | URI to PNG | writes file at out_path or temp |
| `fits.pixels` | path, points[(x,y)] | per-pixel rgba or float | none |
| `stars.profile` | path, x, y, size? (default 21) | record below | none |
| `stars.radial_profile` | path, x, y, size? | `{ radii: float[], mean: float[], n_pixels: int[] }` -- JSON, ~20 bins | none |
| `stars.gallery_png` | path, n?, grid? (default 5×5) | PNG image content (MCP image type) of top-N brightest stars as cutout mosaic | writes a temp PNG, returns image content inline |
| `catalog.lookup` | designation | RA/Dec/Mag/colour + cross-refs | none |
| `catalog.spatial` | ra, dec, radius_deg | array of nearby objects | none |
| `catalog.tyc_prefix` | query string | array of matching TYC | none |
| `spcc.match` | path | funnel: detect/no-cat/tol-miss/photo-miss/k-rej/accepted + per-gate examples | none |
| `stack.summary` | output_dir | per-master {WB, match-counts, paths} | none |
| `profile.list` | — | array of {id, name, slug, equipment slot summary, modified-utc} | none |
| `profile.get` | id | full profile JSON (already user-readable) | none |
| `devices.discover` | source? (filter to one IDeviceSource) | array of {uri, source, name, type, isConnected, capabilities-preview} | enumerate via SDK / Alpaca UDP / mDNS / COM-port scan — no `Initialize` call |
| `devices.capabilities` | uri | type-specific {cooling? filter wheel slots? focuser step range? mount tracking rates?} | read-only state probe; never sets `Connected=true` if not already |
| `app.paths` | — | {profiles, logs, planner, planet-cache, …} under `%LOCALAPPDATA%/TianWen/` | none |
| `app.catalog_init_timing` | — | per-phase `LastInitPhaseTimings` from the singleton DB | triggers init if not yet loaded |
| `log.tail` | path, lines? | last N lines | none |
| `log.grep` | path, pattern, context? | matching lines + line numbers | none |

The `spcc.match` tool is the direct payoff for the funnel-debugging conversation we just had: instead of grepping a stack-run log for "from N/M Tycho-2 matches", the AI can call this and get the full per-gate count breakdown plus example rejections (5 sample stars that hit each gate). That makes "why is SPCC under-counting?" debuggable in one tool call.

## Star profile data format

The `stars.profile` tool returns a JSON record that mixes structured metadata with a base64-encoded float32 pixel block:

```json
{
  "x_centroid": 1024.87,            // refined sub-pixel centroid (Gaussian fit on the cutout)
  "y_centroid": 768.34,
  "hfd": 2.41,                       // px
  "fwhm": 3.12,                      // px (Gaussian sigma * 2*sqrt(2*ln(2)))
  "ellipticity": 0.08,
  "background": 0.0124,              // local sky bg (median of cutout perimeter ring)
  "peak": 0.481,                     // background-subtracted peak value
  "flux": 88.2,                      // integrated background-subtracted flux
  "snr": 134.2,
  "cutout": {
    "width": 21,
    "height": 21,
    "channels": 1,                   // 3 for RGB cutouts
    "dtype": "float32",
    "background_subtracted": true,
    "pixels_b64": "<base64 of width*height*channels float32 LE bytes>"
  }
}
```

**Why this split**:

| Component | Format | Justification |
|---|---|---|
| Metrics (`hfd`, `fwhm`, ...) | Structured JSON | Already what the existing `ImagedStar` exposes; trivial to consume |
| Pixel block | base64 float32 LE | A 21×21 single-channel cutout = 1.76 KB raw = ~2.4 KB base64 -- compact even for galleries of many stars. **Float values matter for star analysis** (peak/bg/SNR computations need precision the JSON ASCII repr can't preserve concisely). Float32 LE is the universal in-memory layout for `Image`'s sample arrays so no conversion is needed server-side, and the AI client can do `Convert.FromBase64String(...).Cast<byte, float>()` to view it. |
| Visual inspection (Q: "do these stars look round?") | `stars.gallery_png` returning MCP image content | Vision is the natural channel for "look at" -- a 5×5 grid of stretched cutout thumbnails (~525×525 px PNG) compresses to <50 KB, and I can pattern-match diffraction spikes / halos / asymmetry directly. Reading numerical pixel arrays is feasible but vision is the right tool for this question. |
| Radial profile (1D, ~10-20 bins) | JSON `float[]` arrays | Too small for base64 to win; readable arrays are easier to reason about (`[1.0, 0.93, 0.72, 0.41, ...]` is immediately a star shape) |

The base64 path scales: a `stars.gallery` tool that returns 25 cutouts would cost ~60 KB of base64 vs ~50 KB of PNG — for *raw values*, base64 is competitive with PNG even before considering precision loss in PNG.

## Resources surface (v1)

| URI scheme | Maps to |
|---|---|
| `tianwen:fits/<absolute-path>` | A FITS file -> served as `image/fits` with header preview as `text/plain` companion |
| `tianwen:log/<absolute-path>` | A log file -> tail-followable via `resources/subscribe` |
| `tianwen:catalog/<index>` | Specific catalog index (e.g. `TYC_425-2502-1`) -> JSON object |
| `tianwen:profile/<profile-id>` | Read-only TianWen profile JSON |

Resources are URI-addressable and discoverable via `resources/list`. Tools that emit files (`fits.render_png`) return a `tianwen:fits/...` URI so the AI can follow up with `resources/read` to look at the PNG via vision.

## Phasing

### Phase A — stdio pump + handshake + 3 trivial tools
Stand up the wire. Tools: `fits.header`, `catalog.lookup`, `log.tail`. Validates AOT publish, validates JSON contracts, validates Claude Code can spawn + talk to `tianwen-mcp`. ~1-2 days.

### Phase B — FITS analysis tools
`fits.stats`, `fits.find_stars`, `fits.plate_solve`, `fits.pixels`. These wrap existing `TianWen.Lib` APIs 1:1, no new logic. ~1 day.

### Phase B' — Star profile + gallery
`stars.profile`, `stars.radial_profile`, `stars.gallery_png`. The profile tool needs a sub-pixel centroid refinement (small Gaussian fit on the cutout) plus the base64-encoded float32 cutout output. The gallery tool composes the top-N star cutouts into a PNG mosaic using `DIR.Lib.RgbaImageRenderer` + the existing per-channel stretch math (or borrow `MasterPreviewRenderer.RenderAsync` if the Lib extract from Phase D has landed). Returning as MCP image content (not a file path) means the AI sees the pixels directly without a follow-up `resources/read`. ~1-1.5 days.

### Phase C — SPCC funnel diagnostic
The marquee tool. Requires extending `Tycho2ColorCalibration.MatchStars` to count rejections per gate (the work we already discussed in the prior thread). Adds the `[stack] ... funnel=...` line to the CLI as a side benefit, plus exposes the same data through `spcc.match`. ~1-2 days.

### Phase D — PNG render + catalog tools + resources
`fits.render_png` (composes `MasterPreviewRenderer` from `UI.Abstractions` — needs the project ref or a thin Lib-level extract), `catalog.spatial`, `catalog.tyc_prefix`, resources surface. ~1-2 days.

### Phase E — Profiles + AppData paths + catalog init timing
`profile.list`, `profile.get`, `app.paths`, `app.catalog_init_timing`. All thin wrappers around existing TianWen.Lib API surfaces (`Profile.LoadAsync`, `AppDataPaths`, `CelestialObjectDB.LastInitPhaseTimings`). Quick win. ~0.5 day.

### Phase F — Device discovery
`devices.discover` walks the registered `IDeviceSource<T>` implementations. The trick is that discovery for some sources (Alpaca UDP, OnStep mDNS) is asynchronous + time-bounded — the tool needs a sensible default timeout (~3 s) and emit progressive results. `devices.capabilities` queries each driver's capability surface WITHOUT calling `Initialize` (use the existing `IDeviceDriver.SupportsCapability` pattern). The non-trivial bit is making sure no source accidentally connects — needs a code audit of each `IDeviceSource<T>.EnumerateAsync` path. ~1-2 days.

### Phase G — stack summary + log grep
Convenience tools that aggregate the data the AI would otherwise piece together from multiple file reads. ~0.5 day.

Total estimate: ~8-10 days of focused work. Phase A alone delivers a usable scaffold; each subsequent phase strictly adds tools without breaking earlier ones.

## Open design questions

1. ~~**AOT vs JIT publish**~~: settled — AOT, with `PublishAot=true`, `InvariantGlobalization=true`, `WithTools<T>()` chained per class. Expected binary size ~30-40 MB after trim.
2. **Reference to `UI.Abstractions`**: `MasterPreviewRenderer` lives there for layering reasons (it pulls in `AstroImageDocument`, the stretch pipeline, etc.). The MCP server probably needs *just* the render-from-FITS-to-PNG path. Options: (a) project-reference UI.Abstractions in MCP (cheap, but couples MCP to the UI assembly tree), (b) extract `RenderPngFromMaster` into a thin Lib-level helper. Lean (b) for layering.
3. **Process model**: MCP servers are long-lived per-client (stdio session lasts as long as the client is connected). The TianWen catalog DB takes ~270 ms warm to init (`catalog-binary-format.md`); init once on first tool call that needs it, the `_isInitialized` fast path makes subsequent calls free. Same singleton-via-DI pattern as `seq-mcp`'s `SeqConnection`.
4. **Device-source enumeration safety** (Phase F): each `IDeviceSource<T>.EnumerateAsync` must be audited to confirm it doesn't open serial ports or set `IDeviceDriver.Connected = true`. ASCOM Alpaca UDP discovery is benign. OnStep mDNS scan is benign. Native SDK enumerate (ZWO/QHY) is benign — but check. Meade serial / Skywatcher serial enumerate via `SerialPort.GetPortNames()` (just lists COM names, doesn't open). Document the audit result in the tool's XML doc + add an integration test that asserts no driver moves into `Connected` state during enumeration.
5. **Security**: MCP servers can receive any path. The tool surface accepts absolute paths for `fits.*` / `log.*`; we don't sandbox. Acceptable for a developer-tool context. Document it explicitly.

## Out of scope (deliberate)

- **WebSocket MCP transport** — stdio covers the local-dev case. Hosting can already expose HTTP/WebSocket via `TianWen.Hosting` if a remote API is needed.
- **Device control tools** — would require an active TianWen session + careful safety story. Run the actual GUI / `tianwen-server` for that.
- **Multi-frame stacking from MCP** — long-running. Better to surface the existing `tianwen stack` CLI invocation via a `bash` tool than reimplement progress streaming over JSON-RPC.
- **Authentication / multi-tenant** — single local dev tool, not a service.

## Done means

- `dotnet publish TianWen.AI.MCP -c Release -r win-arm64` produces a `tianwen-mcp.exe` (~30-40 MB after AOT trim, similar to the existing `tianwen-fits` / `tianwen-server` binaries).
- The `register.ps1` / `register.sh` helper scripts (following the Drawboard convention) add this server to a Claude Code `mcp.json`:
  ```json
  {
    "mcpServers": {
      "tianwen": {
        "command": "C:/path/to/tianwen-mcp.exe",
        "args": []
      }
    }
  }
  ```
  ...exposing all v1 tools via the MCP tab in Claude Code.
- The "blue deficit" investigation from this session can be reproduced as: `spcc.match` against `master_SoL_120s.fits` → returns the funnel JSON → AI proposes a diagnosis based on which gate dominates. Today that takes ~30 minutes of human-driven `tianwen stack` runs + log greps. With the MCP server it's one tool call.
- Side benefit: the same `[McpServerTool]` methods can be invoked from `tests/TianWen.AI.MCP.Tests` (xUnit) for unit coverage, matching the Drawboard convention (`seq-mcp/tests/SeqMcp.Tests`).
