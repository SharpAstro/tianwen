using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using DIR.Lib;
using SdlVulkan.Renderer;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;

namespace TianWen.UI.Gui
{
    /// <summary>
    /// Renders the Equipment / Profile tab for the TianWen N.I.N.A.-style GUI.
    /// Handles profile summary, discovered devices, and device assignment.
    /// </summary>
    public sealed class VkEquipmentTab : VkTabBase
    {
        // Layout constants (at 1x scale)
        private const float BaseProfilePanelWidth = 360f;
        private const float BaseFontSize          = 14f;
        private const float BaseItemHeight        = 24f;
        private const float BaseBottomBarHeight   = 50f;
        private const float BasePadding           = 8f;
        private const float BaseHeaderHeight      = 28f;
        private const float BaseButtonHeight      = 28f;
        private const float BaseBadgeWidth        = 68f;
        private const float BaseCheckmarkWidth    = 20f;
        private const float BaseArrowWidth        = 22f;

        // Colors
        private static readonly RGBAColor32 ProfilePanelBg   = new RGBAColor32(0x1e, 0x1e, 0x28, 0xff);
        private static readonly RGBAColor32 DeviceListBg     = new RGBAColor32(0x18, 0x18, 0x22, 0xff);
        private static readonly RGBAColor32 SlotNormal       = new RGBAColor32(0x2a, 0x2a, 0x35, 0xff);
        private static readonly RGBAColor32 SlotActive       = new RGBAColor32(0x20, 0x35, 0x55, 0xff);
        private static readonly RGBAColor32 SlotHover        = new RGBAColor32(0x30, 0x30, 0x3e, 0xff);
        private static readonly RGBAColor32 DeviceRowBg      = new RGBAColor32(0x20, 0x20, 0x2c, 0xff);
        private static readonly RGBAColor32 AssignedGreen    = new RGBAColor32(0x40, 0xc0, 0x40, 0xff);
        private static readonly RGBAColor32 DimmedText       = new RGBAColor32(0x60, 0x60, 0x70, 0xff);
        private static readonly RGBAColor32 CreateButton     = new RGBAColor32(0x30, 0x60, 0x90, 0xff);
        private static readonly RGBAColor32 HeaderText       = new RGBAColor32(0x88, 0xaa, 0xdd, 0xff);
        private static readonly RGBAColor32 BodyText         = new RGBAColor32(0xcc, 0xcc, 0xcc, 0xff);
        private static readonly RGBAColor32 DimText          = new RGBAColor32(0x88, 0x88, 0x88, 0xff);
        private static readonly RGBAColor32 SeparatorColor   = new RGBAColor32(0x33, 0x33, 0x44, 0xff);
        private static readonly RGBAColor32 BadgeBg          = new RGBAColor32(0x28, 0x28, 0x38, 0xff);
        private static readonly RGBAColor32 SiteText         = new RGBAColor32(0x99, 0xbb, 0x99, 0xff);
        private static readonly RGBAColor32 OtaHeaderBg      = new RGBAColor32(0x24, 0x24, 0x32, 0xff);
        private static readonly RGBAColor32 ContentBg        = new RGBAColor32(0x16, 0x16, 0x1e, 0xff);
        private static readonly RGBAColor32 BottomBarBg      = new RGBAColor32(0x14, 0x14, 0x1c, 0xff);
        private static readonly RGBAColor32 AccentInstruct   = new RGBAColor32(0x88, 0xcc, 0xff, 0xff);
        private static readonly RGBAColor32 FilterTableBg    = new RGBAColor32(0x1a, 0x1a, 0x26, 0xff);
        private static readonly RGBAColor32 FilterRowAlt     = new RGBAColor32(0x20, 0x20, 0x2e, 0xff);
        private static readonly RGBAColor32 EditButtonBg     = new RGBAColor32(0x2a, 0x40, 0x5a, 0xff);
        private static readonly RGBAColor32 RemoveButtonBg   = new RGBAColor32(0x5a, 0x2a, 0x2a, 0xff);

        private const int MaxFilterSlots = 8;

        /// <summary>Tab state (scroll offsets, discovery results, assignment mode).</summary>
        public EquipmentTabState State { get; } = new EquipmentTabState();

        /// <summary>Background task tracker for async operations. Set by the host.</summary>
        public BackgroundTaskTracker? Tracker { get; set; }

        /// <summary>Callback for device discovery (needs DI). Set by the host.</summary>
        public Func<Task>? OnDiscover { get; set; }

        /// <summary>Callback for adding a new OTA to the profile. Set by the host.</summary>
        public Func<Task>? OnAddOta { get; set; }

        /// <summary>Callback for starting site coordinate editing. Set by the host.</summary>
        public Action? OnEditSite { get; set; }

        /// <summary>Callback for creating a new profile. Set by the host.</summary>
        public Action? OnCreateProfile { get; set; }

        /// <summary>Callback for assigning a device to the active slot. Set by the host.</summary>
        public Func<int, Task>? OnAssignDevice { get; set; }

        /// <summary>Callback for updating profile data (filter config, OTA props). Set by the host.</summary>
        public Func<ProfileData, Task>? OnUpdateProfile { get; set; }

        public VkEquipmentTab(VkRenderer renderer) : base(renderer)
        {
        }

        // -----------------------------------------------------------------------
        // Public entry points
        // -----------------------------------------------------------------------

