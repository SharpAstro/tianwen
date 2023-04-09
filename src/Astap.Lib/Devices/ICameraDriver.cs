using Astap.Lib.Imaging;
using CommunityToolkit.HighPerformance;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Astap.Lib.Devices;

public interface ICameraDriver : IDeviceDriver
{
    bool CanGetCoolerPower { get; }

    bool CanGetCoolerOn { get; }

    bool CanSetCoolerOn { get; }

    bool CanGetCCDTemperature { get; }

    bool CanSetCCDTemperature { get; }

    bool CanGetHeatsinkTemperature { get; }

    bool CanStopExposure { get; }

    bool CanAbortExposure { get; }

    bool CanFastReadout { get; }

    bool CanSetBitDepth { get; }

    /// <summary>
    /// True if <see cref="Gain"/> value is supported. Exclusive with <see cref="UsesGainMode"/>.
    /// </summary>
    bool UsesGainValue { get; }

    /// <summary>
    /// True if <see cref="Gain"/> mode is supported. Exclusive with <see cref="UsesGainValue"/>.
    /// </summary>
    bool UsesGainMode { get; }

    /// <summary>
    /// True if <see cref="Offset"/> value mode is supported. Exclusive with  <see cref="UsesOffsetMode"/>.
    /// </summary>
    bool UsesOffsetValue { get; }

    /// <summary>
    /// True if <see cref="Offset"/> mode is supported. Exclusive with <see cref="UsesOffsetValue"/>.
    /// </summary>
    bool UsesOffsetMode { get; }

    double PixelSizeX { get; }

    double PixelSizeY { get; }

    /// <summary>
    /// Returns the maximum allowed binning for the X camera axis.
    /// </summary>
    short MaxBinX { get; }

    /// <summary>
    /// Returns the maximum allowed binning for the Y camera axis.
    /// </summary>
    short MaxBinY { get; }

    /// <summary>
    /// Sets the binning factor for the X axis, also returns the current value.
    /// </summary>
    int BinX { get; set; }

    /// <summary>
    /// Sets the binning factor for the Y axis, also returns the current value.
    /// </summary>
    int BinY { get; set; }

    /// <summary>
    /// Sets the subframe start position for the X axis (0 based) and returns the current value.
    /// </summary>
    int StartX { get; set; }

    /// <summary>
    /// Sets the subframe start position for the Y axis (0 based). Also returns the current value.
    /// </summary>
    int StartY { get; set; }

    /// <summary>
    /// Returns the width of the camera chip in unbinned pixels or a value smaller than 0 when not initialised.
    /// </summary>
    int CameraXSize { get; }

    /// <summary>
    /// Returns the height of the camera chip in unbinned pixels or a value smaller than 0 when not initialised.
    /// </summary>
    int CameraYSize { get; }

    string? ReadoutMode { get; set; }

    bool FastReadout { get; set; }

    int[,]? ImageData { get; }

    bool ImageReady { get; }

    bool CoolerOn { get; set; }

    double CoolerPower { get; }

    /// <summary>
    /// Sets the camera cooler setpoint in degrees Celsius, and returns the current setpoint.
    /// </summary>
    double SetCCDTemperature { get; set; }

    /// <summary>
    /// Returns the current heat sink temperature (called "ambient temperature" by some manufacturers) in degrees Celsius.
    /// </summary>
    double HeatSinkTemperature { get; }

    /// <summary>
    /// Returns the current CCD temperature in degrees Celsius.
    /// </summary>
    double CCDTemperature { get; }

    /// <summary>
    /// Returns bit depth, usually <see cref="BitDepth.Int8"/> or <see cref="BitDepth.Int16"/> or <see langword="null"/> if camera is not initialised.
    /// Will throw if <see cref="CanSetBitDepth"/> is <see langword="false" /> and an attempt to set value is made.
    /// </summary>
    BitDepth? BitDepth { get; set; }

    short Gain { get; set; }

    short GainMin { get; }

    short GainMax { get; }

    IEnumerable<string> Gains { get; }

    string? GainMode
    {
        get => Connected && UsesGainMode && Gains.ToList() is { Count: > 0 } gains && Gain is short idx && idx >= 0 && idx < gains.Count
            ? gains[idx]
            : null;

        set
        {
            if (Connected && UsesGainMode && Gains.ToList() is { Count: > 0} gains && value is not null && gains.IndexOf(value) is var idx and <= short.MaxValue)
            {
                Gain = (short)idx;
            }
        }
    }

    int Offset { get; set; }

