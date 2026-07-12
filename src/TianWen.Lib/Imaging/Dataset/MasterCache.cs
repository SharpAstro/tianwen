using Microsoft.Extensions.Logging;
using nom.tam.fits;
using nom.tam.util;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
/// on a slug match. Foreign masters (PixInsight XISF, hand-processed FITS) are never ingested —
/// masters are always built from raw calibration frames whose provenance we control.
/// </summary>
/// <param name="mastersDir">Shared cache directory (created if missing). Lives under the build
/// output root, e.g. <c>&lt;out&gt;/masters</c>.</param>
public sealed class MasterCache(string mastersDir, ILogger? logger = null)
{
    private const string FingerprintCard = "DSETFPR"; // dataset input-set fingerprint (<= 8 chars)
    private const string InputCountCard = "DSETNIN";  // dataset input frame count

    // Keyed by (sensor-config group, optical train): two cameras that share a sensor model produce
    // the same MasterGroupKey, so without the train in the key a second body's build would either
    // cross-serve the first's cached Task (in-flight map) or overwrite its file (same slug) --
    // silently calibrating one camera's lights with another's master.
    private readonly ConcurrentDictionary<(MasterGroupKey Key, CalibrationResolver.CalTrain Train), Task<Image?>> _inFlight = new();

    /// <summary>
    /// Returns the master for a calibration group, building it if the on-disk cache is missing
    /// or fingerprint-stale, else loading the cached file. Returns null when the group has too
    /// few frames to combine (&lt; 2). Concurrent calls for the same key + train share one build.
    /// </summary>
    public Task<Image?> GetOrBuildAsync(
        MasterGroupKey key, CalibrationResolver.CalTrain train, IReadOnlyList<FrameInfo> inputs, CancellationToken cancellationToken = default)
        => _inFlight.GetOrAdd((key, train), _ => BuildOrLoadAsync(key, train, inputs, cancellationToken));

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
