using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging.Stacking;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Dataset;

/// <summary>
/// Archive PSF/noise distribution report for the dataset builder (docs/plans/ai-denoise-deconv.md
/// §2.4 P0 deliverable, task #41). Characterises the registered sessions two ways:
/// <list type="bullet">
///   <item><b>PSF distribution</b>: per-sub median FWHM / HFD / ellipticity percentiles (the
///     population the denoiser sees) plus a <b>field-radius profile</b> of median FWHM + ellipticity
///     binned centre→corner (detected on each session master). The field-radius profile is the input
///     to the deconvolver's position-varying synthetic-PSF sweep (§2.2): a fast lens's corners are
///     genuinely broader than its centre, so the degradation must sample by field radius, and this
///     report says what range to sweep.</item>
///   <item><b>Noise floor</b>: per-session master background σ (MAD relative to full-scale), a
///     coarse cross-session noise characterisation.</item>
/// </list>
/// Pure analysis over <see cref="SessionRegistrar.RegisteredSession"/>s; no tile format coupling.
/// </summary>
public static class DatasetPsfNoiseReport
{
    /// <summary>A five-number summary of one metric across the sampled population.</summary>
    public sealed record Percentiles(double P5, double P25, double P50, double P75, double P95)
    {
        public static Percentiles Empty { get; } = new(0, 0, 0, 0, 0);
    }

    /// <summary>Median FWHM + ellipticity of stars whose normalised field radius (0 = frame centre,
    /// 1 = corner) falls in <c>[RMin, RMax)</c>, over all session masters.</summary>
    public sealed record RadiusBin(double RMin, double RMax, double MedianFwhm, double MedianEllipticity, int Stars);

    /// <summary>
    /// The raw per-star samples of one field-radius annulus for ONE session, before any cross-session
    /// median. Kept raw rather than reduced so a brightness cut can be applied at analysis time.
    /// </summary>
    /// <param name="Fwhm">Every sampled star's FWHM whose field radius fell in this bin.</param>
    /// <param name="Ellipticity">The same stars' ellipticities, index-aligned with <paramref name="Fwhm"/>.</param>
    /// <param name="Flux">Per-star flux, the brightness proxy a consumer bands on;
    /// <see langword="null"/> on records written before it was stored.
    /// <para><b>Without it the profile measures the wrong thing, which is not hypothetical.</b> A
    /// master's outer annuli are vignetted, so their stars are fainter, and star width correlates
    /// with measured flux; an uncontrolled median per annulus therefore reports each annulus's
    /// BRIGHTNESS COMPOSITION rather than its PSF. Measured on a 24 mm session, the uncontrolled
    /// profile fell from 3.03 px at the centre to 2.85 px at the corner, i.e. it claimed the corners
    /// were SHARPER, which is backwards for any lens; banding the same stars by flux flattened it to
    /// 3.20 to 3.42 px, matching a single unstacked raw frame of the same field (2.96 to 3.42 px,
    /// with ellipticity rising 0.41 to 0.57 as a real lens does). The inverted trend had been carried
    /// as an open question about the OPTICS ("the centre-to-corner fall is session-dependent") when it
    /// was an artifact of this aggregation; its session-dependence is just how much vignetting and
    /// coverage variation each session has. <see cref="PsfProfileFit"/> already controls brightness
    /// for exactly this reason, via a peak band, and these bins did not.</para>
    ///
    /// <para>On a record written with <see cref="CommonStarSampling"/> this is the REFERENCE
    /// channel's flux, and is therefore IDENTICAL in every channel of the session. That is the
    /// whole point: it makes one physical star set, selected once, the thing every channel's
    /// profile describes.</para>
    ///
    /// <para><b>Why a per-channel percentile was wrong.</b> A star's flux differs between channels,
    /// so the old 55th-90th percentile taken per channel cut red, green and blue at different
    /// physical brightnesses (ch0's centre band sat at 0.01653 against ch2's 0.00847). Measured
    /// FWHM swings 25-30% with brightness, so each channel's radial profile was tracking the
    /// brightness composition of its own selection: banded flux moved -15.7% centre-to-corner in
    /// ch0 while moving +39.6% and +48.4% in ch1 and ch2, and the measured FWHM followed the SIGN
    /// of that move in all three. That is what inverted red, which appeared to grow SHARPER toward
    /// the corner (2.637 -> 2.006 px) while green and blue broadened. It was never optics: red's
    /// ellipticity degrades fastest toward the corner (+20.7%) over the same stars in the same
    /// bins, and a star cannot be more elongated and sharper at once.</para>
    /// </param>
    public sealed record RadiusSamples(float[] Fwhm, float[] Ellipticity, float[]? Flux = null);

    /// <summary>One channel's field-radius profile across a train's sessions.</summary>
    /// <param name="Channel">Channel index in the master's own order (0 = red for the archive's RGGB
    /// sensors).</param>
    /// <param name="Bins">Annuli from centre to corner, always <c>radiusBins</c> long.</param>
    public sealed record ChannelRadiusProfile(int Channel, ImmutableArray<RadiusBin> Bins);

    /// <summary>
    /// One session's contribution to the report, in the form that gets PERSISTED
    /// (<see cref="DatasetPsfStore"/>) so the report survives a partial or resumed run.
    ///
    /// <para>These are raw samples, not per-session summaries, and that is load-bearing: the report's
    /// field-radius profile is a median over every star in a bin across all sessions of an optical
    /// train, which a stored median-of-medians could not reconstruct. Persisting the samples means a
    /// resumed run rebuilds a byte-identical report to the one an uninterrupted run would have
    /// produced.</para>
    /// </summary>
    /// <param name="SessionId">Portable session id; the store's key (last record per id wins).</param>
    /// <param name="OpticalTrain">
    /// <see cref="CalibrationResolver.CalTrain.Describe"/> of the session's train. Stored rather than
    /// re-derived because re-deriving needs the session's frames, which a resumed run has not read.
    /// </param>
    /// <param name="Bins">Per-bin samples, indexed by bin; length is the report's radius-bin count.</param>
    /// <param name="MasterProfiles">
    /// Measured PSF SHAPE of the session master (<see cref="PsfProfileFit"/>), <b>one entry per
    /// colour channel</b>: the stacked-profile FWHM and the Moffat exponent describing its wings.
    /// An entry is null where that channel could not support a measurement, and the whole array is
    /// empty on any record written before this was measured at all -- the store is append-only and
    /// last-wins, so old sessions read back empty here until re-measured with <c>--force-psf</c>.
    /// It is what the deconvolver's synthetic-PSF sweep should be calibrated from: a width alone
    /// does not pin a profile, and the wings are what produce ringing.
    ///
    /// <para><b>Per channel, because one channel is not the frame's PSF.</b> Measured over the 49
    /// archive masters that support it, green's stacked profile is 35% narrower than red's at the
    /// median (ratio 0.648, and narrower in 48 of 49), blue's 20% narrower (0.799, in 44 of 49),
    /// and the Moffat exponent moves with it (p50 red 5.00, green 7.00, blue 4.50). Sampling one
    /// channel therefore does not describe the master; it describes that channel. Red in particular
    /// is the WIDEST channel in 48 of 49 masters, so the earlier channel-0-only measurement was
    /// reporting the worst case as if it were the frame. Verified not to be a registration artifact:
    /// the median centroid shift between channels is 0.064 px (max 0.339), far too small to widen a
    /// ~2.9 px profile, and re-measuring with a common set of the same physical stars in every
    /// channel reproduces the ratios (0.641 -> 0.648), so it is not a star-population effect either.
    /// The size is train-dependent (green/red 0.637 Samyang, 0.668 SH61, 0.904 ZS61) and blue's
    /// direction flips between trains (blue/red 0.738 SH61 but 1.279 ZS61), which is why this is
    /// stored per channel per session rather than reduced to one archive-wide correction.</para>
    /// </param>
    /// <param name="MasterStrategy">
    /// Which integrator produced the master these numbers were measured on
    /// (<see cref="SessionRegistrar.RegisteredSession.MasterStrategy"/>), or null on a record
    /// written before it was tracked. <b>Every consumer of the per-channel numbers above must
    /// group by this.</b> Drizzle is gated per session, so the archive legitimately holds both
    /// kinds, and the two are not comparable here: an AHD master reconstructs two of every three
    /// colour samples from neighbours, and it reconstructs GREEN from closer neighbours than red
    /// or blue because green has twice the CFA sampling. That is a mechanism which produces
    /// exactly the green-is-narrower result documented above, so a mixed population would let
    /// demosaic behaviour masquerade as chromatic optics. Drizzled masters interpolate nothing
    /// and are the clean measurement.
    /// <para>It lives here, at SESSION level, and deliberately NOT on
    /// <c>DatasetTileExporter.TileManifestRow</c>: a session-level fact copied onto every tile row
    /// is how the manifest's FWHM column drifted into being authoritative and wrong (see that
    /// record's remarks). One home, joined on <paramref name="SessionId"/>.</para>
    /// </param>
    public sealed record SessionPsf(
        string SessionId,
        string OpticalTrain,
        float[] SubFwhm,
        float[] SubHfd,
        float[] SubEllipticity,
        double MasterNoiseRelative,
        RadiusSamples[][]? BinsByChannel,
        PsfProfileFit.Result?[]? MasterProfiles = null,
        string? MasterStrategy = null,
        string? RadiusSampling = null);