        /// <summary>
        /// Renders the Equipment tab into the given content area.
        /// </summary>
        public void Render(
            GuiAppState appState,
            RectF32 contentRect,
            float dpiScale,
            string fontPath)
        {
            // Clear clickable regions from previous frame
            BeginFrame();

            // Clear the whole content area first
            FillRect(contentRect.X, contentRect.Y, contentRect.Width, contentRect.Height, ContentBg);

            if (appState.ActiveProfile is null && !State.IsCreatingProfile)
            {
                RenderNoProfile(contentRect, dpiScale, fontPath);
                return;
            }

            if (State.IsCreatingProfile)
            {
                RenderProfileCreation(contentRect, dpiScale, fontPath);
                return;
            }

            RenderProfileView(appState, contentRect, dpiScale, fontPath);
        }

        // -----------------------------------------------------------------------
        // No-profile view
        // -----------------------------------------------------------------------

        private void RenderNoProfile(
            RectF32 rect,
            float dpiScale, string fontPath)
        {
            var fontSize    = BaseFontSize * dpiScale;
            var padding     = BasePadding * dpiScale;
            var buttonH     = BaseButtonHeight * dpiScale;
            var buttonW     = 160f * dpiScale;

            var centerX = rect.X + rect.Width / 2f;
            var centerY = rect.Y + rect.Height / 2f;

            DrawText(
                "No equipment profile configured.".AsSpan(),
                fontPath,
                rect.X, rect.Y, rect.Width, rect.Height - buttonH - padding * 2f,
                fontSize, DimText, TextAlign.Center, TextAlign.Far);

            var btnX = centerX - buttonW / 2f;
            var btnY = centerY + padding;

            RenderButton("Create Profile", btnX, btnY, buttonW, buttonH, fontPath, fontSize, CreateButton, BodyText, "CreateProfile",
                () => OnCreateProfile?.Invoke());
        }

        // -----------------------------------------------------------------------
        // Profile creation view
        // -----------------------------------------------------------------------

        private void RenderProfileCreation(
            RectF32 rect,
            float dpiScale, string fontPath)
        {
            var fontSize    = BaseFontSize * dpiScale;
            var padding     = BasePadding * dpiScale;
            var fieldH      = BaseItemHeight * dpiScale * 1.4f;
            var buttonH     = BaseButtonHeight * dpiScale;
            var bottomBarH  = BaseBottomBarHeight * dpiScale;

            var layout = new PixelLayout(rect);
            var bottomBarRect = layout.Dock(PixelDockStyle.Bottom, bottomBarH);
            var mainRect = layout.Fill();

            // Header
            DrawText(
                "New Equipment Profile".AsSpan(),
                fontPath,
                mainRect.X + padding, mainRect.Y + padding, mainRect.Width - padding * 2f, BaseHeaderHeight * dpiScale,
                fontSize * 1.2f, HeaderText, TextAlign.Near, TextAlign.Center);

            // Name input field
            var fieldY = mainRect.Y + padding + BaseHeaderHeight * dpiScale + padding;
            var fieldW = Math.Min(360f * dpiScale, mainRect.Width - padding * 2f);
            var fieldX = mainRect.X + padding;

            DrawText(
                "Profile name:".AsSpan(),
                fontPath,
                fieldX, fieldY, fieldW, fontSize * 1.5f,
                fontSize, BodyText, TextAlign.Near, TextAlign.Near);

            var inputY = (int)(fieldY + fontSize * 1.6f);
            RenderTextInput(State.ProfileNameInput, (int)fieldX, inputY, (int)fieldW, (int)fieldH, fontPath, fontSize);

            // Create button
            var btnY = inputY + (int)fieldH + (int)padding;
            var btnW = 120f * dpiScale;
            RenderButton("Create", fieldX, btnY, btnW, buttonH, fontPath, fontSize, CreateButton, BodyText, "CreateProfile",
                () => { if (State.ProfileNameInput.Text.Length > 0) State.ProfileNameInput.OnCommit?.Invoke(State.ProfileNameInput.Text); });

            // Bottom status
            FillRect(bottomBarRect.X, bottomBarRect.Y, bottomBarRect.Width, bottomBarRect.Height, BottomBarBg);
            DrawText(
                "Enter a name for the new profile and press Create or Enter.".AsSpan(),
                fontPath,
                bottomBarRect.X + padding, bottomBarRect.Y, bottomBarRect.Width - padding * 2f, bottomBarRect.Height,
                fontSize, DimText, TextAlign.Near, TextAlign.Center);
        }

        // -----------------------------------------------------------------------
        // Full profile view
        // -----------------------------------------------------------------------

        private void RenderProfileView(
            GuiAppState appState,
            RectF32 contentRect,
            float dpiScale, string fontPath)
        {
            var profilePanelW = BaseProfilePanelWidth * dpiScale;
            var bottomBarH    = BaseBottomBarHeight * dpiScale;

            var layout = new PixelLayout(contentRect);
            var bottomBarRect = layout.Dock(PixelDockStyle.Bottom, bottomBarH);
            var profileRect = layout.Dock(PixelDockStyle.Left, profilePanelW);
            var deviceListRect = layout.Fill();

            // Left: profile panel
            FillRect(profileRect.X, profileRect.Y, profileRect.Width, profileRect.Height, ProfilePanelBg);
            RenderProfilePanel(appState, profileRect, dpiScale, fontPath);

            // Vertical separator
            FillRect(deviceListRect.X, deviceListRect.Y, 1f, deviceListRect.Height, SeparatorColor);

            // Right: device list
            FillRect(deviceListRect.X, deviceListRect.Y, deviceListRect.Width, deviceListRect.Height, DeviceListBg);
            RenderDeviceList(appState, deviceListRect, dpiScale, fontPath);

            // Bottom bar
            FillRect(bottomBarRect.X, bottomBarRect.Y, bottomBarRect.Width, bottomBarRect.Height, BottomBarBg);
            RenderBottomBar(appState, bottomBarRect, dpiScale, fontPath);
        }

