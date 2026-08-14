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
    /// Renderer-agnostic Equipment / Profile tab. Handles profile summary,
    /// discovered devices, device assignment, filter editing, and OTA properties.
    /// </summary>
    public partial class EquipmentTab<TSurface>(Renderer<TSurface> renderer) : PixelWidgetBase<TSurface>(renderer)
    {
        private readonly EquipmentContent _content = new EquipmentContent();

        // Layout constants (at 1x scale)
        private const float BaseProfilePanelWidth = 500f;
        private static readonly float BaseFontSize     = GuiTheme.Metrics.BaseFontSize;
        private static readonly float BaseItemHeight   = GuiTheme.Metrics.ItemHeight;
        private const float BaseBottomBarHeight   = 50f;
        private static readonly float BasePadding      = GuiTheme.Metrics.Padding;
        private static readonly float BaseHeaderHeight = GuiTheme.Metrics.HeaderHeight;
        private static readonly float BaseButtonHeight = GuiTheme.Metrics.ButtonHeight;
        private const float BaseBadgeWidth        = 68f;
        private const float BaseCheckmarkWidth    = 20f;
        private const float BaseArrowWidth        = 22f;
        private const float BaseStatusGlyphWidth  = 22f;
        private const float BaseConnectBtnWidth   = 80f;

        // Colors
        private static RGBAColor32 ProfilePanelBg   => GuiTheme.Palette.PanelBg;
        private static RGBAColor32 DeviceListBg     => GuiTheme.Palette.ContentBg;
        // Slot/OTA/filter chrome colours live on EquipmentPanelStyle.Default (the single source shared
        // with the data-driven EquipmentPanelLayout); reference them here rather than re-declaring literals.
        private static RGBAColor32 SlotNormal                => EquipmentPanelStyle.Default.SlotNormal;
        private static RGBAColor32 SlotActive                => EquipmentPanelStyle.Default.SlotActive;
        private static RGBAColor32 DeviceRowBg      => GuiTheme.Palette.PanelBg;
        private static RGBAColor32 DeviceRowBgAlt   => GuiTheme.AltRowBg;
        private static RGBAColor32 AssignedGreen    => GuiTheme.Palette.Success;
        private static RGBAColor32 DimmedText       => GuiTheme.Palette.DimText;
        private static RGBAColor32 CreateButton     => GuiTheme.Mix(GuiTheme.Palette.PanelBg, GuiTheme.Palette.Accent, 0.55f);
        private static RGBAColor32 HeaderText                => GuiTheme.Palette.HeaderText;
        private static RGBAColor32 BodyText                  => GuiTheme.Palette.BodyText;
        private static RGBAColor32 DimText                   => GuiTheme.Palette.DimText;
        private static RGBAColor32 SeparatorColor            => GuiTheme.Palette.Separator;
        private static RGBAColor32 BadgeBg          => GuiTheme.Palette.Separator;
        private static RGBAColor32 SiteText         => GuiTheme.Palette.Info;
        private static RGBAColor32 OtaHeaderBg               => EquipmentPanelStyle.Default.OtaHeaderBg;
        private static RGBAColor32 ContentBg                 => GuiTheme.Palette.ContentBg;
        private static RGBAColor32 BottomBarBg      => GuiTheme.Palette.HeaderBg;
        private static RGBAColor32 AccentInstruct   => GuiTheme.Palette.Accent;
        private static RGBAColor32 FilterTableBg             => EquipmentPanelStyle.Default.FilterTableBg;
        private static RGBAColor32 FilterRowAlt     => GuiTheme.AltRowBg;
        private static RGBAColor32 EditButtonBg     => GuiTheme.Mix(GuiTheme.Palette.PanelBg, GuiTheme.Palette.Accent, 0.45f);
        private static RGBAColor32 RemoveButtonBg   => GuiTheme.Mix(GuiTheme.Palette.PanelBg, GuiTheme.Palette.Error, 0.45f); // muted red: arm OTA removal
        // Reachability indicator colors (power glyph + segmented On|Off button highlight)
        private static RGBAColor32 ReachConnected   => GuiTheme.Palette.Success;
        private static RGBAColor32 ReachDisconnected=> GuiTheme.Palette.Warn;
        private static RGBAColor32 ReachOffline     => GuiTheme.Palette.DimText;
        private static RGBAColor32 SegmentActive    => GuiTheme.Mix(GuiTheme.Palette.PanelBg, GuiTheme.Palette.Accent, 0.55f);
        private static RGBAColor32 SegmentInactive  => GuiTheme.Palette.Separator;
        private static RGBAColor32 SegmentDisabled  => GuiTheme.AltRowBg;
        // Confirmation strip colors
        private static RGBAColor32 ConfirmWarmBg    => GuiTheme.Mix(GuiTheme.Palette.PanelBg, GuiTheme.Palette.Success, 0.45f); // green-ish: safe choice
        private static RGBAColor32 ConfirmForceBg   => GuiTheme.Mix(GuiTheme.Palette.PanelBg, GuiTheme.Palette.Warn, 0.5f); // amber: caution
        private static RGBAColor32 ConfirmDangerBg  => GuiTheme.Palette.Error; // red: destructive
        private static RGBAColor32 ConfirmCancelBg  => GuiTheme.Palette.Separator; // neutral

        /// <summary>Tab state (scroll offsets, discovery results, assignment mode).</summary>
        public EquipmentTabState State { get; } = new EquipmentTabState();

        /// <summary>Background task tracker for async operations. Set by the host.</summary>
        public BackgroundTaskTracker? Tracker { get; set; }

        // Last-rendered device-list rect, captured during render so the Scroll handler knows whether the
        // wheel is over the list. The atom-model controller (DIR.Lib) owns the offset + wheel/drag/thumb
        // math; the device list is row-snapped with an interactive scrollbar.
        private RectF32 _deviceListRect;
        private readonly ListScrollController _deviceScroll = new ListScrollController { SnapToAtom = true };

        public override bool HandleInput(InputEvent evt) => evt switch
        {
            // (The two dropdowns' key routing is gone: an open menu claims the keyboard when it paints, and
            // the host asks once. See WindowUiSettings.KeyboardClaimant.)
            // ESC dismisses any active selection/confirmation before bubbling to quit
            InputEvent.KeyDown(InputKey.Escape, _) => DismissActiveState(),
            // An open, overflowing filter-name dropdown claims the wheel first (no-op when closed / fits /
            // wheel outside its overlay); returning true redraws, mirroring the device-list wheel path.
            InputEvent.Scroll when State.FilterNameDropdown.HandleScrollInput(evt) => true,
            InputEvent.Scroll when State.ProfileDropdown.HandleScrollInput(evt) => true,
            // Wheel over the list scrolls it; the controller keeps the fractional trackpad carry.
            InputEvent.Scroll(_, var mouseX, var mouseY, _)
                when _deviceListRect.Contains(mouseX, mouseY) => _deviceScroll.HandleInput(evt),
            // Unclaimed left press (the On|Off segments + confirm strips are registered clickables that
            // win first via HitTestAndDispatch): arm the surface tap-or-drag / grab the scrollbar thumb.
            InputEvent.MouseDown(_, _, MouseButton.Left, _, _) => _deviceScroll.HandleInput(evt),
            // A drag consumes moves; when idle the controller no-ops (returns false) and moves fall through.
            InputEvent.MouseMove(_, _) when _deviceScroll.HandleInput(evt) => true,
            InputEvent.MouseUp(_, _, MouseButton.Left) => HandleDeviceListRelease(evt),
            _ => base.HandleInput(evt)
        };

        private bool HandleDeviceListRelease(InputEvent evt)
        {
            var consumed = _deviceScroll.HandleInput(evt);
            // Tap-on-release selects the device row: post the same assignment signal the old row-click did
            // (a no-op unless a profile slot is armed for assignment).
            if (_deviceScroll.TakeAtomTap() is { } atom && atom < State.DiscoveredDevices.Count)
            {
                PostSignal(new AssignDeviceSignal(atom));
            }
            return consumed;
        }

        /// <summary>
        /// When a slot on the left is activated for assignment, scroll the discovered-device
        /// list so the relevant row is visible: the device currently assigned to that slot if
        /// any, otherwise the first device of the slot's expected type. Without this the
        /// highlighted/matching row can sit off-screen (the list is not reordered per slot),
        /// so the user has to hunt for it (e.g. the Gemini cover buried below the switches).
        /// No-op if the target is already visible, so it never fights an in-progress scroll.
        /// </summary>
        private void ScrollActiveSlotDeviceIntoView(GuiAppState appState)
        {
            if (State.ActiveAssignment is not { } slot) return;

            var devices = State.DiscoveredDevices;
            if (devices.Count == 0) return;

            var assignedUri = appState.ActiveProfile?.Data is { } data
                ? EquipmentActions.GetAssignedDevice(data, slot)
                : null;

            var target = -1;
            if (assignedUri is not null)
            {
                for (var i = 0; i < devices.Count; i++)
                {
                    if (DeviceBase.SameDevice(devices[i].DeviceUri, assignedUri)) { target = i; break; }
                }
            }
            if (target < 0)
            {
                for (var i = 0; i < devices.Count; i++)
                {
                    if (devices[i].DeviceType == slot.ExpectedDeviceType) { target = i; break; }
                }
            }
            if (target < 0) return;

            // Minimal scroll to bring the target row into view (no-op if already visible), against the
            // controller's geometry from the last render.
            _deviceScroll.EnsureVisible(target);
        }

        /// <summary>
        /// Clears any active selection, assignment mode, confirmation strip, or expanded
        /// device settings. Returns true (consumed) if anything was dismissed, false otherwise
        /// so ESC can bubble up to the global quit handler.
        /// </summary>
        private bool DismissActiveState()
        {
            var dismissed = false;

            // Modal first: while the refused-switch dialog is up it is the only thing Esc should close.
            if (State.ProfileSwitchBlocked is not null)
            {
                State.ProfileSwitchBlocked = null;
                return true;
            }

            if (State.ActiveAssignment is not null)
            {
                State.ActiveAssignment = null;
                dismissed = true;
            }

            if (State.ExpandedDeviceSettingsUri is not null)
            {
                State.StopEditingDeviceSettings();
                dismissed = true;
            }

            if (State.PendingDisconnectConfirm is not null)
            {
                State.PendingDisconnectConfirm = null;
                State.PendingForceConfirm = null;
                dismissed = true;
            }

            if (State.PendingCoolerOffConfirm is not null)
            {
                State.PendingCoolerOffConfirm = null;
                State.PendingCoolerOffForceConfirm = null;
                dismissed = true;
            }

            if (State.IsCreatingProfile)
            {
                State.IsCreatingProfile = false;
                dismissed = true;
            }

            if (State.IsEditingSite)
            {
                State.IsEditingSite = false;
                dismissed = true;
            }

            if (State.PendingRemoveOtaIndex >= 0)
            {
                State.PendingRemoveOtaIndex = -1;
                dismissed = true;
            }

            return dismissed;
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
            LiveSessionState? liveSessionState = null)
        {
            // Clear clickable regions from previous frame
            BeginFrame();

            var dpiScale = DpiScale;
            var fontPath = FontPath;

            // Clear the whole content area first
            RenderLayout(Layout.Builder.Spacer().Bg(ContentBg), contentRect);

            if (appState.ActiveProfile is null && !State.IsCreatingProfile)
            {
                RenderNoProfile(contentRect);
                return;
            }

            if (State.IsCreatingProfile)
            {
                RenderProfileCreation(contentRect);
                return;
            }

            RenderProfileView(appState, contentRect, liveSessionState);

            // Dropdown overlay -- rendered absolutely last so it paints on top of everything
            var fontSize = BaseFontSize * dpiScale;
            RenderDropdownMenu(State.FilterNameDropdown, fontPath, fontSize * 0.85f,
                FilterTableBg, SlotActive, BodyText, SeparatorColor,
                viewportWidth: contentRect.X + contentRect.Width,
                viewportHeight: contentRect.Y + contentRect.Height);
            RenderDropdownMenu(State.ProfileDropdown, fontPath, fontSize * 0.85f,
                FilterTableBg, SlotActive, BodyText, SeparatorColor,
                viewportWidth: contentRect.X + contentRect.Width,
                viewportHeight: contentRect.Y + contentRect.Height);

            // Refused-profile-switch modal goes on top of even the dropdown (the dropdown is what
            // raised it, and its own selection already closed it).
            if (State.ProfileSwitchBlocked is { Length: > 0 } blocked)
            {
                RenderProfileSwitchBlocked(contentRect, blocked, fontSize);
            }
        }

        // -----------------------------------------------------------------------
        // No-profile view
        // -----------------------------------------------------------------------

        private void RenderNoProfile(
            RectF32 rect)
        {
            // Message + Create button vertically centred by equal star spacers; the button is
            // centred horizontally by star spacers around a fixed-width cell. No manual centre math.
            const float buttonW = 160f;

            // When local profiles already exist (just none auto-selected -- Program.cs only
            // auto-picks when there's exactly one), offer them directly instead of stranding the
            // user with only "Create Profile". Same SwitchProfileSignal the header dropdown uses.
            var profiles = State.AllProfiles;
            Layout.Node[] pickerRows = profiles.Count == 0
                ? []
                :
                [
                    Layout.Builder.Spacer().RowH(BasePadding * 2f),
                    Layout.Builder.Text("Or pick an existing profile:", BaseFontSize * 0.9f, DimText, TextAlign.Center, TextAlign.Center)
                        .RowH(BaseItemHeight),
                    .. profiles.Select(p =>
                    {
                        var id = p.ProfileId;
                        return Layout.Builder.HStack(
                                Layout.Builder.Spacer().WStar(),
                                Layout.Builder.Text(p.DisplayName, BaseFontSize, BodyText, TextAlign.Center, TextAlign.Center)
                                    .WFixed(buttonW).HStar().Bg(SlotNormal)
                                    .Clickable(new HitResult.ButtonHit($"SwitchProfile:{id}"), _ => PostSignal(new SwitchProfileSignal(id))),
                                Layout.Builder.Spacer().WStar())
                            .RowH(BaseButtonHeight * 0.85f);
                    }),
                ];

            var tree = Layout.Builder.VStack(
                [
                    Layout.Builder.Spacer().Stretch(),
                    Layout.Builder.Text("No equipment profile configured.", BaseFontSize, DimText, TextAlign.Center, TextAlign.Center)
                        .RowH(BaseItemHeight * 1.5f),
                    Layout.Builder.Spacer().RowH(BasePadding),
                    Layout.Builder.HStack(
                            Layout.Builder.Spacer().WStar(),
                            Layout.Builder.Text("Create Profile", BaseFontSize, BodyText, TextAlign.Center, TextAlign.Center)
                                .WFixed(buttonW).HStar().Bg(CreateButton)
                                .Clickable(new HitResult.ButtonHit("CreateProfile"), _ => PostSignal(new CreateProfileSignal())),
                            Layout.Builder.Spacer().WStar())
                        .RowH(BaseButtonHeight),
                    .. pickerRows,
                    Layout.Builder.Spacer().Stretch(),
                ]);

            RenderLayout(tree, rect);
        }

        // -----------------------------------------------------------------------
        // Profile creation view
        // -----------------------------------------------------------------------

        private void RenderProfileCreation(
            RectF32 rect)
        {
            // The creation form is ONE arranged tree: header + label + name field + Create button stack
            // from the top of the Dock fill (padded VStack), with a hint status bar docked to the bottom.
            // The field is a TextInput leaf and the button a Clickable Text node, so the form IS the tree --
            // there is nothing left for a draw callback to do.
            var content = Layout.Builder.VStack(
                    Layout.Builder.Text("New Equipment Profile", BaseFontSize * 1.2f, HeaderText).RowH(BaseHeaderHeight),
                    Layout.Builder.Spacer().RowH(BasePadding),
                    Layout.Builder.Text("Profile name:", BaseFontSize, BodyText).RowH(BaseItemHeight),
                    Layout.Builder.HStack(
                            Layout.Builder.TextInput(State.ProfileNameInput, BaseFontSize).WStar(1f, 0f, 360f).HStar(),
                            Layout.Builder.Spacer().WStar())
                        .RowH(BaseItemHeight * 1.4f),
                    Layout.Builder.Spacer().RowH(BasePadding),
                    Layout.Builder.HStack(
                            Layout.Builder.Text("Create", BaseFontSize, BodyText, TextAlign.Center, TextAlign.Center)
                                .WFixed(120f).HStar().Bg(CreateButton)
                                .Clickable(new HitResult.ButtonHit("CreateProfile"), _ =>
                                {
                                    if (State.ProfileNameInput.Text.Length > 0)
                                        State.ProfileNameInput.OnCommit?.Invoke(State.ProfileNameInput.Text);
                                }),
                            Layout.Builder.Spacer().WStar())
                        .RowH(BaseButtonHeight),
                    Layout.Builder.Spacer().Stretch())
                .Pad(BasePadding);

            var tree = Layout.Builder.Dock(
                content,
                Layout.Builder.Bottom(
                    Layout.Builder.HStack(
                            Layout.Builder.Text("Enter a name for the new profile and press Create or Enter.",
                                BaseFontSize, DimText, TextAlign.Near, TextAlign.Center).Stretch())
                        .Bg(BottomBarBg).Pad(BasePadding),
                    BaseBottomBarHeight));

            RenderLayout(tree, rect);
        }

        // -----------------------------------------------------------------------
        // Full profile view
        // -----------------------------------------------------------------------

        private void RenderProfileView(
            GuiAppState appState,
            RectF32 contentRect,
            LiveSessionState? liveSessionState = null)
        {
            // The whole view is ONE arranged tree: a bottom-docked hint bar over a body row of
            // [profile panel | 1px separator | device list]. Each region is a keyed Fill leaf whose
            // arranged rect drives the existing sub-renderer; the backgrounds ride on the nodes. The
            // only constructed rect is the host content rect -- no PixelLayout docking cursor.
            var tree = Layout.Builder.Dock(
                Layout.Builder.HStack(
                    Layout.Builder.Fill(key: "profilePanel").WFixed(BaseProfilePanelWidth).HStar().Bg(ProfilePanelBg),
                    Layout.Builder.Spacer().WFixed(1f).HStar().Bg(SeparatorColor),
                    Layout.Builder.Fill(key: "deviceList").WStar().HStar().Bg(DeviceListBg)),
                Layout.Builder.Bottom(
                    Layout.Builder.Fill(key: "bottomBar").Bg(BottomBarBg), BaseBottomBarHeight));

            RenderLayout(tree, contentRect, drawFill: (fill, r) =>
            {
                switch (fill.Key)
                {
                    case "profilePanel":
                        RenderProfilePanel(appState, r, liveSessionState);
                        break;
                    case "deviceList":
                        RenderDeviceList(appState, r);
                        break;
                    case "bottomBar":
                        RenderBottomBar(appState, r);
                        break;
                }
            });
        }

        /// <summary>
        /// Truncates <paramref name="text"/> with a trailing ellipsis so its rendered width
        /// fits within <paramref name="maxWidth"/>. Returns the original string when it already fits.
        /// Falls back gracefully on tiny widths by clipping characters until just the ellipsis remains.
        /// </summary>
        private string TruncateToWidth(string text, float fontSize, float maxWidth)
        {
            if (text.Length == 0 || maxWidth <= 0f) return text;
            var fontPath = FontPath;
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

        // -----------------------------------------------------------------------
        // Bottom bar
        // -----------------------------------------------------------------------

        private void RenderBottomBar(
            GuiAppState appState,
            RectF32 rect)
        {
            var dpiScale = DpiScale;
            var fontPath = FontPath;
            var fontSize = BaseFontSize * dpiScale;
            var padding  = BasePadding * dpiScale;

            var x = rect.X;
            var y = rect.Y;
            var w = rect.Width;
            var h = rect.Height;

            string message;
            RGBAColor32 textColor;

            // Note: appState.StatusMessage is rendered in the chrome's top status bar -- don't
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
                DeviceType.Switch         => "Switch",
                DeviceType.Weather        => "WX",
                DeviceType.Profile        => "Profile",
                _                         => "?"
            };
    }
}
