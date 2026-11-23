using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
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
            DeblurOperation.Id, DenoiseOperation.Id, EnhanceOperation.Id, FillDigsOperation.Id, GradientOperation.Id, LinearFitOperation.Id,
            LrgbOperation.Id, LuminanceOperation.Id, MatchPsfOperation.Id, ProtectCoresOperation.Id, RemoveStarsOperation.Id, SolveOperation.Id,
        ]);
        provider.GetServices<IOperation>().Count().ShouldBe(catalog.All.Count, "the catalog holds every operation registered, each once");
        provider.GetRequiredService<WorkbenchRunner>().ShouldNotBeNull();
        provider.GetRequiredService<OperationCatalog>().ShouldBeSameAs(catalog, "one catalog a host");
    }
}
