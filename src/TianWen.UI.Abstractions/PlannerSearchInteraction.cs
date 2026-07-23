using System;
using System.Collections.Immutable;
using DIR.Lib;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// The planner search box as a <see cref="SearchInteraction{TResult}"/> (TResult = a suggestion
    /// string): input wiring + the Up/Down/Enter/Escape protocol + the suggestion list all come from the
    /// DIR.Lib base; this subclass supplies only the domain resolution (autocomplete + target search /
    /// commit). It replaces the old static <c>Wire</c> helper, which spread the same machinery across
    /// <see cref="PlannerState"/> fields + <c>TextInputInteraction</c>'s arrow-nav block.
    ///
    /// <para>Behaviour preserved from the old wiring, via base policy overrides: no auto-highlight (Enter
    /// searches the raw text until the user arrows down -> <see cref="AutoSelectFirstResult"/> false), Up at
    /// index 0 deselects back to raw-query mode (<see cref="AllowDeselectOnUp"/>), and the first Escape
    /// collapses the dropdown while a second cancels the box (<see cref="CollapseResultsOnEscape"/>).</para>
    ///
    /// Constructed by the host (desktop <see cref="AppSignalHandler"/> / web Planner.razor) with
    /// host-flavoured context (profile/site transform, focus release, redraw). The standalone <c>plan</c>
    /// CLI never constructs one, so <see cref="PlannerState.Search"/> stays null there and the box is inert
    /// -- the same contract the old nullable <c>CommitSuggestionAt</c> carried.
    /// </summary>
    public sealed class PlannerSearchInteraction : SearchInteraction<string>
    {
        private readonly PlannerState _state;
        private readonly ICelestialObjectDB _db;
        private readonly Func<Transform?> _createTransform;
        private readonly Func<string[]?> _autoComplete;
        private readonly Action<int>? _ensureVisible;

        /// <param name="state">The planner state; the box wraps <see cref="PlannerState.SearchInput"/>.</param>
        /// <param name="db">Catalog DB for target resolution.</param>
        /// <param name="createTransform">Site-initialised transform, or null when no site is configured yet.</param>
        /// <param name="autoComplete">The candidate cache, or null until the host finishes the catalog load.</param>
        /// <param name="ensureVisible">Scrolls the resolved target into the planner list.</param>
        /// <param name="deactivate">Releases input focus the host's way (desktop posts DeactivateTextInputSignal).</param>
        /// <param name="requestRedraw">Marks the host surface dirty.</param>
        public PlannerSearchInteraction(
            PlannerState state,
            ICelestialObjectDB db,
            Func<Transform?> createTransform,
            Func<string[]?> autoComplete,
            Action<int>? ensureVisible,
            Action deactivate,
            Action requestRedraw)
            : base(state.SearchInput, requestRedraw, releaseFocus: deactivate)
        {
            _state = state;
            _db = db;
            _createTransform = createTransform;
            _autoComplete = autoComplete;
            _ensureVisible = ensureVisible;
        }

        protected override bool AutoSelectFirstResult => false;
        protected override bool AllowDeselectOnUp => true;
        protected override bool CollapseResultsOnEscape => true;

        protected override ImmutableArray<string> Query(string text)
            => _autoComplete() is { } cache ? PlannerActions.ComputeSuggestions(cache, text) : [];

        // Enter on a highlighted suggestion (or a dropdown click via CommitAt): resolve + select WITHOUT
        // re-searching, then reset the box (clear text, drop the dropdown, release focus).
        protected override void Commit(string suggestion)
        {
            if (_createTransform() is { } transform)
            {
                var idx = PlannerActions.CommitSuggestion(_state, _db, transform, suggestion, _state.Comets);
                if (idx >= 0)
                {
                    _state.SelectedTargetIndex = idx;
                    _ensureVisible?.Invoke(idx);
                }
            }
            ResetBox();
            ReleaseFocus?.Invoke();
            RequestRedraw();
        }

        // Enter with no highlighted suggestion: search targets by the raw text. Matches the old OnCommit,
        // which cleared only the dropdown state (not the text) and left the box active.
        protected override void CommitRawQuery(string text)
        {
            if (text.Length > 0 && _createTransform() is { } transform)
            {
                var idx = PlannerActions.SearchTargets(_state, _db, transform, text, _state.Comets);
                if (idx >= 0)
                {
                    _state.SelectedTargetIndex = idx;
                    _ensureVisible?.Invoke(idx);
                }
            }
            CollapseResults(); // clears Results + SelectedIndex + LastQuery; leaves the text
            RequestRedraw();
        }

        // Escape with nothing to collapse, or the field's own cancel: clear the box + release focus.
        protected override void Dismiss()
        {
            ResetBox();
            base.Dismiss(); // ReleaseFocus?.Invoke()
        }

        private void ResetBox()
        {
            Input.Clear();
            Results = [];
            SelectedIndex = -1;
            LastQuery = "";
        }
    }
}
