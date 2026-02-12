using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using AscomDeviceType = ASCOM.Common.DeviceTypes;
using AscomProfile = ASCOM.Com.Profile;

namespace TianWen.Lib.Devices.Ascom;

internal class AscomDeviceIterator : IDeviceSource<AscomDevice>
{
    private static readonly bool IsSupported;

    static AscomDeviceIterator()
    {
        IsSupported = OperatingSystem.IsWindows() && CheckMininumAscomPlatformVersion(new Version(6, 5, 0, 0));
    }

    [SupportedOSPlatform("windows")]
    internal static bool CheckMininumAscomPlatformVersion(Version minVersion)
    {
        using var hklm32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);

        if (hklm32.OpenSubKey(string.Join('\\', "SOFTWARE", "ASCOM", "Platform"), false) is { } platformKey
            && platformKey.GetValue("PlatformVersion") is string versionString and { Length: > 0 }
            && Version.TryParse(versionString, out var version)
        )
        {
            return version >= minVersion;
        }
        return false;
    }

    /// <summary>
    /// Returns true if COM object was initalised successfully.
    /// </summary>
    public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(IsSupported);

    private static readonly HashSet<AscomDeviceType> _allSupportedDeviceTypes = [AscomDeviceType.Camera, AscomDeviceType.FilterWheel, AscomDeviceType.Focuser, AscomDeviceType.Telescope];

    private Dictionary<DeviceType, List<AscomDevice>> _devices = [];

    public IEnumerable<DeviceType> RegisteredDeviceTypes => IsSupported ? _allSupportedDeviceTypes.Select(d => d.ToDeviceType()) : [];

    public async ValueTask DiscoverAsync(CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows() && await CheckSupportAsync(cancellationToken))
        {
            var devices = new Dictionary<DeviceType, List<AscomDevice>>();
            foreach (var ascomDeviceType in _allSupportedDeviceTypes)
            {
                var drivers = AscomProfile.GetDrivers(ascomDeviceType);
                var deviceType = ascomDeviceType.ToDeviceType();
                var devicesOfType = new List<AscomDevice>();
                foreach (var driver in drivers)
                {
                    devicesOfType.Add(new AscomDevice(deviceType, driver.ProgID, driver.Name));
                }

                devices[deviceType] = devicesOfType;
            }

            Interlocked.Exchange(ref _devices, devices);
        }
    }

    public IEnumerable<AscomDevice> RegisteredDevices(DeviceType deviceType) => _devices[deviceType];
}
