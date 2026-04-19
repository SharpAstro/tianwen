using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;
using TianWen.Lib.Devices.Discovery;
using QHYCCD.SDK;
using static QHYCCD.SDK.QHYCamera;

namespace TianWen.Lib.Devices.QHYCCD;

internal class QHYDeviceSource(ISerialProbeService probeService) : IDeviceSource<QHYDevice>
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
    /// Three-phase discovery:
    /// <list type="number">
    ///   <item>Enumerate cameras via the QHY SDK (lightweight — no open/close).</item>
    ///   <item>Consume standalone CFW / QFOC matches from <see cref="ISerialProbeService"/> —
    ///         the actual serial probing runs in <see cref="QhyCfw3SerialProbe"/> and
    ///         <see cref="QfocSerialProbe"/> before <c>DiscoverAsync</c> is called.</item>
    ///   <item>Open each camera to check for a plugged camera-cable CFW.</item>
    /// </list>
    /// </summary>
    public ValueTask DiscoverAsync(CancellationToken cancellationToken = default)
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

        // Phase 2: read standalone CFW / QFOC matches published by the central probe service.
        foreach (var match in probeService.ResultsFor("QHYCFW3"))
        {
            _serialFilterWheels.Add(new QHYDevice(match.DeviceUri));
        }
        foreach (var match in probeService.ResultsFor("QFOC"))
        {
            _serialFocusers.Add(new QHYDevice(match.DeviceUri));
        }

        // Phase 3: open each camera to check for camera-cable CFWs
        if (cameraSupported)
        {
            DiscoverCameraControlledFilterWheels();
        }

        return ValueTask.CompletedTask;
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
    /// Shared with <see cref="QhyCfw3SerialProbe"/>.
    /// </summary>
    internal static string? SeedFilterParams(int slotCount)
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
