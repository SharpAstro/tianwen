using System;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Phase A of <c>docs/plans/plate-solver-performance.md</c>: the solver races both parities and
/// exactly one of them is real work, so the first seed clear of chance stops the other.
///
/// <para><b>The saving is work that does not happen, which the output cannot show</b> -- the WCS is
/// byte-identical whether or not the loser ran to exhaustion. So these assert on
/// <see cref="CatalogPlateSolver.LastParityRace"/> as well as on the solve, or they would pass just
/// as happily with the cancellation deleted. Measured over the 96 frozen Vela frames, the losing
/// parity spends 259.5M hypotheses against the winner's 8.1M: 97% of the seed's whole cost.</para>
/// </summary>
[Collection("Imaging")]
public class PlateSolveParityRaceTests(ITestOutputHelper output)
{
    /// <summary>The flip E2E's own field, at the ROI where a synthetic frame carries enough Tycho-2 stars.</summary>
    private const double FieldRaHours = 5.74, FieldDecDeg = 20.0;
    private const double FocalLengthMm = 480.0;
    private const int Roi = 2048;

    private static async System.Threading.Tasks.Task<(Image Image, ImageDim Dim, WCS Hint, CatalogPlateSolver Solver)>
        BuildSolvableFakeFieldAsync(System.Threading.CancellationToken ct)
    {
        var preset = FakeCameraDriver.GetPresetForId(1);
        var pixelScaleArcsec = CoordinateUtils.PixelScaleArcsec(preset.PixelSize, FocalLengthMm);
        var db = await SharedCatalogDB.InitAsync(ct);

        var magCutoff = Math.Min(15.0, SyntheticStarFieldRenderer.DetectabilityMagCutoff(1.0, 10.0));
        var stars = SyntheticStarFieldRenderer.ProjectCatalogStars(
            FieldRaHours, FieldDecDeg, FocalLengthMm, preset.PixelSize, Roi, Roi, db, magCutoff);
        var data = SyntheticStarFieldRenderer.Render(
            Roi, Roi, defocusSteps: 0, offsetX: 0, offsetY: 0,
            stars: System.Runtime.InteropServices.CollectionsMarshal.AsSpan(stars),
            exposureSeconds: 10.0, noiseSeed: 4242, apertureScaleFactor: 1.0);

        var image = new Image([data], BitDepth.Int16, maxValue: 65535f, minValue: 0f, pedestal: 0f,
            new ImageMeta(
                Instrument: "FakeCamera1", ExposureStartTime: DateTimeOffset.UtcNow,
                ExposureDuration: TimeSpan.FromSeconds(10), FrameType: FrameType.Light, Telescope: "Fake",
                PixelSizeX: (float)preset.PixelSize, PixelSizeY: (float)preset.PixelSize,
                FocalLength: (int)FocalLengthMm, FocusPos: 0, Filter: Filter.None, BinX: 1, BinY: 1,
                CCDTemperature: -10, SensorType: SensorType.Monochrome, BayerOffsetX: 0, BayerOffsetY: 0,
                RowOrder: RowOrder.TopDown, Latitude: 0f, Longitude: 0f, Gain: 100, Aperture: 0,
                SensorModel: preset.SensorName));

        return (image, new ImageDim(pixelScaleArcsec, Roi, Roi), new WCS(FieldRaHours, FieldDecDeg),
            new CatalogPlateSolver(db, NullLogger<CatalogPlateSolver>.Instance));
    }

    [Fact(Timeout = 300_000)]
    public async System.Threading.Tasks.Task GivenASolvableFieldThenTheLosingParityIsAbandonedAndTheSolveStands()
    {
        var ct = TestContext.Current.CancellationToken;
        var (image, dim, hint, solver) = await BuildSolvableFakeFieldAsync(ct);

        var result = await solver.SolveImageAsync(image, dim, searchOrigin: hint, cancellationToken: ct);

        result.Solution.ShouldNotBeNull("premise: this field must solve, or the race is untested");
        var s = result.Solution.Value;
        // The field is synthesised at the hint, so a correct solve returns it. Loose because the
        // point here is the race, not centroid precision -- that is the Vela suite's job.
        Math.Abs(s.CenterDec - FieldDecDeg).ShouldBeLessThan(0.5, "solved centre must be the field");

        var race = solver.LastParityRace;
        output.WriteLine($"abandoned={race.AbandonedAParity} reRan={race.ReRanAbandonedParity} " +
            $"centre=({s.CenterRA:F4}h, {s.CenterDec:F3}) scale={s.PixelScaleArcsec:F3}");

        race.AbandonedAParity.ShouldBeTrue(
            "the winning parity seeded clear of chance, so the other one must have been stopped; "
            + "this is the assertion that fails if the cancellation is deleted, since the WCS is identical either way");
        race.ReRanAbandonedParity.ShouldBeFalse(
            "a solve whose winner passes the acceptance gate must never pay for the abandoned half again");
    }
}
