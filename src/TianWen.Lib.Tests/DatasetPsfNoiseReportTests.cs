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

        [Fact]
        public void TheReferenceChannelIsGreen_OnAThreeChannelMaster()
        {
            // Green carries twice the CFA sampling, so it detects the most stars and yields the
            // largest matched set. Fixed rather than per-session: the profile pools sessions of one
            // train, and a varying reference would band them on different physical quantities.
            DatasetPsfNoiseReport.ReferenceChannel(3).ShouldBe(1);
            DatasetPsfNoiseReport.ReferenceChannel(1).ShouldBe(0);
        }

        [Fact]
        public void AStarMissingFromOneChannel_IsNotSampledInAnyOfThem()
        {
            // The point of the common set: a star that only one channel detected cannot contribute
            // its width to that channel's annulus, because then the channels would be describing
            // different populations and their radial trends would not be comparable.
            var red = new List<ImagedStar>();
            var green = new List<ImagedStar>();
            var blue = new List<ImagedStar>();
            for (var i = 0; i < 50; i++)
            {
                var x = 100f + i * 7f;
                var y = 100f + i * 3f;
                red.Add(Star(x, y, fwhm: 3.0f, flux: 0.02f));
                // Sub-pixel disagreement between channels is real (median 0.064 px) and must still match.
                green.Add(Star(x + 0.05f, y - 0.04f, fwhm: 2.0f, flux: 0.01f));
                blue.Add(Star(x - 0.03f, y + 0.06f, fwhm: 2.2f, flux: 0.008f));
            }
            // Present in red and green only, and far from every other star so nothing else claims it.
            red.Add(Star(900f, 900f, fwhm: 9.9f, flux: 0.02f));
            green.Add(Star(900f, 900f, fwhm: 9.9f, flux: 0.01f));

            var matched = DatasetPsfNoiseReport.MatchStarsAcrossChannels(
                [StarsOf(red), StarsOf(green), StarsOf(blue)],
                DatasetPsfNoiseReport.ReferenceChannel(3));

            matched.ShouldNotBeNull();
            matched.X.Length.ShouldBe(50);
            matched.Fwhm[0].ShouldNotContain(9.9f);
            matched.Fwhm[1].ShouldNotContain(9.9f);
            // The reference channel's flux is what travels, so the band is one physical criterion.
            matched.ReferenceFlux.ShouldAllBe(f => f == 0.01f);
        }

        [Fact]
        public void EveryChannelsAnnulusHoldsTheSameStars_AndTheSameFluxToBandOn()
        {
            // This is the property that fixes the inversion. The downstream band is a percentile of
            // Flux applied index-wise to Fwhm, so identical flux arrays plus identical bin
            // membership mean the band keeps the SAME physical stars in all three channels. With a
            // per-channel percentile it kept a different brightness in each, and since measured FWHM
            // swings 25-30% with brightness, each channel's radial trend followed its own selection.
            var red = new List<ImagedStar>();
            var green = new List<ImagedStar>();
            var blue = new List<ImagedStar>();
            for (var i = 0; i < 60; i++)
            {
                // Spread across the field so more than one annulus is populated.
                var x = 20f + i * 30f;
                var y = 20f + i * 20f;
                red.Add(Star(x, y, fwhm: 3.0f + i * 0.01f, flux: 0.02f + i * 0.001f));
                green.Add(Star(x, y, fwhm: 2.0f + i * 0.01f, flux: 0.01f + i * 0.001f));
                blue.Add(Star(x, y, fwhm: 2.2f + i * 0.01f, flux: 0.008f + i * 0.001f));
            }

            var matched = DatasetPsfNoiseReport.MatchStarsAcrossChannels(
                [StarsOf(red), StarsOf(green), StarsOf(blue)], referenceChannel: 1);
            matched.ShouldNotBeNull();

            var bins = DatasetPsfNoiseReport.BinCommonStarsByFieldRadius(
                matched, cx: 960, cy: 640, halfDiag: 1153, radiusBins: 5);

            bins.Length.ShouldBe(3);
            for (var b = 0; b < 5; b++)
            {
                var reference = bins[0][b];
                foreach (var channel in bins)
                {
                    channel[b].Fwhm.Length.ShouldBe(reference.Fwhm.Length);
                    channel[b].Ellipticity.Length.ShouldBe(reference.Fwhm.Length);
                    channel[b].Flux!.Length.ShouldBe(reference.Fwhm.Length);
                    channel[b].Flux.ShouldBe(reference.Flux);
                }
            }
            bins[0].Sum(s => s.Fwhm.Length).ShouldBe(matched.X.Length, "every matched star lands in exactly one annulus");
        }

        [Fact]
        public void AHandfulOfMatchedStars_StillBins_BecauseTheStarFloorBelongsToTheBand()
        {
            // The matcher deliberately has no star-count floor. Deciding a session is too thin to
            // say anything is the band's job (FluxBand needs 40 for percentiles and folds a smaller
            // session in unbanded), and duplicating that floor here would drop the session from the
            // profile altogether -- worse than the unbanded fold it used to get.
            var red = new List<ImagedStar>();
            var green = new List<ImagedStar>();
            for (var i = 0; i < 10; i++)
            {
                red.Add(Star(50f + i * 11f, 50f + i * 13f, fwhm: 3.0f, flux: 0.02f));
                green.Add(Star(50f + i * 11f, 50f + i * 13f, fwhm: 2.0f, flux: 0.01f));
            }

            var matched = DatasetPsfNoiseReport.MatchStarsAcrossChannels(
                [StarsOf(red), StarsOf(green)], referenceChannel: 0);

            matched.ShouldNotBeNull();
            matched.X.Length.ShouldBe(10);
        }

        private static ImagedStar Star(float x, float y, float fwhm, float flux)
            => new(HFD: fwhm * 0.9f, StarFWHM: fwhm, SNR: 20f, Flux: flux,
                XCentroid: x, YCentroid: y, Ellipticity: 0.5f);

        private static StarList StarsOf(IEnumerable<ImagedStar> stars)
            => new(new System.Collections.Concurrent.ConcurrentBag<ImagedStar>(stars));

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

        /// <summary>The filter is the FOURTH field of the session id and nothing else: a target is
        /// not a filter, and a filter with no OBJECT still occupies the fourth slot because
        /// <see cref="ImagingSession.Id"/> always emits the OBJECT slot when a filter is present.</summary>
        [Theory]
        [InlineData("2025-06/session|ASI533", "")]
        [InlineData("2025-06/session|ASI533|Vela SNR", "")]
        [InlineData("2025-06/session|ASI533|Vela SNR|Optolong L-Ultimate 3nm", "Optolong L-Ultimate 3nm")]
        [InlineData("2025-06/session|ASI533||Ha", "Ha")]
        public void FilterFromSessionId_ReadsTheFourthFieldAndOnlyThat(string sessionId, string expected)
            => DatasetPsfNoiseReport.FilterFromSessionId(sessionId).ShouldBe(expected);

        /// <summary>
        /// An RGB night and a narrowband night on the same scope are two different measurement
        /// populations (autofocus optimises ~500-550 nm, so how badly red loses depends on the
        /// passband), so one optical train splits into one section per filter -- while a record
        /// whose id predates filters keeps the pre-filter behaviour: one unfiltered bucket.
        /// </summary>
        [Fact]
        public void SessionsOfOneTrain_AreSplitByFilter_AndPreFilterIdsKeepOneBucket()
        {
            DatasetPsfNoiseReport.SessionPsf Session(string id) => new(
                SessionId: id, OpticalTrain: "T", SubFwhm: [3f], SubHfd: [3f],
                SubEllipticity: [0.5f], MasterNoiseRelative: 0.004, BinsByChannel: null);

            var acc = new DatasetPsfNoiseReport.Accumulator(radiusBins: 1);
            acc.Add(Session("d1|CAM|Obj|Optolong L-Ultimate 3nm"));
            acc.Add(Session("d2|CAM|Obj|Optolong L-Quad Enhance"));
            acc.Add(Session("d3|CAM|Obj"));
            var report = acc.Build();

            report.Trains.Length.ShouldBe(3);
            // Ordered by label then filter, so the unfiltered bucket ("" sorts first) leads.
            report.Trains.Select(t => t.Filter).ShouldBe(["", "Optolong L-Quad Enhance", "Optolong L-Ultimate 3nm"]);
            report.Trains.ShouldAllBe(t => t.OpticalTrain == "T" && t.Sessions == 1);
        }

        /// <summary>The rendered section names its filter (in the heading AND beside the numbers)
        /// and classifies the glass, so a reader can judge a corner trend without knowing the
        /// hardware by name -- and so the first Newtonian to enter the archive announces itself.
        /// "(no filter recorded)" is deliberate wording: an absent FILTER header is an absent fact,
        /// not broadband.</summary>
        [Fact]
        public async Task TheRenderedSectionNamesTheFilterAndTheOpticalSystem()
        {
            var ct = TestContext.Current.CancellationToken;
            DatasetPsfNoiseReport.SessionPsf Session(string id, string train) => new(
                SessionId: id, OpticalTrain: train, SubFwhm: [3f], SubHfd: [3f],
                SubEllipticity: [0.5f], MasterNoiseRelative: 0.004, BinsByChannel: null);

            var acc = new DatasetPsfNoiseReport.Accumulator(radiusBins: 1);
            acc.Add(Session("d1|SV605|Orion|Optolong L-Ultimate 3nm", "SVBONY SV605CC / SH61 EDPH @ 270mm"));
            acc.Add(Session("d2|ASI585|Widefield", "ZWO ASI585MC Pro @ 24mm"));
            var mdPath = Path.Combine(_dir, "filter-sections.md");
            await DatasetPsfNoiseReport.WriteMarkdownAsync(acc.Build(), mdPath, ct);
            var md = await File.ReadAllTextAsync(mdPath, ct);

            md.ShouldContain("### SVBONY SV605CC / SH61 EDPH @ 270mm [Optolong L-Ultimate 3nm]");
            md.ShouldContain("- Filter: Optolong L-Ultimate 3nm | Optical system: refractor");
            md.ShouldContain("### ZWO ASI585MC Pro @ 24mm");
            md.ShouldContain("- Filter: (no filter recorded) | Optical system: camera lens");
        }
    }
}
