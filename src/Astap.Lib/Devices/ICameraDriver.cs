using Astap.Lib.Imaging;
using CommunityToolkit.HighPerformance;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Astap.Lib.Devices;

public interface ICameraDriver : IDeviceDriver
{
    bool CanGetCoolerPower { get; }

    bool CanGetCoolerOn { get; }

    bool CanSetCCDTemperature { get; }

    bool CanGetHeatsinkTemperature { get; }

    bool CanStopExposure { get; }

    bool CanAbortExposure { get; }

    bool CanFastReadout { get; }

    /// <summary>
    /// True if <see cref="Gain"/> value is supported. Exclusive with <see cref="UsesGainMode"/>.
    /// </summary>
    bool UsesGainValue { get; }

    /// <summary>
    /// True if <see cref="Gain"/> mode is supported. Exclusive with <see cref="UsesGainValue"/>.
    /// </summary>
    bool UsesGainMode { get; }

    /// <summary>
    /// True if <see cref="Offset"/> value mode is supported. Exclusive with  <see cref="UseOffsetMode"/>.
    /// </summary>
    bool UsesOffsetValue { get; }

    /// <summary>
    /// True if <see cref="Offset"/> mode is supported. Exclusive with <see cref="UsesOffsetValue"/>.
    /// </summary>
    bool UseOffsetMode { get; }

    double PixelSizeX { get; }

    double PixelSizeY { get; }

    int BinX { get; }

    int BinY { get; }

    int StartX { get; }

    int StartY { get; }

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

    int? BitDepth { get; }

    short Gain { get; set; }

    short GainMin { get; }

    short GainMax { get; }

    IReadOnlyList<string> Gains { get; }

    string? GainMode
    {
        get => Connected && UsesGainMode && Gains is { Count: > 0 } gains && Gain is short idx && idx >= 0 && idx < gains.Count
            ? gains[idx]
            : null;

        set
        {
            if (Connected && UsesGainMode && Gains is { Count: > 0} gains && value is not null && gains.IndexOf(value) is var idx and <= short.MaxValue)
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
        get => Connected && UseOffsetMode && Offsets is { Count: > 0 } offsets && Offset is int idx && idx >= 0 && idx < offsets.Count
            ? offsets[idx]
            : null;

        set
        {
            if (Connected && UseOffsetMode && Offsets is { Count: > 0 } offsets && value is not null && offsets.IndexOf(value) is var idx)
            {
                Offset = idx;
            }
        }
    }

    IReadOnlyList<string> Offsets { get; }

    void StartExposure(TimeSpan duration, bool light);

    /// <summary>
    /// Should only be called when <see cref="CanStopExposure"/> is true.
    /// </summary>
    void StopExposure();

    /// <summary>
    /// Should only be called when <see cref="CanAbortExposure"/> is true.
    /// </summary>
    void AbortExposure();

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

    Image? Image => Connected && ImageReady && ImageData is { Length: > 0 } data && BitDepth is int bitDepth and > 0
        ? DataToImage(
            data,
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
                SensorType != SensorType.Monochrome ? BayerOffsetY : 0
            )
        )
        : null;

    /// <summary>
    /// TODO: assumes width x height arrays and assumes little endian.
    /// </summary>
    /// <param name="sourceData">2d image array</param>
    /// <param name="bitDepth">either 8 or 16, -32 for float is not yet supported</param>
    /// <param name="blackLevel">black level or offset</param>
    /// <param name="imageMeta">image meta data</param>
    /// <returns>image from data, transformed to floats</returns>
    public static Image DataToImage(int[,] sourceData, int bitDepth, float blackLevel, in ImageMeta imageMeta)
    {
        var width = sourceData.GetLength(0);
        var height = sourceData.GetLength(1);
        var span2d = sourceData.AsSpan2D();

        var maxVal = 0f;
        var targetData = new float[height, width];

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

        return new Image(targetData, width, height, bitDepth, maxVal, blackLevel, imageMeta);
    }
}
