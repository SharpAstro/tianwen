using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Core document model for the FITS viewer. Manages the image lifecycle:
/// loading, debayering, stretching, channel extraction, plate solving,
/// and conversion to display-ready RGBA pixels.
/// </summary>
public sealed class FitsDocument
{
    private readonly string _filePath;

    /// <summary>Pre-allocated buffer for stretch output, reused across stretch calls to avoid allocation.</summary>
    private float[][,]? _displayBuffer;

    /// <summary>Raw image as loaded from the FITS file.</summary>
    public Image RawImage { get; }

    /// <summary>Debayered image (or same as <see cref="RawImage"/> if not Bayer).</summary>
    public Image DebayeredImage { get; private set; }

    /// <summary>Currently stretched image for display.</summary>
    public Image DisplayImage { get; private set; }

    /// <summary>WCS solution, available after plate solving.</summary>
    public WCS? Wcs { get; private set; }

    /// <summary>Per-channel statistics computed from the raw image.</summary>
    public ImageHistogram[] ChannelStatistics { get; }

    /// <summary>Current debayer algorithm used.</summary>
    public DebayerAlgorithm DebayerAlgorithm { get; private set; }

    public bool IsPlateSolved => Wcs?.HasCDMatrix == true;

    private FitsDocument(string filePath, Image rawImage)
    {
        _filePath = filePath;
        RawImage = rawImage;

        // Normalize to 0..1 so all downstream processing and display works in unit range.
        var normalized = rawImage.ScaleFloatValuesToUnit();
        DebayeredImage = normalized;
        DisplayImage = normalized;
        DebayerAlgorithm = DebayerAlgorithm.None;

        var stats = new ImageHistogram[rawImage.ChannelCount];
        for (var c = 0; c < rawImage.ChannelCount; c++)
        {
            stats[c] = rawImage.Statistics(c);
        }
        ChannelStatistics = stats;
    }

    /// <summary>
    /// Loads a FITS file and creates a new <see cref="FitsDocument"/>.
    /// </summary>
    public static FitsDocument? Open(string filePath)
    {
        if (Image.TryReadFitsFile(filePath, out var image) && image is not null)
        {
            return new FitsDocument(filePath, image);
        }
        return null;
    }

    /// <summary>
    /// Applies debayering with the specified algorithm. No-op if not a Bayer image.
    /// </summary>
    public async Task ApplyDebayerAsync(DebayerAlgorithm algorithm, CancellationToken cancellationToken = default)
    {
        if (RawImage.ImageMeta.SensorType is not SensorType.RGGB)
        {
            DebayeredImage = RawImage.ScaleFloatValuesToUnit();
            DebayerAlgorithm = DebayerAlgorithm.None;
            return;
        }

        if (algorithm is DebayerAlgorithm.None)
        {
            DebayeredImage = RawImage.ScaleFloatValuesToUnit();
            DebayerAlgorithm = DebayerAlgorithm.None;
            return;
        }

        DebayeredImage = (await RawImage.DebayerAsync(algorithm, cancellationToken)).ScaleFloatValuesToUnit();
        DebayerAlgorithm = algorithm;
    }

    /// <summary>
    /// Applies stretch to the debayered image and updates <see cref="DisplayImage"/>.
    /// Reuses a pre-allocated buffer to avoid large allocations on repeated stretch calls.
    /// </summary>
    public async Task ApplyStretchAsync(StretchMode mode, StretchParameters parameters, CancellationToken cancellationToken = default)
    {
        if (mode is StretchMode.None)
        {
            DisplayImage = DebayeredImage;
            return;
        }

        var (channelCount, width, height) = DebayeredImage.Shape;

        // Allocate or reuse the display buffer
        if (_displayBuffer is null
            || _displayBuffer.Length != channelCount
            || _displayBuffer[0].GetLength(0) != height
            || _displayBuffer[0].GetLength(1) != width)
        {
            _displayBuffer = Image.CreateChannelData(channelCount, height, width);
        }

        DisplayImage = mode switch
        {
            StretchMode.Linked => await DebayeredImage.StretchLinkedIntoAsync(_displayBuffer, parameters.Factor, parameters.ShadowsClipping, cancellationToken),
            StretchMode.Unlinked => await DebayeredImage.StretchUnlinkedIntoAsync(_displayBuffer, parameters.Factor, parameters.ShadowsClipping, cancellationToken),
            StretchMode.Luma => await DebayeredImage.StretchLumaIntoAsync(_displayBuffer, parameters.Factor, parameters.ShadowsClipping, cancellationToken),
            _ => DebayeredImage
        };
    }

    /// <summary>
    /// Plate-solves the image using the provided factory.
    /// </summary>
    public async Task<bool> PlateSolveAsync(IPlateSolverFactory solverFactory, CancellationToken cancellationToken = default)
    {
        var imageDim = RawImage.GetImageDim();
        var result = await solverFactory.SolveFileAsync(_filePath, imageDim, cancellationToken: cancellationToken);
        if (result.Solution is { } wcs)
        {
            Wcs = wcs;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Gets pixel information at the given display coordinates, including sky coordinates if plate-solved.
    /// Coordinates are 0-based pixel positions in the raw image.
    /// </summary>
    public PixelInfo GetPixelInfo(int x, int y)
    {
        var image = DisplayImage;
        if (x < 0 || x >= image.Width || y < 0 || y >= image.Height)
        {
            return new PixelInfo(x, y, [], null, null);
        }

        var values = new float[image.ChannelCount];
        for (var c = 0; c < image.ChannelCount; c++)
        {
            values[c] = image[c, y, x];
        }

        double? ra = null, dec = null;
        if (Wcs is { } wcs)
        {
            // PixelToSky expects 1-based FITS coordinates
            var sky = wcs.PixelToSky(x + 1, y + 1);
            if (sky.HasValue)
            {
                ra = sky.Value.RA;
                dec = sky.Value.Dec;
            }
        }

        return new PixelInfo(x, y, values, ra, dec);
    }

    /// <summary>
    /// Extracts per-channel float arrays from <see cref="DisplayImage"/> for GPU upload as R32f textures.
    /// Returns 1 channel for mono/single-channel view, 3 channels for RGB composite.
    /// Each array is a flat height*width float span copied from the image data.
    /// </summary>
    public float[][] GetChannelArrays(ChannelView channelView)
    {
        var image = DisplayImage;
        var (channelCount, _, _) = image.Shape;

        if (channelView is ChannelView.Composite && channelCount >= 3)
        {
            // RGB composite — return 3 channel planes
            return
            [
                image.GetChannelSpan(0).ToArray(),
                image.GetChannelSpan(1).ToArray(),
                image.GetChannelSpan(2).ToArray(),
            ];
        }

        // Single channel (mono, or specific channel view)
        var ch = channelView switch
        {
            ChannelView.Red or ChannelView.Channel0 => 0,
            ChannelView.Green or ChannelView.Channel1 => 1,
            ChannelView.Blue or ChannelView.Channel2 => 2,
            ChannelView.Channel3 => 3,
            _ => 0
        };

        if (ch >= channelCount)
        {
            ch = 0;
        }

        return [image.GetChannelSpan(ch).ToArray()];
    }
}
