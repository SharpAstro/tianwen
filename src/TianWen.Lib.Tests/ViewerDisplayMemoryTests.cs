using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Geometry;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A file's last display is remembered per file (#1814): every display setting comes back, a file rewritten since reads as none, and
/// two paths never share a record.
/// </summary>
public class ViewerDisplayMemoryTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempFolders _folders = new TempFolders();

    public void Dispose() => _folders.Dispose();

    /// <summary>
    /// THE display set, listed once: each entry is a name and a reader. A display setting added to
    /// <see cref="ViewerState.ForAnnotatedExport"/> goes here, into <see cref="Changed"/> and into the record, or the tests below fail.
    /// <c>WaveletDerived</c> and <c>WaveletLimb</c> are deliberately absent (a capture's own, not writable).
    /// </summary>
    private static readonly (string Name, Func<ViewerState, object?> Read)[] DisplayFields =
    [
        (nameof(ViewerState.StretchMode), s => s.StretchMode),
        (nameof(ViewerState.StretchParameters), s => s.StretchParameters),
        (nameof(ViewerState.ChannelView), s => s.ChannelView),
        (nameof(ViewerState.DebayerAlgorithm), s => s.DebayerAlgorithm),
        (nameof(ViewerState.ColorCalibrationEnabled), s => s.ColorCalibrationEnabled),
        (nameof(ViewerState.BackgroundNeutralizationEnabled), s => s.BackgroundNeutralizationEnabled),
        (nameof(ViewerState.BackgroundNeutralizationMethod), s => s.BackgroundNeutralizationMethod),
        (nameof(ViewerState.BackgroundNeutralizationStrength), s => s.BackgroundNeutralizationStrength),
        (nameof(ViewerState.BackgroundReduceEnabled), s => s.BackgroundReduceEnabled),
        (nameof(ViewerState.BackgroundSkyLevel), s => s.BackgroundSkyLevel),
        (nameof(ViewerState.ManualWhiteBalance), s => s.ManualWhiteBalance),
        (nameof(ViewerState.CurvesBoost), s => s.CurvesBoost),
        (nameof(ViewerState.CurvesMode), s => s.CurvesMode),
        (nameof(ViewerState.CurveData), s => string.Join(',', s.CurveData)),
        (nameof(ViewerState.HdrAmount), s => s.HdrAmount),
        (nameof(ViewerState.HdrKnee), s => s.HdrKnee),
        (nameof(ViewerState.FinishContrast), s => s.FinishContrast),
        (nameof(ViewerState.FinishSaturation), s => s.FinishSaturation),
        (nameof(ViewerState.StarStretch), s => s.StarStretch),
        (nameof(ViewerState.StarSaturation), s => s.StarSaturation),
        (nameof(ViewerState.StarMethod), s => s.StarMethod),
        (nameof(ViewerState.StarAmount), s => s.StarAmount),
        (nameof(ViewerState.WaveletSharpenEnabled), s => s.WaveletSharpenEnabled),
        (nameof(ViewerState.WaveletGains), s => string.Join(',', s.WaveletGains)),
        (nameof(ViewerState.DisplayCrop), s => s.DisplayCrop),
    ];

    private static ViewerState Changed() => new()
    {
        StretchMode = StretchMode.Unlinked,
        StretchParameters = new StretchParameters(0.123, -1.7),
        ChannelView = ChannelView.Green,
        DebayerAlgorithm = DebayerAlgorithm.AHD,
        ColorCalibrationEnabled = false,
        BackgroundNeutralizationEnabled = false,
        BackgroundNeutralizationMethod = BackgroundNeutralizationMethod.GreenPivot,
        BackgroundNeutralizationStrength = 0.37f,
        BackgroundReduceEnabled = false,
        BackgroundSkyLevel = 0.071f,
        ManualWhiteBalance = (1.25f, 0.9f, 1.6f),
        CurvesBoost = 0.43f,
        CurvesMode = 2,
        CurveData = [0f, 0.1f, 0.35f, 0.8f, 1f],
        HdrAmount = 0.52f,
        HdrKnee = 0.61f,
        FinishContrast = 0.29f,
        FinishSaturation = 1.37f,
        StarStretch = 0.77f,
        StarSaturation = 0.83f,
        StarMethod = StarStretchMethod.StarStretch,
        StarAmount = 0.58f,
        WaveletSharpenEnabled = true,
        WaveletGains = [1.1f, 1.2f, 0.9f],
        DisplayCrop = new PixelRect(10, 20, 300, 200),
    };

    private string MakeFile(string name, int length = 64)
    {
        var path = Path.Combine(_folders.Create("tianwen-viewer-files-").FullName, name);
        File.WriteAllBytes(path, new byte[length]);
        return path;
    }

    private FakeExternal MakeExternal() => new(output, _folders.Create("tianwen-viewer-memory-"));

    [Fact]
    public void TheChangedStateDiffersFromTheDefaultsInEveryListedField()
    {
        var defaults = new ViewerState();
        var changed = Changed();

        foreach (var (name, read) in DisplayFields)
        {
            read(changed).ShouldNotBe(read(defaults), name);
        }
    }

    [Fact]
    public async Task EveryDisplayFieldComesBackAndAnnotatedExportCarriesTheSameSet()
    {
        var external = MakeExternal();
        var file = MakeFile("m42.fits");
        var ct = TestContext.Current.CancellationToken;
        var changed = Changed();

        await ViewerDisplayMemory.SaveAsync(external, file, changed, ct);
        var record = await ViewerDisplayMemory.LoadAsync(external, file, ct);
        record.ShouldNotBeNull();

        var restored = new ViewerState();
        ViewerDisplayMemory.Apply(record, restored);
        var exported = changed.ForAnnotatedExport();

        foreach (var (name, read) in DisplayFields)
        {
            read(restored).ShouldBe(read(changed), name);

            // DisplayCrop is the one display field the export keeps out of its copy, by design.
            if (name != nameof(ViewerState.DisplayCrop))
            {
                read(exported).ShouldBe(read(changed), $"{name} should be in ForAnnotatedExport");
            }
        }
    }

    /// <summary>
    /// What <see cref="ViewerState.ForAnnotatedExport"/> assigns that a file's record deliberately does not keep: the capture's own
    /// wavelet reads, the overlays, the sky backdrop, and the window and chrome.
    /// </summary>
    private static readonly string[] NotRemembered =
    [
        nameof(ViewerState.WaveletDerived), nameof(ViewerState.WaveletLimb),
        nameof(ViewerState.ShowGrid), nameof(ViewerState.ShowOverlays), nameof(ViewerState.ShowStarOverlay), nameof(ViewerState.ShowSkyBackdrop),
        nameof(ViewerState.Zoom), nameof(ViewerState.ZoomToFit), nameof(ViewerState.PanOffset), nameof(ViewerState.HideChrome),
        nameof(ViewerState.ShowFileList), nameof(ViewerState.ShowInfoPanel), nameof(ViewerState.ShowHistogram), nameof(ViewerState.IsSequence),
        nameof(ViewerState.SplitFraction), nameof(ViewerState.NeedsTextureUpdate), nameof(ViewerState.MouseScreenPosition),
    ];

    [Fact]
    public void EveryMemberForAnnotatedExportAssignsIsRememberedOrListedAsNot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "TianWen.slnx")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull($"No src/TianWen.slnx above {AppContext.BaseDirectory}: the source guard has nothing to read");

        var text = File.ReadAllText(Path.Combine(dir.FullName, "src", "TianWen.UI.Abstractions", "ViewerState.cs"));
        var start = text.IndexOf("ForAnnotatedExport() => new ViewerState", StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, "ForAnnotatedExport moved: update this guard");
        var end = text.IndexOf("\n    };", start, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start);

        var assigned = System.Text.RegularExpressions.Regex.Matches(text[start..end], @"^\s*(\w+)\s*=", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToArray();
        assigned.ShouldNotBeEmpty();

        var known = DisplayFields.Select(f => f.Name).Concat(NotRemembered).ToHashSet(StringComparer.Ordinal);
        assigned.Where(n => !known.Contains(n)).ShouldBeEmpty(
            "a member ForAnnotatedExport now assigns is neither in DisplayFields (and the record) nor in NotRemembered: a display setting must be remembered per file");
    }

    [Fact]
    public async Task ARewrittenFileReadsAsNone()
    {
        var external = MakeExternal();
        var file = MakeFile("m31.fits");
        var ct = TestContext.Current.CancellationToken;

        await ViewerDisplayMemory.SaveAsync(external, file, Changed(), ct);
        (await ViewerDisplayMemory.LoadAsync(external, file, ct)).ShouldNotBeNull();

        File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(file).AddMinutes(3));
        (await ViewerDisplayMemory.LoadAsync(external, file, ct)).ShouldBeNull();

        await ViewerDisplayMemory.SaveAsync(external, file, Changed(), ct);
        (await ViewerDisplayMemory.LoadAsync(external, file, ct)).ShouldNotBeNull();

        File.WriteAllBytes(file, new byte[65]);
        (await ViewerDisplayMemory.LoadAsync(external, file, ct)).ShouldBeNull();
    }

    [Fact]
    public async Task TwoPathsDoNotShareARecordAndACaseDifferenceDoes()
    {
        var external = MakeExternal();
        var a = MakeFile("a.fits");
        var b = MakeFile("b.fits");
        var ct = TestContext.Current.CancellationToken;

        ViewerDisplayMemory.PathFor(external, a).ShouldNotBe(ViewerDisplayMemory.PathFor(external, b));
        ViewerDisplayMemory.PathFor(external, a).ShouldBe(ViewerDisplayMemory.PathFor(external, a.ToUpperInvariant()));

        await ViewerDisplayMemory.SaveAsync(external, a, Changed(), ct);
        (await ViewerDisplayMemory.LoadAsync(external, b, ct)).ShouldBeNull();
        (await ViewerDisplayMemory.LoadAsync(external, a, ct)).ShouldNotBeNull();
    }

    [Fact]
    public async Task AFileWithNothingSavedOrNotThereReadsAsNone()
    {
        var external = MakeExternal();
        var file = MakeFile("none.fits");
        var ct = TestContext.Current.CancellationToken;

        (await ViewerDisplayMemory.LoadAsync(external, file, ct)).ShouldBeNull();
        await ViewerDisplayMemory.SaveAsync(external, Path.Combine(Path.GetDirectoryName(file)!, "gone.fits"), Changed(), ct);
        Directory.Exists(Path.Combine(external.AppDataFolder.FullName, "Viewer")).ShouldBeFalse();
    }
}
