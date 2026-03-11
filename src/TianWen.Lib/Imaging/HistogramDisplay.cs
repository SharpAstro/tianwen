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

    private readonly float[,] _rawBins;
    private readonly float[,] _displayBins;

    /// <summary>Number of channels.</summary>
    public int ChannelCount { get; }

    /// <summary>Number of full-resolution bins per channel.</summary>
    public int RawBinCount { get; }

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

        for (var c = 0; c < ChannelCount; c++)
        {
            var hist = channelStatistics[c].Histogram;
            for (var i = 0; i < hist.Length; i++)
            {
                _rawBins[c, i] = hist[i];
            }
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
                // No stretch — downsample by summing groups
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
