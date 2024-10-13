using System;
using static ZWOptical.SDK.ASICamera2;

namespace Astap.Lib.Devices.ZWO;

public static class ASIImageTypeEx
{
    public static BitDepth? ToBitDepth(this ASI_IMG_TYPE imageType)
        => imageType switch
        {
            ASI_IMG_TYPE.ASI_IMG_RAW8 or ASI_IMG_TYPE.ASI_IMG_Y8 => BitDepth.Int8,
            ASI_IMG_TYPE.ASI_IMG_RAW16 => BitDepth.Int16,
            _ => null
        };

    public static ASI_IMG_TYPE ToASIImageType(this BitDepth bitDepth)
        => bitDepth switch
        {
            BitDepth.Int8 => ASI_IMG_TYPE.ASI_IMG_RAW8,
            BitDepth.Int16 => ASI_IMG_TYPE.ASI_IMG_RAW16,
            _ => throw new ArgumentException($"Bit depth {bitDepth} is not supported!", nameof(bitDepth))
        };
}
