using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;
using TianWen.Lib.Connections;
using QHYCCD.SDK;
using static QHYCCD.SDK.QHYCamera;

namespace TianWen.Lib.Devices.QHYCCD;

internal class QHYDeviceSource(IExternal external) : IDeviceSource<QHYDevice>
{
    static readonly Dictionary<DeviceType, bool> _supportedDeviceTypes = [];
    private readonly List<QHYDevice> _serialFilterWheels = [];

    static QHYDeviceSource()
    {
        bool isSupported;
        try
        {
            isSupported = EnsureResourceInitialized() && GetSDKVersion().Major > 0;
        }
        catch
        {
            isSupported = false;
        }

        // QHY SDK only enumerates cameras; filter wheels are discovered via camera CFW port or serial probing
        _supportedDeviceTypes[DeviceType.Camera] = isSupported;
        // Filter wheels are always potentially supported (serial CFW doesn't need the camera SDK)
        _supportedDeviceTypes[DeviceType.FilterWheel] = true;
    }

    public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_supportedDeviceTypes.Values.Any(v => v));

    /// <summary>
    /// Probes serial ports for standalone QHY CFWs (e.g. QHYCFW3) using the VRS command.
    /// </summary>
    public async ValueTask DiscoverAsync(CancellationToken cancellationToken = default)
    {
        _serialFilterWheels.Clear();

        using var resourceLock = await external.WaitForSerialPortEnumerationAsync(cancellationToken);

        foreach (var portName in external.EnumerateAvailableSerialPorts(resourceLock))
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromMilliseconds(500));

                var port = external.OpenSerialDevice(portName, QHYSerialControlledFilterWheelDriver.CFW_BAUD, Encoding.ASCII);
                if (port is not { IsOpen: true })
                {
                    continue;
                }

                var fwVersion = await QHYSerialControlledFilterWheelDriver.ProbeAsync(port, cts.Token);
                if (fwVersion is null)
                {
                    port.TryClose();
                    continue;
                }

                var slotCount = await QHYSerialControlledFilterWheelDriver.QuerySlotCountAsync(port, cts.Token);
                port.TryClose();

                if (slotCount <= 0)
                {
                    continue;
                }

                var portWithoutPrefix = ISerialConnection.RemoveProtoPrrefix(portName);
                var deviceId = $"QHYCFW3_{portWithoutPrefix}";
                var displayName = $"QHYCFW3 {slotCount}-Slot (FW {fwVersion}) on {portWithoutPrefix}";

                var filterParams = SeedFilterParams(slotCount);
                var portParam = $"{DeviceQueryKey.Port.Key}={Uri.EscapeDataString(portName)}";
                var query = filterParams is { Length: > 0 } ? $"{portParam}&{filterParams}" : portParam;
                var uri = new Uri($"{DeviceType.FilterWheel}://{typeof(QHYDevice).Name}/{deviceId}?{query}#{displayName}");

                _serialFilterWheels.Add(new QHYDevice(uri));
            }
            catch (OperationCanceledException)
            {
                // Timeout — not a QHYCFW3 on this port
            }
            catch (Exception)
            {
                // Skip this port
            }
        }
    }

    public IEnumerable<DeviceType> RegisteredDeviceTypes { get; } = _supportedDeviceTypes
        .Where(p => p.Value)
        .Select(p => p.Key)
        .ToList();

    public IEnumerable<QHYDevice> RegisteredDevices(DeviceType deviceType)
    {
        if (_supportedDeviceTypes.TryGetValue(deviceType, out var isSupported) && isSupported)
        {
            return deviceType switch
            {
                DeviceType.Camera => ListCameras(),
                DeviceType.FilterWheel => ListCameraControlledFilterWheels().Concat(_serialFilterWheels),
                _ => throw new ArgumentException($"Device type {deviceType} not implemented!", nameof(deviceType))
            };
        }

        return [];
    }

    static IEnumerable<QHYDevice> ListCameras() => ListDevice(DeviceType.Camera);

    /// <summary>
    /// Discovers camera-cable-connected filter wheels by iterating all QHY cameras
    /// and probing each one for a plugged CFW.
    /// </summary>
    static IEnumerable<QHYDevice> ListCameraControlledFilterWheels()
    {
        var ids = new HashSet<int>();
        var iterator = new DeviceIterator<QHYCCD_CAMERA_INFO>();

        foreach (var deviceInfo in iterator)
        {
            if (!ids.Contains(deviceInfo.ID) && deviceInfo.Open())
            {
                try
                {
                    if (deviceInfo.IsCfwPlugged && deviceInfo.CfwSlotCount > 0)
                    {
                        var deviceId = deviceInfo.SerialNumber is { Length: > 0 } sn ? sn
                            : deviceInfo.IsUSB3Device && deviceInfo.CustomId is { Length: > 0 } cid ? cid
                            : deviceInfo.Name;

                        var slotCount = deviceInfo.CfwSlotCount;
                        var filterParams = SeedFilterParams(slotCount);
                        var queryPart = filterParams is { Length: > 0 } ? $"?{filterParams}" : "";
                        var uri = new Uri($"{DeviceType.FilterWheel}://{typeof(QHYDevice).Name}/{deviceId}{queryPart}#{deviceInfo.Name} {slotCount}-Slot CFW");
                        yield return new QHYDevice(uri);
                    }

                    ids.Add(deviceInfo.ID);
                }
                finally
                {
                    _ = deviceInfo.Close();
                }
            }
        }
    }

    /// <summary>
    /// Builds query params seeding default filter names from the CFW slot count.
    /// </summary>
    private static string? SeedFilterParams(int slotCount)
    {
        if (slotCount <= 0)
        {
            return null;
        }

        var parts = new string[slotCount];
        for (var i = 0; i < slotCount; i++)
        {
            parts[i] = $"{DeviceQueryKeyExtensions.FilterKey(i + 1)}={Uri.EscapeDataString($"Filter {i + 1}")}";
        }
        return string.Join("&", parts);
    }

    static IEnumerable<QHYDevice> ListDevice(DeviceType deviceType)
    {
        var ids = new HashSet<int>();
        var iterator = new DeviceIterator<QHYCCD_CAMERA_INFO>();

        foreach (var deviceInfo in iterator)
        {
            if (!ids.Contains(deviceInfo.ID) && deviceInfo.Open())
            {
                try
                {
                    var deviceId = deviceInfo.SerialNumber is { Length: > 0 } sn ? sn
                        : deviceInfo.IsUSB3Device && deviceInfo.CustomId is { Length: > 0 } cid ? cid
                        : deviceInfo.Name;

                    var uri = new Uri($"{deviceType}://{typeof(QHYDevice).Name}/{deviceId}#{deviceInfo.Name}");
                    yield return new QHYDevice(uri);

                    ids.Add(deviceInfo.ID);
                }
                finally
                {
                    _ = deviceInfo.Close();
                }
            }
        }
    }
}
