using Shouldly;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Dataset;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Coverage for <see cref="DatasetPsfNoiseReport"/> (dataset builder P0/#41): builds the archive
    /// PSF/noise distribution report from a registered synthetic session and asserts the field-radius
    /// profile, ordered percentiles, and Markdown rendering.
    /// </summary>
    [Collection("Imaging")]
    public class DatasetPsfNoiseReportTests(ITestOutputHelper output) : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "psfreport-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        [Fact]
        public async Task Build_ProducesFieldRadiusProfileAndOrderedPercentiles()
        {
            var ct = TestContext.Current.CancellationToken;
            var registered = await DatasetSyntheticFixtures.RegisterAsync(_dir, ct);

            const int bins = 4;
            var report = await DatasetPsfNoiseReport.BuildAsync([registered], radiusBins: bins, cancellationToken: ct);

            report.Sessions.ShouldBe(1);
            report.Subs.ShouldBe(registered.Subs.Length);
            report.StarsSampled.ShouldBeGreaterThan(0);

            // One optical train (the fixture's single camera); its field-radius profile has one entry
            // per bin, spans [0,1] centre->corner, and at least the inner bins carry stars with a
            // positive FWHM.
            var train = report.Trains.ShouldHaveSingleItem();
            train.OpticalTrain.ShouldContain("SynthBayer");
            train.FieldRadiusProfile.Length.ShouldBe(bins);
            train.FieldRadiusProfile[0].RMin.ShouldBe(0.0);
            train.FieldRadiusProfile[^1].RMax.ShouldBe(1.0);
            train.FieldRadiusProfile.Sum(b => b.Stars).ShouldBe((int)report.StarsSampled);
            train.FieldRadiusProfile.Count(b => b.Stars > 0 && b.MedianFwhm > 0).ShouldBeGreaterThan(0);

            // Percentiles are monotone non-decreasing and the PSF metrics are positive.
            foreach (var p in new[] { report.SubFwhm, report.SubHfd, report.SubEllipticity })
            {
                p.P5.ShouldBeLessThanOrEqualTo(p.P50);
                p.P50.ShouldBeLessThanOrEqualTo(p.P95);
            }
            report.SubFwhm.P50.ShouldBeGreaterThan(0.0);
            report.SubHfd.P50.ShouldBeGreaterThan(0.0);
            // Noise floor is a small positive fraction of full-scale.
            report.MasterNoiseRelative.P50.ShouldBeGreaterThan(0.0);
            report.MasterNoiseRelative.P95.ShouldBeLessThan(1.0);

            // Markdown renders with the expected sections.
            var mdPath = Path.Combine(_dir, "psf-noise-report.md");
            await DatasetPsfNoiseReport.WriteMarkdownAsync(report, mdPath, ct);
            File.Exists(mdPath).ShouldBeTrue();
            var md = await File.ReadAllTextAsync(mdPath, ct);
            md.ShouldContain("Field-radius PSF profile");
            md.ShouldContain("Per-sub PSF distribution");

            output.WriteLine($"stars={report.StarsSampled} fwhm.p50={report.SubFwhm.P50:F2} noise.p50={report.MasterNoiseRelative.P50:F5}");
        }

        [Fact]
        public async Task Build_SeparatesFieldRadiusProfilePerOpticalTrain()
        {
            // Two optical trains (a refractor camera + a Newtonian) must each get their OWN
            // field-radius profile -- a Newtonian's coma grows with field radius while a refractor's
            // field stays flat, so a single merged profile would smear the position-varying signal the
            // deconvolver sweep reproduces. We reuse the fixture's master pixels under a second,
            // distinct optical-train identity: the point under test is the per-train BUCKETING, not the
            // pixels.
            var ct = TestContext.Current.CancellationToken;
            var refractor = await DatasetSyntheticFixtures.RegisterAsync(_dir, ct);
            var newtonianLights = refractor.Session.Lights
                .Select(f => new FrameInfo(f.Path, f.Width, f.Height, f.ChannelCount, f.BitDepth,
                    f.Meta with { Instrument = "QHY294PROC", Telescope = "Newtonian", FocalLength = 800 }))
                .ToImmutableArray();
            var newtonian = refractor with
            {
                Session = refractor.Session with { Camera = "QHY294PROC", Lights = newtonianLights },
            };

            var report = await DatasetPsfNoiseReport.BuildAsync([refractor, newtonian], radiusBins: 4, cancellationToken: ct);

            report.Sessions.ShouldBe(2);
            report.Trains.Length.ShouldBe(2);
            report.Trains.Select(t => t.OpticalTrain).ShouldContain(s => s.Contains("SynthBayer"));
            report.Trains.Select(t => t.OpticalTrain).ShouldContain(s => s.Contains("QHY294PROC") && s.Contains("Newtonian") && s.Contains("800mm"));
            // Each train carries its own full profile; the overall star count is the sum of both.
            report.Trains.ShouldAllBe(t => t.FieldRadiusProfile.Length == 4);
            report.StarsSampled.ShouldBe(report.Trains.Sum(t => t.StarsSampled));

            // The Markdown renders one field-radius subsection per train.
            var mdPath = Path.Combine(_dir, "psf-two-train.md");
            await DatasetPsfNoiseReport.WriteMarkdownAsync(report, mdPath, ct);
            var md = await File.ReadAllTextAsync(mdPath, ct);
            md.ShouldContain("### ");
            md.ShouldContain("QHY294PROC / Newtonian @ 800mm");
        }
    }
}
