using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using DIR.Lib;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Left profile panel: the data-driven section walk (profile header, device slots, site,
    /// guide focal length, OTA headers/properties) dispatched from RenderProfilePanel.
    /// </summary>
    partial class EquipmentTab<TSurface>
    {
        // -----------------------------------------------------------------------
        // Left panel: profile summary
        // -----------------------------------------------------------------------

        private void RenderProfilePanel(
            GuiAppState appState,
            RectF32 rect,
            float dpiScale, string fontPath,
            string? emojiFontPath = null,
            LiveSessionState? liveSessionState = null)
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

            if (data is { } pd)
            {
                // The panel's vertical flow is a data-driven section list (TODO.md:57): the order lives in
                // EquipmentContent.GetProfilePanelSections, not in a hardcoded cursor walk. Each section is
                // dispatched to its renderer (which advances the cursor); runtime-conditional sections
                // (mount/camera telemetry, device settings) self-gate to a no-op when their device isn't
                // hub-connected / declares no settings, exactly as the inline calls did before.
                foreach (var section in _content.GetProfilePanelSections(pd))
                {
                    cursor = RenderSection(section, appState, pd, profile, liveSessionState,
                        x, cursor, w, y, h, dpiScale, fontPath, emojiFontPath,
                        fontSize, padding, itemH, arrowW, headerH, buttonH);
                }
            }
            else
            {
                cursor = RenderProfileHeaderSection(profile, x, cursor, w, fontPath, fontSize, padding, headerH);
                FillRect(x + padding, cursor, w - padding * 2f, 1f, SeparatorColor);
                cursor += padding;
                DrawText(
                    "Profile has no data.".AsSpan(),
                    fontPath,
                    x + padding, cursor, w - padding * 2f, itemH,
                    fontSize, DimText, TextAlign.Near, TextAlign.Center);
            }
        }

        /// <summary>
        /// Dispatches one <see cref="PanelSection"/> to its renderer at the current cursor Y, returning the
        /// new cursor. Static rows (slots / header / separator) go through the engine-backed helpers;
        /// interactive + variable-height widgets stay as imperative section helpers. This is the single
        /// switch the data-driven section loop (<see cref="EquipmentContent.GetProfilePanelSections"/>) walks,
        /// replacing the old hardcoded cursor sequence.
        /// </summary>
        private float RenderSection(PanelSection section, GuiAppState appState, ProfileData pd, Profile profile,
            LiveSessionState? liveSessionState, float x, float cursor, float w, float y, float h,
            float dpiScale, string fontPath, string? emojiFontPath,
            float fontSize, float padding, float itemH, float arrowW, float headerH, float buttonH)
        {
            switch (section)
            {
                case PanelSection.ProfileHeader:
                    return RenderProfileHeaderSection(profile, x, cursor, w, fontPath, fontSize, padding, headerH);

                case PanelSection.Separator:
                    FillRect(x + padding, cursor, w - padding * 2f, 1f, SeparatorColor);
                    return cursor + padding;

                case PanelSection.Spacer spacer:
                    return cursor + (spacer.Gap == PanelGap.Full ? padding : padding / 2f);

                case PanelSection.Slot slot:
                    return RenderProfileSlot(slot.Row.Label, EquipmentActions.GetAssignedDevice(pd, slot.Row.Slot), slot.Row.Slot,
                        x, cursor, w, itemH, dpiScale, fontPath, fontSize, padding, arrowW, appState, pd, emojiFontPath);

                case PanelSection.MountTelemetry:
                    return RenderMountTelemetryIfAny(appState, pd.Mount, liveSessionState,
                        x, cursor, w, itemH, dpiScale, fontPath, fontSize, padding);

                case PanelSection.DeviceSettings ds:
                    return RenderDeviceSettingsIfAny(appState, pd, ds.Device, ds.Label,
                        x, cursor, w, itemH, dpiScale, fontPath, fontSize, padding);

                case PanelSection.Site:
                    return RenderSiteSection(pd, x, cursor, w, dpiScale, fontPath, fontSize, padding, itemH, buttonH, arrowW);

                case PanelSection.GuideFocalLength:
                    return RenderGuideFocalLengthSection(pd, x, cursor, w, dpiScale, fontPath, fontSize, padding, itemH);

                case PanelSection.CameraTelemetry ct:
                    return RenderCameraTelemetryIfAny(appState, ct.Camera,
                        x, cursor, w, itemH, dpiScale, fontPath, fontSize, padding);

                case PanelSection.OtaHeader oh:
                    return RenderOtaHeaderSection(appState, pd, oh.Index, x, cursor, w, dpiScale, fontPath, fontSize, padding, itemH);

                case PanelSection.OtaProps op:
                {
                    var otaData = pd.OTAs[op.Index];
                    return State.EditingOtaIndex == op.Index
                        ? RenderOtaPropertyEditors(appState, op.Index, otaData, x, cursor, w, itemH, dpiScale, fontPath, fontSize, padding, buttonH)
                        : RenderOtaPropertiesSummary(otaData, x, cursor, w, itemH, fontPath, fontSize, padding);
                }

                case PanelSection.FilterTable ft:
                    return RenderFilterTable(appState, ft.OtaIndex, pd, x, cursor, w, itemH, dpiScale, fontPath, fontSize, padding, buttonH);

                case PanelSection.AddOta:
                    return RenderAddOtaSection(appState, pd, x, cursor, w, y, h, dpiScale, fontPath, fontSize, padding, buttonH);

                default:
                    return cursor;
            }
        }

        /// <summary>Profile-name header row. Returns the new cursor Y.</summary>
        private float RenderProfileHeaderSection(Profile profile, float x, float cursor, float w,
            string fontPath, float fontSize, float padding, float headerH)
        {
            DrawText(
                $"Profile: {profile.DisplayName}".AsSpan(),
                fontPath,
                x + padding, cursor, w - padding * 2f, headerH,
                fontSize * 1.1f, HeaderText, TextAlign.Near, TextAlign.Center);
            return cursor + headerH;
        }

        /// <summary>Site latitude/longitude/elevation block (edit fields, display row, or "set site" button). Returns the new cursor Y.</summary>
        private float RenderSiteSection(ProfileData pd, float x, float cursor, float w, float dpiScale,
            string fontPath, float fontSize, float padding, float itemH, float buttonH, float arrowW)
        {
            // Site info -- clickable to edit
            var site = EquipmentActions.GetSiteFromProfile(pd);
            if (State.IsEditingSite)
            {
                // Show editable lat/lon/elevation fields
                var fieldH = (int)(itemH * 1.2f);
                var fieldW = (int)(w - padding * 2f);
                var fieldX = (int)(x + padding);

                var labelW_site = 50f * dpiScale;
                var rowW_site = w - padding * 2f;

                var latRow = FormRowLayout.LabeledInputRow("  Lat:", labelW_site, fieldH, padding, BaseFontSize * 0.85f, DimText);
                RenderLayout(latRow, new RectF32(x + padding, cursor, rowW_site, fieldH), fontPath, dpiScale,
                    drawFill: (_, r) => RenderTextInput(State.LatitudeInput, (int)r.X, (int)r.Y, (int)r.Width, (int)r.Height, fontPath, fontSize * 0.9f));
                cursor += fieldH + 2;

                var lonRow = FormRowLayout.LabeledInputRow("  Lon:", labelW_site, fieldH, padding, BaseFontSize * 0.85f, DimText);
                RenderLayout(lonRow, new RectF32(x + padding, cursor, rowW_site, fieldH), fontPath, dpiScale,
                    drawFill: (_, r) => RenderTextInput(State.LongitudeInput, (int)r.X, (int)r.Y, (int)r.Width, (int)r.Height, fontPath, fontSize * 0.9f));
                cursor += fieldH + 2;

                var elevRow = FormRowLayout.LabeledInputRow("  Elev:", labelW_site, fieldH, padding, BaseFontSize * 0.85f, DimText);
                RenderLayout(elevRow, new RectF32(x + padding, cursor, rowW_site, fieldH), fontPath, dpiScale,
                    drawFill: (_, r) => RenderTextInput(State.ElevationInput, (int)r.X, (int)r.Y, (int)r.Width, (int)r.Height, fontPath, fontSize * 0.9f));
                cursor += fieldH + 2;

                // Tie-breaker selector: which side wins on mount connect when both have a site?
                DrawText("  Tie:".AsSpan(), fontPath, x + padding, cursor, 50f * dpiScale, itemH, fontSize * 0.85f, DimText, TextAlign.Near, TextAlign.Center);
                var tbX = fieldX + (int)(50f * dpiScale);
                var tbW = (fieldW - (int)(50f * dpiScale)) / 2 - 2;
                var isMountWins = pd.SiteTieBreaker == SiteTieBreaker.Mount;
                RenderButton("Mount", tbX, cursor, tbW, buttonH, fontPath, fontSize,
                    isMountWins ? SlotActive : CreateButton, BodyText, "TieMount",
                    _ => PostSignal(new UpdateProfileSignal(EquipmentActions.SetSiteTieBreaker(pd, SiteTieBreaker.Mount))));
                RenderButton("Profile", tbX + tbW + 4, cursor, tbW, buttonH, fontPath, fontSize,
                    !isMountWins ? SlotActive : CreateButton, BodyText, "TieProfile",
                    _ => PostSignal(new UpdateProfileSignal(EquipmentActions.SetSiteTieBreaker(pd, SiteTieBreaker.Profile))));
                cursor += buttonH + 2;

                // Save button
                var saveBtnW = Renderer.MeasureText("Save Site".AsSpan(), fontPath, fontSize).Width + padding * 4f;
                RenderButton("Save Site", x + padding, cursor, saveBtnW, buttonH, fontPath, fontSize, CreateButton, BodyText, "SaveSite",
                    _ => State.LatitudeInput.OnCommit?.Invoke(State.LatitudeInput.Text));
                cursor += buttonH + padding;
            }
            else if (site.HasValue)
            {
                var (lat, lon, elev) = site.Value;
                var latStr = lat >= 0 ? $"{lat:F1}\u00b0N" : $"{-lat:F1}\u00b0S";
                var lonStr = lon >= 0 ? $"{lon:F1}\u00b0E" : $"{-lon:F1}\u00b0W";
                var elevStr = elev.HasValue ? $", {elev.Value:F0}m" : "";
                var siteStr = $"  Site: {latStr}, {lonStr}{elevStr}";

                var siteBtnW = w - padding * 2f;
                var siteRow = Layout.Builder.HStack(
                    Layout.Builder.Text(siteStr, BaseFontSize * 0.9f, SiteText).Stretch(),
                    Layout.Builder.Text("[>]", BaseFontSize * 0.85f, DimText, TextAlign.Center, TextAlign.Center).WFixed(arrowW).HStar())
                    .WFixed(siteBtnW).HFixed(itemH)
                    .Bg(SlotNormal)
                    .Clickable(new HitResult.ButtonHit("EditSite"), _ => PostSignal(new EditSiteSignal()));
                RenderLayout(siteRow, new RectF32(x + padding, cursor, siteBtnW, itemH), fontPath, dpiScale);
                cursor += itemH;
            }
            else
            {
                // No site configured -- show "Set Site" button
                var setSiteBtnW = Renderer.MeasureText("Set Site".AsSpan(), fontPath, fontSize).Width + padding * 4f;
                var setSiteLeaf = Layout.Builder.Text("Set Site", BaseFontSize, BodyText, TextAlign.Center, TextAlign.Center)
                    .WFixed(setSiteBtnW).HFixed(buttonH)
                    .Bg(CreateButton)
                    .Clickable(new HitResult.ButtonHit("EditSite"), _ => PostSignal(new EditSiteSignal()));
                RenderLayout(setSiteLeaf, new RectF32(x + padding, cursor, setSiteBtnW, buttonH), fontPath, dpiScale);
                cursor += buttonH;
            }
            return cursor;
        }

        /// <summary>Guide-scope focal-length input row. Returns the new cursor Y.</summary>
        private float RenderGuideFocalLengthSection(ProfileData pd, float x, float cursor, float w, float dpiScale,
            string fontPath, float fontSize, float padding, float itemH)
        {
            var labelW = w * 0.35f;
            var fieldH = (int)(itemH * 0.9f);

            // Initialize from profile if not already set
            if (State.GuiderFocalLengthInput.Text.Length == 0 && pd.GuiderFocalLength is > 0)
            {
                State.GuiderFocalLengthInput.Text = pd.GuiderFocalLength.Value.ToString();
                State.GuiderFocalLengthInput.CursorPos = State.GuiderFocalLengthInput.Text.Length;
            }

            var guideRow = FormRowLayout.LabeledInputRow("Guide FL (mm):", labelW, fieldH, padding, BaseFontSize * 0.85f, DimText);
            RenderLayout(guideRow, new RectF32(x + padding, cursor, w - padding * 2f, fieldH), fontPath, dpiScale,
                drawFill: (_, r) => RenderTextInput(State.GuiderFocalLengthInput, (int)r.X, (int)r.Y, (int)r.Width, (int)r.Height, fontPath, fontSize * 0.9f));
            cursor += fieldH + 2;
            return cursor;
        }

        /// <summary>OTA section header with the Remove / Edit buttons. Returns the new cursor Y.</summary>
        private float RenderOtaHeaderSection(GuiAppState appState, ProfileData pd, int index, float x, float cursor, float w,
            float dpiScale, string fontPath, float fontSize, float padding, float itemH)
        {
            var ota = pd.OTAs[index];

            // OTA header with [Remove] + [Edit] buttons
            var isEditingOta = State.EditingOtaIndex == index;
            var editBtnW = 50f * dpiScale;
            var removeBtnW = 74f * dpiScale;
            var btnGap = 4f * dpiScale;
            var capturedI = index;

            var hub = appState.DeviceHub;
            bool OtaDeviceConnected(Uri? u) =>
                u is not null && u != NoneDevice.Instance.DeviceUri && hub?.IsConnected(u) == true;
            var otaHasConnectedDevice = OtaDeviceConnected(ota.Camera) || OtaDeviceConnected(ota.Focuser)
                || OtaDeviceConnected(ota.FilterWheel) || OtaDeviceConnected(ota.Cover);

            // Title leaf (left, takes remaining width after the two buttons)
            var titleLeaf = Layout.Builder.Text($"Telescope #{index}: {ota.Name}", BaseFontSize, HeaderText).Stretch();

            // [Remove] button leaf -- disabled grey when a device is connected; armed/normal otherwise
            Layout.Node removeLeaf;
            if (otaHasConnectedDevice)
            {
                removeLeaf = Layout.Builder.Text("Remove", BaseFontSize * 0.85f, DimmedText, TextAlign.Center, TextAlign.Center)
                    .WFixed(removeBtnW).HStar()
                    .Bg(SegmentDisabled);
                    // No Hit, no OnClick -- disabled
            }
            else
            {
                var armed = State.PendingRemoveOtaIndex == index;
                removeLeaf = Layout.Builder.Text(armed ? "Confirm?" : "Remove", BaseFontSize * 0.85f, BodyText, TextAlign.Center, TextAlign.Center)
                    .WFixed(removeBtnW).HStar()
                    .Bg(armed ? ConfirmDangerBg : RemoveButtonBg)
                    .Clickable(new HitResult.ButtonHit($"RemoveOta{index}"), _ =>
                    {
                        if (State.PendingRemoveOtaIndex == capturedI)
                        {
                            State.PendingRemoveOtaIndex = -1;
                            if (appState.ActiveProfile is { Data: { } removeData })
                            {
                                PostSignal(new UpdateProfileSignal(EquipmentActions.RemoveOTA(removeData, capturedI)));
                            }
                        }
                        else
                        {
                            State.PendingRemoveOtaIndex = capturedI;
                            appState.NeedsRedraw = true;
                        }
                    });
            }

            // Gap leaf between Remove and Edit
            var gapLeaf = Layout.Builder.Spacer().WFixed(btnGap).HStar();

            // [Edit]/[Save] toggle button leaf
            var editLabel = isEditingOta ? "Save" : "Edit";
            var editLeaf = Layout.Builder.Text(editLabel, BaseFontSize * 0.85f, BodyText, TextAlign.Center, TextAlign.Center)
                .WFixed(editBtnW).HStar()
                .Bg(EditButtonBg)
                .Clickable(new HitResult.ButtonHit($"EditOta{index}"), _ =>
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

            // Build the full header row as a horizontal Stack
            var headerRow = Layout.Builder.HStack(titleLeaf, removeLeaf, gapLeaf, editLeaf)
                .WFixed(w).HFixed(itemH)
                .Bg(OtaHeaderBg);
            RenderLayout(headerRow, new RectF32(x, cursor, w, itemH), fontPath, dpiScale);
            return cursor + itemH;
        }

        /// <summary>Add-OTA action row (drawn only if it fits before the panel bottom). Returns the new cursor Y.</summary>
        private float RenderAddOtaSection(GuiAppState appState, ProfileData pd, float x, float cursor, float w, float y, float h,
            float dpiScale, string fontPath, float fontSize, float padding, float buttonH)
        {
            // [+ Add OTA] button. "Connect All" used to share this row but now lives in the app
            // chrome (top bar) so it is reachable from every tab — see VkGuiRenderer.RenderStatusBar
            // + EquipmentActions.ComputeConnectAllStatus.
            var addOtaBtnY = cursor;
            var addOtaBtnW = 120f * dpiScale;
            if (addOtaBtnY + buttonH < y + h - padding)
            {
                RenderButton("+ Add OTA", x + padding, addOtaBtnY, addOtaBtnW, buttonH, fontPath, fontSize, CreateButton, BodyText, "AddOta",
                    _ => PostSignal(new AddOtaSignal()));
            }
            return cursor;
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

            // Device name -- pre-truncate to the name column width (the engine's Text leaf doesn't clip).
            // Name column = (1 - LabelShare) of the space left after the lead pad + indicator, matching
            // EquipmentPanelLayout.SlotRow's [pad | label .35 | name * | indicator] split.
            var nameW = (1f - EquipmentPanelLayout.LabelShare) * (w - padding - arrowW);
            var deviceLabel = EquipmentActions.DeviceLabel(deviceUri, registry: null);
            var truncated = TruncateToWidth(deviceLabel, fontPath, fontSize, nameW);

            // Right-edge indicator. When a device is assigned and we have access to the live
            // hub + discovery snapshot, draw a coloured square via FillRect (font-independent)
            // so the user sees per-slot reachability without leaving the profile panel.
            // Active assignment-mode highlight always wins and shows the original [>] arrow.
            EquipmentActions.DeviceReachability? slotReach = null;
            if (!isActive && deviceUri is not null && deviceUri != NoneDevice.Instance.DeviceUri
                && profileData is { } pd && appState is { })
            {
                var reach0 = EquipmentActions.GetReachability(pd, appState.DeviceHub,
                    State.DiscoveredDevices, deviceUri);
                if (reach0 != EquipmentActions.DeviceReachability.NotAssigned)
                {
                    slotReach = reach0;
                }
            }

            // Build the row through the SHARED EquipmentPanelLayout.SlotRow so the live GPU panel and the
            // (eventual) TUI panel render the exact same structure -- one path, guarded by the bridge's tests.
            // SlotRow works in design units, so we render it at the real DpiScale; its indicator is a Fill
            // slot we paint below (reachability dot, or the [>] arrow when unassigned / in assignment mode).
            var capturedSlot = slot;
            var capturedAppState = appState;
            var arrowColor = isActive ? AccentInstruct : DimText;
            var isAssigned = deviceUri is not null && deviceUri != NoneDevice.Instance.DeviceUri;

            var row = EquipmentPanelLayout.SlotRow(
                new DeviceSlotRow(label, truncated, isAssigned, slot),
                EquipmentPanelStyle.Default,
                activeSlot: isActive ? slot : null,
                onSlotClick: _ => _ =>
                {
                    var nowActive = State.ActiveAssignment != capturedSlot;
                    State.ActiveAssignment = nowActive ? capturedSlot : null;
                    if (nowActive && capturedAppState is not null) ScrollActiveSlotDeviceIntoView(capturedAppState);
                    if (capturedAppState is not null) capturedAppState.NeedsRedraw = true;
                });

            RenderLayout(row, new RectF32(x, y, w, itemH), fontPath, dpiScale,
                drawFill: (_, r) =>
                {
                    if (slotReach is { } reach)
                    {
                        var dotColor = reach switch
                        {
                            EquipmentActions.DeviceReachability.Connected    => ReachConnected,
                            EquipmentActions.DeviceReachability.Disconnected => ReachDisconnected,
                            EquipmentActions.DeviceReachability.Offline      => ReachOffline,
                            _                                                => DimText
                        };
                        var dotSize = MathF.Min(r.Height * 0.45f, arrowW * 0.55f);
                        var dotX = r.X + (r.Width - padding / 2f - dotSize) * 0.5f;
                        var dotY = r.Y + (r.Height - dotSize) * 0.5f;
                        FillRect(dotX, dotY, dotSize, dotSize, dotColor);
                    }
                    else
                    {
                        DrawText(">".AsSpan(), fontPath, r.X, r.Y, r.Width - padding / 2f, r.Height,
                            fontSize, arrowColor, TextAlign.Center, TextAlign.Center);
                    }
                });

            // Separator line at bottom of slot (painted on top of the row background).
            FillRect(x, y + itemH - 1f, w, 1f, SeparatorColor);

            return y + itemH;
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
            var rowW_ota = w - padding * 2f;

            // Name
            var nameRow = FormRowLayout.LabeledInputRow("  Name:", labelW, fieldH, padding, BaseFontSize * 0.85f, DimText);
            RenderLayout(nameRow, new RectF32(x + padding, cursor, rowW_ota, fieldH), fontPath, dpiScale,
                drawFill: (_, r) => RenderTextInput(State.OtaNameInput, (int)r.X, (int)r.Y, (int)r.Width, (int)r.Height, fontPath, fontSize * 0.9f));
            cursor += fieldH + 2;

            // Focal length
            var flRow = FormRowLayout.LabeledInputRow("  FL (mm):", labelW, fieldH, padding, BaseFontSize * 0.85f, DimText);
            RenderLayout(flRow, new RectF32(x + padding, cursor, rowW_ota, fieldH), fontPath, dpiScale,
                drawFill: (_, r) => RenderTextInput(State.FocalLengthInput, (int)r.X, (int)r.Y, (int)r.Width, (int)r.Height, fontPath, fontSize * 0.9f));
            cursor += fieldH + 2;

            // Aperture
            var apRow = FormRowLayout.LabeledInputRow("  Aper (mm):", labelW, fieldH, padding, BaseFontSize * 0.85f, DimText);
            RenderLayout(apRow, new RectF32(x + padding, cursor, rowW_ota, fieldH), fontPath, dpiScale,
                drawFill: (_, r) => RenderTextInput(State.ApertureInput, (int)r.X, (int)r.Y, (int)r.Width, (int)r.Height, fontPath, fontSize * 0.9f));
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

    }
}