    int OffsetMin { get; }

    int OffsetMax { get; }

    string? OffsetMode
    {
        get => Connected && UsesOffsetMode && Offsets.ToList() is { Count: > 0 } offsets && Offset is int idx && idx >= 0 && idx < offsets.Count
            ? offsets[idx]
            : null;

        set
        {
            if (Connected && UsesOffsetMode && Offsets.ToList() is { Count: > 0 } offsets && value is not null && offsets.IndexOf(value) is var idx)
            {
                Offset = idx;
            }
        }
    }

    IEnumerable<string> Offsets { get; }

    double ExposureResolution { get; }

    void StartExposure(TimeSpan duration, bool light);

    /// <summary>
    /// Should only be called when <see cref="CanStopExposure"/> is true.
    /// </summary>
    void StopExposure();

    /// <summary>
    /// Should only be called when <see cref="CanAbortExposure"/> is true.
    /// </summary>
    void AbortExposure();

    /// <summary>
    /// Reports the maximum ADU value the camera can produce.
    /// </summary>
    int MaxADU { get; }

    /// <summary>
    /// Reports the full well capacity of the camera in electrons, at the current camera settings (binning, SetupDialog settings, etc.)
    /// </summary>
    double FullWellCapacity { get; }

    /// <summary>
    /// Returns the gain of the camera in photoelectrons per A/D unit.
    /// </summary>
    double ElectronsPerADU { get; }

    DateTime LastExposureStartTime { get; }

    TimeSpan LastExposureDuration { get; }

    string? Telescope { get; set; }

    int FocalLength { get; set; }

    Filter Filter { get; set; }

    int FocusPos { get; set; }

    SensorType SensorType { get; }

    int BayerOffsetX { get; }

    int BayerOffsetY { get; }

    CameraState CameraState { get; }

    ImageSourceFormat ImageSourceFormat { get; }

    Image? Image => Connected && ImageReady && ImageData is { Length: > 0 } data && BitDepth is { } bitDepth && bitDepth.IsIntegral()
        ? DataToImage(
            data,
            ImageSourceFormat,
            bitDepth,
            Offset,
            new ImageMeta(
                Name,
                LastExposureStartTime,
                LastExposureDuration,
                Telescope ?? "",
                (float)PixelSizeX,
                (float)PixelSizeY,
                FocalLength,
                FocusPos,
                Filter,
                BinX,
                BinY,
                (float)CCDTemperature,
                SensorType,
                SensorType != SensorType.Monochrome ? BayerOffsetX : 0,
                SensorType != SensorType.Monochrome ? BayerOffsetY : 0,
                RowOrder.TopDown
            )
        )
        : null;

    /// <summary>
    /// Returns an <see cref="Image"/> by transposing and converting image source data if required to Height X Width X 32-bit floats.
    /// </summary>
    /// <param name="sourceData">2d image array</param>
    /// <param name="sourceFormat">source format of <paramref name="sourceData"/></param>
    /// <param name="bitDepth">either 8 or 16, -32 for float is not yet supported</param>
    /// <param name="blackLevel">black level or offset</param>
    /// <param name="imageMeta">image meta data</param>
    /// <returns>image from data, transposed and transformed to 32-bit floats</returns>
    public static Image DataToImage(int[,] sourceData, ImageSourceFormat sourceFormat, BitDepth bitDepth, float blackLevel, in ImageMeta imageMeta)
    {
        var width = sourceData.GetLength(0);
        var height = sourceData.GetLength(1);
        var span2d = sourceData.AsSpan2D();

        var maxVal = 0f;
        var targetData = new float[height, width];

        switch (sourceFormat)
        {
            case ImageSourceFormat.WidthXHeightLE:
                for (var h = 0; h < height; h++)
                {
                    int w = 0;
                    foreach (var val in span2d.GetColumn(h))
                    {
                        float valF = val;
                        targetData[h, w++] = valF;
                        maxVal = MathF.Max(valF, maxVal);
                    }
                }
                break;

            case ImageSourceFormat.HeightXWidthLE:
                for (var h = 0; h < height; h++)
                {
                    for (var w = 0; w < width; w++)
                    {
                        float valF = sourceData[h, w];
                        targetData[h, w] = valF;
                        maxVal = MathF.Max(valF, maxVal);
                    }
                }
                break;


            default:
                throw new ArgumentException($"Source format {sourceFormat} is not supported!", nameof(sourceFormat));
        }

        return new Image(targetData, width, height, bitDepth, maxVal, blackLevel, imageMeta);
    }
}
