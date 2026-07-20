using System;
using System.Threading.Tasks;
using DIR.Lib;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Wires the planner search-input callbacks (search commit, autocomplete suggestions,
    /// suggestion commit) onto <see cref="PlannerState.SearchInput"/>. Shared by
    /// <see cref="AppSignalHandler"/> (desktop/TUI hosts) and the web host - the callback
    /// bodies used to live only in AppSignalHandler, which the web host never constructs
    /// (it has no device stack), so the search box was inert in the browser.
    /// </summary>
    public static class PlannerSearchInteraction
    {
        /// <summary>
        /// <paramref name="createTransform"/> returns a site-initialised transform, or null when
        /// no site is configured yet (desktop: from the active profile; web: from the lat/lon
        /// fields). <paramref name="autoComplete"/> returns the candidate cache - null until the
        /// host finishes the catalog load (see <see cref="PlannerActions.BuildAutoCompleteList"/>).
        /// <paramref name="deactivate"/> releases input focus the host's way (desktop posts
        /// DeactivateTextInputSignal). <paramref name="ensureVisible"/> scrolls the target list.
        /// </summary>
        public static void Wire(
            PlannerState plannerState,
            ICelestialObjectDB db,
            Func<Transform?> createTransform,
            Func<string[]?> autoComplete,
            Action<int>? ensureVisible,
            Action deactivate,
            Action requestRedraw)
        {
            plannerState.SearchInput.OnCommit = text =>
            {
                plannerState.Suggestions.Clear();
                plannerState.SuggestionIndex = -1;
                plannerState.LastSuggestionQuery = "";

                if (text.Length > 0 && createTransform() is { } transform)
                {
                    var resultIdx = PlannerActions.SearchTargets(
                        plannerState, db, transform, text, plannerState.Comets);
                    if (resultIdx >= 0)
                    {
                        plannerState.SelectedTargetIndex = resultIdx;
                        ensureVisible?.Invoke(resultIdx);
                    }
                }
                requestRedraw();
                return Task.CompletedTask;
            };

            plannerState.SearchInput.OnCancel = () =>
            {
                plannerState.SearchInput.Clear();
                plannerState.SearchResults = [];
                plannerState.Suggestions.Clear();
                plannerState.SuggestionIndex = -1;
                plannerState.LastSuggestionQuery = "";
                deactivate();
                plannerState.NeedsRedraw = true;
                requestRedraw();
            };

            plannerState.SearchInput.OnTextChanged = text =>
            {
                if (autoComplete() is { } cache)
                {
                    PlannerActions.UpdateSuggestions(plannerState, cache, text);
                }
            };

            // Autocomplete navigation: Up/Down/Return/Escape when suggestions are visible
            plannerState.SearchInput.OnKeyOverride = key =>
            {
                if (plannerState.Suggestions.Count == 0)
                {
                    return false;
                }

                switch (key)
                {
                    case TextInputKey.Backspace or TextInputKey.Delete:
                        return false; // Let the text input handle it, OnTextChanged will update suggestions

                    case TextInputKey.Enter when plannerState.SuggestionIndex >= 0:
                        CommitSuggestion(plannerState.Suggestions[plannerState.SuggestionIndex]);
                        return true;

                    case TextInputKey.Escape:
                        plannerState.Suggestions.Clear();
                        plannerState.SuggestionIndex = -1;
                        plannerState.LastSuggestionQuery = "";
                        requestRedraw();
                        return true;

                    default:
                        return false;
                }
            };

            // Mouse counterpart of the keyboard Enter-on-highlighted-suggestion path (OnKeyOverride
            // above): the dropdown row's OnClick calls this so a click commits IDENTICALLY. Before this
            // the suggestion click had no handler at all -- only the keyboard could commit a suggestion
            // ("arrow+enter works, mouse doesn't"). Routes through the same local CommitSuggestion.
            plannerState.CommitSuggestionAt = index =>
            {
                if (index >= 0 && index < plannerState.Suggestions.Count)
                {
                    CommitSuggestion(plannerState.Suggestions[index]);
                }
            };

            // Local helper captured by the search-input closures above (keyboard Enter-on-suggestion
            // AND the dropdown mouse-click, via CommitSuggestionAt -- one path, so both behave identically).
            void CommitSuggestion(string suggestion)
            {
                // Resolve + select the target first (it needs the suggestion text), then RESET the box:
                // clear the text, drop the dropdown, and release focus. Without the reset the search input
                // lingered showing the committed name and -- on the web -- the floating <input> overlay
                // stayed visible (it only hides on deactivate), including after switching to the sky atlas.
                if (createTransform() is { } transform)
                {
                    var resultIdx = PlannerActions.CommitSuggestion(
                        plannerState, db, transform, suggestion, plannerState.Comets);
                    if (resultIdx >= 0)
                    {
                        plannerState.SelectedTargetIndex = resultIdx;
                        ensureVisible?.Invoke(resultIdx);
                    }
                }

                plannerState.SearchInput.Clear();
                plannerState.Suggestions.Clear();
                plannerState.SuggestionIndex = -1;
                plannerState.LastSuggestionQuery = "";
                deactivate();
                requestRedraw();
            }
        }
    }
}
