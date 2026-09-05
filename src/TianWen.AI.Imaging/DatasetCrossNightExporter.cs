using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TianWen.AI.Imaging.Onnx;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.BackgroundExtraction;
using TianWen.Lib.Imaging.Dataset;
using TianWen.Lib.Imaging.Degradation;
using TianWen.Lib.Imaging.Stacking;

namespace TianWen.AI.Imaging
{
    /// <summary>
    /// Exports CROSS-NIGHT training pairs: the same object integrated on two different nights, brought
    /// onto one grid and one stretch so the trainer's half-master regime reads them as an N2N pair.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every pair the denoiser has trained on so far shares its noise between the two sides: a
    /// same-session N2N pair goes through one calibration and one registration, and a supervised
    /// injection pair's target is the very master its input was made from. The same target on two
    /// nights shares the SIGNAL and nothing else. Photon noise is independent by construction, and the
    /// fixed-pattern calibration residual lands on different sky pixels once the second night is
    /// registered onto the first, so the registration is what decorrelates it
    /// (docs/plans/denoiser-training.md, H8).
    /// </para>
    /// <para>
    /// The output is a P0-shaped dataset: <c>tiles-manifest.jsonl</c> plus <c>tiles/&lt;pair&gt;/*.f16</c>,
    /// read by the trainer's <c>--prepare</c> unchanged. Night A goes out as <c>halfmaster_a</c>, night B
    /// as <c>halfmaster_b</c>, and the <c>master</c> slot holds their mean, so the trainer's
    /// <c>--half-pairs</c> regime (which already swaps the two sides at random, so no a-to-b direction is
    /// learnable) trains on the cross-night pair with no code change, and every scorer that reads the
    /// half slots as (input, other side) scores it the same way. The manifest row's <c>SourceFile</c>
    /// carries the night's own session id, so provenance survives the slot naming. A sidecar
    /// <see cref="PairManifestFileName"/> holds one row per pair with everything that decides whether
    /// the pair is usable: the fitted transform, the level match, the PSF on both sides before and
    /// after, and the residual noise correlation between the two sides. Nothing trains until those are
    /// in the run log.
    /// </para>
    /// <para>
    /// The six rules the plan states, and where each lives here:
    /// <list type="number">
    /// <item><b>Match on PSF, or convolve the sharper night to the wider one.</b> N2N assumes identical
    /// signal, and two nights differ in seeing, so a naive pair teaches the model to blur toward the
    /// worse night. The FWHM is measured on each master's own stars; beyond
    /// <see cref="Options.MaxFwhmMismatch"/> the pair is refused unless <see cref="Options.PsfMatch"/>
    /// convolves the sharper night with a Gaussian of the quadrature difference.</item>
    /// <item><b>Flatten both nights and match levels.</b> The median gradient in this archive is 2.3
    /// background sigma (gradient report G1), larger than the noise being learned; an unflattened pair
    /// teaches the model to move backgrounds. Both masters go through
    /// <see cref="ClassicalBackgroundExtractor"/>, then night B is fitted to night A per channel as
    /// gain times A plus offset over the pixels both cover (robust, 3 sigma clipped), which is rule 5's
    /// transparency match at the same time.</item>
    /// <item><b>Register both nights to one grid with one resampler.</b> Registering the target alone
    /// puts the resampler's blur on the target side, and the model learns THAT. The grid is the
    /// midpoint of the fitted transform: night A is resampled by half the rotation and translation one
    /// way, night B by the other half, so both carry the same interpolation and neither side is the
    /// sharper one by construction. The fitted transform stays exact on the B side
    /// (<c>M * H^-1</c>), so the split costs registration nothing.</item>
    /// <item><b>One MTF for both sides</b> (the E1 rule): parameters are taken from the combined mean
    /// and applied to both nights through <see cref="Image.MtfStretchWith"/>, or the stretch difference
    /// is signal.</item>
    /// <item><b>Global gain per pair:</b> the level fit above. N2N is symmetric, so both directions are
    /// augmentation, which the trainer's random swap already provides.</item>
    /// <item><b>Exclusions:</b> a session whose id contains an <see cref="Options.Exclude"/> token stays
    /// out, and a one-channel master (mono) is refused as everywhere in this campaign.</item>
    /// </list>
    /// </para>
    /// <para>
    /// The retained masters carry an EXACT ZERO ring where no frame covered the canvas; it is masked to
    /// NaN before anything reads the pixels, as the gradient report does, or the flattener chases the
    /// edge and the star finder sees a cliff. Cells are sampled only where BOTH nights are finite.
    /// </para>
    /// </remarks>
    public static class DatasetCrossNightExporter
    {
        /// <summary>Sidecar beside the tile manifest: one <see cref="PairRow"/> per exported pair.</summary>
        public const string PairManifestFileName = "pairs.jsonl";

        /// <summary>The mean of both nights, in the master slot: the reference a scorer detects on.</summary>
        public const string FrameCombined = DatasetTileExporter.FrameMaster;

        /// <summary>Night A, in the trainer's first half slot.</summary>
        public const string FrameNightA = DatasetTileExporter.FrameHalfMasterA;

        /// <summary>Night B, in the trainer's second half slot.</summary>
        public const string FrameNightB = DatasetTileExporter.FrameHalfMasterB;

        /// <summary>A third night of the same object, on the pair's own grid. A MEASUREMENT frame: the
        /// trainer's cache prepare reads master / subs / the two half slots and nothing else, so this
        /// one is invisible to training by construction.</summary>
        public const string FrameNightC = "nightc";

        /// <summary>Separates the two session ids of an explicit <c>--pair</c> on the command line.
        /// A session id contains <c>|</c> and <c>/</c>, never two colons.</summary>
        public const string PairSeparator = "::";

        /// <summary>Tile edge, the trainer's fixed chunk.</summary>
        public const int TileSize = 256;

        /// <summary>Stars either master must yield for the pair to be registered at all.</summary>
        public const int MinStars = FrameRegistration.MinStarsForMatch;

        /// <summary>Star detection channel: green. Under a dual-band filter red carries the Ha
        /// nebulosity, which confuses a detector, while green carries OIII plus continuum.</summary>
        private const int StarChannel = 1;

        private const float StarSnrMin = 20f;
        private const int StarMax = 500;

        /// <summary>Two session ids of one bake, both with a retained master, and optionally a THIRD
        /// night of the same object.
        /// <para>The third night is a measurement and never training data. Two frames cannot separate a
        /// night's photon noise from its systematic: both are per-night, and a difference drops whatever
        /// the two share. Three can, because <c>cov(A - B, A - C)</c> keeps only the terms belonging to
        /// A, which is the statistic that says whether the shared-noise component H8 posits exists
        /// before an arm is spent on it.</para></summary>
        public sealed record PairSpec(string SessionA, string SessionB, string? SessionC = null);

