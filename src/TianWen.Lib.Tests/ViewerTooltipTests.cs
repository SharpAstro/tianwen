using System;
using System.Threading.Tasks;
using DIR.Lib;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <c>tianwen-fits</c> shows a toolbar button's tooltip through the router and DIR.Lib's one painter (#1446): after
/// the hover delay, beside the button, and gone once the pointer leaves. Before, the viewer placed a box of its own
/// from the hovered button, at once, and nothing in this host painted the router's tooltip, nor ran its after-paint
/// step, so a tooltip could never expire here.
/// </summary>
/// <remarks>
/// It reads PIXELS, unlike <see cref="ViewerE2ETests"/>, because a tooltip registers no region (it must not, or it
/// would take the pointer from the button that asked for it) and is chrome, which the CPU surface does draw. It
/// compares the band under the toolbar with itself frame to frame, so no colour or font is pinned.
/// </remarks>
[Collection("Viewer")]
public class ViewerTooltipTests
{
    public static TheoryData<float> Scales => new TheoryData<float> { 1f, 1.5f };

    [Theory]
    [MemberData(nameof(Scales))]
    public async Task AToolbarButtonsTooltipShowsOnceThePointerRestsAndGoesWhenItLeaves(float dpiScale)
    {
        await using var e2e = ViewerE2E.Start(dpiScale);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
        e2e.Host.Router.Clock = clock;
        var button = e2e.ToolbarButton(ToolbarAction.FileList);
        // The rows under the toolbar, where a button's tooltip opens.
        var band = new Band(
            Top: (int)MathF.Ceiling(button.Y + button.Height) + 1,
            Bottom: (int)(button.Y + button.Height + (40f * dpiScale)));
        // The baseline is a settled window with the pointer over the picture, where it goes back to at the end.
        var image = e2e.Viewer.ImageArea;
        e2e.Host.HandlePointer(new InputEvent.MouseMove(image.X + (image.Width / 2f), image.Y + (image.Height / 2f)));
        e2e.Frame();
        e2e.Frame();
        var bare = Pixels(e2e, band);

        e2e.Host.HandlePointer(new InputEvent.MouseMove(button.X + (button.Width / 2f), button.Y + (button.Height / 2f)));
        e2e.Frame();
        e2e.Host.Router.Tooltip.ShouldBeNull("the pointer has only just arrived");
        Differs(Pixels(e2e, band), bare).ShouldBe(0, "nothing is drawn under the bar before the delay");

        clock.Advance(e2e.Host.Router.TooltipDelay);
        e2e.Host.Router.TooltipNeedsFrame.ShouldBeTrue("the delay ran out while the pointer rested");
        e2e.Host.WantsFrame().ShouldBeTrue("which asks the loop for a frame");

        e2e.Frame();
        e2e.Host.Router.Tooltip.ShouldNotBeNull().Text.ShouldBe("Show / hide the file list (L)");
        Differs(Pixels(e2e, band), bare).ShouldBeGreaterThan(0, "the tooltip is drawn under the button");
        e2e.Host.Router.TooltipNeedsFrame.ShouldBeFalse("the host ran the router's after-paint step once the frame was drawn");

        e2e.Host.HandlePointer(new InputEvent.MouseMove(image.X + (image.Width / 2f), image.Y + (image.Height / 2f)));
        e2e.Frame();
        e2e.Host.Router.Tooltip.ShouldBeNull();
        Differs(Pixels(e2e, band), bare).ShouldBe(0, "the tooltip went with the pointer");
    }

    /// <summary>The window's rows <see cref="Top"/> up to <see cref="Bottom"/>, full width.</summary>
    private readonly record struct Band(int Top, int Bottom);

    /// <summary>The pixels inside <paramref name="band"/>, copied.</summary>
    private static byte[] Pixels(ViewerE2E e2e, Band band)
    {
        var stride = e2e.Width * 4;
        return e2e.Pixels().AsSpan(band.Top * stride, (band.Bottom - band.Top) * stride).ToArray();
    }

    private static int Differs(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        a.Length.ShouldBe(b.Length);
        var count = 0;
        for (var i = 0; i < a.Length; i += 4)
        {
            if (!a.Slice(i, 4).SequenceEqual(b.Slice(i, 4)))
            {
                count++;
            }
        }
        return count;
    }
}
