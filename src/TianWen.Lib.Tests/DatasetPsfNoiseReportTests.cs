using Shouldly;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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

            // Field-radius profile has one entry per bin, spanning [0,1] centre->corner, and at least
            // the inner bins carry stars with a positive FWHM.
            report.FieldRadiusProfile.Length.ShouldBe(bins);
            report.FieldRadiusProfile[0].RMin.ShouldBe(0.0);
            report.FieldRadiusProfile[^1].RMax.ShouldBe(1.0);
            report.FieldRadiusProfile.Sum(b => b.Stars).ShouldBe((int)report.StarsSampled);
            report.FieldRadiusProfile.Count(b => b.Stars > 0 && b.MedianFwhm > 0).ShouldBeGreaterThan(0);

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
    }
}
