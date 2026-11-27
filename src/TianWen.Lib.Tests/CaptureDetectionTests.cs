using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using TianWen.Lib.Imaging.Workbench;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// What a capture folder holds (#2553), on small synthetic FITS folders: the kind read off IMAGETYP, off a folder's name, off
/// the pixels (always marked unsure), a light no dark matches, a file under workdir that is never a frame, and a
/// reassignment that is recorded, changes the stack step's key and is taken by the scan.
/// </summary>
[Collection("Imaging")]
public sealed class CaptureDetectionTests : IDisposable
{
    private const int Size = 32;
    private const float FullScale = 4096f;
    private readonly TempFolders _folders = new();
    private int _serial;

    public void Dispose() => _folders.Dispose();

    // One small FITS: a flat level with a little deterministic noise, so the median is the level; stars (a few bright
    // pixels) when asked.
    private string Write(string folder, string name, FrameType type, double exposure, double level, int gain = 100, float temperature = -10f,
        string target = "", bool stars = false)
    {
        Directory.CreateDirectory(folder);
        var pixels = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                pixels[y, x] = (float)(level + (((x * 7) + (y * 13)) % 5) - 2);
            }
        }

        if (stars)
        {
            // Two 6x6 blocks, so a walk that takes every fourth pixel still meets them.
            foreach (var (bx, by) in new[] { (4, 4), (20, 18) })
            {
                for (var y = by; y < by + 6; y++)
                {
                    for (var x = bx; x < bx + 6; x++)
                    {
                        pixels[y, x] = 3900f;
                    }
                }
            }
        }

        var meta = new ImageMeta
        {
            Instrument = "Synth",
            ExposureStartTime = new DateTimeOffset(2026, 12, 21, 0, 0, 0, TimeSpan.Zero).AddMinutes(_serial++),
            ExposureDuration = TimeSpan.FromSeconds(exposure),
            FrameType = type,
            PixelSizeX = 3.76f,
            PixelSizeY = 3.76f,
            FocalLength = 1000,
            BinX = 1,
            BinY = 1,
            CCDTemperature = temperature,
            SensorType = SensorType.Monochrome,
            ObjectName = target,
            Gain = (short)gain,
            Offset = 25,
            Latitude = 48f,
            Longitude = 11f,
            SensorFullScaleAdu = FullScale,
        };
        var path = Path.Combine(folder, name + ".fits");
        new Image(data: [pixels], bitDepth: BitDepth.Int16, maxValue: FullScale, minValue: 0f, pedestal: 0f, imageMeta: meta).WriteToFitsFile(path);
        return path;
    }

    private string NewCapture() => Path.Combine(_folders.Create("capture-").FullName, "night");

    private static CaptureDetectionOptions NoGrade => new() { GradeLights = false };

    [Fact(Timeout = 60_000)]
    public async Task FramesThatStateTheirTypeAreTakenForIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = NewCapture();
        for (var i = 0; i < 3; i++)
        {
            Write(folder, $"light_{i}", FrameType.Light, 60, 300, target: "M31", stars: true);
            Write(folder, $"dark_{i}", FrameType.Dark, 60, 100);
        }

        var detected = await CaptureDetection.DetectAsync(folder, NoGrade, ct);

        var lights = detected.Groups.Single(group => group.Kind == DetectedGroupKind.Lights);
        lights.Files.Length.ShouldBe(3);
        lights.Detail.ShouldContain("M31");
        lights.Detail.ShouldContain("60 s");
        lights.Detail.ShouldContain("gain 100");
        lights.Files.ShouldAllBe(file => file.Why == "IMAGETYP = Light" && !file.Unsure && file.Kind == FrameType.Light);
        var darks = detected.Groups.Single(group => group.Kind == DetectedGroupKind.Darks);
        darks.Files.Length.ShouldBe(3);
        darks.Files[0].ExposureSeconds.ShouldBe(60);
        darks.Files[0].Gain.ShouldBe(100);
        darks.Files[0].SensorTemperatureC.ShouldBe(-10f);
        darks.Files[0].Camera.ShouldBe("Synth");
        // Lights with a dark that matches, and nothing unsure: nothing to look at.
        detected.Groups.ShouldNotContain(group => group.Kind == DetectedGroupKind.NeedsALook);
    }

    [Fact(Timeout = 60_000)]
    public async Task FramesWithNoTypeTakeItFromTheFolderTheyAreIn()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = NewCapture();
        Write(Path.Combine(folder, "Lights"), "a", FrameType.None, 60, 300, stars: true);
        Write(Path.Combine(folder, "Darks"), "a", FrameType.None, 60, 100);
        Write(Path.Combine(folder, "Darks"), "b", FrameType.None, 60, 100);
        Write(Path.Combine(folder, "Flats"), "a", FrameType.None, 2, 2000);
        Write(Path.Combine(folder, "Flat Darks"), "a", FrameType.None, 2, 100);

        var detected = await CaptureDetection.DetectAsync(folder, NoGrade, ct);

        var byPlace = detected.Files.ToDictionary(file => file.Place);
        byPlace["Lights/a.fits"].Kind.ShouldBe(FrameType.Light);
        byPlace["Darks/a.fits"].Kind.ShouldBe(FrameType.Dark);
        byPlace["Darks/a.fits"].Why.ShouldBe("no IMAGETYP; folder named Darks");
        byPlace["Flats/a.fits"].Kind.ShouldBe(FrameType.Flat);
        byPlace["Flat Darks/a.fits"].Kind.ShouldBe(FrameType.DarkFlat);
        byPlace.Values.ShouldAllBe(file => !file.Unsure, "a folder's name is not a guess");
    }

    [Fact(Timeout = 60_000)]
    public async Task AFrameWithNeitherTypeNorFolderIsGuessedFromItsPixelsAndMarkedUnsure()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = NewCapture();
        Write(folder, "even", FrameType.None, 2, 2000);
        Write(folder, "stars", FrameType.None, 60, 300, stars: true);

        var detected = await CaptureDetection.DetectAsync(folder, NoGrade, ct);

        var even = detected.Files.Single(file => file.Place == "even.fits");
        even.Kind.ShouldBe(FrameType.Flat);
        even.Unsure.ShouldBeTrue();
        even.Why.ShouldBe("no IMAGETYP; even field at 49 % of full scale");
        var stars = detected.Files.Single(file => file.Place == "stars.fits");
        stars.Kind.ShouldBe(FrameType.Light);
        stars.Unsure.ShouldBeTrue();
        stars.Why.ShouldBe("no IMAGETYP; stars in the field");
        var look = detected.Groups.Single(group => group.Kind == DetectedGroupKind.NeedsALook);
        look.Files.Select(file => file.Place).ShouldBe(["even.fits", "stars.fits"]);
    }

    [Fact(Timeout = 60_000)]
    public async Task ALightWithNoMatchingDarkNeedsALook()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = NewCapture();
        for (var i = 0; i < 3; i++)
        {
            Write(folder, $"light_{i}", FrameType.Light, 60, 300, target: "M31", stars: true);
            // Darks of another exposure and gain: no match.
            Write(folder, $"dark_{i}", FrameType.Dark, 10, 100, gain: 300);
        }

        var detected = await CaptureDetection.DetectAsync(folder, NoGrade, ct);

        var look = detected.Groups.Single(group => group.Kind == DetectedGroupKind.NeedsALook);
        look.Files.Length.ShouldBe(3);
        look.Files.ShouldAllBe(file => file.Note != null && file.Note.Contains("no dark matches 60 s") && !file.Unsure);
        // They stay in their own group too, with the same note.
        detected.Groups.Single(group => group.Kind == DetectedGroupKind.Lights).Files.ShouldAllBe(file => file.Note != null);
    }

    [Fact(Timeout = 60_000)]
    public async Task AFileUnderWorkdirIsNeverAFrame()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = NewCapture();
        Write(folder, "light_0", FrameType.Light, 60, 300, stars: true);
        Write(Path.Combine(folder, "workdir", "store"), "result", FrameType.Light, 60, 300, stars: true);

        var scan = await StackingPipeline.ScanFramesAsync(folder, outputDir: null, includeIntegrations: false, includeSuspectedCloud: false, reassigned: default, ct);
        var detected = await CaptureDetection.DetectAsync(folder, NoGrade, ct);

        scan.Frames.Select(frame => Path.GetFileName(frame.Path)).ShouldBe(["light_0.fits"]);
        detected.Files.Select(file => file.Place).ShouldBe(["light_0.fits"]);
    }

    [Fact(Timeout = 120_000)]
    public async Task AReassignmentIsSavedChangesTheStackKeyAndIsTakenByTheScan()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = NewCapture();
        Write(folder, "light_0", FrameType.Light, 60, 300, stars: true);
        Write(folder, "odd", FrameType.None, 2, 2000);
        var made = await WorkbenchProjects.NewAsync(folder, ct);
        made.Refusal.ShouldBeNull();
        File.Exists(Path.Combine(folder, "workdir", "project.json")).ShouldBeTrue();
        var runner = new WorkbenchRunner(new OperationCatalog([new StackOperation()]));
        var workdir = WorkbenchProjects.WorkdirOf(folder);
        var before = (await runner.StatusAsync(workdir, cancellationToken: ct)).Images[0].Steps[0].Key;

        var done = await WorkbenchProjects.ReassignAsync(folder, "odd.fits", FrameType.Bias, ct);

        done.Refusal.ShouldBeNull();
        done.Reassigned.Select(each => each.Format()).ShouldBe(["odd.fits=bias"]);
        (await WorkbenchProjects.ReassignmentsAsync(folder, ct)).Select(each => each.Format()).ShouldBe(["odd.fits=bias"]);
        var after = (await runner.StatusAsync(workdir, cancellationToken: ct)).Images[0].Steps[0].Key;
        after.ShouldNotBe(before, "the stack step's key holds the reassignment");

        var reassigned = await CaptureDetection.DetectAsync(folder, new CaptureDetectionOptions { GradeLights = false, Reassigned = done.Reassigned }, ct);
        var odd = reassigned.Files.Single(file => file.Place == "odd.fits");
        (odd.Kind, odd.Reassigned, odd.Unsure).ShouldBe((FrameType.Bias, true, false));
        odd.Why.ShouldStartWith("set by you (detected: Flat");

        var scan = await StackingPipeline.ScanFramesAsync(folder, null, false, false, done.Reassigned, ct);
        scan.Frames.Single(frame => Path.GetFileName(frame.Path) == "odd.fits").FrameType.ShouldBe(FrameType.Bias);

        // Taken back: the detection stands and the key is the first one.
        (await WorkbenchProjects.ReassignAsync(folder, "odd.fits", null, ct)).Refusal.ShouldBeNull();
        (await runner.StatusAsync(workdir, cancellationToken: ct)).Images[0].Steps[0].Key.ShouldBe(before);

        // Refused in words.
        (await WorkbenchProjects.ReassignAsync(folder, "nothing.fits", FrameType.Dark, ct)).Refusal.ShouldNotBeNull().ShouldContain("not a file of the capture");
        (await WorkbenchProjects.ReassignAsync(folder, "odd.fits", FrameType.Focus, ct)).Refusal.ShouldNotBeNull().ShouldContain("no kind of frame");
    }
}
