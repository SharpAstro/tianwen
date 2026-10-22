using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
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

    public static async Task<Result> RunAsync(string bakeRoot, string platesRoot, IReadOnlyList<string> sessions, string outPath, ILogger? logger,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
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
            var row = StarShape.Measure(session, master, catalogue, field, master.AbsentPixels());
            await StarShape.AppendAsync(outPath, row, cancellationToken);
            measured++;
            progress?.Report(string.Create(CultureInfo.InvariantCulture,
                $"[star-shapes] {session}: {string.Join("; ", row.Bands.Select(static b => $"from {b.MinSignificance:F0}, {b.Stars} stars, gains {string.Join(" ", b.Channels.Select(static c => $"{c.WingGain:F2}"))}"))}"));
        }
        return new Result(measured, present, missing);
    }
}
