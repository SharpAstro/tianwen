using System;
using System.Collections.Immutable;
using System.Linq;

namespace TianWen.Lib.Devices
{
    /// <summary>Why changing the active profile is refused right now.</summary>
    public enum ProfileSwitchBlocker
    {
        /// <summary>Nothing in the way -- the switch is safe.</summary>
        None,

        /// <summary>A session (or an out-of-session run that owns the equipment, e.g. a flat run)
        /// is active.</summary>
        SessionActive,

        /// <summary>Devices are connected in the hub under the profile being switched away from.</summary>
        DevicesConnected
    }

    /// <summary>
    /// Outcome of <see cref="ProfileSwitchGate.Evaluate"/>: whether the switch may proceed and, when
    /// not, a user-actionable explanation naming what is in the way.
    /// <para>
    /// <b>Default-value note:</b> a default-constructed verdict (<c>new()</c>) reads as
    /// <see cref="Allowed"/> with a <c>default</c> (not empty) <see cref="ConnectedDevices"/>, so
    /// <see cref="Describe"/> guards with <c>IsDefaultOrEmpty</c> rather than <c>Length</c>. Always
    /// obtain one from <see cref="ProfileSwitchGate.Evaluate"/>.
    /// </para>
    /// </summary>
    public readonly record struct ProfileSwitchVerdict(
        ProfileSwitchBlocker Blocker,
        ImmutableArray<string> ConnectedDevices)
    {
        /// <summary>True when nothing blocks the switch.</summary>
        public bool Allowed => Blocker is ProfileSwitchBlocker.None;

        /// <summary>How many device names <see cref="Describe"/> spells out before eliding the rest,
        /// so the message stays inside a dialog card / one status line.</summary>
        private const int MaxNamedDevices = 4;

        /// <summary>
        /// A plain-language, user-actionable sentence for the blocker (empty when
        /// <see cref="Allowed"/>): what is in the way and what to do about it. Shared verbatim by the
        /// GUI dialog, the TUI status line, and the hosted API's 409 body, so the wording can never
        /// drift between surfaces.
        /// </summary>
        public string Describe() => Blocker switch
        {
            ProfileSwitchBlocker.SessionActive =>
                "A session is running. Stop it before switching profiles -- the running session owns "
                + "the equipment configured by the current profile.",

            ProfileSwitchBlocker.DevicesConnected =>
                $"Disconnect equipment first ({NameList()}). Switching profiles while devices are "
                + "connected would leave them running with no UI surface to control or warm them down.",

            _ => ""
        };

        private string NameList()
        {
            if (ConnectedDevices.IsDefaultOrEmpty) return "no devices";

            var named = string.Join(", ", ConnectedDevices.Take(MaxNamedDevices));
            var rest = ConnectedDevices.Length - MaxNamedDevices;
            return rest > 0 ? $"{named} and {rest} more" : named;
        }
    }

    /// <summary>
    /// The single "may I re-bind THIS node's active profile right now?" check, shared by every surface
    /// that can switch (GUI profile dropdown, TUI profile picker, hosted
    /// <c>PUT /api/v1/session/profile</c>).
    /// <para>
    /// <b>Scope: local profiles only.</b> This gates a <i>rebind of the local node's equipment</i>, not
    /// "changing what you are looking at". Selecting a <b>remote</b> rig (docs/plans/remote-profile.md)
    /// is a view-context overlay -- the local session keeps running underneath, untouched -- so it must
    /// NEVER be gated, and returning to the local context must be equally free. The two are separate
    /// signals for exactly this reason (<c>SwitchProfileSignal</c> carries a local profile id and is
    /// gated; <c>SelectRemoteRigSignal</c> is not). The single-session invariant is <b>per node</b>: the
    /// local node owns at most one session, each rig owns its own, and a client may observe several
    /// while owning only its local one.
    /// </para>
    /// <para>
    /// <b>Why this gate exists.</b> Every host assumes a <i>single</i> profile context: connected
    /// drivers, camera telemetry buffers, filter-edit state, planner site/timezone and the live-session
    /// preview all key off the active profile. Swapping it underneath connected hardware orphans those
    /// drivers -- they stay connected in the <see cref="IDeviceHub"/> while no UI surface references
    /// their URIs any more, so the user can no longer disconnect them, warm a cooler down, or close a
    /// cover: the equipment is stranded until the process exits. A running session is the same hazard
    /// one level up (it holds borrowed drivers for the whole night).
    /// </para>
    /// <para>
    /// Connected-devices is the load-bearing check and a running session is strictly a subset of it (a
    /// session cannot run without connected drivers), but session-active is reported separately because
    /// "stop the session" is a different instruction than "disconnect the mount".
    /// </para>
    /// <para>
    /// A one-shot CLI command needs no gate: <c>ProfileSelector</c> resolves the profile once at
    /// startup, before anything is connected.
    /// </para>
    /// </summary>
    public static class ProfileSwitchGate
    {
        /// <summary>
        /// Evaluates the gate. <paramref name="hub"/> may be null (a host with no device hub composed
        /// -- then only <paramref name="sessionActive"/> can block). <paramref name="sessionActive"/> is
        /// the caller's own notion of "a run owns the equipment": the GUI/TUI pass
        /// <c>LiveSessionState.HasActiveRun</c>, the hosted API passes
        /// <c>IHostedSession.CurrentSession is not null</c>.
        /// </summary>
        public static ProfileSwitchVerdict Evaluate(IDeviceHub? hub, bool sessionActive)
        {
            ImmutableArray<string> connected = hub is null
                ? []
                : [.. hub.ConnectedDevices.Select(static d => Label(d.DeviceUri, d.Driver))];

            var blocker = sessionActive
                ? ProfileSwitchBlocker.SessionActive
                : connected.Length > 0
                    ? ProfileSwitchBlocker.DevicesConnected
                    : ProfileSwitchBlocker.None;

            return new ProfileSwitchVerdict(blocker, connected);
        }

        /// <summary>
        /// "Camera (FakeCamera1)" -- device kind plus the URI's device-name segment. Deliberately
        /// derived from the <b>URI</b> and the driver's local <see cref="IDeviceDriver.DriverType"/>
        /// rather than <see cref="IDeviceDriver.Name"/>, which on some backends (ASCOM COM) is a live
        /// driver property and must not be hit while building a UI message.
        /// </summary>
        private static string Label(Uri deviceUri, IDeviceDriver driver)
        {
            var segments = deviceUri.Segments;
            var name = segments.Length > 0
                ? Uri.UnescapeDataString(segments[^1].TrimEnd('/'))
                : deviceUri.Host;

            return name.Length > 0 ? $"{driver.DriverType} ({name})" : driver.DriverType.ToString();
        }
    }
}
