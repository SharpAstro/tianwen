using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Imaging;
using TianWen.Lib.IO;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Devices.Guider;

/// <summary>
/// A guide camera's dark library on disk (#1915): the frames <see cref="Sequencing.DarkFrameRun"/> took of it, found by
/// camera, and combined into the master darks (one per exposure, gain, and temperature) that <see cref="GuideDarkCorrection"/> applies.
/// </summary>
/// <remarks>
/// <para>The library sits where every dark does, under <c>Darks/</c> in the image folder, in <c>Guide/&lt;camera&gt;/&lt;date&gt;/</c>,
/// so the frames are ordinary FITS darks a stacker or a person can read; what makes them a guide camera's is the folder,
/// and what makes them a row of the library is the state in their headers (<c>EXPTIME</c>, <c>GAIN</c>, <c>CCD-TEMP</c>).</para>
/// <para>A correction is built once per folder state and kept until a file is added or changed, so a loop that asks at its
/// start pays for the read once. A guide dark library is small (a handful of frames per exposure of a small sensor).</para>
/// </remarks>
public sealed class GuideDarkLibrary(IExternal external, ILogger<GuideDarkLibrary> logger)
{
    /// <summary>Frames beyond this many per master are ignored (the newest are kept): a median is taken over them.</summary>
    public const int MaxFramesPerMaster = 32;

    private readonly ConcurrentDictionary<string, Cached> _cache = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Cached(long Stamp, GuideDarkCorrection? Correction);

    /// <summary>The folder under <c>Darks/</c> a guide camera's frames are written to, for <see cref="Sequencing.DarkFrameRunOptions.LibraryFolder"/>.</summary>
    public string LibraryFolderOf(string cameraName) => Path.Combine("Guide", external.GetSafeFileName(cameraName));

    /// <summary>The folder a guide camera's darks are read from.</summary>
    public string FolderOf(string cameraName) => Path.Combine(external.ImageOutputFolder.FullName, "Darks", LibraryFolderOf(cameraName));

    /// <summary>The correction for a guide camera, or null when it has no darks. Read again only when its folder changed.</summary>
    public GuideDarkCorrection? For(string cameraName)
    {
        var folder = FolderOf(cameraName);
        if (!Directory.Exists(folder))
        {
            return null;
        }

        var files = FileEnumeration.EnumerateFileStamps(folder, [".fits", ".fit"], recursive: true).ToList();
        var stamp = files.Count == 0 ? 0 : files.Aggregate(files.Count * 31L, (s, f) => HashCode.Combine(s, f.Length, f.LastWriteTimeUtc.Ticks, f.Path.Length));
        if (_cache.TryGetValue(folder, out var cached) && cached.Stamp == stamp)
        {
            return cached.Correction;
        }

        var correction = Build(files.Select(f => f.Path));
        _cache[folder] = new Cached(stamp, correction);
        logger.LogInformation("Guide darks of {Camera}: {Count} master(s), {Masked} bad pixel(s) masked", cameraName, correction?.DarkCount ?? 0, correction?.MaskedPixels ?? 0);
        return correction;
    }

    private GuideDarkCorrection? Build(IEnumerable<string> paths)
    {
        // Grouped from the HEADERS alone, by the state they were shot in: the size and channels (a bin 2 dark is no dark of a bin 1
        // frame), the exposure to the millisecond, the gain, and the temperature to 2 degrees. Only the frames a master uses are
        // then read, so a folder of many nights is never held in memory at once.
        var groups = new Dictionary<(int Width, int Height, int Channels, long ExposureMs, short Gain, int TempBin), List<(string Path, double TempC)>>();
        foreach (var path in paths.OrderBy(static p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (!Image.TryReadFitsHeader(path, out var info))
            {
                logger.LogWarning("Guide dark {Path} could not be read", path);
                continue;
            }
            var meta = info.Meta;
            if (meta.ExposureDuration <= TimeSpan.Zero)
            {
                continue;
            }
            var temp = double.IsFinite(meta.CCDTemperature) ? (int)Math.Round(meta.CCDTemperature / 2.0) : int.MinValue;
            var key = (info.Width, info.Height, info.ChannelCount, (long)Math.Round(meta.ExposureDuration.TotalMilliseconds), (short)meta.Gain, temp);
            if (!groups.TryGetValue(key, out var list))
            {
                groups[key] = list = [];
            }
            list.Add((path, meta.CCDTemperature));
        }

        var masters = ImmutableArray.CreateBuilder<GuideDark>();
        foreach (var (key, files) in groups)
        {
            // Paths sort by date folder, so the newest are last.
            var used = files.Count > MaxFramesPerMaster ? files[^MaxFramesPerMaster..] : files;
            var frames = new List<Image>(used.Count);
            try
            {
                foreach (var (path, _) in used)
                {
                    if (Image.TryReadFitsFile(path, out var image) && image is not null)
                    {
                        frames.Add(image);
                    }
                    else
                    {
                        logger.LogWarning("Guide dark {Path} could not be read", path);
                    }
                }
                if (frames.Count > 0 && Median(frames) is { } master)
                {
                    var temps = used.Select(static f => f.TempC).Where(double.IsFinite).ToList();
                    masters.Add(new GuideDark(TimeSpan.FromMilliseconds(key.ExposureMs), key.Gain, temps.Count == 0 ? double.NaN : temps.Average(), master));
                }
            }
            finally
            {
                foreach (var image in frames)
                {
                    image.Release();
                }
            }
        }
        return masters.Count == 0 ? null : GuideDarkCorrection.FromDarks(masters.ToImmutable());
    }

    /// <summary>The per-pixel median of frames of one shape (a frame of another shape than the first is left out).</summary>
    internal static Image? Median(List<Image> frames)
    {
        var first = frames[0];
        var same = frames.Where(f => f.Width == first.Width && f.Height == first.Height && f.ChannelCount == first.ChannelCount).ToList();
        var data = new float[first.ChannelCount][,];
        var scratch = new float[same.Count];
        for (var c = 0; c < data.Length; c++)
        {
            var planes = same.Select(f => f.GetChannelArray(c)).ToArray();
            var plane = new float[first.Height, first.Width];
            for (var y = 0; y < first.Height; y++)
            {
                for (var x = 0; x < first.Width; x++)
                {
                    for (var i = 0; i < planes.Length; i++)
                    {
                        scratch[i] = planes[i][y, x];
                    }
                    plane[y, x] = StatisticsHelper.MedianFast(scratch);
                }
            }
            data[c] = plane;
        }
        return new Image(data, first.BitDepth, first.MaxValue, 0f, first.Pedestal, first.ImageMeta, first.SamplesAreUnitReferred);
    }
}
