using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// AUTO (#817; A4, #1391): a recorded capture's best stack with nothing asked. The capture is identified
/// (<see cref="PlanetaryIdentification"/>: its planet, its filter, its telescope) and stacked and sharpened by
/// <see cref="PlanetaryBestStack"/> at the measured defaults, since A2 (#1389) found no per-capture tune that carries; the master says
/// what it was identified as. <c>planetary stack --auto</c> and the viewer's Auto view both run it and write it by
/// <see cref="Write"/>, so the two masters are the same bits.
/// </summary>
public static class PlanetaryAuto
{
    /// <summary>
    /// The best stack's options AUTO runs with: the measured defaults, the identity's planet, telescope and filter, the identity written
    /// into the master (<see cref="PlanetaryStackOptions.Optics"/>), and every strength stop fitted, which a viewer switches between without
    /// deriving again (the masters are the same either way).
    /// </summary>
    public static PlanetaryBestStackOptions OptionsFor(PlanetaryIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new PlanetaryBestStackOptions(identity.Planet, identity.Telescope)
        {
            WavelengthsNm = identity.WavelengthsNm,
            FitStops = PlanetarySharpening.StrengthStops,
            Stack = new PlanetaryStackOptions
            {
                Optics = new PlanetaryMasterOptics(identity.TelescopeName ?? "", identity.ApertureMm,
                    identity.Layout == PlanetaryFrameLayout.Mono ? identity.Filter : null),
            },
        };
    }

    /// <summary>The best stack of <paramref name="stream"/> as AUTO makes it of a capture identified as <paramref name="identity"/>; the caller owns the result's images.</summary>
    public static Task<PlanetaryBestStackResult> RunAsync(IPlanetaryFrameStream stream, PlanetaryIdentity identity, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
        => PlanetaryBestStack.RunAsync(stream, OptionsFor(identity), progress, cancellationToken);

    /// <summary>
    /// Where AUTO's masters of a capture named <paramref name="baseName"/> (its file's name, without the extension) go in
    /// <paramref name="outputDir"/>: <c>master_&lt;capture&gt;_auto.fits</c> and its <c>_sharpened</c>, beside the masters a Best stack or a plain
    /// <c>planetary stack</c> writes, never over them.
    /// </summary>
    public static (string Master, string Sharpened) OutputPaths(string outputDir, string baseName)
        => PlanetaryBestStack.OutputPaths(outputDir, baseName + "_auto");

    /// <summary>Both of <paramref name="result"/>'s masters written to <paramref name="paths"/>, with their colour balance's cards: the one writer the CLI and the viewer share.</summary>
    public static void Write(PlanetaryBestStackResult result, (string Master, string Sharpened) paths)
    {
        ArgumentNullException.ThrowIfNull(result);
        var cards = result.Balance?.HeaderCards();
        result.Stack.Master.WriteToFitsFile(paths.Master, null, cards);
        result.Sharpened.WriteToFitsFile(paths.Sharpened, null, cards);
    }
}