    /// <summary>
    /// The filter encoded in a <see cref="SessionPsf.SessionId"/>, or empty when the session had
    /// none recorded. <see cref="ImagingSession.Id"/> emits <c>dir|CAMERA[|OBJECT[|FILTER]]</c>
    /// and always emits the OBJECT slot (possibly empty) when a filter is present, so a fourth
    /// field IS the filter, unambiguously. Deriving it here rather than storing it again is
    /// deliberate: the id already carries it for every record written since filters entered the
    /// session key (the whole organized-root store), a second copy would be one more thing able to
    /// disagree with the join key, and records that predate filtered ids (three fields or fewer)
    /// degrade to one unfiltered bucket per train with no migration. Note that empty does NOT mean
    /// broadband -- per the filter-inference work, an absent FILTER header is an absent fact -- so
    /// the report renders it as "(no filter recorded)", never as a filter.
    /// </summary>
    public static string FilterFromSessionId(string sessionId)
    {
        var first = sessionId.IndexOf('|');
        if (first < 0) return "";
        var second = sessionId.IndexOf('|', first + 1);
        if (second < 0) return "";
        var third = sessionId.IndexOf('|', second + 1);
        return third < 0 ? "" : sessionId[(third + 1)..];
    }

    /// <summary>Per-(optical train, filter) sub-report. The field-radius PSF profile lives HERE,
    /// never aggregated across trains OR filters. Across trains because a Newtonian's coma grows
    /// with field radius while a refractor's does not, so a merged profile would smear the
    /// position-varying degradation the deconvolver sweep must reproduce. Across filters because
    /// autofocus never optimises red (it minimises star size where the flux is, ~500-550 nm), and
    /// how badly red loses depends on the passband: measured red/green centre width is 1.381
    /// broadband but 1.642 under narrowband on the same rigs, with the field-radius trend flipping
    /// sign between them -- so an RGB night and an Ha-OIII night on the same scope are two
    /// different measurement populations, and pooling them was the same grouping error the
    /// per-<see cref="SessionPsf.MasterStrategy"/> split already fixed for integrators. Keyed by
    /// <see cref="CalibrationResolver.CalTrain"/>'s label (camera + telescope + focal length) plus
    /// <see cref="Filter"/>.</summary>
    /// <param name="Filter">The filter this section's sessions were shot through
    /// (<see cref="FilterFromSessionId"/>), empty when none was recorded -- which the rendering
    /// states as "(no filter recorded)" rather than treating as broadband.</param>
    /// <param name="RecordedAs">The distinct header labels that folded into this train, sorted; a
    /// single entry equal to <paramref name="OpticalTrain"/> in the ordinary case. More than one
    /// means <see cref="TelescopeAliases"/> merged differently-spelled headers, and the report says
    /// so: a merge that changes how many sessions back a profile has to be visible in the artifact,
    /// or the reader cannot tell a genuine 38-session train from an over-eager alias.</param>
    /// <param name="ChannelProfiles">Per-channel stacked-profile summary across this train's
    /// sessions, indexed by channel. Kept per channel rather than pooled because the channels differ
    /// far too much to average: see <see cref="SessionPsf.MasterProfiles"/> for the measurement, and
    /// note that pooling them would report a width no channel actually has while hiding that the
    /// spread between them is train-dependent.</param>
    /// <param name="ProfileSessions">How many sessions carried a measurable profile; less than
    /// <paramref name="Sessions"/> means the rest predate the measurement and need
    /// <c>--force-psf</c>.</param>
    public sealed record TrainReport(
        string OpticalTrain,
        string Filter,
        int Sessions,
        int Subs,
        long StarsSampled,
        Percentiles SubFwhm,
        Percentiles SubHfd,
        Percentiles SubEllipticity,
        ImmutableArray<ChannelRadiusProfile> FieldRadiusProfiles,
        int RadialSessions,
        Percentiles MasterNoiseRelative,
        ImmutableArray<string> RecordedAs,
        ImmutableArray<ChannelProfile> ChannelProfiles,
        int ProfileSessions,
        ImmutableArray<StrategyCount> MasterStrategies,
        int BandedSessions);

    /// <summary>How many of a train's sessions were mastered by one integrator
/// (<see cref="SessionPsf.MasterStrategy"/>; <c>"(unrecorded)"</c> for records written before it was
    /// tracked). Carried into the report so the per-channel table can never quietly average an AHD
    /// population together with a drizzled one, which would read as chromatic optics and is demosaic
    /// behaviour: see <see cref="SessionPsf.MasterStrategy"/> for the mechanism and the measured size
    /// of it.</summary>
    public sealed record StrategyCount(string Strategy, int Sessions);

    /// <summary>One channel's stacked-profile summary across a train's sessions.</summary>
    /// <param name="Channel">Channel index, in the master's own order (0 = red for the archive's
    /// RGGB sensors).</param>
    /// <param name="Fwhm">Stacked-profile FWHM (<see cref="PsfProfileFit"/>). Brightness-controlled,
    /// so unlike the per-sub FWHM it does not carry the 25-30% swing that comes from which stars
    /// happened to be sampled.</param>
    /// <param name="MoffatBeta">Moffat exponent: LOWER means HEAVIER wings. This is the number the
    /// deconvolver's synthetic-PSF sweep should span, per channel.</param>
    /// <param name="Sessions">Sessions contributing a measurable profile on this channel. Lower than
    /// its siblings where a channel is too star-poor to stack, which happens on blue first.</param>
    public sealed record ChannelProfile(
        int Channel,
        Percentiles Fwhm,
        Percentiles MoffatBeta,
        int Sessions);

