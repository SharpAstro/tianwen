using Microsoft.Extensions.DependencyInjection;
using TianWen.Lib.Devices.Gemini;

namespace TianWen.Lib.Extensions;

public static class GeminiServiceCollectionExtensions
{
    /// <summary>
    /// Registers the native (ASCOM-free) Gemini serial devices plus their discovery probes, all <c>#</c>-terminated:
    /// the FlatPanel Lite cover/calibrator and the Focuser Pro (a rebadged myFocuserPro2 controller) at 9600 baud, sharing
    /// the LX200-family probe group, and the Power&amp;Data Hub Advanced 3 power box at 19200.
    /// </summary>
    public static IServiceCollection AddGemini(this IServiceCollection services) => services
        .AddDevicSource<GeminiDevice, GeminiDeviceSource>(uri => new GeminiDevice(uri))
        .AddSerialProbe<GeminiFlatPanelSerialProbe>()
        .AddDevicSource<GeminiFocuserDevice, GeminiFocuserDeviceSource>(uri => new GeminiFocuserDevice(uri))
        .AddSerialProbe<GeminiFocuserSerialProbe>()
        .AddDevicSource<GeminiPowerBoxDevice, GeminiPowerBoxDeviceSource>(uri => new GeminiPowerBoxDevice(uri))
        .AddSerialProbe<GeminiPowerBoxSerialProbe>();
}
