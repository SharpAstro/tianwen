using System;
using System.Collections.Generic;
using DIR.Lib;
using SdlVulkan.Renderer;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;

namespace TianWen.UI.Gui
{
    /// <summary>
    /// Identifies the kind of region that was hit during a click test.
    /// </summary>
    public enum EquipmentHitType
    {
        ProfileSlot,
        DeviceRow,
        CreateButton,
        AddOtaButton,
        DiscoverButton,
        TextInput,
        EditSiteButton,
        SaveSiteButton
    }

    /// <summary>
    /// Describes what was hit during <see cref="VkEquipmentTab.HitTest"/>.
    /// </summary>
    public record EquipmentHitResult(EquipmentHitType Type, AssignTarget? Slot = null, int DeviceIndex = -1, TextInputState? TextInput = null);

    /// <summary>
    /// Renders the Equipment / Profile tab for the TianWen N.I.N.A.-style GUI.
    /// Handles profile summary, discovered devices, and device assignment.
    /// </summary>
    public sealed class VkEquipmentTab
    {
        private readonly VkRenderer _renderer;
        private readonly List<(float X, float Y, float W, float H, EquipmentHitResult Result)> _clickableRegions = [];

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

        /// <summary>Tab state (scroll offsets, discovery results, assignment mode).</summary>
        public EquipmentTabState State { get; } = new EquipmentTabState();

        /// <summary>
        /// Registers a clickable region during rendering. Hit testing walks this list.
        /// </summary>
        private void RegisterClickable(float x, float y, float w, float h, EquipmentHitResult result)
        {
            _clickableRegions.Add((x, y, w, h, result));
        }

        /// <summary>Frame counter for text input cursor blink.</summary>
        public long FrameCount { get; set; }

        public VkEquipmentTab(VkRenderer renderer)
        {
            _renderer = renderer;
        }

        // -----------------------------------------------------------------------
        // Public entry points
        // -----------------------------------------------------------------------

        /// <summary>
        /// Renders the Equipment tab into the given content area.
        /// </summary>
        public void Render(
            GuiAppState appState,
            float left, float top, float width, float height,
            float dpiScale,
            string fontPath)
        {
            // Clear clickable regions from previous frame
            _clickableRegions.Clear();

            // Clear the whole content area first
            FillRect(left, top, width, height, ContentBg);

            if (appState.ActiveProfile is null && !State.IsCreatingProfile)
            {
                RenderNoProfile(left, top, width, height, dpiScale, fontPath);
                return;
            }

            if (State.IsCreatingProfile)
            {
                RenderProfileCreation(left, top, width, height, dpiScale, fontPath);
                return;
            }

            RenderProfileView(appState, left, top, width, height, dpiScale, fontPath);
        }

        // -----------------------------------------------------------------------
        // No-profile view
        // -----------------------------------------------------------------------

        private void RenderNoProfile(
            float left, float top, float width, float height,
            float dpiScale, string fontPath)
        {
            var fontSize    = BaseFontSize * dpiScale;
            var padding     = BasePadding * dpiScale;
            var buttonH     = BaseButtonHeight * dpiScale;
            var buttonW     = 160f * dpiScale;

            var centerX = left + width / 2f;
            var centerY = top + height / 2f;

            DrawText(
                "No equipment profile configured.".AsSpan(),
                fontPath,
                left, top, width, height - buttonH - padding * 2f,
                fontSize, DimText, TextAlign.Center, TextAlign.Far);

            var btnX = centerX - buttonW / 2f;
            var btnY = centerY + padding;

            FillRect(btnX, btnY, buttonW, buttonH, CreateButton);
            RegisterClickable(btnX, btnY, buttonW, buttonH, new EquipmentHitResult(EquipmentHitType.CreateButton));
            DrawText(
                "Create Profile".AsSpan(),
                fontPath,
                btnX, btnY, buttonW, buttonH,
                fontSize, BodyText, TextAlign.Center, TextAlign.Center);
        }

        // -----------------------------------------------------------------------
        // Profile creation view
        // -----------------------------------------------------------------------

