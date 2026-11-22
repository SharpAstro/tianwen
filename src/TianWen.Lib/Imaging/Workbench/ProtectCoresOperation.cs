using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging.StarRemoval;

namespace TianWen.Lib.Imaging.Workbench;

/// <summary>
/// Core protection alone, the step <c>image remove-stars</c> runs after its remover (<see cref="ExtendedCoreMask"/>,
/// #1658): the brightest part of each galaxy or nebula takes the input back, so a nucleus or knot the remover took as
/// a star is in the starless plate again. <c>image protect-cores</c> calls this.
/// </summary>
/// <remarks>
/// On a plate-solved master the catalogued stars inside a core stay removed (#2242, <see cref="CoreCatalogue"/>). Which
/// catalogue would answer on this computer is part of the step's key. The plate is read as written (its absent pixels
/// are NaN, and an exact zero in it is the remover's value); the master as a master. No remover runs.
/// </remarks>
public sealed class ProtectCoresOperation(ICelestialObjectDB? objectDb = null) : Operation<ProtectCoresSettings>
{
    /// <summary>The operation's id.</summary>
    public const string Id = "protect-cores";

    /// <inheritdoc/>
    public override OperationDescription Description { get; } = new(
        Id,
        Version: 1,
        Title: "Protect cores",
        Verb: "image protect-cores",
        OperationOutputKind.Stored,
        ExtraInputs: [new OperationInputRole("input", "The master the stars were removed from.")],
        Settings:
        [
            new OperationSetting("catalogue", "The star catalogue asked for the stars inside a protected core, which then stay removed (a "
                + "plate-solved master only): auto (the star database ASTAP installs, else the embedded Tycho-2), embedded, or none."),
        ]);

    /// <inheritdoc/>
    protected override JsonTypeInfo<ProtectCoresSettings> SettingsInfo => OperationSettingsJsonContext.Default.ProtectCoresSettings;

    /// <summary>The catalogue that would answer here: a result made with one is not a result made with another.</summary>
    public override string ServedBy(ProtectCoresSettings settings) => CoreCatalogue.Source(settings.Catalogue);

    /// <inheritdoc/>
    public override ImmutableArray<string> CommandLine(ProtectCoresSettings settings, IReadOnlyList<string> inputs, string output)
        =>
        [
            StepCommandLine.Program, "image", "protect-cores", "--input", inputs[1], "--starless", inputs[0], "-o", output,
            "--core-catalogue", CoreCatalogue.Word(settings.Catalogue),
        ];

    /// <inheritdoc/>
    public override async ValueTask<OperationOutcome> RunAsync(ProtectCoresSettings settings, IReadOnlyList<OperationInput> inputs, string outputPath, Action<string>? say,
        CancellationToken cancellationToken)
    {
        if (inputs.Count != 2)
        {
            return OperationOutcome.BadInput($"protect-cores takes a starless plate and the master it came from, and was handed {inputs.Count} image(s)");
        }

        if (!inputs[1].TryReadMaster(out var input, out var wcs, out var refusal)
            || !inputs[0].TryRead(out var starless, out _, out refusal))
        {
            return OperationOutcome.BadInput(refusal);
        }

        if (!OperationInputs.OnOneGrid(input, starless))
        {
            return OperationOutcome.BadInput(OperationInputs.NotOnOneGrid);
        }

        var catalogStars = await CoreCatalogue.ReadAsync(settings.Catalogue, wcs, input.Width, input.Height, objectDb, say, cancellationToken);
        var mask = ExtendedCoreMask.Find(input, ExtendedCoreMask.Options.Default, catalogStars);
        CoreCatalogue.Describe(inputs[0].Name, mask, say);
        OperationOutput.Write(mask.Apply(input, starless), outputPath, wcs);
        say?.Invoke($"wrote {outputPath}");
        return OperationOutcome.Done;
    }
}
