using System;

namespace TianWen.Lib.Imaging;

public enum RowOrder
{
    TopDown,
    BottomUp
}

public static class RowOrderEx
{
    extension(RowOrder rowOrder)
    {
        public string ToFITSValue() => rowOrder switch
        {
            RowOrder.TopDown => "TOP-DOWN",
            RowOrder.BottomUp => "BOTTOM-UP",
            _ => throw new ArgumentException($"Value {rowOrder} is not handled", nameof(rowOrder))
        };
    }

    extension(RowOrder)
    {
        public static RowOrder? FromFITSValue(string? value)
            => Enum.TryParse(value?.Replace("-", ""), true, out RowOrder ro) ? ro : null;
    }
}