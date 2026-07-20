using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

public class PlannerSearchInteractionTests
{
    /// <summary>
    /// Committing a suggestion must RESET the search box (clear the text, drop the dropdown) and release
    /// focus -- the fix for "clicking a suggestion left the committed name in the box and the floating
    /// &lt;input&gt; overlay lingered onto the sky atlas" (the overlay only hides on deactivate). Both the
    /// keyboard Enter-on-suggestion and the dropdown mouse-click go through the same CommitSuggestion, so
    /// this exercises the shared reset via <see cref="PlannerState.CommitSuggestionAt"/> (the mouse entry).
    /// A null transform skips the DB resolution so the test isolates the reset/deactivate contract.
    /// </summary>
    [Fact]
    public void CommitSuggestion_ClearsInputDropsDropdownAndReleasesFocus()
    {
        var state = new PlannerState();
        state.SearchInput.Activate("Androm");
        state.Suggestions.Add("Andromeda Galaxy");
        state.Suggestions.Add("Andromeda I");
        state.SuggestionIndex = 0;
        state.LastSuggestionQuery = "Androm";
        var deactivated = 0;

        PlannerSearchInteraction.Wire(
            state,
            db: null!,                    // unreached: createTransform returns null, skipping resolution
            createTransform: () => null,
            autoComplete: () => null,
            ensureVisible: null,
            deactivate: () => deactivated++,
            requestRedraw: () => { });

        state.CommitSuggestionAt.ShouldNotBeNull();
        state.CommitSuggestionAt!(0);

        state.SearchInput.Text.ShouldBe("");            // was left as the committed name before the fix
        state.Suggestions.ShouldBeEmpty();
        state.SuggestionIndex.ShouldBe(-1);
        state.LastSuggestionQuery.ShouldBe("");
        deactivated.ShouldBe(1);                        // focus released -> the overlay hides on the host
    }
}
