using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

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

    /// <summary>
    /// Maps <see cref="DeviceType"/> to the ASCOM registry key name suffix (e.g. "Camera Drivers").
    /// </summary>
    private static string AscomRegistryKeyName(DeviceType deviceType) => deviceType switch
    {
        DeviceType.Camera => "Camera",
        DeviceType.CoverCalibrator => "CoverCalibrator",
        DeviceType.FilterWheel => "FilterWheel",
        DeviceType.Focuser => "Focuser",
        DeviceType.Switch => "Switch",
        DeviceType.Telescope => "Telescope",
        _ => throw new ArgumentOutOfRangeException(nameof(deviceType), deviceType, null)
    };

    private static readonly DeviceType[] _allSupportedDeviceTypes = [DeviceType.Camera, DeviceType.FilterWheel, DeviceType.Focuser, DeviceType.Telescope];

    private Dictionary<DeviceType, List<AscomDevice>> _devices = [];

    public IEnumerable<DeviceType> RegisteredDeviceTypes => IsSupported ? _allSupportedDeviceTypes : [];

    [SupportedOSPlatform("windows")]
    private static List<AscomDevice> GetDriversFromRegistry(DeviceType deviceType)
    {
        var devices = new List<AscomDevice>();
        var keyName = AscomRegistryKeyName(deviceType);

        using var hklm32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        using var driversKey = hklm32.OpenSubKey($@"SOFTWARE\ASCOM\{keyName} Drivers", false);

        if (driversKey is null)
        {
            return devices;
        }

        foreach (var progId in driversKey.GetSubKeyNames())
        {
            using var progIdKey = driversKey.OpenSubKey(progId, false);
            var displayName = progIdKey?.GetValue(null) as string ?? progId;
            devices.Add(new AscomDevice(deviceType, progId, displayName));
        }

        return devices;
    }

    public async ValueTask DiscoverAsync(CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows() && await CheckSupportAsync(cancellationToken))
        {
            var devices = new Dictionary<DeviceType, List<AscomDevice>>();
            foreach (var deviceType in _allSupportedDeviceTypes)
            {
                devices[deviceType] = GetDriversFromRegistry(deviceType);
            }

            Interlocked.Exchange(ref _devices, devices);
        }
    }

    public IEnumerable<AscomDevice> RegisteredDevices(DeviceType deviceType) => _devices.TryGetValue(deviceType, out var devices) ? devices : [];
}
