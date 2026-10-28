using System;
using System.Diagnostics;
using System.IO;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Optics;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

public class ZzDeriveTimingProbe
{
    [Fact(Timeout = 600_000)]
    public void Time()
    {
        var path = "D:/temp/tianwen-scratch/planetary/saturn-check/master_2021-12-16-1119_3_.fits";
        if (!File.Exists(path))
        {
            return;
        }
        Assert.True(Image.TryReadFitsFile(path, out var image));
        var pupil = new Pupil(0.254, ObstructionRatio: 58.0 / 254, Vanes: 4, VaneWidthM: 0.001, VaneAngleDeg: 28);
        var epoch = new DateTimeOffset(2021, 12, 16, 11, 19, 0, TimeSpan.Zero);
        var o = TestContext.Current.TestOutputHelper;
        for (var run = 0; run < 4; run++)
        {
            var timer = new PlanetaryStageTimer();
            var sw = Stopwatch.StartNew();
            var (gains, how, _) = PlanetaryBestStack.DeriveGains(image, CatalogIndex.Saturn, epoch, pupil, [650], colourSaturation: 1.1, progress: timer);
            sw.Stop();
            timer.Enter(PlanetaryStackStage.Writing);
            var s = $"run {run}: {image.Width}x{image.Height}x{image.ChannelCount} total {sw.Elapsed.TotalSeconds:0.00} s; gains {gains.Length}; {how}; ";
            foreach (var (stage, t) in timer.Spent)
            {
                s += $"{stage} {t.TotalSeconds:0.00} s; ";
            }
            o?.WriteLine(s);
            System.Console.Error.WriteLine("PROBE " + s);
        }
    }
}
