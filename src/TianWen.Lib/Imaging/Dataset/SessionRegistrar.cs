using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Drawing;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Stacking;

namespace TianWen.Lib.Imaging.Dataset;

/// <summary>
/// Per-session registration + master integration for the dataset builder
/// (docs/plans/ai-denoise-deconv.md §2.4, task P0/#39). Ties the measure+gate
/// (<see cref="SessionFrameAnalyzer"/>) to the stacker's registration + integration
/// seams and emits, for one <see cref="ImagingSession"/>:
/// <list type="bullet">
///   <item>the <b>registered subs</b> — each surviving light calibrated, debayered, and
///     warped onto a common union canvas, persisted as scratch FITS so the tiler (#40)
///     can read cell footprints back without re-warping. Cell (i, j) of any two subs is
///     an N2N training pair by construction (§2.4).</item>
///   <item>the <b>session master</b> — the robust integration of those subs, the N2N eval
///     truth and the deconv synthetic-degradation source (§2.1/§2.2).</item>
/// </list>
///
/// <para><b>One path with the stacker.</b> Reference pick, quad-tolerance ladder, rigid
/// refinement, union-canvas geometry, rejector selection, and the streaming integrator are
/// the same <c>StackingPipeline</c> code (<see cref="CanvasGeometry"/>,
/// <see cref="RegistrationRefiner"/>, <see cref="StackingPipeline.BuildRejector"/>,
/// <see cref="Float16StagedStrategy"/>), so a dataset master registers byte-for-byte like a
/// <c>tianwen stack</c> master. The only copied code is the two-line
/// <see cref="TryMatchAsync"/> tolerance ladder (verbatim from <c>StackingPipeline</c>).</para>
///
/// <para><b>Zero re-detection.</b> The gate already ran <see cref="Image.FindStarsAsync"/>
/// on every sub and <see cref="SessionFrameAnalyzer.AnalyzedFrame"/> retains the star list,
/// so both the reference pick and the per-sub quad match run off those retained lists —
/// no image is reloaded to detect stars. Pixels are reloaded exactly once more (the warp
/// pass) because holding every debayered sub in RAM would blow the budget on a large
/// session; the integrator then re-reads the warped scratch FITS (cheap, no debayer).</para>
/// </summary>
public static class SessionRegistrar
{
    /// <summary>Absolute floor of matched stars for a quad fit. Below this the affine
    /// solve is unstable; the sub is dropped rather than misregistered. Mirrors
    /// <c>StackingPipeline.MinStarsForMatch</c>.</summary>
    private const int MinStarsForMatch = 24;

    /// <summary>Cap on the brightest stars used to build quad fingerprints. Bright stars
    /// reproduce across detection-threshold jitter between frames, so the top-K signature
    /// stays stable. Mirrors the stacker's <c>QuadStars</c> default.</summary>
    private const int QuadStars = 500;

    /// <summary>Quad-match tolerance ladder — try tight first, loosen on failure. Verbatim
    /// from <c>StackingPipeline.QuadTolerances</c>.</summary>
    private static readonly float[] QuadTolerances = [0.008f, 0.02f, 0.05f, 0.1f, 0.2f, 0.5f];

    /// <summary>One surviving light registered onto the session's union canvas.</summary>
    /// <param name="Source">The original raw light (header-only handle). Carries the FITS
    /// metadata — gain, exposure, filter, temperature — that the tile manifest (#40) needs;
    /// the scratch FITS holds pixels only.</param>
    /// <param name="WarpedPath">Scratch FITS of the calibrated + debayered sub warped to the
    /// canvas grid (float32, linear, NaN outside the source footprint). Shares the exact
    /// pixel grid with every other sub and the master, so cell (i, j) is a fixed sky footprint
    /// across the whole session.</param>
    /// <param name="TransformToCanvas">Composed source→canvas affine (registration transform
    /// left-multiplied by the union-canvas shift).</param>
    /// <param name="Metrics">The sub's PSF metrics from the gate (retained, not recomputed) —
    /// median HFD/FWHM/ellipticity + star count. Feeds the per-tile manifest + stats report.</param>
    public sealed record RegisteredSub(
        FrameInfo Source,
        string WarpedPath,
        Matrix3x2 TransformToCanvas,
        FrameMetrics Metrics);

