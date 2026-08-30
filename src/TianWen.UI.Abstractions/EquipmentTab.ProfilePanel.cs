using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using DIR.Lib;
using LAN.Lib;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Left profile panel: the data-driven section walk (profile header, device slots, site,
    /// guide focal length, OTA headers/properties) built into ONE arranged <see cref="Layout.Node"/>
    /// tree by RenderProfilePanel -- no per-section pixel cursor.
    /// </summary>
    partial class EquipmentTab<TSurface>
    {
        // -----------------------------------------------------------------------
        // Left panel: profile summary
        // -----------------------------------------------------------------------

        // Per-frame Fill-leaf painter dispatch for the profile panel's single RenderLayout. Keyed by the
        // Fill leaf's key; populated while building section nodes and drained by DispatchProfilePanelFill.
        // Render-thread-only (RenderProfilePanel runs on the paint path), so a plain Dictionary is safe.
        private readonly Dictionary<string, Action<RectF32>> _profilePanelFills = new();

        private void DispatchProfilePanelFill(Layout.Content.Fill fill, RectF32 r)
        {
            if (fill.Key is { } k && _profilePanelFills.TryGetValue(k, out var painter)) painter(r);
        }

        /// <summary>
        /// The whole profile panel is one arranged tree: a <see cref="Layout.Builder.VStack(System.ReadOnlySpan{Layout.Node})"/>
        /// of section nodes (from <see cref="EquipmentContent.GetProfilePanelSections"/>), padded once and
        /// rendered by a single <see cref="PixelWidgetBase{TSurface}.RenderLayout"/>. The engine stacks the
        /// sections (no <c>cursor +=</c>); text-inputs, the cooler sparkline, and the slot reachability dots
        /// are keyed <see cref="Layout.Content.Fill"/> leaves painted through <see cref="_profilePanelFills"/>.
        /// Runtime-conditional sections (mount/camera telemetry, device settings) self-gate to null (no node)
        /// when their device isn't hub-connected / declares no settings, exactly as the inline calls did.
        /// </summary>
        private void RenderProfilePanel(
            GuiAppState appState,
            RectF32 rect,
            LiveSessionState? liveSessionState = null)
        {
            var dpiScale = DpiScale;
            var padding = BasePadding * dpiScale;
            var innerX = rect.X + padding;
            var innerW = rect.Width - padding * 2f;

            var profile = appState.ActiveProfile!;
            var data = profile.Data;

            _profilePanelFills.Clear();

            Layout.Node tree;
            if (data is { } pd)
            {
                var sections = new List<Layout.Node>();
                var idx = 0;
                foreach (var section in _content.GetProfilePanelSections(pd))
                {
                    var node = BuildSection(section, appState, pd, profile, liveSessionState,
                        innerX, innerW, idx++);
                    if (node is { } n) sections.Add(n);
                }
                tree = Layout.Builder.VStack([.. sections]).Pad(BasePadding);
            }
            else
            {
                tree = Layout.Builder.VStack(
                        Layout.Builder.Text($"Profile: {profile.DisplayName}", BaseFontSize * 1.1f, HeaderText).RowH(BaseHeaderHeight),
                        SeparatorRow(),
                        Layout.Builder.Text("Profile has no data.", BaseFontSize, DimText).RowH(BaseItemHeight))
                    .Pad(BasePadding);
            }

            // Clip overflow to the panel so a tall profile can't overdraw the neighbouring device list /
            // bottom bar (this replaces the old "draw the Add OTA button only if it fits" cursor check).
            PushClip(rect.X, rect.Y, rect.Width, rect.Height);
            RenderLayout(tree, rect, drawFill: DispatchProfilePanelFill);
            PopClip();
        }

        /// <summary>
        /// Opens the profile-switcher dropdown anchored under the header: local profiles (from
        /// <see cref="EquipmentTabState.AllProfiles"/>) first, then discovered "tianwen-server" rigs
        /// (docs/plans/remote-profile.md), each labelled "(remote)". Every entry's action is captured
        /// directly against its own item (a <see cref="Profile"/> / rig label) rather than resolved
        /// later by index, so the list can never drift out of sync with a state change between open
        /// and click. Remote rows post <see cref="SelectRemoteRigSignal"/>, which binds on first select.
        /// <para>
        /// Bound-but-undiscovered rigs are listed too, marked "(offline)". A rig that is switched off
        /// must still be visible: dropping it would be indistinguishable from having lost the binding,
        /// and the user would re-bind a rig they already own.
        /// </para>
        /// </summary>
        private void OpenProfileDropdown(GuiAppState appState, float x, float y, float width)
        {
            var profiles = State.AllProfiles;
            var rigs = appState.PeerTable?.PeersOf("tianwen-server") ?? [];
            var rigLabels = LanPeer.ResolveLabels(rigs);
            var bound = State.BoundRigs;

            // Each row carries its own action, so there is no second list indexed by position: adding a
            // row cannot get out of step with what choosing it does.
            var items = ImmutableArray.CreateBuilder<DropdownItem<string>>();

            // "This computer" first, and only while a rig is on screen -- offering it otherwise would be
            // a no-op row on the overwhelmingly common local-only path.
            if (State.RemoteContextActive)
            {
                items.Add(DropdownItem<string>.Action("← This computer", () => PostSignal(new SelectLocalContextSignal())));
            }

            foreach (var p in profiles)
            {
                var profileId = p.ProfileId;
                items.Add(DropdownItem<string>.Action(p.DisplayName, () => PostSignal(new SwitchProfileSignal(profileId))));
            }

            for (var i = 0; i < rigs.Count; i++)
            {
                var label = rigLabels[i];
                items.Add(DropdownItem<string>.Action($"{label} (remote)", () => PostSignal(new SelectRemoteRigSignal(label))));
            }

            // Bound rigs discovery has not seen this run. Matched on node id, so a rig that is announcing
            // under a new name is not listed twice.
            foreach (var binding in bound)
            {
                if (rigs.Any(p => string.Equals(p.NodeId, binding.NodeId, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var alias = binding.Alias;
                items.Add(DropdownItem<string>.Action($"{alias} (offline)", () => PostSignal(new SelectRemoteRigSignal(alias))));
            }

            State.ProfileDropdown.Open(x, y, width, items.ToImmutable());
        }

        /// <summary>
        /// The "can't switch profile right now" dialog: a centred card over a dimmed backdrop showing
        /// <see cref="EquipmentTabState.ProfileSwitchBlocked"/> (the shared
        /// <see cref="ProfileSwitchVerdict.Describe"/> text) with a single [OK]. Mirrors
        /// <c>LiveSessionTab.RenderSessionPrompt</c>'s card geometry so the two modals look alike.
        /// Dismissal clears the state directly (the tab owns it) -- Esc does the same via
        /// <c>DismissActiveState</c>.
        /// </summary>
        private void RenderProfileSwitchBlocked(RectF32 contentRect, string notice, float fontSize)
        {
            var fontPath = FontPath;
            var dpiScale = DpiScale;
            var pad = BasePadding * dpiScale;
            var rowH = BaseItemHeight * dpiScale;
            var cardW = MathF.Min(contentRect.Width * 0.7f, 520f * dpiScale);
            // Six rows of body band: the longest verdict message (four named devices + the "why"
            // sentence) wraps to ~4 lines at this card width, so this leaves headroom without
            // measuring the wrap up front.
            var cardH = rowH * 6f + pad * 2f;
            var cardX = contentRect.X + (contentRect.Width - cardW) / 2f;
            var cardY = contentRect.Y + (contentRect.Height - cardH) / 2f;

            RenderLayout(Layout.Builder.Spacer().Bg(new RGBAColor32(0x00, 0x00, 0x00, 0xaa)), contentRect);
            RenderLayout(Layout.Builder.Spacer().Bg(ProfilePanelBg), new RectF32(cardX, cardY, cardW, cardH));
            // Amber accent bar: a refusal to act, not a destructive confirm.
            RenderLayout(Layout.Builder.Spacer().Bg(ConfirmForceBg), new RectF32(cardX, cardY, cardW, 2f));

            // The notice is a full sentence, so it has to wrap rather than clip. One Text node per word
            // in a WrapH flow container does that with the layout engine's own measurement -- no
            // hand-rolled break-at-width loop, and no fixed line breaks baked into the message.
            var words = notice.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var lineH = fontSize * 1.35f;
            var wordNodes = new Layout.Node[words.Length];
            for (var i = 0; i < words.Length; i++)
            {
                wordNodes[i] = Layout.Builder
                    .Text(words[i], fontSize, BodyText, TextAlign.Near, TextAlign.Center)
                    .RowH(lineH);
            }
            var spaceW = Renderer.MeasureText(" ".AsSpan(), fontPath, fontSize).Width;

            var card = Layout.Builder.VStack(
                    Layout.Builder.Text("Cannot switch profile", fontSize * 1.1f, HeaderText, TextAlign.Near, TextAlign.Center)
                        .RowH(rowH),
                    Layout.Builder.WrapH(wordNodes).WithGap(spaceW).Stretch(),
                    Layout.Builder.HStack(
                            Layout.Builder.Spacer().WStar(),
                            Layout.Builder.Text("OK", fontSize, BodyText, TextAlign.Center, TextAlign.Center)
                                .WFixed(100f * dpiScale).HStar().Bg(ConfirmCancelBg)
                                .Clickable(new HitResult.ButtonHit("ProfileSwitchBlockedOk"),
                                    _ => State.ProfileSwitchBlocked = null))
                        .RowH(rowH * 1.3f))
                .Pad(pad);

            RenderLayout(card, new RectF32(cardX, cardY, cardW, cardH), dpiScale: 1f);
        }

        /// <summary>A 1px separator line at the top of a <see cref="BasePadding"/>-tall band -- the
        /// layout-node replacement for the old keyed-Fill + hand-drawn FillRect separator.</summary>
        private static Layout.Node SeparatorRow()
            => Layout.Builder.VStack(
                    Layout.Builder.Spacer().RowH(1f).Bg(SeparatorColor),
                    Layout.Builder.Spacer().RowH(BasePadding - 1f));

        /// <summary>
        /// Builds one <see cref="PanelSection"/> as a <see cref="Layout.Node"/> (or null for a no-op /
        /// hidden section). This is the single switch the data-driven section list walks -- each case
        /// returns a self-contained sub-tree, so RenderProfilePanel just stacks them.
        /// </summary>
        private Layout.Node? BuildSection(PanelSection section, GuiAppState appState, ProfileData pd, Profile profile,
            LiveSessionState? liveSessionState, float innerX, float innerW, int idx)
        {
            switch (section)
            {
                case PanelSection.ProfileHeader:
                {
                    // Painter stashes the arranged rect (like the filter-name cell) so the dropdown --
                    // opened on the click, which fires a later frame -- anchors from the real layout
                    // position rather than a hand-computed x/y.
                    var anchor = new RectF32[1];
                    const string key = "profileHeader";
                    var label = $"Profile: {profile.DisplayName} ▾";
                    var fontSize = BaseFontSize * 1.1f * DpiScale;
                    var fontPath = FontPath;
                    _profilePanelFills[key] = r =>
                    {
                        anchor[0] = r;
                        DrawText(label.AsSpan(), fontPath, r.X, r.Y, r.Width, r.Height, fontSize, HeaderText, TextAlign.Near, TextAlign.Center);
                    };
                    return Layout.Builder.Fill(key: key).RowH(BaseHeaderHeight)
                        .Clickable(new HitResult.ButtonHit("ProfileHeader"), _ =>
                        {
                            var a = anchor[0];
                            OpenProfileDropdown(appState, a.X, a.Y + a.Height, a.Width);
                        });
                }

                case PanelSection.Separator:
                    return SeparatorRow();

                case PanelSection.Spacer spacer:
                    return Layout.Builder.Spacer().RowH(spacer.Gap == PanelGap.Full ? BasePadding : BasePadding / 2f);

                case PanelSection.Slot slot:
                    return BuildProfileSlot(slot.Row.Label, EquipmentActions.GetAssignedDevice(pd, slot.Row.Slot), slot.Row.Slot,
                        appState, pd, innerX, innerW);

                case PanelSection.MountTelemetry:
                    return BuildMountTelemetry(appState, pd.Mount, liveSessionState, innerW);

                case PanelSection.DeviceSettings ds:
                    return BuildDeviceSettingsIfAny(appState, pd, ds.Device, ds.Label);

                case PanelSection.Site:
                    return BuildSite(pd);
                case PanelSection.MountLimits:
                    return BuildMountLimits(pd);

                case PanelSection.GuideFocalLength:
                    return BuildGuideFocalLength(pd, innerW);

                case PanelSection.CameraTelemetry ct:
                    return BuildCameraTelemetry(appState, ct.Camera, innerW);

                case PanelSection.OtaHeader oh:
                    return BuildOtaHeader(appState, pd, oh.Index);

                case PanelSection.OtaProps op:
                {
                    var otaData = pd.OTAs[op.Index];
                    return State.EditingOtaIndex == op.Index
                        ? BuildOtaPropertyEditors(appState, op.Index, otaData)
                        : BuildOtaPropertiesSummary(otaData);
                }

                case PanelSection.FilterTable ft:
                    return BuildFilterTable(appState, ft.OtaIndex, pd);

                case PanelSection.AddOta:
                    return BuildAddOtaSection();

                default:
                    return null;
            }
        }

        /// <summary>Site latitude/longitude/elevation block: edit fields, a display row, or a "set site" button.</summary>
        private Layout.Node BuildSite(ProfileData pd)
        {
            var site = EquipmentActions.GetSiteFromProfile(pd);

            if (State.IsEditingSite)
            {
                float fieldH = BaseItemHeight * 1.2f;
                const float labelW = 50f;

                // The field IS the row's declaration now: no fill key, no painter entry, no dispatcher hop.
                // Note the font size is in DESIGN units here, not the DPI-scaled `fontSize` the old direct
                // RenderTextInput call needed -- the painter crosses the measure context's scale, so passing
                // a pre-scaled value would apply DPI twice.
                Layout.Node InputRow(string label, TextInputState input)
                    => FormRowLayout.LabeledInputRow(label, labelW, fieldH, 0f, BaseFontSize * 0.85f, DimText,
                        input, inputFontSize: BaseFontSize * 0.9f);

                var isMountWins = pd.SiteTieBreaker == SiteTieBreaker.Mount;
                Layout.Node TieBtn(string label, bool active, SiteTieBreaker tb, string hit) =>
                    Layout.Builder.Text(label, BaseFontSize, BodyText, TextAlign.Center, TextAlign.Center)
                        .WStar().HStar().Bg(active ? SlotActive : CreateButton)
                        .Clickable(new HitResult.ButtonHit(hit), _ => PostSignal(new UpdateProfileSignal(EquipmentActions.SetSiteTieBreaker(pd, tb))));

                // Tie-breaker: which side wins on mount connect when both have a site?
                var tieRow = Layout.Builder.HStack(
                        Layout.Builder.Text("  Tie:", BaseFontSize * 0.85f, DimText).WFixed(labelW).HStar(),
                        TieBtn("Mount", isMountWins, SiteTieBreaker.Mount, "TieMount"),
                        Layout.Builder.Spacer().WFixed(4f).HStar(),
                        TieBtn("Profile", !isMountWins, SiteTieBreaker.Profile, "TieProfile"))
                    .RowH(BaseButtonHeight);

                var saveRow = Layout.Builder.HStack(
                        Layout.Builder.Text("Save Site", BaseFontSize, BodyText, TextAlign.Center, TextAlign.Center)
                            .WFixed(72f).HStar().Bg(CreateButton)
                            .Clickable(new HitResult.ButtonHit("SaveSite"), _ => State.LatitudeInput.OnCommit?.Invoke(State.LatitudeInput.Text)),
                        Layout.Builder.Spacer().Stretch())
                    .RowH(BaseButtonHeight);

                return Layout.Builder.VStack(
                        InputRow("  Lat:", State.LatitudeInput),
                        InputRow("  Lon:", State.LongitudeInput),
                        InputRow("  Elev:", State.ElevationInput),
                        tieRow,
                        saveRow)
                    .WithGap(2f).WStar();
            }

            if (site.HasValue)
            {
                var (lat, lon, elev) = site.Value;
                var latStr = lat >= 0 ? $"{lat:F1}\u00b0N" : $"{-lat:F1}\u00b0S";
                var lonStr = lon >= 0 ? $"{lon:F1}\u00b0E" : $"{-lon:F1}\u00b0W";
                var elevStr = elev.HasValue ? $", {elev.Value:F0}m" : "";
                var siteStr = $"  Site: {latStr}, {lonStr}{elevStr}";

                return Layout.Builder.HStack(
                        Layout.Builder.Text(siteStr, BaseFontSize * 0.9f, SiteText).Stretch(),
                        Layout.Builder.Text("[>]", BaseFontSize * 0.85f, DimText, TextAlign.Center, TextAlign.Center).WFixed(BaseArrowWidth).HStar())
                    .RowH(BaseItemHeight).Bg(SlotNormal)
                    .Clickable(new HitResult.ButtonHit("EditSite"), _ => PostSignal(new EditSiteSignal()));
            }

            // No site configured -- show "Set Site" button
            return Layout.Builder.HStack(
                    Layout.Builder.Text("Set Site", BaseFontSize, BodyText, TextAlign.Center, TextAlign.Center)
                        .WFixed(64f).HStar().Bg(CreateButton)
                        .Clickable(new HitResult.ButtonHit("EditSite"), _ => PostSignal(new EditSiteSignal())),
                    Layout.Builder.Spacer().Stretch())
                .RowH(BaseButtonHeight);
        }

        /// <summary>
        /// Mount safety limits (docs/plans/mount-safety-limits.md, P1), modelled on <see cref="BuildSite"/>: a
        /// display row with <c>[&gt;]</c>, and an editor whose four numbers save together while the on/off
        /// switch and the two responses save at once through <see cref="UpdateProfileSignal"/>. The numbers
        /// are asked for in terms the user can measure -- how far past the meridian THIS rig can track before
        /// the tube meets the pier, at its lowest imaging Dec with the longest tube fitted -- because the
        /// threshold is a conservative approximation of a Dec- and tube-dependent envelope, not an exact figure.
        /// </summary>
        private Layout.Node BuildMountLimits(ProfileData pd)
        {
            if (State.IsEditingMountLimits)
            {
                float fieldH = BaseItemHeight * 1.2f;
                // Wide enough for the longest label at this font (seen truncated to "Past meridian, wa..." at 128);
                // the fields and choice buttons are Star-sized and take what is left of the panel.
                const float labelW = 210f;
                Layout.Node InputRow(string label, TextInputState input)
                    => FormRowLayout.LabeledInputRow(label, labelW, fieldH, 0f, BaseFontSize * 0.85f, DimText,
                        input, inputFontSize: BaseFontSize * 0.9f);
                // The switch and the two responses are ONE cycle button each showing the PENDING value, the way
                // a device setting is edited ("Dec Pulse as GOTO": Yes / No; an enum cycles through its values)
                // -- not a pair of pills, which painted both halves in the active colour (CreateButton IS
                // SegmentActive) and wrote the profile on every click, past Cancel.
                var state = State;
                Layout.Node CycleRow(string label, string value, string hit, Action cycle) =>
                    Layout.Builder.HStack(
                            Layout.Builder.Text(label, BaseFontSize * 0.85f, DimText).WFixed(labelW).HStar(),
                            Layout.Builder.Text(value, BaseFontSize * 0.85f, BodyText, TextAlign.Center, TextAlign.Center)
                                .WStar().HStar().Bg(EditButtonBg)
                                .Clickable(new HitResult.ButtonHit(hit), _ => cycle()))
                        .RowH(BaseButtonHeight);
                static MountLimitResponse NextResponse(MountLimitResponse r) => r switch
                {
                    MountLimitResponse.Warn => MountLimitResponse.StopTracking,
                    MountLimitResponse.StopTracking => MountLimitResponse.Park,
                    _ => MountLimitResponse.Warn,
                };
                static string ResponseLabel(MountLimitResponse r) => r switch
                {
                    MountLimitResponse.Warn => "Warn",
                    MountLimitResponse.StopTracking => "Stop tracking",
                    _ => "Park",
                };
                Layout.Node Gap() => Layout.Builder.Spacer().WFixed(4f).HStar();

                var onRow = CycleRow("  Limits:", state.LimitEnabledPending ? "Yes" : "No", "CycleLimitsEnabled",
                    () => state.LimitEnabledPending = !state.LimitEnabledPending);
                var saveRow = Layout.Builder.HStack(
                        Layout.Builder.Text("Save Limits", BaseFontSize, BodyText, TextAlign.Center, TextAlign.Center)
                            .WFixed(88f).HStar().Bg(CreateButton)
                            .Clickable(new HitResult.ButtonHit("SaveMountLimits"), _ => State.LimitMeridianWarnInput.OnCommit?.Invoke(State.LimitMeridianWarnInput.Text)),
                        Gap(),
                        Layout.Builder.Text("Cancel", BaseFontSize, BodyText, TextAlign.Center, TextAlign.Center)
                            .WFixed(60f).HStar().Bg(SlotNormal)
                            .Clickable(new HitResult.ButtonHit("CancelMountLimits"), _ => State.LimitMeridianWarnInput.OnCancel?.Invoke()),
                        Layout.Builder.Spacer().Stretch())
                    .RowH(BaseButtonHeight);
                return Layout.Builder.VStack(
                        onRow,
                        InputRow("  Past meridian, warn at (min):", State.LimitMeridianWarnInput),
                        InputRow("  ...act a further (min):", State.LimitMeridianExtraInput),
                        CycleRow("  Meridian action:", ResponseLabel(state.LimitMeridianResponsePending), "CycleMeridianResponse",
                            () => state.LimitMeridianResponsePending = NextResponse(state.LimitMeridianResponsePending)),
                        InputRow("  Horizon floor (deg):", State.LimitHorizonActionInput),
                        InputRow("  ...warn from (deg above):", State.LimitHorizonExtraInput),
                        CycleRow("  Horizon action:", ResponseLabel(state.LimitHorizonResponsePending), "CycleHorizonResponse",
                            () => state.LimitHorizonResponsePending = NextResponse(state.LimitHorizonResponsePending)),
                        saveRow)
                    .WithGap(2f).WStar();
            }

            return Layout.Builder.HStack(
                    Layout.Builder.Text("  " + EquipmentActions.DescribeMountLimits(pd.MountLimits), BaseFontSize * 0.9f, SiteText).Stretch(),
                    Layout.Builder.Text("[>]", BaseFontSize * 0.85f, DimText, TextAlign.Center, TextAlign.Center).WFixed(BaseArrowWidth).HStar())
                .RowH(BaseItemHeight).Bg(SlotNormal)
                .Clickable(new HitResult.ButtonHit("EditMountLimits"), _ => PostSignal(new EditMountLimitsSignal()));
        }

        /// <summary>Guide-scope focal-length input row.</summary>
        private Layout.Node BuildGuideFocalLength(ProfileData pd, float innerW)
        {
            var dpiScale = DpiScale;
            var labelWDesign = innerW * 0.35f / dpiScale;
            float fieldH = BaseItemHeight * 0.9f;

            // Initialize from profile if not already set
            if (State.GuiderFocalLengthInput.Text.Length == 0 && pd.GuiderFocalLength is > 0)
            {
                State.GuiderFocalLengthInput.Text = pd.GuiderFocalLength.Value.ToString();
                State.GuiderFocalLengthInput.CursorPos = State.GuiderFocalLengthInput.Text.Length;
            }

            return FormRowLayout.LabeledInputRow("Guide FL (mm):", labelWDesign, fieldH, 0f, BaseFontSize * 0.85f,
                DimText, State.GuiderFocalLengthInput, inputFontSize: BaseFontSize * 0.9f);
        }

        /// <summary>OTA section header with the Remove / Edit buttons.</summary>
        private Layout.Node BuildOtaHeader(GuiAppState appState, ProfileData pd, int index)
        {
            var ota = pd.OTAs[index];
            var isEditingOta = State.EditingOtaIndex == index;
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
                    .WFixed(74f).HStar()
                    .Bg(SegmentDisabled);
                    // No Hit, no OnClick -- disabled
            }
            else
            {
                var armed = State.PendingRemoveOtaIndex == index;
                removeLeaf = Layout.Builder.Text(armed ? "Confirm?" : "Remove", BaseFontSize * 0.85f, BodyText, TextAlign.Center, TextAlign.Center)
                    .WFixed(74f).HStar()
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

            // [Edit]/[Save] toggle button leaf
            var editLabel = isEditingOta ? "Save" : "Edit";
            var editLeaf = Layout.Builder.Text(editLabel, BaseFontSize * 0.85f, BodyText, TextAlign.Center, TextAlign.Center)
                .WFixed(50f).HStar()
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

            return Layout.Builder.HStack(
                    titleLeaf,
                    removeLeaf,
                    Layout.Builder.Spacer().WFixed(4f).HStar(),
                    editLeaf)
                .RowH(BaseItemHeight).Bg(OtaHeaderBg);
        }

        /// <summary>Add-OTA action row. "Connect All" lives in the app chrome (top bar), not here.</summary>
        private Layout.Node BuildAddOtaSection() =>
            Layout.Builder.HStack(
                    Layout.Builder.Text("+ Add OTA", BaseFontSize, BodyText, TextAlign.Center, TextAlign.Center)
                        .WFixed(120f).HStar().Bg(CreateButton)
                        .Clickable(new HitResult.ButtonHit("AddOta"), _ => PostSignal(new AddOtaSignal())),
                    Layout.Builder.Spacer().Stretch())
                .RowH(BaseButtonHeight);

        /// <summary>
        /// Builds a single profile slot row: <c>[pad | label | name | indicator]</c> (via the shared
        /// <see cref="EquipmentPanelLayout.SlotRow"/>) plus its reachability-dot / arrow indicator and the
        /// bottom separator, both painted through the keyed indicator <see cref="Layout.Content.Fill"/>.
        /// </summary>
        private Layout.Node BuildProfileSlot(
            string label, Uri? deviceUri, AssignTarget slot,
            GuiAppState appState, ProfileData pd, float innerX, float innerW)
        {
            var dpiScale = DpiScale;
            var fontPath = FontPath;
            var fontSize = BaseFontSize * dpiScale;
            var padding = BasePadding * dpiScale;
            var arrowW = BaseArrowWidth * dpiScale;
            var isActive = State.ActiveAssignment == slot;

            // Device name -- pre-truncate to the name column width (the engine's Text leaf doesn't clip).
            var nameW = (1f - EquipmentPanelLayout.LabelShare) * (innerW - padding - arrowW);
            var deviceLabel = EquipmentActions.DeviceLabel(deviceUri, registry: null);
            var truncated = TruncateToWidth(deviceLabel, fontSize, nameW);

            // Right-edge indicator. When a device is assigned + we have the live hub + discovery snapshot,
            // draw a coloured reachability square; the active-assignment highlight always wins and shows [>].
            EquipmentActions.DeviceReachability? slotReach = null;
            if (!isActive && deviceUri is not null && deviceUri != NoneDevice.Instance.DeviceUri)
            {
                var reach0 = EquipmentActions.GetReachability(pd, appState.DeviceHub, State.DiscoveredDevices, deviceUri);
                if (reach0 != EquipmentActions.DeviceReachability.NotAssigned)
                {
                    slotReach = reach0;
                }
            }

            var capturedSlot = slot;
            var capturedAppState = appState;
            var arrowColor = isActive ? AccentInstruct : DimText;
            var isAssigned = deviceUri is not null && deviceUri != NoneDevice.Instance.DeviceUri;
            var indKey = $"slotInd:{slot}";

            _profilePanelFills[indKey] = r =>
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
                // Slot bottom-border: intentionally a raw FillRect, not a layout node. It (a) runs inside
                // this drawFill callback, where RenderLayout must not re-enter, and (b) spans the full panel
                // inner width (innerX/innerW) while overlaying only the row's last pixel row -- a sibling
                // separator node would add 1px/slot and shift the EquipmentPanelLayoutTests-pinned geometry.
                // The clean layout-DSL fix is a bottom-border option on EquipmentPanelLayout.SlotRow (deferred).
                FillRect(innerX, r.Y + r.Height - 1f, innerW, 1f, SeparatorColor);
            };

            return EquipmentPanelLayout.SlotRow(
                new DeviceSlotRow(label, truncated, isAssigned, slot),
                EquipmentPanelStyle.Default,
                activeSlot: isActive ? slot : null,
                onSlotClick: _ => _ =>
                {
                    var nowActive = State.ActiveAssignment != capturedSlot;
                    State.ActiveAssignment = nowActive ? capturedSlot : null;
                    if (nowActive) ScrollActiveSlotDeviceIntoView(capturedAppState);
                    capturedAppState.NeedsRedraw = true;
                },
                indicatorFillKey: indKey);
        }

        // -----------------------------------------------------------------------
        // OTA property editors
        // -----------------------------------------------------------------------

        private Layout.Node? BuildOtaPropertiesSummary(OTAData ota)
        {
            var fRatio = ota.Aperture is > 0 ? $"f/{(double)ota.FocalLength / ota.Aperture.Value:F1}" : "";
            var designStr = ota.OpticalDesign != OpticalDesign.Unknown ? ota.OpticalDesign.ToString() : "";
            var parts = new List<string>(4);
            if (ota.FocalLength > 0) parts.Add($"{ota.FocalLength}mm");
            if (ota.Aperture is > 0) parts.Add($"\u00d8{ota.Aperture}mm");
            if (fRatio.Length > 0) parts.Add(fRatio);
            if (designStr.Length > 0) parts.Add(designStr);

            if (parts.Count == 0) return null;

            var summary = string.Join("  ", parts);
            // Indented one extra pad relative to the panel's own inset (was x + padding * 2f).
            return Layout.Builder.HStack(
                    Layout.Builder.Spacer().WFixed(BasePadding).HStar(),
                    Layout.Builder.Text(summary, BaseFontSize * 0.8f, DimText).Stretch())
                .RowH(BaseItemHeight * 0.8f);
        }

        private Layout.Node BuildOtaPropertyEditors(
            GuiAppState appState, int otaIndex, OTAData ota)
        {
            var dpiScale = DpiScale;
            var fontPath = FontPath;
            float fieldH = BaseItemHeight * 1.1f;
            const float labelW = 80f;

            Layout.Node InputRow(string label, TextInputState input)
                => FormRowLayout.LabeledInputRow(label, labelW, fieldH, 0f, BaseFontSize * 0.85f, DimText,
                    input, inputFontSize: BaseFontSize * 0.9f);

            var capturedIdx = otaIndex;
            var designLabel = $"  Design: {ota.OpticalDesign}";
            var designBtnW = Renderer.MeasureText(designLabel.AsSpan(), fontPath, BaseFontSize * 0.9f).Width + BasePadding * 4f;
            var designRow = Layout.Builder.HStack(
                    Layout.Builder.Text(designLabel, BaseFontSize * 0.9f, BodyText, TextAlign.Center, TextAlign.Center)
                        .WFixed(designBtnW).HStar().Bg(EditButtonBg)
                        .Clickable(new HitResult.ButtonHit($"CycleDesign{otaIndex}"), _ =>
                        {
                            if (appState.ActiveProfile is { } prof && prof.Data is { } data)
                            {
                                var currentOta = data.OTAs[capturedIdx];
                                var nextDesign = (OpticalDesign)(((int)currentOta.OpticalDesign + 1) % 8);
                                var newData = EquipmentActions.UpdateOTA(data, capturedIdx, opticalDesign: nextDesign);
                                PostSignal(new UpdateProfileSignal(newData));
                            }
                        }),
                    Layout.Builder.Spacer().Stretch())
                .RowH(BaseButtonHeight);

            return Layout.Builder.VStack(
                    InputRow("  Name:", State.OtaNameInput),
                    InputRow("  FL (mm):", State.FocalLengthInput),
                    InputRow("  Aper (mm):", State.ApertureInput),
                    designRow)
                .WithGap(2f).WStar();
        }

    }
}
