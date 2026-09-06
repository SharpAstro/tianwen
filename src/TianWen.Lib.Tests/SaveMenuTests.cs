using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The Save dropdown's rows and what each one asks for.
/// </summary>
/// <remarks>
/// Rows are matched to signals BY INDEX, so reordering the labels swaps what two of them do with
/// nothing to fail: the menu would read correctly, the wrong file would be written, and the only
/// symptom is a 97 MB save where 18 MB was asked for or an annotated frame with no annotation. That
/// is what this pins -- the label and its effect asserted in the same place, so they can only drift
/// past a red test.
/// </remarks>
public class SaveMenuTests
{
    [Fact]
    public void TheRowsAreTheOnesTheDialogPromises()
    {
        ImageRendererBase<object>.SaveMenuRows.ShouldBe(
        [
            "Image as displayed (16-bit)...",
            "Image as displayed (8-bit)...",
            "Image with overlays...",
        ]);
    }

    [Theory]
    [InlineData(0, false, PngDepth.SixteenBit)]
    [InlineData(1, false, PngDepth.EightBit)]
    [InlineData(2, true, PngDepth.SixteenBit)]
    public void EachRowAsksForWhatItSays(int row, bool withOverlays, PngDepth depth)
    {
        var signal = ImageRendererBase<object>.SaveSignalFor(row);

        signal.WithOverlays.ShouldBe(withOverlays);
        signal.PngDepth.ShouldBe(depth);
    }

    [Fact]
    public void TheLosslessCleanRasterIsFirst()
    {
        // Load-bearing beyond tidiness: right-clicking the Save button skips the dropdown entirely
        // and saves the clean 16-bit raster, so row 0 has to be the same thing that shortcut does,
        // or the button and its menu disagree about what "Save" means.
        ImageRendererBase<object>.SaveSignalFor(0).ShouldBe(new SaveImageSignal(WithOverlays: false));
    }

    [Fact]
    public void AnIndexPastTheEndFallsBackToTheSafeRow()
    {
        // A dropdown cannot currently produce one, but the mapping is total on purpose: the fallback
        // is the lossless clean raster, never the annotated one, so a future row added without
        // touching the switch degrades to the least surprising save rather than the most.
        ImageRendererBase<object>.SaveSignalFor(99).ShouldBe(new SaveImageSignal(WithOverlays: false));
        ImageRendererBase<object>.SaveSignalFor(-1).ShouldBe(new SaveImageSignal(WithOverlays: false));
    }

    [Fact]
    public void EveryRowHasAMapping()
    {
        // The count is the join between the two: a label added without a case, or a case whose row
        // was removed, is what this catches.
        for (var row = 0; row < ImageRendererBase<object>.SaveMenuRows.Length; row++)
        {
            _ = ImageRendererBase<object>.SaveSignalFor(row);
        }

        ImageRendererBase<object>.SaveMenuRows.Length.ShouldBe(3);
    }
}
