using CommunityToolkit.HighPerformance;
using System;

namespace TianWen.Lib.Imaging;

public record Float32HxWImageData(float[,,] Data, float MaxValue)
{
    /// <summary>
    /// Transposes and converts image source data if required to Height X Width X 32-bit floats, single channel.
    /// </summary>
    /// <param name="sourceData">2d image array in width x height</param>
    /// <returns>image data as 32-bit float, transposed to height x width</returns>
    public static Float32HxWImageData FromWxHImageData(int[,] sourceData)
    {
        var width = sourceData.GetLength(0);
        var height = sourceData.GetLength(1);
        var span2d = sourceData.AsSpan2D();

        var maxValue = 0f;
        var targetData = new float[1, height, width];

        for (var h = 0; h < height; h++)
        {
            int w = 0;
            foreach (var val in span2d.GetColumn(h))
            {
                float valF = val;
                targetData[0, h, w++] = valF;
                maxValue = MathF.Max(valF, maxValue);
            }
        }

        return new Float32HxWImageData(targetData, maxValue);
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
        new Image(Data, bitDepth, MaxValue, blackLevel, imageMeta);
}