using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Orchestrates file loading, star detection, and DI-dependent toolbar actions
/// for the FITS viewer. Owns document lifecycle and background task management.
/// Does not depend on SDL/Vulkan — display concerns stay in Program.cs.
/// </summary>
public sealed class ViewerController(
    ViewerState state,
    IDocumentCache documentCache,
    IFileDialogHelper fileDialog,
    IPlateSolverFactory plateSolverFactory,
    ILogger<ViewerController> logger)
{
    private Task? _loadTask;
    private Task? _backgroundTask;
    private Task? _starDetectionTask;
    private CancellationTokenSource? _starDetectionCts;

    /// <summary>The currently loaded document. Null until the first file is loaded.</summary>
    public AstroImageDocument? Document { get; private set; }

    /// <summary>
    /// Fires with the loaded filename after a document is successfully opened.
    /// Subscribers must be thread-safe (fires on a background thread).
    /// </summary>
    public event Action<string>? FileLoaded;

    /// <summary>
    /// True when a file-load task is in flight.
    /// Used by the render loop to gate <c>CheckNeedsRedraw</c>.
    /// </summary>
    public bool IsLoadPending => _loadTask is { IsCompleted: false };

    /// <summary>
    /// Checks <see cref="ViewerState.RequestedFilePath"/> and, if set, starts loading
    /// on a background thread. Only one load runs at a time.
    /// Must be called every frame from OnRender.
    /// </summary>
    public void HandleFileRequest(CancellationToken appToken)
    {
        if (state.RequestedFilePath is not { } requestedPath
            || (_loadTask is not null && !_loadTask.IsCompleted))
        {
            return;
        }

        state.RequestedFilePath = null;
        state.StatusMessage = $"Loading {Path.GetFileName(requestedPath)}...";

        // Cancel any in-progress star detection from previous image
        _starDetectionCts?.Cancel();
        _starDetectionCts?.Dispose();
        _starDetectionCts = null;

        var debayerAlgorithm = state.DebayerAlgorithm;

        _loadTask = Task.Run(async () =>
        {
            var newDoc = await documentCache.GetOrLoadAsync(requestedPath, debayerAlgorithm, appToken);
            if (newDoc is not null)
            {
                Document = newDoc;
                state.NeedsTextureUpdate = true;
                state.CursorImagePosition = null;
                state.CursorPixelInfo = null;
                state.StatusMessage = null;

                // Disable stretch for pre-stretched images, re-enable for linear images
                state.StretchMode = newDoc.IsPreStretched ? StretchMode.None : StretchMode.Unlinked;
                state.HistogramLogScale = state.StretchMode is StretchMode.None;

                if (newDoc.Wcs is { } wcs)
                {
                    logger.LogInformation("WCS: HasCD={HasCDMatrix}, Approx={IsApproximate}, Scale={PixelScale:F2}\"/px, RA={CenterRA:F4}h, Dec={CenterDec:F4}°",
                        wcs.HasCDMatrix, wcs.IsApproximate, wcs.PixelScaleArcsec, wcs.CenterRA, wcs.CenterDec);
                }

                FileLoaded?.Invoke(Path.GetFileName(requestedPath));

                // Kick off star detection in the background
                var sdCts = CancellationTokenSource.CreateLinkedTokenSource(appToken);
                _starDetectionCts = sdCts;
                _starDetectionTask = Task.Run(async () =>
                {
                    try
                    {
                        await newDoc.DetectStarsAsync(sdCts.Token);
                        logger.LogInformation("Detected {StarCount} stars in {Duration:F1}s (HFR={HFR:F2}, FWHM={FWHM:F2})",
                            newDoc.Stars?.Count ?? 0, newDoc.StarDetectionDuration.TotalSeconds, newDoc.AverageHFR, newDoc.AverageFWHM);
                        state.NeedsRedraw = true;
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Star detection failed");
                        newDoc.Stars = StarList.Empty;
                        state.StatusMessage = "Star detection failed";
                        state.NeedsRedraw = true;
                    }
                }, sdCts.Token);
            }
            else
            {
                logger.LogWarning("Failed to open image file: {FilePath}", requestedPath);
                state.StatusMessage = $"Failed to open: {Path.GetFileName(requestedPath)}";
            }
        }, appToken);
    }

    /// <summary>
    /// Handles toolbar actions that require DI (Open, PlateSolve).
    /// Call after <see cref="ViewerActions.HandleToolbarAction"/> returns <c>false</c>.
    /// </summary>
    public void HandleToolbarAction(ToolbarAction action, bool reverse, CancellationToken appToken)
    {
        switch (action)
        {
            case ToolbarAction.Open:
                state.StatusMessage = "Opening file dialog...";
                _backgroundTask = Task.Run(async () =>
                {
                    var filters = AstroImageDocument.FileDialogFilters
                        .ToDictionary(f => f.Name, f => (IReadOnlyList<string>)f.Extensions);
                    var picked = await fileDialog.PickAsync(filters, combinedFilterName: "All supported images", title: "Open image").ConfigureAwait(false);
                    state.StatusMessage = null;
                    if (picked is null)
                    {
                        return;
                    }

                    if (Directory.Exists(picked))
                    {
                        ViewerActions.ScanFolder(state, picked);
                        if (state.ImageFileNames.Count > 0)
                        {
                            ViewerActions.SelectFile(state, 0);
                        }
                    }
                    else if (File.Exists(picked))
                    {
                        var dir = Path.GetDirectoryName(picked);
                        if (dir is not null)
                        {
                            ViewerActions.ScanFolder(state, dir, Path.GetFileName(picked));
                        }
                        state.RequestedFilePath = picked;
                    }
                }, appToken);
                break;

            case ToolbarAction.PlateSolve:
                if (Document is not null && !state.IsPlateSolving && !Document.IsPlateSolved)
                {
                    _backgroundTask = ViewerActions.PlateSolveAsync(Document, state, plateSolverFactory, appToken);
                }
                break;
        }
    }

    /// <summary>
    /// Called from OnPostFrame to release completed task closures so captured documents can be GC'd.
    /// </summary>
    public void ReleaseCompletedTasks()
    {
        if (_loadTask is { IsCompleted: true }) _loadTask = null;
        if (_starDetectionTask is { IsCompleted: true }) _starDetectionTask = null;
        if (_backgroundTask is { IsCompleted: true }) _backgroundTask = null;
    }

    /// <summary>
    /// Awaits all in-flight tasks and disposes the star detection CTS. Call at app shutdown.
    /// </summary>
    public async Task ShutdownAsync()
    {
        _starDetectionCts?.Cancel();
        _starDetectionCts?.Dispose();

        if (_loadTask is not null)
        {
            try { await _loadTask; } catch (OperationCanceledException) { }
        }
        if (_backgroundTask is not null)
        {
            try { await _backgroundTask; } catch (OperationCanceledException) { }
        }
    }
}
