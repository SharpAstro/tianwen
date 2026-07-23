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
    /// keyboard Enter-on-suggestion and the dropdown mouse-click go through the same commit path, so this
    /// exercises the shared reset via <see cref="DIR.Lib.SearchInteraction.CommitAt"/> (the mouse entry).
    /// A null transform skips the DB resolution so the test isolates the reset/deactivate contract.
    /// </summary>
    [Fact]
    public void CommitSuggestion_ClearsInputDropsDropdownAndReleasesFocus()
    {
        var state = new PlannerState();
        state.SearchInput.Activate("Androm");
        var deactivated = 0;

        var search = new PlannerSearchInteraction(
            state,
            db: null!,                    // unreached: createTransform returns null, skipping resolution
            createTransform: () => null,
            autoComplete: () => ["Andromeda Galaxy", "Andromeda I"],
            ensureVisible: null,
            deactivate: () => deactivated++,
            requestRedraw: () => { });
        state.Search = search;

        // Populate the dropdown the way a keystroke would (base OnTextChanged -> Query).
        state.SearchInput.OnTextChanged!("Androm");
        search.Results.Length.ShouldBe(2);
        search.SelectedIndex.ShouldBe(-1); // planner does not auto-highlight

        // Mouse-click commit of the first suggestion (== keyboard Enter-on-highlight).
        search.CommitAt(0);

        state.SearchInput.Text.ShouldBe("");            // was left as the committed name before the fix
        search.Results.ShouldBeEmpty();
        search.SelectedIndex.ShouldBe(-1);
        search.LastQuery.ShouldBe("");
        deactivated.ShouldBe(1);                        // focus released -> the overlay hides on the host
    }
}
