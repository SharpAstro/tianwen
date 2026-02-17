using CommunityToolkit.HighPerformance;
using nom.tam.fits;
using nom.tam.util;
using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;

namespace TianWen.Lib.Imaging;

public partial class Image
{
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
}
