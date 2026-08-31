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
        BuildSolvableFakeFieldAsync(System.Threading.CancellationToken ct, bool flipY = false)
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

        if (flipY)
        {
            // A vertical flip IS a parity change, with no mirror anywhere in the optics -- which is
            // the plan's own point about FITS row order: a BOTTOM-UP frame read as TOP-DOWN differs
            // from this by nothing. So the correct parity for this frame is the opposite sign, and a
            // solver that had quietly hard-wired one would fail here rather than merely be slower.
            var h = data.GetLength(0);
            var w = data.GetLength(1);
            var mirrored = new float[h, w];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    mirrored[y, x] = data[h - 1 - y, x];
                }
            }
            data = mirrored;
        }

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

    /// <summary>
    /// The SAME field flipped on Y, which is a parity change with no mirror in the optics (exactly
    /// what a BOTTOM-UP frame read as TOP-DOWN is). The other parity must win, and the race must
    /// abandon the other half.
    ///
    /// <para>What it covers: the pick genuinely READS the image. The upright field is won by the
    /// mirror attempt and the flipped one by the standard attempt, so neither parity is hard-wired
    /// and neither is merely winning by being tried first.</para>
    ///
    /// <para><b>What it does NOT cover, measured rather than assumed:</b> the winner flag's two
    /// CONSUMERS -- which half counts as abandoned, and which sign gets re-run -- live in the
    /// acceptance gate's fallback, and a solvable field never reaches it. Reintroducing the original
    /// bug there (tracking the winner with <c>ReferenceEquals</c> against a
    /// <c>readonly record struct</c>, which boxes and is unconditionally false) leaves BOTH of these
    /// tests green. It was checked, precisely because a pair of tests that watch each parity win
    /// looks like it must cover the flag and does not.</para>
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async System.Threading.Tasks.Task GivenTheSameFieldFlippedOnYThenTheOtherParityWinsAndIsNotHardWired()
    {
        var ct = TestContext.Current.CancellationToken;

        var (upright, dimU, hintU, solverU) = await BuildSolvableFakeFieldAsync(ct);
        var uprightResult = await solverU.SolveImageAsync(upright, dimU, searchOrigin: hintU, cancellationToken: ct);
        uprightResult.Solution.ShouldNotBeNull("premise: the upright field must solve");
        var uprightRace = solverU.LastParityRace;

        var (flipped, dimF, hintF, solverF) = await BuildSolvableFakeFieldAsync(ct, flipY: true);
        var flippedResult = await solverF.SolveImageAsync(flipped, dimF, searchOrigin: hintF, cancellationToken: ct);

        output.WriteLine($"upright: winnerIsStd={uprightRace.WinnerIsStd} abandoned={uprightRace.AbandonedAParity}");
        output.WriteLine($"flipped: winnerIsStd={solverF.LastParityRace.WinnerIsStd} abandoned={solverF.LastParityRace.AbandonedAParity} solved={flippedResult.Solution is not null}");

        flippedResult.Solution.ShouldNotBeNull(
            "a Y-flipped field is still a solvable field -- the parity is the only thing that changed");

        var flippedRace = solverF.LastParityRace;
        flippedRace.WinnerIsStd.ShouldBe(!uprightRace.WinnerIsStd,
            "flipping the frame on Y inverts the parity, so the OTHER attempt must win; the same winner "
            + "both times would mean the pick is not actually reading the image");
        flippedRace.AbandonedAParity.ShouldBeTrue("the winning parity still stops its sibling");
        flippedRace.ReRanAbandonedParity.ShouldBeFalse("its winner passes the gate, so nothing is re-run");
    }
}
