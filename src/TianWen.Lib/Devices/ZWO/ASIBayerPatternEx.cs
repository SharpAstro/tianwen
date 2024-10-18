using System;
using static ZWOptical.SDK.ASICamera2;

namespace Astap.Lib.Devices.ZWO;

public static class ASIBayerPatternEx
{
    public static int BayerXOffset(this ASI_BAYER_PATTERN @this)
        => @this switch
        {
            ASI_BAYER_PATTERN.ASI_BAYER_RG => 0,
            ASI_BAYER_PATTERN.ASI_BAYER_BG => 1,
            ASI_BAYER_PATTERN.ASI_BAYER_GR => 1,
            ASI_BAYER_PATTERN.ASI_BAYER_GB => 0,
            _ => throw new ArgumentException($"Unknown Bayer pattern {@this}")
        };

    public static int BayerYOffset(this ASI_BAYER_PATTERN @this)
    => @this switch
    {
        ASI_BAYER_PATTERN.ASI_BAYER_RG => 0,
        ASI_BAYER_PATTERN.ASI_BAYER_BG => 1,
        ASI_BAYER_PATTERN.ASI_BAYER_GR => 0,
        ASI_BAYER_PATTERN.ASI_BAYER_GB => 1,
        _ => throw new ArgumentException($"Unknown Bayer pattern {@this}")
    };
}