        // -----------------------------------------------------------------------
        // Left panel: profile summary
        // -----------------------------------------------------------------------

        private void RenderProfilePanel(
            GuiAppState appState,
            RectF32 rect,
            float dpiScale, string fontPath)
        {
            var fontSize   = BaseFontSize * dpiScale;
            var padding    = BasePadding * dpiScale;
            var itemH      = BaseItemHeight * dpiScale;
            var arrowW     = BaseArrowWidth * dpiScale;
            var headerH    = BaseHeaderHeight * dpiScale;
            var buttonH    = BaseButtonHeight * dpiScale;

            var x = rect.X;
            var y = rect.Y;
            var w = rect.Width;
            var h = rect.Height;

            var profile = appState.ActiveProfile!;
            var data = profile.Data;

            var cursor = y + padding;

            // Profile name header
            DrawText(
                $"Profile: {profile.DisplayName}".AsSpan(),
                fontPath,
                x + padding, cursor, w - padding * 2f, headerH,
                fontSize * 1.1f, HeaderText, TextAlign.Near, TextAlign.Center);
            cursor += headerH;

            // Separator
            FillRect(x + padding, cursor, w - padding * 2f, 1f, SeparatorColor);
            cursor += padding;

            if (data is { } pd)
            {
                // Mount slot
                cursor = RenderProfileSlot(
                    "Mount", pd.Mount, new AssignTarget.ProfileLevel("Mount"),
                    x, cursor, w, itemH, dpiScale, fontPath, fontSize, padding, arrowW);

                // Site info — clickable to edit
                var site = EquipmentActions.GetSiteFromMount(pd.Mount ?? NoneDevice.Instance.DeviceUri);
                if (State.IsEditingSite)
                {
                    // Show editable lat/lon/elevation fields
                    var fieldH = (int)(itemH * 1.2f);
                    var fieldW = (int)(w - padding * 2f);
                    var fieldX = (int)(x + padding);

                    DrawText("  Lat:".AsSpan(), fontPath, x + padding, cursor, 50f * dpiScale, itemH, fontSize * 0.85f, DimText, TextAlign.Near, TextAlign.Center);
                    RenderTextInput(State.LatitudeInput, fieldX + (int)(50f * dpiScale), (int)cursor, fieldW - (int)(50f * dpiScale), fieldH, fontPath, fontSize * 0.9f);
                    cursor += fieldH + 2;

                    DrawText("  Lon:".AsSpan(), fontPath, x + padding, cursor, 50f * dpiScale, itemH, fontSize * 0.85f, DimText, TextAlign.Near, TextAlign.Center);
                    RenderTextInput(State.LongitudeInput, fieldX + (int)(50f * dpiScale), (int)cursor, fieldW - (int)(50f * dpiScale), fieldH, fontPath, fontSize * 0.9f);
                    cursor += fieldH + 2;

                    DrawText("  Elev:".AsSpan(), fontPath, x + padding, cursor, 50f * dpiScale, itemH, fontSize * 0.85f, DimText, TextAlign.Near, TextAlign.Center);
                    RenderTextInput(State.ElevationInput, fieldX + (int)(50f * dpiScale), (int)cursor, fieldW - (int)(50f * dpiScale), fieldH, fontPath, fontSize * 0.9f);
                    cursor += fieldH + 2;

                    // Save button
                    var saveBtnW = Renderer.MeasureText("Save Site".AsSpan(), fontPath, fontSize).Width + padding * 4f;
                    RenderButton("Save Site", x + padding, cursor, saveBtnW, buttonH, fontPath, fontSize, CreateButton, BodyText, "SaveSite",
                        () => State.LatitudeInput.OnCommit?.Invoke(State.LatitudeInput.Text));
                    cursor += buttonH + padding;
                }
                else if (site.HasValue)
                {
                    var (lat, lon, elev) = site.Value;
                    var latStr = lat >= 0 ? $"{lat:F1}°N" : $"{-lat:F1}°S";
                    var lonStr = lon >= 0 ? $"{lon:F1}°E" : $"{-lon:F1}°W";
                    var elevStr = elev.HasValue ? $", {elev.Value:F0}m" : "";
                    var siteStr = $"  Site: {latStr}, {lonStr}{elevStr}";

                    var siteBtnW = w - padding * 2f;
                    FillRect(x + padding, cursor, siteBtnW, itemH, SlotNormal);
                    RegisterClickable(x + padding, cursor, siteBtnW, itemH, new HitResult.ButtonHit("EditSite"),
                        () => OnEditSite?.Invoke());
                    DrawText(siteStr.AsSpan(), fontPath, x + padding, cursor, siteBtnW - arrowW, itemH, fontSize * 0.9f, SiteText, TextAlign.Near, TextAlign.Center);
                    DrawText("[>]".AsSpan(), fontPath, x + w - padding - arrowW, cursor, arrowW, itemH, fontSize * 0.85f, DimText, TextAlign.Center, TextAlign.Center);
                    cursor += itemH;
                }
                else
                {
                    // No site configured — show "Set Site" button
                    var setSiteBtnW = Renderer.MeasureText("Set Site".AsSpan(), fontPath, fontSize).Width + padding * 4f;
                    RenderButton("Set Site", x + padding, cursor, setSiteBtnW, buttonH, fontPath, fontSize, CreateButton, BodyText, "EditSite",
                        () => OnEditSite?.Invoke());
                    cursor += buttonH;
                }

                cursor += padding / 2f;

                // Guider slot
                cursor = RenderProfileSlot(
                    "Guider", pd.Guider, new AssignTarget.ProfileLevel("Guider"),
                    x, cursor, w, itemH, dpiScale, fontPath, fontSize, padding, arrowW);

                // Guider Camera slot
                cursor = RenderProfileSlot(
                    "Guider Cam", pd.GuiderCamera, new AssignTarget.ProfileLevel("GuiderCamera"),
                    x, cursor, w, itemH, dpiScale, fontPath, fontSize, padding, arrowW);

                // Guider Focuser slot
                cursor = RenderProfileSlot(
                    "Guider Foc", pd.GuiderFocuser, new AssignTarget.ProfileLevel("GuiderFocuser"),
                    x, cursor, w, itemH, dpiScale, fontPath, fontSize, padding, arrowW);

                cursor += padding;
                FillRect(x + padding, cursor, w - padding * 2f, 1f, SeparatorColor);
                cursor += padding;

                // OTA sections
                for (var i = 0; i < pd.OTAs.Length; i++)
                {
                    var ota = pd.OTAs[i];

                    // OTA header with [Edit] button
                    FillRect(x, cursor, w, itemH, OtaHeaderBg);
                    var isEditingOta = State.EditingOtaIndex == i;
                    var editBtnW = 50f * dpiScale;
                    DrawText(
                        $"Telescope #{i}: {ota.Name}".AsSpan(),
                        fontPath,
                        x + padding, cursor, w - padding * 2f - editBtnW, itemH,
                        fontSize, HeaderText, TextAlign.Near, TextAlign.Center);

                    // [Edit]/[Done] toggle button
                    var editLabel = isEditingOta ? "Done" : "Edit";
                    var capturedI = i;
                    RenderButton(editLabel, x + w - padding - editBtnW, cursor, editBtnW, itemH, fontPath, fontSize * 0.85f, EditButtonBg, BodyText, $"EditOta{i}",
                        () =>
                        {
                            if (isEditingOta)
                            {
                                // Commit OTA property edits
                                if (appState.ActiveProfile is { } prof && prof.Data is { } editData)
                                {
                                    var newName = State.OtaNameInput.Text is { Length: > 0 } n ? n : null;
                                    int? newFl = int.TryParse(State.FocalLengthInput.Text, out var fl) && fl > 0 ? fl : null;
                                    int? newAp = int.TryParse(State.ApertureInput.Text, out var ap) ? ap : null;
                                    var newData = EquipmentActions.UpdateOTA(editData, capturedI,
                                        name: newName, focalLength: newFl, aperture: newAp);
                                    if (OnUpdateProfile is { } update)
                                    {
                                        Tracker?.Run(() => update(newData), "Update OTA properties");
                                    }
                                }
                                State.StopEditingOta();
                            }
                            else
                            {
                                State.BeginEditingOta(capturedI, ota);
                            }
                        });
                    cursor += itemH;

                    // OTA property editors (when editing)
                    if (isEditingOta)
                    {
                        cursor = RenderOtaPropertyEditors(appState, i, ota, x, cursor, w, itemH, dpiScale, fontPath, fontSize, padding, buttonH);
                    }
                    else
                    {
                        // Show OTA properties summary
                        cursor = RenderOtaPropertiesSummary(ota, x, cursor, w, itemH, fontPath, fontSize, padding);
                    }

                    // OTA sub-slots
                    cursor = RenderProfileSlot(
                        "  Camera", ota.Camera, new AssignTarget.OTALevel(i, "Camera"),
                        x, cursor, w, itemH, dpiScale, fontPath, fontSize, padding, arrowW);
                    cursor = RenderProfileSlot(
                        "  Focuser", ota.Focuser, new AssignTarget.OTALevel(i, "Focuser"),
                        x, cursor, w, itemH, dpiScale, fontPath, fontSize, padding, arrowW);
                    cursor = RenderProfileSlot(
                        "  Filter Wheel", ota.FilterWheel, new AssignTarget.OTALevel(i, "FilterWheel"),
                        x, cursor, w, itemH, dpiScale, fontPath, fontSize, padding, arrowW);

                    // Filter table (shown when a filter wheel is assigned)
                    if (ota.FilterWheel is not null && ota.FilterWheel != NoneDevice.Instance.DeviceUri)
                    {
                        cursor = RenderFilterTable(appState, i, pd, x, cursor, w, itemH, dpiScale, fontPath, fontSize, padding, buttonH);
                    }

                    cursor = RenderProfileSlot(
                        "  Cover", ota.Cover, new AssignTarget.OTALevel(i, "Cover"),
                        x, cursor, w, itemH, dpiScale, fontPath, fontSize, padding, arrowW);

                    cursor += padding / 2f;
                }

                // [+ Add OTA] button — only if there is room
                var addOtaBtnY = cursor;
                var addOtaBtnW = 120f * dpiScale;
                if (addOtaBtnY + buttonH < y + h - padding)
                {
                    RenderButton("+ Add OTA", x + padding, addOtaBtnY, addOtaBtnW, buttonH, fontPath, fontSize, CreateButton, BodyText, "AddOta",
                        () => { if (OnAddOta is { } addOta) Tracker?.Run(addOta, "Add OTA"); });
                }
            }
            else
            {
                DrawText(
                    "Profile has no data.".AsSpan(),
                    fontPath,
                    x + padding, cursor, w - padding * 2f, itemH,
                    fontSize, DimText, TextAlign.Near, TextAlign.Center);
            }
        }

