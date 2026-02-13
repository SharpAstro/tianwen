using System;

namespace TianWen.Lib.Imaging;

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
    extension(FrameType frameType)
    {
        public bool NeedsOpenShutter => frameType switch
        {
            FrameType.Light or FrameType.Flat => true,
            _ => false
        };

        public string ToFITSValue() => frameType.ToString();
    }

    extension(FrameType)
    {
        public static FrameType? FromFITSValue(string value) => Enum.TryParse(value?.Replace("-", ""), true, out FrameType frameType) ? frameType : null;
    }
}