using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TianWen.AI.Imaging.Onnx;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Dataset;
using TianWen.Lib.Stat;

namespace TianWen.AI.Imaging;

/// <summary>
/// Exports N2N / deconv training tiles from a registered session
/// (docs/plans/ai-denoise-deconv.md §2.4, task P0/#40). For each sampled 256-px cell on the
/// session's union canvas it writes the master tile (eval truth / deconv source) plus a random
/// handful of registered-sub tiles (each sub-pair a Noise2Noise training pair), all sharing the
/// exact footprint, and appends one JSONL manifest row per tile.
///
/// <para><b>Zero train/inference skew (non-negotiable).</b> Every frame is pushed through the
/// <i>same</i> NAFNet input pre-stretch the inference path uses; 
/// <see cref="ChunkedNafnetRunner.ApplyInputStretch"/> (SAS-Pro auto-detect gating
/// <see cref="Image.MtfStretch"/> to <see cref="AiNafnetInputs.TargetMedian"/> = 0.25), and the
/// tile bytes are stored post-stretch in the <c>[0, 1]</c> convention. Python trains on the bytes
/// as-stored and never re-implements preprocessing. Because the session master is integrated
/// unnormalised (a genuine linear frame on the subs' scale, see <see cref="SessionRegistrar"/>),
/// the master and its subs traverse the identical auto-detect branch and land at the same median,
/// so a sub tile, its N2N partner, and the master tile are directly comparable.</para>
///
/// <para><b>NaN safety.</b> Warped subs carry NaN outside their source footprint; cells are sampled
/// only inside the session's all-frames intersection (<see cref="SessionRegistrar.RegisteredSession.StatsRect"/>),
/// so every exported tile is fully covered by every sub and the master; no NaN reaches a tile.
/// The MTF pre-stretch is itself NaN-robust, so the whole-frame stretch balance is unpoisoned.</para>
///
/// <para><b>Determinism.</b> Cell selection (structure-biased) and per-cell sub choice are seeded
/// from the portable session id, and the manifest is canonically sorted before writing, so a
/// re-run reproduces the tile set + row order byte-for-byte, which every downstream seeded split
/// depends on (plan §2.4).</para>
/// </summary>
public static class DatasetTileExporter
{
    /// <summary>Little-endian fp16 raw blob (channel-major C·H·W), directly
    /// <c>np.frombuffer(..., '&lt;f2').reshape(C, H, W)</c>-able.</summary>
    public const string TileExtension = ".f16";

    /// <summary>Manifest file name written under the output directory.</summary>
    public const string ManifestFileName = "tiles-manifest.jsonl";

    /// <summary>
    /// The <see cref="TileManifestRow.Frame"/> vocabulary. Named constants because these strings are
    /// the join key a trainer selects on, and they appear in four places that must agree (the tile
    /// filename, the row, the canonical sort, and the parity check's source resolution).
    ///
    /// <para><b>"Half" is overloaded in this file, deliberately kept apart.</b>
    /// <see cref="ExtractTileHalfs"/> and its <see cref="Half"/> spans are fp16 STORAGE; a
    /// half-MASTER is one of two disjoint integrations of the session. Anything referring to the
    /// latter says <c>halfMaster</c>.</para>
    /// </summary>
    public const string FrameMaster = "master";

    /// <inheritdoc cref="FrameMaster"/>
    public const string FrameSub = "sub";

    /// <summary>
    /// One of the two disjoint half-session integrations (<see cref="SessionRegistrar.RegisteredSession.HalfMasterA"/>).
    /// The A/B pair is an N2N pair at ~1.41x a full master's noise, which is the regime a denoiser is
    /// actually deployed in; sub pairs sit at 2.96x even at the deepest 4v4 the sub tiles allow, so a
    /// model trained on those alone has to extrapolate to the case that matters most.
    /// </summary>
    public const string FrameHalfMasterA = "halfmaster_a";

    /// <inheritdoc cref="FrameHalfMasterA"/>
    public const string FrameHalfMasterB = "halfmaster_b";

    /// <summary>
    /// Canonical rank of a <see cref="TileManifestRow.Frame"/> for the manifest sort: master, then
    /// the half-master pair in A/B order, then subs. Stated as a rank rather than left to
    /// <see cref="string.CompareOrdinal(string, string)"/> on the value, which would silently reorder
    /// the file the next time a frame kind is added or renamed ("halfmaster_a" sorts BEFORE "master"
    /// lexicographically, so the old ordinal compare would have quietly moved the master row).
    /// </summary>
    private static int FrameRank(string frame) => frame switch
    {
        FrameMaster => 0,
        FrameHalfMasterA => 1,
        FrameHalfMasterB => 2,
        _ => 3,
    };

