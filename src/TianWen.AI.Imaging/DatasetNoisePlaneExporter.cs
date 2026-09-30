using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Degradation;

namespace TianWen.AI.Imaging;

/// <summary>
/// E16's per-pixel conditioning planes for a bake's REAL frames: the session master and its two half-masters,
/// whose P0 tiles the eval caches are prepared from (docs/plans/denoiser-training.md, run log "Detail kept").
///
/// <para>The degrade export writes a plane beside each injected draw with the calibration KNOWN. A real frame's
/// noise is not known, so here it is ESTIMATED from the session's retained linear master
/// (<see cref="StretchedNoise.EstimateCalibration"/>), exactly what a runner has at inference, and every plane goes
/// through the same <see cref="StretchedNoise.Plane"/>: a model scored on these planes is scored on the
/// conditioning the product will give it.</para>
///
/// <para><b>Two approximations, stated.</b> A half-master's tile was stretched with ITS OWN parameters, which are
/// not retained; the master's stand in (same sky, same median, a floor a little lower). And a half holds half the
/// subs, so its noise is taken as the master's times sqrt 2.</para>
///
/// <para>The planes are written under <see cref="Options.OutRoot"/>, mirroring the bake's tile paths by
/// <see cref="DatasetDegradationExporter.SigmaPathFor"/>, never into the bake: the trainer's <c>--prepare
/// --sigma-root</c> reads them from there.</para>
/// </summary>
public static class DatasetNoisePlaneExporter
{
    /// <summary>The frames a plane is written for, by default: the master and both halves.</summary>
    public static ImmutableArray<string> DefaultFrames { get; } =
        [DatasetTileExporter.FrameMaster, DatasetTileExporter.FrameHalfMasterA, DatasetTileExporter.FrameHalfMasterB];

    /// <summary>What to write.</summary>
    /// <param name="BakeRoot">A dataset bake: <c>tiles-manifest.jsonl</c>, <c>tiles/</c> and <c>session-masters/</c>.</param>
    /// <param name="OutRoot">Where the planes go, mirroring the bake's tile paths.</param>
    /// <param name="SessionFilters">Case-insensitive substrings of the session id; empty takes every session.</param>
    /// <param name="Frames">Which frames get a plane (<see cref="DefaultFrames"/>).</param>
    public sealed record Options(string BakeRoot, string OutRoot, ImmutableArray<string> SessionFilters, ImmutableArray<string> Frames);

    /// <summary>One session's estimate and what was written.</summary>
    /// <param name="SessionId">The session.</param>
    /// <param name="Tiles">Planes written.</param>
    /// <param name="MasterSigma">The master's estimated noise at the background per channel, linear, unit range.</param>
    /// <param name="Background">The background level each channel's estimate anchored at, linear, unit range.</param>
    /// <param name="SkyPlaneMaster">The plane's value, in its own units, over a flat sky at the stretch's target
    /// median, for the master: the number to hold against a measured one.</param>
    /// <param name="SkyPlaneHalf">The same for a half-master.</param>
    public sealed record SessionResult(string SessionId, int Tiles, ImmutableArray<double> MasterSigma, ImmutableArray<double> Background, double SkyPlaneMaster, double SkyPlaneHalf);

