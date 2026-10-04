using System;
using System.Globalization;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging.Degradation;
using TianWen.Lib.Imaging.Optics;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The capture statistics on a Saturn (docs/plans/planetary-restoration.md, S3, #1233): the disk is the globe the ringed limb fit finds,
/// and the sky, the halo and the camera's levels are read outside the rings as well as the globe, so a twin is compared with the real
/// capture on the planet's light and not on where its rings happen to fall.
/// </summary>
public class SaturnCaptureStatisticsTests
{
    private static readonly DateTimeOffset Capture = new DateTimeOffset(2022, 10, 9, 11, 25, 0, TimeSpan.Zero);

    private const int Width = 160;
    private const int Height = 120;
    // In 8-bit ADU, as the real capture is read: its sky stands at 13, its noise there 0.4.
    private const double FullScale = 255;
    private const double Sky = 12.75;
    private const double Peak = 102;

    [Fact(Timeout = 300_000)]
    public async Task TheSkyAroundSaturnIsReadClearOfItsRings()
    {
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Saturn, Capture);
        // A colour plane of the 2022-10-09 capture: the sensor's 0.302"/px at twice the pitch.
        const double Scale = 0.604;
        var radius = aspect.AngularDiameterArcsec / 2 / Scale;
        var placement = new DiskPlacement(80.3, 60.4, radius, NorthAngleDeg: 103.7);
        var render = PlanetaryRender.Render(SaturnLimbFitTests.SyntheticSaturn(), aspect, placement, Width, Height, minnaertK: 0.9, supersample: 2,
            rings: SaturnRings.Main);
        var seen = PsfKernel.Moffat(3, 3).Convolve(render, Width, Height);
        var brightest = 0f;
        foreach (var value in seen)
        {
            brightest = Math.Max(brightest, value);
        }

        var random = new Random(7);
        const int Frames = 24;
        var frames = new float[Frames][,];
        var times = new DateTimeOffset[Frames];
        for (var i = 0; i < Frames; i++)
        {
            frames[i] = new float[Height, Width];
            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    var adu = Math.Round(Sky + (Peak * seen[(y * Width) + x] / brightest) + (0.5 * PhaseScreen.Gaussian(random)));
                    frames[i][y, x] = (float)(Math.Clamp(adu, 0, FullScale) / FullScale);
                }
            }
            times[i] = Capture + TimeSpan.FromMilliseconds(4 * i);
        }

        using var stream = new InMemoryFrameStream(frames, times);
        var statistics = await PlanetaryCaptureStatistics.MeasureAsync(stream,
            new CaptureStatisticsOptions(PlanetaryLimbFit.OptionsFor(aspect)) { FullScaleAdu = FullScale, Pairs = 5 },
            cancellationToken: TestContext.Current.CancellationToken);

        statistics.ShouldNotBeNull();
        var s = statistics;
        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"globe R {s.DiskRadius:0.000} px (put at {radius:0.000}); disk {s.Camera.DiskLevel:0.0000}, local sky {s.Camera.LocalSkyLevel:0.0000} (put at {Sky}); " +
            $"halo {string.Join(", ", s.Halo)}"));
        // The disk is the globe, never the rings' reach: started from the bright area and fitted on it, the globe read 19.8 px, and
        // every region the statistics read was a third too far out.
        s.DiskRadius.ShouldBe(radius, radius * 0.01);
        // Read past the rings as well as the globe, the sky is the sky put in, and the first annulus holds the seeing's glow alone.
        s.Camera.LocalSkyLevel.ShouldBe(Sky, 0.1);
        s.Halo[0].ShouldBeLessThan(0.03 * s.Camera.DiskLevel);
    }
}
