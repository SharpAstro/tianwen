using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Default <see cref="IFileDialogHelper"/> implementation that delegates to the
/// static <see cref="FileDialogHelper"/> (P/Invoke on Windows, shell on Linux/macOS).
/// </summary>
internal sealed class FileDialogService : IFileDialogHelper
{
    public Task<string?> PickAsync(
        IReadOnlyDictionary<string, IReadOnlyList<string>> filters,
        string? combinedFilterName = null,
        string title = "Open file",
        CancellationToken cancellationToken = default)
        => FileDialogHelper.PickAsync(filters, combinedFilterName, title, cancellationToken);
}
