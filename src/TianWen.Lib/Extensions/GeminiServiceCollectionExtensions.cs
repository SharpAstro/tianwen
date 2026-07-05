using Microsoft.Extensions.DependencyInjection;
using TianWen.Lib.Devices.Gemini;

namespace TianWen.Lib.Extensions;

public static class GeminiServiceCollectionExtensions
{
    /// <summary>
    /// Registers the native (ASCOM-free) Gemini serial devices plus their discovery probes (9600 baud,
    /// <c>#</c>-terminated, sharing the LX200-family probe group): the FlatPanel Lite cover/calibrator and
    /// the Focuser Pro (a rebadged myFocuserPro2 controller).
    /// </summary>
    public static IServiceCollection AddGemini(this IServiceCollection services) => services
        .AddDevicSource<GeminiDevice, GeminiDeviceSource>(uri => new GeminiDevice(uri))
        .AddSerialProbe<GeminiFlatPanelSerialProbe>()
        .AddDevicSource<GeminiFocuserDevice, GeminiFocuserDeviceSource>(uri => new GeminiFocuserDevice(uri))
        .AddSerialProbe<GeminiFocuserSerialProbe>();
}