    /// <summary>The full report: an archive-wide population summary (the per-sub metrics + noise
    /// floor the denoiser sees across everything) plus a per-optical-train breakdown, each carrying
    /// its OWN field-radius PSF profile.</summary>
    public sealed record Report(
        int Sessions,
        int Subs,
        long StarsSampled,
        Percentiles SubFwhm,
        Percentiles SubHfd,
        Percentiles SubEllipticity,
        Percentiles MasterNoiseRelative,
        ImmutableArray<TrainReport> Trains);

    /// <summary>
    /// Builds the report over all <paramref name="sessions"/> at once (convenience for tests +
    /// small runs). The archive-scale builder should use <see cref="Accumulator"/> instead so each
    /// session master is released after its stats are folded in, rather than held for the whole run.
    /// </summary>
    public static async Task<Report> BuildAsync(
        IReadOnlyList<SessionRegistrar.RegisteredSession> sessions,
        int radiusBins = 5,
        float snrMin = 5f,
        int maxStars = 3000,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var acc = new Accumulator(radiusBins, snrMin, maxStars);
        foreach (var session in sessions)
        {
            await acc.AddAsync(session, logger, cancellationToken);
        }
        return acc.Build();
    }

    /// <summary>
    /// Measures ONE registered session into a persistable <see cref="SessionPsf"/>: the per-sub
    /// metrics the gate already retained (no detection), the master's relative background sigma, and
    /// one star detection on the master binned by field radius.
    ///
    /// <para>Separated from <see cref="Accumulator.Add(SessionPsf, ILogger?)"/> so the archive builder
    /// can persist the record and fold the very same object, which is what lets a resumed run rebuild
    /// the report without the master it no longer has. This is the only place a measurement is
    /// produced.</para>
    /// </summary>
    public static async Task<SessionPsf> MeasureSessionAsync(
        SessionRegistrar.RegisteredSession session,
        int radiusBins = 5,
        float snrMin = 5f,
        int maxStars = 3000,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var label = CalibrationResolver.CalTrain.OpticalTrain(session.Session.Lights[0]).Describe();

        var subFwhm = new float[session.Subs.Length];
        var subHfd = new float[session.Subs.Length];
        var subEcc = new float[session.Subs.Length];
        for (var i = 0; i < session.Subs.Length; i++)
        {
            var metrics = session.Subs[i].Metrics;
            subFwhm[i] = metrics.MedianFwhm;
            subHfd[i] = metrics.MedianHfd;
            subEcc[i] = metrics.MedianEllipticity;
        }

        return await MeasureMasterAsync(
            session.Session.Id, label, session.Master, session.CanvasWidth, session.CanvasHeight,
            subFwhm, subHfd, subEcc, session.MasterStrategy.ToString(),
            radiusBins, snrMin, maxStars, logger, cancellationToken);
    }

    /// <summary>
    /// The measurement itself, over a master plus the per-sub metrics recorded when it was integrated.
    /// Split out from <see cref="MeasureSessionAsync"/> so a re-measure can run from a RETAINED master
    /// and the previous store record instead of re-registering, which is the difference between
    /// minutes and re-reading every sub in the archive. That is the whole reason the build retains a
    /// master per session.
    ///
    /// <para>Everything here is derived from <paramref name="master"/> except the three sub arrays,
    /// which are carried through verbatim. Those came from the frame gate at registration time and
    /// CANNOT be recovered from a master, which is why a re-measure needs the prior record as well as
    /// the FITS, and why a session with tiles but no record still has to be re-registered.</para>
    /// </summary>
    /// <param name="opticalTrain">Already-described train label, taken as a string rather than
    /// re-derived: a record read back from the store carries only the label, because its frames were
    /// never re-read.</param>
    /// <param name="masterStrategy">Which integrator actually produced <paramref name="master"/>, so a
    /// re-measure must pass the STORED value through. A drizzled master relabelled as AHD would
    /// silently corrupt the per-channel comparison this report exists to make, since the difference
    /// between those two integrators is most of the apparent per-channel spread.</param>
    public static async Task<SessionPsf> MeasureMasterAsync(
        string sessionId,
        string opticalTrain,
        Image master,
        int canvasWidth,
        int canvasHeight,
        float[] subFwhm,
        float[] subHfd,
        float[] subEllipticity,
        string? masterStrategy,
        int radiusBins = 5,
        float snrMin = 5f,
        int maxStars = 3000,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var label = opticalTrain;

        var cx = canvasWidth * 0.5;
        var cy = canvasHeight * 0.5;
        var halfDiag = 0.5 * Math.Sqrt((double)canvasWidth * canvasWidth + (double)canvasHeight * canvasHeight);

        var stars = await master.FindStarsAsync(
            channel: 0, snrMin: snrMin, maxStars: maxStars, cancellationToken: cancellationToken);

        // PSF SHAPE, measured on the master per COLOUR CHANNEL. Separate from the per-bin FWHM
        // samples because it answers a different question: those describe how the width varies
        // across the field, this describes what the profile IS, which is what the deconvolver has to
        // synthesise. Per channel because the channels are not interchangeable here -- green's
        // profile is ~35% narrower than red's across the archive (see SessionPsf.MasterProfiles),
        // so measuring one and calling it the master's PSF is measuring the wrong thing.
        //
        // Each channel is detected on its OWN stars rather than reusing channel 0's. The centroids
        // barely move between channels (median 0.064 px), so that is not the reason; the reason is
        // that a star's brightness differs per channel, and PsfProfileFit's brightness band has to
        // rank the stars it will actually stack. Detection dominates the cost here, so this is the
        // one place the per-channel measurement is not free.
        var (channelCount, _, _) = master.Shape;
        var masterProfiles = new PsfProfileFit.Result?[channelCount];
        var starsByChannel = new StarList[channelCount];
        for (var c = 0; c < channelCount; c++)
        {
            starsByChannel[c] = c == 0
                ? stars
                : await master.FindStarsAsync(
                    channel: c, snrMin: snrMin, maxStars: maxStars, cancellationToken: cancellationToken);
            masterProfiles[c] = PsfProfileFit.Measure(master, c, starsByChannel[c]);

            if (masterProfiles[c] is { } fit)
            {
                logger?.LogInformation(
                    "  [{Session}] ch{Channel} PSF sampled {Stars} stars, profile FWHM {Fwhm:F3} px, Moffat beta {Beta:F2} (log-rms {MoffatRms:F3} vs gaussian {GaussRms:F3}, {Stacked} stars stacked) ({Train})",
                    sessionId, c, starsByChannel[c].Count, fit.Fwhm, fit.MoffatBeta, fit.MoffatLogRms, fit.GaussianLogRms, fit.StarsStacked, label);
            }
            else
            {
                logger?.LogInformation("  [{Session}] ch{Channel} PSF sampled {Stars} stars, profile shape not measurable ({Train})",
                    sessionId, c, starsByChannel[c].Count, label);
            }
        }

        // The field-radius bins come from ONE set of physical stars matched across the channels,
        // not from each channel's own detections. See RadiusSamples.Flux for the defect that
        // forced this: a per-channel brightness band selects a different physical brightness in
        // every channel and at every radius, and measured FWHM follows brightness, so each
        // channel's radial profile was reporting its own selection's brightness composition. With
        // one matched set the selection is identical in all three, so a channel-to-channel
        // difference in the radial trend is finally about the light and not about which stars were
        // picked. The stacked PROFILE fits above deliberately keep each channel's own full list:
        // that measurement ranks stars to stack by their brightness in the channel being fitted,
        // which is the right thing for it and the wrong thing here.
        var common = MatchStarsAcrossChannels(starsByChannel, ReferenceChannel(channelCount));
        var binsByChannel = common is null
            ? null
            : BinCommonStarsByFieldRadius(common, cx, cy, halfDiag, radiusBins);
        if (common is not null)
        {
            logger?.LogInformation(
                "  [{Session}] field-radius bins on {Common} stars matched across all {Channels} channels, banded on ch{Reference} flux ({Train})",
                sessionId, common.X.Length, channelCount, ReferenceChannel(channelCount), label);
        }
        else
        {
            // Loud, because the alternative is a silently unbinned session: too few stars survive
            // the match, so this session contributes no radial samples at all rather than
            // contributing confounded ones.
            logger?.LogWarning(
                "  [{Session}] too few stars matched across all {Channels} channels for a field-radius profile ({Train})",
                sessionId, channelCount, label);
        }

        return new SessionPsf(
            SessionId: sessionId,
            OpticalTrain: label,
            SubFwhm: subFwhm,
            SubHfd: subHfd,
            SubEllipticity: subEllipticity,
            MasterNoiseRelative: RelativeBackgroundMad(master),
            BinsByChannel: binsByChannel,
            MasterProfiles: masterProfiles,
            MasterStrategy: masterStrategy,
            RadiusSampling: binsByChannel is null ? null : CommonStarSampling);
    }

