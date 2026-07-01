using Microsoft.Extensions.DependencyInjection;
using TianWen.Lib.Devices.Gemini;

namespace TianWen.Lib.Extensions;

public static class GeminiServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Gemini FlatPanel Lite as a native (ASCOM-free) serial cover/calibrator device source
    /// plus its discovery probe (9600 baud, <c>#</c>-terminated, shares the LX200-family probe group).
    /// </summary>
    public static IServiceCollection AddGemini(this IServiceCollection services) => services
        .AddDevicSource<GeminiDevice, GeminiDeviceSource>(uri => new GeminiDevice(uri))
        .AddSerialProbe<GeminiFlatPanelSerialProbe>();
}
