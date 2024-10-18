using System;

namespace Astap.Lib.Imaging;

public enum FrameType
{
    None,
    Light,
    Dark,
    Bias,
    Flat,
    DarkFlat
}

public static class FrameTypeEx
{
    public static bool NeedsOpenShutter(this FrameType @this) => @this switch
    {
        FrameType.Light or FrameType.Flat => true,
        _ => false
    };

    public static string ToFITSValue(this FrameType @this) => @this.ToString();

    public static FrameType? FromFITSValue(string value) => Enum.TryParse(value?.Replace("-", ""), true, out FrameType frameType) ? frameType : null;
}