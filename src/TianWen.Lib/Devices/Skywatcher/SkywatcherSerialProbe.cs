using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Connections;
using TianWen.Lib.Devices.Discovery;

namespace TianWen.Lib.Devices.Skywatcher;

/// <summary>
/// Shared handshake for the two baud variants. Skywatcher mounts respond to
/// <c>:e1\r</c> with their firmware + mount-model code at either 115200 (USB-integrated,
/// e.g. EQ6-R, AzEQ6) or 9600 (legacy serial adapter). Both rates run the exact same
/// command/parse cycle; the only difference is the <see cref="ISerialProbe.BaudRate"/>
/// the probe registers under. The probe service groups by baud, so declaring two
/// concrete classes means the port is opened at 115200 once, all 115200 probes run,
/// port is closed, reopened at 9600, 9600 probes run — no redundant opens.
/// </summary>
internal abstract class SkywatcherSerialProbeBase : ISerialProbe
{
    public string Name => "Skywatcher";
    public abstract int BaudRate { get; }
    public Encoding Encoding => Encoding.ASCII;
    public ProbeExclusivity Exclusivity => ProbeExclusivity.Shared;
    // Skywatcher AxisProtocol responses end in '\r'.
    public ProbeFraming Framing => ProbeFraming.CarriageReturnTerminated;
    public TimeSpan Budget => TimeSpan.FromMilliseconds(300);
    public int MaxAttempts => 1;

    // The URI that Skywatcher publishes uses "SkywatcherDevice" as its host (see
    // SkywatcherDevice.cs). Declaring it here lets Stage 1 verification target the
    // right probe when the active profile pins a Skywatcher mount.
    private static readonly IReadOnlyCollection<string> _hosts = [nameof(SkywatcherDevice)];
    public IReadOnlyCollection<string> MatchesDeviceHosts => _hosts;

    public async ValueTask<SerialProbeMatch?> ProbeAsync(string port, ISerialConnection conn, CancellationToken cancellationToken)
    {
        using var @lock = await conn.WaitAsync(cancellationToken);

        var command = SkywatcherProtocol.BuildCommand('e', '1');
        if (!await conn.TryWriteAsync(command, cancellationToken))
        {
            return null;
        }

        var response = await conn.TryReadTerminatedAsync("\r"u8.ToArray(), cancellationToken);
        if (response is null
            || !SkywatcherProtocol.TryParseResponse(response, out var data)
            || !SkywatcherProtocol.TryParseFirmwareResponse(data, out var firmware))
        {
            return null;
        }

        var modelName = firmware.MountModel.DisplayName;
        var deviceId = $"Skywatcher_{modelName.Replace(' ', '_')}_{firmware.VersionString}_{ISerialConnection.CleanupPortName(port)}";
        var displayName = $"Skywatcher {modelName} (FW {firmware.VersionString})";

        var device = new SkywatcherDevice(DeviceType.Mount, deviceId, displayName, port, BaudRate);
        return new SerialProbeMatch(port, device.DeviceUri);
    }
}

internal sealed class SkywatcherSerialProbeUsb : SkywatcherSerialProbeBase
{
    public override int BaudRate => SkywatcherProtocol.DEFAULT_USB_BAUD;
}

internal sealed class SkywatcherSerialProbeLegacy : SkywatcherSerialProbeBase
{
    public override int BaudRate => SkywatcherProtocol.DEFAULT_LEGACY_BAUD;
}
