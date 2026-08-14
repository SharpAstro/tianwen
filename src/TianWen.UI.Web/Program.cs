using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using TianWen.Lib.Devices;
using TianWen.Lib.Extensions;
using TianWen.UI.Web.Devices;
using TianWen.UI.Web.Pages;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Planner>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// The comet repository fetches JPL SBDB over HTTP; the catalog DB is fully embedded. An HttpClient
// scoped to the app base address covers the wwwroot font fetch at startup and the SBDB call.
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// Minimal browser service graph: the shared clock + a browser IExternal (MEMFS / no serial / no TCP)
// + astrometry (embedded catalog DB + JPL comet repository). Everything else in the desktop
// Program.cs (native camera/mount SDKs, serial mounts, ASCOM/Alpaca, session factory) is either
// inherently non-browser or irrelevant to the planner + atlas.
builder.Services.AddLogging();
builder.Services.AddTimeProvider();
builder.Services.AddSingleton<IExternal, BrowserExternal>();
// JPL sends no CORS headers, so the live SBDB API is permanently unreachable from a browser
// origin. The Pages deploy bakes the SAME query response as a same-origin static asset
// (comets-sbdb.json, curled in CI) and the comet source fetches that instead - the response
// shape, parser, and cache layers are all unchanged. On a dev server without the baked file the
// fetch 404s and the repository degrades to a DSO-only session exactly like the CORS failure did.
//
// The SAME is true of JPL Horizons, which serves the per-object current-apparition refinement from a
// different host and likewise sends no CORS headers. Baking only the bulk set left every stale comet
// drawn on the atlas retrying a request that could never succeed (measured: 45 attempts, 50 s of
// request time in one four-minute session). comets-apparitions.json is that overlay, resolved in CI,
// and it declares itself sealed so the per-object fetch is not attempted here at all.
//
// Both are plain same-origin URLs relative to the app base, so NOTHING here asks whether it is running
// on Pages: the dev server 404s both and degrades along exactly the path a seed-less host takes.
var baseAddress = new Uri(builder.HostEnvironment.BaseAddress);
builder.Services.AddAstrometry(
    cometQueryUri: new Uri(baseAddress, "comets-sbdb.json"),
    apparitionSeedUri: new Uri(baseAddress, "comets-apparitions.json"));

await builder.Build().RunAsync();