    /// <summary>The registered + integrated output for one session.</summary>
    /// <param name="Session">The source session.</param>
    /// <param name="Master">Integrated session master on the union canvas (RGB float, linear,
    /// median-normalised by the integrator). N2N eval truth + deconv degradation source.</param>
    /// <param name="Subs">The registered subs, in registration order. Every warped scratch FITS
    /// shares the canvas grid with <paramref name="Master"/>.</param>
    /// <param name="CanvasWidth">Union-canvas width (pixels). Shared by the master + every sub.</param>
    /// <param name="CanvasHeight">Union-canvas height (pixels).</param>
    /// <param name="StatsRect">The all-frames-overlap intersection rectangle — the region where
    /// every sub contributes, useful for the tiler's structure-biased cell sampling and for
    /// per-frame stretch statistics.</param>
    /// <param name="Reference">The sub chosen as the registration reference (identity transform).</param>
    /// <param name="GatedCount">Subs that survived the quality gate (registration candidates).</param>
    /// <param name="RegisteredCount">Subs that registered successfully (== <see cref="Subs"/> length).</param>
    /// <param name="SkippedCount">Gated subs that failed to register (too few stars / no quad fit).</param>
    public sealed record RegisteredSession(
        ImagingSession Session,
        Image Master,
        ImmutableArray<RegisteredSub> Subs,
        int CanvasWidth,
        int CanvasHeight,
        Rectangle StatsRect,
        FrameInfo Reference,
        int GatedCount,
        int RegisteredCount,
        int SkippedCount);

