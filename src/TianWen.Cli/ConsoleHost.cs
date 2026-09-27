using Console.Lib;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pastel;
using System.Collections.Concurrent;
using TianWen.Lib.Devices;
using TianWen.RemoteClient;
using TianWen.UI.Abstractions;

namespace TianWen.Cli;

/// <summary>
/// The CLI's host. The rig is this computer's node's (P6 of docs/plans/hardware-in-the-server.md, #936), so every verb that
/// touches a device or a profile is the node's client: the devices are the node's listing and its discovery its job, and a
/// profile is read from and written through the node's one profile writer. The node is found or started on the first verb
/// that needs it, never for one that does not (a stack, a solve, the viewer).
/// </summary>
internal sealed class ConsoleHost(
    IExternal external,
    IHostApplicationLifetime applicationLifetime,
    IVirtualTerminal terminal,
    ITimeProvider timeProvider,
    ILogger<ConsoleHost> logger,
    LocalNodeOptions? nodeOptions = null,
    TextWriter? output = null,
    TextWriter? error = null
) : IConsoleHost, IDisposable
{
    private readonly SemaphoreSlim _connecting = new SemaphoreSlim(1, 1);
    private HttpClient? _http;
    private TianWenNodeClient? _node;
    private string? _unreachable;
    private int _discovered;

    // Each profile as it was last read from the node, and the revision it was read at, which a write names.
    private readonly ConcurrentDictionary<Guid, (ProfileData Data, string Revision)> _read = new ConcurrentDictionary<Guid, (ProfileData, string)>();

    public IVirtualTerminal Terminal { get; } = terminal;

    public IHostApplicationLifetime ApplicationLifetime { get; } = applicationLifetime;

    public IExternal External { get; } = external;

    public ITimeProvider TimeProvider { get; } = timeProvider;

    public void WriteScrollable(string content, bool newLine = true)
    {
        var to = output ?? System.Console.Out;
        if (newLine)
        {
            to.WriteLine(content);
        }
        else
        {
            to.Write(content);
        }
    }

    public string? ReadLine() => System.Console.ReadLine();

    public void WriteError(string message)
    {
        (error ?? System.Console.Error).WriteLine(message);
    }

    public void WriteError(Exception exception)
    {
        (error ?? System.Console.Error).WriteLine(exception.Message.Pastel(ConsoleColor.Red));
    }

    public async Task<TianWenNodeClient?> NodeAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _node) is { } node)
        {
            return node;
        }

        await _connecting.WaitAsync(cancellationToken);
        try
        {
            if (_node is null && _unreachable is null)
            {
                var found = await new LocalNodeLauncher(nodeOptions ?? new LocalNodeOptions(), logger).FindOrStartAsync(cancellationToken);
                if (found is { Transport: { } transport })
                {
                    logger.LogInformation("This computer's node ({Outcome}): {Message}", found.Outcome, found.Message);
                    _http = transport.CreateHttpClient();
                    Volatile.Write(ref _node, new TianWenNodeClient(_http));
                }
                else
                {
                    _unreachable = found.Message;
                }
            }
        }
        finally
        {
            _connecting.Release();
        }

        if (_node is null)
        {
            WriteError($"No node holds this computer's rig: {_unreachable}");
        }
        return _node;
    }

    public async Task<IReadOnlyCollection<DeviceBase>> ListAllDevicesAsync(DeviceDiscoveryOption options, CancellationToken cancellationToken)
    {
        if (await NodeAsync(cancellationToken) is not { } node)
        {
            return [];
        }

        // Discovery is the node's job: run it when asked, and before the first listing of this process's life.
        if (options.HasFlag(DeviceDiscoveryOption.Force) || Interlocked.Exchange(ref _discovered, 1) == 0)
        {
            var started = await node.StartDiscoveryAsync(cancellationToken);
            if (started.Value is not { } job)
            {
                WriteError($"The node did not discover devices: {started.Error}");
            }
            else if ((await node.UntilEndedAsync(job, TimeProvider, cancellationToken)).Value is { State: not Hosting.Dto.JobState.Succeeded } ended)
            {
                WriteError($"Discovery on the node did not finish: {ended.Error ?? ended.State.ToString()}");
            }
        }

        var listed = await node.GetDevicesAsync(cancellationToken);
        if (listed.Value is not { } devices)
        {
            WriteError($"The node did not list its devices: {listed.Error}");
            return [];
        }
        return [.. EquipmentActions.ForTheDeviceList([.. devices.Select(static d => new NodeDevice(d))],
            options.HasFlag(DeviceDiscoveryOption.IncludeFake))];
    }

    public async Task<IReadOnlyCollection<TDevice>> ListDevicesAsync<TDevice>(DeviceType deviceType, DeviceDiscoveryOption options, CancellationToken cancellationToken)
        where TDevice : DeviceBase
    {
        if (deviceType is not DeviceType.Profile)
        {
            return [.. (await ListAllDevicesAsync(options, cancellationToken)).Where(d => d.DeviceType == deviceType).OfType<TDevice>()];
        }
        return [.. (await ListProfilesAsync(cancellationToken)).OfType<TDevice>()];
    }

    // Every profile the node stores, each read whole with the revision a write of it names.
    private async Task<IReadOnlyCollection<Profile>> ListProfilesAsync(CancellationToken cancellationToken)
    {
        if (await NodeAsync(cancellationToken) is not { } node)
        {
            return [];
        }

        var summaries = await node.GetProfilesAsync(cancellationToken);
        if (summaries.Value is not { } listed)
        {
            WriteError($"The node did not list its profiles: {summaries.Error}");
            return [];
        }

        var profiles = new List<Profile>(listed.Length);
        foreach (var summary in listed)
        {
            if ((await node.GetProfileAsync(summary.ProfileId, cancellationToken)).Value is { } detail)
            {
                profiles.Add(Adopt(detail));
            }
        }
        return [.. profiles.OrderBy(static p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)];
    }

    public async Task<Profile?> SaveProfileAsync(Profile profile, CancellationToken cancellationToken)
    {
        if (await NodeAsync(cancellationToken) is not { } node)
        {
            return null;
        }

        var data = profile.Data ?? ProfileData.Empty;
        if (!_read.TryGetValue(profile.ProfileId, out var read))
        {
            var existing = await node.GetProfileAsync(profile.ProfileId, cancellationToken);
            if (existing.IsNotFound)
            {
                // A new profile: the node makes it (and names it), then takes its equipment.
                var created = await node.CreateProfileAsync(profile.DisplayName, cancellationToken);
                if (created.Value is not { } made)
                {
                    WriteError($"The node did not create the profile: {created.Error}");
                    return null;
                }
                var stored = Adopt(made);
                if (data == ProfileData.Empty || !_read.TryGetValue(stored.ProfileId, out read))
                {
                    return stored;
                }
                profile = stored.WithData(data);
            }
            else if (existing.Value is not { } detail || !_read.TryGetValue(Adopt(detail).ProfileId, out read))
            {
                // Not read by this process yet, and the node would not say at which revision it is.
                WriteError($"The node did not read the profile: {existing.Error}");
                return null;
            }
        }

        var written = await NodeProfileWrites.WriteAsync(node, profile.ProfileId, read.Data, data, read.Revision,
            name: null, logger, cancellationToken);
        if (written.Value is not { } saved)
        {
            WriteError($"Could not save the profile: {written.Error}");
            return null;
        }
        return Adopt(saved);
    }

    public async Task<bool> DeleteProfileAsync(Profile profile, CancellationToken cancellationToken)
    {
        if (await NodeAsync(cancellationToken) is not { } node)
        {
            return false;
        }

        var deleted = await node.DeleteProfileAsync(profile.ProfileId, cancellationToken);
        if (!deleted.IsSuccess)
        {
            WriteError($"The node did not delete the profile: {deleted.Error}");
            return false;
        }
        _read.TryRemove(profile.ProfileId, out _);
        return true;
    }

    // A profile as the node sent it, remembered with the revision a write of it names.
    private Profile Adopt(Hosting.Dto.ProfileDetailDto detail)
    {
        var data = detail.Data ?? ProfileData.Empty;
        if (detail.Revision is { } revision)
        {
            _read[detail.ProfileId] = (data, revision);
        }
        return new Profile(detail.ProfileId, detail.Name, data);
    }

    public void Dispose()
    {
        _http?.Dispose();
        _connecting.Dispose();
    }
}
