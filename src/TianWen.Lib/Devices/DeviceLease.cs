using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace TianWen.Lib.Devices
{
    /// <summary>
    /// A live claim on one device: while it exists, an orchestrator (a <c>Session</c>, a flat run, a
    /// polar-alignment or planetary capture) is driving that device and nothing else may disconnect or
    /// command it.
    /// <para>
    /// <b>Reads are never leased.</b> Telemetry polls, status reads and previews stay free for every
    /// observer -- the whole point of the remote-profile work is that watching a rig costs it nothing.
    /// A lease only ever refuses the two things that break a run: taking the driver away, and commanding
    /// it behind the owner's back.
    /// </para>
    /// </summary>
    /// <param name="DeviceUri">The leased device, keyed the same way the hub keys connections
    /// (scheme + authority + path, query ignored).</param>
    /// <param name="OwnerLabel">Plain-language owner, shown to the user verbatim in the refusal --
    /// e.g. <c>"the imaging session"</c> or <c>"the flat run"</c>. Phrase it to complete the sentence
    /// "... is in use by {OwnerLabel}".</param>
    /// <remarks>
    /// Deliberately carries no acquisition timestamp. It would have to come from an injected
    /// <c>ITimeProvider</c> (the repo forbids <c>DateTimeOffset.UtcNow</c>), which would add a
    /// constructor dependency to the hub that several hosts do not register -- and the acquire/release
    /// log lines already carry a timestamp, so the field would be redundant as well as costly.
    /// </remarks>
    public readonly record struct DeviceLease(Uri DeviceUri, string OwnerLabel);

    /// <summary>
    /// Thrown when something tries to disconnect a device that a run currently owns. This is the
    /// <b>unbypassable backstop</b>, not the normal path: callers are expected to ask
    /// <see cref="DeviceOwnershipGate.Evaluate"/> first and show the user
    /// <see cref="DeviceOwnershipVerdict.Describe"/>. Reaching this exception means a caller skipped
    /// the gate, which is why it is exceptional rather than a returned status.
    /// </summary>
    public sealed class DeviceLeasedException(DeviceLease lease)
        : InvalidOperationException($"Device {lease.DeviceUri} is in use by {lease.OwnerLabel} and cannot be disconnected. Stop that run first, or pass force: true.")
    {
        /// <summary>The claim that blocked the operation.</summary>
        public DeviceLease Lease { get; } = lease;
    }

    /// <summary>
    /// An all-or-nothing claim over several devices at once -- what an orchestrator takes when it starts
    /// and drops when it finishes.
    /// <para>
    /// <b>All-or-nothing matters.</b> A run that grabbed the mount and the camera but lost the filter
    /// wheel to another owner would half-own the rig and half-fail later, at some arbitrary point in the
    /// night. <see cref="Acquire"/> therefore releases everything it took before reporting the conflict,
    /// so a refused run leaves ownership exactly as it found it.
    /// </para>
    /// </summary>
    public sealed class DeviceLeaseSet : IDisposable
    {
        private readonly List<IDisposable> _held;
        private bool _disposed;

        private DeviceLeaseSet(List<IDisposable> held) => _held = held;

        /// <summary>An empty set -- nothing claimed, disposing is a no-op. Used when no hub is composed
        /// (a headless test host), so callers need no null-checking around ownership.</summary>
        public static DeviceLeaseSet Empty => new DeviceLeaseSet([]);

        /// <summary>
        /// Claims every distinct URI in <paramref name="deviceUris"/> for <paramref name="ownerLabel"/>.
        /// A null <paramref name="hub"/> yields <see cref="Empty"/>.
        /// </summary>
        /// <exception cref="DeviceLeasedException">One of the devices is already owned; nothing is left
        /// claimed.</exception>
        public static DeviceLeaseSet Acquire(IDeviceHub? hub, IEnumerable<Uri> deviceUris, string ownerLabel)
        {
            if (hub is null)
            {
                return Empty;
            }

            var held = new List<IDisposable>();
            // Distinct: the same physical device can legitimately fill two slots (an OAG guide camera that
            // is also an OTA camera), and a second claim on a URI we already hold would deadlock against
            // ourselves.
            foreach (var uri in deviceUris.Distinct())
            {
                if (hub.TryAcquireLease(uri, ownerLabel, out var lease))
                {
                    held.Add(lease);
                    continue;
                }

                foreach (var acquired in held)
                {
                    acquired.Dispose();
                }

                hub.TryGetLease(uri, out var conflicting);
                throw new DeviceLeasedException(conflicting);
            }

            return new DeviceLeaseSet(held);
        }

        /// <summary>Releases every claim. Idempotent, so a run may drop ownership early (its finaliser is
        /// done with the hardware) and still be disposed again by a <c>using</c>.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var lease in _held)
            {
                lease.Dispose();
            }

            _held.Clear();
        }
    }

    /// <summary>What a caller wants to do with a device, which changes only the wording of the refusal.</summary>
    public enum DeviceAction
    {
        /// <summary>Take the driver away (hub disconnect). Enforced by <see cref="IDeviceHub.DisconnectAsync"/>.</summary>
        Disconnect,

        /// <summary>Command the hardware out of band -- slew, jog, expose, move, filter change.</summary>
        Actuate
    }

    /// <summary>
    /// Outcome of <see cref="DeviceOwnershipGate.Evaluate"/>: whether the caller may proceed and, when
    /// not, a user-actionable explanation naming the owner.
    /// <para>
    /// <b>Default-value note:</b> a default-constructed verdict (<c>new()</c>) reads as
    /// <see cref="Allowed"/>. Always obtain one from <see cref="DeviceOwnershipGate.Evaluate"/>.
    /// </para>
    /// </summary>
    public readonly record struct DeviceOwnershipVerdict(DeviceLease? Owner, DeviceAction Action)
    {
        /// <summary>True when nothing owns the device and the caller may proceed.</summary>
        public bool Allowed => Owner is null;

        /// <summary>
        /// A plain-language, user-actionable sentence (empty when <see cref="Allowed"/>). Shared verbatim
        /// by the GUI notification, the TUI status line, the hosted API's 409 body and the Alpaca error
        /// text, so the wording can never drift between surfaces -- the same contract
        /// <see cref="ProfileSwitchVerdict.Describe"/> holds for profile switching.
        /// </summary>
        public string Describe() => Owner is not { } owner
            ? ""
            : Action switch
            {
                DeviceAction.Disconnect =>
                    $"{Label(owner)} is in use by {owner.OwnerLabel}. Stop that run before disconnecting it -- "
                    + "pulling the driver out from under a run leaves it reconnecting in a loop or failing the night.",

                _ =>
                    $"{Label(owner)} is in use by {owner.OwnerLabel}. Stop that run to control it manually -- "
                    + "two things commanding the same device at once is how a night gets lost.",
            };

        /// <summary>
        /// "Mount (FakeMount1)" -- derived from the URI alone, deliberately NOT from
        /// <see cref="IDeviceDriver.Name"/>, which on some backends (ASCOM COM) is a live driver property
        /// and must not be hit while building a UI message. Same rule as
        /// <c>ProfileSwitchGate.Label</c>.
        /// </summary>
        private static string Label(DeviceLease owner)
        {
            var uri = owner.DeviceUri;
            var kind = uri.Scheme.Length > 0
                ? char.ToUpperInvariant(uri.Scheme[0]) + uri.Scheme[1..]
                : "Device";

            var segments = uri.Segments;
            var name = segments.Length > 0
                ? Uri.UnescapeDataString(segments[^1].TrimEnd('/'))
                : uri.Host;

            return name.Length > 0 ? $"{kind} ({name})" : kind;
        }
    }

    /// <summary>
    /// The single "may I disconnect / command this device right now?" check, shared by every surface that
    /// can touch hardware: the GUI Equipment tab and sky map, the TUI, the hosted API, and (over P5) the
    /// Alpaca device plane.
    /// <para>
    /// <b>Why this exists as one shared rule.</b> Before it, five call sites each invented their own guard
    /// against <c>LiveSessionState.IsRunning</c> -- three of them a silent <c>return</c> with no message --
    /// and every one of them was wrong for the same reason: <c>IsRunning</c> is false during a flat run,
    /// which owns the hardware just as completely (that is exactly why <c>HasActiveRun</c> was added, and
    /// none of the guards used it). Polar-align and planetary capture set neither flag. A UI flag can
    /// never be the right predicate here anyway, because the hosted API and the Alpaca plane never see it.
    /// Ownership is a property of the <b>hub</b>, which is the one place that knows a driver is spoken
    /// for regardless of which orchestrator holds it and which surface is asking.
    /// </para>
    /// <para>
    /// <b>Enforcement is asymmetric, deliberately.</b> Disconnect has a single choke point
    /// (<see cref="IDeviceHub.DisconnectAsync"/>) so the hub enforces it outright and a caller that skips
    /// this gate gets a <see cref="DeviceLeasedException"/> rather than a stolen driver. Actuation has no
    /// such choke point short of wrapping every driver in a proxy, which would put an interception layer
    /// on the imaging hot path; so actuation call sites ask this gate. Both paths evaluate the same rule,
    /// which is what keeps the two from drifting.
    /// </para>
    /// <para>
    /// <b>Escalation.</b> There is no force-override on the actuation path by design. An operator who
    /// genuinely needs the hardware stops the run (abort the session / cancel the flat run) and the lease
    /// frees itself. That keeps "I am taking over" an explicit, logged act instead of a silent race.
    /// </para>
    /// </summary>
    public static class DeviceOwnershipGate
    {
        /// <summary>
        /// Evaluates the gate for one device. <paramref name="hub"/> may be null (a host with no device
        /// hub composed), in which case nothing is owned and the verdict is <see cref="DeviceOwnershipVerdict.Allowed"/>.
        /// </summary>
        public static DeviceOwnershipVerdict Evaluate(IDeviceHub? hub, Uri deviceUri, DeviceAction action)
            => hub is not null && hub.TryGetLease(deviceUri, out var lease)
                ? new DeviceOwnershipVerdict(lease, action)
                : new DeviceOwnershipVerdict(null, action);

        /// <summary>
        /// Every device currently owned, for a UI that wants to badge the whole equipment list in one pass
        /// rather than ask per row.
        /// </summary>
        public static ImmutableArray<DeviceLease> OwnedDevices(IDeviceHub? hub)
            => hub is null ? [] : [.. hub.Leases];
    }
}