        /// <param name="BakeRoot">A bake with <c>session-masters/</c> and <c>tiles-manifest.jsonl</c>.</param>
        /// <param name="OutDir">Where the pair cache goes; must not be the bake.</param>
        /// <param name="Pairs">Explicit pairs. Empty means discover them: every two sessions of the bake
        /// sharing camera, object and filter on different nights.</param>
        /// <param name="ObjectFilters">Discovery only: keep a group when its object name contains one
        /// of these (case-insensitive). Empty keeps every group.</param>
        /// <param name="Exclude">A session whose id contains any of these tokens stays out (rule 6).</param>
        /// <param name="MaxFwhmMismatch">Relative FWHM difference between the two masters above which the
        /// pair is refused, unless <paramref name="PsfMatch"/>. 0.03 is what the plan's pair table calls
        /// matched.</param>
        /// <param name="PsfMatch">Convolve the sharper night to the wider one instead of refusing.</param>
        /// <param name="CellsPerPair">Tiles per pair, sampled with the P0 exporter's structure bias.</param>
        /// <param name="Seed">Folded into the per-pair cell sampling seed.</param>
        /// <param name="Force">Re-export pairs the output manifest already lists.</param>
        /// <param name="QuadStars">Stars a side the quad matcher may use, or 0 to escalate from
        /// <see cref="FrameRegistration.DefaultQuadStars"/> through twice and four times it. A partial
        /// overlap does not fail registration, it STARVES it: the budget takes the brightest N of each
        /// night, and where two nights share half a field two lists of 100 can hold no common quad.</param>
        /// <param name="PsfTolerance">Relative FWHM difference the two EXPORTED sides must be inside.
        /// Rule 1 is a statement about the tiles the model sees, and the pre-registration blur matches the
        /// masters, which the warp then widens by different amounts on the two sides.</param>
        /// <param name="PsfIterations">Cap on the exported-side match passes; each measures both sides and
        /// convolves the sharper one by the quadrature difference. Zero keeps the master-side behaviour.</param>
        /// <param name="InjectDraws">Degraded draws of night A written into the trainer's sub slots, or 0
        /// for the plain pair export. The input distribution is then the supervised arm's own (a master
        /// plus shape-calibrated injected noise) and the only thing left differing between this arm and
        /// that one is the TARGET, which is the single axis H8 is about. It is also what puts the
        /// conditioning plane where deployment is: a night's master is a floor injection can raise and
        /// nothing can lower.</param>
        /// <param name="MinInjectedNoise">Bottom of the log-uniform injected level range, in multiples of
        /// THIS NIGHT's own measured background noise, not of one sub: the level is anchored on the pair's
        /// own difference (<see cref="PairRow.NoiseSigmaA"/>), which is a noise measurement rather than a
        /// MAD over a master reading its gradient as noise. A draw at k leaves the input at
        /// <c>sqrt(1 + k^2)</c> times the night's noise, so 0.5 is 1.12x.</param>
        /// <param name="MaxInjectedNoise">Top of the same range.</param>
        /// <param name="WarpResampleSigma">Extra smoothing per injected realisation, in pixels. The knob
        /// that calibrates the injected shape against a registered stack's; 0.5 is what the S-warped arms
        /// measured, and it is a per-bake measurement rather than a constant.</param>
        public sealed record Options(
            string BakeRoot,
            string OutDir,
            ImmutableArray<PairSpec> Pairs = default,
            ImmutableArray<string> ObjectFilters = default,
            ImmutableArray<string> Exclude = default,
            double MaxFwhmMismatch = 0.03,
            bool PsfMatch = false,
            int CellsPerPair = 300,
            int Seed = 1,
            bool Force = false,
            int QuadStars = 0,
            double PsfTolerance = 0.01,
            int PsfIterations = 4,
            int InjectDraws = 0,
            double MinInjectedNoise = 0.5,
            double MaxInjectedNoise = 3.0,
            double WarpResampleSigma = 0.0);

        /// <summary>
        /// One exported pair. Per-channel arrays are R, G, B. <paramref name="ResidualCorrelation"/> is the
        /// Pearson correlation of the two nights' high-passed residuals over the faintest half of the
        /// combined scene with star pixels clipped: the number H8 rides on, since a same-session pair
        /// reads well above zero there and an independent pair must not.
        /// <paramref name="FwhmA"/>/<paramref name="FwhmB"/> are measured on the masters as read (after
        /// any PSF convolution), <paramref name="FwhmAfterA"/>/<paramref name="FwhmAfterB"/> on the two
        /// sides as exported (registered and level matched), both in pixels.
        /// <para><paramref name="RegistrationBasis"/> names the quad budget that answered, so a pair that
        /// needed four times the default is a pair to look at twice; <paramref name="PsfPasses"/> counts
        /// the exported-side match passes; <paramref name="InjectedDraws"/> the degraded draws of night A
        /// written into the sub slots; <paramref name="SessionC"/> the measurement night, if any.</para>
        /// </summary>
        public sealed record PairRow(
            string PairId,
            string SessionA,
            string SessionB,
            string Camera,
            string Object,
            string Filter,
            double FwhmA,
            double FwhmB,
            string BlurredSide,
            double BlurFwhmPx,
            int StarsA,
            int StarsB,
            float QuadTolerance,
            double RegistrationRmsPx,
            double Scale,
            double RotationDeg,
            double TranslationX,
            double TranslationY,
            double[] Gain,
            double[] Offset,
            double OverlapFraction,
            double[] NoiseSigmaA,
            double[] NoiseSigmaB,
            double[] ResidualCorrelation,
            double FwhmAfterA,
            double FwhmAfterB,
            int Cells,
            long ElapsedMs,
            string RegistrationBasis = "",
            int PsfPasses = 0,
            int InjectedDraws = 0,
            string SessionC = "",
            double FwhmAfterC = 0);

        /// <summary>What one run did. <paramref name="Skipped"/> carries a reason per refused pair.</summary>
        public sealed record RunResult(
            ImmutableArray<PairRow> Pairs,
            ImmutableArray<string> Skipped,
            int Failed,
            string TileManifestPath,
            string PairManifestPath);

