using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using TianWen.Cli;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging.Enhancement;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// What an argument line of <c>tianwen stack</c> MEANS, read through the real option definitions. An option that is
/// absent must hand the pipeline the same value a library caller gets from <see cref="StackingOptions"/>: a
/// nullable option answers null when it is not typed, and a null passed by name replaces a record default
/// instead of deferring to it (#1370: the per-frame quality gate ran OFF for every stack that did not type
/// <c>--quality-reject-sigma</c>, though its help said it defaults to 3).
/// </summary>
public sealed class StackOptionDefaultsTests : IDisposable
{
    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    [Fact]
    public async Task TheQualityGateRunsAtTheSharedDefaultWhenTheOptionIsNotTyped()
    {
        var options = await ResolveAsync();

        options.QualityRejectSigma.ShouldBe(FrameQualityFilter.DefaultSigma);
    }

    [Fact]
    public async Task ZeroSwitchesTheRelativeGateOff()
    {
        var options = await ResolveAsync("--quality-reject-sigma", "0");

        options.QualityRejectSigma.ShouldBe(0f);
    }

    [Fact]
    public async Task ATypedSigmaReplacesTheDefault()
    {
        var options = await ResolveAsync("--quality-reject-sigma", "2.5");

        options.QualityRejectSigma.ShouldBe(2.5f);
    }

    /// <summary>
    /// The pixel rejector's two thresholds are the other nullable options, and for them null IS the answer: the record
    /// default is null and <c>StackingPipeline.BuildRejector</c> reads it as "this kind's own threshold", which depends
    /// on the frame count and so cannot be one number here. A typed value still overrides.
    /// </summary>
    [Fact]
    public async Task ThePixelRejectorThresholdsStayUnsetUntilTyped()
    {
        var bare = await ResolveAsync();
        bare.RejectLowSigma.ShouldBeNull();
        bare.RejectHighSigma.ShouldBeNull();

        var typed = await ResolveAsync("--reject-low-sigma", "2.25", "--reject-high-sigma", "2.75");
        typed.RejectLowSigma.ShouldBe(2.25f);
        typed.RejectHighSigma.ShouldBe(2.75f);
    }

    /// <summary>
    /// <c>--require-gain-match</c> was declared, documented as ON by default, and never added to the command: the parser
    /// did not know it, and the value read back for an option the command does not hold is <c>default(bool)</c>, so every
    /// stack accepted a wrong-gain dark the library refuses by default.
    /// </summary>
    [Fact]
    public async Task AWrongGainDarkIsRefusedByDefaultAndTheOptionTurnsThatOff()
    {
        (await ResolveAsync()).RequireGainMatch.ShouldBeTrue();
        (await ResolveAsync("--require-gain-match", "false")).RequireGainMatch.ShouldBeFalse();
    }

    /// <summary>
    /// The class of bug, not the one instance: a bare line resolves to the record a library caller gets, compared WHOLE, so a
    /// member added to <see cref="StackingOptions"/> later is compared without anyone listing it. The list of members this
    /// replaced had already missed <see cref="StackingOptions.EnhanceOptions"/> (#1414). The two members the command always
    /// builds where the record leaves null are compared with what the pipeline reads that null AS: a default
    /// <see cref="DrizzleOptions"/> (<c>StackingPipeline</c> and both drizzle strategies) and
    /// <see cref="EnhanceOptions.Default"/> (the enhance pass), not copied across from the answer under test.
    /// </summary>
    [Fact]
    public async Task ABareLineResolvesToTheRecordsOwnDefaults()
    {
        var resolved = await ResolveAsync();
        var defaults = new StackingOptions(resolved.DataRoot, resolved.OutputDir);

        resolved.ShouldBe(defaults with
        {
            DrizzleOptions = defaults.DrizzleOptions ?? new DrizzleOptions(),
            EnhanceOptions = defaults.EnhanceOptions ?? EnhanceOptions.Default,
        });
    }

    /// <summary>
    /// Runs the real <c>stack</c> command over <paramref name="args"/> and an empty data root, stopping at the
    /// <see cref="StackingOptions"/> it resolved (<see cref="StackSubCommand.OptionsSink"/>) so no pipeline starts.
    /// </summary>
    private async Task<StackingOptions> ResolveAsync(params string[] args)
    {
        StackingOptions? resolved = null;
        var command = new StackSubCommand(
            Substitute.For<IConsoleHost>(),
            NullLogger<StackingPipeline>.Instance,
            Substitute.For<ICelestialObjectDB>(),
            new SharpenPipeline())
        {
            OptionsSink = options =>
            {
                resolved = options;
                return 0;
            },
        }.Build();

        var dataRoot = _folders.Create("stack-options-").FullName;
        var exitCode = await command.Parse([dataRoot, .. args]).InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        exitCode.ShouldBe(0);
        return resolved.ShouldNotBeNull();
    }
}