        private void RenderProfileCreation(
            float left, float top, float width, float height,
            float dpiScale, string fontPath)
        {
            var fontSize    = BaseFontSize * dpiScale;
            var padding     = BasePadding * dpiScale;
            var fieldH      = BaseItemHeight * dpiScale * 1.4f;
            var buttonH     = BaseButtonHeight * dpiScale;
            var bottomBarH  = BaseBottomBarHeight * dpiScale;

            // Header
            DrawText(
                "New Equipment Profile".AsSpan(),
                fontPath,
                left + padding, top + padding, width - padding * 2f, BaseHeaderHeight * dpiScale,
                fontSize * 1.2f, HeaderText, TextAlign.Near, TextAlign.Center);

            // Name input field
            var fieldY = top + padding + BaseHeaderHeight * dpiScale + padding;
            var fieldW = Math.Min(360f * dpiScale, width - padding * 2f);
            var fieldX = left + padding;

            DrawText(
                "Profile name:".AsSpan(),
                fontPath,
                fieldX, fieldY, fieldW, fontSize * 1.5f,
                fontSize, BodyText, TextAlign.Near, TextAlign.Near);

            var inputY = (int)(fieldY + fontSize * 1.6f);
            TextInputRenderer.Render(
                _renderer,
                State.ProfileNameInput,
                (int)fieldX, inputY, (int)fieldW, (int)fieldH,
                fontPath, fontSize, FrameCount);
            RegisterClickable(fieldX, inputY, fieldW, fieldH, new EquipmentHitResult(EquipmentHitType.TextInput, TextInput: State.ProfileNameInput));

            // Create button
            var btnY = inputY + (int)fieldH + (int)padding;
            var btnW = 120f * dpiScale;
            FillRect(fieldX, btnY, btnW, buttonH, CreateButton);
            RegisterClickable(fieldX, btnY, btnW, buttonH, new EquipmentHitResult(EquipmentHitType.CreateButton));
            DrawText(
                "Create".AsSpan(),
                fontPath,
                fieldX, btnY, btnW, buttonH,
                fontSize, BodyText, TextAlign.Center, TextAlign.Center);

            // Bottom status
            var statusY = top + height - bottomBarH;
            FillRect(left, statusY, width, bottomBarH, BottomBarBg);
            DrawText(
                "Enter a name for the new profile and press Create or Enter.".AsSpan(),
                fontPath,
                left + padding, statusY, width - padding * 2f, bottomBarH,
                fontSize, DimText, TextAlign.Near, TextAlign.Center);
        }

        // -----------------------------------------------------------------------
        // Full profile view
        // -----------------------------------------------------------------------

        private void RenderProfileView(
            GuiAppState appState,
            float left, float top, float width, float height,
            float dpiScale, string fontPath)
        {
            var profilePanelW = BaseProfilePanelWidth * dpiScale;
            var bottomBarH    = BaseBottomBarHeight * dpiScale;

            var mainH         = height - bottomBarH;
            var deviceListLeft = left + profilePanelW;
            var deviceListW   = width - profilePanelW;

            // Left: profile panel
            FillRect(left, top, profilePanelW, mainH, ProfilePanelBg);
            RenderProfilePanel(appState, left, top, profilePanelW, mainH, dpiScale, fontPath);

            // Vertical separator
            FillRect(deviceListLeft, top, 1f, mainH, SeparatorColor);

            // Right: device list
            FillRect(deviceListLeft, top, deviceListW, mainH, DeviceListBg);
            RenderDeviceList(appState, deviceListLeft, top, deviceListW, mainH, dpiScale, fontPath);

            // Bottom bar
            var statusY = top + mainH;
            FillRect(left, statusY, width, bottomBarH, BottomBarBg);
            RenderBottomBar(appState, left, statusY, width, bottomBarH, dpiScale, fontPath);
        }

        // -----------------------------------------------------------------------
        // Left panel: profile summary
        // -----------------------------------------------------------------------

