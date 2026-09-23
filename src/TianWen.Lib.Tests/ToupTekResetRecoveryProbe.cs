using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Meziantou.Extensions.Logging.Xunit.v3;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.ToupTek;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The DAL camera driver's reset step, end to end on a real ToupTek body: reset, found again,
/// settings put back, and a frame taken afterwards.
/// </summary>
/// <remarks>
/// <para>The scripted <see cref="DALCameraRecoveryTests"/> pin WHEN the driver resets; this pins that
/// the reset itself works on hardware, which a fake cannot: the device really drops off the bus, the
/// SDK really forgets the handle, and every setting really returns to its power-on default. It does not
/// wait for a stall to happen (one in several hundred frames); it calls the reset directly.</para>
/// <para>Gated on <c>TIANWEN_TOUPTEK_PROBE=1</c> because it resets whatever ToupTek camera is attached.
/// Real clock: the driver's reappear wait has to be measured in real seconds.</para>
/// </remarks>
[Collection("Devices")]
public class ToupTekResetRecoveryProbe(ITestOutputHelper output)
{
    private const string GateVar = "TIANWEN_TOUPTEK_PROBE";

    [Fact(Timeout = 120_000)]
    public async Task AResetCameraComesBackWithItsSettingsAndTakesAFrame()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(GateVar) == "1", $"{GateVar} is not 1 (this RESETS the attached camera)");
        var ct = TestContext.Current.CancellationToken;
        var device = new ToupTekDeviceSource().RegisteredDevices(DeviceType.Camera).FirstOrDefault();
        Assert.SkipWhen(device is null, "no ToupTek camera attached");

        var external = new FakeExternal(output);
        using var sp = new ServiceCollection()
            .AddSingleton<IExternal>(external)
            .AddSingleton<ITimeProvider>(SystemTimeProvider.Instance)
            .AddLogging(logging => logging.AddProvider(new XUnitLoggerProvider(output, false)).SetMinimumLevel(LogLevel.Debug))
            .BuildServiceProvider();
        var camera = new ToupTekCameraDriver(device, sp);
        await camera.ConnectAsync(ct);
        try
        {
            var gain = (short)(camera.GainMin + 7);
            var offset = camera.OffsetMin + 3;
            await camera.SetGainAsync(gain, ct);
            await camera.SetOffsetAsync(offset, ct);
            camera.BinX = 2;
            var width = camera.NumX;
            output.WriteLine($"{camera.Name}: gain {gain}, offset {offset}, bin 2, {width} px wide");

            (await TakeFrameAsync(camera)).ShouldBe(width, "a frame before the reset");

            var watch = Stopwatch.StartNew();
            await camera.ResetAndReopenAsync(ct);
            output.WriteLine($"reset and reopen took {watch.Elapsed.TotalSeconds:F1} s");

            (await camera.GetGainAsync(ct)).ShouldBe(gain, "restored after the reset");
            (await camera.GetOffsetAsync(ct)).ShouldBe(offset, "restored after the reset");
            camera.BinX.ShouldBe(2);
            (await TakeFrameAsync(camera)).ShouldBe(width, "the first frame after the reset, still binned");
        }
        finally
        {
            await camera.DisconnectAsync(ct);
        }
    }

    private static async Task<int> TakeFrameAsync(ICameraDriver camera)
    {
        var ct = TestContext.Current.CancellationToken;
        var exposure = TimeSpan.FromMilliseconds(10);
        await camera.StartExposureAsync(exposure, cancellationToken: ct);
        var ready = await camera.WaitForImageReadyAsync(exposure, SystemTimeProvider.Instance, TimeSpan.FromMilliseconds(5),
            timeout: TimeSpan.FromSeconds(10), cancellationToken: ct);
        ready.ShouldBeTrue("the frame arrived");
        var image = (await camera.GetImageAsync(ct)).ShouldNotBeNull();
        try
        {
            return image.Width;
        }
        finally
        {
            image.Release();
        }
    }
}
