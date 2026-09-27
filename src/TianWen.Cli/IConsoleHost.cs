using Console.Lib;
using Microsoft.Extensions.Hosting;
using TianWen.Lib.Devices;
using TianWen.RemoteClient;

namespace TianWen.Cli;

public interface IConsoleHost
{
    IVirtualTerminal Terminal { get; }

    /// <summary>
    /// The devices of the given type, or the profiles, as this computer's node lists them (P6 of
    /// docs/plans/hardware-in-the-server.md, #936). Empty, having said why, when there is no node to ask.
    /// </summary>
    Task<IReadOnlyCollection<TDevice>> ListDevicesAsync<TDevice>(DeviceType deviceType, DeviceDiscoveryOption options, CancellationToken cancellationToken)
        where TDevice : DeviceBase;

    /// <summary>Every device this computer's node lists, after its discovery job when asked for or not yet run.</summary>
    Task<IReadOnlyCollection<DeviceBase>> ListAllDevicesAsync(DeviceDiscoveryOption options, CancellationToken cancellationToken);

    /// <summary>This computer's node, found or started on first use; null, having said why, when there is none to reach.</summary>
    Task<TianWenNodeClient?> NodeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Writes <paramref name="profile"/> through the node's one profile writer, creating it when the node has none of that id
    /// (the node names a new profile, so use the one answered), and answers it as stored; null, having said why, when the
    /// node refused it.
    /// </summary>
    Task<Profile?> SaveProfileAsync(Profile profile, CancellationToken cancellationToken);

    /// <summary>Deletes the profile on the node; false, having said why, when the node refused (its active profile, a run).</summary>
    Task<bool> DeleteProfileAsync(Profile profile, CancellationToken cancellationToken);

    IHostApplicationLifetime ApplicationLifetime { get; }

    IExternal External { get; }

    ITimeProvider TimeProvider { get; }

    void WriteScrollable(string content, bool newLine = true);

    string? ReadLine();

    void WriteError(string error);

    void WriteError(Exception exception);
}

[Flags]
public enum DeviceDiscoveryOption
{
    None = 0,
    Force       = 0b0001,
    IncludeFake = 0b0010
}
