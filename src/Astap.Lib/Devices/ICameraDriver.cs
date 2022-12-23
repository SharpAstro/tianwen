using Astap.Lib.Imaging;
using CommunityToolkit.HighPerformance;
using System;

namespace Astap.Lib.Devices;

public interface ICameraDriver : IDeviceDriver
{
    bool CanGetCoolerPower { get; }

    bool CanSetCCDTemperature { get; }

    bool CanStopExposure { get; }

    bool CanAbortExposure { get; }

    bool CanFastReadout { get; }

    double PixelSizeX { get; }

    double PixelSizeY { get; }

    int XBinning { get; }

    int YBinning { get; }

    int StartX { get; }

    int StartY { get; }

    string? ReadoutMode { get; set; }

    bool FastReadout { get; set; }

    int[,]? ImageData { get; }

    bool ImageReady { get; }

    bool CoolerOn { get; }

    double CoolerPower { get; }

    /// <summary>
    /// Returns the current heat sink temperature (called "ambient temperature" by some manufacturers) in degrees Celsius.
    /// </summary>
    double HeatSinkTemperature { get; }

    /// <summary>
    /// Returns the current CCD temperature in degrees Celsius.
    /// </summary>
    double CCDTemperature { get; }

    int? BitDepth { get; }

    int Offset { get; set; }

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

    Image? Image => ImageReady is true && ImageData is int[,] data && BitDepth is int bitDepth and > 0
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
                XBinning,
                YBinning,
                (float)CCDTemperature
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
