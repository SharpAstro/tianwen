using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;

namespace TianWen.Lib.Devices;

/// <summary>
/// Hides ASCOM COM drivers for which TianWen ships a first-class native backend, so a discovery picker
/// offers exactly one entry per physical device instead of the native driver plus its redundant (often
/// buggier) ASCOM twin. The motivating case is the Gemini FlatPanel Lite: its ASCOM driver both
/// CET-crashes an in-proc host and stores its COM port in a per-process <c>user.config</c> (so it
/// silently fails to connect from any host that did not configure it) -- the native serial backend has
/// neither problem.
/// <para>
/// The hide is <b>conditional on native availability</b>: an ASCOM ProgID is dropped only when the
/// discovery pass actually surfaced a native device of the superseding family (matched by
/// <see cref="DeviceBase.DeviceClass"/>, the URI host). If the native SDK can't load or no device is
/// present, no native device is discovered and the ASCOM twin passes through as the fallback -- the user
/// is never left with no driver.
/// </para>
/// <para>
/// The correlation is pure data (ASCOM ProgID -&gt; native <see cref="DeviceBase.DeviceClass"/>) matched
/// against the already-discovered devices, so no device family references another and the ASCOM subsystem
/// depends on nothing here. ProgID matching is exact + case-insensitive; an unknown vendor ProgID variant
/// is simply not hidden (a duplicate entry is a safe failure, a missing device is not).
/// </para>
/// </summary>
internal static class NativeDriverBlacklist
{
    // ASCOM ProgID -> DeviceClass (URI host, == the native device type name) of the family that supersedes
    // it. Only vendors whose native driver is a genuine, complete replacement for the SAME device type are
    // listed. Deliberately excluded: the mount drivers (ASCOM.iOptron2017/OnStep/SkyWatcher), whose native
    // equivalents cover only a vendor subset -- e.g. native iOptron is the SkyGuider Pro, not the CEM/GEM/HEM
    // range ASCOM.iOptron2017.Telescope drives -- so hiding them would remove mounts we cannot otherwise control.
    private static readonly IReadOnlyDictionary<string, string> _nativeClassByProgId =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Gemini FlatPanel Lite -- native serial cover/calibrator (GeminiDevice, AddGemini).
            ["ASCOM.GeminiFPLite.CoverCalibrator"] = "GeminiDevice",

            // Gemini Focuser Pro -- native serial focuser, a rebadged myFocuserPro2 (GeminiFocuserDevice, AddGemini).
            ["ASCOM.GeminiFocuserPro.Focuser"] = "GeminiFocuserDevice",

            // ZWO ASI camera / EAF focuser / EFW filter wheel -- native ZWO SDK (ZWODevice, AddZWO).
            ["ASCOM.ASICamera2.Camera"] = "ZWODevice",
            ["ASCOM.ASICamera2_2.Camera"] = "ZWODevice",
            ["ASCOM.EAF.Focuser"] = "ZWODevice",
            ["ASCOM.EAF_2.Focuser"] = "ZWODevice",
            ["ASCOM.EFW2.FilterWheel"] = "ZWODevice",
            ["ASCOM.EFW2_2.FilterWheel"] = "ZWODevice",

            // QHYCCD camera / filter wheel / focuser -- native QHYCCD SDK (QHYDevice, AddQHY).
            ["ASCOM.QHYCCD.Camera"] = "QHYDevice",
            ["ASCOM.QHYCCD_CAM2.Camera"] = "QHYDevice",
            ["ASCOM.QHYCCD_GUIDER.Camera"] = "QHYDevice",
            ["ASCOM.QHYCFW.FilterWheel"] = "QHYDevice",
            ["ASCOM.QHYCFW2st.FilterWheel"] = "QHYDevice",
            ["ASCOM.QHYFWRS232.FilterWheel"] = "QHYDevice",
            ["ASCOM.qfoc.Focuser"] = "QHYDevice",
        };

    /// <summary>Looks up the native <see cref="DeviceBase.DeviceClass"/> that supersedes an ASCOM ProgID, if any.</summary>
    public static bool TryGetNativeClass(string progId, out string nativeClass)
        => _nativeClassByProgId.TryGetValue(progId, out nativeClass!);

    /// <summary>
    /// Filters <paramref name="devices"/> (the discovered devices of a single <see cref="DeviceType"/>),
    /// dropping any ASCOM driver whose ProgID is superseded by a native family that is also present in the
    /// same set. Everything else passes through unchanged.
    /// </summary>
    public static IEnumerable<DeviceBase> FilterSuperseded(IEnumerable<DeviceBase> devices, ILogger logger)
    {
        // Two passes over a single-type list (small): collect the DeviceClasses actually discovered, then
        // drop ASCOM twins whose superseding native family is among them. A native device's DeviceClass is
        // never itself a blacklisted ASCOM ProgID, so recording all classes can't cause a false hide.
        // DeviceClass comes from Uri.Host, which URI normalisation lower-cases ("ZWODevice" -> "zwodevice"),
        // so the mapped native class ("ZWODevice") must be matched case-insensitively.
        var materialized = devices as IReadOnlyList<DeviceBase> ?? [.. devices];
        var classesPresent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in materialized)
        {
            classesPresent.Add(device.DeviceClass);
        }

        foreach (var device in materialized)
        {
            if (_nativeClassByProgId.TryGetValue(device.DeviceId, out var nativeClass)
                && classesPresent.Contains(nativeClass))
            {
                logger.LogDebug("Hiding ASCOM driver {ProgId} from discovery; superseded by discovered native {NativeClass}.",
                    device.DeviceId, nativeClass);
                continue;
            }

            yield return device;
        }
    }
}
