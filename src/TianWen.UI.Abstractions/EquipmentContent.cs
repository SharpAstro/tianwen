using System;
using System.Collections.Generic;
using TianWen.Lib.Devices;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Display model for a device slot in the profile summary.
/// </summary>
public readonly record struct DeviceSlotRow(
    string Label,
    string DeviceName,
    bool IsAssigned,
    AssignTarget Slot);

/// <summary>
/// Display model for an OTA section in the profile summary.
/// </summary>
public readonly record struct OtaSummaryRow(
    int Index,
    string Name,
    string Properties,
    IReadOnlyList<DeviceSlotRow> DeviceSlots,
    IReadOnlyList<FilterSlotRow>? Filters);

/// <summary>
/// Display model for a filter slot in the filter table.
/// </summary>
public readonly record struct FilterSlotRow(
    int Position,
    string Name,
    int FocusOffset);

/// <summary>
/// Content helper for the equipment/profile tab.
/// Produces display-ready models from profile data, consumed by both
/// GPU (PixelWidgetBase) and terminal (Console.Lib) hosts.
/// </summary>
public class EquipmentContent(IDeviceHub? registry = null)
{
    /// <summary>
    /// Returns all profile-level device slots (core + extra).
    /// </summary>
    public List<DeviceSlotRow> GetProfileSlots(ProfileData data)
    {
        var slots = GetCoreProfileSlots(data);
        slots.AddRange(GetExtraProfileSlots(data));
        return slots;
    }

    /// <summary>
    /// Core profile-level slots that have special rendering (site editing, focal length, settings).
    /// These are rendered by hardcoded sections in the equipment tab.
    /// </summary>
    public List<DeviceSlotRow> GetCoreProfileSlots(ProfileData data)
    {
        return
        [
            new DeviceSlotRow("Mount", DeviceLabel(data.Mount), IsAssignedDevice(data.Mount), new AssignTarget.ProfileLevel("Mount")),
            new DeviceSlotRow("Guider", DeviceLabel(data.Guider), IsAssignedDevice(data.Guider), new AssignTarget.ProfileLevel("Guider")),
            new DeviceSlotRow("Guider Cam", DeviceLabel(data.GuiderCamera), IsAssignedDevice(data.GuiderCamera), new AssignTarget.ProfileLevel("GuiderCamera")),
            new DeviceSlotRow("Guider Foc", DeviceLabel(data.GuiderFocuser), IsAssignedDevice(data.GuiderFocuser), new AssignTarget.ProfileLevel("GuiderFocuser")),
        ];
    }

    /// <summary>
    /// Extra profile-level device slots (weather, future device types).
    /// Rendered dynamically in a loop — no special UI between them.
    /// </summary>
    public List<DeviceSlotRow> GetExtraProfileSlots(ProfileData data)
    {
        return
        [
            new DeviceSlotRow("Weather", DeviceLabel(data.Weather), IsAssignedDevice(data.Weather), new AssignTarget.ProfileLevel("Weather")),
        ];
    }

    /// <summary>
    /// Returns the site info string, or null if no site is configured.
    /// </summary>
    public string? GetSiteLabel(ProfileData data)
    {
        var site = EquipmentActions.GetSiteFromProfile(data);
        if (!site.HasValue)
        {
            return null;
        }

        var (lat, lon, elev) = site.Value;
        var latStr = lat >= 0 ? $"{lat:F1}\u00b0N" : $"{-lat:F1}\u00b0S";
        var lonStr = lon >= 0 ? $"{lon:F1}\u00b0E" : $"{-lon:F1}\u00b0W";
        var elevStr = elev.HasValue ? $", {elev.Value:F0}m" : "";
        return $"{latStr}, {lonStr}{elevStr}";
    }

