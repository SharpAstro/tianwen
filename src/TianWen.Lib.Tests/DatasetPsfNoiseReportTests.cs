using Shouldly;
using System;
using System.Collections.Generic;
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
            // One profile per channel of the master, each with one entry per bin.
            train.FieldRadiusProfiles.ShouldNotBeEmpty();
            train.RadialSessions.ShouldBe(1);
            foreach (var channel in train.FieldRadiusProfiles)
            {
                channel.Bins.Length.ShouldBe(bins);
                channel.Bins[0].RMin.ShouldBe(0.0);
                channel.Bins[^1].RMax.ShouldBe(1.0);
            }
            // Summed across EVERY channel, because StarsSampled counts them all: a per-channel profile
            // that only accounted for channel 0 would silently under-report.
            train.FieldRadiusProfiles.Sum(p => p.Bins.Sum(b => b.Stars)).ShouldBe((int)report.StarsSampled);
            train.FieldRadiusProfiles[0].Bins.Count(b => b.Stars > 0 && b.MedianFwhm > 0).ShouldBeGreaterThan(0);

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
        public async Task Build_MeasuresTheProfileOnEveryChannel_AndSaysSoInTheMarkdown()
        {
            // Channel 0 alone is not the master's PSF. Across the 49 archive masters that support the
            // measurement, green's stacked profile is 35% narrower than red's and red is the WIDEST
            // channel in 48 of them, so measuring one channel reported the worst case as the frame's.
            // This pins that every channel is measured and that the artifact shows them separately;
            // the synthetic fixture is too uniform to reproduce the archive's spread, so the
            // assertion is on the SHAPE of the output, not on a ratio.
            // Driven through the accumulator with archive-shaped records rather than the synthetic
            // fixture, which is far too star-poor to clear PsfProfileFit's 40-star floor on any
            // channel (it measures nothing, so it could not tell a per-channel report from the old
            // single-channel one). The numbers are the real HD 71526 master's.
            var ct = TestContext.Current.CancellationToken;
            const string label = "ZWO ASI533MC Pro / Samyang 135 f/2 ED @ 130mm";
            var acc = new DatasetPsfNoiseReport.Accumulator(radiusBins: 1);
            acc.Add(WithProfiles(label,
                new PsfProfileFit.Result(2.9097, 7.65, 0.19, 1.91, 400),
                new PsfProfileFit.Result(1.9136, 7.10, 0.11, 2.00, 400),
                new PsfProfileFit.Result(2.3836, 4.85, 0.12, 2.75, 400)));

            var report = acc.Build();
            var train = report.Trains.ShouldHaveSingleItem();

            train.ChannelProfiles.Length.ShouldBe(3);
            for (var c = 0; c < 3; c++)
            {
                train.ChannelProfiles[c].Channel.ShouldBe(c);
                train.ChannelProfiles[c].Sessions.ShouldBe(1);
                train.ChannelProfiles[c].Fwhm.P50.ShouldBeGreaterThan(0.0);
            }
            // Green narrower than red is the archive's signature, and the whole reason this is per
            // channel; a pooled number would sit between them and describe neither.
            train.ChannelProfiles[1].Fwhm.P50.ShouldBeLessThan(train.ChannelProfiles[0].Fwhm.P50);

            var mdPath = Path.Combine(_dir, "psf-noise-report-channels.md");
            await DatasetPsfNoiseReport.WriteMarkdownAsync(report, mdPath, ct);
            var md = await File.ReadAllTextAsync(mdPath, ct);
            md.ShouldContain("PER CHANNEL");
            md.ShouldContain("| Channel | Sessions | FWHM p50 (px) | vs ch0 |");
            // The ratio column exists so the spread is readable without arithmetic, and channel 0 is
            // its own reference, so it must read exactly 1.
            md.ShouldContain("| 0 | 1 | ");
            md.ShouldContain(" | 1.000 | ");
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
            report.Trains.ShouldAllBe(t => t.FieldRadiusProfiles.All(p => p.Bins.Length == 4));
            report.StarsSampled.ShouldBe(report.Trains.Sum(t => t.StarsSampled));

            // The Markdown renders one field-radius subsection per train.
            var mdPath = Path.Combine(_dir, "psf-two-train.md");
            await DatasetPsfNoiseReport.WriteMarkdownAsync(report, mdPath, ct);
            var md = await File.ReadAllTextAsync(mdPath, ct);
            md.ShouldContain("### ");
            md.ShouldContain("QHY294PROC / Newtonian @ 800mm");
        }

        /// <summary>
        /// The field-radius profile must be brightness-banded, or it reports each annulus's BRIGHTNESS
        /// COMPOSITION rather than its PSF. This is the shape of a real master: outer annuli are
        /// vignetted so their stars are fainter, star width correlates with measured flux, and the
        /// uncontrolled median therefore claims the corners are SHARPER than the centre, which is
        /// backwards for any lens.
        ///
        /// <para>Measured on a 24 mm session before the fix: 3.03 px at the centre falling to 2.85 px
        /// at the corner, while single unstacked raw frames of the same field ran 2.96 to 3.42 px the
        /// correct way round. The inverted result had been carried as an open question about the OPTICS
        /// (a suspected spacing or field-curvature error) when it was this aggregation.</para>
        ///
        /// <para>The fixture below is deliberately extreme: every annulus holds the SAME two PSF
        /// widths, so a correct measurement cannot show any radial trend at all, and only the
        /// faint-star proportion varies with radius. An unbanded median reads the trend anyway.</para>
        /// </summary>
        [Fact]
        public void FieldRadiusProfile_IsBrightnessBanded_SoVignettingCannotFakeARadialTrend()
        {
            // Per annulus: (bright stars, faint stars). Faint ones dominate the outer annuli, exactly
            // as vignetting arranges. Bright stars are narrow, faint ones wide, at EVERY radius.
            var mix = new[] { (40, 4), (34, 10), (26, 18), (14, 30), (6, 38) };
            var bins = new DatasetPsfNoiseReport.RadiusSamples[mix.Length];
            for (var b = 0; b < mix.Length; b++)
            {
                var (bright, faint) = mix[b];
                var fwhm = new List<float>();
                var ecc = new List<float>();
                var flux = new List<float>();
                for (var i = 0; i < bright; i++) { fwhm.Add(3.0f); ecc.Add(0.5f); flux.Add(10_000f); }
                for (var i = 0; i < faint; i++) { fwhm.Add(4.0f); ecc.Add(0.5f); flux.Add(1_000f); }
                bins[b] = new DatasetPsfNoiseReport.RadiusSamples([.. fwhm], [.. ecc], [.. flux]);
            }
            var record = new DatasetPsfNoiseReport.SessionPsf(
                SessionId: "banded", OpticalTrain: "T", SubFwhm: [3f], SubHfd: [3f],
                SubEllipticity: [0.5f], MasterNoiseRelative: 0.004, BinsByChannel: [bins]);

            var acc = new DatasetPsfNoiseReport.Accumulator(radiusBins: mix.Length);
            acc.Add(record);
            var report = acc.Build();

            var train = report.Trains.ShouldHaveSingleItem();
            train.BandedSessions.ShouldBe(1);
            var widths = train.FieldRadiusProfiles.ShouldHaveSingleItem().Bins.Select(b => b.MedianFwhm).ToArray();
            // The band keeps the bright population at every radius, so the profile is flat. Without
            // banding the last annulus reads 4.0 against the first's 3.0 -- a fabricated 33% "fall".
            foreach (var w in widths)
            {
                w.ShouldBe(3.0, tolerance: 1e-6,
                    $"annulus widths should all be the bright population's 3.0 px, got [{string.Join(", ", widths)}]");
            }
        }

        /// <summary>A record from before flux was stored must still render, rather than being dropped
        /// or silently mixed in as if it were banded. It keeps the old behaviour and the report says
        /// how many sessions are comparable.</summary>
        [Fact]
        public void FieldRadiusProfile_WithoutStoredFlux_StillRenders_AndIsReportedAsUnbanded()
        {
            var record = new DatasetPsfNoiseReport.SessionPsf(
                SessionId: "legacy", OpticalTrain: "T", SubFwhm: [3f], SubHfd: [3f],
                SubEllipticity: [0.5f], MasterNoiseRelative: 0.004,
                BinsByChannel: [[new DatasetPsfNoiseReport.RadiusSamples([3.0f, 4.0f], [0.5f, 0.5f])]]);

            var acc = new DatasetPsfNoiseReport.Accumulator(radiusBins: 1);
            acc.Add(record);
            var report = acc.Build();

            var train = report.Trains.ShouldHaveSingleItem();
            train.BandedSessions.ShouldBe(0);
            train.FieldRadiusProfiles.ShouldHaveSingleItem().Bins.ShouldHaveSingleItem().Stars.ShouldBe(2);
        }

        /// <summary>
        /// The field-radius profile is kept PER CHANNEL, and the channels must not be pooled or
        /// overwritten. Red is the widest channel on 48 of 49 archive masters, so the previous
        /// channel-0-only profile described red's field dependence while reading as the frame's, and
        /// averaging the channels would report a width no channel actually has.
        /// </summary>
        [Fact]
        public void FieldRadiusProfile_IsKeptPerChannel_SoOneChannelCannotSpeakForTheFrame()
        {
            // Two channels with deliberately different widths, flat across the field so the assertion
            // is about WHICH channel a number came from and nothing else.
            var record = new DatasetPsfNoiseReport.SessionPsf(
                SessionId: "per-channel", OpticalTrain: "T", SubFwhm: [3f], SubHfd: [3f],
                SubEllipticity: [0.5f], MasterNoiseRelative: 0.004,
                BinsByChannel:
                [
                    [Samples(4.0f), Samples(4.0f)],   // channel 0, the wide one
                    [Samples(2.0f), Samples(2.0f)],   // channel 1, the narrow one
                ]);

            var acc = new DatasetPsfNoiseReport.Accumulator(radiusBins: 2);
            acc.Add(record);
            var train = acc.Build().Trains.ShouldHaveSingleItem();

            train.RadialSessions.ShouldBe(1);
            train.FieldRadiusProfiles.Length.ShouldBe(2);
            train.FieldRadiusProfiles[0].Channel.ShouldBe(0);
            train.FieldRadiusProfiles[1].Channel.ShouldBe(1);
            foreach (var bin in train.FieldRadiusProfiles[0].Bins)
            {
                bin.MedianFwhm.ShouldBe(4.0, tolerance: 1e-6);
            }
            foreach (var bin in train.FieldRadiusProfiles[1].Bins)
            {
                bin.MedianFwhm.ShouldBe(2.0, tolerance: 1e-6);
            }

            // The rendered table names the channel, so a reader cannot mistake one for the frame.
            train.FieldRadiusProfiles.Select(p => p.Channel).ShouldBe([0, 1]);
        }

        /// <summary>
        /// A channel measurable on one session but not another must not shift the others' samples by a
        /// slot. Same hazard the per-channel stacked profile already guards: blue is the first channel
        /// to become unmeasurable on a star-poor field, and a mono session carries one channel where a
        /// colour one carries three.
        /// </summary>
        [Fact]
        public void AChannelMissingFromOneSession_DoesNotShiftAnotherSessionsChannels()
        {
            var colour = new DatasetPsfNoiseReport.SessionPsf(
                SessionId: "colour", OpticalTrain: "T", SubFwhm: [3f], SubHfd: [3f],
                SubEllipticity: [0.5f], MasterNoiseRelative: 0.004,
                BinsByChannel: [[Samples(4.0f)], [Samples(3.0f)], [Samples(5.0f)]]);
            var mono = new DatasetPsfNoiseReport.SessionPsf(
                SessionId: "mono", OpticalTrain: "T", SubFwhm: [3f], SubHfd: [3f],
                SubEllipticity: [0.5f], MasterNoiseRelative: 0.004,
                BinsByChannel: [[Samples(4.0f)]]);

            var acc = new DatasetPsfNoiseReport.Accumulator(radiusBins: 1);
            acc.Add(colour);
            acc.Add(mono);
            var train = acc.Build().Trains.ShouldHaveSingleItem();

            train.FieldRadiusProfiles.Length.ShouldBe(3);
            // Channel 0 pooled both sessions (both 4.0); channels 1 and 2 saw only the colour session,
            // and crucially channel 2 still reads 5.0 rather than having inherited the mono sample.
            train.FieldRadiusProfiles[0].Bins[0].Stars.ShouldBe(2);
            train.FieldRadiusProfiles[1].Bins[0].Stars.ShouldBe(1);
            train.FieldRadiusProfiles[1].Bins[0].MedianFwhm.ShouldBe(3.0, tolerance: 1e-6);
            train.FieldRadiusProfiles[2].Bins[0].Stars.ShouldBe(1);
            train.FieldRadiusProfiles[2].Bins[0].MedianFwhm.ShouldBe(5.0, tolerance: 1e-6);
        }

        /// <summary>One annulus holding a single star of the given width; ellipticity is a constant
        /// because these tests are about which channel a width came from.</summary>
        private static DatasetPsfNoiseReport.RadiusSamples Samples(float fwhm)
            => new([fwhm], [0.5f]);

        /// <summary>A minimal record carrying one profile per channel; the per-sub arrays are only
        /// there because the report needs somewhere to take percentiles from.</summary>
        private static DatasetPsfNoiseReport.SessionPsf WithProfiles(
            string train, params PsfProfileFit.Result?[] profiles)
            => new(
                SessionId: "s-" + train.GetHashCode().ToString("x8"),
                OpticalTrain: train,
                SubFwhm: [2.9f],
                SubHfd: [2.7f],
                SubEllipticity: [0.5f],
                MasterNoiseRelative: 0.004,
                BinsByChannel: [[new DatasetPsfNoiseReport.RadiusSamples([2.9f], [0.5f])]],
                MasterProfiles: profiles);
    }
}
