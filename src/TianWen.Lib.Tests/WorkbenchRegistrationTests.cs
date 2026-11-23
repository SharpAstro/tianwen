using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Extensions;
using TianWen.Lib.Imaging.Workbench;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <c>AddWorkbench()</c> as a host composes it: every operation is built by the container from its constructor, and a
/// host that registered no enhancer and no AI still has them all.
/// </summary>
public sealed class WorkbenchRegistrationTests
{
    [Fact]
    public void AHostWithNoEnhancerHasEveryOperationAndTheRunnerBuiltByTheContainer()
    {
        // ValidateOnBuild builds every registration's constructor now, so one the container cannot fill fails here and
        // not the first time a project names it.
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddWorkbench()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var catalog = provider.GetRequiredService<OperationCatalog>();

        catalog.All.Select(static operation => operation.Description.Id).ShouldBe(
        [
            AddLineOperation.Id, AddStarsOperation.Id, AlignOperation.Id, CombineOperation.Id, ContinuumOperation.Id, CropOperation.Id,
            DeblurOperation.Id, DenoiseOperation.Id, DenoiseColourOperation.Id, EnhanceOperation.Id, FillDigsOperation.Id, GradientOperation.Id,
            LinearFitOperation.Id, LrgbOperation.Id, LuminanceOperation.Id, MatchPsfOperation.Id, PlanetaryStackOperation.Id, ProtectCoresOperation.Id,
            RemoveStarsOperation.Id, SolveOperation.Id,
        ]);
        provider.GetServices<IOperation>().Count().ShouldBe(catalog.All.Count, "the catalog holds every operation registered, each once");
        provider.GetRequiredService<WorkbenchRunner>().ShouldNotBeNull();
        provider.GetRequiredService<OperationCatalog>().ShouldBeSameAs(catalog, "one catalog a host");
    }

    /// <summary>
    /// The solve is built by hand because it takes the factory's pick: left to the container, a constructor that asks
    /// for an <see cref="IPlateSolver"/> is handed the LAST one registered, which is not the factory.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task TheSolveStepAsksTheSolverFactoryAndNotTheLastSolverRegistered()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = new FixedPlateSolver(solution: null);
        var other = new FixedPlateSolver(solution: null);
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IPlateSolverFactory>(factory)
            .AddSingleton<IPlateSolver>(other)
            .AddWorkbench()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var solve = provider.GetRequiredService<OperationCatalog>().All.OfType<SolveOperation>().Single();
        var image = WorkbenchFixtures.Master(seed: 1);

        await solve.SolveAsync(new SolveSettings(), image, "some.fits", say: null, ct);

        factory.Solves.ShouldBe(1, "the step asks the factory");
        other.Solves.ShouldBe(0, "and not the solver registered after it");
    }
}
