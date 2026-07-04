using Microsoft.Win32;
using System;
using System.IO;
using System.Runtime.Versioning;

namespace TianWen.Lib.Devices.Ascom.ComInterop;

/// <summary>
/// Decides whether an ASCOM driver must be hosted out-of-process (in the CET-off
/// <c>tianwen-ascomhost</c>) or can run in-proc via <see cref="DispatchObject"/>.
/// <para>
/// The out-of-process host is needed <b>only</b> for the CET-incompatible in-proc .NET Framework CLR:
/// a driver whose <c>InprocServer32</c> default value resolves to <c>mscoree.dll</c> (the Framework
/// shim). Those drivers fastfail our CET-on process on connect (0xC0000409). Everything else is CET-safe
/// and stays in-proc:
/// </para>
/// <list type="bullet">
///   <item><c>LocalServer32</c> drivers (e.g. GS Server) already run out-of-proc; COM marshals to their
///     own process, so their CLR never loads into ours.</item>
///   <item>Native in-proc drivers (ZWO/ASI/PlayerOne/QHYCCD) and .NET Core comhost drivers
///     (<c>&lt;name&gt;.comhost.dll</c>, not <c>mscoree.dll</c>) are CET-compatible.</item>
/// </list>
/// Reads both registry views (Platform 6 registers 32-bit, Platform 7 may be 64-bit-only), matching
/// <see cref="AscomDeviceIterator"/>'s discovery walk.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class AscomComServerClassifier
{
    /// <summary>
    /// True when <paramref name="progId"/> is an in-proc .NET Framework COM server
    /// (<c>InprocServer32 = mscoree.dll</c>) with no out-of-proc <c>LocalServer32</c>, i.e. one that
    /// must be hosted out-of-process. <paramref name="reason"/> carries a short human-readable
    /// classification for logging either way.
    /// </summary>
    public static bool RequiresOutOfProcessHost(string progId, out string reason)
    {
        if (NativeMethods.CLSIDFromProgID(progId, out var clsid) != 0)
        {
            reason = "ProgID does not resolve to a CLSID";
            return false;
        }

        var clsidString = $"{{{clsid.ToString().ToUpperInvariant()}}}";

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var clsidKey = hklm.OpenSubKey($@"SOFTWARE\Classes\CLSID\{clsidString}", false);
            if (clsidKey is null)
            {
                continue;
            }

            // LocalServer32 => the driver runs out-of-proc already; its CLR never loads into ours, so it
            // is CET-safe. Prefer in-proc DispatchObject (CoCreateInstance launches the local server).
            using (var localServer = clsidKey.OpenSubKey("LocalServer32", false))
            {
                if (localServer?.GetValue(null) is string ls && !string.IsNullOrWhiteSpace(ls))
                {
                    reason = "LocalServer32 (out-of-proc already, CET-safe)";
                    return false;
                }
            }

            using var inproc = clsidKey.OpenSubKey("InprocServer32", false);
            if (inproc?.GetValue(null) is string raw && !string.IsNullOrWhiteSpace(raw))
            {
                var fileName = Path.GetFileName(Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"')));
                if (string.Equals(fileName, "mscoree.dll", StringComparison.OrdinalIgnoreCase))
                {
                    var runtime = inproc.GetValue("RuntimeVersion") as string;
                    reason = $"InprocServer32=mscoree.dll (in-proc .NET Framework CLR, RuntimeVersion={runtime ?? "?"})";
                    return true;
                }

                reason = $"InprocServer32={fileName} (CET-safe in-proc)";
                return false;
            }
        }

        reason = "no usable InprocServer32/LocalServer32 found";
        return false;
    }
}
