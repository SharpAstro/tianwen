using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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

/// <summary>Semantic vertical-gap sizes used by <see cref="PanelSection.Spacer"/>; the renderer maps them to scaled padding.</summary>
public enum PanelGap
{
    /// <summary>Half the panel padding.</summary>
    Half,
    /// <summary>The full panel padding.</summary>
    Full,
}

/// <summary>
/// One vertical section of the profile/equipment panel, in render order. The panel is described as a
/// data-driven list of these (see <see cref="EquipmentContent.GetProfilePanelSections"/>): the host walks
/// the list and dispatches each section to its renderer, so the panel's order lives in the content model,
/// not in a hand-sequenced cursor walk (TODO.md:57). Surface-neutral -- a section carries only structural
/// keys (device URI, OTA index, slot row), never runtime/UI state; the renderer pulls live state (hub,
/// input fields) when it draws. Sections whose visibility depends on runtime state (telemetry /
/// device-settings shown only when a device is hub-connected or declares settings) are always emitted --
/// their renderer self-gates to a no-op.
/// </summary>
public abstract record PanelSection
{
    /// <summary>The profile-name header row.</summary>
    public sealed record ProfileHeader : PanelSection;

    /// <summary>A full-width 1px divider followed by a padding-sized gap.</summary>
    public sealed record Separator : PanelSection;

    /// <summary>A vertical gap (semantic size; the renderer maps it to scaled padding).</summary>
    public sealed record Spacer(PanelGap Gap) : PanelSection;

    /// <summary>A device-slot row (click to assign).</summary>
    public sealed record Slot(DeviceSlotRow Row) : PanelSection;

    /// <summary>The site latitude/longitude/elevation block (display, edit, or "set site").</summary>
    public sealed record Site : PanelSection;

    /// <summary>The guide-scope focal-length input row.</summary>
    public sealed record GuideFocalLength : PanelSection;

    /// <summary>Per-device settings sub-section for <paramref name="Device"/> (self-gates when unassigned / no settings).</summary>
    public sealed record DeviceSettings(Uri? Device, string Label) : PanelSection;

    /// <summary>Mount RA/Dec/slew/track telemetry expander (self-gates when the mount is not hub-connected).</summary>
    public sealed record MountTelemetry(Uri? Mount) : PanelSection;

    /// <summary>Camera cooler + temperature telemetry expander (self-gates when the camera is not hub-connected).</summary>
    public sealed record CameraTelemetry(Uri? Camera) : PanelSection;

    /// <summary>An OTA section header with its Remove / Edit buttons.</summary>
    public sealed record OtaHeader(int Index) : PanelSection;

    /// <summary>The OTA's optical properties (inline editors when editing, summary otherwise).</summary>
    public sealed record OtaProps(int Index) : PanelSection;

    /// <summary>The OTA's filter table (emitted only when a filter wheel is assigned).</summary>
    public sealed record FilterTable(int OtaIndex) : PanelSection;

    /// <summary>The Add-OTA + Connect-All action row.</summary>
    public sealed record AddOta : PanelSection;
}

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
    /// Builds the ordered, data-driven section list for the profile/equipment panel. The host renders each
    /// section in order (advancing a cursor), so the panel's structure -- including the per-OTA loop -- lives
    /// here rather than in a hardcoded cursor walk (TODO.md:57). ProfileData-derivable visibility (e.g. a
    /// filter table only when a filter wheel is assigned) is decided here; runtime-only visibility
    /// (telemetry / settings shown when a device is hub-connected) is NOT -- those sections are always
    /// emitted and their renderer self-gates. Surface-neutral: consumable by the GPU and (later) TUI host alike.
    /// </summary>
    public ImmutableArray<PanelSection> GetProfilePanelSections(ProfileData data)
    {
        var b = ImmutableArray.CreateBuilder<PanelSection>();

        // Profile name header + divider.
        b.Add(new PanelSection.ProfileHeader());
        b.Add(new PanelSection.Separator());

        // Core profile slots are [Mount, Guider, Guider Cam, Guider Foc] (see GetCoreProfileSlots).
        var core = GetCoreProfileSlots(data);

        // Mount: slot, live telemetry, driver settings, then the site block.
        b.Add(new PanelSection.Slot(core[0]));
        b.Add(new PanelSection.MountTelemetry(data.Mount));
        b.Add(new PanelSection.DeviceSettings(data.Mount, "Mount Settings"));
        b.Add(new PanelSection.Site());
        b.Add(new PanelSection.Spacer(PanelGap.Half));

        // Guider group: guider, guide camera, guide focuser, guide-scope focal length, guider settings.
        b.Add(new PanelSection.Slot(core[1]));
        b.Add(new PanelSection.Slot(core[2]));
        b.Add(new PanelSection.Slot(core[3]));
        b.Add(new PanelSection.GuideFocalLength());
        b.Add(new PanelSection.DeviceSettings(data.Guider, "Guider Settings"));

        b.Add(new PanelSection.Spacer(PanelGap.Full));
        b.Add(new PanelSection.Separator());

        // Extra profile slots (Weather, future device types) + their settings.
        foreach (var slot in GetExtraProfileSlots(data))
        {
            b.Add(new PanelSection.Slot(slot));
            b.Add(new PanelSection.DeviceSettings(EquipmentActions.GetAssignedDevice(data, slot.Slot), $"{slot.Label} Settings"));
        }

        b.Add(new PanelSection.Spacer(PanelGap.Full));
        b.Add(new PanelSection.Separator());

        // Per-OTA sections -- the data-driven loop (no hardcoded count).
        foreach (var ota in GetOtaSummaries(data))
        {
            var otaData = data.OTAs[ota.Index];
            b.Add(new PanelSection.OtaHeader(ota.Index));
            b.Add(new PanelSection.OtaProps(ota.Index));

            // Sub-slots are [Camera, Focuser, Filter Wheel, Cover]; indent the labels to match the panel.
            var sub = ota.DeviceSlots;
            b.Add(new PanelSection.Slot(Indent(sub[0])));                                  // Camera
            b.Add(new PanelSection.DeviceSettings(otaData.Camera, "Camera Settings"));
            b.Add(new PanelSection.CameraTelemetry(otaData.Camera));
            b.Add(new PanelSection.Slot(Indent(sub[1])));                                  // Focuser
            b.Add(new PanelSection.DeviceSettings(otaData.Focuser, "Focuser Settings"));
            b.Add(new PanelSection.Slot(Indent(sub[2])));                                  // Filter Wheel
            if (ota.Filters is not null)                                                   // filter wheel assigned
            {
                b.Add(new PanelSection.FilterTable(ota.Index));
            }
            b.Add(new PanelSection.Slot(Indent(sub[3])));                                  // Cover
            b.Add(new PanelSection.Spacer(PanelGap.Half));
        }

        // Add-OTA / Connect-All action row.
        b.Add(new PanelSection.AddOta());

        return b.ToImmutable();

        static DeviceSlotRow Indent(DeviceSlotRow row) => row with { Label = "  " + row.Label };
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
