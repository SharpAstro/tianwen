using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging.BackgroundExtraction;
using TianWen.Lib.Imaging.Enhancement;
using TianWen.Lib.Imaging.Workbench;

namespace TianWen.Lib.Extensions;

public static class WorkbenchServiceCollectionExtensions
{
    /// <summary>
    /// Registers the operations of the processing application (docs/plans/stacking-workbench.md, W1), the catalog that
    /// finds one by id and the runner of a project's edit lists.
    /// </summary>
    /// <remarks>
    /// Call it after the AI registration (<c>AddTianWenAi()</c>, <c>AddRcAstroAi()</c>) where there is one: the
    /// gradient operation takes the <see cref="IGradientCorrector"/> registered first, and this adds the classical
    /// fit only where no other was registered, so a host with no AI project still has every operation here.
    /// </remarks>
    public static IServiceCollection AddWorkbench(this IServiceCollection services)
    {
        services.AddClassicalBackgroundExtraction();
        services.AddSingleton<IOperation>(static sp => new CropOperation(sp.GetService<ILogger<CropOperation>>()));
        services.AddSingleton<IOperation, LinearFitOperation>();
        services.AddSingleton<IOperation, AlignOperation>();
        services.AddSingleton<IOperation, ContinuumOperation>();
        services.AddSingleton<IOperation, AddLineOperation>();
        services.AddSingleton<IOperation, LuminanceOperation>();
        services.AddSingleton<IOperation, LrgbOperation>();
        services.AddSingleton<IOperation, AddStarsOperation>();
        services.AddSingleton<IOperation, FillDigsOperation>();
        // GetService: the embedded catalogue answers for the stars inside a protected core, and a host without it still
        // protects cores (those stars then stay with them).
        services.AddSingleton<IOperation>(static sp => new ProtectCoresOperation(sp.GetService<ICelestialObjectDB>()));
        services.AddSingleton<IOperation>(static sp => new GradientOperation(
            sp.GetRequiredService<IBackgroundExtractor>(),
            sp.GetRequiredService<IGradientCorrector>(),
            sp.GetService<ILogger<GradientOperation>>()));
        services.TryAddSingleton(static sp => new OperationCatalog(sp.GetServices<IOperation>()));
        services.TryAddSingleton<WorkbenchRunner>();
        return services;
    }
}
