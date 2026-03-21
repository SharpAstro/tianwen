namespace TianWen.UI.Abstractions;

/// <summary>
/// Renderer-agnostic key codes for keyboard input handling.
/// Mapped from platform-specific scancodes (SDL3, Console, etc.) by the host.
/// </summary>
public enum InputKey
{
    None,

    // Navigation
    Up,
    Down,
    Left,
    Right,
    Home,
    End,
    PageUp,
    PageDown,

    // Actions
    Enter,
    Escape,
    Tab,
    Space,
    Backspace,
    Delete,

    // Letters
    A, B, C, D, E, F, G, H, I, J, K, L, M,
    N, O, P, Q, R, S, T, U, V, W, X, Y, Z,

    // Digits
    D0, D1, D2, D3, D4, D5, D6, D7, D8, D9,

    // Function keys
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,

    // Symbols
    Plus,
    Minus,
}

/// <summary>
/// Modifier key flags, platform-agnostic.
/// </summary>
[System.Flags]
public enum InputModifier
{
    None  = 0,
    Shift = 1,
    Ctrl  = 2,
    Alt   = 4,
}