    /// <summary>One manifest row per exported tile.</summary>
    /// <param name="Tile">Blob path relative to the output directory (forward slashes).</param>
    /// <param name="SessionId">Portable session id (keys the pinned train/test split).</param>
    /// <param name="Camera">INSTRUME of the session.</param>
    /// <param name="Frame">One of <see cref="FrameMaster"/>, <see cref="FrameHalfMasterA"/>,
    /// <see cref="FrameHalfMasterB"/>, <see cref="FrameSub"/>.</param>
    /// <param name="SourceFile">Original raw light file name for a sub tile; "" for the master and
    /// the half-masters, which are integrations of many lights rather than one file.</param>
    /// <param name="CellX">Tile origin X on the union canvas (shared across the cell's tiles).</param>
    /// <param name="CellY">Tile origin Y on the union canvas.</param>
    /// <param name="TileSize">Tile edge length in pixels (square).</param>
    /// <param name="Channels">Channel count stored in the blob (C·H·W layout).</param>
    /// <param name="Gain">Camera gain (session-uniform).</param>
    /// <param name="ExposureSeconds">Sub exposure in seconds.</param>
    /// <param name="NoiseMad">Per-tile noise proxy: MAD of the stored channel-0 tile.</param>
    /// <remarks>
    /// There is deliberately no session median FWHM here, and re-adding one would reintroduce a bug
    /// that already happened. This row is written once, at export time, and a session that resumes
    /// with its tiles intact never rewrites its rows; the PSF store is rewritten independently (and
    /// <c>--force-psf</c> rewrites only the store). So the moment the estimator changed, 45 of 50
    /// sessions were left carrying quantized FWHM values that no longer matched the store, and the
    /// column looked authoritative while being wrong.
    /// <para>
    /// FWHM belongs to the PSF store, which is its single source of truth: a per-session median is
    /// <c>median(SubFwhm)</c> over that session's record in <c>stats/psf-sessions.jsonl</c>, joined
    /// on <see cref="SessionId"/>. That is exactly what this column used to hold, so nothing is lost
    /// by its absence -- only the second copy that could drift.
    /// </para>
    /// </remarks>
    public sealed record TileManifestRow(
        string Tile,
        string SessionId,
        string Camera,
        string Frame,
        string SourceFile,
        int CellX,
        int CellY,
        int TileSize,
        int Channels,
        int Gain,
        double ExposureSeconds,
        double NoiseMad);

    /// <summary>Summary of one session's export.</summary>
    public sealed record TileExportResult(
        int Cells,
        int MasterTiles,
        int SubTiles,
        int HalfMasterTiles,
        string ManifestPath,
        ImmutableArray<TileManifestRow> Rows);

    /// <summary>Result of the zero-skew parity check: how many stored tiles were re-derived and the
    /// largest absolute value difference found. A green check is <see cref="MaxAbsDiff"/> == 0.</summary>
    public sealed record ParityResult(int Checked, double MaxAbsDiff);

