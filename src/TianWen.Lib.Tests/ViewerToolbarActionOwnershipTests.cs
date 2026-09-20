using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Who owns a toolbar action's state when the work is SPLIT between
/// <see cref="ViewerActions.HandleToolbarAction"/> and a renderer-side helper that can see the
/// document.
/// </summary>
/// <remarks>
/// The split is where a button and its key rot apart. A press runs
/// <see cref="ViewerActions.HandleToolbarAction"/> and THEN the helper, so if both move the same
/// flag the press nets zero -- which is what happened to Background Neutralize: the arm set the
/// flag, the helper (itself a complete toggle) read it, took its OFF branch and set it back. The
/// button did nothing at all while the <c>N</c> key, which calls only the helper, worked.
/// <para>The rule is now DECLARED as <see cref="ViewerActions.RendererOwnedState"/> and obeyed
/// before the switch, so these drive the declaration rather than naming an action: an entry added
/// there is covered here without a new test, and an arm that starts moving a declared action's flag
/// fails.</para>
/// </remarks>
public class ViewerToolbarActionOwnershipTests
{
    public static TheoryData<ToolbarAction, bool> DeclaredOwnership()
    {
        var data = new TheoryData<ToolbarAction, bool>();
        foreach (var action in ViewerActions.RendererOwnedState)
        {
            data.Add(action, false);
            data.Add(action, true);
        }

        return data;
    }

    // Every flag a declared action could plausibly be confused for owning. Listed explicitly rather
    // than reflected over ViewerState, which this repo does not do in tests -- and which would drag
    // in every unrelated property besides.
    [Theory]
    [MemberData(nameof(DeclaredOwnership))]
    public void ADeclaredActionLeavesItsStateToTheHelperThatCanCompleteIt(ToolbarAction action, bool before)
    {
        var state = new ViewerState
        {
            BackgroundNeutralizationEnabled = before,
            ColorCalibrationEnabled = before,
        };

        ViewerActions.HandleToolbarAction(state, source: null, action)
            .ShouldBeTrue("the action is handled here even though its state is not");

        state.BackgroundNeutralizationEnabled.ShouldBe(before,
            $"{action} is declared renderer-owned; moving its flag here makes a press toggle twice");
        state.ColorCalibrationEnabled.ShouldBe(before);
        state.NeedsRedraw.ShouldBeTrue("the press still owes a frame");
    }

    // The contrast, and the reason the rule is not simply "helpers own flags":
    // TryStartColorCalibration STARTS a calibration and toggles nothing, so its arm and its helper
    // compose. If it ever joins the declaration, this fails and says why.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ColorCalibrateKeepsItsFlipBecauseItsHelperTogglesNothing(bool enabledBefore)
    {
        ViewerActions.RendererOwnedState.ShouldNotContain(ToolbarAction.ColorCalibrate);

        var state = new ViewerState { ColorCalibrationEnabled = enabledBefore };

        ViewerActions.HandleToolbarAction(state, source: null, ToolbarAction.ColorCalibrate)
            .ShouldBeTrue();

        state.ColorCalibrationEnabled.ShouldBe(!enabledBefore);
    }
}
