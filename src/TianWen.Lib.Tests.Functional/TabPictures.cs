using DIR.Lib;
using System;
using System.Collections.Generic;
using System.Linq;
using TianWen.Lib.Devices;
using TianWen.UI.Abstractions;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// The tabs a run is watched on, drawn offline from one view's <see cref="LiveSessionState"/> (P5b part 9 of
/// docs/plans/hardware-in-the-server.md, #935): the Live Session and Guider tabs over the CPU renderer, recording every run
/// of text drawn and every region laid out beside the pixels, and the Home card's run as <see cref="HomeBoard.RunCard"/>
/// describes it (the Home tab draws only from its cards). Two views of one run are compared picture by picture, so a
/// mirror that lays a tab out differently is named by what it drew differently, not only by a pixel count.
/// <para>
/// One instance draws one view, and keeps its tabs from one comparison to the next as a window does. No preview viewer
/// is attached: the frame on show is P6's (the harness's known gap), and what the viewer draws of a frame is its own
/// tests' subject.
/// </para>
/// </summary>
internal sealed class TabPictures : IDisposable
{
    private const uint Width = 1600;
    private const uint Height = 1000;

    private readonly RecordingRenderer _liveRenderer = new RecordingRenderer(Width, Height);
    private readonly RecordingRenderer _guiderRenderer = new RecordingRenderer(Width, Height);
    private readonly LiveSessionTab<RgbaImage> _live;
    private readonly GuiderTab<RgbaImage> _guider;

    public TabPictures()
    {
        var font = FontResolver.ResolveSystemFont();
        _live = new LiveSessionTab<RgbaImage>(_liveRenderer) { DpiScale = 1f, FontPath = font };
        _guider = new GuiderTab<RgbaImage>(_guiderRenderer) { DpiScale = 1f, FontPath = font };
    }

    /// <summary>The three tabs as the view's state draws them now.</summary>
    public IReadOnlyList<TabPicture> Draw(LiveSessionState state, ITimeProvider time)
    {
        var rect = new RectF32(0, 0, Width, Height);

        _liveRenderer.Start();
        _live.Render(state, rect, time);
        var live = _liveRenderer.Picture("Live Session tab", _live.GetRegisteredRegions());

        _guiderRenderer.Start();
        _guider.Render(state, rect, time);
        var guider = _guiderRenderer.Picture("Guider tab", _guider.GetRegisteredRegions());

        var home = new TabPicture("Home card", [HomeBoard.RunCard(state, time.GetUtcNow()).ToString()], [], []);
        return [live, guider, home];
    }

    /// <summary>
    /// How two pictures of one tab differ, in words: the text drawn in one and not the other, the regions laid out in one
    /// and not the other, and, when both of those agree, where the pixels differ. Empty when the two are the same picture.
    /// </summary>
    public static IEnumerable<string> Differences(TabPicture inProcess, TabPicture mirrored)
    {
        foreach (var line in OnlyIn("text", inProcess.Text, mirrored.Text))
        {
            yield return line;
        }
        foreach (var line in OnlyIn("region", inProcess.Regions, mirrored.Regions))
        {
            yield return line;
        }
        // A chart, a sparkline or a bar draws no text: where the words and the regions agree, the pixels say the rest.
        if (inProcess.Text.SequenceEqual(mirrored.Text) && inProcess.Regions.SequenceEqual(mirrored.Regions)
            && PixelsDiffer(inProcess, mirrored) is { } box)
        {
            yield return $"the same text and regions, but the pixels differ within {box}";
        }
    }

    private static IEnumerable<string> OnlyIn(string what, IReadOnlyList<string> inProcess, IReadOnlyList<string> mirrored)
    {
        if (inProcess.SequenceEqual(mirrored))
        {
            yield break;
        }
        var left = inProcess.GroupBy(x => x, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var right = mirrored.GroupBy(x => x, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var any = false;
        foreach (var (item, count) in left)
        {
            if (count > right.GetValueOrDefault(item))
            {
                any = true;
                yield return $"{what} in-process only: {item}";
            }
        }
        foreach (var (item, count) in right)
        {
            if (count > left.GetValueOrDefault(item))
            {
                any = true;
                yield return $"{what} mirrored only: {item}";
            }
        }
        if (!any)
        {
            yield return $"the same {what}s in a different order";
        }
    }

    /// <summary>The smallest box holding every pixel that differs, or null when none does.</summary>
    private static string? PixelsDiffer(TabPicture a, TabPicture b)
    {
        if (a.Pixels.AsSpan().SequenceEqual(b.Pixels))
        {
            return null;
        }
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
        for (var i = 0; i < a.Pixels.Length; i += 4)
        {
            if (!a.Pixels.AsSpan(i, 4).SequenceEqual(b.Pixels.AsSpan(i, 4)))
            {
                var x = i / 4 % (int)Width;
                var y = i / 4 / (int)Width;
                x0 = Math.Min(x0, x);
                y0 = Math.Min(y0, y);
                x1 = Math.Max(x1, x);
                y1 = Math.Max(y1, y);
            }
        }
        return $"x {x0}..{x1}, y {y0}..{y1}";
    }

    public void Dispose()
    {
        _liveRenderer.Dispose();
        _guiderRenderer.Dispose();
    }

    /// <summary>The CPU renderer, recording each run of text as it is drawn.</summary>
    private sealed class RecordingRenderer(uint width, uint height) : RgbaImageRenderer(width, height)
    {
        private readonly List<string> _text = new List<string>();

        public void Start()
        {
            _text.Clear();
            Surface.Clear(new RGBAColor32(0, 0, 0, 0xff));
        }

        public override void DrawText(ReadOnlySpan<char> text, string fontFamily, float fontSize, RGBAColor32 fontColor,
            in RectInt layout, TextAlign horizAlignment = TextAlign.Center, TextAlign vertAlignment = TextAlign.Near)
        {
            _text.Add($"\"{text}\" at {layout.UpperLeft.X},{layout.UpperLeft.Y} {layout.Width}x{layout.Height}, {fontSize:F1} px, "
                + $"#{fontColor.Red:X2}{fontColor.Green:X2}{fontColor.Blue:X2}{fontColor.Alpha:X2}");
            base.DrawText(text, fontFamily, fontSize, fontColor, layout, horizAlignment, vertAlignment);
        }

        public TabPicture Picture(string tab, ClickableRegion[] regions) => new TabPicture(
            tab,
            [.. _text],
            [.. regions.Select(r => $"{r.X:F1},{r.Y:F1} {r.Width:F1}x{r.Height:F1} {r.Result}")],
            (byte[])Surface.Pixels.Clone());
    }
}

/// <summary>One tab as one view drew it: the text runs and regions in drawing order, and the pixels.</summary>
internal sealed record TabPicture(string Tab, IReadOnlyList<string> Text, IReadOnlyList<string> Regions, byte[] Pixels);
