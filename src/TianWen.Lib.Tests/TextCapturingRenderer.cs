using System;
using System.Collections.Generic;
using DIR.Lib;

namespace TianWen.Lib.Tests;

/// <summary>Records every DrawText call's text + layout rect, and still draws it.</summary>
internal sealed class TextCapturingRenderer(uint w, uint h) : RgbaImageRenderer(w, h)
{
    public List<(string Text, RectInt Rect)> Texts { get; } = [];

    public override void DrawText(ReadOnlySpan<char> text, string fontFamily, float fontSize,
        RGBAColor32 fontColor, in RectInt layout,
        TextAlign horizAlignment = TextAlign.Center, TextAlign vertAlignment = TextAlign.Near)
    {
        Texts.Add((text.ToString(), layout));
        base.DrawText(text, fontFamily, fontSize, fontColor, layout, horizAlignment, vertAlignment);
    }
}