        /// <summary>
        /// Renders a single profile slot row. Returns the new cursor Y.
        /// </summary>
        private float RenderProfileSlot(
            string label,
            Uri? deviceUri,
            AssignTarget slot,
            float x, float y, float w, float itemH,
            float dpiScale, string fontPath,
            float fontSize, float padding, float arrowW)
        {
            var isActive = State.ActiveAssignment == slot;
            var bgColor = isActive ? SlotActive : SlotNormal;

            FillRect(x, y, w, itemH, bgColor);
            var capturedSlot = slot;
            RegisterClickable(x, y, w, itemH, new HitResult.SlotHit<AssignTarget>(slot),
                () => { State.ActiveAssignment = State.ActiveAssignment == capturedSlot ? null : capturedSlot; });

            // Separator line at bottom of slot
            FillRect(x, y + itemH - 1f, w, 1f, SeparatorColor);

            // Label column (~35% of width)
            var labelW = w * 0.35f;
            DrawText(
                label.AsSpan(),
                fontPath,
                x + padding, y, labelW, itemH,
                fontSize * 0.9f, DimText, TextAlign.Near, TextAlign.Center);

            // Device name (fills remaining space minus arrow button)
            var deviceLabel = EquipmentActions.DeviceLabel(deviceUri, registry: null);
            var nameX = x + labelW;
            var nameW = w - labelW - arrowW - padding;
            DrawText(
                deviceLabel.AsSpan(),
                fontPath,
                nameX, y, nameW, itemH,
                fontSize, isActive ? BodyText : BodyText, TextAlign.Near, TextAlign.Center);

            // [>] assignment indicator on the right
            var arrowX = x + w - arrowW;
            var arrowColor = isActive ? AccentInstruct : DimText;
            DrawText(
                ">".AsSpan(),
                fontPath,
                arrowX, y, arrowW - padding / 2f, itemH,
                fontSize, arrowColor, TextAlign.Center, TextAlign.Center);

            return y + itemH;
        }

