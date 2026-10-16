using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Where a <see cref="PlanetaryCapture"/>'s loop runs (TIA2's capture-priority leg): on a thread of its own at
/// <see cref="ThreadPriority.AboveNormal"/>, never the thread pool, whose normal-priority continuations fell behind the driver's
/// hand-off while the machine's cores were busy at normal priority; and, with no frame coming, a thread that waits rather
/// than spins.
/// </summary>
[Collection("Session")]
public partial class PlanetaryCaptureReaderTests(ITestOutputHelper output)
{
    [Fact(Timeout = 60_000)]
    public async Task EveryFrameIsTakenOnTheReadersOwnThreadAboveNormalNeverOnThePool()
    {
        var ct = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 10, 16, 22, 0, 0, TimeSpan.Zero));
        var camera = new FakeCameraDriver(new FakeDevice(DeviceType.Camera, 8), new FakeExternal(output, timeProvider).BuildServiceProvider());
        await camera.ConnectAsync(ct);
        camera.NumX = 64;
        camera.NumY = 64;
        await using var capture = new PlanetaryCapture(new FakeTimeProviderWrapper(), NullLogger.Instance);
        capture.ArmFrameGate();
        capture.Start(camera, new VideoCaptureOptions(TimeSpan.FromMilliseconds(5)), ct).ShouldBeTrue();

        // Each frame stepped by the gate, whose wait the step completes on THIS thread: the loop must still take it on its own.
        for (var frame = 0; frame < 20; frame++)
        {
            var next = capture.WaitForNextFrameAsync(ct);
            capture.StepFrame();
            await next;
        }

        capture.FramesReceived.ShouldBe(20);
        capture.FramesOnThreadPool.ShouldBe(0, "every frame's iteration ran on the reader's own thread");
        capture.ReaderPriority.ShouldBe(CaptureReaderThread.Priority);
        CaptureReaderThread.Priority.ShouldBe(ThreadPriority.AboveNormal, "above every normal-priority thread, below the driver's Highest");
        await capture.StopAsync(ct);
        capture.IsCapturing.ShouldBeFalse();
    }

    [Fact(Timeout = 60_000)]
    public async Task AReaderWithNoFrameComingHoldsNoCore()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() || OperatingSystem.IsLinux(), "a thread's processor time is read on Windows and Linux");
        var ct = TestContext.Current.CancellationToken;
        var reader = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var camera = Substitute.For<IVideoCameraDriver>();
        camera.CanVideoCapture.Returns(true);
        camera.CaptureVideoAsync(Arg.Any<VideoCaptureOptions>(), Arg.Any<CancellationToken>())
            .Returns(call => NoFrameEverAsync(reader, call.Arg<CancellationToken>()));
        await using var capture = new PlanetaryCapture(SystemTimeProvider.Instance, NullLogger.Instance);
        capture.Start(camera, new VideoCaptureOptions(TimeSpan.FromMilliseconds(5)), ct).ShouldBeTrue();
        var readerThread = await reader.Task.WaitAsync(ct);

        var before = ProcessorTime(readerThread);
        var clock = Stopwatch.StartNew();
        await SystemTimeProvider.Instance.SleepAsync(TimeSpan.FromSeconds(1), ct);
        var used = ProcessorTime(readerThread) - before;
        output.WriteLine($"the reader used {used.TotalMilliseconds:F1} ms of processor time in {clock.Elapsed.TotalMilliseconds:F0} ms with no frame");

        // A spinning reader would use about all of it; one blocked in its wait uses none.
        used.ShouldBeLessThan(TimeSpan.FromMilliseconds(100));
        capture.IsCapturing.ShouldBeTrue("the capture still waits for its first frame");
        await capture.StopAsync(ct);
        capture.IsCapturing.ShouldBeFalse("the stop reached the reader, which was waiting");
    }

    // A camera whose stream never sends a frame: it says which thread asked for the first, the reader, and waits to be stopped.
    private static async IAsyncEnumerable<Image> NoFrameEverAsync(TaskCompletionSource<int> reader, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        reader.TrySetResult(NativeThreadId());
        await new TaskCompletionSource().Task.WaitAsync(cancellationToken);
        yield break;
    }

    private static TimeSpan ProcessorTime(int nativeThreadId)
    {
        using var process = Process.GetCurrentProcess();
        return process.Threads.Cast<ProcessThread>().Single(thread => thread.Id == nativeThreadId).TotalProcessorTime;
    }

    private static int NativeThreadId() => OperatingSystem.IsWindows() ? (int)GetCurrentThreadId() : GetTid();

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("libc", EntryPoint = "gettid")]
    private static partial int GetTid();
}
