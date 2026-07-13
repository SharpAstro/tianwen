using Microsoft.Extensions.Logging;
using nom.tam.fits;
using nom.tam.util;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging.Calibration;

namespace TianWen.Lib.Imaging.Dataset;

/// <summary>
/// Archive-wide calibration-master cache for the dataset builder. Builds one master per
/// <see cref="MasterGroupKey"/> group and caches it on disk in a SHARED directory (not per-run),
/// so a dark/bias library reused across many sessions is built exactly once and reused across
/// runs. Cache validity is gated on an <b>input-set fingerprint</b> stamped into the master's
/// FITS header (frame count + a digest of the sorted input identities): a grown or changed
/// library invalidates its stale master and rebuilds, rather than silently serving the old one
/// on a slug match.
/// <para>A calibration group flagged <see cref="CalibrationResolver.CalGroup.IsMaster"/> takes the
/// separate FOREIGN-master path (<see cref="LoadForeignMasterAsync"/>): an already-integrated master
/// (e.g. an Astro Pixel Processor MD-IG dark) is loaded directly rather than rebuilt from raw subs.
/// This exists because a camera's proper calibration library sometimes survives only as a master. A
/// tool-normalised subtractive master ([0,1]) is rescaled back to ADU so it can be subtracted from
/// raw-ADU lights; a foreign flat is re-normalised to mean~1. Raw groups (the common case) are still
/// built from raw calibration frames whose provenance we control.</para>
/// </summary>
/// <param name="mastersDir">Shared cache directory (created if missing). Lives under the build
/// output root, e.g. <c>&lt;out&gt;/masters</c>.</param>
public sealed class MasterCache(string mastersDir, ILogger? logger = null)
{
    private const string FingerprintCard = "DSETFPR"; // dataset input-set fingerprint (<= 8 chars)
    private const string InputCountCard = "DSETNIN";  // dataset input frame count

    // Keyed by (sensor-config group, optical train, master flag): two cameras that share a sensor
    // model produce the same MasterGroupKey, so without the train in the key a second body's build
    // would either cross-serve the first's cached Task (in-flight map) or overwrite its file (same
    // slug) -- silently calibrating one camera's lights with another's master. The master flag keeps
    // a foreign already-integrated master group distinct from a raw group of the same config (they
    // take different paths -- direct load vs median build).
    private readonly ConcurrentDictionary<(MasterGroupKey Key, CalibrationResolver.CalTrain Train, bool IsMaster), Task<Image?>> _inFlight = new();

    /// <summary>
    /// Returns the master for a calibration group. For a raw group (<paramref name="isMaster"/> =
    /// false) it builds the master if the on-disk cache is missing or fingerprint-stale, else loads
    /// the cached file, returning null when the group has too few frames to combine (&lt; 2). For a
    /// FOREIGN master group (<paramref name="isMaster"/> = true) it loads the already-integrated
    /// master file directly (no rebuild). Concurrent calls for the same key + train + flag share one
    /// build/load.
    /// </summary>
    /// <param name="normalizedAduScale">ADU full-scale to rescale a NORMALISED foreign subtractive
    /// master ([0,1]) back into, so it can be subtracted from raw-ADU lights. Derived by the caller
    /// from the lights' storage bit depth (Int16 -&gt; 65535); null when it can't be determined (a
    /// float light), in which case a normalised subtractive master is skipped rather than
    /// mis-subtracted. Ignored for raw builds and for flats (divisive, re-normalised to mean~1).</param>
    public Task<Image?> GetOrBuildAsync(
        MasterGroupKey key, CalibrationResolver.CalTrain train, IReadOnlyList<FrameInfo> inputs,
        bool isMaster = false, float? normalizedAduScale = null, CancellationToken cancellationToken = default)
        => _inFlight.GetOrAdd((key, train, isMaster), _ => isMaster
            ? LoadForeignMasterAsync(key, inputs, normalizedAduScale, cancellationToken)
            : BuildOrLoadAsync(key, train, inputs, cancellationToken));

