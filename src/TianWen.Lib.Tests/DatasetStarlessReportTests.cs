using Shouldly;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Dataset;
using TianWen.Lib.Imaging.StarRemoval;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The starless-plates run over a pool, hours long, ends through its stop file and never by ending the process: the stop is
/// honoured between masters, the next run skips what the store holds, and a stop file left from an earlier stop does not
/// end a fresh run before it starts.
/// </summary>
[Collection("Imaging")]
public class DatasetStarlessReportTests(ITestOutputHelper output) : IDisposable
{
    private const int Size = 192;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tianwen-starless-report-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task AStopFileEndsTheRunBeforeTheNextMasterAndTheNextRunPicksUpThere()
    {
        var mastersDir = Path.Combine(_root, "masters");
        Directory.CreateDirectory(mastersDir);
        var masters = new List<string>();
        for (var k = 0; k < 3; k++)
        {
            var path = Path.Combine(mastersDir, $"master{k}.fits");
            var master = SyntheticMaster(seed: k + 1);
            master.WriteToFitsFile(path);
            master.Release();
            masters.Add(path);
        }
        var outRoot = Path.Combine(_root, "out");
        Directory.CreateDirectory(outRoot);
        var stopPath = Path.Combine(outRoot, DatasetStarlessReport.StopFileName);
        await File.WriteAllTextAsync(stopPath, "", TestContext.Current.CancellationToken);

        var lines = new List<string>();
        var options = new DatasetStarlessReport.RunOptions([.. masters], outRoot, ProbeHoles: 0);
        var first = await DatasetStarlessReport.RunAsync(options, progress: new Immediate(line =>
        {
            lines.Add(line);
            // The stop is created while the first master's record is written, as a person would between two masters.
            if (line.Contains(" subtracted, ", StringComparison.Ordinal))
            {
                File.WriteAllText(stopPath, "");
            }
        }), cancellationToken: TestContext.Current.CancellationToken);
        foreach (var line in lines)
        {
            output.WriteLine(line);
        }

        lines.ShouldContain(static l => l.Contains("cleared a stop file", StringComparison.Ordinal), "a stop left from before must not end a fresh run");
        first.Stopped.ShouldBeTrue();
        first.Measured.ShouldBe(1);
        File.Exists(stopPath).ShouldBeFalse("the run deletes the stop file it honoured");

        var second = await DatasetStarlessReport.RunAsync(options, cancellationToken: TestContext.Current.CancellationToken);
        second.Stopped.ShouldBeFalse();
        second.Skipped.ShouldBe(1);
        second.Measured.ShouldBe(2);
        foreach (var path in masters)
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            File.Exists(Path.Combine(outRoot, "plates", stem + "_plate.fits")).ShouldBeTrue();
            File.Exists(StarlessCatalogue.PathFor(Path.Combine(outRoot, "plates"), stem)).ShouldBeTrue();
        }
    }

    /// <summary>A small mono master: a flat sky, white noise and a scatter of round Moffat stars.</summary>
    private static Image SyntheticMaster(int seed)
    {
        var rng = new Random(seed);
        var plane = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                plane[y, x] = (float)(0.1 + (0.001 * StarInjection.Gaussian(rng)));
            }
        }
        var profile = StarProfile.Round(StarProfileFamily.Moffat, 3.0, 3.0);
        for (var s = 0; s < 25; s++)
        {
            var cx = 12 + (rng.NextDouble() * (Size - 24));
            var cy = 12 + (rng.NextDouble() * (Size - 24));
            var amplitude = 0.02 + (rng.NextDouble() * 0.3);
            for (var y = Math.Max(0, (int)cy - 12); y <= Math.Min(Size - 1, (int)cy + 12); y++)
            {
                for (var x = Math.Max(0, (int)cx - 12); x <= Math.Min(Size - 1, (int)cx + 12); x++)
                {
                    plane[y, x] += (float)(amplitude * profile.PixelMean(x, y, cx, cy));
                }
            }
        }
        return new Image([plane], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
    }

    private sealed class Immediate(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
