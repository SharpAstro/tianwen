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
    private readonly List<QHYDevice> _cameras = [];
    private readonly List<QHYDevice> _cameraControlledFilterWheels = [];
    private readonly List<QHYDevice> _serialFilterWheels = [];
    private readonly List<QHYDevice> _serialFocusers = [];

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
        // QFOC focusers are always potentially supported (serial, independent of camera SDK)
        _supportedDeviceTypes[DeviceType.Focuser] = true;
    }

    public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_supportedDeviceTypes.Values.Any(v => v));

    /// <summary>
    /// Discovers all QHY devices in three phases:
    /// <list type="number">
    ///   <item>Enumerate cameras via QHY SDK (lightweight — no open/close)</item>
    ///   <item>Probe serial ports for standalone CFWs (QHYCFW3) and QFOC focusers</item>
    ///   <item>Open each camera briefly to check for a plugged camera-cable CFW</item>
    /// </list>
    /// This order lets serial probing run while the SDK iterator is not holding camera handles,
    /// and defers the heavier camera-open-for-CFW check to the end.
    /// </summary>
    public async ValueTask DiscoverAsync(CancellationToken cancellationToken = default)
    {
        _cameras.Clear();
        _cameraControlledFilterWheels.Clear();
        _serialFilterWheels.Clear();
        _serialFocusers.Clear();

        // Phase 1: enumerate cameras via SDK (no open, just iterate)
        if (_supportedDeviceTypes.TryGetValue(DeviceType.Camera, out var cameraSupported) && cameraSupported)
        {
            EnumerateCameras();
        }

        // Phase 2: probe serial ports for standalone CFWs and QFOC focusers
        using var resourceLock = await external.WaitForSerialPortEnumerationAsync(cancellationToken);

        foreach (var portName in external.EnumerateAvailableSerialPorts(resourceLock))
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromMilliseconds(500));

                var port = await external.OpenSerialDeviceAsync(portName, QHYSerialControlledFilterWheelDriver.CFW_BAUD, Encoding.ASCII, cts.Token);
                if (port is not { IsOpen: true })
                {
                    continue;
                }

                var fwVersion = await QHYSerialControlledFilterWheelDriver.ProbeAsync(port, cts.Token);
                if (fwVersion is null)
                {
                    port.TryClose();

                    // Not a CFW — try probing for a QFOC focuser on this port
                    await ProbeForQfocAsync(portName, cancellationToken);
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

        // Phase 3: open each camera to check for camera-cable CFWs
        if (cameraSupported)
        {
            DiscoverCameraControlledFilterWheels();
        }
    }

    /// <summary>
    /// Phase 1: lightweight camera enumeration via QHY SDK.
    /// Opens each camera briefly only to read its identity (serial number, name).
    /// </summary>
    private void EnumerateCameras()
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

                    var cameraUri = new Uri($"{DeviceType.Camera}://{typeof(QHYDevice).Name}/{deviceId}#{deviceInfo.Name}");
                    _cameras.Add(new QHYDevice(cameraUri));

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
    /// Phase 3: open each camera to check for a plugged camera-cable CFW.
    /// Runs after serial probing so that camera handles are not held during port scanning.
    /// </summary>
    private void DiscoverCameraControlledFilterWheels()
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
                        var cfwUri = new Uri($"{DeviceType.FilterWheel}://{typeof(QHYDevice).Name}/{deviceId}{queryPart}#{deviceInfo.Name} {slotCount}-Slot CFW");
                        _cameraControlledFilterWheels.Add(new QHYDevice(cfwUri));
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
    /// Probes a serial port for a QFOC focuser (Standard or High Precision) using the JSON init command.
    /// </summary>
    private async ValueTask ProbeForQfocAsync(string portName, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMilliseconds(1500));

            var port = await external.OpenSerialDeviceAsync(portName, QHYFocuserDriver.QFOC_BAUD, Encoding.ASCII, cts.Token);
            if (port is not { IsOpen: true })
            {
                return;
            }

            var probeResult = await QHYFocuserDriver.ProbeAsync(port, cts.Token);
            port.TryClose();

            if (probeResult is not var (firmwareVersion, boardVersion))
            {
                return;
            }

            var portWithoutPrefix = ISerialConnection.RemoveProtoPrrefix(portName);
            var deviceId = $"QFOC_{portWithoutPrefix}";
            var displayName = $"QFOC (FW {firmwareVersion}, Board {boardVersion}) on {portWithoutPrefix}";

            var portParam = $"{DeviceQueryKey.Port.Key}={Uri.EscapeDataString(portName)}";
            var uri = new Uri($"{DeviceType.Focuser}://{typeof(QHYDevice).Name}/{deviceId}?{portParam}#{displayName}");

            _serialFocusers.Add(new QHYDevice(uri));
        }
        catch (OperationCanceledException)
        {
            // Timeout — not a QFOC on this port
        }
        catch (Exception)
        {
            // Skip this port
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
                DeviceType.Camera => _cameras,
                DeviceType.FilterWheel => _cameraControlledFilterWheels.Concat(_serialFilterWheels),
                DeviceType.Focuser => _serialFocusers,
                _ => throw new ArgumentException($"Device type {deviceType} not implemented!", nameof(deviceType))
            };
        }

        return [];
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

}
