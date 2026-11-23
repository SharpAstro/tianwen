using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging.Planetary;

namespace TianWen.Lib.Imaging.Workbench;

/// <summary>
/// The settings of <see cref="PlanetarySharpenOperation"/>: what is said of the master, in place of what it is read to
/// be. AUTO takes no other setting: its sharpening is the measured default.
/// </summary>
public sealed record PlanetarySharpenSettings : PlanetaryIdentitySettings;

/// <summary>
/// A planetary master sharpened again without stacking it again (<c>tianwen planetary sharpen --auto</c>): the master
/// identified off its own header, and sharpened as AUTO sharpens the master it has just stacked
/// (<see cref="PlanetaryAuto.Sharpen"/>). It is how a master a compose joined, or one a step cropped, is sharpened.
/// </summary>
/// <remarks>
/// <para>It runs two routines in their order and nothing between, which the verb's <c>--sharpened</c> runs too
/// (<see cref="SharpenToAsync"/>): <see cref="PlanetaryIdentification.IdentifyMasterAsync"/> and
/// <see cref="PlanetaryAuto.Sharpen"/>.</para>
/// <para><b>A step never asks what telescope a camera was last said to be on</b>
/// (<see cref="PlanetaryTelescopeMemory"/>), as the stack's step does not. The verb's <c>--no-telescope-memory</c> is
/// the same, and a step's command line says it.</para>
/// <para><b>Where the gains cannot be derived the master is still sharpened</b>, by the planetary preset, as a stack's
/// is: no planet with a rotation model, no instant, no telescope, or a limb that does not fit. The step's words say
/// which, and it is no refusal.</para>
/// <para><b>What the master says of itself is more than its bytes</b>, as with a capture: a planet, a filter and a
/// telescope its header does not state are read off its file's and folders' names, which are not in the step's key.
/// A master a step of this project wrote says them in its header.</para>
/// </remarks>
public sealed class PlanetarySharpenOperation : Operation<PlanetarySharpenSettings>
{
    /// <summary>The operation's id.</summary>
    public const string Id = "planetary-sharpen";

    /// <inheritdoc/>
    public override OperationDescription Description { get; } = new(
        Id, Version: 1, Title: "Planetary sharpen", Verb: "planetary sharpen --auto", OperationOutputKind.Stored, ExtraInputs: [],
        Settings:
        [
            new OperationSetting("planet", "The planet, by its name; left out, read off the master."),
            new OperationSetting("filterNm", "A mono master's filter, as its wavelength in nanometres; left out, read off the master."),
            new OperationSetting("apertureMm", "The telescope's aperture in millimetres; left out, read off the master."),
            new OperationSetting("design", "The design of the telescope apertureMm gives, whose usual central obstruction the sharpening takes."),
        ]);

    /// <inheritdoc/>
    protected override JsonTypeInfo<PlanetarySharpenSettings> SettingsInfo => OperationSettingsJsonContext.Default.PlanetarySharpenSettings;

    /// <inheritdoc/>
    public override string? Refusal(PlanetarySharpenSettings settings) => PlanetaryIdentitySettings.RefusalOf(settings);

    /// <inheritdoc/>
    public override ImmutableArray<string> CommandLine(PlanetarySharpenSettings settings, IReadOnlyList<string> inputs, string output)
    {
        var line = ImmutableArray.CreateBuilder<string>();
        line.AddRange(StepCommandLine.Program, "planetary", "sharpen", inputs[0], "--auto", "--no-telescope-memory", "--sharpened", output);
        PlanetaryIdentitySettings.AddTo(line, settings);
        return line.ToImmutable();
    }

    /// <inheritdoc/>
    public override async ValueTask<OperationOutcome> RunAsync(PlanetarySharpenSettings settings, IReadOnlyList<OperationInput> inputs, string outputPath,
        Action<string>? say, CancellationToken cancellationToken)
    {
        if (!inputs[0].TryRead(out var master, out _, out var refusal))
        {
            return OperationOutcome.BadInput(refusal);
        }

        try
        {
            // Never the telescope a camera was last said to be on: a step follows from its master and its settings.
            await SharpenToAsync(master, inputs[0].Path ?? inputs[0].Name, PlanetaryIdentitySettings.GivenOf(settings), external: null, outputPath, say,
                cancellationToken);
            return OperationOutcome.Done;
        }
        finally
        {
            master.Release();
        }
    }

    /// <summary>
    /// <paramref name="master"/> identified, sharpened as AUTO sharpens, and written to <paramref name="outputPath"/>:
    /// the step's routine, and the verb's where it is given <c>--sharpened</c>.
    /// </summary>
    /// <param name="master">The linear master. The caller owns it.</param>
    /// <param name="path">Where it was read from: its file's and folders' names say what its header does not.</param>
    /// <param name="given">What is said of it, in place of what it is read to be.</param>
    /// <param name="external">Where the telescope last said for its camera is kept; null to ask nothing and keep
    /// nothing, as a step does.</param>
    /// <param name="outputPath">The FITS file to write.</param>
    /// <param name="say">Takes each line of what it was identified as and how it was sharpened.</param>
    /// <param name="cancellationToken">Ends the work.</param>
    public static async Task SharpenToAsync(Image master, string path, PlanetaryIdentityGiven given, IExternal? external, string outputPath, Action<string>? say,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(master);
        var identity = await PlanetaryIdentification.IdentifyMasterAsync(master, path, given, external, cancellationToken);
        say?.Invoke($"AUTO: {identity.Describe()}");
        var (sharpened, how) = await Task.Run(() => PlanetaryAuto.Sharpen(master, identity), cancellationToken);
        try
        {
            sharpened.WriteToFitsFile(outputPath);
            say?.Invoke($"wrote {outputPath} ({how})");
        }
        finally
        {
            sharpened.Release();
        }
    }
}
