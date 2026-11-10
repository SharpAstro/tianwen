using System;
using System.Collections.Generic;
using System.Linq;

namespace TianWen.Lib.Devices;

/// <summary>
/// What a reconcile FOLLOWED, worded once for the node's notification feed (#2040): a follow connects with no dialogue, and
/// says so, so a wrong one is seen in the Notifications tab instead of silently (the owner, 2026-11-06). An exact match says
/// nothing, and neither does a device whose port alone moved under the same id (a camera's serial, a USB bridge's serial, a
/// socket): that is transport, as it always was. Nor does an id re-keyed where the device sits on the same port key; another
/// unit on the same COM number is a follow and is said.
/// </summary>
public static class DeviceFollowNotes
{
    /// <summary>One line per device of <paramref name="before"/> that reconcile followed to another id, or whose firmware changed.</summary>
    public static IEnumerable<string> For(ProfileData before, ProfileData after)
        => before.DeviceUris().Zip(after.DeviceUris()).Select(pair => Of(pair.First, pair.Second)).OfType<string>();

    /// <summary>
    /// The note for one device, or null: <c>Gemini Focuser Pro: followed to COM4 (was COM3)</c> when a device was matched by
    /// the evidence under another id, <c>Skywatcher AZ-GTi: firmware 3.39 to 3.40</c> when its firmware changed, both when both.
    /// </summary>
    public static string? Of(Uri before, Uri after)
    {
        var (from, to) = (Where(before), Where(after));
        var samePlace = from is not null && string.Equals(from, to, StringComparison.OrdinalIgnoreCase);
        // An id that changed only its SHAPE where the device sits (a serial-less port re-keyed on its socket after an
        // upgrade, GeminiFocuser_COM3 to GeminiFocuser_socket-1a2b3c4d on COM3) moved nothing, so it says nothing. That holds
        // only while the port's own key is the one stored, or the stored one named nothing but the port (no key, or a name
        // key): another cable or socket that Windows handed the same COM number is another unit, and that follow is said, by
        // its ids, since its port reads the same.
        var keyBefore = before.QueryValue(DeviceQueryKey.PortKey);
        var reKeyedInPlace = samePlace
            && (keyBefore is not { Length: > 0 }
                || before.QueryValue(DeviceQueryKey.PortIdentity) is not ("device" or "socket")
                || string.Equals(keyBefore, after.QueryValue(DeviceQueryKey.PortKey), StringComparison.Ordinal));
        var followed = !reKeyedInPlace && !DeviceUriExtensions.DeviceKeyComparer.Equals(before.DeviceKey, after.DeviceKey);
        var firmwareBefore = before.QueryValue(DeviceQueryKey.Firmware);
        var firmwareAfter = after.QueryValue(DeviceQueryKey.Firmware);
        var firmwareChanged = firmwareBefore is { Length: > 0 } && firmwareAfter is { Length: > 0 }
            && !string.Equals(firmwareBefore, firmwareAfter, StringComparison.OrdinalIgnoreCase);
        if (!followed && !firmwareChanged)
        {
            return null;
        }

        var parts = new List<string>(2);
        if (followed)
        {
            parts.Add(samePlace ? $"followed to {after.DeviceKey} on {to} (was {before.DeviceKey})"
                : from is not null && to is not null ? $"followed to {to} (was {from})"
                : $"followed (was {before.DeviceKey})");
        }
        if (firmwareChanged)
        {
            parts.Add($"firmware {firmwareBefore} to {firmwareAfter}");
        }

        return $"{NameOf(after)}: {string.Join("; ", parts)}";
    }

    /// <summary>The device's display name, its fragment, without the "on COM4" a serial probe appends.</summary>
    private static string NameOf(Uri uri)
    {
        var name = Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));
        var on = name.LastIndexOf(" on ", StringComparison.Ordinal);
        return on > 0 ? name[..on] : name is { Length: > 0 } ? name : uri.DeviceKey;
    }

    /// <summary>Where a device was reached: its port without the <c>serial:</c> prefix, else its host.</summary>
    private static string? Where(Uri uri)
        => uri.QueryValue(DeviceQueryKey.Port) is { Length: > 0 } port ? Connections.ISerialConnection.RemoveProtoPrrefix(port)
            : uri.QueryValue(DeviceQueryKey.Host);
}
