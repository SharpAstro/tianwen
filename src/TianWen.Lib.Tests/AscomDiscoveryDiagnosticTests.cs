using Microsoft.Win32;
using Shouldly;
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Ascom;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Diagnostic, machine-dependent tests that probe ASCOM discovery step by step on the
/// developer's actual box. They Skip on non-Windows and Skip when the ASCOM Platform is not
/// installed. The point is to surface *why* discovery is empty by dumping the raw registry
/// state that <see cref="AscomDeviceIterator"/> looks at — Registry32 vs Registry64 vs Default —
/// so we can tell whether the issue is missing keys, a wrong registry view, or version gating.
/// </summary>
[SupportedOSPlatform("Windows")]
public class AscomDiscoveryDiagnosticTests(ITestOutputHelper output)
{
    private const string AscomPlatformSubKey = @"SOFTWARE\ASCOM\Platform";

    private static readonly DeviceType[] _ascomDeviceTypes =
    [
        DeviceType.Camera,
        DeviceType.CoverCalibrator,
        DeviceType.FilterWheel,
        DeviceType.Focuser,
        DeviceType.Switch,
        DeviceType.Telescope,
    ];

    [Fact]
    public void DumpProcessAndOsBitness()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ASCOM only on Windows.");

        output.WriteLine($"OS                : {RuntimeInformation.OSDescription}");
        output.WriteLine($"OSArchitecture    : {RuntimeInformation.OSArchitecture}");
        output.WriteLine($"ProcessArchitecture: {RuntimeInformation.ProcessArchitecture}");
        output.WriteLine($"Is64BitOS         : {Environment.Is64BitOperatingSystem}");
        output.WriteLine($"Is64BitProcess    : {Environment.Is64BitProcess}");
        output.WriteLine($"FrameworkDescription: {RuntimeInformation.FrameworkDescription}");
    }

    [Theory]
    [InlineData(RegistryView.Registry32)]
    [InlineData(RegistryView.Registry64)]
    [InlineData(RegistryView.Default)]
    public void DumpAscomPlatformVersionForAllRegistryViews(RegistryView view)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ASCOM only on Windows.");

        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using var platformKey = hklm.OpenSubKey(AscomPlatformSubKey, writable: false);

        if (platformKey is null)
        {
            output.WriteLine($"[{view}] HKLM\\{AscomPlatformSubKey} : <missing>");
            return;
        }

        output.WriteLine($"[{view}] HKLM\\{AscomPlatformSubKey} : present");
        foreach (var name in platformKey.GetValueNames())
        {
            output.WriteLine($"    {name,-24} = {platformKey.GetValue(name)}");
        }
    }

    [Theory]
    [InlineData(RegistryView.Registry32)]
    [InlineData(RegistryView.Registry64)]
    public void DumpAscomDriverSubkeysForAllRegistryViews(RegistryView view)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ASCOM only on Windows.");

        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);

        var totalDrivers = 0;
        foreach (var deviceType in _ascomDeviceTypes)
        {
            var subKeyName = $@"SOFTWARE\ASCOM\{deviceType} Drivers";
            using var driversKey = hklm.OpenSubKey(subKeyName, writable: false);
            if (driversKey is null)
            {
                output.WriteLine($"[{view}] {subKeyName} : <missing>");
                continue;
            }

            var progIds = driversKey.GetSubKeyNames();
            totalDrivers += progIds.Length;
            output.WriteLine($"[{view}] {subKeyName} : {progIds.Length} driver(s)");
            foreach (var progId in progIds)
            {
                using var progIdKey = driversKey.OpenSubKey(progId, writable: false);
                var displayName = progIdKey?.GetValue(null) as string ?? "<no default value>";
                output.WriteLine($"    {progId} -> {displayName}");
            }
        }

        output.WriteLine($"[{view}] TOTAL drivers across all device types: {totalDrivers}");
    }

    /// <summary>
    /// Mirrors <see cref="AscomDeviceIterator.CheckMininumAscomPlatformVersion"/> exactly.
    /// If this skips on a machine that has ASCOM installed, the iterator's Registry32-only
    /// gating is the bug — Platform 7 (or x64-only installs) writes to the native key, not
    /// the WoW6432Node redirect.
    /// </summary>
    [Fact]
    public void CheckMinimumAscomPlatformVersionMatchesIteratorContract()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ASCOM only on Windows.");

        var detected = AscomDeviceIterator.CheckMininumAscomPlatformVersion(new Version(6, 5, 0, 0));
        output.WriteLine($"AscomDeviceIterator.CheckMininumAscomPlatformVersion(6.5) = {detected}");

        // Cross-check: any registry view + any of the known value-name spellings.
        // Platform <= 6.x uses "PlatformVersion"; Platform 7.x uses "Platform Version" (with a space).
        var candidateValueNames = new[] { "PlatformVersion", "Platform Version" };
        Version? bestVersion = null;
        RegistryView? sourceView = null;
        string? sourceValueName = null;
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64, RegistryView.Default })
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var platformKey = hklm.OpenSubKey(AscomPlatformSubKey, writable: false);
            if (platformKey is null) continue;

            foreach (var valueName in candidateValueNames)
            {
                if (platformKey.GetValue(valueName) is string s
                    && Version.TryParse(s, out var v)
                    && (bestVersion is null || v > bestVersion))
                {
                    bestVersion = v;
                    sourceView = view;
                    sourceValueName = valueName;
                }
            }
        }

        if (bestVersion is null)
        {
            Assert.Skip("ASCOM Platform not installed (no version value in any registry view) — nothing to diagnose.");
        }

        output.WriteLine($"Best Platform Version found across views: {bestVersion} (in {sourceView}, value name '{sourceValueName}')");

        if (!detected && bestVersion >= new Version(6, 5, 0, 0))
        {
            Assert.Fail(
                $"BUG REPRO: ASCOM Platform {bestVersion} is installed (visible in {sourceView} as '{sourceValueName}'), " +
                $"but AscomDeviceIterator.CheckMininumAscomPlatformVersion returned false. " +
                $"Likely cause: the iterator only reads the value name 'PlatformVersion' (no space), " +
                $"but Platform 7.x writes it as 'Platform Version' (with a space).");
        }

        detected.ShouldBeTrue();
    }

    [Fact]
    public async Task IteratorCheckSupportReturnsTrueWhenAscomPlatformInstalled()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ASCOM only on Windows.");
        Assert.SkipUnless(AnyRegistryViewHasAscomPlatform(), "ASCOM Platform not installed in any registry view — nothing to diagnose.");

        var iterator = new AscomDeviceIterator();
        var supported = await iterator.CheckSupportAsync(TestContext.Current.CancellationToken);

        output.WriteLine($"AscomDeviceIterator.CheckSupportAsync = {supported}");
        supported.ShouldBeTrue("ASCOM is visibly installed but the iterator says unsupported — discovery will silently return nothing.");
    }

    [Fact]
    public async Task IteratorDiscoversAtLeastOneDriverWhenAscomPlatformInstalled()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ASCOM only on Windows.");
        Assert.SkipUnless(AnyRegistryViewHasAscomPlatform(), "ASCOM Platform not installed in any registry view.");

        var iterator = new AscomDeviceIterator();

        // Discovery is a no-op if CheckSupportAsync returns false, so report that first.
        var supported = await iterator.CheckSupportAsync(TestContext.Current.CancellationToken);
        if (!supported)
        {
            Assert.Fail("Iterator says unsupported despite ASCOM being installed — see CheckMinimumAscomPlatformVersionMatchesIteratorContract for diagnosis.");
        }

        await iterator.DiscoverAsync(TestContext.Current.CancellationToken);

        var perType = _ascomDeviceTypes.ToDictionary(t => t, t => iterator.RegisteredDevices(t).ToArray());
        var total = perType.Values.Sum(arr => arr.Length);

        foreach (var (type, devices) in perType)
        {
            output.WriteLine($"  {type,-20} : {devices.Length}");
            foreach (var d in devices)
            {
                output.WriteLine($"    {d.DeviceUri}");
            }
        }
        output.WriteLine($"TOTAL discovered: {total}");

        if (total == 0)
        {
            Assert.Fail(
                "Discovery returned 0 devices despite ASCOM being installed. " +
                "Check the DumpAscomDriverSubkeys output: if Registry64 has drivers but Registry32 is empty, " +
                "GetDriversFromRegistry is opening the wrong view.");
        }
    }

    private static bool AnyRegistryViewHasAscomPlatform()
    {
        // Look for either spelling — Platform <= 6.x uses 'PlatformVersion',
        // Platform 7.x uses 'Platform Version' (with a space).
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64, RegistryView.Default })
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var platformKey = hklm.OpenSubKey(AscomPlatformSubKey, writable: false);
            if (platformKey is null) continue;

            if (platformKey.GetValue("PlatformVersion") is string or null
                && (platformKey.GetValue("PlatformVersion") is string s1 && Version.TryParse(s1, out _)
                    || platformKey.GetValue("Platform Version") is string s2 && Version.TryParse(s2, out _)))
            {
                return true;
            }
        }
        return false;
    }
}
