using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Abstraction over the native file picker dialog.
/// See <see cref="FileDialogHelper"/> for the platform-specific implementation.
/// </summary>
public interface IFileDialogHelper
{
    /// <summary>
    /// Shows a native open-file dialog filtered to the given file types.
    /// Returns the selected path, or <c>null</c> if cancelled.
    /// </summary>
    Task<string?> PickAsync(
        IReadOnlyDictionary<string, IReadOnlyList<string>> filters,
        string? combinedFilterName = null,
        string title = "Open file",
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Shows a native save-file dialog offering the given file types.
    /// Returns the chosen path, or <c>null</c> if cancelled.
    /// </summary>
    /// <remarks>
    /// The caller decides the FORMAT from the returned path's extension, not from which filter the
    /// user highlighted. Only Windows reports the chosen filter back (<c>nFilterIndex</c>); zenity,
    /// kdialog and osascript report nothing but the path, so a filter-index contract could not be
    /// honoured on two of the three platforms. Reading the extension also does what a user who types
    /// <c>shot.jpg</c> under a highlighted PNG filter plainly means.
    /// </remarks>
    /// <param name="filters">Display name to extensions map. The FIRST entry is the default type, and
    /// its first extension is appended when the user types a name without one.</param>
    /// <param name="suggestedFileName">Pre-filled file name, extension included.</param>
    /// <param name="title">Dialog title.</param>
    /// <param name="cancellationToken">Cancellation token (only effective on the process-based dialogs).</param>
    Task<string?> SaveAsync(
        IReadOnlyDictionary<string, IReadOnlyList<string>> filters,
        string? suggestedFileName = null,
        string title = "Save image",
        CancellationToken cancellationToken = default);
}
