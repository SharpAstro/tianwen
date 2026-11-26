using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Astrometry.PlateSolve;
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
    /// <para><b>An operation is registered by its type</b>, and the container fills its constructor: what the host
    /// registered, and the parameter's own default where it registered nothing (a logger, an enhancer), as
    /// <see cref="SharpenPipeline"/>'s roles are filled. The catalog takes every <see cref="IOperation"/> there is
    /// and finds one by the id its own description states, so no id is written a second time as a key, and nothing
    /// asks the container for an operation while a project runs.</para>
    /// </remarks>
    public static IServiceCollection AddWorkbench(this IServiceCollection services)
    {
        services.AddClassicalBackgroundExtraction();
        // Built by hand, one of two (the solve is the other): its constructor takes the non-generic ILogger its verb hands it, and the
        // container fills that with nothing (AddAstrometry says the same of CatalogPlateSolver).
        services.AddSingleton<IOperation>(static sp => new CropOperation(sp.GetService<ILogger<CropOperation>>()));
        services.AddSingleton<IOperation, LinearFitOperation>();
        services.AddSingleton<IOperation, GradientOperation>();
        services.AddSingleton<IOperation, AlignOperation>();
        services.AddSingleton<IOperation, ContinuumOperation>();
        services.AddSingleton<IOperation, AddLineOperation>();
        services.AddSingleton<IOperation, LuminanceOperation>();
        services.AddSingleton<IOperation, LrgbOperation>();
        services.AddSingleton<IOperation, AddStarsOperation>();
        services.AddSingleton<IOperation, FillDigsOperation>();
        services.AddSingleton<IOperation, ScnrOperation>();
        // The embedded catalogue answers for the stars inside a protected core where the host registered it, and a host
        // without it still protects cores (those stars then stay with them): the constructor's own default.
        services.AddSingleton<IOperation, ProtectCoresOperation>();
        // The operations several masters go through in one run, and the recipe as one step. An enhancer the host did
        // not register is the constructor's own default, none: its step says so when it runs, and every other
        // operation still works.
        services.AddSingleton<IOperation, MatchPsfOperation>();
        services.AddSingleton<IOperation, DeblurOperation>();
        services.AddSingleton<IOperation, DenoiseOperation>();
        services.AddSingleton<IOperation, DenoiseColourOperation>();
        services.AddSingleton<IOperation, RemoveStarsOperation>();
        services.AddSingleton<IOperation, CombineOperation>();
        // The enhance runs on the host's pipeline, with whatever enhancers the host registered; a host that registered
        // none gets a pipeline of none, whose every step is then left out.
        services.TryAddSingleton<SharpenPipeline>();
        services.AddSingleton<IOperation, EnhanceOperation>();
        // Built by hand, as the crop is: the solve takes the solver the FACTORY picks among those a host registered,
        // where the container would hand it the last one registered as a plate solver. A host with no plate solver
        // still has every other operation, and its solve step says so when it runs.
        services.AddSingleton<IOperation>(static sp => new SolveOperation(sp.GetService<IPlateSolverFactory>()));
        // The planetary steps. A capture (a SER file) is the image's original; its master is the step's result.
        services.AddSingleton<IOperation, PlanetaryStackOperation>();
        services.AddSingleton<IOperation, PlanetaryComposeOperation>();
        services.AddSingleton<IOperation, PlanetarySharpenOperation>();
        // The stack of a set of frames: a folder is the image's original, and each group's master a result of the step.
        services.AddSingleton<IOperation, StackOperation>();
        // A comet's night stacked on the comet: the frames' folder, and the comet's track as a file. The catalogue its
        // reference frame is solved against and the star remover of its star layer are the host's where it registered
        // them; one without a catalogue still has the step, which says so when it runs with a track.
        services.AddSingleton<IOperation, CometStackOperation>();
        services.TryAddSingleton<OperationCatalog>();
        services.TryAddSingleton<WorkbenchRunner>();
        return services;
    }
}