        // -----------------------------------------------------------------------
        // Right panel: device list
        // -----------------------------------------------------------------------

        private void RenderDeviceList(
            GuiAppState appState,
            RectF32 rect,
            float dpiScale, string fontPath)
        {
            var fontSize   = BaseFontSize * dpiScale;
            var padding    = BasePadding * dpiScale;
            var itemH      = BaseItemHeight * dpiScale;
            var headerH    = BaseHeaderHeight * dpiScale;
            var badgeW     = BaseBadgeWidth * dpiScale;
            var checkW     = BaseCheckmarkWidth * dpiScale;
            var buttonH    = BaseButtonHeight * dpiScale;

            var x = rect.X;
            var y = rect.Y;
            var w = rect.Width;
            var h = rect.Height;

            // Header
            DrawText(
                "Discovered Devices".AsSpan(),
                fontPath,
                x + padding, y, w - padding * 2f, headerH,
                fontSize * 1.05f, HeaderText, TextAlign.Near, TextAlign.Center);

            FillRect(x + padding, y + headerH - 1f, w - padding * 2f, 1f, SeparatorColor);

            var listTop  = y + headerH + padding / 2f;
            var listH    = h - headerH - padding - buttonH - padding;

            var devices  = State.DiscoveredDevices;
            var data     = appState.ActiveProfile?.Data;

            var expectedType = State.ActiveAssignment?.ExpectedDeviceType;
            // URI of the device currently assigned to the active slot (for highlighting)
            var activeSlotUri = State.ActiveAssignment is { } activeSlot && data is { } slotData
                ? EquipmentActions.GetAssignedDevice(slotData, activeSlot)
                : null;

            for (var i = State.DeviceScrollOffset; i < devices.Count; i++)
            {
                var rowY = listTop + (i - State.DeviceScrollOffset) * itemH;
                if (rowY + itemH > y + listH)
                {
                    break;
                }

                var device  = devices[i];
                var isAssigned = data is { } assignData && EquipmentActions.IsDeviceAssigned(assignData, device.DeviceUri);
                var isWrongType = expectedType.HasValue && device.DeviceType != expectedType.Value;
                // Highlight the device currently in the active slot
                var isCurrentForSlot = DeviceBase.SameDevice(device.DeviceUri, activeSlotUri);

                // Row background
                FillRect(x, rowY, w, itemH, isCurrentForSlot ? SlotActive : DeviceRowBg);
                var capturedIdx = i;
                RegisterClickable(x, rowY, w, itemH, new HitResult.ListItemHit("Devices", i),
                    () => { if (OnAssignDevice is { } assign) Tracker?.Run(() => assign(capturedIdx), "Assign device"); });
                FillRect(x, rowY + itemH - 1f, w, 1f, SeparatorColor);

                // Type badge
                var badgeText = DeviceTypeBadge(device.DeviceType);
                var textColor = isWrongType ? DimmedText : BodyText;

                FillRect(x + padding, rowY + itemH * 0.15f, badgeW - padding, itemH * 0.7f, BadgeBg);
                DrawText(
                    badgeText,
                    fontPath,
                    x + padding, rowY, badgeW, itemH,
                    fontSize * 0.8f, isWrongType ? DimmedText : HeaderText, TextAlign.Center, TextAlign.Center);

                // Device name
                var nameX = x + badgeW + padding;
                var nameW = w - badgeW - padding * 2f - checkW;
                DrawText(
                    device.DisplayName.AsSpan(),
                    fontPath,
                    nameX, rowY, nameW, itemH,
                    fontSize, textColor, TextAlign.Near, TextAlign.Center);

                // Assigned checkmark
                if (isAssigned)
                {
                    DrawText(
                        "\u2713".AsSpan(),
                        fontPath,
                        x + w - checkW - padding, rowY, checkW, itemH,
                        fontSize, AssignedGreen, TextAlign.Center, TextAlign.Center);
                }
            }

            // [Discover] button at the bottom of the list area
            var discoverBtnY = y + h - buttonH - padding;
            var discoverLabel = State.IsDiscovering ? "Discovering..." : "Discover";
            var discoverBtnW = Renderer.MeasureText("Discovering...".AsSpan(), fontPath, fontSize).Width + padding * 4f;
            var discoverBtnX = x + padding;

            RenderButton(discoverLabel, discoverBtnX, discoverBtnY, discoverBtnW, buttonH, fontPath, fontSize, CreateButton, BodyText, "Discover",
                () => { if (OnDiscover is { } discover) Tracker?.Run(discover, "Discover devices"); });
        }

