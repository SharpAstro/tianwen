using DIR.Lib;
using TianWen.Hosting.Dto;

namespace TianWen.UI.Abstractions;

/// <summary>
/// The question a request for control of this computer's rig puts to whoever is at it (P6b of
/// docs/plans/hardware-in-the-server.md, decision 13, #1021), drawn over everything as the quit question is: one description
/// for the GUI's card and the TUI's line. The default is to decline, since an Enter pressed for something else must not
/// hand the rig to another computer: <see cref="AllowKey"/> allows, Escape answers later, from the Home card's Sharing
/// panel, while the asker still waits.
/// </summary>
public static class ControlRequestQuestion
{
    /// <summary>The card's title.</summary>
    public const string Title = "Control of this rig";

    /// <summary>The key that allows the request.</summary>
    public const InputKey AllowKey = InputKey.A;

    /// <summary>Who asks, as the question names them: the label its computer gave, and where it asks from.</summary>
    public static string Who(PendingControlRequestDto request) =>
        request.Address is { } address ? $"'{request.Label}' ({address})" : $"'{request.Label}'";

    /// <summary>What the request is, and what allowing it gives.</summary>
    public static string Message(PendingControlRequestDto request) =>
        $"{Who(request)} asks to control this rig. Allowed, it can start and stop runs and move this rig's devices, "
        + "until its grant is revoked on the Home card.";

    /// <summary>The keys, as the card says them.</summary>
    public const string KeyHint = "Enter: Decline   A: Allow   Escape: answer later, on the Home card";

    /// <summary>The whole question on one line, for the TUI's status bar.</summary>
    public static string OneLine(PendingControlRequestDto request) =>
        $"{Who(request)} asks to control this rig. [A] Allow  [Enter] Decline  [Esc] Later";
}
