using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TianWen.Hosting;

/// <summary>
/// When the machine last came up, from the operating system: what tells a crash journal from a rig nobody knows the
/// state of (a journal older than the last boot, docs/plans/hardware-in-the-server.md, "When the server dies").
/// </summary>
/// <remarks>
/// <para>
/// <b>Never from uptime alone.</b> <c>Environment.TickCount64</c> stops during suspend on Linux, and on Windows it runs
/// on through a Fast Startup shutdown, which hibernates the kernel instead of stopping it: the machine powers off and
/// back on, every USB device with it, and the tick count, <c>LastBootUpTime</c> and the registry's shutdown time all
/// carry on as if nothing happened. Measured on the desktop that wrote this (2026-09-26): a Fast Startup boot on
/// 2026-09-15 that only the Kernel-Boot event saw.
/// </para>
/// <para>
/// So on Windows the answer is the newest <c>Microsoft-Windows-Kernel-Boot</c> event 27 ("The boot type was 0x..."),
/// which the kernel logs at every boot, a Fast Startup one included, and which a user of the machine may read. The tick
/// count is its floor, and the whole answer when the log cannot be read. Linux has <c>btime</c> in
/// <c>/proc/stat</c>, and macOS and FreeBSD <c>kern.boottime</c>: both wall-clock times of the boot itself.
/// </para>
/// </remarks>
internal static partial class MachineBoot
{
    /// <summary>When the machine last booted, in UTC, or null when the operating system will not say.</summary>
    public static DateTimeOffset? LastBootUtc()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return WindowsLastBootUtc();
            }
            if (OperatingSystem.IsLinux())
            {
                return LinuxLastBootUtc();
            }
            if (OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
            {
                return BsdLastBootUtc();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
        {
            // Unknown, which the journal treats as a boot it cannot rule out.
        }
        return null;
    }

    [SupportedOSPlatform("linux")]
    private static DateTimeOffset? LinuxLastBootUtc()
    {
        foreach (var line in File.ReadLines("/proc/stat"))
        {
            if (line.StartsWith("btime ", StringComparison.Ordinal)
                && long.TryParse(line.AsSpan(6).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
            }
        }
        return null;
    }

    [SupportedOSPlatform("macos")]
    [SupportedOSPlatform("freebsd")]
    private static unsafe DateTimeOffset? BsdLastBootUtc()
    {
        // struct timeval { time_t tv_sec; suseconds_t tv_usec; }: 16 bytes on a 64-bit build, seconds first.
        var timeval = stackalloc byte[16];
        nuint size = 16;
        if (SysctlByName("kern.boottime", timeval, ref size, null, 0) != 0 || size < 8)
        {
            return null;
        }
        return DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(timeval, 8)));
    }

    [LibraryImport("libc", EntryPoint = "sysctlbyname", StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int SysctlByName(string name, void* oldValue, ref nuint oldLength, void* newValue, nuint newLength);

    [SupportedOSPlatform("windows")]
    private static DateTimeOffset WindowsLastBootUtc()
    {
        var tickFloor = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        return NewestKernelBootEventUtc() is { } logged && logged > tickFloor ? logged : tickFloor;
    }

    private const int EvtQueryChannelPath = 0x1;
    private const int EvtQueryReverseDirection = 0x200;
    private const int EvtRenderContextSystem = 1;
    private const int EvtRenderEventValues = 0;
    private const int EvtSystemTimeCreated = 8;
    private const int EvtVarTypeFileTime = 17;
    private const int EvtVariantSize = 16;
    private const int ErrorInsufficientBuffer = 122;

    /// <summary>The time the kernel logged its newest boot, or null when the System log will not say.</summary>
    [SupportedOSPlatform("windows")]
    internal static unsafe DateTimeOffset? NewestKernelBootEventUtc()
    {
        var query = EvtQuery(0, "System", "*[System[Provider[@Name='Microsoft-Windows-Kernel-Boot'] and (EventID=27)]]",
            EvtQueryChannelPath | EvtQueryReverseDirection);
        if (query == 0)
        {
            return null;
        }

        nint evt = 0, context = 0;
        try
        {
            if (!EvtNext(query, 1, out evt, 5_000, 0, out var returned) || returned < 1)
            {
                return null;
            }
            context = EvtCreateRenderContext(0, 0, EvtRenderContextSystem);
            if (context == 0)
            {
                return null;
            }

            // The system properties as an array of EVT_VARIANT (an 8-byte value, a count and a type), then the strings
            // they point into; a few hundred bytes, so the stack serves unless the kernel says otherwise.
            var buffer = stackalloc byte[4096];
            var size = 4096;
            byte[]? rented = null;
            if (!EvtRender(context, evt, EvtRenderEventValues, size, buffer, out var used, out var count))
            {
                if (Marshal.GetLastPInvokeError() != ErrorInsufficientBuffer)
                {
                    return null;
                }
                rented = new byte[used];
                size = used;
                fixed (byte* larger = rented)
                {
                    if (!EvtRender(context, evt, EvtRenderEventValues, size, larger, out used, out count))
                    {
                        return null;
                    }
                    return TimeCreated(new ReadOnlySpan<byte>(larger, used), count);
                }
            }
            return TimeCreated(new ReadOnlySpan<byte>(buffer, used), count);
        }
        finally
        {
            if (context != 0)
            {
                EvtClose(context);
            }
            if (evt != 0)
            {
                EvtClose(evt);
            }
            EvtClose(query);
        }
    }

    private static DateTimeOffset? TimeCreated(ReadOnlySpan<byte> values, int count)
    {
        var at = EvtSystemTimeCreated * EvtVariantSize;
        if (count <= EvtSystemTimeCreated || values.Length < at + EvtVariantSize
            || BinaryPrimitives.ReadInt32LittleEndian(values.Slice(at + 12, 4)) != EvtVarTypeFileTime)
        {
            return null;
        }
        return DateTimeOffset.FromFileTime(BinaryPrimitives.ReadInt64LittleEndian(values.Slice(at, 8))).ToUniversalTime();
    }

    [LibraryImport("wevtapi.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint EvtQuery(nint session, string path, string query, int flags);

    [LibraryImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EvtNext(nint resultSet, int eventsSize, out nint events, int timeout, int flags, out int returned);

    [LibraryImport("wevtapi.dll", SetLastError = true)]
    private static partial nint EvtCreateRenderContext(int valuePathsCount, nint valuePaths, int flags);

    [LibraryImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool EvtRender(nint context, nint fragment, int flags, int bufferSize, void* buffer, out int bufferUsed, out int propertyCount);

    [LibraryImport("wevtapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EvtClose(nint handle);
}
