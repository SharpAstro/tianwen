using System;

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

    /// <summary>Optional placeholder text shown when empty and not active.</summary>
    public string Placeholder { get; set; } = "";

    /// <summary>Set to true when the user pressed Enter to commit the value.</summary>
    public bool IsCommitted { get; set; }

    /// <summary>Set to true when the user pressed Escape to cancel.</summary>
    public bool IsCancelled { get; set; }

    /// <summary>
    /// Handles a text input event (from SDL3 TextInput or Console.Lib TryReadInput).
    /// Inserts the text at the cursor position.
    /// </summary>
    public void InsertText(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return;
        }

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
                if (CursorPos > 0)
                {
                    Text = Text.Remove(CursorPos - 1, 1);
                    CursorPos--;
                }
                return true;

            case TextInputKey.Delete:
                if (CursorPos < Text.Length)
                {
                    Text = Text.Remove(CursorPos, 1);
                }
                return true;

            case TextInputKey.Left:
                if (CursorPos > 0)
                {
                    CursorPos--;
                }
                return true;

            case TextInputKey.Right:
                if (CursorPos < Text.Length)
                {
                    CursorPos++;
                }
                return true;

            case TextInputKey.Home:
                CursorPos = 0;
                return true;

            case TextInputKey.End:
                CursorPos = Text.Length;
                return true;

            case TextInputKey.Enter:
                IsCommitted = true;
                return true;

            case TextInputKey.Escape:
                IsCancelled = true;
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Resets the field to empty, uncommitted state.
    /// </summary>
    public void Clear()
    {
        Text = "";
        CursorPos = 0;
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
    }
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
    Escape
}
