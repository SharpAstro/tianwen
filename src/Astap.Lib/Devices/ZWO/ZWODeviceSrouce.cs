using System;
using System.Collections.Generic;
using System.Linq;
using static ZWOptical.ASISDK.ASICameraDll2;
using static ZWOptical.ASISDK.ASICameraDll2.ASI_BOOL;
using static ZWOptical.ASISDK.ASICameraDll2.ASI_ERROR_CODE;

namespace Astap.Lib.Devices.ZWO;

public class ZWODeviceSrouce : IDeviceSource<ZWODevice>
{
    static readonly Dictionary<string, bool> _supportedDeviceTypes = new();

    static ZWODeviceSrouce()
    {
        bool supportsCamera;
        try
        {
            supportsCamera = !string.IsNullOrWhiteSpace(ASIGetSDKVersion());
        }
        catch
        {
            supportsCamera = false;
        }

        _supportedDeviceTypes[DeviceBase.Camera] = supportsCamera;
    }

    public bool IsSupported => _supportedDeviceTypes.Count > 0;

    public IEnumerable<string> RegisteredDeviceTypes { get; } = _supportedDeviceTypes.Where(p => p.Value).Select(p => p.Key).ToList();

    public IEnumerable<ZWODevice> RegisteredDevices(string deviceType)
    {
        if (_supportedDeviceTypes.TryGetValue(deviceType, out var isSupported) && isSupported)
        {
            return deviceType switch
            {
                DeviceBase.Camera => ListCameras(),
                _ => throw new ArgumentException($"Device type {deviceType} not implemented!", nameof(deviceType))
            };
        }
        else
        {
            return Enumerable.Empty<ZWODevice>();
        }
    }

    static IEnumerable<ZWODevice> ListCameras()
    {
        var camIds = new HashSet<int>();

        var count = ASIGetNumOfConnectedCameras();
        for (var i = 0; i < count; i++)
        {
            if (ASIGetCameraProperty(out var camInfo, i) is ASI_SUCCESS
                && !camIds.Contains(camInfo.CameraID)
                && ASIOpenCamera(camInfo.CameraID) is ASI_SUCCESS
            )
            {
                try
                {
                    if (ASIGetSerialNumber(camInfo.CameraID, out var camSerial) is ASI_SUCCESS)
                    {
                        yield return new ZWODevice(DeviceBase.Camera, camSerial.ID, camInfo.Name);
                    }
                    else if (camInfo.IsUSB3Camera is ASI_TRUE && ASIGetID(camInfo.CameraID, out var camId) is ASI_SUCCESS)
                    {
                        yield return new ZWODevice(DeviceBase.Camera, camId.ID, camInfo.Name);
                    }
                    else
                    {
                        yield return new ZWODevice(DeviceBase.Camera, camInfo.Name, camInfo.Name);
                    }

                    camIds.Add(camInfo.CameraID);
                }
                finally
                {
                    _ = ASICloseCamera(camInfo.CameraID);
                }
            }
        }
    }
}
