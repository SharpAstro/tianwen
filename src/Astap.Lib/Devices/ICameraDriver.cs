using Astap.Lib.Imaging;
using CommunityToolkit.HighPerformance;
using System;

namespace Astap.Lib.Devices;

public interface ICameraDriver : IDeviceDriver
{
    bool? CanGetCoolerPower { get; }

    double? PixelSizeX { get; }

    double? PixelSizeY { get; }

    int? StartX { get; }

    int? StartY { get; }

    int[,]? ImageData { get; }

    bool? ImageReady { get; }

    int? BitDepth { get; }

    void StartExposure(TimeSpan duration, bool light);

    Image? Image => ImageReady is true && ImageData is int[,] data && BitDepth is int bitDepth and > 0
        ? DataToImage(data, bitDepth)
        : null;

    /// <summary>
    /// TODO: assumes width x height arrays and assumes little endian.
    /// </summary>
    /// <param name="sourceData">2d image array</param>
    /// <param name="bitDepth">either 8 or 16</param>
    /// <returns></returns>
    public static Image DataToImage(int[,] sourceData, int bitDepth)
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

        return new Image(targetData, width, height, bitDepth, maxVal);
    }
}