    /// <summary>
    /// Exports tiles for one registered session under <paramref name="outDir"/> and appends its
    /// rows to the shared JSONL manifest. Returns the per-session summary. The manifest is written
    /// (this session's rows, canonically sorted) as an atomic append.
    /// </summary>
    /// <param name="session">The registered session (master + canvas-aligned warped subs).</param>
    /// <param name="outDir">Output root. Tiles land under <c>&lt;outDir&gt;/tiles/&lt;sessionSlug&gt;/</c>,
    /// the manifest at <c>&lt;outDir&gt;/<see cref="ManifestFileName"/></c>.</param>
    /// <param name="tileSize">Cell edge length (must match the inference chunk size, default 256).</param>
    /// <param name="cellsPerSession">Upper bound on sampled cells (structure-biased).</param>
    /// <param name="subsPerCell">Sub tiles exported per cell (any two form an N2N pair).</param>
    /// <param name="logger">Optional progress log.</param>
    public static async Task<TileExportResult> ExportAsync(
        SessionRegistrar.RegisteredSession session,
        string outDir,
        int tileSize = 256,
        int cellsPerSession = 300,
        int subsPerCell = 8,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var imaging = session.Session;

        // A non-finite master extremum silently zeroes EVERY tile, so refuse before writing anything.
        //
        // This is not hypothetical. StreamingFrameStaging used to cast a float straight to Half, so a
        // sub whose pixels reached the 16-bit ceiling (N.I.N.A. writes a 14-bit sensor scaled into
        // [0, 65532], and Half tops out at 65504) staged as +Inf. The integrator averaged that into
        // the master, the master's MaxValue became +Inf, ToUnitRange below divided by it, and every
        // sample went to 0. Five sessions wrote 1,500 tiles of pure zeroes that looked like perfectly
        // ordinary files, and nothing noticed until a human opened one in ASTAP.
        //
        // The parity check cannot catch this class by construction: it compares stored bytes against
        // bytes re-derived through the same path, and zeroes equal zeroes. It pins drift, never
        // emptiness. So the guard has to be a separate assertion about the DATA, made here.
        //
        // Throwing rather than warning is deliberate: a poisoned master makes the whole session's
        // output worthless, the runner already catches per-session faults and moves on, and a
        // half-written session is worse than a skipped one.
        RequireFiniteRange(session.Master, "master", imaging.Id);
        // The half-masters go through the identical ToUnitRange division, so they carry the identical
        // exposure to it; a guard on the master alone would let a poisoned half write 2 zero tiles per
        // cell into a set whose master tiles are all fine, which is worse than either extreme because
        // the session looks healthy.
        if (session.HalfMasterA is { } preA)
        {
            RequireFiniteRange(preA, FrameHalfMasterA, imaging.Id);
        }
        if (session.HalfMasterB is { } preB)
        {
            RequireFiniteRange(preB, FrameHalfMasterB, imaging.Id);
        }

        var slug = Sanitize(imaging.Id);
        var tilesDir = Path.Combine(outDir, "tiles", slug);
        Directory.CreateDirectory(tilesDir);

        // Candidate cell origins tile the all-frames intersection at half-tile stride (structure
        // bias then thins them to cellsPerSession). Half-tile stride keeps tiles fully inside the
        // NaN-free StatsRect while giving the sampler more candidates than it needs.
        var rect = session.StatsRect;
        var stride = Math.Max(1, tileSize / 2);
        var candidates = new List<Point>();
        for (var oy = rect.Y; oy + tileSize <= rect.Bottom; oy += stride)
        {
            for (var ox = rect.X; ox + tileSize <= rect.Right; ox += stride)
            {
                candidates.Add(new Point(ox, oy));
            }
        }
        if (candidates.Count == 0)
        {
            logger?.LogWarning("  [{Session}] intersection {Rect} too small for a {Tile}px tile -- no tiles",
                imaging.Id, rect, tileSize);
            return new TileExportResult(0, 0, 0, 0, Path.Combine(outDir, ManifestFileName), []);
        }

        var seed = StableSeed(imaging.Id);
        var rng = new Random(seed);

        // Structure-biased cell selection: weight each candidate by the local MAD of the (stretched)
        // master channel 0 -- stars/nebulosity score high, blank sky low (plus a floor so background
        // tiles still appear, which denoise needs to learn not to over-smooth). Efraimidis-Spirakis
        // weighted sampling without replacement keeps it deterministic under the seeded RNG.
        var (masterStretched, _, _, _) = ChunkedNafnetRunner.ApplyInputStretch(ToUnitRange(session.Master));
        var masterCh0 = masterStretched.GetChannelSpan(0);
        var mW = masterStretched.Width;
        var weights = new double[candidates.Count];
        for (var i = 0; i < candidates.Count; i++)
        {
            weights[i] = CellMad(masterCh0, mW, candidates[i].X, candidates[i].Y, tileSize) + 1e-4;
        }
        var selected = WeightedSampleWithoutReplacement(candidates, weights, Math.Min(cellsPerSession, candidates.Count), rng);
        // Canonical cell order (row-major) so the master pass, the sub->cell map, and the manifest
        // all agree independent of the sampler's internal ordering.
        selected.Sort(static (a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));

        var subCount = session.Subs.Length;
        var perCellSubs = new int[selected.Count][];
        var subToCells = new Dictionary<int, List<int>>();
        var take = Math.Min(subsPerCell, subCount);
        var indexScratch = new int[subCount];
        for (var c = 0; c < selected.Count; c++)
        {
            perCellSubs[c] = SampleDistinct(indexScratch, subCount, take, rng);
            foreach (var subIdx in perCellSubs[c])
            {
                if (!subToCells.TryGetValue(subIdx, out var list))
                {
                    subToCells[subIdx] = list = new List<int>();
                }
                list.Add(c);
            }
        }

        var refMeta = session.Reference.Meta;
        var channels = session.Master.ChannelCount;
        var rows = ImmutableArray.CreateBuilder<TileManifestRow>();

        // Master tiles (one per selected cell), then the half-master pair over the SAME cells, so a
        // cell's master / halfA / halfB / sub tiles are all the same footprint on the same grid.
        var masterTiles = EmitWholeFrameTiles(masterStretched, FrameMaster);
        var halfMasterTiles = 0;
        // Stretched one at a time and dropped: each is another full-canvas float image (124 MB on a
        // 236-sub session), and the session record already holds three unstretched ones.
        if (session.HalfMasterA is { } halfMasterA)
        {
            var (stretched, _, _, _) = ChunkedNafnetRunner.ApplyInputStretch(ToUnitRange(halfMasterA));
            halfMasterTiles += EmitWholeFrameTiles(stretched, FrameHalfMasterA);
        }
        if (session.HalfMasterB is { } halfMasterB)
        {
            var (stretched, _, _, _) = ChunkedNafnetRunner.ApplyInputStretch(ToUnitRange(halfMasterB));
            halfMasterTiles += EmitWholeFrameTiles(stretched, FrameHalfMasterB);
        }

        // One tile per selected cell out of a whole-session frame (the master or one half-master), as
        // opposed to the per-sub pass below. Deliberately no integrated-frame COUNT on the row: what a
        // consumer conditions on is the noise level, NoiseMad measures it per tile, and a second
        // derived copy of a session-level fact on a row that is never rewritten is exactly how the
        // FWHM column came to be authoritative while wrong (see TileManifestRow's remarks).
        int EmitWholeFrameTiles(Image stretched, string frame)
        {
            for (var c = 0; c < selected.Count; c++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var cell = selected[c];
                var file = $"x{cell.X}_y{cell.Y}_{frame}{TileExtension}";
                var mad = WriteTile(stretched, cell, tileSize, Path.Combine(tilesDir, file), imaging.Id);
                rows.Add(new TileManifestRow(
                    Tile: $"tiles/{slug}/{file}", SessionId: imaging.Id, Camera: imaging.Camera,
                    Frame: frame, SourceFile: "", CellX: cell.X, CellY: cell.Y, TileSize: tileSize,
                    Channels: channels, Gain: refMeta.Gain, ExposureSeconds: refMeta.ExposureDuration.TotalSeconds,
                    NoiseMad: mad));
            }
            return selected.Count;
        }

        // Sub tiles, iterated sub-major so only one stretched sub is resident at a time.
        var subTiles = 0;
        foreach (var (subIdx, cellsForSub) in subToCells)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sub = session.Subs[subIdx];
            var warped = await Task.Run(() =>
            {
                if (!Image.TryReadFitsFile(sub.WarpedPath, out var img))
                {
                    throw new IOException($"Failed to re-read warped scratch FITS: {sub.WarpedPath}");
                }
                return img;
            }, cancellationToken);
            var (subStretched, _, _, _) = ChunkedNafnetRunner.ApplyInputStretch(ToUnitRange(warped));
            var sourceName = Path.GetFileName(sub.Source.Path);
            var subMeta = sub.Source.Meta;
            foreach (var cellIndex in cellsForSub)
            {
                var cell = selected[cellIndex];
                var file = $"x{cell.X}_y{cell.Y}_s{subIdx:D3}{TileExtension}";
                var mad = WriteTile(subStretched, cell, tileSize, Path.Combine(tilesDir, file), imaging.Id);
                rows.Add(new TileManifestRow(
                    Tile: $"tiles/{slug}/{file}", SessionId: imaging.Id, Camera: imaging.Camera,
                    Frame: FrameSub, SourceFile: sourceName, CellX: cell.X, CellY: cell.Y, TileSize: tileSize,
                    Channels: channels, Gain: subMeta.Gain, ExposureSeconds: subMeta.ExposureDuration.TotalSeconds,
                    NoiseMad: mad));
                subTiles++;
            }
        }

