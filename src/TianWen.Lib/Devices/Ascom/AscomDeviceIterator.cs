using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices.Ascom.ComInterop;

namespace TianWen.Lib.Devices.Ascom;

internal partial class AscomDeviceIterator(ILogger<AscomDeviceIterator> logger) : IDeviceSource<AscomDevice>
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
            // Platform <= 6.x writes "PlatformVersion"; Platform 7.x renamed it to "Platform Version" (with a space).
            && ((platformKey.GetValue("PlatformVersion") ?? platformKey.GetValue("Platform Version")) is string versionString and { Length: > 0 })
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
    private List<AscomDevice> GetDriversFromRegistry(DeviceType deviceType)
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

            if (!IsComServerRegistered(progId, out var reason))
            {
                logger.LogDebug("Skipping stale ASCOM {Type} registration {ProgId} ({DisplayName}): {Reason}",
                    deviceType, progId, displayName, reason);
                continue;
            }

            devices.Add(new AscomDevice(deviceType, progId, displayName));
        }

        return devices;
    }

    /// <summary>
    /// Verifies the ProgID resolves to a CLSID, that the CLSID has an
    /// <c>InprocServer32</c> or <c>LocalServer32</c> subkey, and that the backing
    /// DLL/EXE actually exists on disk. Filters orphaned ASCOM registry entries
    /// left behind by uninstalled drivers.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static bool IsComServerRegistered(string progId, out string reason)
    {
        var hr = NativeMethods.CLSIDFromProgID(progId, out var clsid);
        if (hr != 0)
        {
            reason = $"CLSIDFromProgID failed with HRESULT 0x{hr:X8}";
            return false;
        }

        var clsidString = $"{{{clsid.ToString().ToUpperInvariant()}}}";

        // ASCOM Platform 6 drivers live under Wow6432Node (32-bit view); newer
        // .NET 6+ drivers (Platform 7) may register 64-bit-only. Check both.
        var clsidFound = false;
        string? lastRejection = null;
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var clsidKey = hklm.OpenSubKey($@"SOFTWARE\Classes\CLSID\{clsidString}", false);
            if (clsidKey is null) continue;
            clsidFound = true;

            // LocalServer32 is out-of-proc so bitness doesn't matter. InprocServer32
            // must match our process bitness — a 32-bit DLL can't load into a 64-bit
            // process (CoCreateInstance returns REGDB_E_CLASSNOTREG = "Class not registered").
            if (TryUsableServer(clsidKey, "LocalServer32", requireBitnessMatch: false, out _)
                || TryUsableServer(clsidKey, "InprocServer32", requireBitnessMatch: true, out lastRejection))
            {
                reason = string.Empty;
                return true;
            }
        }

        reason = !clsidFound
            ? $"CLSID {clsidString} not registered in HKLM\\SOFTWARE\\Classes\\CLSID (32/64-bit view)"
            : lastRejection ?? $"CLSID {clsidString} has no usable InprocServer32/LocalServer32";
        return false;
    }

    // Strip everything to the right of the rightmost `.exe` or `.dll` — handles quoted paths,
    // unquoted paths containing spaces, trailing `/Embedding` or similar args, uniformly.
    [GeneratedRegex(@"^.*\.(?:exe|dll)", RegexOptions.IgnoreCase)]
    private static partial Regex ExecutablePathRegex();

    [SupportedOSPlatform("windows")]
    private static bool TryUsableServer(RegistryKey clsidKey, string serverSubKey, bool requireBitnessMatch, out string? rejection)
    {
        rejection = null;
        using var serverKey = clsidKey.OpenSubKey(serverSubKey, false);
        if (serverKey?.GetValue(null) is not string raw || string.IsNullOrWhiteSpace(raw)) return false;

        var match = ExecutablePathRegex().Match(raw);
        if (!match.Success)
        {
            rejection = $"{serverSubKey} value '{raw}' does not contain a .exe/.dll path";
            return false;
        }

        var filePath = Environment.ExpandEnvironmentVariables(match.Value.Trim().Trim('"'));

        // .NET inproc drivers register InprocServer32 as "mscoree.dll" — resolve against System32.
        if (!Path.IsPathRooted(filePath))
        {
            filePath = Path.Combine(Environment.SystemDirectory, filePath);
        }

        if (!File.Exists(filePath))
        {
            rejection = $"{serverSubKey} file '{filePath}' does not exist";
            return false;
        }

        if (requireBitnessMatch && !IsPeBitnessCompatible(filePath))
        {
            rejection = $"{serverSubKey} '{filePath}' is 32-bit native and cannot load in this 64-bit process (install a Platform 7 replacement or wrap via ASCOM Device Hub)";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Reads the PE machine field and rejects 32-bit DLLs when we're a 64-bit process
    /// (they can't be loaded in-proc across bitness). .NET assemblies compiled AnyCPU
    /// report as x86 in the PE header but load everywhere — detect and trust them.
    /// </summary>
    private static bool IsPeBitnessCompatible(string filePath)
    {
        const ushort IMAGE_FILE_MACHINE_I386 = 0x014c;
        const ushort PE_HEADER_OFFSET = 0x3C;

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs);
            fs.Seek(PE_HEADER_OFFSET, SeekOrigin.Begin);
            var peOffset = br.ReadInt32();
            fs.Seek(peOffset, SeekOrigin.Begin);
            if (br.ReadUInt32() != 0x00004550u) return true; // "PE\0\0" — not a PE, don't reject
            var machine = br.ReadUInt16();

            // Only reject x86 DLLs from a 64-bit process; everything else loads fine.
            if (machine == IMAGE_FILE_MACHINE_I386 && Environment.Is64BitProcess)
            {
                // .NET AnyCPU assemblies have machine=x86 but the CLR loads them in either
                // bitness. Detect via the CLR header data directory at OptionalHeader+208
                // (PE32) / +224 (PE32+). If the CLR header is present, trust it.
                return HasClrHeader(fs, br, peOffset);
            }
            return true;
        }
        catch
        {
            return true; // on read error, trust the registration
        }
    }

    private static bool HasClrHeader(FileStream fs, BinaryReader br, int peOffset)
    {
        // COFF header is 20 bytes at peOffset+4. OptionalHeader follows.
        // PE32 magic = 0x10B; CLR header directory entry is at optional+208 (RVA, size).
        fs.Seek(peOffset + 4 + 20, SeekOrigin.Begin);
        var magic = br.ReadUInt16();
        var clrDirOffset = magic == 0x10B ? 208 : 224;
        fs.Seek(peOffset + 4 + 20 + clrDirOffset, SeekOrigin.Begin);
        var rva = br.ReadUInt32();
        var size = br.ReadUInt32();
        return rva != 0 && size != 0;
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
