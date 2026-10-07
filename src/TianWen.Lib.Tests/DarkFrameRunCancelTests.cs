using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A dark run that is cancelled stops the exposure it was waiting on, not only its wait: left Exposing, the camera refused the
/// disconnect that followed (a Canon 6D, 2026-09-30: "is exposing or downloading: warm it first").
/// </summary>
public class DarkFrameRunCancelTests : IDisposable
{
    private readonly TempFolders _folders = new TempFolders();

    public void Dispose() => _folders.Dispose();

    [Fact(Timeout = 30_000)]
    public async Task A_cancelled_run_aborts_the_exposure_it_was_waiting_for()
    {
        var root = _folders.Create("tianwen-darkcancel-");
        var external = Substitute.For<IExternal>();
        external.ImageOutputFolder.Returns(root);
        var camera = Substitute.For<ICameraDriver>();
        camera.CanAbortExposure.Returns(true);
        camera.BinX.Returns(1);
        camera.BinY.Returns(1);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var polls = 0;
        camera.GetImageReadyAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            // The second look at the camera: the node's cancel arrives mid-exposure.
            if (++polls == 2)
            {
                cts.Cancel();
            }

            return ValueTask.FromResult(false);
        });

        var run = new DarkFrameRun(external, new FakeTimeProviderWrapper(), NullLogger<DarkFrameRun>.Instance);

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await run.RunAsync(camera, new DarkFrameRunOptions(TimeSpan.FromSeconds(30), Count: 3), progress: null, cts.Token));

        await camera.Received(1).StartExposureAsync(TimeSpan.FromSeconds(30), Arg.Any<Imaging.FrameType>(), Arg.Any<CancellationToken>());
        await camera.Received(1).AbortExposureAsync(CancellationToken.None);
    }
}
