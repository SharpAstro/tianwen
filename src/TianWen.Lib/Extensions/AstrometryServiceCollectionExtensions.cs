using TianWen.Lib.Astrometry.Catalogs;
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
    /// <returns></returns>
    public static IServiceCollection AddAstrometry(this IServiceCollection services) => services
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
        .AddSingleton<ICelestialObjectDB, CelestialObjectDB>();
}
