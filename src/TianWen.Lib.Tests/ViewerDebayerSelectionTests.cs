using System;
using System.Linq;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Which demosaics the viewer offers, which one it starts on, and that each of them is a demosaic
/// the GPU can actually run.
/// </summary>
/// <remarks>
/// The bug behind this file: the selector offered AHD, the shader had no AHD, so the screen showed
/// MHC while a Save wrote AHD. Nothing failed -- both are plausible pictures of the same sky, and
/// the difference (a faint dark ring at every star core, plus 1258 ms per save) is only visible by
/// blinking two renders against each other. A viewer whose whole contract is "the file is what you
/// were looking at" cannot offer a choice it does not honour, so the invariant asserted here is not
/// "AHD is gone" but "no selectable entry shares a GPU mode with another", which stays true for
/// whatever is added next.
/// </remarks>
public class ViewerDebayerSelectionTests
{
    [Fact]
    public void EveryOfferedAlgorithmHasItsOwnShaderBranch()
    {
        var modes = ViewerActions.DebayerAlgorithms
            .Select(a => (Algorithm: a, Mode: ImageRendererBase<object>.GpuDebayerMode(a)))
            .ToArray();

        modes.Select(m => m.Mode).Distinct().Count().ShouldBe(modes.Length,
            "two selectable algorithms sharing a GPU mode means one of them displays as the other: " +
            string.Join(", ", modes.Select(m => $"{m.Algorithm}->{m.Mode}")));
    }

    /// <summary>
    /// AHD is the entry that broke the rule above, and it stays out until the shader grows one.
    /// It is NOT removed from <see cref="DebayerAlgorithm"/>: stacking and the dataset exporters
    /// still ask for it by name, and a viewer state persisted before it was withdrawn must keep
    /// working, which is what the next test covers.
    /// </summary>
    [Fact]
    public void AhdIsNotOffered()
    {
        ViewerActions.DebayerAlgorithms.ShouldNotContain(DebayerAlgorithm.AHD);

        // Still answers, so an old state renders instead of throwing -- as MHC, which is the one
        // place display and save disagree and precisely why it is not selectable.
        ImageRendererBase<object>.GpuDebayerMode(DebayerAlgorithm.AHD)
            .ShouldBe(ImageRendererBase<object>.GpuDebayerMode(DebayerAlgorithm.MHC));
    }

    [Fact]
    public void AStatePersistedWithAWithdrawnAlgorithmRejoinsTheRing()
    {
        var state = new ViewerState { DebayerAlgorithm = DebayerAlgorithm.AHD };

        ViewerActions.CycleDebayerAlgorithm(state);

        // Array.IndexOf answers -1 for something not in the ring; the step must land on a real
        // entry rather than wrap off the end or leave the state where it was.
        ViewerActions.DebayerAlgorithms.ShouldContain(state.DebayerAlgorithm);
    }

    [Fact]
    public void AFreshViewerStartsOnTheDefaultAndTheDefaultIsOffered()
    {
        ViewerActions.DefaultDebayerAlgorithm.ShouldBe(DebayerAlgorithm.VNG);
        ViewerActions.DebayerAlgorithms.ShouldContain(ViewerActions.DefaultDebayerAlgorithm);
        new ViewerState().DebayerAlgorithm.ShouldBe(ViewerActions.DefaultDebayerAlgorithm);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CyclingVisitsEveryOfferedAlgorithmExactlyOncePerLap(bool reverse)
    {
        var order = ViewerActions.DebayerAlgorithms;
        var state = new ViewerState { DebayerAlgorithm = order[0] };

        var visited = new DebayerAlgorithm[order.Length];
        for (var i = 0; i < order.Length; i++)
        {
            ViewerActions.CycleDebayerAlgorithm(state, reverse);
            visited[i] = state.DebayerAlgorithm;
        }

        visited.Distinct().Count().ShouldBe(order.Length);
        state.DebayerAlgorithm.ShouldBe(order[0], "a full lap must come back to where it started");
    }

    /// <summary>
    /// The numbers themselves, because they cross a language boundary: C# writes them into
    /// <c>stretchBlend.z</c> and <c>image.frag</c>'s <c>main</c> switches on them, and nothing in
    /// either compiler checks that the two agree. Renumbering the GLSL without this file failing
    /// would silently show one demosaic while the menu named another.
    /// </summary>
    [Theory]
    [InlineData(DebayerAlgorithm.None, 2)]
    [InlineData(DebayerAlgorithm.BilinearMono, 3)]
    [InlineData(DebayerAlgorithm.MHC, 1)]
    [InlineData(DebayerAlgorithm.VNG, 4)]
    public void TheShaderModeNumbersAreTheOnesImageFragSwitchesOn(DebayerAlgorithm algorithm, int mode)
        => ImageRendererBase<object>.GpuDebayerMode(algorithm).ShouldBe(mode);
}
