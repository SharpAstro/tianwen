using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;
using DIR.Lib;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Renderer-agnostic Equipment / Profile tab. Handles profile summary,
    /// discovered devices, device assignment, filter editing, and OTA properties.
    /// </summary>
    public class EquipmentTab<TSurface>(Renderer<TSurface> renderer) : PixelWidgetBase<TSurface>(renderer)
    {
        private readonly EquipmentContent _content = new EquipmentContent();

        // Layout constants (at 1x scale)
        private const float BaseProfilePanelWidth = 500f;
        private const float BaseFontSize          = 14f;
        private const float BaseItemHeight        = 24f;
        private const float BaseBottomBarHeight   = 50f;
        private const float BasePadding           = 8f;
        private const float BaseHeaderHeight      = 28f;
        private const float BaseButtonHeight      = 28f;
        private const float BaseBadgeWidth        = 68f;
        private const float BaseCheckmarkWidth    = 20f;
        private const float BaseArrowWidth        = 22f;
        private const float BaseStatusGlyphWidth  = 22f;
        private const float BaseConnectBtnWidth   = 80f;

        // Colors
        private static readonly RGBAColor32 ProfilePanelBg   = new RGBAColor32(0x1e, 0x1e, 0x28, 0xff);
        private static readonly RGBAColor32 DeviceListBg     = new RGBAColor32(0x18, 0x18, 0x22, 0xff);
        private static readonly RGBAColor32 SlotNormal       = new RGBAColor32(0x2a, 0x2a, 0x35, 0xff);
        private static readonly RGBAColor32 SlotActive       = new RGBAColor32(0x2a, 0x6b, 0xb8, 0xff);
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
        // Reachability indicator colors (⏻ glyph + segmented On|Off button highlight)
        private static readonly RGBAColor32 ReachConnected   = new RGBAColor32(0x40, 0xc0, 0x40, 0xff);
        private static readonly RGBAColor32 ReachDisconnected= new RGBAColor32(0xc0, 0x90, 0x30, 0xff);
        private static readonly RGBAColor32 ReachOffline     = new RGBAColor32(0x70, 0x70, 0x78, 0xff);
        private static readonly RGBAColor32 SegmentActive    = new RGBAColor32(0x30, 0x60, 0x90, 0xff);
        private static readonly RGBAColor32 SegmentInactive  = new RGBAColor32(0x28, 0x28, 0x38, 0xff);
        private static readonly RGBAColor32 SegmentDisabled  = new RGBAColor32(0x22, 0x22, 0x2a, 0xff);
        // Confirmation strip colors
        private static readonly RGBAColor32 ConfirmWarmBg    = new RGBAColor32(0x30, 0x60, 0x40, 0xff); // green-ish: safe choice
        private static readonly RGBAColor32 ConfirmForceBg   = new RGBAColor32(0x6a, 0x40, 0x20, 0xff); // amber: caution
        private static readonly RGBAColor32 ConfirmDangerBg  = new RGBAColor32(0xa0, 0x30, 0x30, 0xff); // red: destructive
        private static readonly RGBAColor32 ConfirmCancelBg  = new RGBAColor32(0x35, 0x35, 0x42, 0xff); // neutral

        /// <summary>Tab state (scroll offsets, discovery results, assignment mode).</summary>
        public EquipmentTabState State { get; } = new EquipmentTabState();

        /// <summary>Background task tracker for async operations. Set by the host.</summary>
        public BackgroundTaskTracker? Tracker { get; set; }


        public override bool HandleInput(InputEvent evt) => evt switch
        {
            InputEvent.KeyDown(var key, _) when State.FilterNameDropdown.HandleKeyDown(key) => true,
            // ESC dismisses expanded device settings pane before bubbling to quit
            InputEvent.KeyDown(InputKey.Escape, _) when State.ExpandedDeviceSettingsUri is not null =>
                DismissExpandedDevice(),
            _ => base.HandleInput(evt)
        };

        private bool DismissExpandedDevice()
        {
            State.StopEditingDeviceSettings();
            return true;
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
            string fontPath,
            string? emojiFontPath = null)
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

            RenderProfileView(appState, contentRect, dpiScale, fontPath, emojiFontPath);

            // Dropdown overlay — rendered absolutely last so it paints on top of everything
            var fontSize = BaseFontSize * dpiScale;
            RenderDropdownMenu(State.FilterNameDropdown, fontPath, fontSize * 0.85f,
                FilterTableBg, SlotActive, BodyText, SeparatorColor,
                viewportWidth: contentRect.X + contentRect.Width,
                viewportHeight: contentRect.Y + contentRect.Height);
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
                _ => PostSignal(new CreateProfileSignal()));
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
                _ => { if (State.ProfileNameInput.Text.Length > 0) State.ProfileNameInput.OnCommit?.Invoke(State.ProfileNameInput.Text); });

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
            float dpiScale, string fontPath,
            string? emojiFontPath = null)
        {
            var profilePanelW = BaseProfilePanelWidth * dpiScale;
            var bottomBarH    = BaseBottomBarHeight * dpiScale;

            var layout = new PixelLayout(contentRect);
            var bottomBarRect = layout.Dock(PixelDockStyle.Bottom, bottomBarH);
            var profileRect = layout.Dock(PixelDockStyle.Left, profilePanelW);
            var deviceListRect = layout.Fill();

            // Left: profile panel
            FillRect(profileRect.X, profileRect.Y, profileRect.Width, profileRect.Height, ProfilePanelBg);
            RenderProfilePanel(appState, profileRect, dpiScale, fontPath, emojiFontPath);

            // Vertical separator
            FillRect(deviceListRect.X, deviceListRect.Y, 1f, deviceListRect.Height, SeparatorColor);

            // Right: device list
            FillRect(deviceListRect.X, deviceListRect.Y, deviceListRect.Width, deviceListRect.Height, DeviceListBg);
            RenderDeviceList(appState, deviceListRect, dpiScale, fontPath, emojiFontPath);

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
            float dpiScale, string fontPath,
            string? emojiFontPath = null)
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
                    x, cursor, w, itemH, dpiScale, fontPath, fontSize, padding, arrowW, appState, pd, emojiFontPath);

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
                        _ => State.LatitudeInput.OnCommit?.Invoke(State.LatitudeInput.Text));
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
                        _ => PostSignal(new EditSiteSignal()));
                    DrawText(siteStr.AsSpan(), fontPath, x + padding, cursor, siteBtnW - arrowW, itemH, fontSize * 0.9f, SiteText, TextAlign.Near, TextAlign.Center);
                    DrawText("[>]".AsSpan(), fontPath, x + w - padding - arrowW, cursor, arrowW, itemH, fontSize * 0.85f, DimText, TextAlign.Center, TextAlign.Center);
                    cursor += itemH;
                }
                else
                {
                    // No site configured — show "Set Site" button
                    var setSiteBtnW = Renderer.MeasureText("Set Site".AsSpan(), fontPath, fontSize).Width + padding * 4f;
                    RenderButton("Set Site", x + padding, cursor, setSiteBtnW, buttonH, fontPath, fontSize, CreateButton, BodyText, "EditSite",
                        _ => PostSignal(new EditSiteSignal()));
                    cursor += buttonH;
                }

                cursor += padding / 2f;

                // Guider slot
                cursor = RenderProfileSlot(
                    "Guider", pd.Guider, new AssignTarget.ProfileLevel("Guider"),
                    x, cursor, w, itemH, dpiScale, fontPath, fontSize, padding, arrowW, appState, pd, emojiFontPath);

                // Guider Camera slot
                cursor = RenderProfileSlot(
                    "Guider Cam", pd.GuiderCamera, new AssignTarget.ProfileLevel("GuiderCamera"),
                    x, cursor, w, itemH, dpiScale, fontPath, fontSize, padding, arrowW, appState, pd, emojiFontPath);

                // Guider Focuser slot
                cursor = RenderProfileSlot(
                    "Guider Foc", pd.GuiderFocuser, new AssignTarget.ProfileLevel("GuiderFocuser"),
                    x, cursor, w, itemH, dpiScale, fontPath, fontSize, padding, arrowW, appState, pd, emojiFontPath);

                // Guide scope focal length
                {
                    var labelW = w * 0.35f;
                    var fieldW = (int)(w - padding * 2f - labelW);
                    var fieldX = (int)(x + padding + labelW);
                    var fieldH = (int)(itemH * 0.9f);

                    // Initialize from profile if not already set
                    if (State.GuiderFocalLengthInput.Text.Length == 0 && pd.GuiderFocalLength is > 0)
                    {
                        State.GuiderFocalLengthInput.Text = pd.GuiderFocalLength.Value.ToString();
                        State.GuiderFocalLengthInput.CursorPos = State.GuiderFocalLengthInput.Text.Length;
                    }

                    DrawText("Guide FL (mm):".AsSpan(), fontPath, x + padding, cursor, labelW, itemH,
                        fontSize * 0.85f, DimText, TextAlign.Near, TextAlign.Center);
                    RenderTextInput(State.GuiderFocalLengthInput, fieldX, (int)cursor, fieldW, fieldH, fontPath, fontSize * 0.9f);
                    cursor += fieldH + 2;
                }

                // Device settings for guider (if it declares any)
                cursor = RenderDeviceSettingsIfAny(appState, pd, pd.Guider, "Guider Settings",
                    x, cursor, w, itemH, dpiScale, fontPath, fontSize, padding);

                cursor += padding;
                FillRect(x + padding, cursor, w - padding * 2f, 1f, SeparatorColor);
                cursor += padding;

                // Extra profile-level slots (Weather, future device types) — rendered dynamically
                foreach (var extraSlot in _content.GetExtraProfileSlots(pd))
                {
                    var extraDeviceUri = EquipmentActions.GetAssignedDevice(pd, extraSlot.Slot);
                    cursor = RenderProfileSlot(
                        extraSlot.Label, extraDeviceUri, extraSlot.Slot,
                        x, cursor, w, itemH, dpiScale, fontPath, fontSize, padding, arrowW, appState, pd, emojiFontPath);
                    cursor = RenderDeviceSettingsIfAny(appState, pd, extraDeviceUri, $"{extraSlot.Label} Settings",
                        x, cursor, w, itemH, dpiScale, fontPath, fontSize, padding);
                }

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

                    // [Edit]/[Save] toggle button
                    var editLabel = isEditingOta ? "Save" : "Edit";
                    var capturedI = i;
                    RenderButton(editLabel, x + w - padding - editBtnW, cursor, editBtnW, itemH, fontPath, fontSize * 0.85f, EditButtonBg, BodyText, $"EditOta{i}",
                        _ =>
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
                                    PostSignal(new UpdateProfileSignal(newData));
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
                        x, cursor, w, itemH, dpiScale, fontPath, fontSize, padding, arrowW, appState, pd, emojiFontPath);
                    // Device settings for camera (if it declares any)
                    cursor = RenderDeviceSettingsIfAny(appState, pd, ota.Camera, "Camera Settings",
                        x, cursor, w, itemH, dpiScale, fontPath, fontSize, padding);
                    // Live cooler control + telemetry graph (only when hub-connected)
                    cursor = RenderCameraTelemetryIfAny(appState, ota.Camera,
                        x, cursor, w, itemH, dpiScale, fontPath, fontSize, padding);
                    cursor = RenderProfileSlot(
                        "  Focuser", ota.Focuser, new AssignTarget.OTALevel(i, "Focuser"),
                        x, cursor, w, itemH, dpiScale, fontPath, fontSize, padding, arrowW, appState, pd, emojiFontPath);
                    cursor = RenderDeviceSettingsIfAny(appState, pd, ota.Focuser, "Focuser Settings",
                        x, cursor, w, itemH, dpiScale, fontPath, fontSize, padding);
                    cursor = RenderProfileSlot(
                        "  Filter Wheel", ota.FilterWheel, new AssignTarget.OTALevel(i, "FilterWheel"),
                        x, cursor, w, itemH, dpiScale, fontPath, fontSize, padding, arrowW, appState, pd, emojiFontPath);

                    // Filter table (shown when a filter wheel is assigned)
                    if (ota.FilterWheel is not null && ota.FilterWheel != NoneDevice.Instance.DeviceUri)
                    {
                        cursor = RenderFilterTable(appState, i, pd, x, cursor, w, itemH, dpiScale, fontPath, fontSize, padding, buttonH);
                    }

                    cursor = RenderProfileSlot(
                        "  Cover", ota.Cover, new AssignTarget.OTALevel(i, "Cover"),
                        x, cursor, w, itemH, dpiScale, fontPath, fontSize, padding, arrowW, appState, pd, emojiFontPath);

                    cursor += padding / 2f;
                }

                // [+ Add OTA] button — only if there is room
                var addOtaBtnY = cursor;
                var addOtaBtnW = 120f * dpiScale;
                if (addOtaBtnY + buttonH < y + h - padding)
                {
                    RenderButton("+ Add OTA", x + padding, addOtaBtnY, addOtaBtnW, buttonH, fontPath, fontSize, CreateButton, BodyText, "AddOta",
                        _ => PostSignal(new AddOtaSignal()));
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
            float fontSize, float padding, float arrowW,
            GuiAppState? appState = null,
            ProfileData? profileData = null,
            string? emojiFontPath = null)
        {
            var isActive = State.ActiveAssignment == slot;
            var bgColor = isActive ? SlotActive : SlotNormal;

            FillRect(x, y, w, itemH, bgColor);
            var capturedSlot = slot;
            var capturedAppState = appState;
            RegisterClickable(x, y, w, itemH, new HitResult.SlotHit<AssignTarget>(slot),
                _ =>
                {
                    State.ActiveAssignment = State.ActiveAssignment == capturedSlot ? null : capturedSlot;
                    if (capturedAppState is not null) capturedAppState.NeedsRedraw = true;
                });

            // Separator line at bottom of slot
            FillRect(x, y + itemH - 1f, w, 1f, SeparatorColor);

            // Label column (~35% of width)
            var labelW = w * 0.35f;
            DrawText(
                label.AsSpan(),
                fontPath,
                x + padding, y, labelW, itemH,
                fontSize * 0.9f, DimText, TextAlign.Near, TextAlign.Center);

            // Device name (fills remaining space minus arrow/indicator column).
            // Truncate with an ellipsis if it would visually overflow into the indicator —
            // DrawText doesn't clip on TextAlign.Near, so we have to do it ourselves.
            var deviceLabel = EquipmentActions.DeviceLabel(deviceUri, registry: null);
            var nameX = x + labelW;
            var nameW = w - labelW - arrowW - padding;
            var truncated = TruncateToWidth(deviceLabel, fontPath, fontSize, nameW);
            DrawText(
                truncated.AsSpan(),
                fontPath,
                nameX, y, nameW, itemH,
                fontSize, isActive ? BodyText : BodyText, TextAlign.Near, TextAlign.Center);

            // Right-edge indicator. When a device is assigned and we have access to the live
            // hub + discovery snapshot, draw a coloured square via FillRect (font-independent)
            // so the user sees per-slot reachability without leaving the profile panel.
            // Active assignment-mode highlight always wins and shows the original [>] arrow.
            var arrowX = x + w - arrowW;
            var arrowColor = isActive ? AccentInstruct : DimText;

            EquipmentActions.DeviceReachability? slotReach = null;
            if (!isActive && deviceUri is not null && deviceUri != NoneDevice.Instance.DeviceUri
                && profileData is { } pd && appState is { })
            {
                var r = EquipmentActions.GetReachability(pd, appState.DeviceHub,
                    State.DiscoveredDevices, deviceUri);
                if (r != EquipmentActions.DeviceReachability.NotAssigned)
                {
                    slotReach = r;
                }
            }

            if (slotReach is { } reach)
            {
                var dotColor = reach switch
                {
                    EquipmentActions.DeviceReachability.Connected    => ReachConnected,
                    EquipmentActions.DeviceReachability.Disconnected => ReachDisconnected,
                    EquipmentActions.DeviceReachability.Offline      => ReachOffline,
                    _                                                => DimText
                };
                var dotSize = MathF.Min(itemH * 0.45f, arrowW * 0.55f);
                var dotX = arrowX + (arrowW - padding / 2f - dotSize) * 0.5f;
                var dotY = y + (itemH - dotSize) * 0.5f;
                FillRect(dotX, dotY, dotSize, dotSize, dotColor);
            }
            else
            {
                DrawText(
                    ">".AsSpan(),
                    fontPath,
                    arrowX, y, arrowW - padding / 2f, itemH,
                    fontSize, arrowColor, TextAlign.Center, TextAlign.Center);
            }

            return y + itemH;
        }

        // -----------------------------------------------------------------------
        // Right panel: device list
        // -----------------------------------------------------------------------

        private void RenderDeviceList(
            GuiAppState appState,
            RectF32 rect,
            float dpiScale, string fontPath,
            string? emojiFontPath = null)
        {
            var fontSize   = BaseFontSize * dpiScale;
            var padding    = BasePadding * dpiScale;
            var itemH      = BaseItemHeight * dpiScale;
            var headerH    = BaseHeaderHeight * dpiScale;
            var badgeW     = BaseBadgeWidth * dpiScale;
            var checkW     = BaseCheckmarkWidth * dpiScale;
            var statusW    = BaseStatusGlyphWidth * dpiScale;
            var connBtnW   = BaseConnectBtnWidth * dpiScale;
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
                    _ => PostSignal(new AssignDeviceSignal(capturedIdx)));
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

                // Reserve right-edge columns: [⏻ status][On|Off button][✓ assigned]
                // Even when not assigned we keep the layout stable so name widths don't jitter.
                var rightReserved = padding + checkW + padding + connBtnW + padding + statusW;

                // Device name
                var nameX = x + badgeW + padding;
                var nameW = w - badgeW - padding * 2f - rightReserved;
                DrawText(
                    device.DisplayName.AsSpan(),
                    fontPath,
                    nameX, rowY, nameW, itemH,
                    fontSize, textColor, TextAlign.Near, TextAlign.Center);

                // Right-edge columns laid out from the right margin inward.
                var checkColX  = x + w - padding - checkW;
                var btnColX    = checkColX - padding - connBtnW;
                var statusColX = btnColX - padding - statusW;

                // Assigned checkmark (still answers "is this URI wired into the profile?")
                if (isAssigned)
                {
                    DrawText(
                        "\u2713".AsSpan(),
                        fontPath,
                        checkColX, rowY, checkW, itemH,
                        fontSize, AssignedGreen, TextAlign.Center, TextAlign.Center);
                }

                // Reachability indicator + connect/disconnect button render for EVERY discovered
                // row (assigned or not). Unassigned devices that get connected appear as
                // "orphans" — useful for ad-hoc connect of e.g. Open-Meteo without first
                // wiring it into a profile slot.
                var reach = EquipmentActions.GetReachability(data, appState.DeviceHub, devices, device.DeviceUri);
                {
                    var connectUriForPending = EquipmentActions.FindAssignedUri(data, device.DeviceUri) ?? device.DeviceUri;
                    var pending = State.PendingTransitions.Contains(connectUriForPending);

                    // Status indicator (display only — answers "is hardware reachable right now?").
                    // Drawn as a filled square via FillRect so it renders regardless of font coverage.
                    var glyphColor = reach switch
                    {
                        EquipmentActions.DeviceReachability.Connected    => ReachConnected,
                        EquipmentActions.DeviceReachability.Disconnected => ReachDisconnected,
                        EquipmentActions.DeviceReachability.Offline      => ReachOffline,
                        _                                                => DimText
                    };
                    var dotSize = MathF.Min(itemH * 0.45f, statusW * 0.6f);
                    var dotX = statusColX + (statusW - dotSize) * 0.5f;
                    var dotY = rowY + (itemH - dotSize) * 0.5f;
                    FillRect(dotX, dotY, dotSize, dotSize, glyphColor);

                    if (reach == EquipmentActions.DeviceReachability.Offline)
                    {
                        // Offline: render OFFLINE label in place of the segmented button (no clickables).
                        DrawText(
                            "OFFLINE".AsSpan(),
                            fontPath,
                            btnColX, rowY, connBtnW, itemH,
                            fontSize * 0.75f, ReachOffline, TextAlign.Center, TextAlign.Center);
                    }
                    else
                    {
                        // Connect with the profile URI (preserves query params like apiKey, port, baud).
                        // The discovered device URI strips these — we only use it to identify the device.
                        var connectUri = EquipmentActions.FindAssignedUri(data, device.DeviceUri) ?? device.DeviceUri;

                        // Pending disconnect confirmations replace the right portion of the row.
                        // Extend the strip well past the status/button/checkmark columns and
                        // into the (now-redundant) device-name area so the action labels fit
                        // comfortably — the user already knows which device they're confirming.
                        var stripX = nameX + nameW * 0.35f;
                        var stripW = (x + w) - stripX - padding;

                        if (DeviceBase.SameDevice(State.PendingForceConfirm, connectUri))
                        {
                            // Stage 2: force-disconnect confirmation. [Cancel] on left,
                            // destructive [REALLY FORCE] on the right — opposite side from
                            // where Force Off was clicked, to defeat muscle-memory escalation.
                            RenderForceConfirmStrip(connectUri, stripX, rowY, stripW, itemH,
                                fontPath, fontSize);
                        }
                        else if (DeviceBase.SameDevice(State.PendingDisconnectConfirm, connectUri))
                        {
                            // Stage 1: warm-or-force confirmation.
                            RenderDisconnectConfirmStrip(connectUri, stripX, rowY, stripW, itemH,
                                fontPath, fontSize, State.PendingDisconnectSafety);
                        }
                        else
                        {
                            // Telegraph disconnect risk on the Off segment itself by checking the
                            // latest cached telemetry. If the camera reports CoolerOn or Busy,
                            // tint Off red so the user knows clicking it will land on the
                            // confirmation strip rather than disconnecting cleanly.
                            var key = connectUri.GetLeftPart(UriPartial.Path);
                            var unsafeOff = false;
                            if (State.CameraTelemetry.TryGetValue(key, out var buf) && buf.Latest is { } latest)
                            {
                                unsafeOff = latest.CoolerOn || latest.Busy;
                            }
                            // Segmented On|Off button (encodes current state + available transition).
                            RenderConnectSegment(connectUri, btnColX, rowY, connBtnW, itemH,
                                fontPath, fontSize, reach, pending, offIsUnsafe: unsafeOff);
                        }
                    }
                }
            }

            // [Discover] button at the bottom of the list area
            var discoverBtnY = y + h - buttonH - padding;
            var discoverLabel = State.IsDiscovering ? "Discovering..." : "Discover";
            var discoverBtnW = Renderer.MeasureText("Discovering...".AsSpan(), fontPath, fontSize).Width + padding * 4f;
            var discoverBtnX = x + padding;

            RenderButton(discoverLabel, discoverBtnX, discoverBtnY, discoverBtnW, buttonH, fontPath, fontSize, CreateButton, BodyText, "Discover",
                mods => PostSignal(new DiscoverDevicesSignal(IncludeFake: (mods & InputModifier.Shift) != 0)));
        }

        /// <summary>
        /// Truncates <paramref name="text"/> with a trailing ellipsis so its rendered width
        /// fits within <paramref name="maxWidth"/>. Returns the original string when it already fits.
        /// Falls back gracefully on tiny widths by clipping characters until just the ellipsis remains.
        /// </summary>
        private string TruncateToWidth(string text, string fontPath, float fontSize, float maxWidth)
        {
            if (text.Length == 0 || maxWidth <= 0f) return text;
            if (Renderer.MeasureText(text.AsSpan(), fontPath, fontSize).Width <= maxWidth) return text;

            const string ellipsis = "\u2026";
            // Binary search the longest prefix that fits with the trailing ellipsis appended.
            var lo = 0;
            var hi = text.Length;
            var best = 0;
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                var candidate = string.Concat(text.AsSpan(0, mid), ellipsis);
                if (Renderer.MeasureText(candidate.AsSpan(), fontPath, fontSize).Width <= maxWidth)
                {
                    best = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            return string.Concat(text.AsSpan(0, best), ellipsis);
        }

        /// <summary>
        /// Stage-1 cooler-off confirmation strip (camera stays connected). [Warm up &amp; Off]
        /// (green), [Force] (amber, escalates), [Cancel] (neutral).
        /// </summary>
        private float RenderCoolerOffConfirmStrip(
            Uri cameraUri, float x, float y, float w, float h, string fontPath, float fontSize)
        {
            var gap = 2f;
            var btnW = (w - gap * 2f) / 3f;
            var bx = x;
            var capUri = cameraUri;

            // [Warm up & Off]
            FillRect(bx, y + h * 0.15f, btnW, h * 0.7f, ConfirmWarmBg);
            DrawText("Warm up & Off".AsSpan(), fontPath, bx, y, btnW, h, fontSize * 0.78f, BodyText, TextAlign.Center, TextAlign.Center);
            RegisterClickable(bx, y, btnW, h, new HitResult.ButtonHit("WarmCoolerOff"),
                _ => PostSignal(new WarmAndCoolerOffSignal(capUri)));

            // [Force] — escalates
            bx += btnW + gap;
            FillRect(bx, y + h * 0.15f, btnW, h * 0.7f, ConfirmForceBg);
            DrawText("Force".AsSpan(), fontPath, bx, y, btnW, h, fontSize * 0.78f, BodyText, TextAlign.Center, TextAlign.Center);
            RegisterClickable(bx, y, btnW, h, new HitResult.ButtonHit("EscalateForceCoolerOff"),
                _ =>
                {
                    State.PendingCoolerOffForceConfirm = capUri;
                    State.PendingCoolerOffConfirm = null;
                });

            // [Cancel]
            bx += btnW + gap;
            FillRect(bx, y + h * 0.15f, btnW, h * 0.7f, ConfirmCancelBg);
            DrawText("Cancel".AsSpan(), fontPath, bx, y, btnW, h, fontSize * 0.78f, BodyText, TextAlign.Center, TextAlign.Center);
            RegisterClickable(bx, y, btnW, h, new HitResult.ButtonHit("CancelCoolerOff"),
                _ =>
                {
                    State.PendingCoolerOffConfirm = null;
                    State.PendingCoolerOffForceConfirm = null;
                });

            return y + h;
        }

        /// <summary>
        /// Stage-2 force-cooler-off confirmation. Same anti-double-click position swap as
        /// the disconnect force flow: [Cancel] LEFT, [⚠ REALLY FORCE] RIGHT.
        /// </summary>
        private float RenderCoolerOffForceStrip(
            Uri cameraUri, float x, float y, float w, float h, string fontPath, float fontSize)
        {
            var gap = 4f;
            var halfW = (w - gap) / 2f;
            var capUri = cameraUri;

            // [Cancel] LEFT
            FillRect(x, y + h * 0.15f, halfW, h * 0.7f, ConfirmCancelBg);
            DrawText("Cancel".AsSpan(), fontPath, x, y, halfW, h, fontSize * 0.8f, BodyText, TextAlign.Center, TextAlign.Center);
            RegisterClickable(x, y, halfW, h, new HitResult.ButtonHit("CancelForceCoolerOff"),
                _ =>
                {
                    State.PendingCoolerOffForceConfirm = null;
                    State.PendingCoolerOffConfirm = null;
                });

            // [⚠ REALLY FORCE COOLER OFF] RIGHT
            var rx = x + halfW + gap;
            FillRect(rx, y + h * 0.15f, halfW, h * 0.7f, ConfirmDangerBg);
            DrawText("\u26A0 REALLY FORCE COOLER OFF".AsSpan(), fontPath, rx, y, halfW, h, fontSize * 0.7f, BodyText, TextAlign.Center, TextAlign.Center);
            RegisterClickable(rx, y, halfW, h, new HitResult.ButtonHit("ConfirmForceCoolerOff"),
                _ =>
                {
                    State.PendingCoolerOffForceConfirm = null;
                    PostSignal(new SetCoolerOffSignal(capUri));
                });

            return y + h;
        }

        /// <summary>
        /// Stage-1 confirmation strip shown when the user clicked Off on a cooled or busy
        /// camera. Three buttons: [Warm &amp; Off] (left, safe-green), [Force Off] (middle, amber),
        /// [Cancel] (right, neutral). Force Off escalates to a stage-2 confirmation rendered
        /// at a different position to prevent muscle-memory double-clicks.
        /// </summary>
        private void RenderDisconnectConfirmStrip(
            Uri deviceUri, float x, float y, float w, float h,
            string fontPath, float fontSize,
            EquipmentActions.DisconnectSafety safety)
        {
            // Three equal-width buttons. Order: [Warm & Off] [Force Off] [Cancel]
            var gap = 2f;
            var btnW = (w - gap * 2f) / 3f;
            var bx = x;

            var safetyLabel = safety switch
            {
                EquipmentActions.DisconnectSafety.CoolerOn   => "Warm up & Off",
                EquipmentActions.DisconnectSafety.Busy       => "Wait & Off",
                EquipmentActions.DisconnectSafety.BusyAndCool=> "Wait + Warm up",
                _                                            => "Warm up & Off"
            };

            var capUri = deviceUri;
            // [Warm/Wait & Off]
            FillRect(bx, y + h * 0.15f, btnW, h * 0.7f, ConfirmWarmBg);
            DrawText(safetyLabel.AsSpan(), fontPath, bx, y, btnW, h, fontSize * 0.75f, BodyText, TextAlign.Center, TextAlign.Center);
            RegisterClickable(bx, y, btnW, h, new HitResult.ButtonHit("WarmDisconnect"),
                _ => PostSignal(new WarmAndDisconnectDeviceSignal(capUri)));

            // [Force Off] — escalates to stage 2 instead of disconnecting directly
            bx += btnW + gap;
            FillRect(bx, y + h * 0.15f, btnW, h * 0.7f, ConfirmForceBg);
            DrawText("Force".AsSpan(), fontPath, bx, y, btnW, h, fontSize * 0.75f, BodyText, TextAlign.Center, TextAlign.Center);
            RegisterClickable(bx, y, btnW, h, new HitResult.ButtonHit("EscalateForce"),
                _ =>
                {
                    State.PendingForceConfirm = capUri;
                    State.PendingDisconnectConfirm = null;
                });

            // [Cancel]
            bx += btnW + gap;
            FillRect(bx, y + h * 0.15f, btnW, h * 0.7f, ConfirmCancelBg);
            DrawText("Cancel".AsSpan(), fontPath, bx, y, btnW, h, fontSize * 0.75f, BodyText, TextAlign.Center, TextAlign.Center);
            RegisterClickable(bx, y, btnW, h, new HitResult.ButtonHit("CancelDisconnect"),
                _ =>
                {
                    State.PendingDisconnectConfirm = null;
                    State.PendingForceConfirm = null;
                });
        }

        /// <summary>
        /// Stage-2 force-disconnect confirmation. Layout deliberately swaps positions so the
        /// destructive [REALLY FORCE] button lands where [Cancel] was on the previous strip,
        /// and vice-versa — defeats the user's muscle memory for "click the same spot twice".
        /// </summary>
        private void RenderForceConfirmStrip(
            Uri deviceUri, float x, float y, float w, float h,
            string fontPath, float fontSize)
        {
            // Two halves: [Cancel] LEFT (where [Warm & Off] used to be) and [REALLY FORCE] RIGHT
            // (where [Cancel] used to be). The reversed pairing means a double-click that
            // started on [Force Off] (middle of stage 1) lands on... nothing meaningful — it
            // either hits the [Cancel] half or ambiguous middle, never the destructive button.
            var gap = 4f;
            var halfW = (w - gap) / 2f;

            var capUri = deviceUri;
            // [Cancel] — LEFT
            FillRect(x, y + h * 0.15f, halfW, h * 0.7f, ConfirmCancelBg);
            DrawText("Cancel".AsSpan(), fontPath, x, y, halfW, h, fontSize * 0.8f, BodyText, TextAlign.Center, TextAlign.Center);
            RegisterClickable(x, y, halfW, h, new HitResult.ButtonHit("CancelForce"),
                _ =>
                {
                    State.PendingForceConfirm = null;
                    State.PendingDisconnectConfirm = null;
                });

            // [REALLY FORCE] — RIGHT (destructive, deliberately offset from where Force Off was)
            var rx = x + halfW + gap;
            FillRect(rx, y + h * 0.15f, halfW, h * 0.7f, ConfirmDangerBg);
            DrawText("\u26A0 REALLY FORCE OFF".AsSpan(), fontPath, rx, y, halfW, h, fontSize * 0.75f, BodyText, TextAlign.Center, TextAlign.Center);
            RegisterClickable(rx, y, halfW, h, new HitResult.ButtonHit("ConfirmForce"),
                _ =>
                {
                    State.PendingForceConfirm = null;
                    PostSignal(new ForceDisconnectDeviceSignal(capUri));
                });
        }

        /// <summary>
        /// Renders the segmented On|Off connect/disconnect button. The current state's segment
        /// is highlighted; only the *other* segment is clickable. While a transition is in flight,
        /// the inactive segment is shown as "…" and no clickables are registered.
        /// </summary>
        private void RenderConnectSegment(
            Uri deviceUri,
            float x, float y, float w, float h,
            string fontPath, float fontSize,
            EquipmentActions.DeviceReachability reach,
            bool pending,
            bool offIsUnsafe = false)
        {
            var segW = w / 2f;
            var isConnected = reach == EquipmentActions.DeviceReachability.Connected;

            var onBg  = isConnected ? SegmentActive : SegmentInactive;
            var offBg = isConnected ? SegmentInactive : SegmentActive;
            // Telegraph that Off would land on the warm/force confirmation strip — tint the
            // *inactive* Off segment red. (When isConnected is true, Off is the actionable
            // segment; show a darker red so it stays clearly clickable.)
            if (offIsUnsafe)
            {
                offBg = isConnected ? ConfirmDangerBg : ConfirmForceBg;
            }

            // Segment backgrounds (inset vertically so the button looks like a control, not a row)
            var segY = y + h * 0.15f;
            var segH = h * 0.7f;
            FillRect(x, segY, segW - 1f, segH, onBg);
            FillRect(x + segW, segY, segW, segH, offBg);

            // Labels: "…" on the segment we are transitioning *to*; otherwise "On" / "Off".
            var onLabel  = pending && !isConnected ? "\u2026".AsSpan()   : "On".AsSpan();
            var offLabel = pending &&  isConnected ? "\u2026".AsSpan()   : "Off".AsSpan();
            DrawText(onLabel,  fontPath, x,        y, segW, h, fontSize * 0.85f, BodyText, TextAlign.Center, TextAlign.Center);
            DrawText(offLabel, fontPath, x + segW, y, segW, h, fontSize * 0.85f, BodyText, TextAlign.Center, TextAlign.Center);

            if (pending)
            {
                return; // both segments inert until the in-flight transition resolves
            }

            // Register BOTH segments as clickable — even the "you are here" segment must
            // swallow the click so it doesn't fall through to the row's AssignDeviceSignal
            // (which would silently enter assignment mode and look like a missed click).
            // Clicking the segment you're already on is a no-op.
            var capturedUri = deviceUri;
            RegisterClickable(x, y, segW, h, new HitResult.ButtonHit("Connect"),
                _ => { if (!isConnected) PostSignal(new ConnectDeviceSignal(capturedUri)); });
            RegisterClickable(x + segW, y, segW, h, new HitResult.ButtonHit("Disconnect"),
                _ => { if (isConnected) PostSignal(new DisconnectDeviceSignal(capturedUri)); });
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

            // Note: appState.StatusMessage is rendered in the chrome's top status bar — don't
            // duplicate it here. The bottom bar is for *contextual* hints specific to the
            // equipment-tab interaction (assignment mode, default tip).
            if (State.ActiveAssignment is not null)
            {
                var typeName = State.ActiveAssignment.ExpectedDeviceType.ToString();
                message   = $"Select a {typeName} device from the list on the right, or click the slot again to cancel.";
                textColor = AccentInstruct;
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
                _ =>
                {
                    if (appState.ActiveProfile is { } prof && prof.Data is { } data)
                    {
                        var currentOta = data.OTAs[capturedIdx];
                        var nextDesign = (OpticalDesign)(((int)currentOta.OpticalDesign + 1) % 8);
                        var newData = EquipmentActions.UpdateOTA(data, capturedIdx, opticalDesign: nextDesign);
                        PostSignal(new UpdateProfileSignal(newData));
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
            var savedFilters = EquipmentActions.GetFilterConfig(pd, otaIndex);
            var isExpanded = State.ExpandedFilterOtaIndex == otaIndex;
            var rowH = itemH * 0.9f;
            var capturedOtaIdx = otaIndex;

            // Toggle header — clicking expands/collapses and loads/discards editing state
            var headerLabel = isExpanded
                ? $"    Filters ({savedFilters.Count}) [-]"
                : $"    Filters ({savedFilters.Count}) [+]";
            FillRect(x + padding, cursor, w - padding * 2f, rowH, FilterTableBg);
            RegisterClickable(x + padding, cursor, w - padding * 2f, rowH, new HitResult.ButtonHit($"ToggleFilters{otaIndex}"),
                _ =>
                {
                    if (isExpanded)
                    {
                        State.ExpandedFilterOtaIndex = -1;
                        State.StopEditingFilters();
                    }
                    else
                    {
                        State.ExpandedFilterOtaIndex = capturedOtaIdx;
                        State.BeginEditingFilters(savedFilters);
                    }
                });
            DrawText(
                headerLabel.AsSpan(),
                fontPath,
                x + padding * 2f, cursor, w - padding * 4f, rowH,
                fontSize * 0.85f, HeaderText, TextAlign.Near, TextAlign.Center);
            cursor += rowH;

            if (!isExpanded || State.EditingFilters is not { } filters)
            {
                return cursor;
            }

            // Layout columns: [#][Name                ][- offset +]
            var slotNumW = 20f * dpiScale;
            var contentW = w - padding * 4f;
            var offsetGroupW = 100f * dpiScale;  // [-] value [+] grouped
            var nameColW = contentW - slotNumW - offsetGroupW;
            var stepBtnW = 24f * dpiScale;
            var offsetValueW = offsetGroupW - stepBtnW * 2f;
            var colStartX = x + padding * 2f;

            // Column headers
            FillRect(x + padding, cursor, w - padding * 2f, rowH, FilterTableBg);
            DrawText("#".AsSpan(), fontPath, colStartX, cursor, slotNumW, rowH, fontSize * 0.75f, DimText, TextAlign.Center, TextAlign.Center);
            DrawText("Name".AsSpan(), fontPath, colStartX + slotNumW, cursor, nameColW, rowH, fontSize * 0.75f, DimText, TextAlign.Near, TextAlign.Center);
            DrawText("Offset".AsSpan(), fontPath, colStartX + slotNumW + nameColW, cursor, offsetGroupW, rowH, fontSize * 0.75f, DimText, TextAlign.Center, TextAlign.Center);
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
                    colStartX, cursor, slotNumW, rowH,
                    fontSize * 0.8f, DimText, TextAlign.Center, TextAlign.Center);

                // Filter name — inline text input if custom editing, otherwise clickable to open dropdown
                var capturedF = f;
                var nameCellX = colStartX + slotNumW;
                var nameCellY = cursor;

                if (State.CustomFilterSlotIndex == f)
                {
                    RenderTextInput(State.CustomFilterNameInput, (int)nameCellX, (int)cursor, (int)nameColW, (int)rowH, fontPath, fontSize * 0.8f);
                }
                else
                {
                RenderButton(EquipmentActions.FilterDisplayName(filter), nameCellX, cursor, nameColW, rowH, fontPath, fontSize * 0.8f, rowBg, BodyText, $"FilterName{otaIndex}_{f}",
                    _ =>
                    {
                        var existingCustom = capturedF < filters.Count ? filters[capturedF].CustomName : null;
                        State.FilterNameDropdown.Open(
                            nameCellX, nameCellY + rowH, nameColW,
                            EquipmentActions.CommonFilterNames,
                            (idx, name) =>
                            {
                                if (capturedF < filters.Count)
                                {
                                    filters[capturedF] = new InstalledFilter(name, filters[capturedF].Position);
                                    State.FiltersDirty = true;
                                }
                            },
                            hasCustomEntry: true,
                            onCustom: () =>
                            {
                                State.CustomFilterSlotIndex = capturedF;
                                // Preserve existing custom name if re-selecting Custom...
                                var preservedName = capturedF < filters.Count && filters[capturedF].CustomName is { } cn ? cn : "";
                                State.CustomFilterNameInput.Text = preservedName;
                                State.CustomFilterNameInput.CursorPos = preservedName.Length;
                                // Signal deferred text input activation (processed in OnPostFrame)
                                PostSignal(new ActivateTextInputSignal(State.CustomFilterNameInput));
                            },
                            customEntryLabel: existingCustom is { Length: > 0 } ? $"Custom: {existingCustom}" : null);
                    });
                }

                // Offset group: [-] value [+]
                var offsetX = colStartX + slotNumW + nameColW;
                var offsetStr = filter.Position >= 0 ? $"+{filter.Position}" : filter.Position.ToString();

                RenderButton("-", offsetX, cursor, stepBtnW, rowH, fontPath, fontSize * 0.8f, EditButtonBg, BodyText, $"FilterOffDec{otaIndex}_{f}",
                    _ =>
                    {
                        if (capturedF < filters.Count)
                        {
                            var cur = filters[capturedF];
                            filters[capturedF] = new InstalledFilter(cur.Filter.Name, cur.Position - 1);
                            State.FiltersDirty = true;
                        }
                    });

                DrawText(
                    offsetStr.AsSpan(),
                    fontPath,
                    offsetX + stepBtnW, cursor, offsetValueW, rowH,
                    fontSize * 0.8f, BodyText, TextAlign.Center, TextAlign.Center);

                RenderButton("+", offsetX + stepBtnW + offsetValueW, cursor, stepBtnW, rowH, fontPath, fontSize * 0.8f, EditButtonBg, BodyText, $"FilterOffInc{otaIndex}_{f}",
                    _ =>
                    {
                        if (capturedF < filters.Count)
                        {
                            var cur = filters[capturedF];
                            filters[capturedF] = new InstalledFilter(cur.Filter.Name, cur.Position + 1);
                            State.FiltersDirty = true;
                        }
                    });

                cursor += rowH;
            }

            // Save / Cancel buttons (only shown when dirty)
            if (State.FiltersDirty)
            {
                var saveBtnW = Renderer.MeasureText("Save".AsSpan(), fontPath, fontSize * 0.85f).Width + padding * 3f;
                var cancelBtnW = Renderer.MeasureText("Cancel".AsSpan(), fontPath, fontSize * 0.85f).Width + padding * 3f;

                RenderButton("Save", x + padding * 2f, cursor, saveBtnW, buttonH * 0.85f, fontPath, fontSize * 0.85f, CreateButton, BodyText, $"SaveFilters{otaIndex}",
                    _ =>
                    {
                        if (appState.ActiveProfile is { } prof && prof.Data is { } data && State.EditingFilters is { } editFilters)
                        {
                            var newData = EquipmentActions.SetFilterConfig(data, capturedOtaIdx, editFilters);
                            PostSignal(new UpdateProfileSignal(newData));
                            State.FiltersDirty = false;
                        }
                    });

                RenderButton("Cancel", x + padding * 2f + saveBtnW + padding, cursor, cancelBtnW, buttonH * 0.85f, fontPath, fontSize * 0.85f, EditButtonBg, BodyText, $"CancelFilters{otaIndex}",
                    _ =>
                    {
                        // Reload from profile
                        State.BeginEditingFilters(savedFilters);
                    });

                cursor += buttonH * 0.85f + padding / 2f;
            }

            // Wire commit for custom filter name — on Enter, apply the name.
            // Always re-wire to avoid stale captures after list replacement.
            State.CustomFilterNameInput.OnCommit = (text) =>
            {
                if (text is { Length: > 0 } && State.EditingFilters is { } ef
                    && State.CustomFilterSlotIndex >= 0 && State.CustomFilterSlotIndex < ef.Count)
                {
                    ef[State.CustomFilterSlotIndex] = new InstalledFilter(text, ef[State.CustomFilterSlotIndex].Position);
                    State.FiltersDirty = true;
                }
                State.CustomFilterSlotIndex = -1;
                State.CustomFilterNameInput.Deactivate();
                return Task.CompletedTask;
            };
            State.CustomFilterNameInput.OnCancel = () =>
            {
                State.CustomFilterSlotIndex = -1;
            };

            return cursor;
        }

        // -----------------------------------------------------------------------
        // Generic device settings
        // -----------------------------------------------------------------------

        /// <summary>
        /// Renders device settings for a device URI if the device declares any <see cref="DeviceSettingDescriptor"/>s.
        /// Returns the updated cursor Y. No-ops if the device has no settings.
        /// </summary>
        /// <summary>
        /// Renders the camera cooler control + telemetry sparkline panel for the given URI,
        /// but only when the camera is currently connected via the device hub. Hidden otherwise.
        /// Layout: collapsible header → readout row → setpoint input + buttons → temp sparkline.
        /// </summary>
        private float RenderCameraTelemetryIfAny(
            GuiAppState appState,
            Uri? cameraUri,
            float x, float cursor, float w, float itemH,
            float dpiScale, string fontPath, float fontSize, float padding)
        {
            if (cameraUri is null || cameraUri == NoneDevice.Instance.DeviceUri) return cursor;
            if (appState.DeviceHub is not { } hub || !hub.IsConnected(cameraUri)) return cursor;

            var key = cameraUri.GetLeftPart(UriPartial.Path);
            var rowH = itemH * 0.9f;
            var headerKey = $"CamCool_{key}";

            // Toggle header — independent of the device-settings expand state.
            // Stored on EquipmentTabState as a separate sub-key so it doesn't clash.
            var paneKey = $"CoolerPane:{key}";
            var isOpen = State.ExpandedDeviceSettingsUri == paneKey;
            var headerLabel = isOpen ? "    Cooler Control [-]" : "    Cooler Control [+]";

            FillRect(x + padding, cursor, w - padding * 2f, rowH, FilterTableBg);
            RegisterClickable(x + padding, cursor, w - padding * 2f, rowH, new HitResult.ButtonHit(headerKey),
                _ =>
                {
                    State.ExpandedDeviceSettingsUri = isOpen ? null : paneKey;
                });
            DrawText(headerLabel.AsSpan(), fontPath,
                x + padding * 2f, cursor, w - padding * 4f, rowH,
                fontSize * 0.85f, HeaderText, TextAlign.Near, TextAlign.Center);
            cursor += rowH;

            if (!isOpen) return cursor;

            // Latest sample (may be null on first frame after connect)
            State.CameraTelemetry.TryGetValue(key, out var buffer);
            var latest = buffer?.Latest;

            // ---- Readout row: 4 fixed-share cells so labels never overflow ----
            var readoutH = rowH;
            var readoutBg = FilterRowAlt;
            var rowX = x + padding;
            var rowW = w - padding * 2f;
            FillRect(rowX, cursor, rowW, readoutH, readoutBg);

            string Cell(double? v, string suffix) => v is { } d ? $"{d:F1}{suffix}" : "—";
            var cellW = rowW / 4f;
            var cellPad = padding;
            var cellFs = fontSize * 0.85f;
            var tempStr = $"CCD: {Cell(latest?.CcdTempC, "\u00b0C")}";
            var setStr  = $"Set: {Cell(latest?.SetpointC, "\u00b0C")}";
            var pwrStr  = latest?.CoolerPowerPct is { } p ? $"Power: {p:F0}%" : "Power: —";
            var stateStr = latest is null ? "\u2026"
                : (latest.Value.CoolerOn ? "Cooler ON" : "Cooler OFF");

            // Belt-and-suspenders: still ellipsize per cell in case the panel shrinks below threshold.
            var inner = cellW - cellPad;
            var t = TruncateToWidth(tempStr,  fontPath, cellFs, inner);
            var s = TruncateToWidth(setStr,   fontPath, cellFs, inner);
            var pw = TruncateToWidth(pwrStr,   fontPath, cellFs, inner);
            var st = TruncateToWidth(stateStr, fontPath, cellFs, inner);
            DrawText(t.AsSpan(),  fontPath, rowX + cellPad,                 cursor, inner, readoutH, cellFs, BodyText, TextAlign.Near, TextAlign.Center);
            DrawText(s.AsSpan(),  fontPath, rowX + cellW + cellPad,         cursor, inner, readoutH, cellFs, BodyText, TextAlign.Near, TextAlign.Center);
            DrawText(pw.AsSpan(), fontPath, rowX + cellW * 2f + cellPad,    cursor, inner, readoutH, cellFs, BodyText, TextAlign.Near, TextAlign.Center);
            DrawText(st.AsSpan(), fontPath, rowX + cellW * 3f + cellPad,    cursor, inner, readoutH, cellFs, BodyText, TextAlign.Near, TextAlign.Center);
            cursor += readoutH;

            // ---- Controls row: setpoint input + [Cool to Setpoint] + [Cooler Off] ----
            var controlsH = rowH;
            FillRect(x + padding, cursor, w - padding * 2f, controlsH, FilterTableBg);
            var labelW = 80f * dpiScale;
            DrawText("    Setpoint:".AsSpan(), fontPath,
                x + padding, cursor, labelW, controlsH,
                fontSize * 0.85f, DimText, TextAlign.Near, TextAlign.Center);

            var inputX = x + padding + labelW;
            var inputW = 70f * dpiScale;
            // Lazy-init the setpoint input for this URI; default text from the latest sample.
            if (!State.CameraSetpointInputs.TryGetValue(key, out var setpointInput))
            {
                setpointInput = new TextInputState { Placeholder = "-10" };
                if (latest?.SetpointC is { } sp) setpointInput.Text = sp.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
                State.CameraSetpointInputs[key] = setpointInput;
            }
            RenderTextInput(setpointInput,
                (int)inputX, (int)cursor,
                (int)inputW, (int)controlsH,
                fontPath, fontSize * 0.85f);

            var btnGap = 4f * dpiScale;
            var coolBtnX = inputX + inputW + btnGap;
            var coolBtnW = 110f * dpiScale;
            var capUri = cameraUri;
            RenderButton("Cool to Setpoint", coolBtnX, cursor, coolBtnW, controlsH, fontPath, fontSize * 0.78f,
                CreateButton, BodyText, $"CoolTo_{key}",
                _ =>
                {
                    var txt = setpointInput.Text;
                    if (double.TryParse(txt, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var v))
                    {
                        PostSignal(new SetCoolerSetpointSignal(capUri, v));
                    }
                });

            var offBtnX = coolBtnX + coolBtnW + btnGap;
            var offBtnW = 80f * dpiScale;
            // Safety: tint the Cooler Off button red when the cooler is on, and route
            // the click to the confirmation strip instead of immediate cooler-off
            // (same condensation/thermal-shock concern as disconnect).
            var coolerUnsafe = latest is { } l && l.CoolerOn;
            var offBg = coolerUnsafe ? ConfirmDangerBg : EditButtonBg;
            RenderButton("Cooler Off", offBtnX, cursor, offBtnW, controlsH, fontPath, fontSize * 0.78f,
                offBg, BodyText, $"CoolerOff_{key}",
                _ =>
                {
                    if (coolerUnsafe)
                    {
                        State.PendingCoolerOffConfirm = capUri;
                        State.PendingCoolerOffForceConfirm = null;
                    }
                    else
                    {
                        PostSignal(new SetCoolerOffSignal(capUri));
                    }
                });
            cursor += controlsH;

            // Confirmation strip — appears under the controls row when the user clicked
            // Cooler Off on a cooled camera. Two stages, mirror of the disconnect flow.
            if (DeviceBase.SameDevice(State.PendingCoolerOffForceConfirm, cameraUri))
            {
                cursor = RenderCoolerOffForceStrip(cameraUri, x + padding, cursor, w - padding * 2f, controlsH, fontPath, fontSize);
            }
            else if (DeviceBase.SameDevice(State.PendingCoolerOffConfirm, cameraUri))
            {
                cursor = RenderCoolerOffConfirmStrip(cameraUri, x + padding, cursor, w - padding * 2f, controlsH, fontPath, fontSize);
            }

            // ---- Sparkline graph ----
            var graphH = 60f * dpiScale;
            var graphX = x + padding;
            var graphW = w - padding * 2f;
            FillRect(graphX, cursor, graphW, graphH, FilterTableBg);
            if (buffer is not null && buffer.Count >= 2)
            {
                RenderTemperatureSparkline(buffer, graphX, cursor, graphW, graphH, fontPath, fontSize);
            }
            else
            {
                DrawText("(sampling — graph appears after a few seconds)".AsSpan(), fontPath,
                    graphX, cursor, graphW, graphH,
                    fontSize * 0.8f, DimText, TextAlign.Center, TextAlign.Center);
            }
            cursor += graphH + padding * 0.5f;

            return cursor;
        }

        /// <summary>
        /// Draws a CCD-temperature line + setpoint reference line in the given rect.
        /// Y-axis spans observed min/max with a small margin; X-axis is sample order.
        /// </summary>
        private void RenderTemperatureSparkline(
            CameraTelemetryBuffer buffer,
            float x, float y, float w, float h,
            string fontPath, float fontSize)
        {
            // Collect points
            var samples = new List<CameraTelemetrySample>(buffer.Count);
            foreach (var s in buffer.InOrder()) samples.Add(s);
            if (samples.Count < 2) return;

            double minT = double.PositiveInfinity, maxT = double.NegativeInfinity;
            foreach (var s in samples)
            {
                if (s.CcdTempC is { } t) { if (t < minT) minT = t; if (t > maxT) maxT = t; }
                if (s.SetpointC is { } sp) { if (sp < minT) minT = sp; if (sp > maxT) maxT = sp; }
            }
            if (double.IsInfinity(minT) || double.IsInfinity(maxT)) return;
            if (Math.Abs(maxT - minT) < 0.5) { var mid = (minT + maxT) * 0.5; minT = mid - 0.5; maxT = mid + 0.5; }

            // Add a small vertical margin.
            var range = maxT - minT;
            minT -= range * 0.1; maxT += range * 0.1; range = maxT - minT;

            // Inset for axis labels on the left
            var labelW = 36f;
            var plotX = x + labelW;
            var plotW = w - labelW - 4f;
            var plotY = y + 4f;
            var plotH = h - 8f;

            // Axis label (min and max temperature)
            DrawText($"{maxT:F1}".AsSpan(), fontPath, x, plotY - 6f, labelW - 4f, 14f, fontSize * 0.7f, DimText, TextAlign.Far, TextAlign.Center);
            DrawText($"{minT:F1}".AsSpan(), fontPath, x, plotY + plotH - 8f, labelW - 4f, 14f, fontSize * 0.7f, DimText, TextAlign.Far, TextAlign.Center);

            // Setpoint reference line (dashed effect via short rects)
            var lastSp = samples[samples.Count - 1].SetpointC;
            if (lastSp is { } setPt)
            {
                var spY = plotY + (float)((maxT - setPt) / range) * plotH;
                for (var dx = 0f; dx < plotW; dx += 6f)
                {
                    FillRect(plotX + dx, spY, 3f, 1f, ReachDisconnected);
                }
            }

            // Plot CCD temp as connected line segments (one tiny rect per pair).
            for (var i = 1; i < samples.Count; i++)
            {
                if (samples[i - 1].CcdTempC is not { } t0) continue;
                if (samples[i].CcdTempC is not { } t1) continue;
                var x0 = plotX + (i - 1) * plotW / (samples.Count - 1);
                var x1 = plotX + i * plotW / (samples.Count - 1);
                var y0 = plotY + (float)((maxT - t0) / range) * plotH;
                var y1 = plotY + (float)((maxT - t1) / range) * plotH;
                DrawLineSegment(x0, y0, x1, y1, ReachConnected);
            }
        }

        /// <summary>
        /// Cheap line segment via a chain of 1px FillRects — adequate for a sparkline.
        /// </summary>
        private void DrawLineSegment(float x0, float y0, float x1, float y1, RGBAColor32 color)
        {
            var dx = x1 - x0; var dy = y1 - y0;
            var steps = (int)MathF.Ceiling(MathF.Max(MathF.Abs(dx), MathF.Abs(dy)));
            if (steps < 1) steps = 1;
            for (var i = 0; i <= steps; i++)
            {
                var t = (float)i / steps;
                FillRect(x0 + dx * t, y0 + dy * t, 1.5f, 1.5f, color);
            }
        }

        private float RenderDeviceSettingsIfAny(
            GuiAppState appState,
            ProfileData pd,
            Uri? deviceUri,
            string sectionLabel,
            float x, float cursor, float w, float itemH,
            float dpiScale, string fontPath, float fontSize, float padding)
        {
            if (deviceUri is null || deviceUri == NoneDevice.Instance.DeviceUri)
            {
                return cursor;
            }

            var device = EquipmentActions.TryDeviceFromUri(deviceUri);
            if (device is null || device.Settings.IsDefaultOrEmpty)
            {
                return cursor;
            }

            return RenderDeviceSettings(appState, pd, deviceUri, device.Settings, sectionLabel,
                x, cursor, w, itemH, dpiScale, fontPath, fontSize, padding);
        }

        /// <summary>
        /// Renders the expandable settings pane for a device. Iterates <see cref="DeviceSettingDescriptor"/>s
        /// and dispatches on <see cref="DeviceSettingKind"/> to render the appropriate control.
        /// </summary>
        private float RenderDeviceSettings(
            GuiAppState appState,
            ProfileData pd,
            Uri savedDeviceUri,
            ImmutableArray<DeviceSettingDescriptor> settings,
            string sectionLabel,
            float x, float cursor, float w, float itemH,
            float dpiScale, string fontPath, float fontSize, float padding)
        {
            var deviceKey = savedDeviceUri.GetLeftPart(UriPartial.Path);
            var isExpanded = State.ExpandedDeviceSettingsUri == deviceKey;
            var rowH = itemH * 0.9f;

            // Toggle header
            var headerLabel = isExpanded ? $"    {sectionLabel} [-]" : $"    {sectionLabel} [+]";
            FillRect(x + padding, cursor, w - padding * 2f, rowH, FilterTableBg);
            RegisterClickable(x + padding, cursor, w - padding * 2f, rowH, new HitResult.ButtonHit($"Toggle_{deviceKey}"),
                _ =>
                {
                    if (isExpanded)
                    {
                        State.StopEditingDeviceSettings();
                    }
                    else
                    {
                        State.BeginEditingDeviceSettings(savedDeviceUri);
                    }
                });
            DrawText(
                headerLabel.AsSpan(),
                fontPath,
                x + padding * 2f, cursor, w - padding * 4f, rowH,
                fontSize * 0.85f, HeaderText, TextAlign.Near, TextAlign.Center);
            cursor += rowH;

            if (!isExpanded || State.EditingDeviceUri is not { } editingUri)
            {
                return cursor;
            }

            var labelW = w * 0.45f;
            var controlX = x + padding * 2f + labelW;
            var controlW = w - padding * 4f - labelW;
            var labelX = x + padding * 2f;
            var stepBtnW = 24f * dpiScale;

            for (var i = 0; i < settings.Length; i++)
            {
                var desc = settings[i];

                // Conditional visibility
                if (desc.IsVisible is { } isVisible && !isVisible(editingUri))
                {
                    continue;
                }

                var rowBg = i % 2 == 0 ? FilterTableBg : FilterRowAlt;
                FillRect(x + padding, cursor, w - padding * 2f, rowH, rowBg);

                // StringEditor uses a narrower label to give more space to the text input
                var rowLabelW = desc.Kind == DeviceSettingKind.StringEditor ? w * 0.25f : labelW;
                var rowControlX = x + padding * 2f + rowLabelW;
                var rowControlW = w - padding * 4f - rowLabelW;
                DrawText($"{desc.Label}:".AsSpan(), fontPath, labelX, cursor, rowLabelW, rowH, fontSize * 0.85f, DimText, TextAlign.Near, TextAlign.Center);

                var capturedDesc = desc;
                switch (desc.Kind)
                {
                    case DeviceSettingKind.BoolToggle:
                    case DeviceSettingKind.EnumCycle:
                    {
                        var valueLabel = desc.FormatValue(editingUri);
                        RenderButton(valueLabel, controlX, cursor, controlW, rowH, fontPath, fontSize * 0.85f, EditButtonBg, BodyText, $"Cycle_{desc.Key}",
                            _ =>
                            {
                                State.EditingDeviceUri = capturedDesc.Increment(editingUri);
                                State.DeviceSettingsDirty = true;
                            });
                        break;
                    }

                    case DeviceSettingKind.IntStepper:
                    case DeviceSettingKind.FloatStepper:
                    case DeviceSettingKind.PercentStepper:
                    {
                        // [-] button
                        if (desc.Decrement is { } decrement)
                        {
                            RenderButton("-", controlX, cursor, stepBtnW, rowH, fontPath, fontSize * 0.85f, EditButtonBg, BodyText, $"Dec_{desc.Key}",
                                _ =>
                                {
                                    State.EditingDeviceUri = decrement(editingUri);
                                    State.DeviceSettingsDirty = true;
                                });
                        }

                        // Value label
                        var valueText = desc.FormatValue(editingUri);
                        var valueW = controlW - stepBtnW * 2f;
                        DrawText(valueText.AsSpan(), fontPath, controlX + stepBtnW, cursor, valueW, rowH, fontSize * 0.85f, BodyText, TextAlign.Center, TextAlign.Center);

                        // [+] button
                        RenderButton("+", controlX + controlW - stepBtnW, cursor, stepBtnW, rowH, fontPath, fontSize * 0.85f, EditButtonBg, BodyText, $"Inc_{desc.Key}",
                            _ =>
                            {
                                State.EditingDeviceUri = capturedDesc.Increment(editingUri);
                                State.DeviceSettingsDirty = true;
                            });
                        break;
                    }

                    case DeviceSettingKind.StringEditor:
                    {
                        var isEditing = State.EditingStringSettingKey == desc.Key;
                        if (isEditing)
                        {
                            // Active text input
                            if (desc.Placeholder is { } placeholder)
                            {
                                State.StringSettingInput.Placeholder = placeholder;
                            }
                            RenderTextInput(State.StringSettingInput, (int)rowControlX, (int)cursor, (int)rowControlW, (int)rowH, fontPath, fontSize * 0.85f);
                        }
                        else
                        {
                            // Display current value (masked if configured) with click-to-edit
                            var rawValue = desc.FormatValue(editingUri);
                            var displayValue = desc.Mask && rawValue.Length > 0
                                ? new string('*', Math.Min(rawValue.Length, 8)) + rawValue[Math.Max(0, rawValue.Length - 4)..]
                                : rawValue;
                            if (displayValue.Length == 0)
                            {
                                displayValue = desc.Placeholder ?? "(empty)";
                            }
                            RenderButton(displayValue, rowControlX, cursor, rowControlW, rowH, fontPath, fontSize * 0.85f, EditButtonBg, BodyText, $"Edit_{desc.Key}",
                                _ =>
                                {
                                    State.EditingStringSettingKey = capturedDesc.Key;
                                    State.StringSettingInput.Activate(capturedDesc.FormatValue(editingUri));
                                });
                        }
                        break;
                    }
                }

                cursor += rowH;
            }

            // Commit any active string editor when focus moves away
            if (State.EditingStringSettingKey is { } activeStringKey
                && State.StringSettingInput is { IsActive: false } stringInput)
            {
                State.EditingDeviceUri = DeviceSettingHelper.WithQueryParam(editingUri, activeStringKey, stringInput.Text);
                State.DeviceSettingsDirty = true;
                State.EditingStringSettingKey = null;
            }

            // Save / Cancel buttons (only when dirty)
            if (State.DeviceSettingsDirty)
            {
                var btnW = 60f * dpiScale;
                var gap = padding;
                var saveBtnX = x + w - padding - btnW * 2f - gap;
                var cancelBtnX = x + w - padding - btnW;

                RenderButton("Save", saveBtnX, cursor, btnW, rowH, fontPath, fontSize * 0.85f, CreateButton, BodyText, $"SaveSettings_{deviceKey}",
                    _ =>
                    {
                        if (appState.ActiveProfile is { Data: { } data } && State.EditingDeviceUri is { } newUri)
                        {
                            var newData = EquipmentActions.UpdateDeviceUri(data, savedDeviceUri, newUri);
                            PostSignal(new UpdateProfileSignal(newData));
                            State.BeginEditingDeviceSettings(newUri);
                        }
                    });

                RenderButton("Cancel", cancelBtnX, cursor, btnW, rowH, fontPath, fontSize * 0.85f, EditButtonBg, DimText, $"CancelSettings_{deviceKey}",
                    _ =>
                    {
                        State.BeginEditingDeviceSettings(savedDeviceUri);
                    });

                cursor += rowH;
            }

            return cursor;
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
                DeviceType.Weather        => "WX",
                DeviceType.Profile        => "Profile",
                _                         => "?"
            };
    }
}
