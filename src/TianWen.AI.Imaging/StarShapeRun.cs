using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging.StarRemoval;

namespace TianWen.AI.Imaging;

/// <summary>
/// <c>tianwen dataset star-shapes</c>: each named session's star shape (<see cref="StarShape.Measure"/>) from its retained
/// master, its starless plate's catalogue and its field profile, one JSON line a master. Resumes: a session already in the
/// file is skipped, so a stop costs at most the master in hand.
/// </summary>
public static class StarShapeRun
{
    /// <summary>What a run did: masters measured, already present, and without a plate or a profile to measure against.</summary>
    public sealed record Result(int Measured, int Present, int Missing);

    /// <summary>
    /// What a run measures. <paramref name="BandEdges"/> are the brightness bands' lower edges by significance (empty:
    /// <see cref="StarShape.TrendBandEdges"/>, down to 20 sigma; <c>1000</c> alone is the one bright band every row held before
    /// the trend). <paramref name="PoolByRig"/> pools each camera's masters' slopes once the run is done
    /// (<see cref="StarShape.PoolSlopesPerRig"/>), each master keeping its own level at the bright band; off, every master draws
    /// its own, noisy slope. <paramref name="CorrectSkyBias"/> takes the blank sky's bias off every star's light
    /// (<see cref="StarShape.BlankField"/>).
    /// </summary>
    public sealed record Options(ImmutableArray<float> BandEdges = default, bool PoolByRig = true, bool CorrectSkyBias = true);

    public static async Task<Result> RunAsync(string bakeRoot, string platesRoot, IReadOnlyList<string> sessions, string outPath, Options options,
        ILogger? logger, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var edges = options.BandEdges.IsDefaultOrEmpty ? StarShape.TrendBandEdges : options.BandEdges;
        var platesDir = Path.Combine(platesRoot, "plates");
        var done = await StarShape.ReadAllAsync(outPath, cancellationToken);
        var (measured, present, missing) = (0, 0, 0);
        foreach (var session in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (done.ContainsKey(session))
            {
                present++;
                continue;
            }
            var stem = DatasetTileExporter.Sanitize(session);
            var cataloguePath = StarlessCatalogue.PathFor(platesDir, stem);
            var field = await StarlessFieldProfile.ReadAsync(StarlessFieldProfile.PathFor(platesDir, stem), cancellationToken);
            if (field is null || !File.Exists(cataloguePath) || !RetainedMasterStore.TryRead(bakeRoot, session, out var master, logger))
            {
                progress?.Report($"[star-shapes] {session}: no master, catalogue or field profile; skipped");
                missing++;
                continue;
            }
            var catalogue = await StarlessCatalogue.ReadAsync(cataloguePath, cancellationToken);
            var row = StarShape.Measure(session, master, catalogue, field, master.AbsentPixels(), edges, master.ImageMeta.Instrument.Trim(),options.CorrectSkyBias);
            await StarShape.AppendAsync(outPath, row, cancellationToken);
            measured++;
            progress?.Report(string.Create(CultureInfo.InvariantCulture,
                $"[star-shapes] {session} ({row.Rig}): {string.Join("; ", row.Bands.Select(static b => $"from {b.MinSignificance:F0}, {b.Stars} stars, gains {string.Join(" ", b.Channels.Select(static c => $"{c.WingGain:F2}"))}"))}; own slopes {string.Join(" ", row.LogSlopes.Select(static s => $"{s:F2}"))}"));
        }
        if (options.PoolByRig && measured + present > 0)
        {
            await PoolAsync(outPath, progress, cancellationToken);
        }
        return new Result(measured, present, missing);
    }

    // Replaces every row's slopes with its rig's pooled ones, through a temporary file renamed over the output so a stop never
    // leaves half a file. Pooling is a function of the bands alone, so running it again over a pooled file changes nothing.
    private static async Task PoolAsync(string outPath, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var rows = await StarShape.ReadAllAsync(outPath, cancellationToken);
        var pooled = StarShape.PoolSlopesPerRig(rows);
        var temporary = outPath + ".pooling";
        File.Delete(temporary);
        foreach (var row in pooled.Values)
        {
            await StarShape.AppendAsync(temporary, row, cancellationToken);
        }
        File.Move(temporary, outPath, overwrite: true);
        foreach (var rig in pooled.Values.Where(static r => r.Rig.Length > 0).GroupBy(static r => r.Rig).OrderBy(static g => g.Key, StringComparer.Ordinal))
        {
            progress?.Report(string.Create(CultureInfo.InvariantCulture,
                $"[star-shapes] rig {rig.Key}: {rig.Count()} masters, pooled slopes {string.Join(" ", rig.First().LogSlopes.Select(static s => $"{s:F2}"))}"));
        }
    }
}