        // -----------------------------------------------------------------------
        // Bottom bar
        // -----------------------------------------------------------------------

        private void RenderBottomBar(
            GuiAppState appState,
            RectF32 rect,
            float dpiScale, string fontPath)
        {
            var fontSize = BaseFontSize * dpiScale;
            var padding  = BasePadding * dpiScale;

            var x = rect.X;
            var y = rect.Y;
            var w = rect.Width;
            var h = rect.Height;

            string message;
            RGBAColor32 textColor;

            if (State.ActiveAssignment is not null)
            {
                var typeName = State.ActiveAssignment.ExpectedDeviceType.ToString();
                message   = $"Select a {typeName} device from the list on the right, or click the slot again to cancel.";
                textColor = AccentInstruct;
            }
            else if (appState.StatusMessage is { Length: > 0 } status)
            {
                message   = status;
                textColor = BodyText;
            }
            else
            {
                message   = "Click a [>] slot to assign a device. Click [Discover] to scan for new devices.";
                textColor = DimText;
            }

            DrawText(
                message.AsSpan(),
                fontPath,
                x + padding, y, w - padding * 2f, h,
                fontSize * 0.9f, textColor, TextAlign.Near, TextAlign.Center);
        }

        // -----------------------------------------------------------------------
        // OTA property editors
        // -----------------------------------------------------------------------

        private float RenderOtaPropertiesSummary(
            OTAData ota,
            float x, float cursor, float w, float itemH,
            string fontPath, float fontSize, float padding)
        {
            var fRatio = ota.Aperture is > 0 ? $"f/{(double)ota.FocalLength / ota.Aperture.Value:F1}" : "";
            var designStr = ota.OpticalDesign != OpticalDesign.Unknown ? ota.OpticalDesign.ToString() : "";
            var parts = new List<string>(3);
            if (ota.FocalLength > 0) parts.Add($"{ota.FocalLength}mm");
            if (ota.Aperture is > 0) parts.Add($"\u00d8{ota.Aperture}mm");
            if (fRatio.Length > 0) parts.Add(fRatio);
            if (designStr.Length > 0) parts.Add(designStr);

            if (parts.Count > 0)
            {
                var summary = string.Join("  ", parts);
                DrawText(
                    summary.AsSpan(),
                    fontPath,
                    x + padding * 2f, cursor, w - padding * 3f, itemH * 0.8f,
                    fontSize * 0.8f, DimText, TextAlign.Near, TextAlign.Center);
                cursor += itemH * 0.8f;
            }

            return cursor;
        }