    /// <summary>
    /// Measures + gates a session's lights, registers the survivors to a common reference,
    /// warps them onto the union canvas (persisted to <paramref name="scratchDir"/>), and
    /// integrates the session master. Returns <c>null</c> when the session cannot yield a
    /// usable master — too few survivors after the gate, or fewer than two subs register.
    /// </summary>
    /// <param name="session">The session to register.</param>
    /// <param name="calibrator">Bias/dark/flat masters resolved by header match, or <c>null</c>
    /// to register uncalibrated (test path only — real N2N pairs MUST be calibrated so the two
    /// subs don't share a fixed-pattern dark-current signal, which would violate the
    /// noise-independence assumption).</param>
    /// <param name="scratchDir">Root for per-session warped-sub + integration scratch. The
    /// session's subdirectory is wiped + recreated; the caller deletes it after tiling.</param>
    /// <param name="qualityRejectSigma">Session-relative MAD gate threshold (0 disables the
    /// relative gate; zero-star frames are still dropped). See <see cref="SessionFrameAnalyzer.ApplyGate"/>.</param>
    /// <param name="qualityMaxRejectFraction">Keep-floor for the gate (purity over yield — 0.5
    /// for the dataset vs the stacker's 0.2).</param>
    /// <param name="minSubs">Minimum survivors required to build a session master.</param>
    /// <param name="debayerAlgorithm">Debayer used for both measurement and warping.</param>
    /// <param name="logger">Optional progress log.</param>
    public static async Task<RegisteredSession?> RegisterAsync(
        ImagingSession session,
        Calibrator? calibrator,
        string scratchDir,
        float qualityRejectSigma = 3f,
        float qualityMaxRejectFraction = 0.5f,
        int minSubs = 10,
        DebayerAlgorithm debayerAlgorithm = DebayerAlgorithm.VNG,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        // 1. Measure every light: calibrate -> debayer -> detect stars -> PSF metrics.
        //    The star list is retained on each AnalyzedFrame so nothing below re-detects.
        var analyzed = new List<SessionFrameAnalyzer.AnalyzedFrame>(session.Lights.Length);
        foreach (var light in session.Lights)
        {
            cancellationToken.ThrowIfCancellationRequested();
            analyzed.Add(await SessionFrameAnalyzer.MeasureAsync(
                light, calibrator, debayerAlgorithm, cancellationToken: cancellationToken));
        }

        // 2. Session-relative quality gate (star-count-led; see SessionFrameAnalyzer doc).
        var gate = SessionFrameAnalyzer.ApplyGate(analyzed, qualityRejectSigma, qualityMaxRejectFraction);
        logger?.LogInformation(
            "  [{Session}] gate: kept {Kept}/{Total} ({Rejected} rejected{Floor})",
            session.Id, gate.Kept.Length, analyzed.Count, gate.Rejected.Length,
            gate.KeepFloorTriggered ? ", floor" : "");
        if (gate.Kept.Length < minSubs)
        {
            logger?.LogWarning("  [{Session}] {Kept} subs survived the gate (< {Min}) -- skipped",
                session.Id, gate.Kept.Length, minSubs);
            return null;
        }

        var survivors = gate.Kept;

        // 3. Reference pick: composite PSF-quality score over the RETAINED metrics (no reload,
        //    no re-detection). Same formula as StackingPipeline -- most stars, penalised by broad
        //    PSF (HFD) and elongation (ellipticity). Rewards sharp-round-many simultaneously.
        var reference = survivors[0];
        var bestScore = float.NegativeInfinity;
        foreach (var f in survivors)
        {
            var m = f.Metrics;
            var score = m.StarCount / (MathF.Max(m.MedianHfd, 1f) * (1f + 4f * m.MedianEllipticity));
            if (score > bestScore)
            {
                bestScore = score;
                reference = f;
            }
        }
        var refW = reference.Frame.Width;
        var refH = reference.Frame.Height;
        logger?.LogInformation(
            "  [{Session}] reference {File} (stars={Stars} hfd={Hfd:F2} ecc={Ecc:F3} score={Score:F1})",
            session.Id, Path.GetFileName(reference.Frame.Path),
            reference.Metrics.StarCount, reference.Metrics.MedianHfd, reference.Metrics.MedianEllipticity, bestScore);

        // 4. Register each survivor against the reference from the RETAINED star lists.
        using var referenceSorted = new SortedStarList(reference.Stars);
        _ = await referenceSorted.FindQuadsAsync(maxStars: QuadStars, cancellationToken: cancellationToken);
        var matched = new List<(SessionFrameAnalyzer.AnalyzedFrame Frame, Matrix3x2 Transform)>(survivors.Length);
        var skipped = 0;
        foreach (var f in survivors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReferenceEquals(f, reference))
            {
                matched.Add((f, Matrix3x2.Identity));
                continue;
            }
            if (f.Stars.Count < MinStarsForMatch)
            {
                skipped++;
                logger?.LogDebug("  [{Session}] {File} stars={Stars} -> skip (too few)",
                    session.Id, Path.GetFileName(f.Frame.Path), f.Stars.Count);
                continue;
            }
            using var lightSorted = new SortedStarList(f.Stars);
            _ = await lightSorted.FindQuadsAsync(maxStars: QuadStars, cancellationToken: cancellationToken);
            var (solution, _, _) = await TryMatchAsync(lightSorted, referenceSorted, QuadStars);
            if (solution is null)
            {
                skipped++;
                logger?.LogDebug("  [{Session}] {File} -> skip (no quad fit at any tolerance)",
                    session.Id, Path.GetFileName(f.Frame.Path));
                continue;
            }
            // Rigid (rotation + isotropic scale + translation) refinement on top of the bulk
            // quad fit -- closes the sub-pixel residual the fingerprint match averages away.
            var refined = RegistrationRefiner.RefineRigid(lightSorted, referenceSorted, solution.Value).Refined;
            matched.Add((f, refined));
        }
        logger?.LogInformation("  [{Session}] registered {Matched}/{Survivors} (skipped {Skipped})",
            session.Id, matched.Count, survivors.Length, skipped);
        if (matched.Count < 2)
        {
            logger?.LogWarning("  [{Session}] fewer than 2 registered subs -- skipped", session.Id);
            return null;
        }

        // 5. Union canvas: the bounding box covering every warped source footprint, plus the
        //    per-frame footprints and the all-frames intersection rect (stretch/sampling stats).
        var transforms = new List<Matrix3x2>(matched.Count);
        foreach (var (_, t) in matched)
        {
            transforms.Add(t);
        }
        var (canvasShift, _, _, canvasW, canvasH) = CanvasGeometry.ComputeUnionCanvas(transforms, refW, refH);
        var (footprints, statsRect) =
            CanvasGeometry.ComputeFootprintsAndStatsRect(transforms, canvasShift, refW, refH, canvasW, canvasH);
        logger?.LogInformation("  [{Session}] canvas {W}x{H}, stats-rect {Rect}",
            session.Id, canvasW, canvasH, statsRect);

