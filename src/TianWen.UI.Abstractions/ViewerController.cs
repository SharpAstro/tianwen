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

    // Per-load cancellation: a load in flight is cancelled when the user navigates to a different file,
    // so a slow open (e.g. a large SER off a spinning disk) doesn't pin the load slot and stall the
    // switch. _loadingPath is the file the in-flight _loadTask is opening.
    private CancellationTokenSource? _loadCts;
    private string? _loadingPath;

    // A previous source (e.g. a SerPreviewSource holding a memory-mapped file) replaced by a new load.
    // Disposed in ReleaseCompletedTasks (post-frame, UI thread) so no in-flight render still reads it.
    private IDisposable? _pendingDisposeSource;

    /// <summary>
    /// The currently loaded document, or null when the active source is not a document (e.g. a SER
    /// sequence). Still-only features (plate solve, star detection) operate on this.
    /// </summary>
    public AstroImageDocument? Document { get; private set; }

    /// <summary>
    /// The source the renderer previews. For a still image this is the same object as
    /// <see cref="Document"/>; for a SER it is a sequence source and <see cref="Document"/> is null.
    /// </summary>
    public IPreviewSource? Source { get; private set; }

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
        if (state.RequestedFilePath is not { } requestedPath)
        {
            return;
        }

        // A load is already running. If the user has since picked a different file, cancel the stale
        // load so it abandons its (now-pointless) open + decode + stats and frees the slot promptly;
        // the newest RequestedFilePath is then started on a later frame (latest-wins). A repeat request
        // for the same in-flight file is left alone to finish.
        if (_loadTask is { IsCompleted: false })
        {
            // Capture the in-flight load's CTS so we cancel exactly that instance (not whatever _loadCts
            // may later point at). The render thread sets _loadTask + _loadCts together, so they pair up.
            if (_loadCts is { } loadCts
                && !string.Equals(requestedPath, _loadingPath, StringComparison.OrdinalIgnoreCase))
            {
                loadCts.Cancel();
            }
            return;
        }

        state.RequestedFilePath = null;
        _loadingPath = requestedPath;
        state.StatusMessage = $"Loading {Path.GetFileName(requestedPath)}...";

        // Cancel any in-progress star detection from previous image
        _starDetectionCts?.Cancel();
        _starDetectionCts?.Dispose();
        _starDetectionCts = null;

        // Fresh per-load cancellation token, linked to the app token. Cancelled if a later request
        // supersedes this load (see the in-flight branch above).
        _loadCts?.Dispose();
        _loadCts = CancellationTokenSource.CreateLinkedTokenSource(appToken);
        var loadToken = _loadCts.Token;

        var debayerAlgorithm = state.DebayerAlgorithm;
        var isSer = string.Equals(Path.GetExtension(requestedPath), ".ser", StringComparison.OrdinalIgnoreCase);

        _loadTask = Task.Run(async () =>
        {
            var previousSource = Source;

            // SER planetary video: a multi-frame sequence handled by SerPreviewSource, NOT the document
            // file loader. Stats are computed once from frame 0; the viewer auto-switches to playback mode.
            if (isSer)
            {
                try
                {
                    var serSource = await SerPreviewSource.OpenAsync(requestedPath, loadToken);
                    if (loadToken.IsCancellationRequested)
                    {
                        // Superseded between OpenAsync returning and here -- drop the just-opened reader
                        // and leave the previous Source untouched (the newer request loads next frame).
                        serSource.Dispose();
                        return;
                    }
                    var wasSequence = state.IsSequence;
                    Document = null;
                    Source = serSource;
                    state.IsSequence = true;
                    state.FrameCount = serSource.FrameCount;
                    state.FrameIndex = 0;
                    state.IsPlaying = serSource.FrameCount > 1;
                    state.PlaybackFps = serSource.FramesPerSecond is { } fps and > 0 ? (float)fps : 30f;
                    // Planetary SER is a bright disk on a near-black sky. The deep-sky MTF auto-stretch
                    // (Unlinked/Linked/Luma map the median to ~0.25) over-amplifies that dark background
                    // into colour speckle and blows the disk to white -- stable on some frames, runaway
                    // on others (exactly the "first frame ok, others broken" report). Match the standalone
                    // SER viewer instead: show the linear [0,1] frame (StretchMode.None); FillUnitFloat has
                    // already normalised the raw samples by the SER bit depth. Only reset the mode when
                    // entering sequence mode, so the user's pick is preserved while scrubbing SER->SER.
                    if (!wasSequence)
                    {
                        state.StretchMode = StretchMode.None;
                    }
                    state.HistogramLogScale = state.StretchMode is StretchMode.None;
                    state.NeedsTextureUpdate = true;
                    state.CursorImagePosition = null;
                    state.CursorPixelInfo = null;
                    state.StatusMessage = null;
                    FileLoaded?.Invoke(Path.GetFileName(requestedPath));
                }
                catch (OperationCanceledException) { logger.LogDebug("SER open cancelled (superseded or shutdown)"); }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to open SER file: {FilePath}", requestedPath);
                    state.StatusMessage = $"Failed to open: {Path.GetFileName(requestedPath)}";
                }

                StashForDispose(previousSource);
                return;
            }

            AstroImageDocument? newDoc;
            try
            {
                newDoc = await documentCache.GetOrLoadAsync(requestedPath, debayerAlgorithm, loadToken);
            }
            catch (OperationCanceledException)
            {
                logger.LogDebug("Image load cancelled (superseded or shutdown): {FilePath}", requestedPath);
                return;
            }

            if (loadToken.IsCancellationRequested)
            {
                // Superseded after the load completed; don't clobber the newer selection.
                return;
            }

            if (newDoc is not null)
            {
                Document = newDoc;
                Source = newDoc;
                state.IsSequence = false;
                state.IsPlaying = false;
                state.FrameCount = 1;
                state.FrameIndex = 0;
                StashForDispose(previousSource);
                state.NeedsTextureUpdate = true;
                state.CursorImagePosition = null;
                state.CursorPixelInfo = null;
                state.StatusMessage = null;

                // Disable stretch for pre-stretched images, re-enable for linear images
                if (newDoc.IsPreStretched)
                {
                    state.StretchMode = StretchMode.None;
                }
                else if (state.StretchMode is StretchMode.Linked or StretchMode.Luma
                    && newDoc.UnstretchedImage.ChannelCount < 3)
                {
                    // Switch from color to mono — Linked/Luma need 3+ channels
                    state.StretchMode = StretchMode.Unlinked;
                }
                else if (state.StretchMode is StretchMode.None && !newDoc.IsPreStretched)
                {
                    state.StretchMode = StretchMode.Unlinked;
                }
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
                    catch (OperationCanceledException) { logger.LogDebug("Star detection cancelled"); }
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

        // Dispose a source replaced by a newer load, post-frame, so no render still references it.
        if (_pendingDisposeSource is { } stale)
        {
            _pendingDisposeSource = null;
            stale.Dispose();
        }
    }

    // Queues a replaced source for post-frame disposal (only disposable sources -- SerPreviewSource holds
    // a memory-mapped file; an AstroImageDocument has nothing unmanaged and is left to the GC).
    private void StashForDispose(IPreviewSource? previous)
    {
        if (previous is IDisposable d && !ReferenceEquals(previous, Source))
        {
            _pendingDisposeSource = d;
        }
    }

    /// <summary>
    /// Awaits all in-flight tasks and disposes the star detection CTS. Call at app shutdown.
    /// </summary>
    public async Task ShutdownAsync()
    {
        _starDetectionCts?.Cancel();
        _starDetectionCts?.Dispose();

        // Cancel an in-flight load so it bails (and disposes its own half-opened reader) before we tear
        // down. Await it first, THEN dispose Source -- otherwise a load completing mid-shutdown could
        // assign a fresh Source after we already disposed the previous one, leaking the new reader.
        _loadCts?.Cancel();

        if (_loadTask is not null)
        {
            try { await _loadTask; } catch (OperationCanceledException) { logger.LogDebug("Load task cancelled during shutdown"); }
        }
        if (_backgroundTask is not null)
        {
            try { await _backgroundTask; } catch (OperationCanceledException) { logger.LogDebug("Background task cancelled during shutdown"); }
        }

        _loadCts?.Dispose();
        _pendingDisposeSource?.Dispose();
        _pendingDisposeSource = null;
        (Source as IDisposable)?.Dispose();
    }
}
