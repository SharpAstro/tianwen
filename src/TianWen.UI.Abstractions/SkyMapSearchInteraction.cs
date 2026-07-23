using System;
using System.Collections.Immutable;
using DIR.Lib;
using TianWen.Lib.Astrometry.Catalogs;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// The sky-map F3 search modal as a <see cref="SearchInteraction{TResult}"/> (TResult =
    /// <see cref="SkyMapSearchResult"/>): input wiring + the Up/Down/Enter/Escape protocol + the result
    /// list come from the DIR.Lib base; this subclass supplies only the catalog resolution
    /// (<see cref="SkyMapSearchActions.FilterResults"/>) and dispatches commit / dismiss through the host's
    /// signal bus. It replaces the three <c>SearchInput.On*</c> assignments + the arrow-nav block that were
    /// spread across <c>AppSignalHandler.SkyMap</c> and <c>TextInputInteraction</c>.
    ///
    /// <para>Behaviour preserved via base policy overrides: the first result is auto-highlighted
    /// (<see cref="AutoSelectFirstResult"/> so Enter commits it), Up clamps at 0
    /// (<see cref="AllowDeselectOnUp"/> false), and Escape closes the modal on the first press
    /// (<see cref="CollapseResultsOnEscape"/> false -> falls through to <see cref="Dismiss"/>).</para>
    ///
    /// Commit + close go through the signal bus (not directly) because they need per-invocation DI context
    /// (catalog DB, comet repo, viewing time, site, sky-map mutation) that lives in the host handler.
    /// </summary>
    public sealed class SkyMapSearchInteraction : SearchInteraction<SkyMapSearchResult>
    {
        private readonly SkyMapSearchState _search;
        private readonly ICelestialObjectDB _db;
        private readonly Action _commit;
        private readonly Action _close;

        /// <param name="search">The F3 search state; the box wraps <see cref="SkyMapSearchState.SearchInput"/>.</param>
        /// <param name="db">Catalog DB the result filter reads.</param>
        /// <param name="commit">Posts the host's commit signal (resolves the highlighted row + slews / info-panels it).</param>
        /// <param name="close">Posts the host's close-modal signal (also releases input focus).</param>
        /// <param name="requestRedraw">Marks the host surface dirty.</param>
        public SkyMapSearchInteraction(
            SkyMapSearchState search,
            ICelestialObjectDB db,
            Action commit,
            Action close,
            Action requestRedraw)
            : base(search.SearchInput, requestRedraw, releaseFocus: null) // focus release rides the close signal's handler
        {
            _search = search;
            _db = db;
            _commit = commit;
            _close = close;
        }

        protected override bool AutoSelectFirstResult => true;
        protected override bool AllowDeselectOnUp => false;
        protected override bool CollapseResultsOnEscape => false;

        protected override ImmutableArray<SkyMapSearchResult> Query(string text)
            => SkyMapSearchActions.FilterResults(_search, _db, text);

        protected override void OnResultsChanged() => _search.ResultsScrollOffset = 0;

        // Commit the highlighted result. The base has already set SelectedIndex (keyboard Enter or a
        // CommitAt mouse click), so the handler reads it back off this interaction.
        protected override void Commit(SkyMapSearchResult result) => _commit();

        // Escape (nothing to collapse) / OnCancel: close the modal via the host signal.
        protected override void Dismiss() => _close();
    }
}
