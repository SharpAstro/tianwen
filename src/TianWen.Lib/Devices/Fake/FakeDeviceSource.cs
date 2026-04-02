using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices.Fake;

internal class FakeDeviceSource : IDeviceSource<FakeDevice>
{
    public IEnumerable<DeviceType> RegisteredDeviceTypes => [DeviceType.Mount, DeviceType.Camera, DeviceType.Focuser, DeviceType.FilterWheel, DeviceType.Guider];

    // Fake mounts with different protocol stacks
    private static readonly (string Port, string DisplaySuffix)[] _mountProtocols =
    [
        ("", ""),                    // default (direct driver)
        ("LX200", " (LX200)"),      // Meade LX200 serial protocol
        ("SGP", " (SGP)"),          // iOptron SkyGuider Pro serial protocol
        ("SkyWatcher", " (SkyWatcher)"), // Skywatcher motor controller protocol
    ];

    public IEnumerable<FakeDevice> RegisteredDevices(DeviceType deviceType)
    {
        var count = deviceType switch
        {
            DeviceType.Guider => 1,
            DeviceType.Camera => 9,
            DeviceType.Focuser => 3,
            _ => 2
        };

        if (deviceType is DeviceType.Mount)
        {
            foreach (var (port, suffix) in _mountProtocols)
            {
                var values = new NameValueCollection
                {
                    { "latitude", "48.2" },
                    { "longitude", "16.3" },
                };
                if (port.Length > 0)
                {
                    values["port"] = port;
                }
                var id = port.Length > 0 ? $"FakeMount_{port}" : "FakeMount1";
                yield return new FakeDevice(new Uri(
                    $"Mount://{nameof(FakeDevice)}/{id}?{values.ToQueryString()}#Fake Mount{suffix}"));
            }
        }
        else
        {
            for (var i = 1; i <= count; i++)
            {
                if (deviceType is DeviceType.Camera)
                {
                    // Include sensor name in camera display name
                    var sensor = FakeCameraDriver.GetPresetForId(i).SensorName;
                    yield return new FakeDevice(new Uri($"Camera://{nameof(FakeDevice)}/FakeCamera{i}#Fake Camera {i} ({sensor})"));
                }
                else
                {
                    yield return new FakeDevice(deviceType, i);
                }
            }
        }

        // Extra guide camera (small mono sensor, separate from imaging cameras)
        if (deviceType is DeviceType.Camera)
        {
            yield return new FakeDevice(new Uri($"Camera://{nameof(FakeDevice)}/FakeGuideCam#Fake Guide Cam ({FakeCameraDriver.GuideCameraPreset.SensorName})"));
        }
    }

    public ValueTask DiscoverAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
}
