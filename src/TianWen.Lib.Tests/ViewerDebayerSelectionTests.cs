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
        // Auto is excluded because it is not a demosaic: it is resolved to one of the others before
        // the shader mode is asked, and GpuDebayerMode throws if it ever is not (see
        // AnUnresolvedAutoIsRefusedAtTheShaderBoundary). Every OTHER entry is a real branch.
        var modes = ViewerActions.DebayerAlgorithms
            .Where(a => a is not DebayerAlgorithm.Auto)
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
        ViewerActions.DefaultDebayerAlgorithm.ShouldBe(DebayerAlgorithm.Auto);
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

    /// <summary>
    /// The four rules of <see cref="DebayerAlgorithm.Auto"/>. A CFA mosaic is the only input a
    /// demosaic acts on at all; the other two answers exist so the selector never SHOWS an algorithm
    /// that is doing nothing.
    /// </summary>
    [Theory]
    // isBayerMosaic, isColour, isVideoStream, expected
    [InlineData(true, false, false, DebayerAlgorithm.VNG)]          // still CFA frame: VNG, measured at stars
    [InlineData(true, false, true, DebayerAlgorithm.MHC)]           // SER: parity with PlanetaryMaster
    [InlineData(false, true, false, DebayerAlgorithm.None)]         // already colour: nothing to demosaic
    [InlineData(false, false, false, DebayerAlgorithm.BilinearMono)] // mono sensor
    public void AutoResolvesFromTheFrame(bool isBayerMosaic, bool isColour, bool isVideoStream, DebayerAlgorithm expected)
        => DebayerAlgorithm.Auto.ResolveAuto(isBayerMosaic, isColour, isVideoStream).ShouldBe(expected);

    /// <summary>
    /// A still CFA frame and a SER carrying the SAME pixels must resolve differently. Stated as its own
    /// test because it is the only rule whose inputs are otherwise identical, so a resolver that ignored
    /// <c>isVideoStream</c> would still pass every other case above.
    /// </summary>
    [Fact]
    public void OnlyTheStreamKindSeparatesTheTwoCfaAnswers()
    {
        var still = DebayerAlgorithm.Auto.ResolveAuto(isBayerMosaic: true, isColour: false, isVideoStream: false);
        var video = DebayerAlgorithm.Auto.ResolveAuto(isBayerMosaic: true, isColour: false, isVideoStream: true);

        still.ShouldBe(DebayerAlgorithm.VNG);
        video.ShouldBe(DebayerAlgorithm.MHC);
        still.ShouldNotBe(video);
    }

    /// <summary>Anything that is not Auto passes through untouched, whatever the frame looks like.</summary>
    [Theory]
    [InlineData(DebayerAlgorithm.None)]
    [InlineData(DebayerAlgorithm.BilinearMono)]
    [InlineData(DebayerAlgorithm.VNG)]
    [InlineData(DebayerAlgorithm.MHC)]
    [InlineData(DebayerAlgorithm.AHD)]
    public void AnExplicitChoiceIsNeverOverridden(DebayerAlgorithm chosen)
    {
        chosen.ResolveAuto(isBayerMosaic: true, isColour: false, isVideoStream: true).ShouldBe(chosen);
        chosen.ResolveAuto(isBayerMosaic: false, isColour: true, isVideoStream: false).ShouldBe(chosen);
    }

    /// <summary>
    /// Auto has no shader branch, so a producer that forgets to resolve it must fail loudly. It used to
    /// be that an unknown algorithm fell through to MHC, which is how AHD displayed as something else
    /// for months without anything going red.
    /// </summary>
    [Fact]
    public void AnUnresolvedAutoIsRefusedAtTheShaderBoundary()
        => Should.Throw<ArgumentOutOfRangeException>(
            () => ImageRendererBase<object>.GpuDebayerMode(DebayerAlgorithm.Auto));

    /// <summary>Auto is last in the enum, so values already written into serialized viewer state stay put.</summary>
    [Fact]
    public void AddingAutoDidNotRenumberTheExistingAlgorithms()
    {
        ((int)DebayerAlgorithm.None).ShouldBe(0);
        ((int)DebayerAlgorithm.BilinearMono).ShouldBe(1);
        ((int)DebayerAlgorithm.VNG).ShouldBe(2);
        ((int)DebayerAlgorithm.AHD).ShouldBe(3);
        ((int)DebayerAlgorithm.MHC).ShouldBe(4);
        ((int)DebayerAlgorithm.Auto).ShouldBe(5);
    }
}
