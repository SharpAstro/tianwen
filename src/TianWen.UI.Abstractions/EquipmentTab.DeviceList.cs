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
    /// Right device list: discovered-device rows on the ListScrollController, plus the
    /// disconnect / cooler-off confirmation strips and the segmented On|Off connect button.
    /// </summary>
    partial class EquipmentTab<TSurface>
    {
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

            var listTop    = y + headerH + padding / 2f;
            var listBottom = y + h - buttonH - padding;  // top of [Discover] button strip
            var listH      = listBottom - listTop;        // usable list height

            // Capture the list rect so the Scroll handler knows whether the wheel is over the list.
            _deviceListRect = new RectF32(x, listTop, w, listH);

            var devices  = State.DiscoveredDevices;
            var data     = appState.ActiveProfile?.Data;

            var expectedType = State.ActiveAssignment?.ExpectedDeviceType;
            // URI of the device currently assigned to the active slot (for highlighting)
            var activeSlotUri = State.ActiveAssignment is { } activeSlot && data is { } slotData
                ? EquipmentActions.GetAssignedDevice(slotData, activeSlot)
                : null;

            var totalItems = devices.Count;

            // Hand the controller this frame's geometry (viewport = list rect, one atom = one row); it owns
            // the offset + wheel/drag/thumb math, and VisibleRows() yields each visible device row's rect
            // (scrollbar column reserved, overflow clipped) -- no hand-rolled rowY / width / break here.
            _deviceScroll.SetExtent(_deviceListRect, itemH, totalItems, dpiScale);

            foreach (var (i, rowRect) in _deviceScroll.VisibleRows())
            {
                var rowY = rowRect.Y;     // re-bind so the row body's existing rowY / rowW references stand
                var rowW = rowRect.Width;

                var device  = devices[i];
                var isAssigned = data is { } assignData && EquipmentActions.IsDeviceAssigned(assignData, device.DeviceUri);
                var isWrongType = expectedType.HasValue && device.DeviceType != expectedType.Value;
                // Highlight the device currently in the active slot
                var isCurrentForSlot = DeviceBase.SameDevice(device.DeviceUri, activeSlotUri);

                // Row background -- alternate odd/even rows for readability so the On|Off
                // buttons line up visually with their device. Active-slot highlight wins.
                var baseBg = (i & 1) == 0 ? DeviceRowBg : DeviceRowBgAlt;
                // Row background as a draw-only leaf; the badge/name/status/segment content draws on top.
                // Row select is no longer a registered clickable -- a press on the row body falls through to
                // the controller (tap-on-release posts AssignDeviceSignal, drag scrolls); the On|Off segments
                // + confirm strips stay registered clickables and win first via HitTestAndDispatch.
                var rowLeaf = Layout.Builder.Spacer()
                    .Bg(isCurrentForSlot ? SlotActive : baseBg);
                RenderLayout(rowLeaf, new RectF32(x, rowY, rowW, itemH), fontPath, dpiScale);
                FillRect(x, rowY + itemH - 1f, rowW, 1f, SeparatorColor);

                // Type badge
                var badgeText = DeviceTypeBadge(device.DeviceType);
                var textColor = isWrongType ? DimmedText : BodyText;

                FillRect(x + padding, rowY + itemH * 0.15f, badgeW - padding, itemH * 0.7f, BadgeBg);
                DrawText(
                    badgeText,
                    fontPath,
                    x + padding, rowY, badgeW, itemH,
                    fontSize * 0.8f, isWrongType ? DimmedText : HeaderText, TextAlign.Center, TextAlign.Center);

                // Reserve right-edge columns: [power status][On|Off button][check assigned]
                // Even when not assigned we keep the layout stable so name widths don't jitter.
                var rightReserved = padding + checkW + padding + connBtnW + padding + statusW;

                // Device name + source moniker. The source tells the user at a glance
                // whether this discovered row is e.g. ASCOM vs native ZWO vs Canon vs
                // OnStep, which otherwise is only implied by the display name (and not
                // always -- "EQ6-R Pro" could be reached via Skywatcher-native, ASCOM,
                // or Alpaca, all with the same friendly name).
                var nameX = x + badgeW + padding;
                var sourceText = device.Source;
                var sourceW = string.IsNullOrEmpty(sourceText)
                    ? 0f
                    : Renderer.MeasureText(sourceText.AsSpan(), fontPath, fontSize * 0.75f).Width + padding;
                var nameW = rowW - badgeW - padding * 2f - rightReserved - sourceW;
                DrawText(
                    device.DisplayName.AsSpan(),
                    fontPath,
                    nameX, rowY, nameW, itemH,
                    fontSize, textColor, TextAlign.Near, TextAlign.Center);
                if (sourceW > 0f)
                {
                    DrawText(
                        sourceText.AsSpan(),
                        fontPath,
                        nameX + nameW, rowY, sourceW, itemH,
                        fontSize * 0.75f, DimmedText, TextAlign.Near, TextAlign.Center);
                }

                // Right-edge columns laid out from the right margin inward.
                var checkColX  = x + rowW - padding - checkW;
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
                // "orphans" -- useful for ad-hoc connect of e.g. Open-Meteo without first
                // wiring it into a profile slot.
                var reach = EquipmentActions.GetReachability(data, appState.DeviceHub, devices, device.DeviceUri);
                {
                    var connectUriForPending = EquipmentActions.FindAssignedUri(data, device.DeviceUri) ?? device.DeviceUri;
                    var pending = State.PendingTransitions.ContainsKey(connectUriForPending);

                    // Status indicator (display only -- answers "is hardware reachable right now?").
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
                        // The discovered device URI strips these -- we only use it to identify the device.
                        var connectUri = EquipmentActions.FindAssignedUri(data, device.DeviceUri) ?? device.DeviceUri;

                        // Pending disconnect confirmations replace the right portion of the row.
                        // Extend the strip well past the status/button/checkmark columns and
                        // into the (now-redundant) device-name area so the action labels fit
                        // comfortably -- the user already knows which device they're confirming.
                        var stripX = nameX + nameW * 0.35f;
                        var stripW = (x + rowW) - stripX - padding;

                        if (DeviceBase.SameDevice(State.PendingForceConfirm, connectUri))
                        {
                            // Stage 2: force-disconnect confirmation. [Cancel] on left,
                            // destructive [REALLY FORCE] on the right -- opposite side from
                            // where Force Off was clicked, to defeat muscle-memory escalation.
                            RenderForceConfirmStrip(connectUri, stripX, rowY, stripW, itemH,
                                fontPath, dpiScale);
                        }
                        else if (DeviceBase.SameDevice(State.PendingDisconnectConfirm, connectUri))
                        {
                            // Stage 1: warm-or-force confirmation.
                            RenderDisconnectConfirmStrip(connectUri, stripX, rowY, stripW, itemH,
                                fontPath, dpiScale, State.PendingDisconnectSafety);
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
                                fontPath, dpiScale, reach, pending, offIsUnsafe: unsafeOff);
                        }
                    }
                }
            }

            // Scrollbar: the controller draws its own track + thumb at the right edge of its viewport
            // (no-op when the list fits).
            _deviceScroll.DrawScrollBar(FillRect);

            // [Discover] button at the bottom of the list area
            var discoverBtnY = y + h - buttonH - padding;
            var discoverLabel = State.IsDiscovering ? "Discovering..." : "Discover";
            var discoverBtnW = Renderer.MeasureText("Discovering...".AsSpan(), fontPath, fontSize).Width + padding * 4f;
            var discoverBtnX = x + padding;

            RenderButton(discoverLabel, discoverBtnX, discoverBtnY, discoverBtnW, buttonH, fontPath, fontSize, CreateButton, BodyText, "Discover",
                mods => PostSignal(new DiscoverDevicesSignal(IncludeFake: (mods & InputModifier.Shift) != 0)));

            // When a Cover slot is being assigned, offer the Manual Light Panel (a hand-switched dumb
            // panel -- not discoverable) as an explicit add next to [Discover]. Once assigned it flows
            // through the ordinary Calibrator flat path, so the user can shoot manual flats with no rig.
            if (State.ActiveAssignment?.ExpectedDeviceType == DeviceType.CoverCalibrator)
            {
                const string manualLabel = "+ Manual Light Panel";
                var manualBtnW = Renderer.MeasureText(manualLabel.AsSpan(), fontPath, fontSize).Width + padding * 4f;
                var manualBtnX = discoverBtnX + discoverBtnW + padding;
                RenderButton(manualLabel, manualBtnX, discoverBtnY, manualBtnW, buttonH, fontPath, fontSize, CreateButton, BodyText, "AddManualCover",
                    _ => PostSignal(new AssignManualCoverSignal()));
            }
        }

        /// <summary>
        /// Stage-1 cooler-off confirmation strip (camera stays connected). [Warm up &amp; Off]
        /// (green), [Force] (amber, escalates), [Cancel] (neutral). Returns the strip node (one
        /// telemetry-panel row); the caller sizes its row height.
        /// </summary>
        private Layout.Node BuildCoolerOffConfirmStrip(Uri cameraUri)
        {
            var capUri = cameraUri;
            const float font = 0.78f;
            // Three equal inset pills; gap is a design unit (2 du) so it scales with DPI.
            return Layout.Builder.HStack(
                FormRowLayout.InsetPillButton("Warm up & Off", BaseFontSize * font, ConfirmWarmBg, BodyText,
                    new HitResult.ButtonHit("WarmCoolerOff"), _ => PostSignal(new WarmAndCoolerOffSignal(capUri))),
                FormRowLayout.InsetPillButton("Force", BaseFontSize * font, ConfirmForceBg, BodyText,
                    new HitResult.ButtonHit("EscalateForceCoolerOff"),
                    _ =>
                    {
                        State.PendingCoolerOffForceConfirm = capUri;
                        State.PendingCoolerOffConfirm = null;
                    }),
                FormRowLayout.InsetPillButton("Cancel", BaseFontSize * font, ConfirmCancelBg, BodyText,
                    new HitResult.ButtonHit("CancelCoolerOff"),
                    _ =>
                    {
                        State.PendingCoolerOffConfirm = null;
                        State.PendingCoolerOffForceConfirm = null;
                    }))
                .WithGap(2f);
        }

        /// <summary>
        /// Stage-2 force-cooler-off confirmation. Same anti-double-click position swap as
        /// the disconnect force flow: [Cancel] LEFT, [REALLY FORCE] RIGHT. Returns the strip node.
        /// </summary>
        private Layout.Node BuildCoolerOffForceStrip(Uri cameraUri)
        {
            var capUri = cameraUri;
            // [Cancel] LEFT, destructive [REALLY FORCE] RIGHT (anti-double-click position swap); two pills, gap 4 du.
            return Layout.Builder.HStack(
                FormRowLayout.InsetPillButton("Cancel", BaseFontSize * 0.8f, ConfirmCancelBg, BodyText,
                    new HitResult.ButtonHit("CancelForceCoolerOff"),
                    _ =>
                    {
                        State.PendingCoolerOffForceConfirm = null;
                        State.PendingCoolerOffConfirm = null;
                    }),
                FormRowLayout.InsetPillButton("\u26A0 REALLY FORCE COOLER OFF", BaseFontSize * 0.7f, ConfirmDangerBg, BodyText,
                    new HitResult.ButtonHit("ConfirmForceCoolerOff"),
                    _ =>
                    {
                        State.PendingCoolerOffForceConfirm = null;
                        PostSignal(new SetCoolerOffSignal(capUri));
                    }))
                .WithGap(4f);
        }

        /// <summary>
        /// Stage-1 confirmation strip shown when the user clicked Off on a cooled or busy
        /// camera. Three buttons: [Warm &amp; Off] (left, safe-green), [Force Off] (middle, amber),
        /// [Cancel] (right, neutral). Force Off escalates to a stage-2 confirmation rendered
        /// at a different position to prevent muscle-memory double-clicks.
        /// </summary>
        private void RenderDisconnectConfirmStrip(
            Uri deviceUri, float x, float y, float w, float h,
            string fontPath, float dpiScale,
            EquipmentActions.DisconnectSafety safety)
        {
            var safetyLabel = safety switch
            {
                EquipmentActions.DisconnectSafety.CoolerOn   => "Warm up & Off",
                EquipmentActions.DisconnectSafety.Busy       => "Wait & Off",
                EquipmentActions.DisconnectSafety.BusyAndCool=> "Wait + Warm up",
                _                                            => "Warm up & Off"
            };

            var capUri = deviceUri;
            const float font = 0.75f;
            // Order: [Warm/Wait & Off] [Force Off -> escalates to stage 2] [Cancel]; three equal inset pills, gap 2 du.
            var strip = Layout.Builder.HStack(
                FormRowLayout.InsetPillButton(safetyLabel, BaseFontSize * font, ConfirmWarmBg, BodyText,
                    new HitResult.ButtonHit("WarmDisconnect"), _ => PostSignal(new WarmAndDisconnectDeviceSignal(capUri))),
                FormRowLayout.InsetPillButton("Force", BaseFontSize * font, ConfirmForceBg, BodyText,
                    new HitResult.ButtonHit("EscalateForce"),
                    _ =>
                    {
                        State.PendingForceConfirm = capUri;
                        State.PendingDisconnectConfirm = null;
                    }),
                FormRowLayout.InsetPillButton("Cancel", BaseFontSize * font, ConfirmCancelBg, BodyText,
                    new HitResult.ButtonHit("CancelDisconnect"),
                    _ =>
                    {
                        State.PendingDisconnectConfirm = null;
                        State.PendingForceConfirm = null;
                    }))
                .WithGap(2f);
            RenderLayout(strip, new RectF32(x, y, w, h), fontPath, dpiScale);
        }

        /// <summary>
        /// Stage-2 force-disconnect confirmation. Layout deliberately swaps positions so the
        /// destructive [REALLY FORCE] button lands where [Cancel] was on the previous strip,
        /// and vice-versa -- defeats the user's muscle memory for "click the same spot twice".
        /// </summary>
        private void RenderForceConfirmStrip(
            Uri deviceUri, float x, float y, float w, float h,
            string fontPath, float dpiScale)
        {
            var capUri = deviceUri;
            // [Cancel] LEFT (where [Warm & Off] was), destructive [REALLY FORCE] RIGHT (where [Cancel] was):
            // the reversed pairing means a double-click that started on [Force Off] (middle of stage 1) lands
            // on the [Cancel] half, never the destructive button. Two equal inset pills, gap 4 du.
            var strip = Layout.Builder.HStack(
                FormRowLayout.InsetPillButton("Cancel", BaseFontSize * 0.8f, ConfirmCancelBg, BodyText,
                    new HitResult.ButtonHit("CancelForce"),
                    _ =>
                    {
                        State.PendingForceConfirm = null;
                        State.PendingDisconnectConfirm = null;
                    }),
                FormRowLayout.InsetPillButton("\u26A0 REALLY FORCE OFF", BaseFontSize * 0.75f, ConfirmDangerBg, BodyText,
                    new HitResult.ButtonHit("ConfirmForce"),
                    _ =>
                    {
                        State.PendingForceConfirm = null;
                        PostSignal(new ForceDisconnectDeviceSignal(capUri));
                    }))
                .WithGap(4f);
            RenderLayout(strip, new RectF32(x, y, w, h), fontPath, dpiScale);
        }

        /// <summary>
        /// Renders the segmented On|Off connect/disconnect button. The current state's segment
        /// is highlighted; only the *other* segment is clickable. While a transition is in flight,
        /// the inactive segment is shown as "..." and no clickables are registered.
        /// </summary>
        private void RenderConnectSegment(
            Uri deviceUri,
            float x, float y, float w, float h,
            string fontPath, float dpiScale,
            EquipmentActions.DeviceReachability reach,
            bool pending,
            bool offIsUnsafe = false)
        {
            var isConnected = reach == EquipmentActions.DeviceReachability.Connected;

            var onBg  = isConnected ? SegmentActive : SegmentInactive;
            var offBg = isConnected ? SegmentInactive : SegmentActive;
            // Telegraph that Off would land on the warm/force confirmation strip: tint the inactive Off
            // segment red. (When isConnected, Off is the actionable segment; a darker red keeps it clickable.)
            if (offIsUnsafe)
            {
                offBg = isConnected ? ConfirmDangerBg : ConfirmForceBg;
            }

            // Ellipsis on the segment we are transitioning *to*; otherwise On / Off.
            var onLabel  = pending && !isConnected ? "\u2026" : "On";
            var offLabel = pending &&  isConnected ? "\u2026" : "Off";

            // Two inset segment pills. Both register a hit (even the active "you are here" segment swallows
            // its click so it does not fall through to the row's AssignDeviceSignal); while a transition is
            // pending both are inert (null Hit, so the click falls through, matching the old early-return).
            var capturedUri = deviceUri;
            Action<InputModifier> onAction = _ => { if (!isConnected) PostSignal(new ConnectDeviceSignal(capturedUri)); };
            Action<InputModifier> offAction = _ => { if (isConnected) PostSignal(new DisconnectDeviceSignal(capturedUri)); };
            var seg = Layout.Builder.HStack(
                FormRowLayout.InsetPillButton(onLabel, BaseFontSize * 0.85f, onBg, BodyText,
                    pending ? null : new HitResult.ButtonHit("Connect"), pending ? null : onAction),
                FormRowLayout.InsetPillButton(offLabel, BaseFontSize * 0.85f, offBg, BodyText,
                    pending ? null : new HitResult.ButtonHit("Disconnect"), pending ? null : offAction))
                .WithGap(1f);
            RenderLayout(seg, new RectF32(x, y, w, h), fontPath, dpiScale);
        }

    }
}
