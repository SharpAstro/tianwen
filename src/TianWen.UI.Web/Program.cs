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
builder.Services.AddAstrometry();

await builder.Build().RunAsync();