        private float RenderOtaPropertyEditors(
            GuiAppState appState,
            int otaIndex, OTAData ota,
            float x, float cursor, float w, float itemH,
            float dpiScale, string fontPath, float fontSize, float padding, float buttonH)
        {
            var fieldH = (int)(itemH * 1.1f);
            var labelW = 80f * dpiScale;
            var fieldW = (int)(w - padding * 2f - labelW);
            var fieldX = (int)(x + padding + labelW);

            // Name
            DrawText("  Name:".AsSpan(), fontPath, x + padding, cursor, labelW, itemH, fontSize * 0.85f, DimText, TextAlign.Near, TextAlign.Center);
            RenderTextInput(State.OtaNameInput, fieldX, (int)cursor, fieldW, fieldH, fontPath, fontSize * 0.9f);
            cursor += fieldH + 2;

            // Focal length
            DrawText("  FL (mm):".AsSpan(), fontPath, x + padding, cursor, labelW, itemH, fontSize * 0.85f, DimText, TextAlign.Near, TextAlign.Center);
            RenderTextInput(State.FocalLengthInput, fieldX, (int)cursor, fieldW, fieldH, fontPath, fontSize * 0.9f);
            cursor += fieldH + 2;

            // Aperture
            DrawText("  Aper (mm):".AsSpan(), fontPath, x + padding, cursor, labelW, itemH, fontSize * 0.85f, DimText, TextAlign.Near, TextAlign.Center);
            RenderTextInput(State.ApertureInput, fieldX, (int)cursor, fieldW, fieldH, fontPath, fontSize * 0.9f);
            cursor += fieldH + 2;

            // Optical design cycle button
            var designLabel = $"  Design: {ota.OpticalDesign}";
            var designBtnW = Renderer.MeasureText(designLabel.AsSpan(), fontPath, fontSize * 0.9f).Width + padding * 4f;
            var capturedIdx = otaIndex;
            RenderButton(designLabel, x + padding, cursor, designBtnW, buttonH, fontPath, fontSize * 0.9f, EditButtonBg, BodyText, $"CycleDesign{otaIndex}",
                () =>
                {
                    if (appState.ActiveProfile is { } prof && prof.Data is { } data)
                    {
                        var currentOta = data.OTAs[capturedIdx];
                        var nextDesign = (OpticalDesign)(((int)currentOta.OpticalDesign + 1) % 8);
                        var newData = EquipmentActions.UpdateOTA(data, capturedIdx, opticalDesign: nextDesign);
                        if (OnUpdateProfile is { } update)
                        {
                            Tracker?.Run(() => update(newData), "Cycle optical design");
                        }
                    }
                });
            cursor += buttonH + padding;

            return cursor;
        }

        // -----------------------------------------------------------------------
        // Filter table
        // -----------------------------------------------------------------------

        private float RenderFilterTable(
            GuiAppState appState,
            int otaIndex, ProfileData pd,
            float x, float cursor, float w, float itemH,
            float dpiScale, string fontPath, float fontSize, float padding, float buttonH)
        {
            var filters = EquipmentActions.GetFilterConfig(pd, otaIndex);
            var isExpanded = State.ExpandedFilterOtaIndex == otaIndex;
            var rowH = itemH * 0.85f;
            var capturedOtaIdx = otaIndex;

            // Toggle header
            var headerLabel = isExpanded
                ? $"    Filters ({filters.Count}) [-]"
                : $"    Filters ({filters.Count}) [+]";
            FillRect(x + padding, cursor, w - padding * 2f, rowH, FilterTableBg);
            RegisterClickable(x + padding, cursor, w - padding * 2f, rowH, new HitResult.ButtonHit($"ToggleFilters{otaIndex}"),
                () => { State.ExpandedFilterOtaIndex = isExpanded ? -1 : capturedOtaIdx; });
            DrawText(
                headerLabel.AsSpan(),
                fontPath,
                x + padding * 2f, cursor, w - padding * 4f, rowH,
                fontSize * 0.85f, HeaderText, TextAlign.Near, TextAlign.Center);
            cursor += rowH;

            if (!isExpanded)
            {
                return cursor;
            }

            // Column headers
            var nameColW = (w - padding * 4f) * 0.55f;
            var offsetColW = (w - padding * 4f) * 0.3f;
            var btnColW = (w - padding * 4f) * 0.15f;

            FillRect(x + padding, cursor, w - padding * 2f, rowH, FilterTableBg);
            DrawText("#".AsSpan(), fontPath, x + padding * 2f, cursor, padding * 2f, rowH, fontSize * 0.75f, DimText, TextAlign.Near, TextAlign.Center);
            DrawText("Name".AsSpan(), fontPath, x + padding * 4f, cursor, nameColW, rowH, fontSize * 0.75f, DimText, TextAlign.Near, TextAlign.Center);
            DrawText("Offset".AsSpan(), fontPath, x + padding * 4f + nameColW, cursor, offsetColW, rowH, fontSize * 0.75f, DimText, TextAlign.Near, TextAlign.Center);
            cursor += rowH;

            // Filter rows
            for (var f = 0; f < filters.Count; f++)
            {
                var filter = filters[f];
                var rowBg = f % 2 == 0 ? FilterTableBg : FilterRowAlt;
                FillRect(x + padding, cursor, w - padding * 2f, rowH, rowBg);

                // Slot number
                DrawText(
                    (f + 1).ToString().AsSpan(),
                    fontPath,
                    x + padding * 2f, cursor, padding * 2f, rowH,
                    fontSize * 0.8f, DimText, TextAlign.Near, TextAlign.Center);

                // Filter name (clickable to edit)
                var capturedF = f;
                var filterNameBtnW = nameColW;
                RenderButton(filter.Filter.Name, x + padding * 4f, cursor, filterNameBtnW, rowH, fontPath, fontSize * 0.8f, rowBg, BodyText, $"FilterName{otaIndex}_{f}",
                    () =>
                    {
                        if (appState.ActiveProfile is { } prof && prof.Data is { } data)
                        {
                            var currentFilters = new List<InstalledFilter>(EquipmentActions.GetFilterConfig(data, capturedOtaIdx));
                            if (capturedF < currentFilters.Count)
                            {
                                // Cycle through common filter names
                                var nextName = CycleFilterName(currentFilters[capturedF].Filter.Name);
                                currentFilters[capturedF] = new InstalledFilter(nextName, currentFilters[capturedF].Position);
                                var newData = EquipmentActions.SetFilterConfig(data, capturedOtaIdx, currentFilters);
                                if (OnUpdateProfile is { } update)
                                {
                                    Tracker?.Run(() => update(newData), "Cycle filter name");
                                }
                            }
                        }
                    });

                // Offset (clickable to step)
                var offsetStr = filter.Position >= 0 ? $"+{filter.Position}" : filter.Position.ToString();
                var offsetBtnW = offsetColW / 2f;

                // [-] button
                RenderButton("-", x + padding * 4f + nameColW, cursor, offsetBtnW * 0.4f, rowH, fontPath, fontSize * 0.8f, EditButtonBg, BodyText, $"FilterOffDec{otaIndex}_{f}",
                    () => StepFilterOffset(appState, capturedOtaIdx, capturedF, -1));

                // Offset value
                DrawText(
                    offsetStr.AsSpan(),
                    fontPath,
                    x + padding * 4f + nameColW + offsetBtnW * 0.4f, cursor, offsetBtnW * 1.2f, rowH,
                    fontSize * 0.8f, BodyText, TextAlign.Center, TextAlign.Center);

                // [+] button
                RenderButton("+", x + padding * 4f + nameColW + offsetBtnW * 1.6f, cursor, offsetBtnW * 0.4f, rowH, fontPath, fontSize * 0.8f, EditButtonBg, BodyText, $"FilterOffInc{otaIndex}_{f}",
                    () => StepFilterOffset(appState, capturedOtaIdx, capturedF, +1));

                // [x] remove button
                RenderButton("x", x + w - padding * 2f - btnColW, cursor, btnColW, rowH, fontPath, fontSize * 0.8f, RemoveButtonBg, BodyText, $"FilterDel{otaIndex}_{f}",
                    () =>
                    {
                        if (appState.ActiveProfile is { } prof && prof.Data is { } data)
                        {
                            var currentFilters = new List<InstalledFilter>(EquipmentActions.GetFilterConfig(data, capturedOtaIdx));
                            if (capturedF < currentFilters.Count)
                            {
                                currentFilters.RemoveAt(capturedF);
                                var newData = EquipmentActions.SetFilterConfig(data, capturedOtaIdx, currentFilters);
                                if (OnUpdateProfile is { } update)
                                {
                                    Tracker?.Run(() => update(newData), "Remove filter slot");
                                }
                            }
                        }
                    });

                cursor += rowH;
            }

            // [+ Add Filter] button (capped at MaxFilterSlots)
            if (filters.Count < MaxFilterSlots)
            {
                var addBtnW = Renderer.MeasureText("+ Add Filter".AsSpan(), fontPath, fontSize * 0.85f).Width + padding * 3f;
                RenderButton("+ Add Filter", x + padding * 2f, cursor, addBtnW, buttonH * 0.85f, fontPath, fontSize * 0.85f, CreateButton, BodyText, $"AddFilter{otaIndex}",
                    () =>
                    {
                        if (appState.ActiveProfile is { } prof && prof.Data is { } data)
                        {
                            var currentFilters = new List<InstalledFilter>(EquipmentActions.GetFilterConfig(data, capturedOtaIdx));
                            if (currentFilters.Count < MaxFilterSlots)
                            {
                                currentFilters.Add(new InstalledFilter($"Filter {currentFilters.Count + 1}"));
                                var newData = EquipmentActions.SetFilterConfig(data, capturedOtaIdx, currentFilters);
                                if (OnUpdateProfile is { } update)
                                {
                                    Tracker?.Run(() => update(newData), "Add filter slot");
                                }
                            }
                        }
                    });
                cursor += buttonH * 0.85f + padding / 2f;
            }

            return cursor;
        }

