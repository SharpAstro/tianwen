using CommunityToolkit.HighPerformance;
using ImageMagick;
using nom.tam.fits;
using nom.tam.util;
using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Stat;
using static TianWen.Lib.Stat.StatisticsHelper;

namespace TianWen.Lib.Imaging;

public class Image(float[,,] data, BitDepth bitDepth, float maxValue, float blackLevel, ImageMeta imageMeta)
{
    public int Width
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        get;
    } = data.GetLength(2);

    public int Height
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        get;
    } = data.GetLength(1);

    public int ChannelCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        get;
    } = data.GetLength(0);

    public (int ChannelCount, int Width, int Height) Shape => (ChannelCount, Width, Height);

    public BitDepth BitDepth => bitDepth;
    public float MaxValue => maxValue;
    /// <summary>
    /// Black level or offset value, defaults to 0 if unknown
    /// </summary>
    public float BlackLevel => blackLevel;
    /// <summary>
    /// Image metadata such as instrument, exposure time, focal length, pixel size, ...
    /// </summary>
    public ImageMeta ImageMeta => imageMeta;

    /// <summary>
    /// Read-only indexer to get a pixel value.
    /// </summary>
    /// <param name="h"></param>
    /// <param name="w"></param>
    /// <returns></returns>
    public float this[int c, int h, int w] => data[c, h, w];

    /// <summary>
    /// Support reading image from disk (used for testing).
    /// </summary>
    /// <param name="stream"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="InvalidDataException">if not a valid image stream</exception>
    internal static async ValueTask<Image> FromStreamAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        using var magic = ArrayPoolHelper.Rent<byte>(sizeof(int));
        await stream.ReadExactlyAsync(magic, cancellationToken);

        if (magic[0] != (byte)'I' || magic[1] != (byte)'m')
        {
            throw new InvalidDataException("Stream does not have a valid file magic");
        }
        var dataIsLittleEndian = magic[2] == 'L';

        int headerIntSize;
        var ver = magic[3] - '0';
        if (ver is 1)
        {
            headerIntSize = 5;
        }
        else if (ver is 2)
        {
            await stream.ReadExactlyAsync(magic, cancellationToken);
            if (dataIsLittleEndian != BitConverter.IsLittleEndian)
            {
                magic.AsSpan(0).Reverse();
            }

            headerIntSize = BitConverter.ToInt32(magic);
        }
        else
        {
            throw new InvalidDataException($"Unsupported image version {ver}");
        }

        using var headers = ArrayPoolHelper.Rent<byte>(headerIntSize * sizeof(int));

        await stream.ReadExactlyAsync(headers, cancellationToken);

        if (dataIsLittleEndian != BitConverter.IsLittleEndian)
        {
            for (var i = 0; i < headerIntSize; i++)
            {
                headers.AsSpan(i * sizeof(int), sizeof(int)).Reverse();
            }
        }

        var ints = headers.AsMemory().Cast<byte, int>().ToArray();
        var width = ints[0];
        var height = ints[1];
        var bitDepth = (BitDepth)ints[2];
        var maxValue = BitConverter.Int32BitsToSingle(ints[3]);
        var blackLevel = BitConverter.Int32BitsToSingle(ints[4]);
        var channelCount = headerIntSize > 5 ? ints[5] : 1;

        var imageSize = channelCount * width * height;
        var dataSize = imageSize * sizeof(float);

        var byteData = new byte[dataSize];
        await stream.ReadExactlyAsync(byteData, cancellationToken);

        if (dataIsLittleEndian != BitConverter.IsLittleEndian)
        {
            for (var i = 0; i < imageSize; i++)
            {
                Array.Reverse(byteData, i * sizeof(float), sizeof(float));
            }
        }

        var data = new float[channelCount, height, width];
        Buffer.BlockCopy(byteData, 0, data, 0, byteData.Length);

        var imageMeta = await JsonSerializer.DeserializeAsync(stream, ImageJsonSerializerContext.Default.ImageMeta, cancellationToken);

        return new Image(data, bitDepth, maxValue, blackLevel, imageMeta);
    }

    /// <summary>
    /// Writes image stream to disk. Use with <see cref="FromStreamAsync"/>.
    /// Internal use only
    /// </summary>
    /// <param name="stream"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    internal async Task WriteStreamAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var (channelCount, width, height) = Shape;
        var magic = (BitConverter.IsLittleEndian ? "ImL2"u8 : "ImB2"u8).ToArray();
        await stream.WriteAsync(magic, cancellationToken);

        int[] header = [
            width,
            height,
            (int)bitDepth,
            BitConverter.SingleToInt32Bits(maxValue),
            BitConverter.SingleToInt32Bits(blackLevel),
            channelCount
        ];

        await stream.WriteAsync(BitConverter.GetBytes(header.Length), cancellationToken);
        for (var i = 0; i < header.Length; i++)
        {
            await stream.WriteAsync(BitConverter.GetBytes(header[i]), cancellationToken);
        }

        await stream.WriteAsync(data.AsMemory().Cast<float, byte>(), cancellationToken);

        await JsonSerializer.SerializeAsync(stream, ImageMeta, ImageJsonSerializerContext.Default.ImageMeta, cancellationToken);
    }

    public static bool TryReadFitsFile(string fileName, [NotNullWhen(true)] out Image? image)
    {
        using var bufferedReader = new BufferedFile(fileName, FileAccess.ReadWrite, FileShare.Read, 1000 * 2088);
        return TryReadFitsFile(new Fits(bufferedReader, fileName.EndsWith(".gz")), out image);
    }

    public static bool TryReadFitsFile(Fits fitsFile, [NotNullWhen(true)] out Image? image)
    {
        var hdu = fitsFile.ReadHDU();
        if (hdu?.Axes?.Length is not { } axisLength
            || hdu.Data is not ImageData imageData
            || imageData.DataArray is not object[] channelOrHeightArray
            || channelOrHeightArray.Length == 0
            || !(BitDepth.FromValue(hdu.BitPix) is { } bitDepth)
        )
        {
            image = default;
            return false;
        }

        int height, width, channelCount;
        TypeCode elementType;

        switch (axisLength)
        {
            case 2:
                height = hdu.Axes[0];
                width = hdu.Axes[1];
                channelCount = 1;
                elementType = Type.GetTypeCode(channelOrHeightArray[0].GetType().GetElementType());
                break;

            case 3:
                channelCount = hdu.Axes[0];
                height = hdu.Axes[1];
                width = hdu.Axes[2];
                elementType = Type.GetTypeCode(((object[])channelOrHeightArray[0])[0].GetType().GetElementType());
                break;

            default:
                image = null;
                return false;
        }

        var exposureStartTime = new DateTime(hdu.ObservationDate.Ticks, DateTimeKind.Utc);
        var maybeExpTime = hdu.Header.GetDoubleValue("EXPTIME", double.NaN);
        var maybeExposure = hdu.Header.GetDoubleValue("EXPOSURE", double.NaN);
        var exposureDuration = TimeSpan.FromSeconds(new double[] { maybeExpTime, maybeExpTime, 0.0 }.First(x => !double.IsNaN(x)));
        var instrument = hdu.Instrument;
        var telescope = hdu.Telescope;
        var equinox = hdu.Equinox;
        var pixelSizeX = hdu.Header.GetFloatValue("XPIXSZ", float.NaN);
        var pixelSizeY = hdu.Header.GetFloatValue("YPIXSZ", float.NaN);
        var xbinning = hdu.Header.GetIntValue("XBINNING", 1);
        var ybinning = hdu.Header.GetIntValue("YBINNING", 1);
        var blackLevel = hdu.Header.GetFloatValue("BLKLEVEL", float.NaN);
        var pixelScale = hdu.Header.GetFloatValue("PIXSCALE", float.NaN);
        var focalLength = hdu.Header.GetIntValue("FOCALLEN", -1);
        var focusPos = hdu.Header.GetIntValue("FOCUSPOS", -1);
        var filterName = hdu.Header.GetStringValue("FILTER");
        var ccdTemp = hdu.Header.GetFloatValue("CCD-TEMP", float.NaN);
        var rowOrder = RowOrder.FromFITSValue(hdu.Header.GetStringValue("ROWORDER")) ?? RowOrder.TopDown;
        var frameType = FrameType.FromFITSValue(hdu.Header.GetStringValue("FRAMETYP") ?? hdu.Header.GetStringValue("IMAGETYP")) ?? FrameType.None;
        var filter = Filter.FromName(filterName);
        var bzero = (float)hdu.BZero;
        var bscale = (float)hdu.BScale;
        var isCFA = hdu.Header.ContainsKey("CFAIMAGE") ? hdu.Header.GetBooleanValue("CFAIMAGE", false) : null as bool?;
        var (sensorType, bayerOffsetX, bayerOffsetY) = SensorType.FromFITSValue(
            isCFA,
            channelCount,
            hdu.Header.GetIntValue("BAYOFFX", 0), hdu.Header.GetIntValue("BAYOFFY", 0),
            hdu.Header.GetStringValue("BAYERPAT"), hdu.Header.GetStringValue("COLORTYP")
        );
        var latitude = hdu.Header.GetFloatValue("LATITUDE", float.NaN);
        var longitude = hdu.Header.GetFloatValue("LONGITUDE", float.NaN);

        var imgArray = new float[channelCount, height, width];
        Span<float> scratchRow = stackalloc float[Math.Min(256, width)];

        var quot = Math.DivRem(width, scratchRow.Length, out var rem);
        var maxValue = (float)hdu.MaximumValue;
        bool needsMinMaxValRecalc = float.IsNaN(blackLevel) || blackLevel < 0 || float.IsNaN(maxValue) || maxValue is <= 0 || maxValue <= blackLevel;
        if (needsMinMaxValRecalc)
        {
            maxValue = float.MinValue;
            blackLevel = float.MaxValue;
        }

        var rowSize = sizeof(float) * width;
        var channelSize = rowSize * height;

        switch (elementType)
        {
            case TypeCode.Byte:
                for (int c = 0; c < channelCount; c++)
                {
                    var heightArray = (object[])(axisLength is 2 ? channelOrHeightArray : channelOrHeightArray[c]);
                    var imgArray2d = imgArray.AsSpan2D(c);
                    for (int h = 0; h < height; h++)
                    {
                        var byteWidthArray = (byte[])heightArray[h];
                        var row = imgArray2d.GetRowSpan(h);
                        var sourceIndex = 0;
                        for (int i = 0; i < quot; i++)
                        {
                            for (int w = 0; w < scratchRow.Length; w++)
                            {
                                var val = bscale * byteWidthArray[sourceIndex + w] + bzero;
                                scratchRow[w] = val;
                                if (needsMinMaxValRecalc && !float.IsNaN(val))
                                {
                                    maxValue = MathF.Max(maxValue, val);
                                    blackLevel = MathF.Min(blackLevel, val);
                                }
                            }
                            sourceIndex += scratchRow.Length;
                            scratchRow.CopyTo(row);
                            row = row[scratchRow.Length..];
                        }
                        if (rem > 0)
                        {
                            // copy rest
                            for (int w = 0; w < rem; w++)
                            {
                                var val = bscale * byteWidthArray[sourceIndex + w] + bzero;
                                scratchRow[w] = val;
                                if (needsMinMaxValRecalc && !float.IsNaN(val))
                                {
                                    maxValue = MathF.Max(maxValue, val);
                                    blackLevel = MathF.Min(blackLevel, val);
                                }
                            }
                            scratchRow[..rem].CopyTo(row);
                        }
                    }
                }
                break;

            case TypeCode.Int16:
                for (int c = 0; c < channelCount; c++)
                {
                    var heightArray = (object[])(axisLength is 2 ? channelOrHeightArray : channelOrHeightArray[c]);
                    var imgArray2d = imgArray.AsSpan2D(c);
                    for (int h = 0; h < height; h++)
                    {
                        var shortWidthArray = (short[])heightArray[h];
                        var row = imgArray2d.GetRowSpan(h);
                        var sourceIndex = 0;
                        for (int i = 0; i < quot; i++)
                        {
                            for (int w = 0; w < scratchRow.Length; w++)
                            {
                                var val = bscale * shortWidthArray[sourceIndex + w] + bzero;
                                scratchRow[w] = val;
                                if (needsMinMaxValRecalc && !float.IsNaN(val))
                                {
                                    maxValue = MathF.Max(maxValue, val);
                                    blackLevel = MathF.Min(blackLevel, val);
                                }
                            }
                            sourceIndex += scratchRow.Length;
                            scratchRow.CopyTo(row);
                            row = row[scratchRow.Length..];
                        }
                        if (rem > 0)
                        {
                            // copy rest
                            for (int w = 0; w < rem; w++)
                            {
                                var val = bscale * shortWidthArray[sourceIndex + w] + bzero;
                                scratchRow[w] = val;
                                if (needsMinMaxValRecalc && !float.IsNaN(val))
                                {
                                    maxValue = MathF.Max(maxValue, val);
                                    blackLevel = MathF.Min(blackLevel, val);
                                }
                            }
                            scratchRow[..rem].CopyTo(row);
                        }
                    }
                }
                break;

            case TypeCode.Int32:
                for (int c = 0; c < channelCount; c++)
                {
                    var heightArray = (object[])(axisLength is 2 ? channelOrHeightArray : channelOrHeightArray[c]);
                    var imgArray2d = imgArray.AsSpan2D(c);
                    for (int h = 0; h < height; h++)
                    {
                        var intWidthArray = (int[])heightArray[h];
                        var row = imgArray2d.GetRowSpan(h);
                        var sourceIndex = 0;
                        for (int i = 0; i < quot; i++)
                        {
                            for (int w = 0; w < scratchRow.Length; w++)
                            {
                                var val = bscale * intWidthArray[sourceIndex + w] + bzero;
                                scratchRow[w] = val;
                                if (needsMinMaxValRecalc && !float.IsNaN(val))
                                {
                                    maxValue = MathF.Max(maxValue, val);
                                    blackLevel = MathF.Min(blackLevel, val);
                                }
                            }
                            sourceIndex += scratchRow.Length;
                            scratchRow.CopyTo(row);
                            row = row[scratchRow.Length..];
                        }
                        if (rem > 0)
                        {
                            // copy rest
                            for (int w = 0; w < rem; w++)
                            {
                                var val = bscale * intWidthArray[sourceIndex + w] + bzero;
                                scratchRow[w] = val;
                                if (needsMinMaxValRecalc && !float.IsNaN(val))
                                {
                                    maxValue = MathF.Max(maxValue, val);
                                    blackLevel = MathF.Min(blackLevel, val);
                                }
                            }
                            scratchRow[..rem].CopyTo(row);
                        }
                    }
                }
                break;

            case TypeCode.Single:
                for (int c = 0; c < channelCount; c++)
                {
                    var heightArray = (object[])(axisLength is 2 ? channelOrHeightArray : channelOrHeightArray[c]);
                    var imgArray2d = imgArray.AsSpan2D(c);
                    for (int h = 0; h < height; h++)
                    {
                        var floatWidthArray = (float[])heightArray[h];
                        var row = imgArray2d.GetRowSpan(h);
                        var sourceIndex = 0;
                        for (int i = 0; i < quot; i++)
                        {
                            for (int w = 0; w < scratchRow.Length; w++)
                            {
                                var val = bscale * floatWidthArray[sourceIndex + w] + bzero;
                                scratchRow[w] = val;
                                if (needsMinMaxValRecalc && !float.IsNaN(val))
                                {
                                    maxValue = MathF.Max(maxValue, val);
                                    blackLevel = MathF.Min(blackLevel, val);
                                }
                            }
                            sourceIndex += scratchRow.Length;
                            scratchRow.CopyTo(row);
                            row = row[scratchRow.Length..];
                        }
                        if (rem > 0)
                        {
                            // copy rest
                            for (int w = 0; w < rem; w++)
                            {
                                var val = bscale * floatWidthArray[sourceIndex + w] + bzero;
                                scratchRow[w] = val;
                                if (needsMinMaxValRecalc && !float.IsNaN(val))
                                {
                                    maxValue = MathF.Max(maxValue, val);
                                    blackLevel = MathF.Min(blackLevel, val);
                                }
                            }
                            scratchRow[..rem].CopyTo(row);
                        }
                    }
                }
                break;

            default:
                image = null;
                return false;
        }

        var imageMeta = new ImageMeta(
            instrument,
            exposureStartTime,
            exposureDuration,
            frameType,
            telescope,
            pixelSizeX,
            pixelSizeY,
            focalLength,
            focusPos,
            filter,
            xbinning,
            ybinning,
            ccdTemp,
            sensorType,
            bayerOffsetX,
            bayerOffsetY,
            rowOrder,
            latitude,
            longitude
        );
        image = new Image(imgArray, bitDepth, maxValue, blackLevel, imageMeta);
        return true;
    }

    public void WriteToFitsFile(string fileName)
    {
        var (channelCount, width, height) = Shape;
        var fits = new Fits();
        object[] jaggedArray;
        int bzero;
        bool dataIsInt;
        switch (bitDepth)
        {
            case BitDepth.Int8:
                var jaggedByteArray = new byte[channelCount][][];
                bzero = 0;
                dataIsInt = true;
                for (var c = 0; c < channelCount; c++)
                {
                    var channel = jaggedByteArray[c] = new byte[height][];
                    for (var h = 0; h < height; h++)
                    {
                        var row = new byte[width];
                        for (var w = 0; w < width; w++)
                        {
                            row[w] = (byte)data[c, h, w];
                        }
                        channel[h] = row;
                    }
                }
                jaggedArray = jaggedByteArray;
                break;

            case BitDepth.Int16:
                var jaggedShortArray = new short[channelCount][][];
                bzero = 32768;
                dataIsInt = true;
                for (var c = 0; c < channelCount; c++)
                {
                    var channel = jaggedShortArray[c] = new short[height][];
                    for (var h = 0; h < height; h++)
                    {
                        var row = new short[width];
                        for (var w = 0; w < width; w++)
                        {
                            row[w] = (short)(data[c, h, w] - bzero);
                        }
                        channel[h] = row;
                    }
                }
                jaggedArray = jaggedShortArray;
                break;

            case BitDepth.Float32:
                var jaggedFloatArray = new float[channelCount][][];
                bzero = 0;
                dataIsInt = false;
                var rowSize = sizeof(float) * width;
                var channelSize = rowSize * height;
                for (var c = 0; c < channelCount; c++)
                {
                    var channel = jaggedFloatArray[c] = new float[height][];
                    for (var h = 0; h < height; h++)
                    {
                        channel[h] = new float[width];
                        Buffer.BlockCopy(data, (c * channelSize) + (h * rowSize), channel[h], 0, rowSize);
                    }
                }
                jaggedArray = jaggedFloatArray;
                break;

            default:
                throw new NotSupportedException($"Bits per pixel {bitDepth} is not supported");
        }
        var basicHdu = FitsFactory.HDUFactory(jaggedArray);
        basicHdu.Header.Bitpix = (int)bitDepth;
        AddHeaderValueIfHasValue("BZERO", bzero, "offset data range to that of unsigned short");
        AddHeaderValueIfHasValue("BSCALE", 1, "default scaling factor");
        AddHeaderValueIfHasValue("BLKLEVEL", BlackLevel, "", isDataValue: true);
        AddHeaderValueIfHasValue("XBINNING", imageMeta.BinX, "");
        AddHeaderValueIfHasValue("YBINNING", imageMeta.BinY, "");
        AddHeaderValueIfHasValue("XPIXSZ", imageMeta.PixelSizeX, "");
        AddHeaderValueIfHasValue("YPIXSZ", imageMeta.PixelSizeX, "");
        AddHeaderValueIfHasValue("DATE-OBS", FitsDate.GetFitsDateString(imageMeta.ExposureStartTime.UtcDateTime), "UT");
        AddHeaderValueIfHasValue("EXPTIME", imageMeta.ExposureDuration.TotalSeconds, "seconds");
        AddHeaderValueIfHasValue("IMAGETYP", imageMeta.FrameType, "");
        AddHeaderValueIfHasValue("FRAMETYP", imageMeta.FrameType, "");
        AddHeaderValueIfHasValue("DATAMAX", MaxValue, "");
        AddHeaderValueIfHasValue("INSTRUME", imageMeta.Instrument, "");
        AddHeaderValueIfHasValue("TELESCOP", imageMeta.Telescope, "");
        AddHeaderValueIfHasValue("ROWORDER", imageMeta.RowOrder, "");
        AddHeaderValueIfHasValue("CCD-TEMP", imageMeta.CCDTemperature, "Celsius");
        AddHeaderValueIfHasValue("BAYOFFX", imageMeta.BayerOffsetX, "");
        AddHeaderValueIfHasValue("BAYOFFY", imageMeta.BayerOffsetY, "");
        AddHeaderValueIfHasValue("LATITUDE", imageMeta.Latitude, "degrees");
        AddHeaderValueIfHasValue("LONGITUDE", imageMeta.Longitude, "degrees");
        if (imageMeta.SensorType is SensorType.RGGB)
        {
            AddHeaderValueIfHasValue("BAYERPAT", "RGGB", "");
            AddHeaderValueIfHasValue("COLORTYP", "RGGB", "");
        }
        fits.AddHDU(basicHdu);

        using var bufferedWriter = new BufferedFile(fileName, FileAccess.ReadWrite, FileShare.Read, 1000 * 2088);
        fits.Write(bufferedWriter);
        bufferedWriter.Flush();
        bufferedWriter.Close();

        void AddHeaderValueIfHasValue<T>(string key, T value, string comment = "", bool isDataValue = false)
        {
            var card = value switch
            {
                float f when !float.IsNaN(f) => new HeaderCard(key, f, comment),
                float f when isDataValue && dataIsInt => new HeaderCard(key, (int)f, comment),
                double d when !double.IsNaN(d) => new HeaderCard(key, d, comment),
                double d when isDataValue && dataIsInt => new HeaderCard(key, (int)d, comment),
                int i => new HeaderCard(key, i, comment),
                long l => new HeaderCard(key, l, comment),
                string s => new HeaderCard(key, s, comment),
                bool b => new HeaderCard(key, b, comment),
                FrameType ft => new HeaderCard(key, ft.ToFITSValue(), comment),
                RowOrder ro => new HeaderCard(key, ro.ToFITSValue(), comment),
                _ => null
            };

            if (card is not null)
            {
                basicHdu.Header.AddCard(card);
            }
        }
    }

    const int BoxRadius = 14;
    const float HfdFactor = 1.5f;
    const int MaxScaledRadius = (int)(HfdFactor * BoxRadius) + 1;
    static readonly ImmutableArray<BitMatrix> StarMasks;

    static Image()
    {
        var starMasksBuilder = ImmutableArray.CreateBuilder<BitMatrix>(MaxScaledRadius);
        for (var radius = 1; radius < MaxScaledRadius; radius++)
        {
            MakeStarMask(radius, out var mask);
            starMasksBuilder.Add(mask);
        }

        StarMasks = starMasksBuilder.ToImmutable();
    }

    static void MakeStarMask(int radius, out BitMatrix starMask)
    {
        var diameter = radius << 1;
        var radius_squared = radius * radius;
        starMask = new BitMatrix(diameter + 1, diameter + 1);

        for (int y = -radius; y <= radius; y++)
        {
            for (int x = -radius; x <= radius; x++)
            {
                if (x * x + y * y <= radius_squared)
                {
                    int pixelX = radius + x;
                    int pixelY = radius + y;
                    if (pixelX >= 0 && pixelX <= diameter && pixelY >= 0 && pixelY <= diameter)
                    {
                        starMask[pixelY, pixelX] = true;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Find background, noise level, number of stars and their HFD, FWHM, SNR, flux and centroid.
    /// </summary>
    /// <param name="channel">Channel</param>
    /// <param name="snrMin">S/N ratio threshold for star detection</param>
    /// <param name="maxStars"></param>
    /// <param name="maxRetries"></param>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public virtual async Task<StarList> FindStarsAsync(int channel, float snrMin = 20f, int maxStars = 500, int maxRetries = 2, CancellationToken cancellationToken = default)
    {
        const int ChunkSize = 2 * MaxScaledRadius;
        const float HalfChunkSizeInv = 1.0f / 2.0f * ChunkSize;
        var (channelCount, width, height) = Shape;

        if (channel >= channelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(channel), channel, $"Channel index {channel} is out of range for image with {ChannelCount} channels");
        }
        if (imageMeta.SensorType is SensorType.RGGB && ChannelCount is 1)
        {
            // debayer to mono
            var monoImage = await DebayerAsync(DebayerAlgorithm.BilinearMono, cancellationToken);
            return await monoImage.FindStarsAsync(channel, snrMin, maxStars, maxRetries, cancellationToken);
        }

        var (background, star_level, noise_level, hist_threshold) = Background(channel);

        var detection_level = MathF.Max(3.5f * noise_level, star_level); /* level above background. Start with a high value */
        var retries = maxRetries;

        if (background >= hist_threshold || background <= 0)  /* abnormal file */
        {
            return new StarList([]);
        }

        var starList = new ConcurrentBag<ImagedStar>();
        var img_star_area = new BitMatrix(height, width);

        // we use interleaved processing of rows (so that we do not have to lock to protect the bitmatrix
        var halfChunkCount = (int)Math.Ceiling(height * HalfChunkSizeInv);
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 4, CancellationToken = cancellationToken };

        do
        {
            for (var i = 0; i <= 1; i++)
            {
                await Parallel.ForAsync(0, halfChunkCount, parallelOptions, async (halfChunk, cancellationToken) =>
                {
                    await Task.Run(() =>
                    {
                        var chunk = 2 * halfChunk + i;
                        var chunkEnd = Math.Min(height, (chunk + 1) * ChunkSize);
                        for (var fitsY = chunk * ChunkSize; fitsY < chunkEnd; fitsY++)
                        {
                            for (var fitsX = 0; fitsX < width; fitsX++)
                            {
                                // new star. For analyse used sigma is 5, so not too low.
                                var value = data[channel, fitsY, fitsX];
                                if (float.IsNaN(value))
                                {
                                    img_star_area[fitsY, fitsX] = true; /* ignore NaN values */
                                }
                                else if (value - background > detection_level
                                    && !img_star_area[fitsY, fitsX]
                                    && AnalyseStar(channel, fitsX, fitsY, BoxRadius, out var star)
                                    && star.HFD is > 0.8f and <= BoxRadius * 2 /* at least 2 pixels in size */
                                    && star.SNR >= snrMin
                                )
                                {
                                    starList.Add(star);
                                    var scaledHfd = HfdFactor * star.HFD;
                                    var r = (int)MathF.Round(scaledHfd); /* radius for marking star area, factor 1.5 is chosen emperiacally. */
                                    var xc_offset = (int)MathF.Round(star.XCentroid - scaledHfd); /* star center as integer */
                                    var yc_offset = (int)MathF.Round(star.YCentroid - scaledHfd);

                                    var mask = StarMasks[Math.Max(r - 1, 0)];

                                    img_star_area.SetRegionClipped(yc_offset, xc_offset, mask);
                                }
                            }
                        }
                    }, cancellationToken);
                });
            }

            /* In principle not required. Try again with lower detection level */
            if (detection_level <= 7 * noise_level)
            {
                retries = -1; /* stop */
            }
            else
            {
                retries--;
                detection_level = MathF.Max(6.999f * noise_level, MathF.Min(30 * noise_level, detection_level * 6.999f / 30)); /* very high -> 30 -> 7 -> stop.  Or  60 -> 14 -> 7.0. Or for very short exposures 3.5 -> stop */
            }
        } while (starList.Count < maxStars && retries > 0);/* reduce detection level till enough stars are found. Note that faint stars have less positional accuracy */

        return new StarList(starList);
    }

    /// <summary>
    /// Generates a historgram of the image with values from 0 to 90% of the maximum value.
    /// This is used to find the background level and star level.
    /// Values above 90% of the maximum value are ignored as they are likely to be saturated stars or artifacts.
    /// NaN values are also ignored.
    /// The histogram is returned as an array of uint where the index represents the pixel value and the value at that index represents the number of pixels with that value.
    /// Additionally, the mean pixel value  and total number of pixels in the histogram are also returned.
    /// </summary>
    /// <param name="channel">Channel index for which to calculate the histogram</param>
    /// <param name="ignoreBlack">Whether to ignore black pixels (value 0) in the histogram. This is useful for images with black borders or vignetting. Default is true.</param>
    /// <param name="thresholdPct">The percentage of the maximum pixel value to use as the upper limit for the histogram. Default is 91%.</param>
    /// <param name="calcStats">If true calculate further statistics like median and MAD</param>
    /// <returns>historgram values</returns>
    public ImageHistogram Histogram(int channel, byte thresholdPct = 91, bool ignoreBlack = true, bool calcStats = false, bool removePedestral = false)
    {
        var (channelCount, width, height) = Shape;

        if (channel >= channelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(channel), channel, $"Channel index {channel} is out of range for image with {ChannelCount} channels");
        }
        if (thresholdPct > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(thresholdPct), thresholdPct, "Threshold percentage must be between 0 and 100");
        }

        float? rescaledMaxValue;
        Image image;
        if (BitDepth is BitDepth.Float32 && MaxValue <= 1.0f)
        {
            rescaledMaxValue = ushort.MaxValue;
            image = ScaleFloatValues(rescaledMaxValue.Value);
        }
        else
        {
            rescaledMaxValue = null;
            image = this;
        }

        var threshold = (uint)Math.Round(image.MaxValue * (0.01d * thresholdPct), MidpointRounding.ToPositiveInfinity) + 1;
        var histogram = ImmutableArray.CreateBuilder<uint>((int)threshold);

        const int size = 1024;
        Span<uint> zeros = stackalloc uint[size];
        zeros.Clear();

        for (var i = 0; i < threshold; i += size)
        {
            if (i + size > threshold)
            {
                histogram.AddRange(zeros[..(int)(threshold - i)]);
            }
            else
            {
                histogram.AddRange(zeros);
            }
        }

        var hist_total = 0u;
        var count = 1; /* prevent divide by zero */
        var total_value = 0f;
        var pedestralAdjustValue = removePedestral ? blackLevel : 0f;

        for (var h = 0; h <= height - 1; h++)
        {
            for (var w = 0; w <= width - 1; w++)
            {
                var value = image[channel, h, w];
                if (!float.IsNaN(value))
                {
                    var valueMinusPedestral = value - pedestralAdjustValue;

                    // ignore black overlap areas and bright stars (if threshold percentage is below 100%)
                    if ((!ignoreBlack || valueMinusPedestral >= 1) && valueMinusPedestral < threshold)
                    {
                        var valueAsInt = (int)Math.Clamp(MathF.Round(valueMinusPedestral), 0, threshold - 1);
                        histogram[valueAsInt]++; // calculate histogram
                        hist_total++;
                        total_value += valueMinusPedestral;
                        count++;
                    }
                }
            }
        }

        var hist_mean = 1.0f / count * total_value;

        float? median, mad;
        if (calcStats)
        {
            var medianlength = histogram.Count / 2.0;
            uint occurances = 0;
            int median1 = 0, median2 = 0;

            /* Determine median out of histogram array */
            for (int i = 0; i < threshold; i++)
            {
                var histValue = histogram[i];

                occurances += histValue;
                if (occurances > medianlength)
                {
                    median1 = i;
                    median2 = i;
                    break;
                }
                else if (occurances == medianlength)
                {
                    median1 = i;
                    for (int j = i + 1; j < threshold; j++)
                    {
                        if (histValue > 0)
                        {
                            median2 = j;
                            break;
                        }
                    }
                    break;
                }
            }
            median = median1 * 0.5f + median2 * 0.5f;

            /* Determine median Absolute Deviation out of histogram array and previously determined median
             * As the histogram already has the values sorted and we know the median,
             * we can determine the mad by beginning from the median and step up and down
             * By doing so we will gain a sorted list automatically, because MAD = DetermineMedian(|xn - median|)
             * So starting from the median will be 0 (as median - median = 0), going up and down will increment by the steps
             */
            occurances = 0;
            var idxDown = median1;
            var idxUp = median2;
            mad = null;
            while (true)
            {
                if (idxDown >= 0 && idxDown != idxUp)
                {
                    occurances += histogram[idxDown] + histogram[idxUp];
                }
                else
                {
                    occurances += histogram[idxUp];
                }

                if (occurances > medianlength)
                {
                    mad = MathF.Abs(idxUp - median.Value);
                    break;
                }

                idxUp++;
                idxDown--;
                if (idxUp >= threshold)
                {
                    break;
                }
            }
        }
        else
        {
            median = null;
            mad = float.NaN;
        }

        return new ImageHistogram(channel, histogram.ToImmutableArray(), hist_mean, hist_total, threshold, thresholdPct, rescaledMaxValue, median, mad, ignoreBlack);
    }

    public ImageHistogram Statistics(int channel, bool removePedestral = false)
        => Histogram(channel, thresholdPct: 100, ignoreBlack: false, calcStats: true, removePedestral);

    private (float Pedestral, float Median, float MAD) GetPedestralMedianAndMADScaledToUnit(int channel)
    {
        var stats = Statistics(channel, removePedestral: true);
        if (stats.Median is not { } median || stats.MAD is not { } mad)
        {
            throw new InvalidOperationException("Median and MAD should have been calculated");
        }

        var maxValueFactor = 1f / (stats.RescaledMaxValue ?? MaxValue);

        return (blackLevel * maxValueFactor, median * maxValueFactor, mad * maxValueFactor);
    }

    /// <summary>
    /// get background and star level from peek histogram
    /// </summary>
    /// <returns>background and star level</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public (float background, float starLevel, float noise_level, float threshold) Background(int channel)
    {
        var (channelCount, width, height) = Shape;

        if (channel >= channelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(channel), channel, $"Channel index {channel} is out of range for image with {ChannelCount} channels");
        }

        // get histogram of img_loaded and his_total
        var histogram = Histogram(channel);
        var background = float.NaN; // define something for images containing 0 or 65535 only

        // find peak in histogram which should be the average background
        var pixels = 0u;
        var max_range = histogram.Mean;
        uint i;
        // mean value from histogram
        for (i = 1; i <= max_range; i++)
        {
            // find peak, ignore value 0 from oversize
            var histVal = histogram.Histogram[(int)i];
            if (histVal > pixels) // find colour peak
            {
                pixels = histVal;
                background = i;
            }
        }

        // check alternative mean value
        if (float.IsNaN(background) || histogram.Mean > 1.5f * background) // 1.5 * most common
        {
            background = histogram.Mean; // strange peak at low value, ignore histogram and use mean
        }

        i = (uint)MathF.Ceiling(histogram.RescaledMaxValue ?? MaxValue);

        var starLevel = 0.0f;
        var above = 0u;

        while (starLevel == 0 && i > background + 1)
        {
            i--;
            if (i < histogram.Histogram.Length)
            {
                above += histogram.Histogram[(int)i];
            }
            if (above > 0.001f * histogram.Total)
            {
                starLevel = i;
            }
        }

        if (starLevel <= background)
        {
            starLevel = background + 1; // no or very few stars
        }
        else
        {
            // star level above background. Important subtract 1 for saturated images. Otherwise no stars are detected
            starLevel = starLevel - background - 1;
        }

        // calculate noise level
        var stepSize = (int)MathF.Round(height / 71.0f); // get about 71x71 = 5000 samples.So use only a fraction of the pixels

        // prevent problems with even raw OSC images
        if (stepSize % 2 == 0)
        {
            stepSize++;
        }

        var sd = 99999.0f;
        float sd_old;
        var iterations = 0;

        var rescaledFactor = histogram.RescaledMaxValue ?? 1.0f;
        // repeat until sd is stable or 7 iterations
        do
        {
            var counter = 1; // never divide by zero

            sd_old = sd;
            var fitsY = 15;
            while (fitsY <= height - 1 - 15)
            {
                var fitsX = 15;
                while (fitsX <= width - 1 - 15)
                {
                    var value = data[channel, fitsY, fitsX];
                    // not an outlier, noise should be symmetrical so should be less then twice background
                    if (!float.IsNaN(value))
                    {
                        var denorm = rescaledFactor * value;

                        // ignore outliers after first run
                        if (denorm < background * 2 && denorm != 0 && (iterations == 0 || (denorm - background) <= 3 * sd_old))
                        {
                            var bgSub = denorm - background;
                            sd += bgSub * bgSub;
                            // keep record of number of pixels processed
                            counter++;
                        }
                    }
                    fitsX += stepSize; // skip pixels for speed
                }
                fitsY += stepSize; // skip pixels for speed
            }
            sd = MathF.Sqrt(sd / counter); // standard deviation
            iterations++;
        } while (sd_old - sd >= 0.05f * sd && iterations < 7); // repeat until sd is stable or 7 iterations

        // renormalize
        if (histogram.RescaledMaxValue is { } rescaledMaxValue)
        {
            var values = VectorMath.Divide([background, starLevel, sd, histogram.Threshold], rescaledMaxValue);

            return ((float)values[0], (float)values[1], (float)values[2], (float)values[3]);
        }
        else
        {
            return (background, starLevel, sd, histogram.Threshold);
        }
    }

    /// <summary>
    /// calculate star HFD and FWHM, SNR, xc and yc are center of gravity.All x, y coordinates in array[0..] positions
    /// </summary>
    /// <param name="x1">x</param>
    /// <param name="y1">y</param>
    /// <param name="boxRadius">box radius</param>
    /// <returns>true if a star was detected</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public bool AnalyseStar(int channel, int x1, int y1, int boxRadius, out ImagedStar star)
    {
        const int maxAnnulusBg = 328; // depends on boxSize <= 50
        Debug.Assert(boxRadius <= 50, nameof(boxRadius) + " should be <= 50 to prevent runtime errors");

        var (channelCount, width, height) = Shape;

        if (channel >= channelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(channel), channel, $"Channel index {channel} is out of range for image with {ChannelCount} channels");
        }

        var r1_square = boxRadius * boxRadius; /*square radius*/
        var r2 = boxRadius + 1; /*annulus width plus 1*/
        var r2_square = r2 * r2;

        var valMax = 0.0f;
        float sumVal;
        float bg;
        float sd_bg;

        float xc = float.NaN, yc = float.NaN;
        int r_aperture = -1;

        if (x1 - r2 <= 0 || x1 + r2 >= width - 1 || y1 - r2 <= 0 || y1 + r2 >= height - 1)
        {
            star = default;
            return false;
        }

        Span<float> backgroundScratch = stackalloc float[maxAnnulusBg];
        int backgroundIndex = 0;

        try
        {
            /*calculate the mean outside the the detection area*/
            for (var i = -r2; i <= r2; i++)
            {
                for (var j = -r2; j <= r2; j++)
                {
                    var distance = i * i + j * j; /*working with sqr(distance) is faster then applying sqrt*/
                    /*annulus, circular area outside rs, typical one pixel wide*/
                    if (distance > r1_square && distance <= r2_square)
                    {
                        var value = data[channel, y1 + i, x1 + j];
                        if (!float.IsNaN(value))
                        {
                            backgroundScratch[backgroundIndex++] = value;
                        }
                    }
                }
            }

            var background = backgroundScratch[..backgroundIndex];
            bg = Median(background);

            float minNonZeroBgValue = 0;
            /* fill background with offsets */
            for (var i = 0; i < background.Length; i++)
            {
                var bg_i = background[i];
                // assumes that median sorts ascending
                if (minNonZeroBgValue == 0)
                {
                    minNonZeroBgValue = bg_i;
                }
                background[i] = MathF.Abs(bg_i - bg);
            }

            //median absolute deviation (MAD)
            var mad_bg = Median(background);
            sd_bg = mad_bg * MAD_TO_SD;

            // add some value for images with zero noise background.
            // This will prevent that background is seen as a star. E.g. some jpg processed by nova.astrometry.net
            if (sd_bg == 0)
            {
                sd_bg = BlackLevel > 0 ? BlackLevel : minNonZeroBgValue;
            }

            // reduce square annulus radius until it is symmetric to remove stars
            bool boxed;
            do
            {
                // Get center of gravity whithin star detection box and count signal pixels, repeat reduce annulus radius till symmetry to remove stars
                sumVal = 0.0f;
                var sumValX = 0.0f;
                var sumValY = 0.0f;
                var signal_counter = 0;

                for (var i = -boxRadius; i <= boxRadius; i++)
                {
                    for (var j = -boxRadius; j <= boxRadius; j++)
                    {
                        var value = data[channel, y1 + i, x1 + j];
                        if (!float.IsNaN(value))
                        {
                            var bg_sub_value = value - bg;
                            if (bg_sub_value > 3.0f * sd_bg)
                            {
                                sumVal += bg_sub_value;
                                sumValX += bg_sub_value * j;
                                sumValY += bg_sub_value * i;
                                signal_counter++; /* how many pixels are illuminated */
                            }
                        }
                    }
                }

                if (sumVal <= 12 * sd_bg)
                {
                    star = default; /*no star found, too noisy */
                    return false;
                }

                var xg = sumValX / sumVal;
                var yg = sumValY / sumVal;

                xc = x1 + xg;
                yc = y1 + yg;
                /* center of gravity found */

                if (xc - boxRadius < 0 || xc + boxRadius > width - 1 || yc - boxRadius < 0 || yc + boxRadius > height - 1)
                {
                    star = default; /* prevent runtime errors near sides of images */
                    return false;
                }

                var rs2_1 = boxRadius + boxRadius + 1;
                boxed = signal_counter >= 2.0f / 9 * (rs2_1 * rs2_1);/*are inside the box 2 of the 9 of the pixels illuminated? Works in general better for solving then ovality measurement as used in the past*/

                if (!boxed)
                {
                    if (boxRadius > 4)
                    {
                        boxRadius -= 2;
                    }
                    else
                    {
                        boxRadius--; /*try a smaller window to exclude nearby stars*/
                    }
                }

                /* check on hot pixels */
                if (signal_counter <= 1)
                {
                    star = default; /*one hot pixel*/
                    return false;
                }
            } while (!boxed && boxRadius > 1); /*loop and reduce aperture radius until star is boxed*/

            boxRadius += 2; /* add some space */

            // Build signal histogram from center of gravity
            Span<int> distance_histogram = stackalloc int[boxRadius + 1]; // this has a fixed upper bound

            for (var i = -boxRadius; i <= boxRadius; i++)
            {
                for (var j = -boxRadius; j <= boxRadius; j++)
                {
                    var distance = (int)MathF.Round(MathF.Sqrt(i * i + j * j)); /* distance from gravity center */
                    if (distance <= boxRadius) /* build histogram for circle with radius boxRadius */
                    {
                        var value = SubpixelValue(channel, xc + i, yc + j);
                        if (!float.IsNaN(value))
                        {
                            var bg_sub_value = value - bg;
                            if (bg_sub_value > 3.0 * sd_bg) /* 3 * sd should be signal */
                            {
                                distance_histogram[distance]++; /* build distance histogram up to circle with diameter rs */

                                if (bg_sub_value > valMax)
                                {
                                    valMax = bg_sub_value; /* record the peak value of the star */
                                }
                            }
                        }
                    }
                }
            }

            var distance_top_value = 0;
            var histStart = false;
            var illuminated_pixels = 0;
            do
            {
                r_aperture++;
                illuminated_pixels += distance_histogram[r_aperture];
                if (distance_histogram[r_aperture] > 0)
                {
                    histStart = true; /*continue until we found a value>0, center of defocused star image can be black having a central obstruction in the telescope*/
                }

                if (distance_top_value < distance_histogram[r_aperture])
                {
                    distance_top_value = distance_histogram[r_aperture]; /* this should be 2*pi*r_aperture if it is nice defocused star disk */
                }
                /* find a distance where there is no pixel illuminated, so the border of the star image of interest */
            } while (r_aperture < boxRadius && (!histStart || distance_histogram[r_aperture] > 0.1f * distance_top_value));

            if (r_aperture >= boxRadius)
            {
                star = default; /* star is equal or larger then box, abort */
                return false;
            }

            if (r_aperture > 2)
            {
                /* if more than 35% surface is illuminated */
                var r_aperture2_2 = 2 * r_aperture - 2;
                if (illuminated_pixels < 0.35f * (r_aperture2_2 * r_aperture2_2))
                {
                    star = default; /* not a star disk but stars, abort */
                    return false;
                }
            }
        }
        catch
        {
            star = default;
            return false;
        }

        // Get HFD
        var pixel_counter = 0;
        sumVal = 0.0f; // reset
        var sumValR = 0.0f;

        // Get HFD using the aproximation routine assuming that HFD line divides the star in equal portions of gravity:
        for (var i = -r_aperture; i <= r_aperture; i++) /*Make steps of one pixel*/
        {
            for (var j = -r_aperture; j <= r_aperture; j++)
            {
                var val = SubpixelValue(channel, xc + i, yc + j) - bg; /* the calculated center of gravity is a floating point position and can be anywhere, so calculate pixel values on sub-pixel level */
                var r = MathF.Sqrt(i * i + j * j); /* distance from star gravity center */
                sumVal += val;/* sumVal will be star total star flux*/
                sumValR += val * r; /* method Kazuhisa Miyashita, see notes of HFD calculation method, note calculate HFD over square area. Works more accurate then for round area */
                if (val >= valMax * 0.5)
                {
                    // How many pixels are above half maximum
                    pixel_counter++;
                }
            }
        }

        var flux = MathF.Max(sumVal, 0.00001f); /* prevent dividing by zero or negative values */
        var hfd = MathF.Max(0.7f, 2 * sumValR / flux);
        var star_fwhm = 2 * MathF.Sqrt(pixel_counter / MathF.PI);/*calculate from surface (by counting pixels above half max) the diameter equals FWHM */
        var snr = flux / MathF.Sqrt(flux + r_aperture * r_aperture * MathF.PI * sd_bg * sd_bg);

        star = new ImagedStar(hfd, star_fwhm, snr, flux, xc, yc);
        return true;
        /*For both bright stars (shot-noise limited) or skybackground limited situations
        snr := signal/noise
        snr := star_signal/sqrt(total_signal)
        snr := star_signal/sqrt(star_signal + sky_signal)
        equals
        snr:=flux/sqrt(flux + r*r*pi* sd^2).

        r is the diameter used for star flux measurement. Flux is the total star flux detected above 3* sd.

        Assuming unity gain ADU/e-=1
        See https://en.wikipedia.org/wiki/Signal-to-noise_ratio_(imaging)
        https://www1.phys.vt.edu/~jhs/phys3154/snr20040108.pdf
        http://spiff.rit.edu/classes/phys373/lectures/signal/signal_illus.html*/


        /*==========Notes on HFD calculation method=================
          Documented this HFD definition also in https://en.wikipedia.org/wiki/Half_flux_diameter
          References:
          https://astro-limovie.info/occultation_observation/halffluxdiameter/halffluxdiameter_en.html       by Kazuhisa Miyashita. No sub-pixel calculation
          https://www.lost-infinity.com/night-sky-image-processing-part-6-measuring-the-half-flux-diameter-hfd-of-a-star-a-simple-c-implementation/
          http://www.ccdware.com/Files/ITS%20Paper.pdf     See page 10, HFD Measurement Algorithm

          HFD, Half Flux Diameter is defined as: The diameter of circle where total flux value of pixels inside is equal to the outside pixel's.
          HFR, half flux radius:=0.5*HFD
          The pixel_flux:=pixel_value - background.

          The approximation routine assumes that the HFD line divides the star in equal portions of gravity:
              sum(pixel_flux * (distance_from_the_centroid - HFR))=0
          This can be rewritten as
             sum(pixel_flux * distance_from_the_centroid) - sum(pixel_values * (HFR))=0
             or
             HFR:=sum(pixel_flux * distance_from_the_centroid))/sum(pixel_flux)
             HFD:=2*HFR

          This is not an exact method but a very efficient routine. Numerical checking with an a highly oversampled artificial Gaussian shaped star indicates the following:

          Perfect two dimensional Gaussian shape with σ=1:   Numerical HFD=2.3548*σ                     Approximation 2.5066, an offset of +6.4%
          Homogeneous disk of a single value  :              Numerical HFD:=disk_diameter/sqrt(2)       Approximation disk_diameter/1.5, an offset of -6.1%

          The approximate routine is robust and efficient.

          Since the number of pixels illuminated is small and the calculated center of star gravity is not at the center of an pixel, above summation should be calculated on sub-pixel level (as used here)
          or the image should be re-sampled to a higher resolution.

          A sufficient signal to noise is required to have valid HFD value due to background noise.

          Note that for perfect Gaussian shape both the HFD and FWHM are at the same 2.3548 σ.
          */


        /*=============Notes on FWHM:=====================
           1)	Determine the background level by the averaging the boarder pixels.
           2)	Calculate the standard deviation of the background.

               Signal is anything 3 * standard deviation above background

           3)	Determine the maximum signal level of region of interest.
           4)	Count pixels which are equal or above half maximum level.
           5)	Use the pixel count as area and calculate the diameter of that area  as diameter:=2 *sqrt(count/pi).*/
    }

    /// <summary>
    /// calculate image pixel value on subpixel level
    /// </summary>
    /// <param name="x1"></param>
    /// <param name="y1"></param>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)]
    private float SubpixelValue(int channel, float x1, float y1)
    {
        var width = Width;
        var height = Height;

        // assumes that maxVal < long.MaxValue
        var x_trunc = (long)MathF.Truncate(x1);
        var y_trunc = (long)MathF.Truncate(y1);

        if (x_trunc < 0 || x_trunc >= width || y_trunc < 0 || y_trunc >= height)
        {
            return float.NaN;
        }
        else if (x_trunc == x1 && y_trunc == y1)
        {
            return data[channel, y_trunc, x_trunc];
        }

        var x_frac = x1 - x_trunc;
        var y_frac = y1 - y_trunc;
        try
        {
            const int tl = 0;
            const int tr = 1;
            const int bl = 2;
            const int br = 3;

            byte mask = 0;
            Span<float> pixels = stackalloc float[4];
            pixels.Fill(float.NaN);

            pixels[tl] = data[channel, y_trunc, x_trunc];
            if (x_trunc < width - 1)
            {
                pixels[tr] = data[channel, y_trunc, x_trunc + 1];
            }

            if (y_trunc < height - 1)
            {
                pixels[bl] = data[channel, y_trunc + 1, x_trunc];
            }

            if (x_trunc < width - 1 && y_trunc < height - 1)
            {
                pixels[br] = data[channel, y_trunc + 1, x_trunc + 1];
            }

            for (var i = 0; i < 4; i++)
            {
                if (!float.IsNaN(pixels[i]))
                {
                    mask |= (byte)(1 << i);
                }
            }

            if ((mask & 0b1111) == 0b1111)
            {
                return pixels[tl] * (1 - x_frac) * (1 - y_frac)
                    + pixels[tr] * x_frac * (1 - y_frac)
                    + pixels[bl] * (1 - x_frac) * y_frac
                    + pixels[br] * x_frac * y_frac;
            }
            else
            {
                int main;
                if (x_frac <= 0.5f && y_frac <= 0.5f)
                {
                    main = tl;
                }
                else if (x_frac > 0.5f && y_frac <= 0.5f)
                {
                    main = tr;
                }
                else if (x_frac <= 0.5f && y_frac > 0.5f)
                {
                    main = bl;
                }
                else
                {
                    main = br;
                }

                // if the main pixel is not lit, return NaN
                if ((mask & (1 << main)) == (1 << main))
                {
                    return pixels[main];
                }
                // for now, return NaN if any non-main pixel is NaN, a better approach would be to interpolate using only the available pixels
                else
                {
                    return float.NaN;
                }
            }
        }
        catch (Exception ex) when (Environment.UserInteractive)
        {
            GC.KeepAlive(ex);
            throw;
        }
        catch
        {
            return float.NaN;
        }
    }

    public Task<Image> DebayerAsync(DebayerAlgorithm debayerAlgorithm, CancellationToken cancellationToken = default)
    {
        // NO-OP for monochrome or full colour images
        if (imageMeta.SensorType is SensorType.Monochrome or SensorType.Color)
        {
            return Task.FromResult(this);
        }

        return debayerAlgorithm switch
        {
            DebayerAlgorithm.BilinearMono => DebayerBilinearMonoAsync(cancellationToken),
            DebayerAlgorithm.VNG => DebayerVNGAsync(cancellationToken),
            DebayerAlgorithm.None => throw new ArgumentException("Must specify an algorithm", nameof(debayerAlgorithm)),
            _ => throw new NotSupportedException($"Debayer algorithm {debayerAlgorithm} is not supported"),
        };
    }

    /// <summary>
    /// Uses a simple 2x2 sliding window to calculate the average of 4 pixels, assumes simple 2x2 Bayer matrix.
    /// Is a no-op for monochrome fames.
    /// </summary>
    /// <returns>Debayered monochrome image</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private async Task<Image> DebayerBilinearMonoAsync(CancellationToken cancellationToken = default)
    {
        var width = Width;
        var height = Height;
        var debayered = new float[1, height, width];
        var w1 = width - 1;
        var h1 = height - 1;

        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Environment.ProcessorCount * 4
        };

        // Process all rows except the last one in parallel
        await Parallel.ForAsync(0, h1, parallelOptions, async (y, ct) => await Task.Run(() =>
        {
            for (int x = 0; x < w1; x++)
            {
                debayered[0, y, x] = (float)(0.25d * ((double)data[0, y, x] + data[0, y + 1, x + 1] + data[0, y, x + 1] + data[0, y + 1, x]));
            }

            // last column
            debayered[0, y, w1] = (float)(0.25d * ((double)data[0, y, w1] + data[0, y + 1, w1 - 1] + data[0, y, w1 - 1] + data[0, y + 1, w1]));

            return ValueTask.CompletedTask;
        }, ct));

        // last row (processed sequentially as it's a single row)
        for (int x = 0; x < w1; x++)
        {
            debayered[0, h1, x] = (float)(0.25d * ((double)data[0, h1, x] + data[0, h1 - 1, x + 1] + data[0, h1, x + 1] + data[0, h1 - 1, x]));
        }

        // last pixel
        debayered[0, h1, w1] = (float)(0.25d * ((double)data[0, h1, w1] + data[0, h1 - 1, w1 - 1] + data[0, h1, w1 - 1] + data[0, h1 - 1, w1]));

        return new Image(debayered, BitDepth.Float32, maxValue, blackLevel, imageMeta with
        {
            SensorType = SensorType.Monochrome,
            BayerOffsetX = 0,
            BayerOffsetY = 0,
            Filter = Filter.Luminance
        });
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private async Task<Image> DebayerVNGAsync(CancellationToken cancellationToken)
    {
        var width = Width;
        var height = Height;
        var debayered = new float[3, height, width]; // RGB output

        var bayerOffsetX = imageMeta.BayerOffsetX;
        var bayerOffsetY = imageMeta.BayerOffsetY;

        var bayerPattern = imageMeta.SensorType.GetBayerPatternMatrix(bayerOffsetX, bayerOffsetY);

        // Pre-compute pattern for rows (avoids modulo in inner loop)
        var pattern00 = bayerPattern[0, 0];
        var pattern01 = bayerPattern[0, 1];
        var pattern10 = bayerPattern[1, 0];
        var pattern11 = bayerPattern[1, 1];

        const int R = 0, G = 1, B = 2;
        const int radius = 2;

        // Process interior pixels in parallel (where full VNG can be applied)
        await Parallel.ForAsync(radius,
            height - radius,
            new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Environment.ProcessorCount * 4 },
            async (y, ct) => await Task.Run(() =>
            {
                // Pre-select pattern row based on y % 2
                int patternEven = (y & 1) == 0 ? pattern00 : pattern10;
                int patternOdd = (y & 1) == 0 ? pattern01 : pattern11;

                for (int x = radius; x < width - radius; x++)
                {
                    int knownColor = (x & 1) == 0 ? patternEven : patternOdd;

                    // Copy known value
                    float rawValue = data[0, y, x];
                    debayered[knownColor, y, x] = rawValue;

                    // Interpolate missing colors based on which color we have
                    if (knownColor == G)
                    {
                        // At green pixel: interpolate R and B
                        // Check if R is on horizontal or vertical neighbors
                        int neighborColor = (x & 1) == 0 ? patternOdd : patternEven;
                        bool rOnHorizontal = neighborColor == R;

                        debayered[R, y, x] = rOnHorizontal
                            ? InterpolateHorizontalVNG(x, y)
                            : InterpolateVerticalVNG(x, y);
                        debayered[B, y, x] = rOnHorizontal
                            ? InterpolateVerticalVNG(x, y)
                            : InterpolateHorizontalVNG(x, y);
                    }
                    else
                    {
                        // At R or B pixel: interpolate G and the opposite color
                        debayered[G, y, x] = InterpolateGreenAtRBVNG(x, y);
                        debayered[knownColor == R ? B : R, y, x] = InterpolateDiagonalVNG(x, y);
                    }
                }

                return ValueTask.CompletedTask;
            }, ct)
        );

        // Process edge pixels with simpler bilinear interpolation (not parallelized - small portion)
        ProcessEdgePixels(debayered, width, height, radius, bayerPattern);

        return new Image(debayered, BitDepth.Float32, maxValue, blackLevel, imageMeta with
        {
            SensorType = SensorType.Color,
            BayerOffsetX = 0,
            BayerOffsetY = 0
        });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private float InterpolateGreenAtRBVNG(int x, int y)
    {
        // Interpolate green at R or B position using 4 cardinal directions
        float center = data[0, y, x];

        // North: green at y-1, same color at y-2
        float gN = data[0, y - 1, x];
        float vN = data[0, y - 2, x];
        float gradN = MathF.Abs(MathF.FusedMultiplyAdd(2, gN, - vN - center));
        float valN = gN + (center - vN) * 0.5f;

        // South
        float gS = data[0, y + 1, x];
        float vS = data[0, y + 2, x];
        float gradS = MathF.Abs(MathF.FusedMultiplyAdd(2, gS, - center - vS));
        float valS = MathF.FusedMultiplyAdd(center - vS, 0.5f, gS);

        // West
        float gW = data[0, y, x - 1];
        float vW = data[0, y, x - 2];
        float gradW = MathF.Abs(2 * gW - vW - center);
        float valW = MathF.FusedMultiplyAdd(center - vW, 0.5f, gW);

        // East
        float gE = data[0, y, x + 1];
        float vE = data[0, y, x + 2];
        float gradE = MathF.Abs(2 * gE - center - vE);
        float valE = MathF.FusedMultiplyAdd(center - vE, 0.5f, gE);

        // Find minimum gradient and threshold
        float minGrad = MathF.Min(MathF.Min(gradN, gradS), MathF.Min(gradW, gradE));
        float threshold = minGrad * 1.5f;

        // Average values within threshold
        float sum = 0;
        int count = 0;

        if (gradN <= threshold) { sum += valN; count++; }
        if (gradS <= threshold) { sum += valS; count++; }
        if (gradW <= threshold) { sum += valW; count++; }
        if (gradE <= threshold) { sum += valE; count++; }

        return count > 0 ? sum / count : valN;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private float InterpolateHorizontalVNG(int x, int y)
    {
        // Interpolate R or B at green position from horizontal neighbors
        float center = data[0, y, x];
        float left = data[0, y, x - 1];
        float right = data[0, y, x + 1];

        float gradL = MathF.Abs(left - center);
        float gradR = MathF.Abs(right - center);

        float minGrad = MathF.Min(gradL, gradR);
        float threshold = MathF.FusedMultiplyAdd(minGrad, 1.5f, 0.01f);

        float sum = 0;
        int count = 0;

        if (gradL <= threshold) { sum += left; count++; }
        if (gradR <= threshold) { sum += right; count++; }

        return count > 0 ? sum / count : (left + right) * 0.5f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private float InterpolateVerticalVNG(int x, int y)
    {
        // Interpolate R or B at green position from vertical neighbors
        float center = data[0, y, x];
        float top = data[0, y - 1, x];
        float bottom = data[0, y + 1, x];

        float gradT = MathF.Abs(top - center);
        float gradB = MathF.Abs(bottom - center);

        float minGrad = MathF.Min(gradT, gradB);
        float threshold = MathF.FusedMultiplyAdd(minGrad, 1.5f, 0.01f);

        float sum = 0;
        int count = 0;

        if (gradT <= threshold) { sum += top; count++; }
        if (gradB <= threshold) { sum += bottom; count++; }

        return count > 0 ? sum / count : (top + bottom) * 0.5f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private float InterpolateDiagonalVNG(int x, int y)
    {
        // Interpolate R at B or B at R from 4 diagonal neighbors
        float center = data[0, y, x];

        float nw = data[0, y - 1, x - 1];
        float ne = data[0, y - 1, x + 1];
        float sw = data[0, y + 1, x - 1];
        float se = data[0, y + 1, x + 1];

        // Green values at cardinal neighbors
        float gN = data[0, y - 1, x];
        float gS = data[0, y + 1, x];
        float gW = data[0, y, x - 1];
        float gE = data[0, y, x + 1];

        // Calculate gradients including green channel differences
        float gradNW = MathF.Abs(nw - center) + MathF.Abs(gN - gW);
        float gradNE = MathF.Abs(ne - center) + MathF.Abs(gN - gE);
        float gradSW = MathF.Abs(sw - center) + MathF.Abs(gS - gW);
        float gradSE = MathF.Abs(se - center) + MathF.Abs(gS - gE);

        float minGrad = MathF.Min(MathF.Min(gradNW, gradNE), MathF.Min(gradSW, gradSE));
        float threshold = MathF.FusedMultiplyAdd(minGrad, 1.5f, 0.01f);

        float sum = 0;
        int count = 0;

        if (gradNW <= threshold) { sum += nw; count++; }
        if (gradNE <= threshold) { sum += ne; count++; }
        if (gradSW <= threshold) { sum += sw; count++; }
        if (gradSE <= threshold) { sum += se; count++; }

        return count > 0 ? sum / count : (nw + ne + sw + se) * 0.25f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private void ProcessEdgePixels(float[,,] debayered, int width, int height, int radius, int[,] bayerPattern)
    {
        // Top and bottom edges
        for (int y = 0; y < height; y++)
        {
            if (y >= radius && y < height - radius) continue; // Skip interior rows

            for (int x = 0; x < width; x++)
            {
                ProcessEdgePixel(debayered, x, y, width, height, bayerPattern);
            }
        }

        // Left and right edges (excluding corners already processed)
        for (int y = radius; y < height - radius; y++)
        {
            for (int x = 0; x < radius; x++)
            {
                ProcessEdgePixel(debayered, x, y, width, height, bayerPattern);
            }
            for (int x = width - radius; x < width; x++)
            {
                ProcessEdgePixel(debayered, x, y, width, height, bayerPattern);
            }
        }
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private void ProcessEdgePixel(float[,,] debayered, int x, int y, int width, int height, int[,] bayerPattern)
    {
        int knownColor = bayerPattern[y & 1, x & 1];
        debayered[knownColor, y, x] = data[0, y, x];

        for (int c = 0; c < 3; c++)
        {
            if (c != knownColor)
            {
                debayered[c, y, x] = BilinearInterpolateColorFast(x, y, c, width, height, bayerPattern);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private float BilinearInterpolateColorFast(int x, int y, int targetColor, int width, int height, int[,] bayerPattern)
    {
        float sum = 0;
        int count = 0;

        int yMin = Math.Max(0, y - 2);
        int yMax = Math.Min(height - 1, y + 2);
        int xMin = Math.Max(0, x - 2);
        int xMax = Math.Min(width - 1, x + 2);

        for (int ny = yMin; ny <= yMax; ny++)
        {
            int patternY = ny & 1;
            for (int nx = xMin; nx <= xMax; nx++)
            {
                if (bayerPattern[patternY, nx & 1] == targetColor)
                {
                    sum += data[0, ny, nx];
                    count++;
                }
            }
        }

        return count > 0 ? sum / count : 0;
    }

    /// <summary>
    /// Scales the floating-point values of the image data to a specified maximum value.
    /// </summary>
    /// <param name="missingValue">Use this value for missing pixels</param>
    /// <remarks>This method is intended for images that have been obtained via <see cref="ScaleFloatValuesToUnit"/> floating-point data (i.e., Float32 bit
    /// depth and a maximum value of 1.0). If the image is already denormalized or uses a different bit depth, no
    /// scaling is performed.</remarks>
    /// <param name="newMaxValue">The new maximum value to which the floating-point values will be scaled. Must be greater than zero.</param>
    /// <returns>An Image instance containing the denormalized data, with values scaled to the specified maximum value. If the
    /// image is already denormalized or not in Float32 bit depth, the original image is returned unchanged.</returns>
    public Image ScaleFloatValues(float newMaxValue, float missingValue = float.NaN)
    {
        if (BitDepth != BitDepth.Float32 || (newMaxValue != MaxValue && MaxValue > 1.0f + float.Epsilon))
        {
            return ScaleFloatValuesToUnit().ScaleFloatValues(newMaxValue);
        }

        var (channelCount, width, height) = Shape;
        var denormalized = new float[channelCount, height, width];

        for (var c = 0; c < channelCount; c++)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    var value = data[c, y, x];
                    if (!float.IsNaN(value))
                    {
                        denormalized[c, y, x] = value * newMaxValue;
                    }
                    else
                    {
                        denormalized[c, y, x] = missingValue;
                    }
                }
            }
        }

        return new Image(denormalized, BitDepth.Float32, newMaxValue, blackLevel * newMaxValue, imageMeta);
    }

    /// <summary>
    /// Divides image by <see cref="MaxValue"/>, thus scaling the floating-point values to a maximum of 1.0.
    /// </summary>
    /// <param name="missingValue">Use this value for missing pixels</param>
    /// <returns></returns>
    public Image ScaleFloatValuesToUnit(float missingValue = float.NaN)
    {
        // NO-OP for already normalized images
        if (MaxValue <= 1.0f)
        {
            return this;
        }

        var (channelCount, width, height) = Shape;
        var normalized = new float[channelCount, height, width];

        for (var c = 0; c < channelCount; c++)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    var value = data[c, y, x];
                    if (!float.IsNaN(value))
                    {
                        normalized[c, y, x] = value / MaxValue;
                    }
                    else
                    {
                        normalized[c, y, x] = missingValue;
                    }
                }
            }
        }

        return new Image(normalized, BitDepth.Float32, 1.0f, blackLevel / maxValue, imageMeta);
    }

    /// <summary>
    /// Finds the offset and rotation between this image and another image by matching stars.
    /// </summary>
    /// <param name="other">Image to base rotation and offset on (i.e. reference image)</param>
    /// <param name="snrMin">Mininum signal to noise ratio to consider for stars</param>
    /// <param name="maxStars">Maximum number of stars to consider</param>
    /// <param name="maxRetries"></param>
    /// <param name="minStars">Mininum of stars required to find matching quads</param>
    /// <param name="quadTolerance">factor of how difference to consider quads matching.</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <remarks>
    /// Returns null if not enough stars are found or no match is found.
    /// Note that the returned offset is in the coordinate system of this image, so it can be used to align this image to the other image.
    /// Currently only images of same pixel scale are supported.
    /// </remarks>
    public async Task<Matrix3x2?> FindOffsetAndRotationAsync(Image other, int channel, int otherChannel, float snrMin = 20f, int maxStars = 500, int maxRetries = 2, int minStars = 24, float quadTolerance = 0.008f, CancellationToken cancellationToken = default)
    {
        var starList1Task = FindStarsAsync(channel, snrMin, maxStars, maxRetries, cancellationToken);
        var starList2Task = other.FindStarsAsync(otherChannel, snrMin, maxStars, maxRetries, cancellationToken);

        var starLists = await Task.WhenAll(starList1Task, starList2Task);

        if (starLists[0].Count >= minStars && starLists[1].Count >= minStars)
        {
            return await new SortedStarList(starLists[0]).FindOffsetAndRotationAsync(starLists[1], minStars / 4, quadTolerance);
        }

        return null;
    }

    /// <summary>
    /// Transforms the image using the given 3x2 affine transformation matrix. The output image will be large enough to contain the entire transformed image. The pixel values are calculated using bilinear interpolation. Note that the transformation is applied in reverse order, so the inverse of the given matrix is used to calculate the source pixel for each destination pixel. This allows for correct handling of rotations and scaling. If the transformation is not invertible, an exception is thrown. Note that this method can be computationally expensive for large images or complex transformations, so it should be used with caution. Also note that this method does not perform any cropping or padding.
    /// </summary>
    /// <param name="transform"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    public Task<Image> TransformAsync(in Matrix3x2 transform, CancellationToken cancellationToken = default)
    {
        if (transform.IsIdentity)
        {
            return Task.FromResult(this);
        }

        var (_, width, height) = Shape;
        var tl_p = Vector2.Transform(Vector2.Zero, transform);
        var tr_p = Vector2.Transform(new Vector2(width, 0), transform);
        var bl_p = Vector2.Transform(new Vector2(0, height), transform);
        var br_p = Vector2.Transform(new Vector2(width, height), transform);

        var top = MathF.Min(MathF.Min(tl_p.Y, tr_p.Y), MathF.Min(bl_p.Y, br_p.Y));
        var left = MathF.Min(MathF.Min(tl_p.X, tr_p.X), MathF.Min(bl_p.X, br_p.X));
        var bottom = MathF.Max(MathF.Max(tl_p.Y, tr_p.Y), MathF.Max(bl_p.Y, br_p.Y));
        var right = MathF.Max(MathF.Max(tl_p.X, tr_p.X), MathF.Max(bl_p.X, br_p.X));

        return DoTransformationAsync(transform, new Vector2(left, top), new Vector2(right, bottom), cancellationToken);
    }

    private async Task<Image> DoTransformationAsync(Matrix3x2 transform, Vector2 tl, Vector2 br, CancellationToken cancellationToken = default)
    {
        var translated = transform * Matrix3x2.CreateTranslation(-tl);
        if (!Matrix3x2.Invert(translated, out var inverseTransform))
        {
            throw new ArgumentException("Transform is not invertible", nameof(transform));
        }

        var newWidth = (int)MathF.Ceiling(br.X - tl.X);
        var newHeight = (int)MathF.Ceiling(br.Y - tl.Y);

        var channelCount = ChannelCount;
        var width = Width;
        var height = Height;
        var transformedData = new float[channelCount, newHeight, newWidth];

        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Environment.ProcessorCount * 4
        };

        for (var c = 0; c < channelCount; c++)
        {
            var channel = c;
            await Parallel.ForAsync(0, newHeight, parallelOptions, async (y, ct) => await Task.Run(() =>
            {
                for (var x = 0; x < newWidth; x++)
                {
                    var sourcePos = Vector2.Transform(new Vector2(x, y), inverseTransform);
                    transformedData[channel, y, x] = sourcePos.X >= 0 && sourcePos.X < width && sourcePos.Y >= 0 && sourcePos.Y < height
                        ? SubpixelValue(channel, sourcePos.X, sourcePos.Y)
                        : float.NaN;
                }

                return ValueTask.CompletedTask;
            }, ct));
        }

        return new Image(transformedData, BitDepth.Float32, MaxValue, BlackLevel, ImageMeta);
    }

    public async Task<IMagickImage<float>> ToMagickImageAsync(DebayerAlgorithm debayerAlgorithm = DebayerAlgorithm.VNG, CancellationToken cancellationToken = default)
    {
        var scaled = BitDepth != BitDepth.Float32 ? ScaleFloatValues(MaxValue, missingValue: 0f) : this;

        Image debayered;
        if (scaled.ImageMeta.SensorType is SensorType.RGGB)
        {
            if (debayerAlgorithm is DebayerAlgorithm.None)
            {
                throw new ArgumentException("Must specify an algorithm for debayering", nameof(debayerAlgorithm));
            }
            debayered = await scaled.DebayerAsync(debayerAlgorithm, cancellationToken);
        }
        else
        {
            debayered = scaled;
        }

        return debayered.DoToMagickImage();
    }

    /// <summary>
    /// Assumes that imge has been converted to floats and debayered.
    /// </summary>
    /// <returns></returns>
    private IMagickImage<float> DoToMagickImage()
    {
        var (channelCount, width, height) = Shape;
        var firstChannel = ChannelToImage(0); // mono or red

        if (channelCount is 3)
        {
            var blue = ChannelToImage(1);
            var green = ChannelToImage(2);

            using var coll = new MagickImageCollection
            {
                firstChannel,
                blue,
                green
            };
            return coll.Combine(ColorSpace.sRGB);
        }
        else
        {
            return firstChannel;
        }

        MagickImage ChannelToImage(int channel)
        {
            var image = new MagickImage(MagickColors.Black, (uint)width, (uint)height)
            {
                Format = MagickFormat.Tiff,
                Depth = 32,
                Endian = BitConverter.IsLittleEndian ? Endian.LSB : Endian.MSB,
                ColorType = ColorType.Grayscale
            };

            using var pix = image.GetPixelsUnsafe();
            pix.SetPixels(data.AsSpan(channel));

            return image;
        }
    }

    public async Task<Image> StretchLinkedAsync(double stretchFactor = 0.2d, double shadowsClipping = -3d, DebayerAlgorithm debayerAlgorithm = DebayerAlgorithm.VNG, CancellationToken cancellationToken = default)
    {

        if (imageMeta.SensorType is SensorType.RGGB)
        {
            var debayered = await DebayerAsync(debayerAlgorithm, cancellationToken);
            return await debayered.StretchLinkedAsync(stretchFactor, shadowsClipping, DebayerAlgorithm.None, cancellationToken);
        }
        else if (imageMeta.SensorType is SensorType.Monochrome)
        {
            return await StretchUnlinkedAsync(stretchFactor, shadowsClipping, DebayerAlgorithm.None, cancellationToken);
        }
        else
        {
            var (channelCount, width, height) = Shape;

            var (pedestral, median, mad) = GetPedestralMedianAndMADScaledToUnit(0);

            var stretchedData = new float[channelCount, height, width];
            for (var c = 0; c < channelCount; c++)
            {
                await StretchChannelAsync(stretchedData, c, stretchFactor, shadowsClipping, pedestral, median, mad, cancellationToken);
            }
            // stretched images are always normalized to unit, so max value is 1.0f
            var stretchedImage = new Image(stretchedData, BitDepth.Float32, 1.0f, 0, imageMeta);
            // rescale if required
            return MaxValue > stretchedImage.MaxValue ? stretchedImage.ScaleFloatValues(MaxValue) : stretchedImage;
        }
    }

    public async Task<Image> StretchUnlinkedAsync(double stretchFactor = 0.2d, double shadowsClipping = -3d, DebayerAlgorithm debayerAlgorithm = DebayerAlgorithm.VNG, CancellationToken cancellationToken = default)
    {
        if (imageMeta.SensorType is SensorType.RGGB)
        {
            var debayered = await DebayerAsync(debayerAlgorithm, cancellationToken);
            return await debayered.StretchUnlinkedAsync(stretchFactor, shadowsClipping, DebayerAlgorithm.None, cancellationToken);
        }
        
        var (channelCount, width, height) = Shape;

        var stretchedData = new float[channelCount, height, width];

        for (var c = 0; c < channelCount; c++)
        {
            var (pedestral, median, mad) = GetPedestralMedianAndMADScaledToUnit(c);
            await StretchChannelAsync(stretchedData, c, stretchFactor, shadowsClipping, pedestral, median, mad, cancellationToken);
        }

        // stretched images are always normalized to unit, so max value is 1.0f
        var stretchedImage = new Image(stretchedData, BitDepth.Float32, 1.0f, 0, imageMeta);

        // rescale if required
        return MaxValue > stretchedImage.MaxValue ? stretchedImage.ScaleFloatValues(MaxValue) : stretchedImage;
    }

    private async Task StretchChannelAsync(float[,,] stretched, int channel, double stretchFactor, double shadowsClipping, float pedestral, float median, float mad, CancellationToken cancellationToken = default)
    {
        var (channelCount, width, height) = Shape;

        if (channel < 0 || channel >= channelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }

        var needsNorm = MaxValue > 1.0f + float.Epsilon;
        var normFactor = 1.0 / MaxValue;

        double shadows, midtones, highlights;

        // assume the image is inverted or overexposed when median is higher than half of the possible value
        if (median > 0.5)
        {
            shadows = 0f;
            highlights = median - shadowsClipping * mad * MAD_TO_SD;
            midtones = MidtonesTransferFunction(stretchFactor, 1f - (highlights - median));
        }
        else
        {
            shadows = median + shadowsClipping * mad * MAD_TO_SD;
            midtones = MidtonesTransferFunction(stretchFactor, median - shadows);
            highlights = 1;
        }

        await Parallel.ForAsync(0, height, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Environment.ProcessorCount * 4 }, async (y, ct) => await Task.Run(() =>
        {
            for (var x = 0; x < width; x++)
            {
                var value = data[channel, y, x];
                if (!float.IsNaN(value))
                {
                    var normValue = (needsNorm ? value * normFactor : value) - pedestral;
                    stretched[channel, y, x] = (float)MidtonesTransferFunction(midtones, 1 - highlights + normValue - shadows);
                }
                else
                {
                    stretched[channel, y, x] = float.NaN;
                }
            }
            return ValueTask.CompletedTask;
        }, ct));
    }

    /// <summary>
    /// Adjusts x for a given midToneBalance
    /// </summary>
    /// <param name="midToneBalance"></param>
    /// <param name="value"></param>
    /// <returns></returns>
    private static double MidtonesTransferFunction(double midToneBalance, double value)
    {
        var clamped = Math.Clamp(value, 0, 1d);
        if (value == clamped)
        {
            return (midToneBalance - 1) * value / Math.FusedMultiplyAdd(Math.FusedMultiplyAdd(2, midToneBalance, -1), value, - midToneBalance);
        }
        else
        {
            return clamped;
        }
    }
}