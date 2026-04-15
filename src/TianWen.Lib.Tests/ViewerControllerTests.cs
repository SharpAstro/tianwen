using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Unit tests for <see cref="ViewerController"/>.
/// Uses NSubstitute to mock <see cref="IDocumentCache"/>, <see cref="IFileDialogHelper"/>,
/// and <see cref="IPlateSolverFactory"/>.
/// </summary>
public class ViewerControllerTests
{
    private static (ViewerController Controller, ViewerState State,
        IDocumentCache Cache, IFileDialogHelper Dialog, IPlateSolverFactory Factory)
        CreateSut()
    {
        var state = new ViewerState();
        var cache = Substitute.For<IDocumentCache>();
        var dialog = Substitute.For<IFileDialogHelper>();
        var factory = Substitute.For<IPlateSolverFactory>();
        var logger = NullLogger<ViewerController>.Instance;
        var controller = new ViewerController(state, cache, dialog, factory, logger);
        return (controller, state, cache, dialog, factory);
    }

    // --- HandleFileRequest ---

    [Fact]
    public void HandleFileRequest_WhenNoRequestedPath_DoesNotStartLoad()
    {
        var (controller, state, cache, _, _) = CreateSut();
        state.RequestedFilePath = null;

        controller.HandleFileRequest(CancellationToken.None);

        controller.IsLoadPending.ShouldBeFalse();
        cache.DidNotReceive().GetOrLoadAsync(Arg.Any<string>(), Arg.Any<DebayerAlgorithm>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleFileRequest_WhenPathSet_LoadsDocumentAndFiresEvent()
    {
        var (controller, state, cache, _, _) = CreateSut();

        // Extract a real test FITS file so we can create a genuine AstroImageDocument
        var testFitsPath = await SharedTestData.ExtractGZippedFitsFileAsync("PlateSolveTestFile");
        cache.GetOrLoadAsync(Arg.Any<string>(), Arg.Any<DebayerAlgorithm>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => AstroImageDocument.OpenAsync(testFitsPath, DebayerAlgorithm.AHD, CancellationToken.None));

        string? loadedFileName = null;
        controller.FileLoaded += name => loadedFileName = name;

        state.RequestedFilePath = "/test/image.fits";
        controller.HandleFileRequest(CancellationToken.None);
        controller.IsLoadPending.ShouldBeTrue();

        // Wait for background task to complete
        await WaitForLoadAsync(controller);

        controller.Document.ShouldNotBeNull();
        state.NeedsTextureUpdate.ShouldBeTrue();
        state.StatusMessage.ShouldBeNull();
        loadedFileName.ShouldBe("image.fits");
    }

    [Fact]
    public async Task HandleFileRequest_WhenCacheReturnsNull_SetsErrorStatus()
    {
        var (controller, state, cache, _, _) = CreateSut();
        cache.GetOrLoadAsync(Arg.Any<string>(), Arg.Any<DebayerAlgorithm>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AstroImageDocument?>(null));

        state.RequestedFilePath = "/test/missing.fits";
        controller.HandleFileRequest(CancellationToken.None);

        await WaitForLoadAsync(controller);

        controller.Document.ShouldBeNull();
        state.StatusMessage.ShouldContain("Failed to open");
    }

    [Fact]
    public void HandleFileRequest_WhileLoadInProgress_IgnoresNewRequest()
    {
        var (controller, state, cache, _, _) = CreateSut();

        // Block the first load indefinitely
        var tcs = new TaskCompletionSource<AstroImageDocument?>();
        cache.GetOrLoadAsync(Arg.Any<string>(), Arg.Any<DebayerAlgorithm>(), Arg.Any<CancellationToken>())
            .Returns(tcs.Task);

        state.RequestedFilePath = "/test/first.fits";
        controller.HandleFileRequest(CancellationToken.None);
        controller.IsLoadPending.ShouldBeTrue();

        // Set a new request while the first is still pending
        state.RequestedFilePath = "/test/second.fits";
        controller.HandleFileRequest(CancellationToken.None);

        // Only one call to cache (the first one)
        cache.Received(1).GetOrLoadAsync(Arg.Any<string>(), Arg.Any<DebayerAlgorithm>(), Arg.Any<CancellationToken>());

        // Clean up — complete the pending task to avoid unobserved exceptions
        tcs.SetResult(null);
    }

    // --- HandleToolbarAction ---

    [Fact]
    public async Task HandleToolbarAction_Open_CallsFileDialog()
    {
        var (controller, state, _, dialog, _) = CreateSut();
        dialog.PickAsync(Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null)); // cancelled

        controller.HandleToolbarAction(ToolbarAction.Open, reverse: false, CancellationToken.None);

        // Wait for background task
        await Task.Delay(200);
        controller.ReleaseCompletedTasks();

        await dialog.Received(1).PickAsync(
            Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
            Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void HandleToolbarAction_PlateSolve_WhenNoDocument_DoesNothing()
    {
        var (controller, state, _, _, factory) = CreateSut();

        controller.HandleToolbarAction(ToolbarAction.PlateSolve, reverse: false, CancellationToken.None);

        state.IsPlateSolving.ShouldBeFalse();
    }

    // --- ReleaseCompletedTasks ---

    [Fact]
    public async Task ReleaseCompletedTasks_ClearsFinishedTaskReferences()
    {
        var (controller, state, cache, _, _) = CreateSut();
        cache.GetOrLoadAsync(Arg.Any<string>(), Arg.Any<DebayerAlgorithm>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AstroImageDocument?>(null));

        state.RequestedFilePath = "/test/image.fits";
        controller.HandleFileRequest(CancellationToken.None);
        controller.IsLoadPending.ShouldBeTrue();

        await WaitForLoadAsync(controller);

        controller.ReleaseCompletedTasks();
        controller.IsLoadPending.ShouldBeFalse();
    }

    // --- ShutdownAsync ---

    [Fact]
    public async Task ShutdownAsync_AwaitsRunningTasks()
    {
        var (controller, state, cache, _, _) = CreateSut();
        var tcs = new TaskCompletionSource<AstroImageDocument?>();
        cache.GetOrLoadAsync(Arg.Any<string>(), Arg.Any<DebayerAlgorithm>(), Arg.Any<CancellationToken>())
            .Returns(tcs.Task);

        state.RequestedFilePath = "/test/image.fits";
        controller.HandleFileRequest(CancellationToken.None);

        // Complete the pending task
        tcs.SetResult(null);

        // ShutdownAsync should complete without throwing
        await controller.ShutdownAsync();
    }

    /// <summary>
    /// Polls until the controller's load task completes, up to a timeout.
    /// </summary>
    private static async Task WaitForLoadAsync(ViewerController controller, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (controller.IsLoadPending && Environment.TickCount64 < deadline)
        {
            await Task.Delay(10);
        }
    }
}
