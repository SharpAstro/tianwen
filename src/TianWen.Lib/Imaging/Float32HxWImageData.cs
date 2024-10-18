using CommunityToolkit.HighPerformance;
using System;

namespace Astap.Lib.Imaging;

public record Float32HxWImageData(float[,] Data, float MaxValue)
{
    /// <summary>
    /// Transposes and converts image source data if required to Height X Width X 32-bit floats.
    /// </summary>
    /// <param name="sourceData">2d image array in width x height</param>
    /// <returns>image data as 32-bit float, transposed to height x width</returns>
    public static Float32HxWImageData FromWxHImageData(int[,] sourceData)
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

        return new(targetData, maxVal);
    }
}