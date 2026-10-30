using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using System;
using System.IO;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// <see cref="GuideDarkLibrary"/> over real FITS darks on disk, and the built-in guider's frames through it (#1915): a hot
/// pixel and a pedestal in the darks are gone from the corrected frame, darks of another size never serve a frame, and the
/// frame a guider measures is the corrected one.
/// </summary>
public class GuideDarkLibraryTests(ITestOutputHelper output)
{
    private const string Camera = "Guide Cam";
    private const float Pedestal = 500f;

    private static float[,] Flat(int width, int height, float level)
    {
        var plane = new float[height, width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                plane[y, x] = level + ((x * 7 + y * 13) % 5 - 2);
            }
        }
        return plane;
    }

    private static void WriteDark(GuideDarkLibrary library, string date, int n, float[,] plane, double seconds)
    {
        var folder = Path.Combine(library.FolderOf(Camera), date);
        Directory.CreateDirectory(folder);
        var meta = new ImageMeta() with
        {
            ExposureStartTime = new DateTimeOffset(2026, 10, 1, 22, 0, n, TimeSpan.Zero),
            ExposureDuration = TimeSpan.FromSeconds(seconds),
            FrameType = FrameType.Dark,
        };
        new Image([plane], BitDepth.Int16, 65535f, 0f, 0f, meta).WriteToFitsFile(Path.Combine(folder, $"dark_{seconds}_{n}.fits"));
    }

    private static Image Light(float[,] plane) => new Image([plane], BitDepth.Int16, 65535f, 0f, 0f, new ImageMeta());

    [Fact(Timeout = 60_000)]
    public void ADarkLibraryOnDiskSubtractsThePedestalAndMasksTheHotPixelOfTheFramesItCorrects()
    {
        var external = new FakeExternal(output);
        var library = new GuideDarkLibrary(external, NullLogger<GuideDarkLibrary>.Instance);
        library.For(Camera).ShouldBeNull("a camera with no darks has no correction");

        // Two nights of frames at two exposures: 100 of dark current a second on a pedestal of 500, and one hot pixel.
        foreach (var (date, n) in new[] { ("2026-10-01", 0), ("2026-10-01", 1), ("2026-10-02", 2) })
        {
            foreach (var seconds in new[] { 1.0, 2.0 })
            {
                var plane = Flat(160, 120, Pedestal + (float)(100 * seconds));
                plane[30, 40] = 60000f;
                WriteDark(library, date, n, plane, seconds);
            }
        }

        var correction = library.For(Camera).ShouldNotBeNull();
        correction.DarkCount.ShouldBe(2, "one master per exposure");
        correction.MaskedPixels.ShouldBeGreaterThan(0);

        // A 1.5 s frame: scaled between the two masters' implied pedestal and dark current.
        var light = Flat(160, 120, Pedestal + 150f);
        light[30, 40] = 60000f;
        light[80, 120] += 3000f;
        var corrected = correction.Correct(Light(light), TimeSpan.FromSeconds(1.5));

        var data = corrected.GetChannel(0).Data;
        data[80, 120].ShouldBe(3000f, 6f, "the star stands on a sky the dark took away");
        data[10, 10].ShouldBe(0f, 6f);
        data[30, 40].ShouldBeLessThan(10f, "the hot pixel takes its neighbours' level");
        var star = GuideStarReadout.Measure(corrected).ShouldNotBeNull();
        star.X.ShouldBe(120, 0.5);
        star.Y.ShouldBe(80, 0.5);
    }

    [Fact(Timeout = 60_000)]
    public void DarksOfAnotherSizeAreMastersOfTheirOwnAndNeverServeOrMaskAFrameOfAnotherSize()
    {
        var external = new FakeExternal(output);
        var library = new GuideDarkLibrary(external, NullLogger<GuideDarkLibrary>.Instance);
        var small = Flat(80, 60, Pedestal);
        small[10, 10] = 60000f;
        var big = Flat(160, 120, Pedestal);
        big[30, 40] = 60000f;
        WriteDark(library, "2026-10-01", 0, small, 1);
        WriteDark(library, "2026-10-01", 1, big, 1);

        var correction = library.For(Camera).ShouldNotBeNull();
        correction.DarkCount.ShouldBe(2, "a bin-2 dark and a bin-1 dark share no master");

        var larger = Light(Flat(320, 240, Pedestal));
        correction.Correct(larger, TimeSpan.FromSeconds(1)).ShouldBeSameAs(larger, "no master and no map has that size");

        // Each size is masked by its own map: (10, 10) is a defect of the small frame only.
        var smallLight = Flat(80, 60, Pedestal);
        smallLight[10, 10] = 5000f;
        var bigLight = Flat(160, 120, Pedestal);
        bigLight[10, 10] = 5000f;
        correction.Correct(Light(smallLight), TimeSpan.FromSeconds(1)).GetChannel(0).Data[10, 10].ShouldBeLessThan(10f);
        correction.Correct(Light(bigLight), TimeSpan.FromSeconds(1)).GetChannel(0).Data[10, 10].ShouldBe(5000f - Pedestal, 6f, "the big frame is not masked there, only dark-subtracted");
    }

    [Fact(Timeout = 60_000)]
    public async Task TheFrameABuiltInGuiderMeasuresIsTheCorrectedOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProviderWrapper(new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));
        var external = new FakeExternal(output, clock);
        var library = new GuideDarkLibrary(external, NullLogger<GuideDarkLibrary>.Instance);
        var services = new ServiceCollection()
            .AddSingleton<IExternal>(external)
            .AddSingleton<ITimeProvider>(clock)
            .AddSingleton<IDeviceHub, DeviceHub>()
            .AddSingleton(library)
            .AddLogging()
            .BuildServiceProvider();

        var camera = new FakeCameraDriver(new FakeDevice(DeviceType.Camera, 1), services);
        await camera.ConnectAsync(ct);
        var driver = new BuiltInGuiderDriver(new BuiltInGuiderDevice(new Uri("guider://BuiltInGuiderDevice/builtin#Built-in Guider")), services);
        var exposure = TimeSpan.FromMilliseconds(100);

        var plain = await driver.CaptureCorrectedFrameAsync(camera, exposure, clock, TimeSpan.FromMilliseconds(1), ct);
        var (width, height) = (plain.Width, plain.Height);
        var plainPeak = Peak(plain);
        plain.Release();
        plainPeak.ShouldBeGreaterThan(100f, "the frame as the camera delivers it has light in it");

        // A dark as bright as the whole frame leaves nothing of it, which only the library's correction can do.
        var dark = new float[height, width];
        MemoryMarshalSpan(dark).Fill(65000f);
        var folder = Path.Combine(library.FolderOf(camera.Name), "2026-06-15");
        Directory.CreateDirectory(folder);
        var meta = new ImageMeta() with
        {
            ExposureStartTime = clock.GetUtcNow(),
            ExposureDuration = exposure,
            FrameType = FrameType.Dark,
        };
        new Image([dark], BitDepth.Int16, 65535f, 0f, 0f, meta).WriteToFitsFile(Path.Combine(folder, "dark.fits"));

        var corrected = await driver.CaptureCorrectedFrameAsync(camera, exposure, clock, TimeSpan.FromMilliseconds(1), ct);
        Peak(corrected).ShouldBeLessThan(plainPeak, "the guider's frame has its dark taken out before a star is measured");
        corrected.Release();
    }

    private static Span<float> MemoryMarshalSpan(float[,] plane) => System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref plane[0, 0], plane.Length);

    private static float Peak(Image image)
    {
        var plane = image.GetChannel(0).Data;
        var peak = float.MinValue;
        foreach (var v in plane)
        {
            peak = Math.Max(peak, v);
        }
        return peak;
    }
}
