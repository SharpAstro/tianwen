using Shouldly;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.BackgroundExtraction;
using TianWen.Lib.Imaging.Dataset;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="DatasetGradientFrameExporter"/> (gradient-remover-training.md, G2): the session and split a
/// master maps to, the frame file, and the thin band's level step measured against a known one.
/// </summary>
[Collection("Imaging")]
public sealed class DatasetGradientFrameExporterTests(ITestOutputHelper output) : IDisposable
{
    private const int W = 512, H = 384, Ring = 6, Strip = 48, Size = 64;
    private const float Sky = 0.01f, Sigma = 2e-4f;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gradexport-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void AFlipSidesSessionIsItsNightsWithTheSideAppended()
    {
        var byStem = new Dictionary<string, string> { ["Cam_Filter_Lagoon_2025-05-25_Cam_Lagoon"] = "Cam/Filter/Lagoon/2025-05-25|Cam|Lagoon" };
        DatasetGradientFrameExporter.SessionIdOf("Cam_Filter_Lagoon_2025-05-25_Cam_Lagoon", byStem).ShouldBe("Cam/Filter/Lagoon/2025-05-25|Cam|Lagoon");
        DatasetGradientFrameExporter.SessionIdOf("Cam_Filter_Lagoon_2025-05-25_Cam_Lagoon_flip=b", byStem).ShouldBe("Cam/Filter/Lagoon/2025-05-25|Cam|Lagoon|flip=b");
        DatasetGradientFrameExporter.SessionIdOf("Somewhere_Else", byStem).ShouldBe("");
    }