    /// <summary>Value of <see cref="SessionPsf.RadiusSampling"/> for a record whose field-radius
    /// bins were built from one star set matched across every channel and banded on a single
    /// reference flux. A record with a null marker predates the fix and was banded per channel, so
    /// its radial profile carries the inversion described on <see cref="RadiusSamples.Flux"/>; the
    /// two must never be pooled into one profile.</summary>
    public const string CommonStarSampling = "common-stars";

    /// <summary>How far apart two channels' centroids may sit and still be the same star. The
    /// measured median shift between channels is 0.064 px and the worst 0.339, so 1 px is loose
    /// enough to match everything real and far tighter than the separation at which a neighbouring
    /// star could be picked up instead.</summary>
    private const double ChannelMatchRadiusPx = 1.0;

    /// <summary>
    /// The channel whose flux the brightness band is applied to: green on a 3-channel master (index
    /// 1 in the archive's RGGB order), channel 0 otherwise.
    ///
    /// <para>Green because it carries twice the CFA sampling of red or blue, so it detects the most
    /// stars and yields the largest matched set. FIXED rather than chosen per session (say, the
    /// channel that happened to detect most) because the profile is a median pooled over every
    /// session of an optical train: a reference that varied per session would band different
    /// sessions on different physical quantities and pool the results as though they agreed.</para>
    /// </summary>
    /// <remarks>Forwards to <see cref="Image.ReferenceStarChannelFor"/>: this was the first place the
    /// choice was worked out, and for a long time the only place it applied -- the plate solver, the
    /// viewer overlay and the session all still detected in red. The value lives on
    /// <see cref="Image"/> now so they share it; this name and its test stay as the guard.</remarks>
    internal static int ReferenceChannel(int channelCount) => Image.ReferenceStarChannelFor(channelCount);

    /// <summary>One star seen in every channel: where it is, the reference channel's flux for it,
    /// and each channel's own measurement of it. Stored column-wise (<c>Fwhm[channel][star]</c>) so
    /// the per-channel arrays can be handed straight to <see cref="RadiusSamples"/> index-aligned
    /// with the shared flux.</summary>
    internal sealed record CommonStars(float[] X, float[] Y, float[] ReferenceFlux, float[][] Fwhm, float[][] Ellipticity);

    /// <summary>
    /// Matches each reference-channel star to its nearest counterpart within
    /// <see cref="ChannelMatchRadiusPx"/> in every other channel, keeping only the stars that are
    /// present in ALL of them. Returns null when too few survive.
    ///
    /// <para>Nearest-within-radius over all pairs, which is quadratic and deliberately so: it runs
    /// once per session against a star detection that already dominates the cost by orders of
    /// magnitude, and an index would add a second thing to get wrong for no measurable gain.</para>
    /// </summary>
    internal static CommonStars? MatchStarsAcrossChannels(StarList[] byChannel, int referenceChannel)
    {
        var channelCount = byChannel.Length;
        if (channelCount == 0 || referenceChannel >= channelCount)
        {
            return null;
        }

        var arrays = new ImagedStar[channelCount][];
        for (var c = 0; c < channelCount; c++)
        {
            arrays[c] = byChannel[c].ToArray();
        }

        var reference = arrays[referenceChannel];
        var x = new List<float>(reference.Length);
        var y = new List<float>(reference.Length);
        var flux = new List<float>(reference.Length);
        var fwhm = new List<float>[channelCount];
        var ecc = new List<float>[channelCount];
        for (var c = 0; c < channelCount; c++)
        {
            fwhm[c] = new List<float>(reference.Length);
            ecc[c] = new List<float>(reference.Length);
        }

        var maxDistanceSquared = ChannelMatchRadiusPx * ChannelMatchRadiusPx;
        var matchIndex = new int[channelCount];
        foreach (var star in reference)
        {
            var matchedEverywhere = true;
            for (var c = 0; c < channelCount && matchedEverywhere; c++)
            {
                matchIndex[c] = FindNearest(arrays[c], star.XCentroid, star.YCentroid, maxDistanceSquared);
                matchedEverywhere = matchIndex[c] >= 0;
            }
            if (!matchedEverywhere)
            {
                continue;
            }

            x.Add(star.XCentroid);
            y.Add(star.YCentroid);
            flux.Add(star.Flux);
            for (var c = 0; c < channelCount; c++)
            {
                var matched = arrays[c][matchIndex[c]];
                fwhm[c].Add(matched.StarFWHM);
                ecc[c].Add(matched.Ellipticity);
            }
        }

        // No star-count floor here on purpose. Binning only collects samples; deciding that a
        // session has too few stars to say anything is the BAND's job and it already has that floor
        // (FluxBand needs 40 for percentiles to mean anything, and a session below it folds in
        // unbanded and is reported as such). Repeating the floor here would silently drop small
        // sessions from the profile entirely, which is a different and worse behaviour than the
        // unbanded fold they had before.
        if (x.Count == 0)
        {
            return null;
        }

        var fwhmArrays = new float[channelCount][];
        var eccArrays = new float[channelCount][];
        for (var c = 0; c < channelCount; c++)
        {
            fwhmArrays[c] = fwhm[c].ToArray();
            eccArrays[c] = ecc[c].ToArray();
        }
        return new CommonStars(x.ToArray(), y.ToArray(), flux.ToArray(), fwhmArrays, eccArrays);
    }

