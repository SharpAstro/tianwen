using System;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.Comets;
using TianWen.Lib.Astrometry.Focus;
using TianWen.Lib.Astrometry.PlateSolve;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TianWen.Lib.Extensions;

public static class AstrometryServiceCollectionExtensions
{
    /// <summary>
    /// Adds all implemented plate solvers (as singleton, they are supposed to be stateless), and the celestial object database.
    /// </summary>
    /// <param name="services"></param>
    /// <param name="cometQueryUri">
    /// Overrides the JPL SBDB comet-query endpoint with a snapshot of the same query response.
    /// Used by the browser host, where JPL's missing CORS headers make the live API unreachable:
    /// its CI deploy bakes the query result as a same-origin static asset and points this at it.
    /// Null (default) = the live JPL API.
    /// </param>
    /// <param name="apparitionSeedUri">
    /// Points at a publish-time snapshot of the per-object current-apparition overlay. Same reason as
    /// <paramref name="cometQueryUri"/> and a second endpoint, because JPL serves the two from different
    /// hosts: the bulk set comes from the SBDB query API, the per-object refinement from Horizons, and
    /// NEITHER sends CORS headers. Baking only the first left the browser retrying the second forever.
    /// A snapshot that declares itself sealed also switches the per-object fetch off entirely.
    /// Null (default) = no seed, so the overlay is built live from Horizons as before.
    /// </param>
    /// <returns></returns>
    public static IServiceCollection AddAstrometry(this IServiceCollection services, Uri? cometQueryUri = null, Uri? apparitionSeedUri = null) => services
        // Factory lambda so the typed ILogger<CatalogPlateSolver> gets resolved and
        // upcast to the non-generic ILogger ctor parameter. The shorter
        // AddSingleton<IPlateSolver, CatalogPlateSolver>() form leaves DI unable to
        // fill a non-generic ILogger and the solver runs with a null logger -- which
        // silently hid CatalogPlateSolver's internal LogDebug stream.
        .AddSingleton<IPlateSolver>(sp => new CatalogPlateSolver(
            sp.GetRequiredService<ICelestialObjectDB>(),
            sp.GetRequiredService<ILogger<CatalogPlateSolver>>()))
        .AddSingleton<IPlateSolver, AstapPlateSolver>()
        .AddSingleton<IPlateSolver, AstrometryNetPlateSolverMultiPlatform>()
        .AddSingleton<IPlateSolver, AstrometryNetPlateSolverUnix>()
        .AddSingleton<IPlateSolverFactory, PlateSolverFactory>()
        .AddSingleton<ICelestialObjectDB, CelestialObjectDB>()
        // Comet elements: a keyless SBDB fetch cached weekly; the source uses a shared static HttpClient
        // (no per-call typed client needed given the weekly TTL), the repository holds the immutable map.
        // Factory lambda (not AddSingleton<I, T>) so the optional endpoint override reaches the ctor;
        // a null override is exactly the previous registration (live JPL API, shared static client).
        .AddSingleton<ISbdbCometSource>(sp => new SbdbCometSource(
            cometQueryUri, sp.GetRequiredService<ILogger<SbdbCometSource>>()))
        // Per-object refinement over the bulk set: Horizons' osculating elements for the apparition in
        // progress. Fetched only for a comet someone is looking at, and only when the bulk record is a
        // revolution or more old -- which is the case that puts a marker degrees off (10P: 9.3).
        .AddSingleton<IHorizonsCometSource>(sp => new HorizonsCometSource(
            sp.GetRequiredService<ILogger<HorizonsCometSource>>()))
        // The publish-time overlay snapshot, when the host has one. A null URI is the desktop
        // configuration: no seed, so the source reports a miss and the live path above is used.
        .AddSingleton<IApparitionSeedSource>(sp => new ApparitionSeedSource(
            apparitionSeedUri, sp.GetRequiredService<ILogger<ApparitionSeedSource>>()))
        .AddSingleton<ICometRepository, CometRepository>();
}
