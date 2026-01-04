using CommunityToolkit.HighPerformance;
using System;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Imaging;

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

        return new Float32HxWImageData(targetData, maxVal);
    }


    /// <summary>
    /// Returns an immutable <see cref="Image"/> from source data in height x width format.
    /// </summary>
    /// <param name="imageData">2d image array</param>
    /// <param name="bitDepth">bit depth</param>
    /// <param name="blackLevel">black level or offset</param>
    /// <param name="imageMeta">image meta data</param>
    /// <returns>image from data, transposed and transformed to 32-bit floats</returns>
    public Image ToImage(BitDepth bitDepth, float blackLevel, in ImageMeta imageMeta) =>
        new Image(Data, Data.GetLength(1), Data.GetLength(0), bitDepth, MaxValue, blackLevel, imageMeta);
}