    /// <summary>Index of the nearest star to a point within a squared distance, or -1.</summary>
    private static int FindNearest(ImagedStar[] stars, float x, float y, double maxDistanceSquared)
    {
        var best = -1;
        var bestDistance = maxDistanceSquared;
        for (var i = 0; i < stars.Length; i++)
        {
            double dx = stars[i].XCentroid - x;
            double dy = stars[i].YCentroid - y;
            var distance = dx * dx + dy * dy;
            if (distance <= bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }
        return best;
    }

    /// <summary>
    /// Buckets the matched star set into field-radius annuli, per channel. Every channel gets the
    /// same bin membership (the radius comes from the shared reference centroid) and the same
    /// <see cref="RadiusSamples.Flux"/> array, so one band computed downstream selects the same
    /// physical stars in all of them.
    /// </summary>
    internal static RadiusSamples[][] BinCommonStarsByFieldRadius(
        CommonStars common, double cx, double cy, double halfDiag, int radiusBins)
    {
        var channelCount = common.Fwhm.Length;
        var binOfStar = new int[common.X.Length];
        for (var i = 0; i < common.X.Length; i++)
        {
            if (halfDiag <= 0)
            {
                binOfStar[i] = 0;
                continue;
            }
            var dx = common.X[i] - cx;
            var dy = common.Y[i] - cy;
            var rNorm = Math.Sqrt(dx * dx + dy * dy) / halfDiag;
            binOfStar[i] = Math.Clamp((int)(rNorm * radiusBins), 0, radiusBins - 1);
        }

        var result = new RadiusSamples[channelCount][];
        for (var c = 0; c < channelCount; c++)
        {
            result[c] = new RadiusSamples[radiusBins];
        }
        for (var b = 0; b < radiusBins; b++)
        {
            var members = new List<int>();
            for (var i = 0; i < binOfStar.Length; i++)
            {
                if (binOfStar[i] == b)
                {
                    members.Add(i);
                }
            }

            var binFlux = new float[members.Count];
            for (var k = 0; k < members.Count; k++)
            {
                binFlux[k] = common.ReferenceFlux[members[k]];
            }
            for (var c = 0; c < channelCount; c++)
            {
                var binFwhm = new float[members.Count];
                var binEcc = new float[members.Count];
                for (var k = 0; k < members.Count; k++)
                {
                    binFwhm[k] = common.Fwhm[c][members[k]];
                    binEcc[k] = common.Ellipticity[c][members[k]];
                }
                // The SAME flux instance in every channel, which is what makes the band identical
                // across them rather than merely equal by construction today.
                result[c][b] = new RadiusSamples(binFwhm, binEcc, binFlux);
            }
        }
        return result;
    }

    /// <summary>
    /// Incremental report builder: fold one <see cref="SessionRegistrar.RegisteredSession"/> in at a
    /// time (<see cref="AddAsync"/>) then <see cref="Build"/>. Per-sub metrics come from the gate's
    /// retained <see cref="FrameMetrics"/> (no detection); the field-radius profile re-detects stars
    /// on each session master (one detection per session, on the sharpest/deepest frame, the one the
    /// deconv sweep degrades). Nothing but small accumulators is retained across sessions, so the
    /// archive-scale build can release each master after folding it in.
    /// </summary>
    public sealed class Accumulator
    {
        private readonly int _radiusBins;
        private readonly float _snrMin;
        private readonly int _maxStars;
        // One accumulator per (optical train, filter). Per train because the field-radius profile is
        // optics-specific -- it must not merge a coma-heavy Newtonian with a flat-field refractor --
        // and per filter because the chromatic behaviour it measures is passband-specific: an RGB
        // night and a narrowband night on the same scope are two different populations (see
        // TrainReport's remarks for the measured sizes). The overall population summary is derived
        // by concatenation. Keyed by the train's DESCRIBED label rather than the CalTrain value,
        // because a record read back from the store carries only the label (its frames were never
        // re-read) and must bucket with a freshly measured session of the same train; the filter
        // half of the key comes out of the session id (FilterFromSessionId), which has carried it
        // since filters entered the session key.
        private readonly Dictionary<(string Label, string Filter), TrainAcc> _byTrain = new();

        public Accumulator(int radiusBins = 5, float snrMin = 5f, int maxStars = 3000)
        {
            _radiusBins = radiusBins;
            _snrMin = snrMin;
            _maxStars = maxStars;
        }

        /// <summary>Measures a freshly registered session then folds it in. The measurement half is
        /// <see cref="MeasureSessionAsync"/> so a caller that wants to PERSIST the record (the
        /// archive builder, via <see cref="DatasetPsfStore"/>) measures once and folds the same
        /// record, rather than there being a second way to compute one.</summary>
        public async Task AddAsync(SessionRegistrar.RegisteredSession session, ILogger? logger = null, CancellationToken cancellationToken = default)
            => Add(await MeasureSessionAsync(session, _radiusBins, _snrMin, _maxStars, logger, cancellationToken), logger);

        /// <summary>
        /// Folds one session's persisted samples into the accumulator. This is the ONLY path that
        /// mutates the accumulator, so a record read back from <see cref="DatasetPsfStore"/> and a
        /// record just measured are treated identically by construction.
        /// </summary>
        public void Add(SessionPsf record, ILogger? logger = null)
        {
            // A record with no per-channel bins predates them (they were channel 0 only, under a
            // different member) and is still folded for everything else it carries: the sub metrics and
            // the noise floor are unaffected, and dropping the session outright would narrow the report
            // for a field-radius profile it can regain from its retained master. RadialSessions below
            // is what tells the reader how many are covered. The per-channel bin-count check lives in
            // the loop, since a channel can be short on its own.
            //
            // Keyed by the STORED label, so a resumed session (whose frames were never re-read) lands
            // in the same train bucket as a freshly measured one -- but canonicalised first, so one
            // lens recorded under two TELESCOP spellings is one train here even though the store
            // faithfully kept both names. Display-time merge: see TelescopeAliases. The filter joins
            // the key from the session id, so an RGB night and a narrowband night on the same scope
            // stop pooling into one profile; a record whose id predates filters lands in the train's
            // single "(no filter recorded)" bucket, which is exactly the pre-filter behaviour.
            var label = TelescopeAliases.CanonicalizeLabel(record.OpticalTrain);
            var filter = FilterFromSessionId(record.SessionId);
            if (!_byTrain.TryGetValue((label, filter), out var acc))
            {
                _byTrain[(label, filter)] = acc = new TrainAcc(label, filter, _radiusBins);
            }
            acc.RecordedAs.Add(record.OpticalTrain);

            acc.Sessions++;
            var strategy = string.IsNullOrWhiteSpace(record.MasterStrategy) ? "(unrecorded)" : record.MasterStrategy;
            acc.MasterStrategies[strategy] = acc.MasterStrategies.TryGetValue(strategy, out var seen) ? seen + 1 : 1;
            for (var c = 0; c < (record.MasterProfiles?.Length ?? 0); c++)
            {
                if (record.MasterProfiles![c] is not { } profile)
                {
                    continue;
                }
                // Grown on demand: a record can carry more channels than an earlier one (mono today,
                // colour tomorrow), and a channel that is measurable on one session but not another
                // must not shift the others' samples by one slot.
                while (acc.ChannelFwhm.Count <= c)
                {
                    acc.ChannelFwhm.Add(new List<float>());
                    acc.ChannelBeta.Add(new List<float>());
                }
                acc.ChannelFwhm[c].Add((float)profile.Fwhm);
                acc.ChannelBeta[c].Add((float)profile.MoffatBeta);
            }
            acc.Fwhm.AddRange(record.SubFwhm);
            acc.Hfd.AddRange(record.SubHfd);
            acc.Ecc.AddRange(record.SubEllipticity);
            acc.Subs += record.SubFwhm.Length;
            acc.Noise.Add(record.MasterNoiseRelative);
            // Brightness-banded PER SESSION, never across sessions: the band has to be a percentile of
            // THIS session's own stars, because aperture, exposure and sky level differ between
            // sessions and a shared absolute flux cut would keep the deep sessions' faint tail while
            // discarding a shallow session entirely. See RadiusSamples.Flux for what the band is for;
            // without it this profile reports each annulus's brightness composition and inverts.
            // Banded PER CHANNEL as well as per session: a star's flux differs between channels (which
            // is why each channel is detected on its own stars in the first place), so one band shared
            // across channels would cut red and blue at green's brightness.
            var banded = false;
            var foldedAnyChannel = false;
            for (var c = 0; c < (record.BinsByChannel?.Length ?? 0); c++)
            {
                var channelBins = record.BinsByChannel![c];
                if (channelBins.Length != _radiusBins)
                {
                    // Same loud-not-silent posture as the whole-record check above.
                    logger?.LogWarning(
                        "PSF record for {Session} channel {Channel} has {Actual} radius bin(s), expected {Expected} -- that channel is not folded into the report.",
                        record.SessionId, c, channelBins.Length, _radiusBins);
                    continue;
                }

                // Grown on demand, like ChannelFwhm: a record can carry more channels than an earlier
                // one, and a channel measurable on one session but not another must not shift the
                // others' samples by one slot.
                while (acc.ChannelBinFwhm.Count <= c)
                {
                    acc.ChannelBinFwhm.Add(NewBinLists(_radiusBins));
                    acc.ChannelBinEcc.Add(NewBinLists(_radiusBins));
                }

                foldedAnyChannel = true;
                var band = FluxBand(channelBins);
                banded |= band is not null;
                for (var b = 0; b < _radiusBins; b++)
                {
                    var samples = channelBins[b];
                    var flux = samples.Flux;
                    for (var i = 0; i < samples.Fwhm.Length; i++)
                    {
                        // A record written before Flux was stored has no band available. Those keep the
                        // old (confounded) behaviour rather than being dropped, so an existing store
                        // still renders; the rendered table says which sessions are banded.
                        if (band is not null && flux is not null && i < flux.Length
                            && (flux[i] < band.Value.Low || flux[i] > band.Value.High))
                        {
                            continue;
                        }
                        acc.ChannelBinFwhm[c][b].Add(samples.Fwhm[i]);
                        if (i < samples.Ellipticity.Length)
                        {
                            acc.ChannelBinEcc[c][b].Add(samples.Ellipticity[i]);
                        }
                        acc.StarsSampled++;
                    }
                }
            }
            if (banded)
            {
                acc.BandedSessions++;
            }
            // Gated on a channel having ACTUALLY been folded, not merely on bins being present: a
            // record whose every channel was skipped for a bin-count mismatch contributes no radial
            // samples, and counting it would overstate the profile's coverage.
            if (foldedAnyChannel)
            {
                acc.RadialSessions++;
            }
        }

        private static List<float>[] NewBinLists(int radiusBins)
        {
            var lists = new List<float>[radiusBins];
            for (var b = 0; b < radiusBins; b++)
            {
                lists[b] = new List<float>();
            }
            return lists;
        }

        /// <summary>
        /// The flux band for one session's field-radius stars: the 55th to 90th percentile over every
        /// annulus together. <see langword="null"/> when the record predates stored flux, or has too
        /// few stars for percentiles to mean anything.
        ///
        /// <para>The low end mirrors <see cref="PsfProfileFit"/>'s own band (its 55th percentile of
        /// peak), which exists because whatever background survives subtraction is a larger fraction
        /// of a fainter star's peak. The high end stops below the top decile rather than at the very
        /// top, so a handful of near-saturated stars cannot dominate an annulus that happens to hold
        /// one.</para>
        /// </summary>
        private static (float Low, float High)? FluxBand(RadiusSamples[] bins)
        {
            var all = new List<float>();
            foreach (var bin in bins)
            {
                if (bin.Flux is null)
                {
                    return null;
                }
                all.AddRange(bin.Flux);
            }
            if (all.Count < 40)
            {
                return null;
            }
            all.Sort();
            return (all[(int)(0.55 * (all.Count - 1))], all[(int)(0.90 * (all.Count - 1))]);
        }

        public Report Build()
        {
            var trains = ImmutableArray.CreateBuilder<TrainReport>(_byTrain.Count);
            // Overall population = concatenation across trains (only the field-radius profile stays
            // per-train). Trains are ordered by label so the report is deterministic across runs.
            var allFwhm = new List<float>();
            var allHfd = new List<float>();
            var allEcc = new List<float>();
            var allNoise = new List<double>();
            var totalSessions = 0;
            var totalSubs = 0;
            long totalStars = 0;

            foreach (var acc in _byTrain.Values
                .OrderBy(a => a.Label, StringComparer.Ordinal)
                .ThenBy(a => a.Filter, StringComparer.Ordinal))
            {
                var radialProfiles = ImmutableArray.CreateBuilder<ChannelRadiusProfile>(acc.ChannelBinFwhm.Count);
                for (var c = 0; c < acc.ChannelBinFwhm.Count; c++)
                {
                    var profile = ImmutableArray.CreateBuilder<RadiusBin>(_radiusBins);
                    for (var b = 0; b < _radiusBins; b++)
                    {
                        profile.Add(new RadiusBin(
                            RMin: (double)b / _radiusBins,
                            RMax: (double)(b + 1) / _radiusBins,
                            MedianFwhm: Median(acc.ChannelBinFwhm[c][b]),
                            MedianEllipticity: Median(acc.ChannelBinEcc[c][b]),
                            Stars: acc.ChannelBinFwhm[c][b].Count));
                    }
                    radialProfiles.Add(new ChannelRadiusProfile(c, profile.MoveToImmutable()));
                }
                var channels = ImmutableArray.CreateBuilder<ChannelProfile>(acc.ChannelFwhm.Count);
                for (var c = 0; c < acc.ChannelFwhm.Count; c++)
                {
                    channels.Add(new ChannelProfile(
                        Channel: c,
                        Fwhm: PercentilesOf(acc.ChannelFwhm[c]),
                        MoffatBeta: PercentilesOf(acc.ChannelBeta[c]),
                        Sessions: acc.ChannelFwhm[c].Count));
                }

                trains.Add(new TrainReport(
                    OpticalTrain: acc.Label,
                    Filter: acc.Filter,
                    RecordedAs: [.. acc.RecordedAs],
                    Sessions: acc.Sessions,
                    Subs: acc.Subs,
                    StarsSampled: acc.StarsSampled,
                    SubFwhm: PercentilesOf(acc.Fwhm),
                    SubHfd: PercentilesOf(acc.Hfd),
                    SubEllipticity: PercentilesOf(acc.Ecc),
                    ChannelProfiles: channels.MoveToImmutable(),
                    // The count for the train as a whole is the best-covered channel: a session whose
                    // blue is too star-poor to stack still HAS a measured profile, and reporting the
                    // worst channel's count would understate coverage.
                    ProfileSessions: acc.ChannelFwhm.Count == 0 ? 0 : acc.ChannelFwhm.Max(l => l.Count),
                    FieldRadiusProfiles: radialProfiles.MoveToImmutable(),
                    RadialSessions: acc.RadialSessions,
                    MasterNoiseRelative: PercentilesOf(acc.Noise),
                    MasterStrategies: [.. acc.MasterStrategies.Select(kv => new StrategyCount(kv.Key, kv.Value))],
                    BandedSessions: acc.BandedSessions));

                allFwhm.AddRange(acc.Fwhm);
                allHfd.AddRange(acc.Hfd);
                allEcc.AddRange(acc.Ecc);
                allNoise.AddRange(acc.Noise);
                totalSessions += acc.Sessions;
                totalSubs += acc.Subs;
                totalStars += acc.StarsSampled;
            }

            return new Report(
                Sessions: totalSessions,
                Subs: totalSubs,
                StarsSampled: totalStars,
                SubFwhm: PercentilesOf(allFwhm),
                SubHfd: PercentilesOf(allHfd),
                SubEllipticity: PercentilesOf(allEcc),
                MasterNoiseRelative: PercentilesOf(allNoise),
                Trains: trains.MoveToImmutable());
        }

        /// <summary>Per-(train, filter) accumulator: the same small metric lists + radius bins the
        /// whole report used to keep once, now held one instance per optical train per filter.</summary>
        private sealed class TrainAcc
        {
            public string Label { get; }
            /// <summary>The session-id-derived filter this bucket aggregates, empty for sessions
            /// with none recorded (including every record written before filters entered the
            /// session key).</summary>
            public string Filter { get; }
            /// <summary>Distinct header labels folded into this train (usually just one). A
            /// SortedSet so the rendered note is deterministic across runs, like everything else in
            /// this report.</summary>
            public readonly SortedSet<string> RecordedAs = new(StringComparer.Ordinal);
            public int Sessions;
            public int Subs;
            public long StarsSampled;
            public readonly List<float> Fwhm = new();
            public readonly List<float> Hfd = new();
            public readonly List<float> Ecc = new();
            public readonly List<double> Noise = new();
            /// <summary>One entry per session that carried a measurable master profile; shorter than
            /// <see cref="Sessions"/> for a train whose older records predate the measurement.</summary>
            /// <summary>Stacked-profile samples per channel; outer index is the channel.</summary>
            public readonly List<List<float>> ChannelFwhm = new();
            public readonly List<List<float>> ChannelBeta = new();
            /// <summary>Field-radius samples per channel, then per annulus. Outer index is the channel,
            /// grown on demand; inner array is always <c>radiusBins</c> long.</summary>
            public readonly List<List<float>[]> ChannelBinFwhm = new();
            public readonly List<List<float>[]> ChannelBinEcc = new();
            /// <summary>Sessions carrying per-channel field-radius bins at all. Lower than
            /// <see cref="Sessions"/> where records predate them and need a re-measure, which now reads
            /// the retained master rather than re-registering.</summary>
            public int RadialSessions;
            /// <summary>Sessions per master integrator. Sorted so the rendered line is deterministic,
            /// like every other aggregate here.</summary>
            public readonly SortedDictionary<string, int> MasterStrategies = new(StringComparer.Ordinal);
            /// <summary>Sessions whose field-radius stars were brightness-banded. Lower than
            /// <see cref="Sessions"/> where records predate stored flux, and the rendered profile says
            /// so, because a mixed table is the one thing this measurement must not do silently.</summary>
            public int BandedSessions;

            public TrainAcc(string label, string filter, int radiusBins)
            {
                Label = label;
                Filter = filter;
            }
        }
    }

    /// <summary>Renders the report as a human-readable Markdown file (the P0 "archive PSF/noise
    /// distribution report" deliverable).</summary>
    public static async Task WriteMarkdownAsync(Report report, string path, CancellationToken cancellationToken = default)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("# Dataset PSF / Noise Distribution Report");
        sb.AppendLine();
        sb.AppendLine(string.Create(ci, $"- Sessions: {report.Sessions}"));
        sb.AppendLine(string.Create(ci, $"- Subs (registered): {report.Subs}"));
        sb.AppendLine(string.Create(ci, $"- Stars sampled (field-radius profile): {report.StarsSampled}"));
        sb.AppendLine(string.Create(ci, $"- Field-radius sections (optical train x filter): {report.Trains.Length}"));
        sb.AppendLine();
        sb.AppendLine("## Per-sub PSF distribution (median-of-frame metrics, all trains)");
        sb.AppendLine();
        sb.AppendLine("| Metric | p5 | p25 | p50 | p75 | p95 |");
        sb.AppendLine("|--------|----|-----|-----|-----|-----|");
        AppendPct(sb, ci, "FWHM (px)", report.SubFwhm);
        AppendPct(sb, ci, "HFD (px)", report.SubHfd);
        AppendPct(sb, ci, "Ellipticity", report.SubEllipticity);
        sb.AppendLine();
        sb.AppendLine("## Noise floor (per-session master background sigma, relative to full-scale, all trains)");
        sb.AppendLine();
        sb.AppendLine("| Metric | p5 | p25 | p50 | p75 | p95 |");
        sb.AppendLine("|--------|----|-----|-----|-----|-----|");
        AppendPct(sb, ci, "MAD / max", report.MasterNoiseRelative);
        sb.AppendLine();
        sb.AppendLine("## Field-radius PSF profile (per optical train PER FILTER, centre -> corner)");
        sb.AppendLine();
        sb.AppendLine("Drives the deconvolver's position-varying synthetic-PSF sweep: sample FWHM per");
        sb.AppendLine("field-radius bin so corner degradation matches the optics. Reported PER OPTICAL");
        sb.AppendLine("TRAIN -- a Newtonian's coma grows toward the corner while a refractor's field");
        sb.AppendLine("stays flat, so a single merged profile would smear both -- and PER FILTER,");
        sb.AppendLine("because autofocus optimises where the flux is (~500-550 nm) and how badly red");
        sb.AppendLine("loses depends on the passband: red/green centre width measures 1.381 broadband");
        sb.AppendLine("but 1.642 narrowband on the same rigs, with the radial trend flipping sign.");
        sb.AppendLine("\"(no filter recorded)\" means exactly that -- an absent FILTER header is an");
        sb.AppendLine("absent fact, not broadband. Sweep each section against its own row set.");
        sb.AppendLine();
        foreach (var train in report.Trains)
        {
            sb.AppendLine(train.Filter.Length > 0
                ? string.Create(ci, $"### {train.OpticalTrain} [{train.Filter}]")
                : string.Create(ci, $"### {train.OpticalTrain}"));
            sb.AppendLine();
            // Which glass this is, coarsely, so the reader does not need to know the hardware by
            // name to judge whether a corner trend is plausible -- and so the first Newtonian to
            // enter the archive announces itself. Filter stated on its own line as well as in the
            // heading, because "(no filter recorded)" must be legible where the numbers are read.
            sb.AppendLine(string.Create(ci,
                $"- Filter: {(train.Filter.Length > 0 ? train.Filter : "(no filter recorded)")} | Optical system: {OpticalSystems.ClassifyLabel(train.OpticalTrain).Label}"));
            // Only when an alias actually merged something: on the ordinary single-spelling train
            // this line would be noise repeating the heading.
            if (train.RecordedAs.Length > 1)
            {
                sb.AppendLine(string.Create(ci,
                    $"- Merged from {train.RecordedAs.Length} header spellings: {string.Join("; ", train.RecordedAs)}"));
            }
            sb.AppendLine(string.Create(ci,
                $"- Sessions: {train.Sessions} | Subs: {train.Subs} | Stars: {train.StarsSampled}"));
            sb.AppendLine(string.Create(ci,
                $"- FWHM p50: {train.SubFwhm.P50:F3} px | Ellipticity p50: {train.SubEllipticity.P50:F3} | Noise p50: {train.MasterNoiseRelative.P50:F5}"));
            if (train.ProfileSessions > 0)
            {
                // The PSF SHAPE, which is what the deconvolver's synthetic sweep has to reproduce.
                // Lower beta = heavier wings; a Gaussian is beta -> infinity. Per channel because
                // they differ far more than a reader would assume: see SessionPsf.MasterProfiles.
                sb.AppendLine(string.Create(ci,
                    $"- Master PSF profile ({train.ProfileSessions}/{train.Sessions} sessions), PER CHANNEL:"));
                // Named, and called out when it is mixed. The per-channel ratios below are only an
                // optical statement within ONE integrator: an AHD master reconstructs green from
                // closer neighbours than red, which manufactures "green is narrower" (measured on one
                // session both ways: G/R 0.767 AHD vs 0.947 drizzled). Averaging the two populations
                // produces a number that describes neither, and nothing in the table would show it.
                if (train.MasterStrategies.Length > 0)
                {
                    var mix = string.Join(", ", train.MasterStrategies.Select(s => $"{s.Strategy} {s.Sessions}"));
                    sb.AppendLine(train.MasterStrategies.Length == 1
                        ? $"- Master integrator: {mix}"
                        : $"- Master integrator MIXED ({mix}) -- the per-channel ratios below average " +
                          "populations that are not comparable; split by MasterStrategy in stats/" +
                          $"{DatasetPsfStore.FileName} before using them");
                }
                sb.AppendLine();
                sb.AppendLine("| Channel | Sessions | FWHM p50 (px) | vs ch0 | Moffat beta p5 | p50 | p95 |");
                sb.AppendLine("|---------|----------|---------------|--------|----------------|-----|-----|");
                var reference = train.ChannelProfiles.Length > 0 ? train.ChannelProfiles[0].Fwhm.P50 : 0;
                foreach (var channel in train.ChannelProfiles)
                {
                    var ratio = reference > 0
                        ? string.Create(ci, $"{channel.Fwhm.P50 / reference:F3}")
                        : "-";
                    sb.AppendLine(string.Create(ci,
                        $"| {channel.Channel} | {channel.Sessions} | {channel.Fwhm.P50:F3} | {ratio} | " +
                        $"{channel.MoffatBeta.P5:F2} | {channel.MoffatBeta.P50:F2} | {channel.MoffatBeta.P95:F2} |"));
                }
            }
            else if (train.Sessions > 0)
            {
                sb.AppendLine("- Master PSF profile: not measured for this train (records predate it; re-run with --force-psf)");
            }
            sb.AppendLine();
            // The band is stated, because the same numbers mean something different without it: an
            // unbanded profile reports each annulus's brightness composition and can invert (measured:
            // 3.03 px centre to 2.85 px corner, i.e. corners apparently sharper, on a session whose
            // single raw frames run 2.96 to 3.42 the correct way round).
            sb.AppendLine(train.BandedSessions == train.Sessions
                ? "- Field-radius stars are brightness-banded per session (55th-90th flux percentile)."
                : string.Create(ci, $"- Field-radius stars brightness-banded for {train.BandedSessions}/{train.Sessions} sessions; the rest predate stored flux and are NOT comparable (re-run with --force-psf)."));
            sb.AppendLine();
            if (train.RadialSessions < train.Sessions)
            {
                sb.AppendLine(string.Create(ci, $"- Field-radius profile covers {train.RadialSessions}/{train.Sessions} sessions; the rest predate per-channel bins (re-run with --force-psf, which now reads the retained master)."));
                sb.AppendLine();
            }

            if (train.FieldRadiusProfiles.Length == 0)
            {
                sb.AppendLine("- Field-radius PSF profile: not measured for this train.");
                sb.AppendLine();
            }

            // PER CHANNEL, never pooled. Red is the widest channel on 48 of 49 archive masters, so a
            // single table built from channel 0 described red's field dependence while reading as the
            // frame's, and averaging the channels would report a width no channel actually has.
            foreach (var channel in train.FieldRadiusProfiles)
            {
                sb.AppendLine(string.Create(ci, $"Channel {channel.Channel}:"));
                sb.AppendLine();
                sb.AppendLine("| Radius (norm) | Median FWHM (px) | Median ellipticity | Stars |");
                sb.AppendLine("|---------------|------------------|--------------------|-------|");
                foreach (var bin in channel.Bins)
                {
                    sb.AppendLine(string.Create(ci, $"| {bin.RMin:F2}-{bin.RMax:F2} | {bin.MedianFwhm:F3} | {bin.MedianEllipticity:F3} | {bin.Stars} |"));
                }
                sb.AppendLine();
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        await File.WriteAllTextAsync(path, sb.ToString(), cancellationToken);
    }

    private static void AppendPct(StringBuilder sb, CultureInfo ci, string label, Percentiles p) =>
        sb.AppendLine(string.Create(ci, $"| {label} | {p.P5:F3} | {p.P25:F3} | {p.P50:F3} | {p.P75:F3} | {p.P95:F3} |"));

    /// <summary>MAD of the master's channel 0 divided by <see cref="Image.MaxValue"/>; a
    /// full-scale-relative background sigma proxy (background-dominated, robust to the ~few % star
    /// pixels), comparable across cameras/scales.</summary>
    private static double RelativeBackgroundMad(Image master)
    {
        var span = master.GetChannelSpan(0);
        var buf = new float[span.Length];
        var n = 0;
        n = StatisticsHelper.CompactFinite(span, buf);
        if (n == 0) return 0.0;
        var slice = buf.AsSpan(0, n);
        var (_, mad) = StatisticsHelper.MedianAndMad(slice);
        var max = master.MaxValue;
        return max > 0 ? mad / max : mad;
    }

    private static double Median(List<float> values)
    {
        if (values.Count == 0) return 0.0;
        values.Sort();
        return values[values.Count / 2];
    }

    internal static Percentiles PercentilesOf<T>(List<T> values) where T : struct, IConvertible
    {
        if (values.Count == 0) return Percentiles.Empty;
        var arr = new double[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            arr[i] = values[i].ToDouble(CultureInfo.InvariantCulture);
        }
        Array.Sort(arr);
        return new Percentiles(
            Pick(arr, 0.05), Pick(arr, 0.25), Pick(arr, 0.50), Pick(arr, 0.75), Pick(arr, 0.95));
    }

    private static double Pick(double[] sorted, double q)
    {
        if (sorted.Length == 1) return sorted[0];
        var idx = (int)Math.Round(q * (sorted.Length - 1), MidpointRounding.AwayFromZero);
        return sorted[Math.Clamp(idx, 0, sorted.Length - 1)];
    }
}