    private async Task<Image?> BuildOrLoadAsync(
        MasterGroupKey key, CalibrationResolver.CalTrain train, IReadOnlyList<FrameInfo> inputs, CancellationToken ct)
    {
        if (inputs.Count < 2)
        {
            logger?.LogWarning("Calibration group {Slug} has only {Count} frame(s) — skipped (need >= 2).", key.Slug(), inputs.Count);
            return null;
        }

        Directory.CreateDirectory(mastersDir);
        // The train suffix keeps two same-sensor cameras' masters on distinct paths (empty for a
        // header-less archive, preserving the legacy filename).
        var masterPath = Path.Combine(mastersDir, $"master_{key.Slug()}{train.SlugSuffix()}.fits");
        var fingerprint = ComputeFingerprint(inputs);

        if (File.Exists(masterPath))
        {
            if (ReadFingerprint(masterPath) == (fingerprint, inputs.Count)
                && Image.TryReadFitsFile(masterPath, out var cached) && cached is not null)
            {
                logger?.LogInformation("  master {File} cache hit ({Count} inputs)", Path.GetFileName(masterPath), inputs.Count);
                return cached;
            }
            logger?.LogInformation("  master {File} stale (input set changed) — rebuilding", Path.GetFileName(masterPath));
        }

        var master = key.Type switch
        {
            FrameType.Bias => await MasterFrameBuilder.BuildBiasMasterAsync(inputs, ct),
            FrameType.Dark or FrameType.DarkFlat => await MasterFrameBuilder.BuildDarkMasterAsync(inputs, ct),
            FrameType.Flat => await MasterFrameBuilder.BuildFlatMasterAsync(inputs, ct),
            _ => throw new ArgumentException($"Not a calibration frame type: {key.Type}", nameof(key)),
        };
        var extraHeaders = new Dictionary<string, (object Value, string Comment)>
        {
            [FingerprintCard] = (fingerprint, "TianWen dataset input-set fingerprint"),
            [InputCountCard] = (inputs.Count, "TianWen dataset input frame count"),
        };
        master.WriteToFitsFile(masterPath, null, extraHeaders);
        logger?.LogInformation("  master {File} built ({Count} inputs)", Path.GetFileName(masterPath), inputs.Count);
        return master;
    }

    /// <summary>A loaded subtractive master whose maximum is at/below this is treated as normalised
    /// to [0,1] rather than raw ADU (a real ADU dark/bias tops out in the hundreds-to-full-scale
    /// range; a normalised one near 1.0), and is rescaled to ADU before use; above it, it is used
    /// as-is.</summary>
    private const float NormalizedMaxThreshold = 1.5f;

    /// <summary>
    /// Loads a FOREIGN, already-integrated master (IMAGETYP=MASTERDARK / MASTERFLAT / MASTERBIAS —
    /// e.g. Astro Pixel Processor's MD-IG / MF-IG files) directly from disk, with no &gt;=2-raw median
    /// rebuild because the frame already IS the master. This is the ONLY path that ingests a master
    /// the pipeline did not itself produce, and it exists because a camera's proper calibration
    /// library sometimes survives only as a master (the raw subs long since deleted) — the difference
    /// between calibrating those sessions and skipping them. Several identical copies of one master
    /// can coexist (a master reused across sessions), so the ordinally-first path is chosen and the
    /// pick never depends on enumeration order.
    /// <para>Scale reconciliation, by frame type — APP stores masters normalised to [0,1]:</para>
    /// <list type="bullet">
    /// <item><b>Dark / bias</b> (subtractive, <c>light - dark</c>): must share the light's ADU domain,
    /// so a normalised master (max &lt;= <see cref="NormalizedMaxThreshold"/>) is rescaled by
    /// <paramref name="normalizedAduScale"/> (the lights' storage full-scale, derived by the caller)
    /// back to ADU; an already-ADU master is used as-is. A normalised master with no known scale
    /// (<paramref name="normalizedAduScale"/> null) is skipped rather than mis-subtracted.</item>
    /// <item><b>Flat</b> (divisive, <c>light / flat</c>, scale-invariant but the <see cref="Calibrator"/>
    /// wants mean ~ 1.0): re-normalised through the raw flat builder's own routine, so a foreign flat
    /// of any input scale behaves like a built one (the scale argument is irrelevant here).</item>
    /// </list>
    /// The loaded/rescaled range is logged so the ADU domain is visible in the bake log.
    /// </summary>
    private async Task<Image?> LoadForeignMasterAsync(
        MasterGroupKey key, IReadOnlyList<FrameInfo> inputs, float? normalizedAduScale, CancellationToken ct)
    {
        if (inputs.Count == 0)
        {
            return null;
        }
        var chosen = inputs.OrderBy(f => f.Path, StringComparer.Ordinal).First();
        var master = await Task.Run(() => Image.TryReadFitsFile(chosen.Path, out var img) ? img : null, ct);
        if (master is null)
        {
            logger?.LogWarning("  master {File} could not be read — skipped.", Path.GetFileName(chosen.Path));
            return null;
        }

        // Flats are divisive and scale-invariant, but the Calibrator expects mean ~ 1.0. A foreign
        // flat's scale is arbitrary ([0,1] APP export, or ADU), so put it through the SAME Bayer-aware
        // mean=1 normalisation the raw flat builder uses -- one path, so it behaves like a built flat.
        if (key.Type is FrameType.Flat)
        {
            MasterFrameBuilder.NormalizeFlatInPlace(master);
            logger?.LogInformation(
                "  master {File} loaded directly (foreign flat master, normalised to mean~1, {Count} copy/copies)",
                Path.GetFileName(chosen.Path), inputs.Count);
            return master;
        }

        // Dark / bias are subtractive (light - dark), so the master MUST be in the light's ADU domain.
        // A tool-normalised master ([0,1]) would subtract essentially nothing; rescale it to ADU using
        // the lights' storage full-scale (derived, not baked). No known scale -> can't reconcile, skip.
        if (master.MaxValue <= NormalizedMaxThreshold)
        {
            if (normalizedAduScale is not { } scale)
            {
                logger?.LogWarning(
                    "  master {File} is normalised to [0,1] (max={Max:F3}) but the lights' ADU full-scale is unknown (float light?) — skipped (can't reconcile scale for subtraction).",
                    Path.GetFileName(chosen.Path), master.MaxValue);
                master.Release();
                return null;
            }
            var rescaled = RescaleInPlace(master, scale);
            logger?.LogInformation(
                "  master {File} loaded directly (foreign {Type} master, normalised -> rescaled x{Scale:F0} to ADU, range [{Min:F1}, {Max:F1}], {Count} copy/copies)",
                Path.GetFileName(chosen.Path), key.Type, scale, rescaled.MinValue, rescaled.MaxValue, inputs.Count);
            return rescaled;
        }

        logger?.LogInformation(
            "  master {File} loaded directly (foreign {Type} master, raw ADU, range [{Min:F1}, {Max:F1}], {Count} copy/copies)",
            Path.GetFileName(chosen.Path), key.Type, master.MinValue, master.MaxValue, inputs.Count);
        return master;
    }