        /// <summary>Runs the export. Resumable: a pair already in the output manifest is skipped unless
        /// <see cref="Options.Force"/>.</summary>
        public static async Task<RunResult> RunAsync(Options options, ILogger? logger = null, CancellationToken cancellationToken = default)
        {
            if (string.Equals(Path.GetFullPath(options.BakeRoot), Path.GetFullPath(options.OutDir), StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("--out must not be the bake itself: both carry a tiles-manifest.jsonl", nameof(options));
            }
            var bakeManifest = Path.Combine(options.BakeRoot, DatasetTileExporter.ManifestFileName);
            if (!File.Exists(bakeManifest))
            {
                throw new FileNotFoundException($"{bakeManifest} not found; --bake must point at a dataset bake", bakeManifest);
            }
            if (options.CellsPerPair <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "CellsPerPair must be positive");
            }

            Directory.CreateDirectory(options.OutDir);
            var outTileManifest = Path.Combine(options.OutDir, DatasetTileExporter.ManifestFileName);
            var outPairManifest = Path.Combine(options.OutDir, PairManifestFileName);

            var headers = await ReadSessionHeadersAsync(bakeManifest, cancellationToken);
            var pairs = options.Pairs.IsDefaultOrEmpty
                ? DiscoverPairs(headers.Keys, options)
                : options.Pairs;

            var done = new HashSet<string>(StringComparer.Ordinal);
            if (!options.Force && File.Exists(outTileManifest))
            {
                foreach (var cp in await DatasetTileExporter.ReadManifestCheckpointsAsync(outTileManifest, cancellationToken))
                {
                    done.Add(cp.Value.SessionId);
                }
            }

            var rows = ImmutableArray.CreateBuilder<PairRow>();
            var skipped = ImmutableArray.CreateBuilder<string>();
            var failed = 0;
            foreach (var pair in pairs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pairId = PairIdFor(pair);
                if (done.Contains(pairId))
                {
                    logger?.LogInformation("[{Pair}] already exported, skipped", pairId);
                    continue;
                }
                if (!headers.TryGetValue(pair.SessionA, out var header))
                {
                    header = headers.TryGetValue(pair.SessionB, out var hb) ? hb : new SessionHeader("", 0, 0);
                }
                try
                {
                    var outcome = await ExportPairAsync(options, pair, pairId, header, outTileManifest, outPairManifest, logger, cancellationToken);
                    if (outcome.Row is { } row)
                    {
                        rows.Add(row);
                    }
                    else
                    {
                        skipped.Add($"{pairId}: {outcome.Reason}");
                        logger?.LogWarning("[{Pair}] skipped: {Reason}", pairId, outcome.Reason);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    logger?.LogError(ex, "[{Pair}] failed", pairId);
                }
            }
            return new RunResult(rows.ToImmutable(), skipped.ToImmutable(), failed, outTileManifest, outPairManifest);
        }

        /// <summary>
        /// The pair's id in the session-id shape (<c>folder|camera|object|filter</c>) so the tools that
        /// parse a session id parse a pair id too; the folder's last segment is the two night labels
        /// joined with <c>+</c>. The two full session ids ride on the <see cref="PairRow"/>.
        /// </summary>
        public static string PairIdFor(PairSpec pair)
        {
            var a = SplitSessionId(pair.SessionA);
            var b = SplitSessionId(pair.SessionB);
            var folderA = a.Folder.Replace('\\', '/');
            var cut = folderA.LastIndexOf('/');
            var prefix = cut >= 0 ? folderA[..(cut + 1)] : "";
            var night = $"{NightLabel(pair.SessionA)}+{NightLabel(pair.SessionB)}";
            return $"{prefix}{night}|{a.Camera}|{a.Object}|{a.Filter}";
        }

        /// <summary>The last path segment of a session id's folder, which the bakes name by date.</summary>
        public static string NightLabel(string sessionId)
        {
            var folder = SplitSessionId(sessionId).Folder.Replace('\\', '/').TrimEnd('/');
            var cut = folder.LastIndexOf('/');
            return cut >= 0 ? folder[(cut + 1)..] : folder;
        }

        /// <summary>A session id is <c>folder|camera|object|filter</c>; older ids stop after the object.</summary>
        public static (string Folder, string Camera, string Object, string Filter) SplitSessionId(string sessionId)
        {
            var parts = sessionId.Split('|');
            var folder = parts[0];
            var camera = parts.Length > 1 ? parts[1].Trim() : "";
            var obj = parts.Length > 2 ? parts[2].Trim() : folder;
            var filter = parts.Length > 3 ? parts[3].Trim() : "";
            return (folder, camera, obj, filter);
        }

        /// <summary>
        /// Every two sessions sharing camera, object and filter on different night labels, oldest first
        /// within a pair, ordered by id. Rule 6's exclusion tokens apply here; a session's master
        /// must exist in the bake.
        /// </summary>
        internal static ImmutableArray<PairSpec> DiscoverPairs(IEnumerable<string> sessionIds, Options options)
        {
            var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in sessionIds.OrderBy(static s => s, StringComparer.Ordinal))
            {
                if (IsExcluded(id, options.Exclude) || !RetainedMasterStore.Exists(options.BakeRoot, id))
                {
                    continue;
                }
                var (_, camera, obj, filter) = SplitSessionId(id);
                if (!options.ObjectFilters.IsDefaultOrEmpty
                    && !options.ObjectFilters.Any(f => obj.Contains(f, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                var key = $"{camera}|{obj}|{filter}";
                if (!groups.TryGetValue(key, out var list))
                {
                    groups[key] = list = new List<string>();
                }
                list.Add(id);
            }
            var pairs = ImmutableArray.CreateBuilder<PairSpec>();
            foreach (var list in groups.Values.Where(static l => l.Count >= 2))
            {
                for (var i = 0; i < list.Count; i++)
                {
                    for (var j = i + 1; j < list.Count; j++)
                    {
                        // Two sessions of one night share calibration and sky, which is the very thing a
                        // cross-night pair exists to escape.
                        if (string.Equals(NightLabel(list[i]), NightLabel(list[j]), StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        pairs.Add(new PairSpec(list[i], list[j]));
                    }
                }
            }
            return pairs.ToImmutable();
        }

        private static bool IsExcluded(string sessionId, ImmutableArray<string> tokens) =>
            !tokens.IsDefaultOrEmpty
            && tokens.Any(t => t.Length > 0 && sessionId.Contains(t, StringComparison.OrdinalIgnoreCase));

        private readonly record struct SessionHeader(string Camera, int Gain, double ExposureSeconds);

        private readonly record struct Outcome(PairRow? Row, string Reason)
        {
            public static Outcome Skip(string reason) => new Outcome(null, reason);
        }

        /// <summary>Camera, gain and exposure per session from the bake's manifest, one streaming pass.</summary>
        private static async Task<Dictionary<string, SessionHeader>> ReadSessionHeadersAsync(string manifestPath, CancellationToken cancellationToken)
        {
            var result = new Dictionary<string, SessionHeader>(StringComparer.Ordinal);
            using var reader = new StreamReader(manifestPath);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }
                DatasetTileExporter.TileManifestRow? row;
                try
                {
                    row = JsonSerializer.Deserialize(line, DatasetDegradationJsonContext.Default.TileManifestRow);
                }
                catch (JsonException)
                {
                    continue; // a torn tail from a killed bake, healed on its next append
                }
                if (row is null || result.ContainsKey(row.SessionId))
                {
                    continue;
                }
                result[row.SessionId] = new SessionHeader(row.Camera, row.Gain, row.ExposureSeconds);
            }
            return result;
        }

        private static async Task<Outcome> ExportPairAsync(
            Options options, PairSpec pair, string pairId, SessionHeader header,
            string outTileManifest, string outPairManifest, ILogger? logger, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            if (!RetainedMasterStore.TryRead(options.BakeRoot, pair.SessionA, out var rawA, logger))
            {
                return Outcome.Skip($"no retained master for {pair.SessionA}");
            }
            if (!RetainedMasterStore.TryRead(options.BakeRoot, pair.SessionB, out var rawB, logger))
            {
                return Outcome.Skip($"no retained master for {pair.SessionB}");
            }
            if (rawA.ChannelCount != 3 || rawB.ChannelCount != 3)
            {
                return Outcome.Skip($"{rawA.ChannelCount}/{rawB.ChannelCount} channels; mono stays out");
            }

            var a = MaskAbsent(rawA);
            var b = MaskAbsent(rawB);

            // Stars once, on the masked linear masters: the flattener does not move them and neither
            // does a blur, so the same lists serve the PSF decision and the registration.
            var starsA = await a.FindStarsAsync(StarChannel, StarSnrMin, StarMax, MinStars, cancellationToken: ct);
            var starsB = await b.FindStarsAsync(StarChannel, StarSnrMin, StarMax, MinStars, cancellationToken: ct);
            if (starsA.Count < MinStars || starsB.Count < MinStars)
            {
                return Outcome.Skip($"too few stars ({starsA.Count} / {starsB.Count}, need {MinStars})");
            }
            var fwhmA = MedianFwhm(starsA);
            var fwhmB = MedianFwhm(starsB);
            var wide = Math.Max(fwhmA, fwhmB);
            var mismatch = Math.Abs(fwhmA - fwhmB) / wide;
            var blurredSide = "";
            var blurFwhm = 0.0;
            if (mismatch > options.MaxFwhmMismatch)
            {
                if (!options.PsfMatch)
                {
                    return Outcome.Skip(string.Create(CultureInfo.InvariantCulture,
                        $"FWHM {fwhmA:F2} against {fwhmB:F2} px differs by {mismatch:P0}, over {options.MaxFwhmMismatch:P0}; --psf-match convolves the sharper night"));
                }
                var sharp = Math.Min(fwhmA, fwhmB);
                // Gaussian widths add in quadrature, so this kernel takes the sharper night to the wider
                // one's width. The two nights' profiles are not exactly Gaussian, which is why the
                // exported FWHM on both sides is measured again and recorded.
                blurFwhm = Math.Sqrt((wide * wide) - (sharp * sharp));
                var sigma = (float)(blurFwhm / 2.354820045);
                if (fwhmA < fwhmB)
                {
                    a = a.GaussianBlur(sigma);
                    blurredSide = "A";
                }
                else
                {
                    b = b.GaussianBlur(sigma);
                    blurredSide = "B";
                }
            }

            // Rule 2: both nights flat, each keeping its own level (the flattener preserves it per
            // channel); the level match below is what brings B onto A.
            var extractor = new ClassicalBackgroundExtractor();
            var flatA = await extractor.ExtractAsync(a, BackgroundExtractionOptions.Default, ct);
            flatA.Background.Release();
            var flatB = await extractor.ExtractAsync(b, BackgroundExtractionOptions.Default, ct);
            flatB.Background.Release();

            // Rule 3: the transform B -> A, then its midpoint grid.
            var (m, quadTolerance, rmsPx, registrationBasis) = await MatchAsync(options, starsA, starsB);
            if (registrationBasis.Length == 0)
            {
                return Outcome.Skip(string.Create(CultureInfo.InvariantCulture,
                    $"no quad fit between the two nights at up to {QuadBudgets(options, starsA.Count, starsB.Count)[^1]} stars a side"));
            }
            var (half, scale, rotationDeg) = HalfTransform(m);
            if (!Matrix3x2.Invert(half, out var halfInverse))
            {
                return Outcome.Skip("the fitted transform is not invertible");
            }
            var width = a.Width;
            var height = a.Height;
            // An identity half (a 180 degree pair, or a test's aligned nights) hands back the flattened
            // image itself, so the flattened images are released with the warps, at the end, never here.
            var warpedA = await flatA.Cleaned.WarpToReferenceGridAsync(halfInverse, width, height, ct);
            var warpedB = await flatB.Cleaned.WarpToReferenceGridAsync(m * halfInverse, width, height, ct);

            // Rules 2 and 5: B = gain * A + offset over what both cover, then B is mapped onto A.
            var channels = 3;
            var n = width * height;
            var gain = new double[channels];
            var offset = new double[channels];
            var planesA = new float[channels][,];
            var planesB = new float[channels][,];
            var planesM = new float[channels][,];
            long covered = 0;
            for (var c = 0; c < channels; c++)
            {
                var sa = warpedA.GetChannelSpan(c);
                var sb = warpedB.GetChannelSpan(c);
                (gain[c], offset[c]) = FitLevel(sa, sb);
                var pa = new float[height, width];
                var pb = new float[height, width];
                var pm = new float[height, width];
                var g = (float)gain[c];
                var o = (float)offset[c];
                for (var i = 0; i < n; i++)
                {
                    var va = sa[i];
                    var vb = sb[i];
                    var y = i / width;
                    var x = i - (y * width);
                    if (float.IsFinite(va) && float.IsFinite(vb))
                    {
                        var matched = (vb - o) / g;
                        pa[y, x] = va;
                        pb[y, x] = matched;
                        pm[y, x] = 0.5f * (va + matched);
                        if (c == 0)
                        {
                            covered++;
                        }
                    }
                    else
                    {
                        pa[y, x] = float.NaN;
                        pb[y, x] = float.NaN;
                        pm[y, x] = float.NaN;
                    }
                }
                planesA[c] = pa;
                planesB[c] = pb;
                planesM[c] = pm;
            }
            var overlap = (double)covered / n;
            if (covered < (long)TileSize * TileSize)
            {
                return Outcome.Skip(string.Create(CultureInfo.InvariantCulture, $"the two nights overlap on {overlap:P1} of the canvas, under one tile"));
            }

            // Rule 1, on the frames the model will actually see. The convolution above matched the two
            // MASTERS; the warp then widened each side by a different amount (the midpoint grid splits
            // the resampling, it does not equalise it), and what rule 1 is about is the exported tiles.
            // So the match is iterated here: measure both exported sides, convolve the sharper by the
            // quadrature difference, measure again. The first arm's pairs shipped 3 to 6 percent apart.
            var psfPasses = 0;
            var fwhmAfterA = await MedianFwhmOfAsync(planesA, warpedA, ct);
            var fwhmAfterB = await MedianFwhmOfAsync(planesB, warpedA, ct);
            for (var pass = 0; pass < options.PsfIterations; pass++)
            {
                if (!double.IsFinite(fwhmAfterA) || !double.IsFinite(fwhmAfterB))
                {
                    break;
                }
                var wideAfter = Math.Max(fwhmAfterA, fwhmAfterB);
                var sharpAfter = Math.Min(fwhmAfterA, fwhmAfterB);
                if (wideAfter <= 0 || (wideAfter - sharpAfter) / wideAfter <= options.PsfTolerance)
                {
                    break;
                }
                var sigma = Math.Sqrt((wideAfter * wideAfter) - (sharpAfter * sharpAfter)) / 2.354820045;
                var sharperIsA = fwhmAfterA < fwhmAfterB;
                var sharperSide = sharperIsA ? planesA : planesB;
                for (var c = 0; c < channels; c++)
                {
                    BlurPlaneInPlace(sharperSide[c], sigma);
                }
                RecomputeMean(planesA, planesB, planesM);
                psfPasses++;
                var side = sharperIsA ? "A" : "B";
                blurredSide = blurredSide.Length == 0 || blurredSide == side ? side : "AB";
                fwhmAfterA = await MedianFwhmOfAsync(planesA, warpedA, ct);
                fwhmAfterB = await MedianFwhmOfAsync(planesB, warpedA, ct);
            }

            // The measurement night, on the pair's own grid and in A's units like the other two. Its
            // footprint narrows the MEAN, which is what the cells are sampled from, so every cell of a
            // triple export carries all three nights rather than a NaN third of the time.
            float[][,]? planesC = null;
            var fwhmAfterC = 0.0;
            if (!string.IsNullOrWhiteSpace(pair.SessionC))
            {
                var third = await WarpThirdNightAsync(
                    options, pair.SessionC!, starsA, warpedA, halfInverse, Math.Max(fwhmAfterA, fwhmAfterB), logger, ct);
                if (third.Planes is null)
                {
                    return Outcome.Skip(third.Skip);
                }
                planesC = third.Planes;
                fwhmAfterC = third.Fwhm;
                MaskWhereAbsent(planesM, planesC[0]);
            }

            // Everything below is in A's linear units. One divisor and one pedestal for the three
            // frames, the pair's own equivalent of the P0 exporter's ToUnitRange on a single master.
            var divisor = Math.Max(1f, Math.Max(warpedA.MaxValue, FiniteMax(planesA, planesB)));
            var inv = 1f / divisor;
            ScaleInPlace(planesA, inv);
            ScaleInPlace(planesB, inv);
            ScaleInPlace(planesM, inv);
            if (planesC is not null)
            {
                ScaleInPlace(planesC, inv);
            }
            var pedestal = warpedA.Pedestal * inv;
            var meta = rawA.ImageMeta;
            var unitA = new Image(planesA, BitDepth.Float32, 1f, 0f, pedestal, meta);
            var unitB = new Image(planesB, BitDepth.Float32, 1f, 0f, pedestal, meta);
            var unitM = new Image(planesM, BitDepth.Float32, 1f, 0f, pedestal, meta);

            // Rule 4: the mean sets the MTF, both nights take it.
            var (stretchedM, applied, origMin, balances) = ChunkedNafnetRunner.ApplyInputStretch(unitM);
            if (!applied || origMin is null || balances is null)
            {
                return Outcome.Skip("the combined frame did not read as linear, so no stretch parameters exist");
            }
            var stretchedA = unitA.MtfStretchWith(origMin, balances);
            var stretchedB = unitB.MtfStretchWith(origMin, balances);
            var stretchedC = planesC is null
                ? null
                : new Image(planesC, BitDepth.Float32, 1f, 0f, pedestal, meta).MtfStretchWith(origMin, balances);

            // Cells where both nights are finite, sampled with the P0 structure bias on the mean.
            var candidates = FiniteTileOrigins(planesM[0], width, height);
            if (candidates.Count == 0)
            {
                return Outcome.Skip("no tile lies entirely inside the common footprint");
            }
            var rng = new Random(DatasetTileExporter.StableSeed(pairId) ^ options.Seed);
            var cells = DatasetTileExporter.SampleCells(candidates, stretchedM, TileSize, options.CellsPerPair, rng);

            // The pair statistics the plan asks for before any training.
            var (noiseA, noiseB, correlation) = ResidualStatistics(planesA, planesB, stretchedM.GetChannelSpan(0), width, height);

            var slug = DatasetTileExporter.Sanitize(pairId);
            var tilesDir = Path.Combine(options.OutDir, "tiles", slug);
            Directory.CreateDirectory(tilesDir);
            var rows = ImmutableArray.CreateBuilder<DatasetTileExporter.TileManifestRow>(cells.Count * 3);
            var frames = new List<(string Frame, Image Image, string Source)>
            {
                (FrameCombined, stretchedM, ""),
                (FrameNightA, stretchedA, pair.SessionA),
                (FrameNightB, stretchedB, pair.SessionB),
            };
            if (stretchedC is not null)
            {
                frames.Add((FrameNightC, stretchedC, pair.SessionC!));
            }
            foreach (var (frame, image, source) in frames)
            {
                foreach (var cell in cells)
                {
                    ct.ThrowIfCancellationRequested();
                    var file = $"x{cell.X}_y{cell.Y}_{frame}{DatasetTileExporter.TileExtension}";
                    var mad = DatasetTileExporter.WriteTile(image, cell, TileSize, Path.Combine(tilesDir, file), pairId);
                    rows.Add(new DatasetTileExporter.TileManifestRow(
                        Tile: $"tiles/{slug}/{file}",
                        SessionId: pairId,
                        Camera: header.Camera,
                        Frame: frame,
                        SourceFile: source,
                        CellX: cell.X,
                        CellY: cell.Y,
                        TileSize: TileSize,
                        Channels: channels,
                        Gain: header.Gain,
                        ExposureSeconds: header.ExposureSeconds,
                        NoiseMad: mad));
                }
            }

            // The sub slots: night A degraded, drawn per cell. Injection happens in LINEAR units and the
            // draw is stretched with the PAIR's own parameters, never its own, so the difference between
            // an input and its target is noise and never a stretch (the degradation exporter's rule 1).
            var injected = 0;
            if (options.InjectDraws > 0)
            {
                var anchor = noiseA[0];
                if (!(anchor > 0) || !double.IsFinite(anchor))
                {
                    return Outcome.Skip(string.Create(CultureInfo.InvariantCulture,
                        $"night A's background noise measured {anchor}, so no injected level can be anchored"));
                }
                foreach (var cell in cells)
                {
                    for (var draw = 0; draw < options.InjectDraws; draw++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var drawRng = new Random(DatasetTileExporter.StableSeed(pairId) ^ (options.Seed * 397) ^ (cell.X * 73856093) ^ (cell.Y * 19349663) ^ (draw * 83492791));
                        var level = LogUniform(drawRng, options.MinInjectedNoise, options.MaxInjectedNoise);
                        var planes = new float[channels][,];
                        for (var c = 0; c < channels; c++)
                        {
                            var region = CutCell(planesA[c], cell, TileSize);
                            var background = MedianFinite(region);
                            // One calibration per cell from night A's own measured noise, so a draw at
                            // level k adds k times that: the ramp with signal is SigmaAt's, the anchor is
                            // a measurement of this night rather than a MAD reading its gradient as noise.
                            var calibration = new LinearDegradation.NoiseCalibration(pedestal, background, anchor, 1);
                            var shape = NoiseField.Warped(TileSize, TileSize, InjectionRealisations, drawRng, options.WarpResampleSigma);
                            LinearDegradation.AddNoiseInPlace(region, shape, calibration, level);
                            var plane = new float[TileSize, TileSize];
                            for (var y = 0; y < TileSize; y++)
                            {
                                for (var x = 0; x < TileSize; x++)
                                {
                                    plane[y, x] = region[(y * TileSize) + x];
                                }
                            }
                            planes[c] = plane;
                        }
                        var cellImage = new Image(planes, BitDepth.Float32, 1f, 0f, pedestal, meta);
                        var stretchedCell = cellImage.MtfStretchWith(origMin, balances);
                        var frame = DatasetDegradationExporter.FrameForDraw(draw);
                        var file = $"x{cell.X}_y{cell.Y}_{frame}{DatasetTileExporter.TileExtension}";
                        var mad = DatasetTileExporter.WriteTile(stretchedCell, Point.Empty, TileSize, Path.Combine(tilesDir, file), pairId);
                        stretchedCell.Release();
                        rows.Add(new DatasetTileExporter.TileManifestRow(
                            Tile: $"tiles/{slug}/{file}",
                            SessionId: pairId,
                            Camera: header.Camera,
                            Frame: frame,
                            SourceFile: pair.SessionA,
                            CellX: cell.X,
                            CellY: cell.Y,
                            TileSize: TileSize,
                            Channels: channels,
                            Gain: header.Gain,
                            ExposureSeconds: header.ExposureSeconds,
                            NoiseMad: mad));
                        injected++;
                    }
                }
            }

            var (_, camera, obj, filter) = SplitSessionId(pair.SessionA);
            var row = new PairRow(
                PairId: pairId,
                SessionA: pair.SessionA,
                SessionB: pair.SessionB,
                Camera: camera,
                Object: obj,
                Filter: filter,
                FwhmA: fwhmA,
                FwhmB: fwhmB,
                BlurredSide: blurredSide,
                BlurFwhmPx: blurFwhm,
                StarsA: starsA.Count,
                StarsB: starsB.Count,
                QuadTolerance: quadTolerance,
                RegistrationRmsPx: rmsPx,
                Scale: scale,
                RotationDeg: rotationDeg,
                TranslationX: m.M31,
                TranslationY: m.M32,
                Gain: gain,
                Offset: offset,
                OverlapFraction: overlap,
                NoiseSigmaA: noiseA,
                NoiseSigmaB: noiseB,
                ResidualCorrelation: correlation,
                FwhmAfterA: fwhmAfterA,
                FwhmAfterB: fwhmAfterB,
                Cells: cells.Count,
                ElapsedMs: sw.ElapsedMilliseconds,
                RegistrationBasis: registrationBasis,
                PsfPasses: psfPasses,
                InjectedDraws: injected,
                SessionC: pair.SessionC ?? "",
                FwhmAfterC: fwhmAfterC);

            // Tiles first, then the two manifests, so a killed run leaves at worst orphan tiles that the
            // next run overwrites, never a manifest row without its tile.
            await DatasetTileExporter.AppendManifestAsync(outTileManifest, rows.ToImmutable(), ct);
            await JsonLinesFile.AppendRecordAsync(outPairManifest, row, DatasetCrossNightJsonContext.Default.PairRow, ct);

            // Ownership spent last, and a second Release on an instance the identity warp handed back
            // unchanged is a no-op, so no instance identity is consulted here.
            warpedA.Release();
            warpedB.Release();
            flatA.Cleaned.Release();
            flatB.Cleaned.Release();
            rawA.Release();
            rawB.Release();
            stretchedC?.Release();
            logger?.LogInformation(
                "[{Pair}] {Cells} cells; FWHM {FwhmA:F2}/{FwhmB:F2} px, rotation {Rot:F3} deg, rms {Rms:F2} px, gain {Gain}, residual correlation {Corr}, {Ms} ms",
                pairId, cells.Count, fwhmA, fwhmB, rotationDeg, rmsPx,
                string.Join('/', gain.Select(static g => g.ToString("G4", CultureInfo.InvariantCulture))),
                string.Join('/', correlation.Select(static r => r.ToString("F3", CultureInfo.InvariantCulture))),
                sw.ElapsedMilliseconds);
            return new Outcome(row, "");
        }

        /// <summary>Exact zeros and non-finite pixels become NaN: the retained master's uncovered ring.</summary>
        internal static Image MaskAbsent(Image source)
        {
            var (channels, width, height) = source.Shape;
            var planes = new float[channels][,];
            for (var c = 0; c < channels; c++)
            {
                var src = source.GetChannelSpan(c);
                var plane = new float[height, width];
                for (var y = 0; y < height; y++)
                {
                    var row = y * width;
                    for (var x = 0; x < width; x++)
                    {
                        var v = src[row + x];
                        plane[y, x] = v == 0f || !float.IsFinite(v) ? float.NaN : v;
                    }
                }
                planes[c] = plane;
            }
            return new Image(planes, source.BitDepth, source.MaxValue, source.MinValue, source.Pedestal, source.ImageMeta);
        }

        /// <summary>
        /// The similarity whose square is <paramref name="m"/>: half the rotation, the square root of the
        /// scale, and the translation that makes two applications land on <paramref name="m"/>'s. The
        /// affine fit may carry a trace of shear, which is why the B side uses <c>m * half^-1</c> exactly
        /// rather than <c>half</c>: the split of the resampling is approximate, the registration is not.
        /// </summary>
        internal static (Matrix3x2 Half, double Scale, double RotationDeg) HalfTransform(Matrix3x2 m)
        {
            var det = (m.M11 * (double)m.M22) - (m.M12 * (double)m.M21);
            var scale = Math.Sqrt(Math.Abs(det));
            var rotation = Math.Atan2(m.M12, m.M11);
            var halfScale = Math.Sqrt(scale);
            var halfRotation = rotation / 2.0;
            var cos = (float)(halfScale * Math.Cos(halfRotation));
            var sin = (float)(halfScale * Math.Sin(halfRotation));
            // Row-vector convention: v' = v * L + t, so L = [[cos, sin], [-sin, cos]] rotates by +angle.
            var linear = new Matrix3x2(cos, sin, -sin, cos, 0f, 0f);
            // h * (L + I) = t, solved for the row vector h.
            var k = new Matrix3x2(linear.M11 + 1f, linear.M12, linear.M21, linear.M22 + 1f, 0f, 0f);
            if (!Matrix3x2.Invert(k, out var kInverse))
            {
                // A 180 degree turn has no real square root in this form; fall back to A unresampled.
                return (Matrix3x2.Identity, scale, rotation * 180.0 / Math.PI);
            }
            var h = Vector2.Transform(new Vector2(m.M31, m.M32), kInverse);
            return (new Matrix3x2(linear.M11, linear.M12, linear.M21, linear.M22, h.X, h.Y), scale, rotation * 180.0 / Math.PI);
        }

        /// <summary>Sub-realisations averaged into one injected noise field. A registered stack's noise
        /// is correlated because many resampled frames were averaged; the count past a dozen or so stops
        /// changing the shape, so it is fixed here rather than read off a header the retained master does
        /// not carry.</summary>
        private const int InjectionRealisations = 8;

        /// <summary>
        /// The third night on the pair's grid and in night A's units: masked, star-matched against A,
        /// flattened, warped onto the midpoint grid and level-matched, exactly as B was.
        /// </summary>
        /// <returns>Null planes with a reason when the night cannot be placed on the grid.</returns>
        private static async Task<(float[][,]? Planes, double Fwhm, string Skip)> WarpThirdNightAsync(
            Options options, string sessionC, StarList starsA, Image warpedA, Matrix3x2 halfInverse,
            double targetFwhm, ILogger? logger, CancellationToken ct)
        {
            if (!RetainedMasterStore.TryRead(options.BakeRoot, sessionC, out var rawC, logger))
            {
                return (null, 0, $"no retained master for the third night {sessionC}");
            }
            try
            {
                if (rawC.ChannelCount != 3)
                {
                    return (null, 0, $"the third night has {rawC.ChannelCount} channels; mono stays out");
                }
                var c0 = MaskAbsent(rawC);
                var starsC = await c0.FindStarsAsync(StarChannel, StarSnrMin, StarMax, MinStars, cancellationToken: ct);
                if (starsC.Count < MinStars)
                {
                    return (null, 0, $"the third night yielded {starsC.Count} stars, needs {MinStars}");
                }
                var (mC, _, _, basis) = await MatchAsync(options, starsA, starsC);
                if (basis.Length == 0)
                {
                    return (null, 0, "no quad fit between the third night and night A");
                }
                var extractor = new ClassicalBackgroundExtractor();
                var flatC = await extractor.ExtractAsync(c0, BackgroundExtractionOptions.Default, ct);
                flatC.Background.Release();
                var (_, width, height) = warpedA.Shape;
                var warpedC = await flatC.Cleaned.WarpToReferenceGridAsync(mC * halfInverse, width, height, ct);
                var channels = 3;
                var planes = new float[channels][,];
                for (var c = 0; c < channels; c++)
                {
                    var sa = warpedA.GetChannelSpan(c);
                    var sc = warpedC.GetChannelSpan(c);
                    var (gain, offset) = FitLevel(sa, sc);
                    var plane = new float[height, width];
                    var g = (float)gain;
                    var o = (float)offset;
                    for (var i = 0; i < sa.Length; i++)
                    {
                        var y = i / width;
                        var x = i - (y * width);
                        var va = sa[i];
                        var vc = sc[i];
                        plane[y, x] = float.IsFinite(va) && float.IsFinite(vc) ? (vc - o) / g : float.NaN;
                    }
                    planes[c] = plane;
                }
                // Brought to the PAIR's width, one-sidedly: a sharper third night is convolved up to it,
                // a wider one is left alone and recorded. Widening A or B to suit the measurement frame
                // would change the arm's own training data to make a statistic look better, and the
                // statistic is there to be believed rather than flattered. The residual is reported.
                var fwhm = await MedianFwhmOfAsync(planes, warpedA, ct);
                for (var pass = 0; pass < options.PsfIterations; pass++)
                {
                    if (!double.IsFinite(fwhm) || !double.IsFinite(targetFwhm) || targetFwhm <= 0)
                    {
                        break;
                    }
                    if (fwhm >= targetFwhm || (targetFwhm - fwhm) / targetFwhm <= options.PsfTolerance)
                    {
                        break;
                    }
                    var sigma = Math.Sqrt((targetFwhm * targetFwhm) - (fwhm * fwhm)) / 2.354820045;
                    for (var c = 0; c < channels; c++)
                    {
                        BlurPlaneInPlace(planes[c], sigma);
                    }
                    fwhm = await MedianFwhmOfAsync(planes, warpedA, ct);
                }
                warpedC.Release();
                flatC.Cleaned.Release();
                return (planes, fwhm, "");
            }
            finally
            {
                rawC.Release();
            }
        }

        /// <summary>NaN wherever the reference plane is not finite: the mean carries the footprint every
        /// frame of the export shares, so a cell sampled from it holds all of them.</summary>
        internal static void MaskWhereAbsent(float[][,] planes, float[,] reference)
        {
            var height = reference.GetLength(0);
            var width = reference.GetLength(1);
            for (var c = 0; c < planes.Length; c++)
            {
                var plane = planes[c];
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        if (!float.IsFinite(reference[y, x]))
                        {
                            plane[y, x] = float.NaN;
                        }
                    }
                }
            }
        }

        /// <summary>One cell of a plane, row-major, as the noise injection wants it.</summary>
        internal static float[] CutCell(float[,] plane, Point origin, int size)
        {
            var region = new float[size * size];
            var height = plane.GetLength(0);
            var width = plane.GetLength(1);
            for (var y = 0; y < size; y++)
            {
                var sy = Math.Clamp(origin.Y + y, 0, height - 1);
                for (var x = 0; x < size; x++)
                {
                    var sx = Math.Clamp(origin.X + x, 0, width - 1);
                    region[(y * size) + x] = plane[sy, sx];
                }
            }
            return region;
        }

        /// <summary>Median over the finite samples, NaN when there are none.</summary>
        internal static double MedianFinite(ReadOnlySpan<float> values)
        {
            var finite = new List<float>(values.Length);
            foreach (var v in values)
            {
                if (float.IsFinite(v))
                {
                    finite.Add(v);
                }
            }
            return finite.Count == 0 ? double.NaN : Median([.. finite]);
        }

        /// <summary>A draw log-uniform in [min, max], so the level range is covered evenly in ratio
        /// rather than in difference.</summary>
        internal static double LogUniform(Random rng, double min, double max)
        {
            if (!(max > min))
            {
                return min;
            }
            var logMin = Math.Log(min);
            return Math.Exp(logMin + (rng.NextDouble() * (Math.Log(max) - logMin)));
        }

        /// <summary>
        /// Separable Gaussian over a plane whose uncovered pixels are NaN, renormalising the kernel over
        /// the finite samples it actually reached.
        /// <para>A plain convolution would spread the uncovered ring inward by the kernel radius on every
        /// pass, and the iteration below runs several. NaN stays NaN; a covered pixel is a weighted mean
        /// of the covered pixels near it, which is what the frame's own edge does anyway.</para>
        /// </summary>
        internal static void BlurPlaneInPlace(float[,] plane, double sigma)
        {
            if (!(sigma > 0.0))
            {
                return;
            }
            var height = plane.GetLength(0);
            var width = plane.GetLength(1);
            var radius = Math.Max(1, (int)Math.Ceiling(3.0 * sigma));
            var kernel = new double[(2 * radius) + 1];
            var twoSigmaSquared = 2.0 * sigma * sigma;
            // AREA sampled, not point sampled. A PSF match asks for kernels well under a pixel wide, and
            // a point-sampled Gaussian of sigma 0.27 px is [0.001, 1, 0.001]: its second moment is 0.002
            // px^2 where the kernel it stands for has 0.073, so the convolution is a no-op that leaves the
            // widths exactly where they were. That failure is silent -- the pass runs, the star does not
            // move -- and it is what a sub-pixel kernel does whenever it is sampled at pixel centres.
            const int SubSamples = 16;
            for (var k = -radius; k <= radius; k++)
            {
                var sum = 0.0;
                for (var s = 0; s < SubSamples; s++)
                {
                    var offset = k - 0.5 + ((s + 0.5) / SubSamples);
                    sum += Math.Exp(-(offset * offset) / twoSigmaSquared);
                }
                kernel[k + radius] = sum / SubSamples;
            }

            var row = new float[width];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    row[x] = plane[y, x];
                }
                for (var x = 0; x < width; x++)
                {
                    if (float.IsNaN(row[x]))
                    {
                        continue;
                    }
                    double sum = 0, weight = 0;
                    var from = Math.Max(0, x - radius);
                    var to = Math.Min(width - 1, x + radius);
                    for (var i = from; i <= to; i++)
                    {
                        var v = row[i];
                        if (float.IsNaN(v))
                        {
                            continue;
                        }
                        var w = kernel[i - x + radius];
                        sum += w * v;
                        weight += w;
                    }
                    plane[y, x] = weight > 0 ? (float)(sum / weight) : row[x];
                }
            }