        // 6. Warp pass: reload -> calibrate -> debayer -> warp onto the canvas -> scratch FITS.
        //    One sub in RAM at a time; the scratch FITS is the shared artifact the master
        //    integration and the tiler (#40) both read.
        var sessionScratch = Path.Combine(scratchDir, Sanitize(session.Id));
        if (Directory.Exists(sessionScratch))
        {
            Directory.Delete(sessionScratch, recursive: true);
        }
        Directory.CreateDirectory(sessionScratch);

        var subs = ImmutableArray.CreateBuilder<RegisteredSub>(matched.Count);
        for (var i = 0; i < matched.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (f, transform) = matched[i];
            var raw = await f.Frame.LoadFullAsync(cancellationToken);
            var calibrated = calibrator?.Apply(raw) ?? raw;
            var debayered = await calibrated.DebayerAsync(debayerAlgorithm, cancellationToken: cancellationToken);
            var shifted = transform * canvasShift;
            var warped = await debayered.WarpToReferenceGridAsync(shifted, canvasW, canvasH, cancellationToken);
            var warpedPath = Path.Combine(sessionScratch, $"warped_{i:D4}.fits");
            warped.WriteToFitsFile(warpedPath);
            subs.Add(new RegisteredSub(f.Frame, warpedPath, shifted, f.Metrics));
        }
        var subsList = subs.MoveToImmutable();

        // 7. Integrate the session master from the scratch warped subs. Reuses the stacker's
        //    rejector selection + streaming float16-staged integrator (bounded RAM regardless
        //    of sub count). The producer re-reads each warped FITS one at a time.
        var integrateScratch = Path.Combine(sessionScratch, "_integrate");
        var job = new IntegrationJob(
            WarpedFrames: WarpedProducer,
            ExpectedFrameCount: subsList.Length,
            Options: new IntegrationOptions(Rejector: StackingPipeline.BuildRejector(subsList.Length)),
            StagingDir: integrateScratch,
            StatsRect: statsRect,
            FrameFootprints: footprints,
            CanvasWidth: canvasW,
            CanvasHeight: canvasH);
        var result = await new Float16StagedStrategy().RunAsync(job, cancellationToken);
        logger?.LogInformation(
            "  [{Session}] master integrated ({Frames} frames, {Rej:P1} mean rejection)",
            session.Id, result.FrameCount, result.MeanRejectionRate);

        return new RegisteredSession(
            session, result.Master, subsList, canvasW, canvasH, statsRect,
            reference.Frame, survivors.Length, matched.Count, skipped);

        async IAsyncEnumerable<Image> WarpedProducer([EnumeratorCancellation] CancellationToken token)
        {
            foreach (var sub in subsList)
            {
                token.ThrowIfCancellationRequested();
                if (!Image.TryReadFitsFile(sub.WarpedPath, out var img))
                {
                    throw new IOException($"Failed to re-read warped scratch FITS: {sub.WarpedPath}");
                }
                yield return img;
            }
        }
    }

    /// <summary>Quad match across the tolerance ladder — tight first, loosen on failure.
    /// Verbatim from <c>StackingPipeline.TryMatchAsync</c>; <c>FindFitAsync</c> memoises the
    /// quad build per <paramref name="maxStars"/> key, so looser retries only re-run the match
    /// pass, not the (expensive) quad construction.</summary>
    private static async Task<(Matrix3x2? Solution, float QuadTolerance, float RmsResidualPx)> TryMatchAsync(
        SortedStarList light, SortedStarList reference, int maxStars)
    {
        foreach (var tol in QuadTolerances)
        {
            var (solution, rmsPx) = await light.FindOffsetAndRotationWithRmsAsync(
                reference, minimumCount: 6, quadTolerance: tol, maxStars: maxStars);
            if (solution is not null)
            {
                return (solution, tol, rmsPx);
            }
        }
        return (null, float.NaN, float.NaN);
    }

    /// <summary>Maps a portable session id (<c>relative/dir|CAMERA</c>) to a single
    /// filesystem-safe scratch folder name.</summary>
    private static string Sanitize(string id)
    {
        var buf = id.ToCharArray();
        for (var i = 0; i < buf.Length; i++)
        {
            if (buf[i] is '/' or '\\' or '|' or ':' or '*' or '?' or '"' or '<' or '>')
            {
                buf[i] = '_';
            }
        }
        return new string(buf);
    }
}
