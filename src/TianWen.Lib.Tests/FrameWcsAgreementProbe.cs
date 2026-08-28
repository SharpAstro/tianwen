using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Does a frame's OWN WCS agree with its OWN pixels?
    /// </summary>
    /// <remarks>
    /// <para>The question to ask before blaming a solver. When a solve fails, there are three
    /// candidates -- the detections are junk, the catalogue is absent, or the matcher cannot form a
    /// correspondence -- and they are indistinguishable from the solver's own output, which only ever
    /// says "no lock". A frame that already carries a CD matrix settles it without solving anything:
    /// project the catalogue through the WCS the FILE claims and count how many detected stars land on
    /// a catalogue star.</para>
    /// <para>A high hit rate means the pixels, the catalogue and the stated WCS all agree, so a solver
    /// that cannot find that correspondence has a matching defect. A low one means the stated WCS is
    /// stale or the detections are not stars, and the solver was right to refuse.</para>
    /// <para>Gated: set <c>TIANWEN_WCS_AGREE_FITS</c> to a plate-solved FITS.</para>
    /// </remarks>
    [Collection("Imaging")]
    public class FrameWcsAgreementProbe(ITestOutputHelper output)
    {
        [Fact]
        public async Task ReportWhetherTheFramesWcsMatchesItsStars()
        {
            var path = Environment.GetEnvironmentVariable("TIANWEN_WCS_AGREE_FITS");
            Assert.SkipWhen(string.IsNullOrWhiteSpace(path), "Set TIANWEN_WCS_AGREE_FITS to a solved FITS");

            var ct = TestContext.Current.CancellationToken;
            Image.TryReadFitsFile(path!, out var image, out var fileWcs);
            Assert.NotNull(image);
            Assert.NotNull(fileWcs);

            var wcs = fileWcs!.Value;
            output.WriteLine($"{System.IO.Path.GetFileName(path)}: {image!.Width}x{image.Height}x{image.ChannelCount}");
            output.WriteLine($"file WCS  RA={wcs.CenterRA:F5}h Dec={wcs.CenterDec:F4} scale={wcs.PixelScaleArcsec:F4}\"/px hasCD={wcs.HasCDMatrix}");

            var stars = (await image.FindStarsAsync(
                image.ReferenceStarChannel, snrMin: 5f, maxStars: 5000, minStars: 100,
                maxFirstPassNoiseSigma: Image.MaxFirstPassNoiseSigma, cancellationToken: ct)).ToArray();
            output.WriteLine($"detected  {stars.Length} stars on channel {image.ReferenceStarChannel}");

            var db = await SharedCatalogDB.InitAsync(ct);
            var all = new Tycho2StarLite[db.Tycho2StarCount];
            var copied = db.CopyTycho2Stars(all);

            var inFrame = new List<(double X, double Y, float V)>();
            for (var i = 0; i < copied; i++)
            {
                var s = all[i];
                if (wcs.SkyToPixel(s.RaHours, s.DecDeg) is { } px
                    && px.X >= 0 && px.Y >= 0 && px.X < image.Width && px.Y < image.Height)
                {
                    inFrame.Add((px.X, px.Y, s.VMag));
                }
            }

            output.WriteLine($"catalog   {inFrame.Count} Tycho-2 stars project inside the frame under the FILE's WCS");
            if (inFrame.Count == 0)
            {
                output.WriteLine("  -> nothing to match against; the WCS points somewhere with no catalogue coverage");
                return;
            }

            // Hit rate at a few tolerances. A correct WCS puts most bright stars within a pixel or two;
            // a stale one puts them nowhere, and a WRONG SCALE puts the centre right and the edges out,
            // which is why the radial breakdown below matters as much as the totals.
            foreach (var tol in new[] { 2.0, 6.0, 20.0, 60.0 })
            {
                var hits = 0;
                foreach (var s in stars)
                {
                    if (inFrame.Any(c => Math.Abs(c.X - s.XCentroid) < tol && Math.Abs(c.Y - s.YCentroid) < tol
                                         && Math.Sqrt((c.X - s.XCentroid) * (c.X - s.XCentroid) + (c.Y - s.YCentroid) * (c.Y - s.YCentroid)) <= tol))
                    {
                        hits++;
                    }
                }
                output.WriteLine($"  within {tol,5:F1} px: {hits,5} of {stars.Length} detections ({(double)hits / stars.Length:P1})");
            }

            // Nearest-catalogue distance for the brightest detections, split by field radius. A constant
            // offset reads the same everywhere; a scale error grows with radius; distortion grows faster.
            var cx = image.Width / 2.0;
            var cy = image.Height / 2.0;
            output.WriteLine("\n  brightest 20 detections: nearest catalogue star, by field radius");
            foreach (var s in stars.OrderByDescending(s => s.Flux).Take(20))
            {
                var best = double.MaxValue;
                foreach (var c in inFrame)
                {
                    var d = Math.Sqrt((c.X - s.XCentroid) * (c.X - s.XCentroid) + (c.Y - s.YCentroid) * (c.Y - s.YCentroid));
                    if (d < best)
                    {
                        best = d;
                    }
                }
                var r = Math.Sqrt((s.XCentroid - cx) * (s.XCentroid - cx) + (s.YCentroid - cy) * (s.YCentroid - cy));
                output.WriteLine($"    ({s.XCentroid,8:F1},{s.YCentroid,8:F1}) r={r,7:F0}px  nearest {best,8:F1}px  hfd={s.HFD:F2} snr={s.SNR:F0}");
            }
        }
    }
}
