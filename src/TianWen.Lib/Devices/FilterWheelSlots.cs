using System;
using System.Collections.Generic;
using System.Globalization;
using System.Web;

namespace TianWen.Lib.Devices;

/// <summary>
/// A filter wheel's slots as its URI states them: <c>filter1..N</c> (the names) and <c>offset1..N</c> (each filter's focus
/// offset), the profile being where a wheel's filters live. ONE reader and ONE writer for every place that reads or writes
/// them: the Equipment tab's table and its Save, a fake wheel's discovery, and what a wheel's connect writes into the
/// profile (<see cref="ProfileDataExtensions"/>, <c>CaptureFilterSlots</c>).
/// </summary>
public static class FilterWheelSlots
{
    /// <summary>The slots <paramref name="wheelUri"/> lists, in order, stopping at the first slot it does not name; empty when it lists none.</summary>
    public static IReadOnlyList<InstalledFilter> Read(Uri wheelUri)
    {
        var query = HttpUtility.ParseQueryString(wheelUri.Query);
        var filters = new List<InstalledFilter>();
        for (var slot = 1; query[DeviceQueryKeyExtensions.FilterKey(slot)] is { } name; slot++)
        {
            var offset = int.TryParse(query[DeviceQueryKeyExtensions.FilterOffsetKey(slot)], NumberStyles.Integer, CultureInfo.InvariantCulture, out var o) ? o : 0;
            filters.Add(new InstalledFilter(name, offset));
        }
        return filters;
    }

    /// <summary>
    /// <paramref name="wheelUri"/> listing exactly <paramref name="filters"/>: the slots it listed before are removed, every
    /// other query key (a transport's, a device setting) is kept.
    /// </summary>
    public static Uri WithSlots(Uri wheelUri, IReadOnlyList<InstalledFilter> filters)
    {
        var query = HttpUtility.ParseQueryString(wheelUri.Query);
        for (var slot = 1; query[DeviceQueryKeyExtensions.FilterKey(slot)] is not null; slot++)
        {
            query.Remove(DeviceQueryKeyExtensions.FilterKey(slot));
            query.Remove(DeviceQueryKeyExtensions.FilterOffsetKey(slot));
        }
        for (var slot = 0; slot < filters.Count; slot++)
        {
            query[DeviceQueryKeyExtensions.FilterKey(slot + 1)] = filters[slot].DisplayName;
            query[DeviceQueryKeyExtensions.FilterOffsetKey(slot + 1)] = filters[slot].Position.ToString(CultureInfo.InvariantCulture);
        }
        return new UriBuilder(wheelUri) { Query = query.ToString() }.Uri;
    }
}
