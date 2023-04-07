using System;

namespace Astap.Lib.Imaging;

public enum RowOrder
{
    TopDown,
    BottomUp
}

public static class RowOrderEx
{
    public static string ToFITSValue(this RowOrder @this) => @this switch
    {
        RowOrder.TopDown => "TOP-DOWN",
        RowOrder.BottomUp => "BOTTOM-UP",
        _ => throw new ArgumentException($"Value {@this} is not handled", nameof(@this))
    };

    public static RowOrder? FromFITSValue(string? value) => Enum.TryParse(value?.Replace("-", ""), true, out RowOrder ro) ? ro : null;
}