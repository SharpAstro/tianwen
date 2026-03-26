using CommunityToolkit.HighPerformance;
using nom.tam.fits;
using nom.tam.util;
using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using TianWen.Lib.Astrometry;

namespace TianWen.Lib.Imaging;

public partial class Image
{
    public static bool TryReadFitsFile(string fileName, [NotNullWhen(true)] out Image? image)
    {
        return TryReadFitsFile(fileName, out image, out _);
    }

    public static bool TryReadFitsFile(string fileName, [NotNullWhen(true)] out Image? image, out WCS? wcs)
    {
        using var bufferedReader = new BufferedFile(fileName, FileAccess.Read, FileShare.Read, 1000 * 2088);
        using var fitsFile = new Fits(bufferedReader, fileName.EndsWith(".gz"));
        return TryReadFitsFile(fitsFile, out image, out wcs);
    }

    public static bool TryReadFitsFile(Fits fitsFile, [NotNullWhen(true)] out Image? image)
    {
        return TryReadFitsFile(fitsFile, out image, out _);
    }

    public static bool TryReadFitsFile(Fits fitsFile, [NotNullWhen(true)] out Image? image, out WCS? wcs)
    {
        wcs = null;
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
        var pedestal = hdu.Header.GetFloatValue("PEDESTAL", 0f);
        var pixelScale = hdu.Header.GetFloatValue("PIXSCALE", hdu.Header.GetFloatValue("SCALE", float.NaN));
        var focalLength = hdu.Header.GetIntValue("FOCALLEN", -1);
        var focusPos = hdu.Header.GetIntValue("FOCUSPOS", hdu.Header.GetIntValue("FOCPOS", -1));
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
        var latitude = hdu.Header.GetFloatValue("SITELAT", float.NaN);
        var longitude = hdu.Header.GetFloatValue("SITELONG", float.NaN);
        var objectName = hdu.Header.GetStringValue("OBJECT") ?? "";
        var gain = (short)hdu.Header.GetIntValue("GAIN", -1);
        var camOffset = hdu.Header.GetIntValue("OFFSET", hdu.Header.GetIntValue("BLKLEVEL", hdu.Header.GetIntValue("CAMOFFS", -1)));
        var setCCDTemp = hdu.Header.GetFloatValue("SET-TEMP", float.NaN);
        var egain = hdu.Header.GetFloatValue("EGAIN", float.NaN);
        var swCreator = hdu.Header.GetStringValue("SWCREATE") ?? "";

        var minValue = (float)hdu.MinimumValue;
        var maxValue = (float)hdu.MaximumValue;
        bool needsMinMaxValRecalc = float.IsNaN(minValue) || minValue < 0 || float.IsNaN(maxValue) || maxValue is <= 0 || maxValue <= minValue;
        if (needsMinMaxValRecalc)
        {
            maxValue = float.MinValue;
            minValue = float.MaxValue;
        }

        bool trivialScaling = bscale == 1f && bzero == 0f;
        var imgChannels = new float[channelCount][,];

        // Use GetChannel API from FITS.Lib 4.2 for per-channel access
        for (int c = 0; c < channelCount; c++)
        {
            var channelArray = imageData.GetChannel(c);

            if (trivialScaling && channelArray is float[,] floatChannel)
            {
                // Zero-copy: reuse float[,] from FITS.Lib directly
                imgChannels[c] = floatChannel;
            }
            else
            {
                imgChannels[c] = Array2DPool<float>.Rent(height, width);
                switch (channelArray)
                {
                    case byte[,] src: ConvertChannel(src, imgChannels[c]); break;
                    case short[,] src: ConvertChannel(src, imgChannels[c]); break;
                    case int[,] src: ConvertChannel(src, imgChannels[c]); break;
                    case float[,] src: ConvertChannel(src, imgChannels[c]); break;
                    default:
                        image = null;
                        return false;
                }
            }
        }

        if (needsMinMaxValRecalc)
        {
            RecalcMinMax(imgChannels, channelCount, ref minValue, ref maxValue);
        }

        void ConvertChannel<T>(T[,] src, float[,] dst) where T : struct, INumberBase<T>
        {
            var dstSpan = dst.AsSpan2D();
            for (int h = 0; h < height; h++)
            {
                var row = dstSpan.GetRowSpan(h);
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

        static void RecalcMinMax(float[][,] channels, int channelCount, ref float minValue, ref float maxValue)
        {
            for (int c = 0; c < channelCount; c++)
            {
                var channel = channels[c];
                var span = MemoryMarshal.CreateReadOnlySpan(ref channel[0, 0], channel.Length);
                // MinNumber/MaxNumber skip NaN values (IEEE 754 minNum/maxNum semantics)
                maxValue = MathF.Max(maxValue, TensorPrimitives.MaxNumber(span));
                minValue = MathF.Min(minValue, TensorPrimitives.MinNumber(span));
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
            longitude,
            objectName,
            Gain: gain,
            Offset: camOffset,
            SetCCDTemperature: setCCDTemp,
            ElectronsPerADU: egain,
            SWCreator: swCreator
        );
        image = new Image(imgChannels, bitDepth, maxValue, minValue, pedestal, imageMeta);
        wcs = WCS.FromHeader(hdu.Header);
        return true;
    }

    public void WriteToFitsFile(string fileName, WCS? wcs = null)
    {
        var (channelCount, width, height) = Shape;
        using var fits = new Fits();
        Array arrayToWrite;
        int bzero;
        bool dataIsInt;
        switch (bitDepth)
        {
            case BitDepth.Int8:
                bzero = 0;
                dataIsInt = true;
                if (channelCount == 1)
                {
                    var byteArray = new byte[height, width];
                    for (var h = 0; h < height; h++)
                    {
                        for (var w = 0; w < width; w++)
                        {
                            byteArray[h, w] = (byte)data[0][h, w];
                        }
                    }
                    arrayToWrite = byteArray;
                }
                else
                {
                    var byteChannels = new byte[channelCount][,];
                    for (var c = 0; c < channelCount; c++)
                    {
                        byteChannels[c] = new byte[height, width];
                        for (var h = 0; h < height; h++)
                        {
                            for (var w = 0; w < width; w++)
                            {
                                byteChannels[c][h, w] = (byte)data[c][h, w];
                            }
                        }
                    }
                    arrayToWrite = byteChannels;
                }
                break;

            case BitDepth.Int16:
                bzero = 32768;
                dataIsInt = true;
                if (channelCount == 1)
                {
                    var shortArray = new short[height, width];
                    for (var h = 0; h < height; h++)
                    {
                        for (var w = 0; w < width; w++)
                        {
                            shortArray[h, w] = (short)(data[0][h, w] - bzero);
                        }
                    }
                    arrayToWrite = shortArray;
                }
                else
                {
                    var shortChannels = new short[channelCount][,];
                    for (var c = 0; c < channelCount; c++)
                    {
                        shortChannels[c] = new short[height, width];
                        for (var h = 0; h < height; h++)
                        {
                            for (var w = 0; w < width; w++)
                            {
                                shortChannels[c][h, w] = (short)(data[c][h, w] - bzero);
                            }
                        }
                    }
                    arrayToWrite = shortChannels;
                }
                break;

            case BitDepth.Float32:
                bzero = 0;
                dataIsInt = false;
                arrayToWrite = channelCount == 1 ? data[0] : data;
                break;

            default:
                throw new NotSupportedException($"Bits per pixel {bitDepth} is not supported");
        }
        var basicHdu = FitsFactory.HDUFactory(arrayToWrite);
        basicHdu.Header.Bitpix = (int)bitDepth;
        AddHeaderValueIfHasValue("BZERO", bzero, "offset data range to that of unsigned short");
        AddHeaderValueIfHasValue("BSCALE", 1, "default scaling factor");
        AddHeaderValueIfHasValue("PEDESTAL", pedestal, "", isDataValue: true);
        AddHeaderValueIfHasValue("XBINNING", imageMeta.BinX, "");
        AddHeaderValueIfHasValue("YBINNING", imageMeta.BinY, "");
        AddHeaderValueIfHasValue("XPIXSZ", imageMeta.PixelSizeX, "");
        AddHeaderValueIfHasValue("YPIXSZ", imageMeta.PixelSizeX, "");
        AddHeaderValueIfHasValue("DATE-OBS", FitsDate.GetFitsDateString(imageMeta.ExposureStartTime.UtcDateTime), "UT");
        AddHeaderValueIfHasValue("EXPTIME", imageMeta.ExposureDuration.TotalSeconds, "seconds");
        AddHeaderValueIfHasValue("IMAGETYP", imageMeta.FrameType, "");
        AddHeaderValueIfHasValue("FRAMETYP", imageMeta.FrameType, "");
        AddHeaderValueIfHasValue("DATAMIN", MinValue, "");
        AddHeaderValueIfHasValue("DATAMAX", MaxValue, "");
        AddHeaderValueIfHasValue("INSTRUME", imageMeta.Instrument, "");
        AddHeaderValueIfHasValue("TELESCOP", imageMeta.Telescope, "");
        AddHeaderValueIfHasValue("OBJECT", imageMeta.ObjectName, "");
        AddHeaderValueIfHasValue("ROWORDER", imageMeta.RowOrder, "");
        if (imageMeta.FocalLength > 0)
        {
            AddHeaderValueIfHasValue("FOCALLEN", imageMeta.FocalLength, "mm");
        }
        if (!double.IsNaN(imageMeta.DerivedPixelScale))
        {
            AddHeaderValueIfHasValue("SCALE", imageMeta.DerivedPixelScale, "arcsec/px");
            AddHeaderValueIfHasValue("PIXSCALE", imageMeta.DerivedPixelScale, "arcsec/px");
        }
        if (imageMeta.FocusPos >= 0)
        {
            AddHeaderValueIfHasValue("FOCUSPOS", imageMeta.FocusPos, "steps");
            AddHeaderValueIfHasValue("FOCPOS", imageMeta.FocusPos, "steps");
        }
        if (imageMeta.Filter is { Name: { Length: > 0 } filterName })
        {
            AddHeaderValueIfHasValue("FILTER", filterName, "");
        }
        AddHeaderValueIfHasValue("CCD-TEMP", imageMeta.CCDTemperature, "Celsius");
        AddHeaderValueIfHasValue("SET-TEMP", imageMeta.SetCCDTemperature, "Celsius");
        if (imageMeta.Gain >= 0)
        {
            AddHeaderValueIfHasValue("GAIN", (int)imageMeta.Gain, "");
        }
        if (imageMeta.Offset >= 0)
        {
            AddHeaderValueIfHasValue("OFFSET", imageMeta.Offset, "camera offset");
        }
        AddHeaderValueIfHasValue("EGAIN", imageMeta.ElectronsPerADU, "e-/ADU");
        AddHeaderValueIfHasValue("BAYOFFX", imageMeta.BayerOffsetX, "");
        AddHeaderValueIfHasValue("BAYOFFY", imageMeta.BayerOffsetY, "");
        AddHeaderValueIfHasValue("SITELAT", imageMeta.Latitude, "degrees");
        AddHeaderValueIfHasValue("SITELONG", imageMeta.Longitude, "degrees");
        if (!double.IsNaN(imageMeta.TargetRA) && !double.IsNaN(imageMeta.TargetDec))
        {
            AddHeaderValueIfHasValue("OBJCTRA", Astrometry.CoordinateUtils.HoursToHMS(imageMeta.TargetRA, ' '), "");
            AddHeaderValueIfHasValue("OBJCTDEC", Astrometry.CoordinateUtils.DegreesToDMS(imageMeta.TargetDec, degreeSign: ' '), "");
            AddHeaderValueIfHasValue("RA", imageMeta.TargetRA * 15.0, "degrees");
            AddHeaderValueIfHasValue("DEC", imageMeta.TargetDec, "degrees");
        }
        AddHeaderValueIfHasValue("SWCREATE", imageMeta.SWCreator, "");
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
                string s when !string.IsNullOrWhiteSpace(s) => new HeaderCard(key, s.Length <= 68 ? s : s[..68], comment),
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
