using System;
using System.IO;
using System.Threading.Tasks;
using DIR.Lib;
using SharpAstro.Ser;
using Shouldly;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The viewer's Best stack (#1159) end to end through the host <c>tianwen-fits</c> runs (<see cref="ViewerE2E"/>): a SER opened, Shift+K,
/// and the capture is stacked by the routine <c>planetary-stack</c> runs, both masters written beside it under that verb's names, the
/// sharpened one opened in its place.
/// </summary>
[Collection("Viewer")]
public class ViewerBestStackTests
{
    [Theory(Timeout = 180_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task ShiftKStacksTheWholeCaptureWritesBothMastersAndOpensTheSharpenedOne(float dpi)
    {
        using var e2e = ViewerE2E.Start(dpi);
        var ct = TestContext.Current.CancellationToken;
        var capture = WriteCapture(Path.Combine(e2e.Folder, "2024-12-15-1256_7-Jupiter.ser"));
        e2e.Host.HandleDropFile(capture);
        await e2e.PumpUntilAsync(() => e2e.State.SequencePath == capture, "the capture to open", ct);

        e2e.Key(InputKey.K, InputModifier.Shift);
        await e2e.PumpUntilAsync(() => e2e.IsShowing(Path.Combine(e2e.Folder, "master_2024-12-15-1256_7-Jupiter_sharpened.fits")),
            "the best stack's sharpened master to open", ct);

        // What the run did is said once its master is on screen (the open and its upload each clear the status line first).
        await e2e.PumpUntilAsync(() => e2e.State.StatusMessage?.StartsWith("Best stack:", StringComparison.Ordinal) == true,
            "the best stack's note", ct);

        File.Exists(Path.Combine(e2e.Folder, "master_2024-12-15-1256_7-Jupiter.fits")).ShouldBeTrue();
        e2e.State.BestStackProgress.ShouldBeNull();
        e2e.State.SequencePath.ShouldBeNull();
        // No telescope given, so the sharpening is the preset's, and the note says so.
        e2e.State.StatusMessage.ShouldNotBeNull().ShouldContain("PlanetaryDefault");
    }

    // A short Jupiter capture: a textured disk wandering a pixel or two, 8 bits, a frame every 10 ms.
    private static string WriteCapture(string path)
    {
        const int n = 96, frames = 32;
        var random = new Random(7);
        var start = new DateTimeOffset(2024, 12, 15, 12, 56, 44, TimeSpan.Zero);
        using (var writer = new SerWriter(path, n, n, SerColorId.Mono, 8))
        {
            var frame = new byte[n * n];
            for (var i = 0; i < frames; i++)
            {
                var (cx, cy) = (48 + (random.NextDouble() * 3) - 1.5, 48 + (random.NextDouble() * 3) - 1.5);
                for (var y = 0; y < n; y++)
                {
                    for (var x = 0; x < n; x++)
                    {
                        var (dx, dy) = (x - cx, y - cy);
                        var v = (dx * dx) + (dy * dy) < 26 * 26
                            ? 0.5 + (0.25 * Math.Sin(x * 0.6) * Math.Cos(y * 0.55)) + (0.12 * Math.Sin((x - y) * 0.3))
                            : 0.03;
                        frame[(y * n) + x] = (byte)Math.Clamp(v * 255, 0, 255);
                    }
                }
                writer.AppendFrame(frame, start.AddMilliseconds(10 * i));
            }
        }
        return path;
    }
}
