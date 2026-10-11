using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;

namespace TianWen.UI.Abstractions;

/// <summary>
/// The window's file dialogs, registered before the window exists and bound to it once it does. A host builds its
/// services first, and the dialogs are the WINDOW's: they parent to it and answer on its event loop (SdlVulkan.Renderer's
/// <c>SdlFileDialogs</c>).
/// </summary>
/// <remarks>
/// <para>
/// These replace TianWen's own dialogs (<c>FileDialogHelper</c>: comdlg32 on Windows, zenity, kdialog and osascript
/// elsewhere), which blocked the render thread on Windows for as long as the dialog was open. The platform code now lives
/// once, upstream, shared with every other DIR.Lib application.
/// </para>
/// <para>
/// Asked before <see cref="Bind"/>, or in a host with no window (the CLI registers the viewer's services for its state
/// alone), it answers with an <see cref="FileDialogResult.Error"/> saying so rather than throwing: a toolbar's Open in a
/// windowless host is a status line, not a crash.
/// </para>
/// </remarks>
public sealed class HostFileDialogs : IFileDialogs
{
    private IFileDialogs? _window;

    /// <summary>Binds the window's dialogs. Called once by the host, on its main thread, after it creates the window.</summary>
    public void Bind(IFileDialogs window) => _window = window;

    /// <inheritdoc/>
    public Task<FileDialogResult> OpenAsync(FileDialogRequest request, CancellationToken cancellationToken = default)
        => _window?.OpenAsync(request, cancellationToken) ?? Unbound();

    /// <inheritdoc/>
    public Task<FileDialogResult> SaveAsync(FileDialogRequest request, CancellationToken cancellationToken = default)
        => _window?.SaveAsync(request, cancellationToken) ?? Unbound();

    /// <inheritdoc/>
    public Task<FileDialogResult> OpenFolderAsync(FileDialogRequest request, CancellationToken cancellationToken = default)
        => _window?.OpenFolderAsync(request, cancellationToken) ?? Unbound();

    private static Task<FileDialogResult> Unbound()
        => Task.FromResult(new FileDialogResult { Error = "there is no window to show a file dialog over" });
}