    /// <summary>Writes the planes, one session at a time.</summary>
    public static async Task<ImmutableArray<SessionResult>> RunAsync(Options options, ILogger? logger, CancellationToken cancellationToken)
    {
        var manifest = Path.Combine(options.BakeRoot, DatasetTileExporter.ManifestFileName);
        var frames = options.Frames.IsDefaultOrEmpty ? DefaultFrames : options.Frames;
        var bySession = new Dictionary<string, List<DatasetTileExporter.TileManifestRow>>(StringComparer.Ordinal);
        await foreach (var line in File.ReadLinesAsync(manifest, cancellationToken))
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }
            DatasetTileExporter.TileManifestRow? row;
            try
            {
                row = JsonSerializer.Deserialize(line, DatasetManifestJsonContext.Default.TileManifestRow);
            }
            catch (JsonException)
            {
                continue;
            }
            if (row is null || !frames.Contains(row.Frame, StringComparer.Ordinal))
            {
                continue;
            }
            if (options.SessionFilters.Length > 0
                && !options.SessionFilters.Any(f => row.SessionId.Contains(f, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            if (!bySession.TryGetValue(row.SessionId, out var rows))
            {
                bySession[row.SessionId] = rows = [];
            }
            rows.Add(row);
        }

        var results = ImmutableArray.CreateBuilder<SessionResult>();
        foreach (var (sessionId, rows) in bySession.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!RetainedMasterStore.TryRead(options.BakeRoot, sessionId, out var master, logger))
            {
                logger?.LogWarning("[noise-planes] {Session}: no retained master, skipped", sessionId);
                continue;
            }
            Image? unit = null;
            try
            {
                unit = DatasetTileExporter.ToUnitRange(master);
                var absent = unit.AbsentPixels();
                var (origMin, balances) = unit.MtfStretchParameters(AiNafnetInputs.TargetMedian, absent);
                var stretches = new StretchedNoise.ChannelStretch[origMin.Length];
                for (var c = 0; c < stretches.Length; c++)
                {
                    stretches[c] = new StretchedNoise.ChannelStretch(balances[c], origMin[c]);
                }
                var calibrations = StretchedNoise.EstimateCalibration(unit, stretches, absent);

                var written = 0;
                foreach (var row in rows)
                {
                    var depth = row.Frame == DatasetTileExporter.FrameMaster ? 1.0 : Math.Sqrt(2.0);
                    var channels = ReadTile(Path.Combine(options.BakeRoot, row.Tile.Replace('/', Path.DirectorySeparatorChar)), row.Channels, row.TileSize);
                    var plane = StretchedNoise.Plane(channels, row.TileSize, row.TileSize, stretches, calibrations, depth);
                    var target = Path.Combine(options.OutRoot, DatasetDegradationExporter.SigmaPathFor(row.Tile).Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target) ?? options.OutRoot);
                    DatasetDegradationExporter.WritePlaneFile(plane, target);
                    written++;
                }

                var result = new SessionResult(sessionId, written, [.. calibrations.Select(c => c.OneSubSigmaAdu)], [.. calibrations.Select(c => c.BackgroundAdu)],
                    SkyPlane(stretches, calibrations, 1.0), SkyPlane(stretches, calibrations, Math.Sqrt(2.0)));
                logger?.LogInformation(
                    "[noise-planes] {Session}: {Tiles} planes; master sigma {Sigma} at background {Background}; sky plane master {Master:F3}, half {Half:F3}",
                    sessionId, written, PerChannel(result.MasterSigma), PerChannel(result.Background), result.SkyPlaneMaster, result.SkyPlaneHalf);
                results.Add(result);
            }
            finally
            {
                if (unit is not null && !ReferenceEquals(unit, master))
                {
                    unit.Release();
                }
                master.Release();
            }
        }
        return results.ToImmutable();
    }

    /// <summary>One value per channel, as the log and the CLI print it.</summary>
    public static string PerChannel(ImmutableArray<double> values)
        => string.Join('/', values.Select(v => v.ToString("E3", CultureInfo.InvariantCulture)));

    /// <summary>The plane over a flat sky at the stretch's target median in every channel, in plane units.</summary>
    private static double SkyPlane(IReadOnlyList<StretchedNoise.ChannelStretch> stretches, IReadOnlyList<LinearDegradation.NoiseCalibration> calibrations, double depth)
    {
        var sumSq = 0.0;
        for (var c = 0; c < stretches.Count; c++)
        {
            var v = StretchedNoise.SigmaAt(AiNafnetInputs.TargetMedian, stretches[c], calibrations[c], depth);
            sumSq += v * v;
        }
        return StretchedNoise.PlaneScale * Math.Sqrt(sumSq) / stretches.Count;
    }

    private static float[][] ReadTile(string path, int channels, int size)
    {
        var bytes = File.ReadAllBytes(path);
        var expected = channels * size * size * 2;
        if (bytes.Length != expected)
        {
            throw new InvalidDataException($"{path} is {bytes.Length} bytes, expected {expected}");
        }
        var halfs = MemoryMarshal.Cast<byte, Half>(bytes.AsSpan());
        var planes = new float[channels][];
        for (var c = 0; c < channels; c++)
        {
            var p = new float[size * size];
            var src = halfs.Slice(c * size * size, size * size);
            for (var i = 0; i < p.Length; i++)
            {
                p[i] = (float)src[i];
            }
            planes[c] = p;
        }
        return planes;
    }
}