    /// <summary>
    /// Returns OTA summary rows for all OTAs in the profile.
    /// </summary>
    public List<OtaSummaryRow> GetOtaSummaries(ProfileData data)
    {
        var rows = new List<OtaSummaryRow>(data.OTAs.Length);
        for (var i = 0; i < data.OTAs.Length; i++)
        {
            var ota = data.OTAs[i];

            var props = $"f={ota.FocalLength}mm";
            if (ota.Aperture is { } ap)
            {
                var fRatio = (double)ota.FocalLength / ap;
                props += $" \u00d8{ap}mm f/{fRatio:F1}";
            }
            if (ota.OpticalDesign is not OpticalDesign.Unknown)
            {
                props += $" ({ota.OpticalDesign})";
            }

            var slots = new List<DeviceSlotRow>
            {
                new DeviceSlotRow("Camera", DeviceLabel(ota.Camera), IsAssignedDevice(ota.Camera), new AssignTarget.OTALevel(i, "Camera")),
                new DeviceSlotRow("Focuser", DeviceLabel(ota.Focuser), IsAssignedDevice(ota.Focuser), new AssignTarget.OTALevel(i, "Focuser")),
                new DeviceSlotRow("Filter Wheel", DeviceLabel(ota.FilterWheel), IsAssignedDevice(ota.FilterWheel), new AssignTarget.OTALevel(i, "FilterWheel")),
                new DeviceSlotRow("Cover", DeviceLabel(ota.Cover), IsAssignedDevice(ota.Cover), new AssignTarget.OTALevel(i, "Cover")),
            };

            List<FilterSlotRow>? filters = null;
            if (ota.FilterWheel is not null && ota.FilterWheel != NoneDevice.Instance.DeviceUri)
            {
                var installedFilters = EquipmentActions.GetFilterConfig(data, i);
                filters = new List<FilterSlotRow>(installedFilters.Count);
                for (var f = 0; f < installedFilters.Count; f++)
                {
                    filters.Add(new FilterSlotRow(f + 1, EquipmentActions.FilterDisplayName(installedFilters[f]), installedFilters[f].Position));
                }
            }

            rows.Add(new OtaSummaryRow(i, ota.Name, props, slots, filters));
        }

        return rows;
    }

    /// <summary>
    /// Formats a complete profile summary as markdown.
    /// </summary>
    public string FormatProfileMarkdown(Profile profile)
    {
        var data = profile.Data;
        if (data is null)
        {
            return $"## {profile.DisplayName}\n\nNo data.";
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## {profile.DisplayName}");
        sb.AppendLine();

        // Profile-level devices
        foreach (var slot in GetProfileSlots(data.Value))
        {
            var marker = slot.IsAssigned ? "\u2705" : "\u274c";
            sb.AppendLine($"**{slot.Label}:** {marker} {slot.DeviceName}");
            sb.AppendLine();
        }

        // Site
        var siteLabel = GetSiteLabel(data.Value);
        if (siteLabel is not null)
        {
            sb.AppendLine($"**Site:** {siteLabel}");
        }
        else
        {
            sb.AppendLine("**Site:** *not configured*");
        }

        // OTAs
        foreach (var ota in GetOtaSummaries(data.Value))
        {
            sb.AppendLine();
            sb.AppendLine($"### Telescope #{ota.Index}: {ota.Name}");
            sb.AppendLine();
            sb.AppendLine($"*{ota.Properties}*");
            sb.AppendLine();

            foreach (var slot in ota.DeviceSlots)
            {
                var marker = slot.IsAssigned ? "\u2705" : "\u274c";
                sb.AppendLine($"**{slot.Label}:** {marker} {slot.DeviceName}");
                sb.AppendLine();
            }

            if (ota.Filters is { Count: > 0 } filters)
            {
                sb.AppendLine();
                sb.AppendLine("| # | Filter | Offset |");
                sb.AppendLine("|--:|--------|-------:|");
                foreach (var f in filters)
                {
                    var offsetStr = f.FocusOffset >= 0 ? $"+{f.FocusOffset}" : $"{f.FocusOffset}";
                    sb.AppendLine($"| {f.Position} | {f.Name} | {offsetStr} |");
                }
            }
        }

        if (data.Value.OTAs.Length == 0)
        {
            sb.AppendLine();
            sb.AppendLine("*No OTAs configured.*");
        }

        return sb.ToString();
    }

    private string DeviceLabel(Uri? uri) => EquipmentActions.DeviceLabel(uri, registry);

    private static bool IsAssignedDevice(Uri? uri)
        => uri is not null && uri != NoneDevice.Instance.DeviceUri;
}
