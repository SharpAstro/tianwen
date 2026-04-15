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
}