        private void RenderProfilePanel(
            GuiAppState appState,
            float x, float y, float w, float h,
            float dpiScale, string fontPath)
        {
            var fontSize   = BaseFontSize * dpiScale;
            var padding    = BasePadding * dpiScale;
            var itemH      = BaseItemHeight * dpiScale;
            var arrowW     = BaseArrowWidth * dpiScale;
            var headerH    = BaseHeaderHeight * dpiScale;
            var buttonH    = BaseButtonHeight * dpiScale;

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
                    TextInputRenderer.Render(_renderer, State.LatitudeInput, fieldX + (int)(50f * dpiScale), (int)cursor, fieldW - (int)(50f * dpiScale), fieldH, fontPath, fontSize * 0.9f, FrameCount);
                    RegisterClickable(fieldX + 50f * dpiScale, cursor, fieldW - 50f * dpiScale, fieldH, new EquipmentHitResult(EquipmentHitType.TextInput, TextInput: State.LatitudeInput));
                    cursor += fieldH + 2;

                    DrawText("  Lon:".AsSpan(), fontPath, x + padding, cursor, 50f * dpiScale, itemH, fontSize * 0.85f, DimText, TextAlign.Near, TextAlign.Center);
                    TextInputRenderer.Render(_renderer, State.LongitudeInput, fieldX + (int)(50f * dpiScale), (int)cursor, fieldW - (int)(50f * dpiScale), fieldH, fontPath, fontSize * 0.9f, FrameCount);
                    RegisterClickable(fieldX + 50f * dpiScale, cursor, fieldW - 50f * dpiScale, fieldH, new EquipmentHitResult(EquipmentHitType.TextInput, TextInput: State.LongitudeInput));
                    cursor += fieldH + 2;

                    DrawText("  Elev:".AsSpan(), fontPath, x + padding, cursor, 50f * dpiScale, itemH, fontSize * 0.85f, DimText, TextAlign.Near, TextAlign.Center);
                    TextInputRenderer.Render(_renderer, State.ElevationInput, fieldX + (int)(50f * dpiScale), (int)cursor, fieldW - (int)(50f * dpiScale), fieldH, fontPath, fontSize * 0.9f, FrameCount);
                    RegisterClickable(fieldX + 50f * dpiScale, cursor, fieldW - 50f * dpiScale, fieldH, new EquipmentHitResult(EquipmentHitType.TextInput, TextInput: State.ElevationInput));
                    cursor += fieldH + 2;

                    // Save button
                    var saveBtnW = _renderer.MeasureText("Save Site".AsSpan(), fontPath, fontSize).Width + padding * 4f;
                    FillRect(x + padding, cursor, saveBtnW, buttonH, CreateButton);
                    RegisterClickable(x + padding, cursor, saveBtnW, buttonH, new EquipmentHitResult(EquipmentHitType.SaveSiteButton));
                    DrawText("Save Site".AsSpan(), fontPath, x + padding, cursor, saveBtnW, buttonH, fontSize, BodyText, TextAlign.Center, TextAlign.Center);
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
                    RegisterClickable(x + padding, cursor, siteBtnW, itemH, new EquipmentHitResult(EquipmentHitType.EditSiteButton));
                    DrawText(siteStr.AsSpan(), fontPath, x + padding, cursor, siteBtnW - arrowW, itemH, fontSize * 0.9f, SiteText, TextAlign.Near, TextAlign.Center);
                    DrawText("[>]".AsSpan(), fontPath, x + w - padding - arrowW, cursor, arrowW, itemH, fontSize * 0.85f, DimText, TextAlign.Center, TextAlign.Center);
                    cursor += itemH;
                }
                else
                {
                    // No site configured — show "Set Site" button
                    var setSiteBtnW = _renderer.MeasureText("Set Site".AsSpan(), fontPath, fontSize).Width + padding * 4f;
                    FillRect(x + padding, cursor, setSiteBtnW, buttonH, CreateButton);
                    RegisterClickable(x + padding, cursor, setSiteBtnW, buttonH, new EquipmentHitResult(EquipmentHitType.EditSiteButton));
                    DrawText("Set Site".AsSpan(), fontPath, x + padding, cursor, setSiteBtnW, buttonH, fontSize, BodyText, TextAlign.Center, TextAlign.Center);
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

                    // OTA header
                    FillRect(x, cursor, w, itemH, OtaHeaderBg);
                    DrawText(
                        $"Telescope #{i}: {ota.Name}".AsSpan(),
                        fontPath,
                        x + padding, cursor, w - padding * 2f, itemH,
                        fontSize, HeaderText, TextAlign.Near, TextAlign.Center);
                    cursor += itemH;

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
                    FillRect(x + padding, addOtaBtnY, addOtaBtnW, buttonH, CreateButton);
                    RegisterClickable(x + padding, addOtaBtnY, addOtaBtnW, buttonH, new EquipmentHitResult(EquipmentHitType.AddOtaButton));
                    DrawText(
                        "+ Add OTA".AsSpan(),
                        fontPath,
                        x + padding, addOtaBtnY, addOtaBtnW, buttonH,
                        fontSize, BodyText, TextAlign.Center, TextAlign.Center);
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
            RegisterClickable(x, y, w, itemH, new EquipmentHitResult(EquipmentHitType.ProfileSlot, slot));

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
            float x, float y, float w, float h,
            float dpiScale, string fontPath)
        {
            var fontSize   = BaseFontSize * dpiScale;
            var padding    = BasePadding * dpiScale;
            var itemH      = BaseItemHeight * dpiScale;
            var headerH    = BaseHeaderHeight * dpiScale;
            var badgeW     = BaseBadgeWidth * dpiScale;
            var checkW     = BaseCheckmarkWidth * dpiScale;
            var buttonH    = BaseButtonHeight * dpiScale;

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

                // Row background
                FillRect(x, rowY, w, itemH, DeviceRowBg);
                RegisterClickable(x, rowY, w, itemH, new EquipmentHitResult(EquipmentHitType.DeviceRow, DeviceIndex: i));
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
            var discoverBtnW = _renderer.MeasureText("Discovering...".AsSpan(), fontPath, fontSize).Width + padding * 4f;
            var discoverBtnX = x + padding;

            FillRect(discoverBtnX, discoverBtnY, discoverBtnW, buttonH, CreateButton);
            RegisterClickable(discoverBtnX, discoverBtnY, discoverBtnW, buttonH, new EquipmentHitResult(EquipmentHitType.DiscoverButton));
            DrawText(
                discoverLabel.AsSpan(),
                fontPath,
                discoverBtnX, discoverBtnY, discoverBtnW, buttonH,
                fontSize, BodyText, TextAlign.Center, TextAlign.Center);
        }