    /// <summary>Multiplies every pixel by <paramref name="scale"/> in place (the loaded master is
    /// ours, not camera-buffer-backed) and rewraps it so the reported min/max track the ADU data.</summary>
    private static Image RescaleInPlace(Image master, float scale)
    {
        var (channelCount, _, _) = master.Shape;
        var data = new float[channelCount][,];
        for (var c = 0; c < channelCount; c++)
        {
            var channel = master.GetChannelArray(c);
            var span = MemoryMarshal.CreateSpan(ref channel[0, 0], channel.Length);
            for (var i = 0; i < span.Length; i++)
            {
                span[i] *= scale;
            }
            data[c] = channel;
        }
        return new Image(data, BitDepth.Float32, master.MaxValue * scale, master.MinValue * scale, master.Pedestal, master.ImageMeta);
    }

    /// <summary>
    /// SHA-256 digest (first 16 hex chars) of the sorted input identities. Uses each frame's
    /// path + DATE-OBS: a changed library (a night added/removed) changes the DATE-OBS set and
    /// so the digest, while a pure re-run over the same files reproduces it exactly.
    /// </summary>
    internal static string ComputeFingerprint(IReadOnlyList<FrameInfo> inputs)
    {
        var ids = inputs
            .Select(f => $"{Path.GetFileName(f.Path)}|{f.Meta.ExposureStartTime.UtcDateTime:O}")
            .OrderBy(s => s, StringComparer.Ordinal);
        var sb = new StringBuilder();
        foreach (var id in ids)
        {
            sb.Append(id).Append('\n');
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexStringLower(hash)[..16];
    }

    private static (string Fingerprint, int Count)? ReadFingerprint(string masterPath)
    {
        try
        {
            using var reader = new BufferedFile(masterPath, FileAccess.Read, FileShare.Read, 4 * 2880);
            using var fits = new Fits(reader, masterPath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase));
            var header = fits.ReadHDUHeaderOnly()?.Header;
            var fingerprint = header?.GetStringValue(FingerprintCard);
            if (fingerprint is null)
            {
                return null;
            }
            var count = header!.GetIntValue(InputCountCard, -1);
            return (fingerprint, count);
        }
        catch
        {
            return null; // unreadable / not a TianWen master -> treat as a miss, rebuild
        }
    }
}
