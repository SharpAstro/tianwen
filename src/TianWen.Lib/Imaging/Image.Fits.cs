using CommunityToolkit.HighPerformance;
using nom.tam.fits;
using nom.tam.util;
using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Numerics;

namespace TianWen.Lib.Imaging;

public partial class Image
{
    public static bool TryReadFitsFile(string fileName, [NotNullWhen(true)] out Image? image)
    {
        using var bufferedReader = new BufferedFile(fileName, FileAccess.Read, FileShare.Read, 1000 * 2088);
        return TryReadFitsFile(new Fits(bufferedReader, fileName.EndsWith(".gz")), out image);
    }

    public static bool TryReadFitsFile(Fits fitsFile, [NotNullWhen(true)] out Image? image)
    {
        var hdu = fitsFile.ReadHDU();
        if (hdu?.Axes?.Length is not { } axisLength
            || hdu.Data is not ImageData imageData
            || imageData.DataArray is not Array dataArray
            || dataArray.Length == 0
            || !(BitDepth.FromValue(hdu.BitPix) is { } bitDepth)
        )
        {
            image = default;
            return false;
        }

        int height, width, channelCount;

        switch (axisLength)
        {
            case 2:
                height = hdu.Axes[0];
                width = hdu.Axes[1];
                channelCount = 1;
                break;

            case 3:
                channelCount = hdu.Axes[0];
                height = hdu.Axes[1];
                width = hdu.Axes[2];
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
        var blackLevel = hdu.Header.GetFloatValue("BLKLEVEL", hdu.Header.GetFloatValue("OFFSET", 0f));
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
            [hdu.Header.GetStringValue("BAYERPAT"), hdu.Header.GetStringValue("COLORTYP")]
        );
        var latitude = hdu.Header.GetFloatValue("LATITUDE", float.NaN);
        var longitude = hdu.Header.GetFloatValue("LONGITUDE", float.NaN);

        var minValue = (float)hdu.MinimumValue;
        var maxValue = (float)hdu.MaximumValue;
        bool needsMinMaxValRecalc = float.IsNaN(minValue) || minValue < 0 || float.IsNaN(maxValue) || maxValue is <= 0 || maxValue <= minValue;
        if (needsMinMaxValRecalc)
        {
            maxValue = float.MinValue;
            minValue = float.MaxValue;
        }

        bool trivialScaling = bscale == 1f && bzero == 0f;
        float[,,] imgArray;

        // Fast path: float 3D data with trivial scaling — reuse the array directly (zero-copy)
        if (trivialScaling && dataArray is float[,,] floatSrc3d)
        {
            imgArray = floatSrc3d;
            if (needsMinMaxValRecalc)
            {
                RecalcMinMax(imgArray, channelCount, ref minValue, ref maxValue);
            }
        }
        // Float 2D with trivial scaling — single BlockCopy into 3D wrapper
        else if (trivialScaling && dataArray is float[,] floatSrc2d)
        {
            imgArray = new float[1, height, width];
            Buffer.BlockCopy(floatSrc2d, 0, imgArray, 0, sizeof(float) * height * width);
            if (needsMinMaxValRecalc)
            {
                RecalcMinMax(imgArray, channelCount, ref minValue, ref maxValue);
            }
        }
        else
        {
            imgArray = new float[channelCount, height, width];
            // Generic conversion path for all element types with bscale/bzero application
            switch (dataArray)
            {
                case byte[,] src: Convert2D(src); break;
                case byte[,,] src: Convert3D(src); break;
                case short[,] src: Convert2D(src); break;
                case short[,,] src: Convert3D(src); break;
                case int[,] src: Convert2D(src); break;
                case int[,,] src: Convert3D(src); break;
                case float[,] src: Convert2D(src); break;
                case float[,,] src: Convert3D(src); break;
                default:
                    image = null;
                    return false;
            }
        }

        void Convert2D<T>(T[,] src) where T : struct, INumberBase<T>
        {
            var dst = imgArray.AsSpan2D(0);
            for (int h = 0; h < height; h++)
            {
                var row = dst.GetRowSpan(h);
                for (int w = 0; w < width; w++)
                {
                    var val = bscale * float.CreateTruncating(src[h, w]) + bzero;
                    row[w] = val;
                    if (needsMinMaxValRecalc && !float.IsNaN(val))
                    {
                        maxValue = MathF.Max(maxValue, val);
                        minValue = MathF.Min(minValue, val);
                    }
                }
            }
        }

        void Convert3D<T>(T[,,] src) where T : struct, INumberBase<T>
        {
            for (int c = 0; c < channelCount; c++)
            {
                var dst = imgArray.AsSpan2D(c);
                for (int h = 0; h < height; h++)
                {
                    var row = dst.GetRowSpan(h);
                    for (int w = 0; w < width; w++)
                    {
                        var val = bscale * float.CreateTruncating(src[c, h, w]) + bzero;
                        row[w] = val;
                        if (needsMinMaxValRecalc && !float.IsNaN(val))
                        {
                            maxValue = MathF.Max(maxValue, val);
                            minValue = MathF.Min(minValue, val);
                        }
                    }
                }
            }
        }

        static void RecalcMinMax(float[,,] imgArray, int channelCount, ref float minValue, ref float maxValue)
        {
            for (int c = 0; c < channelCount; c++)
            {
                var span = imgArray.AsSpan(c);
                for (int i = 0; i < span.Length; i++)
                {
                    var val = span[i];
                    if (!float.IsNaN(val))
                    {
                        maxValue = MathF.Max(maxValue, val);
                        minValue = MathF.Min(minValue, val);
                    }
                }
            }
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
        image = new Image(imgArray, bitDepth, maxValue, minValue, blackLevel, imageMeta);
        return true;
    }

    public void WriteToFitsFile(string fileName, WCS? wcs = null)
    {
        var (channelCount, width, height) = Shape;
        var fits = new Fits();
        Array arrayToWrite;
        int bzero;
        bool dataIsInt;
        switch (bitDepth)
        {
            case BitDepth.Int8:
                var byteArray = new byte[channelCount, height, width];
                bzero = 0;
                dataIsInt = true;
                for (var c = 0; c < channelCount; c++)
                {
                    for (var h = 0; h < height; h++)
                    {
                        for (var w = 0; w < width; w++)
                        {
                            byteArray[c, h, w] = (byte)data[c, h, w];
                        }
                    }
                }
                arrayToWrite = byteArray;
                break;

            case BitDepth.Int16:
                var shortArray = new short[channelCount, height, width];
                bzero = 32768;
                dataIsInt = true;
                for (var c = 0; c < channelCount; c++)
                {
                    for (var h = 0; h < height; h++)
                    {
                        for (var w = 0; w < width; w++)
                        {
                            shortArray[c, h, w] = (short)(data[c, h, w] - bzero);
                        }
                    }
                }
                arrayToWrite = shortArray;
                break;

            case BitDepth.Float32:
                bzero = 0;
                dataIsInt = false;
                arrayToWrite = data;
                break;

            default:
                throw new NotSupportedException($"Bits per pixel {bitDepth} is not supported");
        }
        var basicHdu = FitsFactory.HDUFactory(arrayToWrite);
        basicHdu.Header.Bitpix = (int)bitDepth;
        AddHeaderValueIfHasValue("BZERO", bzero, "offset data range to that of unsigned short");
        AddHeaderValueIfHasValue("BSCALE", 1, "default scaling factor");
        AddHeaderValueIfHasValue("BLKLEVEL", blackLevel, "", isDataValue: true);
        AddHeaderValueIfHasValue("OFFSET", blackLevel, "", isDataValue: true);
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
        if (wcs is { } wcsValue)
        {
            wcsValue.WriteToHeader(basicHdu.Header);
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
}
