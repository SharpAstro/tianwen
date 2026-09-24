using System;
using System.Runtime.InteropServices;

namespace TianWen.Lib.Imaging;

/// <summary>
/// Precomputes display-ready histogram bins from raw <see cref="ImageHistogram"/> data,
/// with optional stretch remapping and log/linear peak tracking.
/// </summary>
public sealed class HistogramDisplay
{
    /// <summary>Number of bins in the display histogram.</summary>
    public const int BinCount = 512;

    // Grow-only: a row holds at least RawBinCount bins, and only the first RawBinCount of it are in use.
    // A raw frame's bin count is its peak plus one, which moves on almost every exposure, so a buffer
    // sized exactly to each frame was a new 256 KB a channel per exposure (see Refresh).
    private float[,] _rawBins;
    private readonly float[,] _displayBins;

    /// <summary>Number of channels.</summary>
    public int ChannelCount { get; }

    /// <summary>Number of full-resolution bins per channel.</summary>
    public int RawBinCount { get; private set; }

    /// <summary>Peak of log(1 + binCount) across all display bins and channels.</summary>
    public float LogPeak { get; private set; }

    /// <summary>Peak raw bin count across all display bins and channels.</summary>
    public float LinearPeak { get; private set; }

    /// <summary>Zero-copy display bins for the given channel (length = <see cref="BinCount"/>).</summary>
    public ReadOnlySpan<float> GetDisplayBins(int channel)
        => MemoryMarshal.CreateReadOnlySpan(ref _displayBins[channel, 0], BinCount);

    public HistogramDisplay(ImageHistogram[] channelStatistics)
    {
        ChannelCount = Math.Min(channelStatistics.Length, 3);
        RawBinCount = ChannelCount > 0 ? channelStatistics[0].Histogram.Length : 0;
        _rawBins = new float[ChannelCount, RawBinCount];
        _displayBins = new float[ChannelCount, BinCount];
        CopyRawBins(channelStatistics);
    }

    /// <summary>
    /// Takes the raw bins of new statistics with the same channel count, IN PLACE: the bin count may
    /// change (a raw frame's is its peak plus one), and the buffer grows only when a frame needs more
    /// bins than any before it. Call <see cref="Recompute"/> afterwards to regenerate the display bins.
    /// </summary>
    /// <remarks>
    /// For a live feed, whose statistics are new on every exposure. Rebuilding the display per exposure
    /// instead cost a raw buffer the size of the frame's histogram each time, 256 KB a channel for a
    /// 16-bit sensor, because the bin count moved with the peak and so never matched the last one.
    /// </remarks>
    /// <exception cref="ArgumentException">The statistics have a different channel count; build a new
    /// display for those.</exception>
    public void Refresh(ImageHistogram[] channelStatistics)
    {
        var channels = Math.Min(channelStatistics.Length, 3);
        if (channels != ChannelCount)
        {
            throw new ArgumentException($"A display of {ChannelCount} channels cannot take {channels}; build a new one.", nameof(channelStatistics));
        }

        RawBinCount = channels > 0 ? channelStatistics[0].Histogram.Length : 0;
        if (RawBinCount > _rawBins.GetLength(1))
        {
            _rawBins = new float[ChannelCount, RawBinCount];
        }

        CopyRawBins(channelStatistics);
    }

    // Row c's first RawBinCount entries from channel c's histogram; what lies past them is never read.
    // A channel shorter than channel 0 (which sets the count) reads as empty past its end, never as the
    // previous exposure's counts.
    private void CopyRawBins(ImageHistogram[] channelStatistics)
    {
        for (var c = 0; c < ChannelCount; c++)
        {
            var hist = channelStatistics[c].Histogram;
            var count = Math.Min(hist.Length, RawBinCount);
            var i = 0;
            for (; i < count; i++)
            {
                _rawBins[c, i] = hist[i];
            }
            for (; i < RawBinCount; i++)
            {
                _rawBins[c, i] = 0f;
            }
        }
    }

    /// <summary>
    /// Refreshes one channel's raw bins from the current frame's normalized <c>[0,1]</c> pixel data, in
    /// place (no allocation). Used for per-frame playback histograms: the display tracks the current
    /// frame while the cached stretch statistics (median/MAD -> shadows/midtones) stay fixed. Geometry
    /// (channel count, bin count) is unchanged; only the counts are recomputed. Call <see cref="Recompute"/>
    /// afterwards to regenerate the display bins.
    /// </summary>
    public void UpdateRawBins(int channel, ReadOnlySpan<float> normalized)
    {
        if ((uint)channel >= (uint)ChannelCount || RawBinCount <= 0)
        {
            return;
        }

        var bins = MemoryMarshal.CreateSpan(ref _rawBins[channel, 0], RawBinCount);
        bins.Clear();
        var scale = RawBinCount - 1;
        foreach (var v in normalized)
        {
            var idx = (int)(Math.Clamp(v, 0f, 1f) * scale);
            bins[idx] += 1f;
        }
    }

    /// <summary>
    /// Recomputes display bins. When stretch is off, downsamples raw bins by summing groups.
    /// When stretch is on, applies the stretch function to each full-resolution bin and
    /// accumulates into display bins.
    /// </summary>
    public void Recompute(
        StretchMode stretchMode,
        float normFactor,
        (float R, float G, float B) pedestals,
        (float R, float G, float B) shadows,
        (float R, float G, float B) midtones,
        (float R, float G, float B) rescales)
    {
        LogPeak = 0f;
        LinearPeak = 0f;

        ReadOnlySpan<float> pedArr = [pedestals.R, pedestals.G, pedestals.B];
        ReadOnlySpan<float> shadArr = [shadows.R, shadows.G, shadows.B];
        ReadOnlySpan<float> midArr = [midtones.R, midtones.G, midtones.B];
        ReadOnlySpan<float> resArr = [rescales.R, rescales.G, rescales.B];

        for (var c = 0; c < ChannelCount; c++)
        {
            // Clear display bins
            MemoryMarshal.CreateSpan(ref _displayBins[c, 0], BinCount).Clear();

            if (stretchMode is StretchMode.None)
            {
                // No stretch: downsample by summing groups
                var binsPerGroup = (float)RawBinCount / BinCount;
                for (var i = 0; i < BinCount; i++)
                {
                    var start = (int)(i * binsPerGroup);
                    var end = (int)((i + 1) * binsPerGroup);
                    if (end > RawBinCount) end = RawBinCount;
                    var sum = 0f;
                    for (var j = start; j < end; j++)
                    {
                        sum += _rawBins[c, j];
                    }
                    _displayBins[c, i] = sum;
                }
            }
            else
            {
                // Apply stretch to each full-resolution bin, accumulate into display bins
                var invRawLen = 1f / RawBinCount;
                for (var i = 0; i < RawBinCount; i++)
                {
                    var count = _rawBins[c, i];
                    if (count <= 0f) continue;

                    var rawVal = (i + 0.5f) * invRawLen;
                    var stretched = Image.StretchValue(rawVal, normFactor, pedArr[c], shadArr[c], midArr[c], resArr[c]);

                    var destBin = (int)(stretched * (BinCount - 1));
                    if (destBin >= 0 && destBin < BinCount)
                    {
                        _displayBins[c, destBin] += count;
                    }
                }
            }

            // Track peaks
            for (var i = 0; i < BinCount; i++)
            {
                var v = _displayBins[c, i];
                if (v > LinearPeak) LinearPeak = v;
                var logV = MathF.Log(1 + v);
                if (logV > LogPeak) LogPeak = logV;
            }
        }
    }
}
