using TianWen.UI.Abstractions;
using static SDL3.SDL;

namespace TianWen.UI.Shared
{
    /// <summary>
    /// Maps SDL3 scancodes and key modifiers to the renderer-agnostic
    /// <see cref="InputKey"/> and <see cref="InputModifier"/> types.
    /// </summary>
    public static class SdlInputMapping
    {
        extension(Scancode scancode)
        {
            public InputKey ToInputKey => scancode switch
            {
                Scancode.Up => InputKey.Up,
                Scancode.Down => InputKey.Down,
                Scancode.Left => InputKey.Left,
                Scancode.Right => InputKey.Right,
                Scancode.Home => InputKey.Home,
                Scancode.End => InputKey.End,
                Scancode.Pageup => InputKey.PageUp,
                Scancode.Pagedown => InputKey.PageDown,
                Scancode.Return => InputKey.Enter,
                Scancode.Escape => InputKey.Escape,
                Scancode.Tab => InputKey.Tab,
                Scancode.Space => InputKey.Space,
                Scancode.Backspace => InputKey.Backspace,
                Scancode.Delete => InputKey.Delete,
                >= Scancode.A and <= Scancode.Z => (InputKey)((int)InputKey.A + (scancode - Scancode.A)),
                >= Scancode.Alpha1 and <= Scancode.Alpha0 => (InputKey)((int)InputKey.D1 + (scancode - Scancode.Alpha1)),
                >= Scancode.F1 and <= Scancode.F12 => (InputKey)((int)InputKey.F1 + (scancode - Scancode.F1)),
                Scancode.Equals => InputKey.Plus,
                Scancode.Minus => InputKey.Minus,
                _ => InputKey.None,
            };
        }

        extension(Keymod keymod)
        {
            public InputModifier ToInputModifier
            {
                get
                {
                    var mod = InputModifier.None;
                    if ((keymod & Keymod.Shift) != 0) mod |= InputModifier.Shift;
                    if ((keymod & Keymod.Ctrl) != 0) mod |= InputModifier.Ctrl;
                    if ((keymod & Keymod.Alt) != 0) mod |= InputModifier.Alt;
                    return mod;
                }
            }
        }
    }
}
