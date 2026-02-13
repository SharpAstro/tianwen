using System;
using TianWen.DAL;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Devices.DAL;

public static class PixelDataFormatEx
{
    public static BitDepth? ToBitDepth(this PixelDataFormat imageType)
    => imageType switch
    {
        PixelDataFormat.RAW8 or PixelDataFormat.Y8 => BitDepth.Int8,
        PixelDataFormat.RAW16 => BitDepth.Int16,
        _ => null
    };

    public static PixelDataFormat ToRawPixelFormat(this BitDepth bitDepth)
        => bitDepth switch
        {
            BitDepth.Int8 => PixelDataFormat.RAW8,
            BitDepth.Int16 => PixelDataFormat.RAW16,
            _ => throw new ArgumentException($"Bit depth {bitDepth} is not supported!", nameof(bitDepth))
        };
}
