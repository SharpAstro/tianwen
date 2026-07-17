using System;
using DIR.Lib;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Host-agnostic text-input key/text machinery, shared by the desktop event handler
    /// (<see cref="GuiEventHandlerBase"/>) and the web host (Planner.razor) - the same pattern as
    /// <see cref="PlannerSliderInteraction"/>: this logic used to live only in
    /// GuiEventHandlerBase, which the web host never routes through, so clicking/typing into a
    /// text input was a no-op in the browser.
    ///
    /// Focus BOOKKEEPING deliberately stays host-owned (desktop: ActivateTextInputSignal ->
    /// GuiAppState.ActiveTextInput + SDL StartTextInput; web: a field on the page) because
    /// activation has platform side effects; this class owns the per-keystroke decision logic,
    /// which is identical everywhere.
    /// </summary>
    public static class TextInputInteraction
    {
        /// <summary>Host callbacks + optional per-app state <see cref="HandleKey"/> needs.
        /// <see cref="Planner"/>/<see cref="SkySearch"/> enable the suggestion/result arrow
        /// navigation when the respective search input is the active one; either may be null.
        /// <see cref="Deactivate"/> must clear the host's active-input tracking (desktop posts
        /// DeactivateTextInputSignal); <see cref="SetActive"/> must update it to an
        /// already-activated input (Tab cycling).</summary>
        public readonly record struct KeyContext(
            BackgroundTaskTracker Tracker,
            Action Deactivate,
            Action<TextInputState> SetActive,
            Action RequestRedraw,
            PlannerState? Planner = null,
            SkyMapSearchState? SkySearch = null,
            IPixelWidget? ActiveTab = null,
            Func<string?>? GetClipboardText = null,
            Action<string>? SetClipboardText = null);

        /// <summary>Inserts typed text into the active input (the TextInput-event path on
        /// desktop; the printable-keydown path on web) and fires OnTextChanged.</summary>
        public static bool HandleText(TextInputState activeInput, string text)
        {
            activeInput.InsertText(text);
            activeInput.OnTextChanged?.Invoke(activeInput.Text);
            return true;
        }

        /// <summary>
        /// Routes a key press to the active text input: suggestion/result navigation, Tab
        /// cycling, OnKeyOverride, clipboard, then the input's own key handling with
        /// commit/cancel dispatch. Swallows every key while an input is active (returns true),
        /// mirroring the historical desktop behaviour.
        /// </summary>
        public static bool HandleKey(
            TextInputState activeInput, InputKey key, InputModifier modifiers, in KeyContext ctx)
        {
            // Autocomplete arrow navigation (planner search box)
            if (ctx.Planner is { } planner
                && activeInput == planner.SearchInput && planner.Suggestions.Count > 0)
            {
                if (key == InputKey.Down)
                {
                    planner.SuggestionIndex = Math.Min(
                        planner.SuggestionIndex + 1, planner.Suggestions.Count - 1);
                    ctx.RequestRedraw();
                    return true;
                }

                if (key == InputKey.Up && planner.SuggestionIndex >= 0)
                {
                    planner.SuggestionIndex--;
                    ctx.RequestRedraw();
                    return true;
                }
            }

            // Sky-map F3 search: arrow keys navigate the result list. The tab's own
            // TryHandleSearchKey only runs when NO text input is active, but the search input IS
            // active here -- and this method swallows all keys (see the final return) -- so the
            // navigation has to happen here too, mirroring the planner block above. Without this,
            // Up/Down never reach the result list while the user is typing in the search box.
            if (ctx.SkySearch is { } skySearch
                && activeInput == skySearch.SearchInput && skySearch.Results.Length > 0)
            {
                if (key == InputKey.Down)
                {
                    skySearch.SelectedResultIndex = Math.Min(
                        skySearch.SelectedResultIndex + 1, skySearch.Results.Length - 1);
                    ctx.RequestRedraw();
                    return true;
                }
                if (key == InputKey.Up)
                {
                    skySearch.SelectedResultIndex = Math.Max(
                        skySearch.SelectedResultIndex - 1, 0);
                    ctx.RequestRedraw();
                    return true;
                }
            }

            var textKey = key.ToTextInputKey(modifiers);

            // Tab cycling through text inputs on the active tab
            if (key == InputKey.Tab)
            {
                var shift = (modifiers & InputModifier.Shift) != 0;
                var inputs = ctx.ActiveTab?.GetRegisteredTextInputs();
                if (inputs is { Count: > 1 })
                {
                    var idx = inputs.IndexOf(activeInput);
                    if (idx >= 0)
                    {
                        var next = shift
                            ? inputs[(idx - 1 + inputs.Count) % inputs.Count]
                            : inputs[(idx + 1) % inputs.Count];
                        activeInput.Deactivate();
                        next.Activate();
                        ctx.SetActive(next);
                        ctx.RequestRedraw();
                        return true;
                    }
                }
            }

            // Let the input's OnKeyOverride handle it first (autocomplete, etc.)
            if (textKey.HasValue && activeInput.OnKeyOverride?.Invoke(textKey.Value) == true)
            {
                ctx.RequestRedraw();
                return true;
            }

            // Handle Ctrl+V paste via platform clipboard
            if (textKey == TextInputKey.Paste)
            {
                var clipboardText = ctx.GetClipboardText?.Invoke();
                if (!string.IsNullOrEmpty(clipboardText))
                {
                    activeInput.InsertText(clipboardText);
                    activeInput.OnTextChanged?.Invoke(activeInput.Text);
                }
                ctx.RequestRedraw();
                return true;
            }

            // Handle Ctrl+C copy via platform clipboard
            if (textKey == TextInputKey.Copy)
            {
                if (activeInput.HasSelection)
                {
                    ctx.SetClipboardText?.Invoke(
                        activeInput.Text[activeInput.SelectionStart..activeInput.SelectionEnd]);
                }
                return true;
            }

            if (textKey.HasValue && activeInput.HandleKey(textKey.Value))
            {
                // Notify text change on modifying keys
                if (textKey.Value is TextInputKey.Backspace or TextInputKey.Delete)
                {
                    activeInput.OnTextChanged?.Invoke(activeInput.Text);
                }

                if (activeInput.IsCommitted)
                {
                    if (activeInput.OnCommit is { } onCommit)
                    {
                        var text = activeInput.Text;
                        ctx.Tracker.Run(() => onCommit(text), "Text input commit");
                    }
                    activeInput.IsCommitted = false;
                }
                else if (activeInput.IsCancelled)
                {
                    activeInput.OnCancel?.Invoke();
                    activeInput.IsCancelled = false;
                    ctx.Deactivate();
                }

                ctx.RequestRedraw();
                return true;
            }

            return true; // Swallow all keys when text input active
        }
    }
}
