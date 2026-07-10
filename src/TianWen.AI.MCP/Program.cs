using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TianWen.AI.MCP.Tools;
using TianWen.Lib.Extensions;

// MCP server hosts read-only debugging tools over stdio JSON-RPC. Spawned by
// Claude Code (or another MCP-aware client) as a child process; the client
// pipes JSON-RPC messages through stdin/stdout. Anything on stdout that isn't
// a valid JSON-RPC message corrupts the wire, so all logging is forced to
// stderr below.

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(options =>
{
    // Route ALL logs to stderr -- stdout is reserved for the JSON-RPC channel.
    // Without this any LogInformation from TianWen.Lib would garble the wire.
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

// Wire just the TianWen.Lib service surface the v1 tool set needs. Astrometry
// gives us ICelestialObjectDB + ICometRepository (for catalog tools); AddExternal
// supplies the ITimeProvider + IExternal the comet cache depends on (weekly-TTL
// SBDB fetch written to AppData/SmallBodies). The rest comes online as later
// phases add tools that touch sessions / devices / fits viewers.
builder.Services.AddExternal();
builder.Services.AddAstrometry();

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInstructions = """
            TianWen MCP server -- read-only debugging access to the TianWen
            astronomy library. Tool categories:
              - fits.*       Inspect FITS files (headers, stats, stars, plate solve, pixels)
              - stars.*      Per-star profile / radial / gallery PNG for visual inspection
              - catalog.*    Tycho-2 / HIP / HD / NGC / common-name lookups
              - spcc.match   Per-gate funnel diagnostic for the photometric WB matcher
              - stack.*      Aggregate masters in a stacking output dir
              - profile.*    List + read user equipment profiles (read-only)
              - devices.*    Enumerate available devices via all sources (NO connect)
              - app.*        AppData paths, catalog init phase timings
              - log.*        Tail + grep TianWen log files
            All tools are read-only. Device discovery enumerates but never
            connects; profile tools never write.
            """;
    })
    .WithStdioServerTransport()
    .WithTools<FitsTools>()
    .WithTools<CatalogTools>()
    .WithTools<LogTools>();

await builder.Build().RunAsync();