        // -----------------------------------------------------------------------
        // Bottom bar
        // -----------------------------------------------------------------------

        private void RenderBottomBar(
            GuiAppState appState,
            float x, float y, float w, float h,
            float dpiScale, string fontPath)
        {
            var fontSize = BaseFontSize * dpiScale;
            var padding  = BasePadding * dpiScale;

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
        // Hit testing
        // -----------------------------------------------------------------------

        /// <summary>
        /// Hit-tests the Equipment tab content area using regions registered during the last Render call.
        /// </summary>
        public EquipmentHitResult? HitTest(float x, float y, float contentLeft, float contentTop, float dpiScale)
        {
            // Walk registered clickable regions from last render — single source of truth
            foreach (var (rx, ry, rw, rh, result) in _clickableRegions)
            {
                if (x >= rx && x < rx + rw && y >= ry && y < ry + rh)
                {
                    return result;
                }
            }

            return null;
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

        // -----------------------------------------------------------------------
        // Drawing helpers (mirror VkPlannerTab pattern)
        // -----------------------------------------------------------------------

        private void FillRect(float x, float y, float w, float h, RGBAColor32 color)
        {
            if (w <= 0 || h <= 0)
            {
                return;
            }

            var ix = (int)x;
            var iy = (int)y;
            var iw = (int)w;
            var ih = (int)h;
            _renderer.FillRectangle(
                new RectInt(new PointInt(ix + iw, iy + ih), new PointInt(ix, iy)),
                color);
        }

        private void DrawText(
            ReadOnlySpan<char> text,
            string fontPath,
            float x, float y, float w, float h,
            float fontSize,
            RGBAColor32 color,
            TextAlign horizAlign = TextAlign.Near,
            TextAlign vertAlign  = TextAlign.Near)
        {
            if (text.IsEmpty || w <= 0 || h <= 0)
            {
                return;
            }

            var ix = (int)x;
            var iy = (int)y;
            var iw = (int)w;
            var ih = Math.Max(1, (int)h);
            var layout = new RectInt(new PointInt(ix + iw, iy + ih), new PointInt(ix, iy));
            _renderer.DrawText(text, fontPath, fontSize, color, layout, horizAlign, vertAlign);
        }
    }
}
