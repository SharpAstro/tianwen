using System.IO;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;

namespace TianWen.UI.Abstractions;

/// <summary>
/// The desktop-alert level this computer keeps (a per-computer choice, never a profile's): one small file,
/// <c>Notifications/desktop-alerts.json</c>, written atomically as every AppData file is. A missing or unreadable file
/// reads as the default.
/// </summary>
public static class DesktopAlertSettings
{
    /// <summary>What a computer with no saved choice uses.</summary>
    public const DesktopAlertLevel Default = DesktopAlertLevel.ErrorsAndSafety;

    public static string PathFor(IExternal external) => Path.Combine(external.AppDataFolder.FullName, "Notifications", "desktop-alerts.json");

    public static async Task<DesktopAlertLevel> LoadAsync(IExternal external, CancellationToken cancellationToken)
        => await external.TryReadJsonAsync(PathFor(external), DesktopAlertSettingsJsonContext.Default.DesktopAlertSettingsDto, logger: null, cancellationToken) is { } saved
            && System.Enum.IsDefined(saved.Level)
            ? saved.Level
            : Default;

    public static Task SaveAsync(IExternal external, DesktopAlertLevel level, CancellationToken cancellationToken)
        => external.AtomicWriteJsonAsync(PathFor(external), new DesktopAlertSettingsDto(level), DesktopAlertSettingsJsonContext.Default.DesktopAlertSettingsDto, cancellationToken);
}

/// <summary>The saved file.</summary>
public sealed record DesktopAlertSettingsDto(DesktopAlertLevel Level);

[JsonSerializable(typeof(DesktopAlertSettingsDto))]
internal partial class DesktopAlertSettingsJsonContext : JsonSerializerContext;
