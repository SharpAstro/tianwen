using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.IO;

namespace TianWen.Lib.Imaging.Grading;

/// <summary>What <see cref="ExpectedSkyFitter.FitAsync"/> found, and what it left out.</summary>
/// <param name="Entries">One fitted model per key with enough frames over enough nights.</param>
/// <param name="FilesSeen">Every FITS file under the folders.</param>
/// <param name="Measured">The frames whose sky was measured.</param>
/// <param name="Rejected">Left out by name (<c>BAD_</c>, <c>MAYBE_CLOUD_</c>).</param>
/// <param name="Twilight">Left out as taken in twilight.</param>
/// <param name="NotLights">Left out as no raw light: calibration frames, integrations, unreadable headers, a frame with no site, time or pointing.</param>
/// <param name="Unmeasured">Lights whose sky could not be read (too small a frame).</param>
/// <param name="TooFew">Keys that had frames but not enough (<see cref="ExpectedSkyFitter.MinFrames"/> over <see cref="ExpectedSkyFitter.MinNights"/> nights).</param>
public sealed record ExpectedSkyFitResult(ImmutableArray<ExpectedSkyEntry> Entries, int FilesSeen, int Measured, int Rejected, int Twilight,
    int NotLights, int Unmeasured, int TooFew);

/// <summary>
/// Fits an <see cref="ExpectedSkyModel"/> per site, rig, sensor, filter, exposure and bin from a tree of lights (#1974): reads
/// each header first to key and filter the frame, leaves out what its name marks rejected or suspected cloud and what was taken in
/// twilight, then reads the planes, measures <see cref="Image.SkySurface"/> and <see cref="SkyCovariates"/>, and releases the image.
/// The level is in the plane's own value space, which for a file read from disk is the frame's counts.
/// </summary>
public static class ExpectedSkyFitter
{
    /// <summary>The fewest measured frames a key needs.</summary>
    public const int MinFrames = 30;

    /// <summary>The fewest nights a key needs: one night alone cannot tell the sky from that night's transparency.</summary>
    public const int MinNights = 2;

    private sealed record Row(ExpectedSkyKey Key, DateOnly Night, SkySample Sample);

    /// <summary>Fits every key found under <paramref name="folders"/> (recursive); nothing is saved, <see cref="ExpectedSkyStore.SaveAsync"/> does that.</summary>
    public static async Task<ExpectedSkyFitResult> FitAsync(IReadOnlyList<string> folders, ILogger? logger, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folders);
        var files = new List<string>();
        foreach (var folder in folders)
        {
            files.AddRange(FileEnumeration.EnumerateFiles(folder, FitsFolderFrameSource.FitsExtensions, recursive: true));
        }
        files.Sort(StringComparer.OrdinalIgnoreCase);

        var rows = new ConcurrentBag<Row>();
        int rejected = 0, twilight = 0, notLights = 0, unmeasured = 0, done = 0;
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
            CancellationToken = cancellationToken,
        };
        await Parallel.ForEachAsync(files, options, async (path, ct) =>
        {
            await Task.Yield();
            try
            {
                if (CaptureRejection.IsRejectedAtCapture(path) || CaptureRejection.IsSuspectedCloud(path))
                {
                    Interlocked.Increment(ref rejected);
                    return;
                }
                if (!Image.TryReadFitsHeader(path, out var info) || info.FrameType != FrameType.Light || info.IsIntegration || info.IsMaster)
                {
                    Interlocked.Increment(ref notLights);
                    return;
                }
                var meta = info.Meta;
                if (meta.ExposureStartTime == default || !LightGrading.SiteKnown(meta) || !double.IsFinite(meta.TargetRA) || !double.IsFinite(meta.TargetDec))
                {
                    Interlocked.Increment(ref notLights);
                    return;
                }
                if (SolarAltitude.Twilight(meta.ExposureStartTime, meta.Latitude, meta.Longitude) is not null)
                {
                    Interlocked.Increment(ref twilight);
                    return;
                }
                if (!Image.TryReadFitsFile(path, out var image))
                {
                    Interlocked.Increment(ref notLights);
                    return;
                }
                SkySurface surface;
                try
                {
                    surface = image.SkySurface(image.ReferenceStarChannel);
                }
                finally
                {
                    image.Release();
                }
                if (!float.IsFinite(surface.Level) || !float.IsFinite(surface.GradientStrength))
                {
                    Interlocked.Increment(ref unmeasured);
                    return;
                }
                var key = new ExpectedSkyKey(LightGrading.RoundSite(meta.Latitude), LightGrading.RoundSite(meta.Longitude), LightGrading.RigOf(meta),
                    meta.SensorType, meta.Filter.Name is null ? string.Empty : meta.Filter.IdentityKey,
                    (long)Math.Round(meta.ExposureDuration.TotalMilliseconds), meta.BinX);
                rows.Add(new Row(key, LightGrading.NightOf(meta), new SkySample(SkyCovariates.From(meta), surface.Level, surface.GradientStrength)));
            }
            finally
            {
                var n = Interlocked.Increment(ref done);
                if (n % 200 == 0)
                {
                    progress?.Report($"[sky-model] {n} of {files.Count} files read");
                }
            }
        }).ConfigureAwait(false);

        var entries = new List<ExpectedSkyEntry>();
        var tooFew = 0;
        foreach (var group in rows.GroupBy(r => r.Key))
        {
            var samples = group.OrderBy(r => r.Sample.Covariates.HoursFromLocalMidnight).Select(r => r.Sample).ToList();
            var nights = group.Select(r => r.Night).Distinct().Count();
            var model = samples.Count >= MinFrames && nights >= MinNights ? ExpectedSkyModel.Fit(samples) : null;
            if (model is null)
            {
                tooFew++;
                logger?.LogInformation("Sky model: {Key} has {Frames} frames over {Nights} nights, not enough", group.Key.Describe(), samples.Count, nights);
                continue;
            }
            entries.Add(new ExpectedSkyEntry(group.Key, model, nights));
        }
        entries.Sort((a, b) => b.Model.FrameCount.CompareTo(a.Model.FrameCount));
        return new ExpectedSkyFitResult([.. entries], files.Count, rows.Count, rejected, twilight, notLights, unmeasured, tooFew);
    }
}
