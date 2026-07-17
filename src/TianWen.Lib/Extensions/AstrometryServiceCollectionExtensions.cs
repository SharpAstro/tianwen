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
    /// <returns></returns>
    public static IServiceCollection AddAstrometry(this IServiceCollection services, Uri? cometQueryUri = null) => services
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
        .AddSingleton<ICometRepository, CometRepository>();
}
