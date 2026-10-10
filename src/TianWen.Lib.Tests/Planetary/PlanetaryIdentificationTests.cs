using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using SharpAstro.Ser;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.VSOP87;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// What AUTO reads a capture as (A4, #1391; <see cref="PlanetaryIdentification"/>), on captures written the way the capture programs on
/// hand write theirs: FireCapture's settings, SharpCap's mount pointing and filter wheel, folder names, the frames' rings, a telescope
/// remembered for a camera, and a master AUTO wrote read back.
/// </summary>
public sealed class PlanetaryIdentificationTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempFolders _temp = new TempFolders();

    // 2022-10-09, when Saturn's rings were 15 degrees open: the frames can tell a ringed planet from a round one.
    private static readonly DateTimeOffset RingsOpen = new DateTimeOffset(2022, 10, 9, 11, 20, 0, TimeSpan.Zero);

    public void Dispose() => _temp.Dispose();

    [Fact(Timeout = 60_000)]
    public async Task FireCapturesSettingsGiveThePlanetTheFilterAndTheTelescope()
    {
        var ct = TestContext.Current.CancellationToken;
        var capture = Capture(Path.Combine(_temp.Create("fc").FullName, "2022-09-10-0649_2-GB-L.ser"), RingsOpen, Disk.Round);
        File.WriteAllText(Path.ChangeExtension(capture, ".txt"),
            "FireCapture v2.7 06 BETA Settings\n------\nObserver=Grant\nScope=30cm SCT\nCamera=ZWO ASI224MC\nFilter=R\nProfile=Jupiter\n");

        var identity = await PlanetaryIdentification.IdentifyAsync(capture, cancellationToken: ct);
        output.WriteLine(identity.Describe());

        (identity.Planet, identity.PlanetFrom).ShouldBe((CatalogIndex.Jupiter, "FireCapture's profile"));
        (identity.Filter, identity.FilterNm, identity.FilterFrom).ShouldBe(("R", (double?)650, "FireCapture's filter"));
        (identity.ApertureMm, identity.Design, identity.TelescopeFrom).ShouldBe(((int?)300, OpticalDesign.SCT, "FireCapture's settings"));
        identity.Camera.ShouldBe("ZWO ASI224MC");
        identity.WavelengthsNm.ShouldBe([650]);
    }

    [Fact(Timeout = 60_000)]
    public async Task SharpCapsMountPointingAndFilterWheelGiveThePlanetAndTheFilter()
    {
        var ct = TestContext.Current.CancellationToken;
        var capture = Capture(Path.Combine(_temp.Create("sc").FullName, "11_20_00Z_.ser"), RingsOpen, Disk.Round);
        SharpCapSettings(capture, CatalogIndex.Jupiter, RingsOpen, filter: "Green");

        var identity = await PlanetaryIdentification.IdentifyAsync(capture, cancellationToken: ct);
        output.WriteLine(identity.Describe());

        (identity.Planet, identity.PlanetFrom).ShouldBe((CatalogIndex.Jupiter, "the mount's pointing in SharpCap's settings"));
        (identity.Filter, identity.FilterNm, identity.FilterFrom).ShouldBe(("Green", (double?)530, "SharpCap's filter wheel"));
        identity.Camera.ShouldBe("G3M678M", "SharpCap's section names the camera");
    }

    [Fact(Timeout = 60_000)]
    public async Task AMountPointingAndAFolderThatDisagreeNameNeither()
    {
        // On 2024-09-17 a mount reported Saturn's place while the camera showed the full Moon, 9 degrees away, filed under Moon (#1391).
        var ct = TestContext.Current.CancellationToken;
        var capture = Capture(Path.Combine(_temp.Create("moon").FullName, "Moon", "10_09_36Z_.ser"), RingsOpen, Disk.Round);
        SharpCapSettings(capture, CatalogIndex.Jupiter, RingsOpen, filter: null);

        var identity = await PlanetaryIdentification.IdentifyAsync(capture, cancellationToken: ct);
        output.WriteLine(identity.Describe());

        identity.Planet.ShouldBeNull();
        identity.PlanetFrom.ShouldContain("gives Jupiter, but the folder's name gives Moon");
    }

    [Fact(Timeout = 60_000)]
    public async Task TheFramesUnnameASaturnTheFolderGivesWhereNoRingsShow()
    {
        // The owner's 2021 Jupiter captures filed under own-saturn read 1.08 and 1.09 where the rings were 19 degrees open.
        var ct = TestContext.Current.CancellationToken;
        var capture = Capture(Path.Combine(_temp.Create("rings").FullName, "Saturn", "21_41_21_pipp.ser"), RingsOpen, Disk.Round);

        var identity = await PlanetaryIdentification.IdentifyAsync(capture, cancellationToken: ct);
        output.WriteLine(identity.Describe());

        identity.Planet.ShouldBeNull();
        identity.PlanetFrom.ShouldContain("the folder's name gives Saturn, but the frames show no rings");
    }

    [Fact(Timeout = 60_000)]
    public async Task TheFramesMakeSaturnOfAJupiterTheFileGivesWhereRingsShow()
    {
        var ct = TestContext.Current.CancellationToken;
        var capture = Capture(Path.Combine(_temp.Create("rings").FullName, "Jupiter_Red_1120.ser"), RingsOpen, Disk.Ringed);

        var identity = await PlanetaryIdentification.IdentifyAsync(capture, cancellationToken: ct);
        output.WriteLine(identity.Describe());

        identity.Planet.ShouldBe(CatalogIndex.Saturn);
        identity.PlanetFrom.ShouldContain("Saturn's rings in the frames");
    }

    [Theory(Timeout = 60_000)]
    [InlineData("Venus")]
    [InlineData(null)]
    public async Task TheFramesNeverNameAPlanetOnTheirOwnNorOverruleAnotherThanJupiterOrSaturn(string? folder)
    {
        // A crescent Venus reads as elongated as the rings (1.54 to 3.55 on hand), and so does a capture nothing names.
        var ct = TestContext.Current.CancellationToken;
        var root = _temp.Create("rings").FullName;
        var capture = Capture(Path.Combine(folder is null ? root : Path.Combine(root, folder), "08_21_10Z_3.ser"), RingsOpen, Disk.Ringed);

        var identity = await PlanetaryIdentification.IdentifyAsync(capture, cancellationToken: ct);

        identity.Planet.ShouldBe(folder is null ? null : CatalogIndex.Venus);
    }

    [Fact(Timeout = 60_000)]
    public async Task ATelescopeGivenForACameraIsRememberedForItsNextCapture()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output, root: _temp.Create("memory"));
        var capture = Capture(Path.Combine(_temp.Create("own").FullName, "Saturn", "2021-12-16-1119_3_.ser"), RingsOpen, Disk.Ringed,
            instrument: "ZWO ASI462MC", telescope: "telescope");

        var before = await PlanetaryIdentification.IdentifyAsync(capture, external: external, cancellationToken: ct);
        before.ApertureMm.ShouldBeNull("SharpCap's default field and no telescope word on the path");
        before.TelescopeFrom.ShouldBe("none named or remembered for ZWO ASI462MC");

        await PlanetaryTelescopeMemory.RememberAsync(external, "zwo  ASI462MC", 254, OpticalDesign.Newtonian, ct);
        var after = await PlanetaryIdentification.IdentifyAsync(capture, external: external, cancellationToken: ct);
        output.WriteLine(after.Describe());

        (after.ApertureMm, after.Design, after.TelescopeFrom).ShouldBe(((int?)254, OpticalDesign.Newtonian, "remembered for ZWO ASI462MC"));
        after.Telescope.ShouldNotBeNull().ObstructionRatio.ShouldBe(0.25, "a Newtonian's usual obstruction, as the viewer's panel takes it");

        // What was given beats what was remembered.
        var given = await PlanetaryIdentification.IdentifyAsync(capture, new PlanetaryIdentityGiven(ApertureMm: 102, Design: OpticalDesign.Cassegrain),
            external, ct);
        (given.ApertureMm, given.TelescopeFrom).ShouldBe(((int?)102, "given"));
    }

    [Fact(Timeout = 120_000)]
    public async Task AnAutoMasterSaysWhatItWasReadAsAndReadsBackSo()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _temp.Create("auto").FullName;
        var capture = Capture(Path.Combine(folder, "Jupiter_Red_2022-10-09T11_20_00_OTA1.ser"), RingsOpen, Disk.Round,
            telescope: "254 mm f/4.7 Newtonian, SW 250PDS");
        var identity = await PlanetaryIdentification.IdentifyAsync(capture, cancellationToken: ct);
        (identity.Planet, identity.FilterNm, identity.ApertureMm, identity.TelescopeFrom)
            .ShouldBe((CatalogIndex.Jupiter, (double?)650, (int?)254, "the capture's header"));

        PlanetaryBestStackResult result;
        using (var stream = SerFrameStream.Open(capture))
        {
            result = await PlanetaryAuto.RunAsync(stream, identity, cancellationToken: ct);
        }
        var paths = PlanetaryAuto.OutputPaths(folder, Path.GetFileNameWithoutExtension(capture));
        try
        {
            PlanetaryAuto.Write(result, paths);
        }
        finally
        {
            result.Stack.Master.Release();
            result.Sharpened.Release();
            result.Layer?.Master.Release();
        }
        Path.GetFileName(paths.Sharpened).ShouldBe("master_Jupiter_Red_2022-10-09T11_20_00_OTA1_auto_sharpened.fits");

        Image.TryReadFitsFile(paths.Sharpened, out var master).ShouldBeTrue();
        try
        {
            var meta = master.ImageMeta;
            (meta.ObjectName, meta.Telescope, meta.Aperture, meta.Filter.FilterNameForFits).ShouldBe(("Jupiter", "254 mm Newtonian", 254, "Red"));
            var read = await PlanetaryIdentification.IdentifyMasterAsync(master, paths.Sharpened, cancellationToken: ct);
            output.WriteLine(read.Describe());
            (read.Planet, read.PlanetFrom).ShouldBe((CatalogIndex.Jupiter, "the master's OBJECT"));
            (read.FilterNm, read.FilterFrom).ShouldBe(((double?)650, "the master's FILTER"));
            (read.ApertureMm, read.Design, read.TelescopeFrom).ShouldBe(((int?)254, OpticalDesign.Newtonian, "the master's header"));
        }
        finally
        {
            master.Release();
        }
    }

    private enum Disk { Round, Ringed }

    // A short mono capture of a textured disk (Round) or a globe in an ellipse twice and a half as wide as it is tall (Ringed), a frame
    // every 10 ms from `start`.
    private static string Capture(string path, DateTimeOffset start, Disk disk, string instrument = "", string telescope = "")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        const int n = 112, frames = 24;
        var random = new Random(11);
        using (var writer = new SerWriter(path, n, n, SerColorId.Mono, 8, instrument: instrument, telescope: telescope))
        {
            var frame = new byte[n * n];
            for (var i = 0; i < frames; i++)
            {
                var (cx, cy) = (56 + (random.NextDouble() * 2) - 1, 56 + (random.NextDouble() * 2) - 1);
                for (var y = 0; y < n; y++)
                {
                    for (var x = 0; x < n; x++)
                    {
                        var (dx, dy) = (x - cx, y - cy);
                        var inside = disk is Disk.Round
                            ? (dx * dx) + (dy * dy) < 26 * 26
                            : ((dx * dx) + (dy * dy) < 18 * 18) || ((dx / 46) * (dx / 46)) + ((dy / 16) * (dy / 16)) < 1;
                        var v = inside ? 0.55 + (0.2 * Math.Sin(x * 0.6) * Math.Cos(y * 0.55)) : 0.03;
                        frame[(y * n) + x] = (byte)Math.Clamp(v * 255, 0, 255);
                    }
                }
                writer.AppendFrame(frame, start.AddMilliseconds(10 * i));
            }
        }
        return path;
    }

    // SharpCap's settings beside a capture: its camera's section, the mount's J2000 pointing at `body` where it stood at `at`, the filter.
    private static void SharpCapSettings(string capture, CatalogIndex body, DateTimeOffset at, string? filter)
    {
        VSOP87a.ReduceJ2000(body, at, out var ra, out var dec, out _).ShouldBeTrue();
        var inv = CultureInfo.InvariantCulture;
        static (int, int, double) Split(double value)
        {
            var whole = (int)Math.Floor(Math.Abs(value));
            var minutes = (Math.Abs(value) - whole) * 60;
            return (whole, (int)Math.Floor(minutes), (minutes - Math.Floor(minutes)) * 60);
        }
        var (rh, rm, rs) = Split(ra);
        var (dd, dm, ds) = Split(dec);
        var pointing = string.Create(inv, $"RA={rh:00}:{rm:00}:{rs:00.0},Dec={(dec < 0 ? '-' : '+')}{dd:00}:{dm:00}:{ds:00} (J2000)");
        var text = "[G3M678M]\nFrameType=Light\n"
            + (filter is null ? "" : $"ZWO FilterWheel (1)={filter}\n")
            + $"ASCOM GS Sky Telescope={pointing}\n"
            + string.Create(inv, $"MidCapture={at:yyyy-MM-ddTHH:mm:ss.fffffffZ}\n")
            + "SharpCapVersion=4.1.14310.0\n";
        File.WriteAllText(Path.ChangeExtension(capture, null) + ".CameraSettings.txt", text);
    }
}