        // Canonical manifest order (cell, then frame kind, then sub index) BEFORE writing --
        // parallel/interleaved writers would otherwise break every downstream seeded operation.
        var sorted = rows.ToImmutable().Sort(RowOrder);
        var manifestPath = Path.Combine(outDir, ManifestFileName);
        await AppendManifestAsync(manifestPath, sorted, cancellationToken);

        logger?.LogInformation(
            "  [{Session}] exported {Cells} cells -> {Master} master + {HalfMaster} half-master + {Sub} sub tiles",
            imaging.Id, selected.Count, masterTiles, halfMasterTiles, subTiles);
        return new TileExportResult(selected.Count, masterTiles, subTiles, halfMasterTiles, manifestPath, sorted);
    }

    /// <summary>
    /// Zero-skew parity check (plan §2.4, task P0/#42). For a strided sample of the exported
    /// <paramref name="rows"/>, re-derives each tile from its source frame through the SAME
    /// <see cref="ToUnitRange"/> + <see cref="ChunkedNafnetRunner.ApplyInputStretch"/> +
    /// <see cref="ExtractTileHalfs"/> path and diffs it against the bytes on disk. The point is a
    /// CI-able pin: if the stored tile ever stops equalling "the C# stretch of the source"; a
    /// changed stored format, a skipped stretch, a wrong cell mapping, an fp16 regression; the max
    /// diff goes non-zero and the guarantee that Python trains on exactly what inference sees breaks
    /// loudly. Requires the session's warped scratch subs to still exist (run before cleanup).
    /// </summary>
    public static async Task<ParityResult> VerifyParityAsync(
        SessionRegistrar.RegisteredSession session,
        string outDir,
        ImmutableArray<TileManifestRow> rows,
        int sampleCount = 8,
        CancellationToken cancellationToken = default)
    {
        if (rows.Length == 0)
        {
            return new ParityResult(0, 0.0);
        }

        var stretchedCache = new Dictionary<string, Image>();
        var step = Math.Max(1, rows.Length / Math.Max(1, sampleCount));
        var maxDiff = 0.0;
        var checkedCount = 0;
        for (var i = 0; i < rows.Length; i += step)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = rows[i];
            var stretched = ResolveStretched(row);
            var expected = ExtractTileHalfs(stretched, new Point(row.CellX, row.CellY), row.TileSize, out _);

            var blobPath = Path.Combine(outDir, row.Tile.Replace('/', Path.DirectorySeparatorChar));
            var storedBytes = await File.ReadAllBytesAsync(blobPath, cancellationToken);
            var stored = MemoryMarshal.Cast<byte, Half>(storedBytes);
            checkedCount++;
            if (stored.Length != expected.Length)
            {
                maxDiff = double.PositiveInfinity;
                continue;
            }
            for (var k = 0; k < expected.Length; k++)
            {
                var d = Math.Abs((double)(float)expected[k] - (float)stored[k]);
                if (d > maxDiff) maxDiff = d;
            }
        }
        return new ParityResult(checkedCount, maxDiff);

        Image ResolveStretched(TileManifestRow row)
        {
            // Keyed by FRAME for every whole-session kind and by source file for a sub. A key derived
            // from SourceFile alone cannot work here: the master and both half-masters carry "", so
            // they would share one cache entry and the parity check would compare a half-master's
            // stored tile against the master's pixels and report a difference that is not there.
            var key = row.Frame == FrameSub ? "sub:" + row.SourceFile : row.Frame;
            if (stretchedCache.TryGetValue(key, out var cached))
            {
                return cached;
            }
            Image frame;
            if (row.Frame != FrameSub)
            {
                frame = row.Frame switch
                {
                    FrameMaster => session.Master,
                    FrameHalfMasterA => session.HalfMasterA,
                    FrameHalfMasterB => session.HalfMasterB,
                    _ => null,
                } ?? throw new IOException(
                    $"Parity check could not resolve source for {row.Tile} (frame={row.Frame}); the " +
                    $"session no longer carries that frame.");
            }
            else
            {
                SessionRegistrar.RegisteredSub? match = null;
                foreach (var sub in session.Subs)
                {
                    if (Path.GetFileName(sub.Source.Path) == row.SourceFile)
                    {
                        match = sub;
                        break;
                    }
                }
                if (match is null || !Image.TryReadFitsFile(match.WarpedPath, out var img))
                {
                    throw new IOException($"Parity check could not resolve source for {row.Tile} (frame={row.Frame}, source={row.SourceFile}).");
                }
                frame = img;
            }
            var (stretched, _, _, _) = ChunkedNafnetRunner.ApplyInputStretch(ToUnitRange(frame));
            stretchedCache[key] = stretched;
            return stretched;
        }
    }

    private static int RowOrder(TileManifestRow a, TileManifestRow b)
    {
        var cmp = a.CellY.CompareTo(b.CellY);
        if (cmp != 0) return cmp;
        cmp = a.CellX.CompareTo(b.CellX);
        if (cmp != 0) return cmp;
        // Master, half-master A, half-master B, then subs (see FrameRank); then by tile path, which
        // orders subs stably by their zero-padded index.
        cmp = FrameRank(a.Frame).CompareTo(FrameRank(b.Frame));
        if (cmp != 0) return cmp;
        return string.CompareOrdinal(a.Tile, b.Tile);
    }

    /// <summary>
    /// Refuses a frame whose extrema are not finite. <see cref="ToUnitRange"/> divides by
    /// <c>MaxValue</c>, so a single +Inf sample sends every tile derived from that frame to zero, and
    /// the parity check cannot see it (it compares stored bytes against bytes re-derived through the
    /// same path, and zeroes equal zeroes; it pins drift, never emptiness).
    ///
    /// <para>Not hypothetical: <c>StreamingFrameStaging</c> used to cast float straight to
    /// <see cref="Half"/>, so a sub reaching the 16-bit ceiling (N.I.N.A. writes a 14-bit sensor
    /// scaled into [0, 65532]; <see cref="Half"/> tops out at 65504) staged as +Inf, the integrator
    /// averaged it into the master, and five sessions wrote 1,500 tiles of pure zeroes that looked
    /// like ordinary files. Nothing noticed until a human opened one in ASTAP.</para>
    ///
    /// <para>Throws rather than warns: the runner already isolates per-session faults and moves on,
    /// and a half-written session is worse than a skipped one.</para>
    /// </summary>
    private static void RequireFiniteRange(Image frame, string label, string sessionId)
    {
        if (!float.IsFinite(frame.MaxValue) || !float.IsFinite(frame.MinValue))
        {
            throw new InvalidOperationException(
                $"[{sessionId}] {label} pixel range is not finite (min={frame.MinValue}, " +
                $"max={frame.MaxValue}); every tile exported from it would be zero. This means a " +
                $"sub overflowed during staging or integration, not that the tiler is at fault.");
        }
    }

    /// <summary>Writes one CHW fp16 tile at <paramref name="cell"/> and returns the MAD of the
    /// stored channel-0 tile (the manifest's per-tile noise proxy).</summary>
    internal static double WriteTile(Image stretched, Point cell, int tileSize, string path, string sessionId)
    {
        var halfs = ExtractTileHalfs(stretched, cell, tileSize, out var ch0Buf);
        EnsureTileIsUsable(halfs, cell, sessionId, path);
        File.WriteAllBytes(path, MemoryMarshal.AsBytes<Half>(halfs).ToArray());
        return Mad(ch0Buf);
    }

    /// <summary>
    /// Backstop against writing a tile that is structurally worthless as training data: one holding a
    /// non-finite sample, or one that is uniformly zero.
    ///
    /// <para>Deliberately separate from <see cref="ExtractTileHalfs"/> rather than folded into it,
    /// because <see cref="VerifyParityAsync"/> re-derives through that same method purely to compare
    /// bytes. Parity must be able to read back a bad tile in order to report on it; only the WRITE
    /// path gets to refuse.</para>
    ///
    /// <para>An all-zero tile cannot occur legitimately. Cells are constrained to the all-frames
    /// intersection (<c>StatsRect</c>), which is NaN-free and carries real sky, and the stretch puts
    /// background well above zero. When it does happen it means the master was poisoned upstream, so
    /// this fires per tile and the session fails rather than emitting a plausible-looking set of empty
    /// files. The master-level check in <see cref="ExportAsync"/> should catch that case first and
    /// with a better message; this covers whatever it does not anticipate.</para>
    /// </summary>
    private static void EnsureTileIsUsable(ReadOnlySpan<Half> samples, Point cell, string sessionId, string path)
    {
        var allZero = true;
        for (var i = 0; i < samples.Length; i++)
        {
            var v = (float)samples[i];
            if (!float.IsFinite(v))
            {
                throw new InvalidOperationException(
                    $"[{sessionId}] tile at ({cell.X},{cell.Y}) holds a non-finite sample ({v}) at index {i}; " +
                    $"refusing to write {Path.GetFileName(path)}.");
            }
            if (v != 0f)
            {
                allZero = false;
            }
        }

        if (allZero)
        {
            throw new InvalidOperationException(
                $"[{sessionId}] tile at ({cell.X},{cell.Y}) is entirely zero; refusing to write " +
                $"{Path.GetFileName(path)}. The cell lies inside the NaN-free intersection and the " +
                $"stretch lifts background above zero, so this means the master is poisoned, not that " +
                $"the sky was blank.");
        }
    }

    /// <summary>Extracts the CHW fp16 samples of one tile at <paramref name="cell"/> (and the
    /// channel-0 floats via <paramref name="ch0"/>). The single source of the stored tile bytes; 
    /// the parity check re-derives through this exact path so "stored == re-stretched" is pinned.
    /// NaN samples (which should not occur inside StatsRect) are clamped to 0 so a stray edge pixel
    /// can never poison training.</summary>
    internal static Half[] ExtractTileHalfs(Image stretched, Point cell, int tileSize, out float[] ch0)
    {
        var channels = stretched.ChannelCount;
        var w = stretched.Width;
        var halfs = new Half[channels * tileSize * tileSize];
        ch0 = new float[tileSize * tileSize];
        var idx = 0;
        for (var c = 0; c < channels; c++)
        {
            var span = stretched.GetChannelSpan(c);
            for (var y = 0; y < tileSize; y++)
            {
                var srcRow = (cell.Y + y) * w + cell.X;
                for (var x = 0; x < tileSize; x++)
                {
                    var v = span[srcRow + x];
                    if (float.IsNaN(v)) v = 0f;
                    halfs[idx++] = (Half)v;
                    if (c == 0) ch0[y * tileSize + x] = v;
                }
            }
        }
        return halfs;
    }

    /// <summary>MAD (median absolute deviation from the median) of a cell of the master's channel 0,
    /// the structure-bias weight. High where stars/nebulosity vary the signal; low over blank sky.</summary>
    private static double CellMad(ReadOnlySpan<float> channel, int width, int ox, int oy, int tileSize)
    {
        var buf = new float[tileSize * tileSize];
        var n = 0;
        for (var y = 0; y < tileSize; y++)
        {
            var row = (oy + y) * width + ox;
            for (var x = 0; x < tileSize; x++)
            {
                var v = channel[row + x];
                if (!float.IsNaN(v)) buf[n++] = v;
            }
        }
        return n == 0 ? 0.0 : Mad(buf.AsSpan(0, n));
    }

    private static double Mad(Span<float> values)
    {
        if (values.Length == 0) return 0.0;
        var median = StatisticsHelper.MedianFast(values);
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = MathF.Abs(values[i] - median);
        }
        return StatisticsHelper.MedianFast(values);
    }

    /// <summary>Efraimidis-Spirakis weighted sampling without replacement: key = u^(1/w), take the
    /// top <paramref name="k"/> by key. Deterministic given the seeded <paramref name="rng"/>.</summary>
    private static List<Point> WeightedSampleWithoutReplacement(List<Point> items, double[] weights, int k, Random rng)
    {
        if (k >= items.Count)
        {
            return new List<Point>(items);
        }
        var keyed = new (int Index, double Key)[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            var u = rng.NextDouble() * (1.0 - 1e-12) + 1e-12; // (0, 1)
            var w = Math.Max(weights[i], 1e-9);
            keyed[i] = (i, Math.Pow(u, 1.0 / w));
        }
        Array.Sort(keyed, static (a, b) => b.Key.CompareTo(a.Key));
        var picked = new List<Point>(k);
        for (var i = 0; i < k; i++)
        {
            picked.Add(items[keyed[i].Index]);
        }
        return picked;
    }

    /// <summary>Picks <paramref name="take"/> distinct indices from [0, <paramref name="count"/>) via a
    /// partial Fisher-Yates over the reused <paramref name="scratch"/> buffer. Fresh draw per call.</summary>
    private static int[] SampleDistinct(int[] scratch, int count, int take, Random rng)
    {
        for (var i = 0; i < count; i++)
        {
            scratch[i] = i;
        }
        for (var i = 0; i < take; i++)
        {
            var j = i + rng.Next(count - i);
            (scratch[i], scratch[j]) = (scratch[j], scratch[i]);
        }
        var result = new int[take];
        Array.Copy(scratch, result, take);
        Array.Sort(result); // stable sub order per cell
        return result;
    }

    /// <summary>What the manifest records about one already-exported session: how many tiles it
    /// wrote, and the directory they went to (relative to the output dir, forward slashes). The
    /// directory is carried so a resuming run can check the tiles are actually THERE rather than
    /// trusting the manifest alone; see <see cref="ReadManifestCheckpointsAsync"/>.</summary>
    public sealed record ManifestCheckpoint(string SessionId, int TileCount, string TileDirRelative);

    /// <summary>Reads the shared JSONL manifest back into per-session checkpoints; the resume
    /// checkpoint (<see cref="DatasetBuildOptions.Resume"/>). A session listed here was FULLY
    /// exported: its rows are appended in one block as the last step of its export, after every
    /// tile file is on disk. Unparseable lines (a torn tail from a killed run, the same case
    /// <see cref="AppendManifestAsync"/> self-heals on the next append) are skipped, never fatal;
    /// a missing file yields an empty map (resume of a fresh output degrades to a normal run).
    ///
    /// <para><b>The manifest is a claim, not proof.</b> It says what WAS written, so a resuming run
    /// must verify the tiles still exist before skipping the session: delete a tile directory and
    /// the old count-only checkpoint still reported "already exported", the session was skipped, and
    /// the run finished with exit 0 while claiming tiles that were gone. Hence the directory here.</para>
    /// </summary>
    public static async Task<Dictionary<string, ManifestCheckpoint>> ReadManifestCheckpointsAsync(string manifestPath, CancellationToken ct)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var dirs = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(manifestPath))
        {
            return [];
        }
        await foreach (var line in File.ReadLinesAsync(manifestPath, ct))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            TileManifestRow? row;
            // Resilience over untrusted tail bytes (killed mid-append): there is no TryDeserialize,
            // so the torn-line skip has to be exception-based, mirroring TryReadFitsHeader's contract.
            try
            {
                row = JsonSerializer.Deserialize(line, DatasetManifestJsonContext.Default.TileManifestRow);
            }
            catch (JsonException)
            {
                continue;
            }
            if (row is not null)
            {
                counts[row.SessionId] = counts.TryGetValue(row.SessionId, out var n) ? n + 1 : 1;
                var slash = row.Tile.LastIndexOf('/');
                dirs[row.SessionId] = slash > 0 ? row.Tile[..slash] : string.Empty;
            }
        }

        var checkpoints = new Dictionary<string, ManifestCheckpoint>(StringComparer.Ordinal);
        foreach (var (sessionId, count) in counts)
        {
            checkpoints[sessionId] = new ManifestCheckpoint(sessionId, count, dirs.GetValueOrDefault(sessionId, string.Empty));
        }
        return checkpoints;
    }

    /// <summary>Appends this session's rows to the shared JSONL manifest. Self-healing: a torn
    /// last line (a previous session's append interrupted mid-write, reachable because the build
    /// runner fault-isolates per session and keeps going) is truncated back to the last complete
    /// row first, so a corrupt line can never get buried mid-file where every JSONL consumer would
    /// choke on it. Internal for the direct healing test.</summary>
    internal static Task AppendManifestAsync(string manifestPath, ImmutableArray<TileManifestRow> rows, CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var row in rows)
        {
            sb.Append(JsonSerializer.Serialize(row, DatasetManifestJsonContext.Default.TileManifestRow));
            sb.Append('\n');
        }
        // The append + torn-tail heal lives in JsonLinesFile, shared with the PSF store: both are
        // append-only per-session checkpoints and the backward scan only needs to be right once.
        return JsonLinesFile.AppendAsync(manifestPath, sb.ToString(), ct);
    }

    /// <summary>
    /// Normalises a native-ADU frame into the <c>[0, 1]</c> linear convention the NAFNet input path
    /// assumes (the same convention <c>Image.ScaleFloatValuesToUnit</c> produces), through Image's
    /// public API: the exporter must NOT reach into Lib internals, and must NOT mutate the caller's
    /// shared master, so it builds a fresh copy via the channel-typed primary ctor (which then
    /// DERIVES an accurate image-wide <see cref="Image.MaxValue"/> from the per-channel maxima). This
    /// is what puts the frame's median below the SAS auto-detect threshold so
    /// <see cref="ChunkedNafnetRunner.ApplyInputStretch"/> actually applies the MTF stretch; a
    /// raw-ADU frame would read as "already stretched" and pass through unstretched.
    ///
    /// <para>The divisor is <c>max(MaxValue label, actual data max)</c>, NaN-aware. The label alone
    /// can understate the data: debayer/warp bilinear interpolation overshoots the source ceiling,
    /// but the legacy <c>float[][,]</c> ctor those steps use keeps the OLD image-wide max, so a bare
    /// ÷label would push bright pixels past 1 and out of the MTF's <c>[0, 1]</c> domain. Using the
    /// larger of the two matches inference's ÷MaxValue exactly on a correctly-labelled frame while
    /// still landing the brightest pixel of a stale-labelled frame at 1.</para>
    /// </summary>
    /// <summary>
    /// The scalar <see cref="ToUnitRange"/> divides by, or 1 when it would pass the frame through
    /// untouched. Exposed separately because the degradation exporter applies the TARGET frame's
    /// divisor to a degraded CELL: sharing the scalar is what keeps a pair in one domain, and
    /// re-deriving it from the degraded frame would rescale the input side by whatever the injected
    /// noise did to the frame's maximum.
    /// </summary>
    internal static float UnitDivisor(Image img)
    {
        var dataMax = float.NegativeInfinity;
        for (var c = 0; c < img.ChannelCount; c++)
        {
            var span = img.GetChannelSpan(c);
            for (var i = 0; i < span.Length; i++)
            {
                var v = span[i];
                if (!float.IsNaN(v) && v > dataMax)
                {
                    dataMax = v;
                }
            }
        }
        var divisor = MathF.Max(img.MaxValue, dataMax);
        return divisor > 1f ? divisor : 1f;
    }

    internal static Image ToUnitRange(Image img)
    {
        var w = img.Width;
        var h = img.Height;
        var channelCount = img.ChannelCount;

        var divisor = UnitDivisor(img);
        if (divisor == 1f) // already in [0, 1] (or an empty frame)
        {
            return img;
        }
        var inv = 1f / divisor;

        var built = ImmutableArray.CreateBuilder<Channel>(channelCount);
        for (var c = 0; c < channelCount; c++)
        {
            var src = img.GetChannelSpan(c);
            var srcChannel = img.GetChannel(c);
            var plane = new float[h, w];
            var dst = MemoryMarshal.CreateSpan(ref plane[0, 0], src.Length);
            var cMin = float.PositiveInfinity;
            var cMax = float.NegativeInfinity;
            for (var i = 0; i < src.Length; i++)
            {
                var v = src[i] * inv; // NaN * inv = NaN, preserved without branching
                dst[i] = v;
                if (!float.IsNaN(v))
                {
                    if (v < cMin) cMin = v;
                    if (v > cMax) cMax = v;
                }
            }
            if (float.IsPositiveInfinity(cMin))
            {
                cMin = 0f;
                cMax = 0f;
            }
            built.Add(new Channel(plane, srcChannel.Filter, cMin, cMax, srcChannel.Index));
        }
        return new Image(built.MoveToImmutable(), BitDepth.Float32, img.Pedestal * inv, img.ImageMeta);
    }

    private static int StableSeed(string s)
    {
        // FNV-1a 32-bit folded to a positive int -- deterministic across runs, unlike the
        // randomised string.GetHashCode.
        var hash = 2166136261u;
        foreach (var ch in s)
        {
            hash ^= ch;
            hash *= 16777619u;
        }
        return (int)(hash & 0x7FFFFFFF);
    }

    /// <summary>
    /// Filename-safe form of a session id. <c>internal</c> rather than private because
    /// <see cref="DatasetBuildRunner"/> names retained session masters with it: a master and its tile
    /// directory must carry the SAME slug, or tracing one to the other means re-deriving the mapping.
    /// </summary>
    internal static string Sanitize(string id)
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

[JsonSerializable(typeof(DatasetTileExporter.TileManifestRow))]
[JsonSourceGenerationOptions(WriteIndented = false)]
internal partial class DatasetManifestJsonContext : JsonSerializerContext;