            var column = new float[height];
            for (var x = 0; x < width; x++)
            {
                for (var y = 0; y < height; y++)
                {
                    column[y] = plane[y, x];
                }
                for (var y = 0; y < height; y++)
                {
                    if (float.IsNaN(column[y]))
                    {
                        continue;
                    }
                    double sum = 0, weight = 0;
                    var from = Math.Max(0, y - radius);
                    var to = Math.Min(height - 1, y + radius);
                    for (var i = from; i <= to; i++)
                    {
                        var v = column[i];
                        if (float.IsNaN(v))
                        {
                            continue;
                        }
                        var w = kernel[i - y + radius];
                        sum += w * v;
                        weight += w;
                    }
                    plane[y, x] = weight > 0 ? (float)(sum / weight) : column[y];
                }
            }
        }

        /// <summary>The mean frame after either side moved: it is a projection of the two, never a third
        /// thing to keep in step by hand.</summary>
        internal static void RecomputeMean(float[][,] planesA, float[][,] planesB, float[][,] planesM)
        {
            for (var c = 0; c < planesA.Length; c++)
            {
                var pa = planesA[c];
                var pb = planesB[c];
                var pm = planesM[c];
                var height = pm.GetLength(0);
                var width = pm.GetLength(1);
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var va = pa[y, x];
                        var vb = pb[y, x];
                        pm[y, x] = float.IsFinite(va) && float.IsFinite(vb) ? 0.5f * (va + vb) : float.NaN;
                    }
                }
            }
        }

        /// <summary>Quad budgets to try in order, each capped by the shorter star list.</summary>
        internal static int[] QuadBudgets(Options options, int starsA, int starsB)
        {
            var cap = Math.Min(starsA, starsB);
            var wanted = options.QuadStars > 0
                ? [options.QuadStars]
                : new[] { FrameRegistration.DefaultQuadStars, FrameRegistration.DefaultQuadStars * 2, FrameRegistration.DefaultQuadStars * 4 };
            var budgets = new List<int>(wanted.Length);
            foreach (var w in wanted)
            {
                var capped = Math.Min(w, cap);
                if (budgets.Count == 0 || capped > budgets[^1])
                {
                    budgets.Add(capped);
                }
            }
            return [.. budgets];
        }

        /// <summary>
        /// The transform taking the second night onto the first, at the first budget that answers.
        /// <para><b>A partial overlap starves the matcher rather than defeating it.</b> The budget takes
        /// the brightest N stars of each night; where two nights share half a field, two lists of a
        /// hundred can hold no quad in common, and the pair is refused for want of stars nobody looked at.
        /// Escalating is what brings a partially overlapping pair in, and the basis it returns records
        /// which budget answered.</para>
        /// </summary>
        /// <returns>An empty basis when no budget matched.</returns>
        internal static async Task<(Matrix3x2 Transform, float QuadTolerance, float RmsPx, string Basis)> MatchAsync(
            Options options, StarList starsA, StarList starsB)
        {
            using var sortedB = new SortedStarList(starsB);
            using var sortedA = new SortedStarList(starsA);
            foreach (var budget in QuadBudgets(options, starsA.Count, starsB.Count))
            {
                var (solution, tolerance, rms) = await FrameRegistration.TryMatchAsync(sortedB, sortedA, budget);
                if (solution is not null)
                {
                    return (solution.Value, tolerance, rms, string.Create(CultureInfo.InvariantCulture, $"quad{budget}"));
                }
            }
            return (Matrix3x2.Identity, 0f, 0f, "");
        }

        private static double MedianFwhm(StarList stars)
        {
            var widths = stars.Select(static s => s.StarFWHM).Where(static f => f > 0f && float.IsFinite(f)).ToArray();
            return widths.Length == 0 ? double.NaN : Median(widths);
        }

        /// <summary>FWHM on an exported side: NaN outside the footprint becomes zero, which is what the
        /// raw masters carry there, so the detector sees the frame it was built for.</summary>
        private static async Task<double> MedianFwhmOfAsync(float[][,] planes, Image template, CancellationToken ct)
        {
            var (channels, width, height) = template.Shape;
            var filled = new float[channels][,];
            for (var c = 0; c < channels; c++)
            {
                var src = planes[c];
                var dst = new float[height, width];
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var v = src[y, x];
                        dst[y, x] = float.IsFinite(v) ? v : 0f;
                    }
                }
                filled[c] = dst;
            }
            var image = new Image(filled, BitDepth.Float32, template.MaxValue, template.MinValue, template.Pedestal, template.ImageMeta);
            var stars = await image.FindStarsAsync(StarChannel, StarSnrMin, StarMax, MinStars, cancellationToken: ct);
            return stars.Count == 0 ? double.NaN : MedianFwhm(stars);
        }

        /// <summary>
        /// Robust per-channel fit of <c>b = gain * a + offset</c> over the pixels both nights cover:
        /// medians seed it, then three rounds of least squares on the pairs within three sigma of the
        /// current residual, with the brightest half-percent of A left out so saturation does not steer
        /// the gain. Subsampled to about two million pairs.
        /// </summary>
        internal static (double Gain, double Offset) FitLevel(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
        {
            var stride = Math.Max(1, a.Length / 2_000_000);
            var xs = new List<float>(Math.Min(a.Length, 2_100_000));
            var ys = new List<float>(xs.Capacity);
            for (var i = 0; i < a.Length; i += stride)
            {
                var va = a[i];
                var vb = b[i];
                if (float.IsFinite(va) && float.IsFinite(vb))
                {
                    xs.Add(va);
                    ys.Add(vb);
                }
            }
            if (xs.Count < 16)
            {
                return (1.0, 0.0);
            }
            var xa = xs.ToArray();
            var ya = ys.ToArray();
            var cap = Percentile(xa, 0.995);
            var gain = 1.0;
            var offset = Median(ya) - Median(xa);
            var residual = new float[xa.Length];
            for (var round = 0; round < 3; round++)
            {
                for (var i = 0; i < xa.Length; i++)
                {
                    residual[i] = (float)(ya[i] - ((gain * xa[i]) + offset));
                }
                var kept = new List<int>(xa.Length);
                for (var i = 0; i < xa.Length; i++)
                {
                    if (xa[i] <= cap)
                    {
                        kept.Add(i);
                    }
                }
                var keptResiduals = new float[kept.Count];
                for (var i = 0; i < kept.Count; i++)
                {
                    keptResiduals[i] = residual[kept[i]];
                }
                var med = Median(keptResiduals);
                for (var i = 0; i < keptResiduals.Length; i++)
                {
                    keptResiduals[i] = MathF.Abs(keptResiduals[i] - (float)med);
                }
                var clip = 3.0 * 1.4826 * Math.Max(Median(keptResiduals), 1e-12);
                double sx = 0, sy = 0, sxx = 0, sxy = 0;
                long count = 0;
                foreach (var i in kept)
                {
                    if (Math.Abs(residual[i] - med) > clip)
                    {
                        continue;
                    }
                    double x = xa[i], y = ya[i];
                    sx += x;
                    sy += y;
                    sxx += x * x;
                    sxy += x * y;
                    count++;
                }
                if (count < 16)
                {
                    break;
                }
                var varX = (sxx / count) - ((sx / count) * (sx / count));
                if (varX <= 0)
                {
                    break;
                }
                gain = ((sxy / count) - ((sx / count) * (sy / count))) / varX;
                offset = (sy / count) - (gain * (sx / count));
            }
            return (gain > 0 && double.IsFinite(gain) && double.IsFinite(offset) ? gain : 1.0, double.IsFinite(offset) ? offset : 0.0);
        }

        /// <summary>Box half-width of the scene-brightness mask in <see cref="ResidualStatistics"/>.</summary>
        internal const int SceneBoxHalf = 7;

        /// <summary>
        /// Per channel: the background noise sigma of each side and the Pearson correlation between the
        /// two sides' residuals. The residual is the pixel minus its 5x5 mean, taken on every second
        /// pixel of the faintest half of the scene, with pixels beyond five MADs on either side dropped so
        /// stars do not enter. Sigma is the residual MAD scaled to a Gaussian and corrected for the 24/25
        /// variance a 5x5 mean subtraction leaves.
        /// </summary>
        /// <remarks>
        /// "Faintest half" is judged on a 15x15 box mean of the combined stretched luminance, never on the
        /// pixel itself. The combined pixel is (A + B) / 2, so selecting on it being low conditions on
        /// the two sides' noise SUMMING low, which anticorrelates them: independent synthetic noise read
        /// -0.23 through the pixel mask and a half-shared control read 0.49 where 0.71 was the truth. A
        /// box mean over 225 pixels carries 1/225 of the centre pixel's noise and the bias goes with it.
        /// </remarks>
        internal static (double[] SigmaA, double[] SigmaB, double[] Correlation) ResidualStatistics(
            float[][,] planesA, float[][,] planesB, ReadOnlySpan<float> stretchedLuminance, int width, int height)
        {
            var channels = planesA.Length;
            var sigmaA = new double[channels];
            var sigmaB = new double[channels];
            var corr = new double[channels];
            var scene = BoxMean(stretchedLuminance, width, height, SceneBoxHalf);
            var finiteScene = new List<float>(scene.Length / 4);
            for (var i = 0; i < scene.Length; i += 4)
            {
                if (float.IsFinite(scene[i]))
                {
                    finiteScene.Add(scene[i]);
                }
            }
            if (finiteScene.Count == 0)
            {
                Array.Fill(sigmaA, double.NaN);
                Array.Fill(sigmaB, double.NaN);
                Array.Fill(corr, double.NaN);
                return (sigmaA, sigmaB, corr);
            }
            var faintCut = Median(finiteScene.ToArray());
            const float SigmaCorrection = 1.4826f / 0.9798f;
            for (var c = 0; c < channels; c++)
            {
                var pa = planesA[c];
                var pb = planesB[c];
                var ra = new List<float>();
                var rb = new List<float>();
                for (var y = 2; y < height - 2; y += 2)
                {
                    for (var x = 2; x < width - 2; x += 2)
                    {
                        if (!(scene[(y * width) + x] <= faintCut))
                        {
                            continue;
                        }
                        if (!TryResidual(pa, x, y, out var resA) || !TryResidual(pb, x, y, out var resB))
                        {
                            continue;
                        }
                        ra.Add(resA);
                        rb.Add(resB);
                    }
                }
                if (ra.Count < 64)
                {
                    sigmaA[c] = sigmaB[c] = corr[c] = double.NaN;
                    continue;
                }
                var arrA = ra.ToArray();
                var arrB = rb.ToArray();
                var madA = Mad(arrA);
                var madB = Mad(arrB);
                var clipA = 5f * madA * 1.4826f;
                var clipB = 5f * madB * 1.4826f;
                double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0;
                long count = 0;
                for (var i = 0; i < arrA.Length; i++)
                {
                    if (MathF.Abs(arrA[i]) > clipA || MathF.Abs(arrB[i]) > clipB)
                    {
                        continue;
                    }
                    double va = arrA[i], vb = arrB[i];
                    sa += va;
                    sb += vb;
                    saa += va * va;
                    sbb += vb * vb;
                    sab += va * vb;
                    count++;
                }
                sigmaA[c] = madA * SigmaCorrection;
                sigmaB[c] = madB * SigmaCorrection;
                if (count < 64)
                {
                    corr[c] = double.NaN;
                    continue;
                }
                var ma = sa / count;
                var mb = sb / count;
                var va2 = (saa / count) - (ma * ma);
                var vb2 = (sbb / count) - (mb * mb);
                corr[c] = va2 > 0 && vb2 > 0 ? ((sab / count) - (ma * mb)) / Math.Sqrt(va2 * vb2) : double.NaN;
            }
            return (sigmaA, sigmaB, corr);

            static bool TryResidual(float[,] plane, int x, int y, out float residual)
            {
                var sum = 0f;
                for (var dy = -2; dy <= 2; dy++)
                {
                    for (var dx = -2; dx <= 2; dx++)
                    {
                        var v = plane[y + dy, x + dx];
                        if (!float.IsFinite(v))
                        {
                            residual = float.NaN;
                            return false;
                        }
                        sum += v;
                    }
                }
                residual = plane[y, x] - (sum / 25f);
                return true;
            }
        }

        /// <summary>Mean over a (2 half + 1)^2 box of the finite values, NaN where none is finite; a
        /// summed-area table over values and over the finite count, so it is one pass whatever the box.</summary>
        internal static float[] BoxMean(ReadOnlySpan<float> plane, int width, int height, int half)
        {
            var stride = width + 1;
            var sum = new double[(height + 1) * stride];
            var count = new int[(height + 1) * stride];
            for (var y = 1; y <= height; y++)
            {
                double rowSum = 0;
                var rowCount = 0;
                for (var x = 1; x <= width; x++)
                {
                    var v = plane[((y - 1) * width) + x - 1];
                    if (float.IsFinite(v))
                    {
                        rowSum += v;
                        rowCount++;
                    }
                    sum[(y * stride) + x] = sum[((y - 1) * stride) + x] + rowSum;
                    count[(y * stride) + x] = count[((y - 1) * stride) + x] + rowCount;
                }
            }
            var result = new float[width * height];
            for (var y = 0; y < height; y++)
            {
                var y0 = Math.Max(0, y - half);
                var y1 = Math.Min(height, y + half + 1);
                for (var x = 0; x < width; x++)
                {
                    var x0 = Math.Max(0, x - half);
                    var x1 = Math.Min(width, x + half + 1);
                    var n = count[(y1 * stride) + x1] - count[(y0 * stride) + x1] - count[(y1 * stride) + x0] + count[(y0 * stride) + x0];
                    result[(y * width) + x] = n == 0
                        ? float.NaN
                        : (float)((sum[(y1 * stride) + x1] - sum[(y0 * stride) + x1] - sum[(y1 * stride) + x0] + sum[(y0 * stride) + x0]) / n);
                }
            }
            return result;
        }

        /// <summary>Tile origins at half-tile stride whose whole tile is finite in <paramref name="plane"/>,
        /// by a summed-area table over the finite mask.</summary>
        internal static List<Point> FiniteTileOrigins(float[,] plane, int width, int height)
        {
            var sat = new int[(height + 1) * (width + 1)];
            var stride = width + 1;
            for (var y = 1; y <= height; y++)
            {
                var rowSum = 0;
                for (var x = 1; x <= width; x++)
                {
                    rowSum += float.IsFinite(plane[y - 1, x - 1]) ? 1 : 0;
                    sat[(y * stride) + x] = sat[((y - 1) * stride) + x] + rowSum;
                }
            }
            var candidates = new List<Point>();
            var step = TileSize / 2;
            var full = TileSize * TileSize;
            for (var oy = 0; oy + TileSize <= height; oy += step)
            {
                for (var ox = 0; ox + TileSize <= width; ox += step)
                {
                    var y0 = oy;
                    var y1 = oy + TileSize;
                    var x0 = ox;
                    var x1 = ox + TileSize;
                    var finite = sat[(y1 * stride) + x1] - sat[(y0 * stride) + x1] - sat[(y1 * stride) + x0] + sat[(y0 * stride) + x0];
                    if (finite == full)
                    {
                        candidates.Add(new Point(ox, oy));
                    }
                }
            }
            return candidates;
        }

        private static float FiniteMax(params float[][][,] planeSets)
        {
            var max = float.NegativeInfinity;
            foreach (var planes in planeSets)
            {
                foreach (var plane in planes)
                {
                    foreach (var v in plane)
                    {
                        if (float.IsFinite(v) && v > max)
                        {
                            max = v;
                        }
                    }
                }
            }
            return float.IsFinite(max) ? max : 0f;
        }

        private static void ScaleInPlace(float[][,] planes, float factor)
        {
            foreach (var plane in planes)
            {
                var height = plane.GetLength(0);
                var width = plane.GetLength(1);
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        plane[y, x] *= factor;
                    }
                }
            }
        }

        private static double Median(float[] values)
        {
            if (values.Length == 0)
            {
                return double.NaN;
            }
            var copy = (float[])values.Clone();
            Array.Sort(copy);
            var mid = copy.Length / 2;
            return copy.Length % 2 == 1 ? copy[mid] : 0.5 * (copy[mid - 1] + copy[mid]);
        }

        private static float Mad(float[] values)
        {
            var med = (float)Median(values);
            var dev = new float[values.Length];
            for (var i = 0; i < values.Length; i++)
            {
                dev[i] = MathF.Abs(values[i] - med);
            }
            return (float)Median(dev);
        }

        private static float Percentile(float[] values, double fraction)
        {
            var copy = (float[])values.Clone();
            Array.Sort(copy);
            var index = (int)Math.Clamp(Math.Round(fraction * (copy.Length - 1)), 0, copy.Length - 1);
            return copy[index];
        }
    }

    [JsonSourceGenerationOptions(WriteIndented = false, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
    [JsonSerializable(typeof(DatasetCrossNightExporter.PairRow))]
    internal sealed partial class DatasetCrossNightJsonContext : JsonSerializerContext
    {
    }
}
