using CommunityToolkit.HighPerformance;
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging;

public partial class Image
{
    /// <summary>
    /// Support reading image from disk (used for testing).
    /// </summary>
    /// <param name="stream"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="InvalidDataException">if not a valid image stream</exception>
    internal static async ValueTask<Image> FromStreamAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        using var magic = ArrayPoolHelper.Rent<byte>(sizeof(int));
        await stream.ReadExactlyAsync(magic, cancellationToken);

        if (magic[0] != (byte)'I' || magic[1] != (byte)'m')
        {
            throw new InvalidDataException("Stream does not have a valid file magic");
        }
        var dataIsLittleEndian = magic[2] == 'L';

        int headerIntSize;
        var ver = magic[3] - '0';
        if (ver is 1)
        {
            headerIntSize = 5;
        }
        else if (ver is 2)
        {
            await stream.ReadExactlyAsync(magic, cancellationToken);
            if (dataIsLittleEndian != BitConverter.IsLittleEndian)
            {
                magic.AsSpan(0).Reverse();
            }

            headerIntSize = BitConverter.ToInt32(magic);
        }
        else
        {
            throw new InvalidDataException($"Unsupported image version {ver}");
        }

        using var headers = ArrayPoolHelper.Rent<byte>(headerIntSize * sizeof(int));

        await stream.ReadExactlyAsync(headers, cancellationToken);

        if (dataIsLittleEndian != BitConverter.IsLittleEndian)
        {
            for (var i = 0; i < headerIntSize; i++)
            {
                headers.AsSpan(i * sizeof(int), sizeof(int)).Reverse();
            }
        }

        var ints = headers.AsMemory().Cast<byte, int>().ToArray();
        var width = ints[0];
        var height = ints[1];
        var bitDepth = (BitDepth)ints[2];
        var maxValue = BitConverter.Int32BitsToSingle(ints[3]);
        var blackLevel = BitConverter.Int32BitsToSingle(ints[4]);
        var channelCount = headerIntSize > 5 ? ints[5] : 1;

        var imageSize = channelCount * width * height;
        var dataSize = imageSize * sizeof(float);

        var byteData = new byte[dataSize];
        await stream.ReadExactlyAsync(byteData, cancellationToken);

        if (dataIsLittleEndian != BitConverter.IsLittleEndian)
        {
            for (var i = 0; i < imageSize; i++)
            {
                Array.Reverse(byteData, i * sizeof(float), sizeof(float));
            }
        }

        var data = new float[channelCount, height, width];
        Buffer.BlockCopy(byteData, 0, data, 0, byteData.Length);

        var imageMeta = await JsonSerializer.DeserializeAsync(stream, ImageJsonSerializerContext.Default.ImageMeta, cancellationToken);

        return new Image(data, bitDepth, maxValue, blackLevel, imageMeta);
    }

    /// <summary>
    /// Writes image stream to disk. Use with <see cref="FromStreamAsync"/>.
    /// Internal use only
    /// </summary>
    /// <param name="stream"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    internal async Task WriteStreamAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var (channelCount, width, height) = Shape;
        var magic = (BitConverter.IsLittleEndian ? "ImL2"u8 : "ImB2"u8).ToArray();
        await stream.WriteAsync(magic, cancellationToken);

        int[] header = [
            width,
            height,
            (int)bitDepth,
            BitConverter.SingleToInt32Bits(maxValue),
            BitConverter.SingleToInt32Bits(blackLevel),
            channelCount
        ];

        await stream.WriteAsync(BitConverter.GetBytes(header.Length), cancellationToken);
        for (var i = 0; i < header.Length; i++)
        {
            await stream.WriteAsync(BitConverter.GetBytes(header[i]), cancellationToken);
        }

        await stream.WriteAsync(data.AsMemory().Cast<float, byte>(), cancellationToken);

        await JsonSerializer.SerializeAsync(stream, ImageMeta, ImageJsonSerializerContext.Default.ImageMeta, cancellationToken);
    }
}
