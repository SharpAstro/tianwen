using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using DIR.Lib;
using SharpAstro.Png;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// On demand (#1159): the viewer's Best stack of a REAL capture, run as <c>tianwen-fits</c> runs it (<see cref="ViewerE2E"/>: the SER
/// dropped, the panel's planet and telescope set, Shift+K), and the master it opens rendered as the viewer shows it, through the
/// CPU mirror of the shader with the uniforms the viewer computes for it. The picture to put beside <c>planetary stack</c>'s preview of
/// the same capture. Set <c>TIANWEN_BEST_STACK_PROBE</c> to the capture's path, and optionally <c>TIANWEN_BEST_STACK_PROBE_PLANET</c>
/// (a planet's name, when the path names none) and <c>TIANWEN_BEST_STACK_PROBE_APERTURE</c> (mm, a Newtonian, 254 when unset). The
/// masters are written beside the capture, as the viewer writes them, and the rendering beside them as <c>*.viewer.png</c>.
/// </summary>
[Collection("Viewer")]
public class ViewerBestStackProbe(ITestOutputHelper output)
{
    private const string EnvVar = "TIANWEN_BEST_STACK_PROBE";

    [Fact(Timeout = 3_600_000)]
    public async Task TheViewersBestStackOfARealCaptureAsTheViewerShowsIt()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(EnvVar) is { Length: > 0 }, $"{EnvVar} not set");
        var capture = Path.GetFullPath(Environment.GetEnvironmentVariable(EnvVar) ?? "");
        var ct = TestContext.Current.CancellationToken;

        await using var e2e = ViewerE2E.Start(1f);
        e2e.Host.HandleDropFile(capture);
        await e2e.PumpUntilAsync(() => e2e.State.SequencePath == capture, "the capture to open", ct);

        if (Environment.GetEnvironmentVariable(EnvVar + "_PLANET") is { Length: > 0 } planetName)
        {
            e2e.State.PlanetaryBody = PlanetaryNameOf(planetName);
        }
        e2e.State.PlanetaryApertureMm = int.TryParse(Environment.GetEnvironmentVariable(EnvVar + "_APERTURE"), out var mm) ? mm : 254;
        e2e.State.PlanetaryDesign = OpticalDesign.Newtonian;

        var started = Stopwatch.StartNew();
        e2e.Key(InputKey.K, InputModifier.Shift);
        var sharpened = Path.Combine(Path.GetDirectoryName(capture) ?? ".", $"master_{Path.GetFileNameWithoutExtension(capture)}_sharpened.fits");
        // A real capture takes minutes, past what PumpUntilAsync waits, so the loop is run here at a tenth of a second a frame.
        while (!e2e.IsShowing(sharpened) || e2e.Controller.IsLoadPending)
        {
            e2e.Frame();
            await Task.Delay(100, ct);
        }
        for (var i = 0; i < 5; i++)
        {
            e2e.Frame();
        }
        output.WriteLine($"{Path.GetFileName(capture)}: the best stack opened after {started.Elapsed.TotalSeconds:0} s; {e2e.State.StatusMessage}");

        var document = e2e.Controller.Document.ShouldNotBeNull();
        var image = document.UnstretchedImage;
        var uniforms = document.ComputeStretchUniforms(e2e.State.StretchMode, e2e.State.StretchParameters);
        output.WriteLine($"opened in {e2e.State.StretchMode}: black {uniforms.Pedestal.R:0.00000}, scale {uniforms.Rescale.R:0.000}, midtones {uniforms.Midtones.R:0.000}");

        var rgba = new ushort[image.Width * image.Height * 4];
        image.RenderStretchedRgba16(uniforms, rgba);
        var png = Path.ChangeExtension(sharpened, ".viewer.png");
        await File.WriteAllBytesAsync(png, PngWriter.EncodeRgba16(rgba, image.Width, image.Height, new PngWriteOptions { Cicp = CicpChunk.Srgb }), ct);
        output.WriteLine($"wrote {png}");
    }

    private static CatalogIndex PlanetaryNameOf(string name)
        => TianWen.Lib.Imaging.Planetary.PlanetaryCaptureName.Named(name) ?? throw new ArgumentException($"{name} names no planet");
}