        private void StepFilterOffset(GuiAppState appState, int otaIndex, int filterIndex, int delta)
        {
            if (appState.ActiveProfile is { } prof && prof.Data is { } data)
            {
                var currentFilters = new List<InstalledFilter>(EquipmentActions.GetFilterConfig(data, otaIndex));
                if (filterIndex < currentFilters.Count)
                {
                    var f = currentFilters[filterIndex];
                    currentFilters[filterIndex] = new InstalledFilter(f.Filter.Name, f.Position + delta);
                    var newData = EquipmentActions.SetFilterConfig(data, otaIndex, currentFilters);
                    if (OnUpdateProfile is { } update)
                    {
                        Tracker?.Run(() => update(newData), "Step filter offset");
                    }
                }
            }
        }

        private static readonly string[] CommonFilterNames =
        [
            "Luminance", "Red", "Green", "Blue",
            "H-Alpha", "OIII", "SII", "H-Beta",
            "H-Alpha + OIII", "UV/IR Cut", "Clear"
        ];

        private static string CycleFilterName(string current)
        {
            for (var i = 0; i < CommonFilterNames.Length; i++)
            {
                if (string.Equals(CommonFilterNames[i], current, StringComparison.OrdinalIgnoreCase))
                {
                    return CommonFilterNames[(i + 1) % CommonFilterNames.Length];
                }
            }
            return CommonFilterNames[0];
        }

        // -----------------------------------------------------------------------
        // Badge helpers
        // -----------------------------------------------------------------------

        private static ReadOnlySpan<char> DeviceTypeBadge(DeviceType type) =>
            type switch
            {
                DeviceType.Camera         => "Camera",
                DeviceType.Mount          => "Mount",
                DeviceType.Focuser        => "Focuser",
                DeviceType.FilterWheel    => "FW",
                DeviceType.CoverCalibrator=> "Cover",
                DeviceType.Guider         => "Guider",
                DeviceType.Profile        => "Profile",
                _                         => "?"
            };
    }
}