    [Fact]
    public void ThePinnedSplitIsReadWithItsCommentsAndMarkersStripped()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, DatasetSplitWriter.TestSessionsFileName);
        File.WriteAllText(path, "# Pinned held-out TEST sessions.\n# another comment\nA/B/C|Cam|X\nD/E/F|Cam|Y\t# FORCED\n\n");
        DatasetSplitWriter.ReadPinned(path).ShouldBe(["A/B/C|Cam|X", "D/E/F|Cam|Y"], ignoreOrder: true);
        DatasetSplitWriter.ReadPinned(Path.Combine(_dir, "missing.txt")).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0f, 0.0)]
    [InlineData(1f, 1.0)]
    public async Task TheThinBandsLevelStepIsMeasuredInSigma(float stepSigma, double expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var (masterPath, _) = WriteMaster("m", stepSigma);

        var row = await DatasetGradientFrameExporter.ExportMasterAsync(masterPath, "S|Cam|M", "train", covariates: null, _dir, Size, ct);
        output.WriteLine($"offset {string.Join("/", row.ThinBandOffsetSigma)} over {row.ThinBandSamples} samples, step {string.Join("/", row.ThinBandStepSigma)} over {row.ThinBandPairs} pairs, thin {row.ThinBandFraction:P1}, absent {row.AbsentFraction:P2}");

        row.HasCoverage.ShouldBeTrue();
        row.ThinBandSamples.ShouldBeGreaterThan(DatasetGradientFrameExporter.ThinBandMinSamples);
        foreach (var offset in row.ThinBandOffsetSigma)
        {
            offset.ShouldBe((float)expected, 0.15f);
        }
        // Across the boundary the same step reads the same, from adjacent samples alone.
        row.ThinBandPairs.ShouldBeGreaterThan(DatasetGradientFrameExporter.ThinBandMinSamples);
        foreach (var step in row.ThinBandStepSigma)
        {
            step.ShouldBe((float)expected, 0.2f);
        }
    }

    [Fact]
    public async Task AFrameFileHoldsEveryPlaneInLayoutOrderAndTheRowDescribesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var (masterPath, wcs) = WriteMaster("m", 0f);

        var row = await DatasetGradientFrameExporter.ExportMasterAsync(masterPath, "S|Cam|M", "test", covariates: null, _dir, Size, ct);

        row.Channels.ShouldBe(3);
        (row.FrameWidth, row.FrameHeight, row.OffsetX, row.OffsetY).ShouldBe((64, 48, 0, 8));
        row.Layout.ShouldBe(DatasetGradientFrameExporter.PlaneLayout);
        row.Split.ShouldBe("test");
        row.ScaleSource.ShouldBe("header");
        row.PixelScaleArcsec.ShouldBe(wcs.PixelScaleArcsec, 1e-9);
        row.FieldWidthDeg.ShouldBe(wcs.PixelScaleArcsec * W / 3600.0, 1e-9);
        row.BackgroundSigma.ShouldAllBe(s => s > Sigma * 0.8f && s < Sigma * 1.2f);
        row.Median.ShouldAllBe(m => Math.Abs(m - Sky) < 1e-3f);
        // The ring is 6 px of 512 x 384 on every side.
        row.AbsentFraction.ShouldBe(1f - (W - 2f * Ring) * (H - 2f * Ring) / (W * H), 2e-3f);
        // The crop is the all-frames rectangle, which leaves the thin strip out.
        row.CropWidth.ShouldBeGreaterThan(0);
        row.CropX.ShouldBeGreaterThan((Ring + Strip) * Size / W - 1);

        var bytes = await File.ReadAllBytesAsync(Path.Combine(_dir, row.File), ct);
        bytes.Length.ShouldBe((2 * 3 + 2) * Size * Size * sizeof(float));
        var planes = Enumerable.Range(0, 8).Select(p => new ReadOnlySpan<byte>(bytes, p * Size * Size * 4, Size * Size * 4).ToArray())
            .Select(b => System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(b).ToArray()).ToArray();
        var presence = planes[6];
        var depth = planes[7];
        // The pad above the frame is absent in every plane; the middle of the frame is fully present at full depth.
        var pad = 3 * Size + 32;
        var middle = (Size / 2) * Size + 40;
        presence[pad].ShouldBe(0f);
        float.IsNaN(planes[0][pad]).ShouldBeTrue();
        float.IsNaN(planes[3][pad]).ShouldBeTrue();
        presence[middle].ShouldBe(1f);
        depth[middle].ShouldBe(1f, 1e-6f);
        planes[0][middle].ShouldBe(planes[3][middle], 5 * Sigma);
        // A sample in the strip sits at the strip's depth.
        var inStrip = (Size / 2) * Size + (Ring + Strip / 2) * Size / W;
        depth[inStrip].ShouldBe(12f / 20f, 1e-3f);
    }

    [Fact(Timeout = 60_000)]
    public async Task ARunSplitsByTheBakesOwnPinnedFileAndResumes()
    {
        var ct = TestContext.Current.CancellationToken;
        var (night, _) = WriteMaster("Cam_Filter_Field_2026-01-01_Cam_Field", 0f);
        var (side, _) = WriteMaster("Cam_Filter_Field_2026-01-01_Cam_Field_flip=a", 0f);
        var (other, _) = WriteMaster("Cam_Filter_Other_2026-01-02_Cam_Other", 0f);
        var (orphan, _) = WriteMaster("Nobody_Knows", 0f);

        var ledgerPath = Path.Combine(_dir, DatasetSessionLedger.FileName);
        await DatasetSessionLedger.AppendAsync(ledgerPath, new DatasetSessionLedger.SessionLedgerEntry(
            "Cam/Filter/Field/2026-01-01|Cam|Field", "x", 3, DateTimeOffset.UnixEpoch, 0, "tiles/Cam_Filter_Field_2026-01-01_Cam_Field"), ct);
        await DatasetSessionLedger.AppendAsync(ledgerPath, new DatasetSessionLedger.SessionLedgerEntry(
            "Cam/Filter/Other/2026-01-02|Cam|Other", "x", 3, DateTimeOffset.UnixEpoch, 0, "tiles/Cam_Filter_Other_2026-01-02_Cam_Other"), ct);
        var pinned = Path.Combine(_dir, DatasetSplitWriter.TestSessionsFileName);
        await File.WriteAllTextAsync(pinned, "# held out\nCam/Filter/Field/2026-01-01|Cam|Field\t# FORCED\n", ct);

        var outDir = Path.Combine(_dir, "export");
        var files = ImmutableArray.Create(night, side, other, orphan, IntegrationFitsWriter.CoveragePathFor(night));
        var options = new DatasetGradientFrameExporter.ExportOptions(files, ledgerPath, pinned, GradientStorePath: null, outDir, Size);
        var result = await DatasetGradientFrameExporter.RunAsync(options, cancellationToken: ct);

        result.Exported.ShouldBe(4);
        result.Failed.ShouldBe(0);
        result.Test.ShouldBe(2);
        result.UnknownSplit.ShouldBe(1);
        var rows = File.ReadAllLines(result.ManifestPath).Where(l => l.Length > 0).ToArray();
        rows.Length.ShouldBe(4);
        rows.Single(r => r.Contains("\"Master\":\"Cam_Filter_Field_2026-01-01_Cam_Field_flip=a.fits\"")).ShouldContain("\"Split\":\"test\"");
        rows.Single(r => r.Contains("\"Master\":\"Cam_Filter_Other_2026-01-02_Cam_Other.fits\"")).ShouldContain("\"Split\":\"train\"");
        rows.Single(r => r.Contains("\"Master\":\"Nobody_Knows.fits\"")).ShouldContain($"\"Split\":\"{DatasetGradientFrameExporter.UnknownSplit}\"");

        var again = await DatasetGradientFrameExporter.RunAsync(options, cancellationToken: ct);
        again.Exported.ShouldBe(0);
        again.Skipped.ShouldBe(4);
    }

    /// <summary>
    /// A three-channel master: sky plus a ramp, Gaussian noise, an exact-zero ring, and a coverage sidecar
    /// whose left strip (inside the ring) only 12 of 20 frames reached, that strip lifted by
    /// <paramref name="stepSigma"/> background sigma. Plate-solved in its header at 4 arcsec per pixel.
    /// </summary>
    private (string Path, WCS Wcs) WriteMaster(string stem, float stepSigma)
    {
        Directory.CreateDirectory(_dir);
        var rng = new Random(11);
        var planes = new float[3][,];
        for (var c = 0; c < 3; c++)
        {
            var plane = new float[H, W];
            for (var y = Ring; y < H - Ring; y++)
            {
                for (var x = Ring; x < W - Ring; x++)
                {
                    var u1 = 1.0 - rng.NextDouble();
                    var u2 = rng.NextDouble();
                    var gauss = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
                    var step = x < Ring + Strip ? stepSigma * Sigma : 0f;
                    plane[y, x] = (float)(Sky + 0.002 * BackgroundPolynomial.Normalise(x, W) + Sigma * gauss + step);
                }
            }
            planes[c] = plane;
        }
        var meta = new ImageMeta { Instrument = "SynthCam", ObjectName = "Synth Field", SensorType = SensorType.Color };
        var master = new Image(planes, BitDepth.Float32, 1f, 0f, 0f, meta);
        const double s = 4.0 / 3600.0;
        var wcs = new WCS(6.5, 3.4) { CRPix1 = W / 2.0, CRPix2 = H / 2.0, CD1_1 = -s, CD1_2 = 0, CD2_1 = 0, CD2_2 = s };
        var path = Path.Combine(_dir, stem + ".fits");
        master.WriteToFitsFile(path, wcs);

        var counts = new float[H, W];
        for (var y = Ring; y < H - Ring; y++)
        {
            for (var x = Ring; x < W - Ring; x++)
            {
                counts[y, x] = x < Ring + Strip ? 12f : 20f;
            }
        }
        var coverage = new Image([counts], BitDepth.Float32, 1f, 0f, 0f, meta with { SensorType = SensorType.Monochrome });
        IntegrationFitsWriter.WriteCoverageMap(path, coverage, frameCount: 20);
        coverage.Release();
        master.Release();
        return (path, wcs);
    }
}
