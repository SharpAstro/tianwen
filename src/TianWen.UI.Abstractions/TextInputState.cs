using System;
using System.Threading.Tasks;

namespace TianWen.UI.Abstractions;

/// <summary>
/// State for a single-line text input field. Renderer-agnostic — works with both
/// VkRenderer (GPU) and RgbaImageRenderer (TUI). The SDL3 StartTextInput/StopTextInput
/// lifecycle is managed by the host application's event loop.
/// </summary>
public class TextInputState
{
    /// <summary>Whether this field is currently focused and accepting text input.</summary>
    public bool IsActive { get; set; }

    /// <summary>The current text content.</summary>
    public string Text { get; set; } = "";

    /// <summary>Cursor position (character index, 0 = before first char).</summary>
    public int CursorPos { get; set; }

    /// <summary>
    /// Selection anchor position, or -1 if no selection.
    /// Selection range is between <see cref="SelectionStart"/> and <see cref="CursorPos"/>.
    /// </summary>
    public int SelectionAnchor { get; set; } = -1;

    /// <summary>Start of the selection range (min of anchor and cursor).</summary>
    public int SelectionStart => HasSelection ? Math.Min(SelectionAnchor, CursorPos) : CursorPos;

    /// <summary>End of the selection range (max of anchor and cursor).</summary>
    public int SelectionEnd => HasSelection ? Math.Max(SelectionAnchor, CursorPos) : CursorPos;

    /// <summary>Whether there is an active text selection.</summary>
    public bool HasSelection => SelectionAnchor >= 0 && SelectionAnchor != CursorPos;

    /// <summary>Optional placeholder text shown when empty and not active.</summary>
    public string Placeholder { get; set; } = "";

    /// <summary>Set to true when the user pressed Enter to commit the value.</summary>
    public bool IsCommitted { get; set; }

    /// <summary>Set to true when the user pressed Escape to cancel.</summary>
    public bool IsCancelled { get; set; }

    /// <summary>
    /// Called when Enter is pressed to commit the value. Set by the owning tab
    /// so the central event handler doesn't need tab-specific commit logic.
    /// Async — the returned Task is tracked by <see cref="BackgroundTaskTracker"/>.
    /// </summary>
    public Func<string, Task>? OnCommit { get; set; }

    /// <summary>
    /// Called when Escape is pressed to cancel editing. Set by the owning tab.
    /// </summary>
    public Action? OnCancel { get; set; }

    /// <summary>
    /// Called on every text change (insert, backspace, delete). Set by the owning tab
    /// for live-search / autocomplete scenarios.
    /// </summary>
    public Action<string>? OnTextChanged { get; set; }

    /// <summary>
    /// Optional key override handler. Gets first crack at keys when this input is active.
    /// Return true to consume the key (e.g. for autocomplete navigation).
    /// </summary>
    public Func<TextInputKey, bool>? OnKeyOverride { get; set; }

    /// <summary>
    /// Handles a text input event (from SDL3 TextInput or Console.Lib TryReadInput).
    /// Replaces selection (if any) with the input, then inserts at cursor.
    /// </summary>
    public void InsertText(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return;
        }

        DeleteSelection();
        Text = Text.Insert(CursorPos, input);
        CursorPos += input.Length;
        IsCommitted = false;
        IsCancelled = false;
    }

    /// <summary>
    /// Handles a key press. Returns true if the key was consumed.
    /// </summary>
    public bool HandleKey(TextInputKey key)
    {
        switch (key)
        {
            case TextInputKey.Backspace:
                if (HasSelection)
                {
                    DeleteSelection();
                }
                else if (CursorPos > 0)
                {
                    Text = Text.Remove(CursorPos - 1, 1);
                    CursorPos--;
                }
                return true;

            case TextInputKey.Delete:
                if (HasSelection)
                {
                    DeleteSelection();
                }
                else if (CursorPos < Text.Length)
                {
                    Text = Text.Remove(CursorPos, 1);
                }
                return true;

            case TextInputKey.Left:
                if (HasSelection)
                {
                    CursorPos = SelectionStart;
                    ClearSelection();
                }
                else if (CursorPos > 0)
                {
                    CursorPos--;
                }
                return true;

            case TextInputKey.Right:
                if (HasSelection)
                {
                    CursorPos = SelectionEnd;
                    ClearSelection();
                }
                else if (CursorPos < Text.Length)
                {
                    CursorPos++;
                }
                return true;

            case TextInputKey.Home:
                ClearSelection();
                CursorPos = 0;
                return true;

            case TextInputKey.End:
                ClearSelection();
                CursorPos = Text.Length;
                return true;

            case TextInputKey.Enter:
                ClearSelection();
                IsCommitted = true;
                return true;

            case TextInputKey.Escape:
                ClearSelection();
                IsCancelled = true;
                return true;

            case TextInputKey.SelectAll:
                SelectAll();
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Selects all text.
    /// </summary>
    public void SelectAll()
    {
        if (Text.Length > 0)
        {
            SelectionAnchor = 0;
            CursorPos = Text.Length;
        }
    }

    /// <summary>
    /// Selects the word at the given character position.
    /// </summary>
    public void SelectWordAt(int position)
    {
        if (Text.Length == 0)
        {
            return;
        }

        position = Math.Clamp(position, 0, Text.Length - 1);

        // Find word boundaries (alphanumeric + underscore)
        var start = position;
        while (start > 0 && IsWordChar(Text[start - 1]))
        {
            start--;
        }

        var end = position;
        while (end < Text.Length && IsWordChar(Text[end]))
        {
            end++;
        }

        // If we clicked on a non-word char, select just that char
        if (start == end && position < Text.Length)
        {
            end = position + 1;
        }

        SelectionAnchor = start;
        CursorPos = end;
    }

    /// <summary>
    /// Resets the field to empty, uncommitted state.
    /// </summary>
    public void Clear()
    {
        Text = "";
        CursorPos = 0;
        SelectionAnchor = -1;
        IsCommitted = false;
        IsCancelled = false;
    }

    /// <summary>
    /// Activates the field with optional initial text.
    /// </summary>
    public void Activate(string? initialText = null)
    {
        IsActive = true;
        IsCommitted = false;
        IsCancelled = false;
        if (initialText is not null)
        {
            Text = initialText;
            CursorPos = initialText.Length;
        }
    }

    /// <summary>
    /// Deactivates the field.
    /// </summary>
    public void Deactivate()
    {
        IsActive = false;
        ClearSelection();
    }

    private void DeleteSelection()
    {
        if (!HasSelection)
        {
            return;
        }

        var start = SelectionStart;
        var end = SelectionEnd;
        Text = Text.Remove(start, end - start);
        CursorPos = start;
        ClearSelection();
    }

    private void ClearSelection()
    {
        SelectionAnchor = -1;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '-';
}

/// <summary>
/// Abstract key actions for text input, mapped from platform-specific scancodes.
/// </summary>
public enum TextInputKey
{
    Backspace,
    Delete,
    Left,
    Right,
    Home,
    End,
    Enter,
    Escape,
    SelectAll
}
