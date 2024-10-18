using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.Focus;
using TianWen.Lib.Astrometry.PlateSolve;
using Microsoft.Extensions.DependencyInjection;

namespace TianWen.Lib.Extensions;

public static class AstrometryServiceCollectionExtensions
{
    /// <summary>
    /// Adds all implemented plate solvers (as singleton, they are supposed to be stateless).
    /// Adds an <see cref="ImageAnalyser"/>.
    /// </summary>
    /// <param name="services"></param>
    /// <returns></returns>
    public static IServiceCollection AddAstrometry(this IServiceCollection services) => services
        .AddSingleton<IPlateSolver, AstapPlateSolver>()
        .AddSingleton<IPlateSolver, AstrometryNetPlateSolverMultiPlatform>()
        .AddSingleton<IPlateSolver, AstrometryNetPlateSolverUnix>()
        .AddSingleton<IPlateSolver, CombinedPlateSolver>()
        .AddScoped<IImageAnalyser, ImageAnalyser>()
        .AddSingleton<ICelestialObjectDB, CelestialObjectDB>();
}
