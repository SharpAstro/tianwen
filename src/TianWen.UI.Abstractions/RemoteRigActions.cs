using LAN.Lib;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Extensions;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Binding and connecting to remote rigs -- the business logic behind
    /// <see cref="SelectRemoteRigSignal"/>, kept out of the subscribe lambda per the
    /// route-don't-implement rule (CLAUDE.md, "Signal Handler Pattern").
    /// </summary>
    public static class RemoteRigActions
    {
        /// <summary>What a select attempt produced, ready for the notification feed.</summary>
        public readonly record struct SelectOutcome(NotificationSeverity Severity, string Message);

        /// <summary>
        /// Binds (on first use) and connects to the rig announced as <paramref name="displayName"/>, then
        /// puts it on screen.
        /// <para>
        /// <b>Binding happens by looking, not by a setup step.</b> Selecting a discovered rig writes a
        /// <see cref="RemoteRigBinding"/> keyed on its stable node id, so the rig survives a rename or a
        /// new DHCP lease and reappears in the picker next run even if it is offline then. Re-selecting a
        /// rig already on screen is a no-op rather than a reconnect -- clicking the current context
        /// should never cost a poll cycle.
        /// </para>
        /// <para>
        /// The binding defaults to <see cref="RemoteRigBinding.RemoteProfileId"/> <c>null</c> = "mirror
        /// whatever it runs", which is the right default for a rig that plans its own nights: pinning a
        /// profile id here would make the binding wrong the moment the rig switched profiles itself.
        /// Choosing a specific profile is the drive-mode step, taken deliberately afterwards.
        /// </para>
        /// </summary>
        public static async Task<SelectOutcome> SelectAsync(
            string displayName,
            RemoteRigRegistry rigs,
            ViewContexts contexts,
            GuiAppState appState,
            IExternal external,
            ITimeProvider timeProvider,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            var binding = FindBinding(rigs, appState.PeerTable, displayName);
            if (binding is null)
            {
                return new SelectOutcome(NotificationSeverity.Warning,
                    $"'{displayName}' is no longer being announced on the network.");
            }

            // Already connected: just look at it.
            if (rigs.Find(binding.BindingId) is { } existing)
            {
                contexts.Activate(existing.Context);
                return new SelectOutcome(NotificationSeverity.Info, $"Watching {binding.Alias}");
            }

            var attempt = await ConnectAndPersistAsync(
                binding, rigs, contexts, appState, external, timeProvider, logger, cancellationToken)
                .ConfigureAwait(false);

            var saveWarning = attempt.SaveFailed ? DescribeSaveFailure([binding.Alias]) : null;

            if (attempt.Connection is not { } connection)
            {
                var offline = $"{binding.Alias} is offline{DescribeLastSeen(binding, timeProvider.GetUtcNow())}.";
                return new SelectOutcome(NotificationSeverity.Warning,
                    saveWarning is null ? offline : $"{offline} {saveWarning}");
            }

            contexts.Activate(connection.Context);

            // The severity follows the write, not the connect: watching the rig succeeded either way, so
            // a clean select stays Info and only a lost binding escalates.
            return saveWarning is null
                ? new SelectOutcome(NotificationSeverity.Info, $"Watching {binding.Alias} at {connection.Address}")
                : new SelectOutcome(NotificationSeverity.Warning,
                    $"Watching {binding.Alias} at {connection.Address}. {saveWarning}");
        }

        /// <summary>How a connect-all sweep went, for the log and the notification feed.</summary>
        /// <param name="Connected">Rigs that gained a mirror on this sweep.</param>
        /// <param name="Offline">Rigs with no reachable address, whose bindings were kept anyway.</param>
        /// <param name="Unsaved">
        /// Aliases whose binding file could not be written. <b>Empty is carried as
        /// <see langword="default"/>, not <c>ImmutableArray&lt;string&gt;.Empty</c></b>, because
        /// <c>ImmutableArray</c> equality is reference equality on the backing array -- the two are not
        /// interchangeable, and only the default keeps an all-clear sweep equal to a plainly-constructed
        /// <c>new ConnectAllOutcome(n, m)</c>.
        /// </param>
        public readonly record struct ConnectAllOutcome(
            int Connected,
            int Offline,
            ImmutableArray<string> Unsaved = default)
        {
            /// <summary>True when the sweep had something to do.</summary>
            public bool DidAnything => Connected > 0 || Offline > 0;

            /// <summary>
            /// The warning for the notification feed, or null when every binding was written. A sweep
            /// runs unattended at startup, so a silent failure here would surface as rigs quietly
            /// missing from the picker on some later run.
            /// </summary>
            public string? DescribeUnsaved() => Unsaved.IsDefaultOrEmpty ? null : DescribeSaveFailure(Unsaved);
        }

        /// <summary>
        /// Starts a mirror for <b>every</b> bound rig that does not already have one, and activates
        /// none of them.
        /// <para>
        /// <b>This is the one place connecting is decoupled from looking.</b> <see cref="SelectAsync"/>
        /// connects and then calls <see cref="ViewContexts.Activate"/>, because picking a rig from the
        /// picker means "show me this". A dashboard needs the opposite: N rigs live at once while the
        /// view stays wherever the user left it. A binding on its own carries no live state -- alias,
        /// node id, last address and <see cref="RemoteRigBinding.LastSeenUtc"/> from disk -- so phase,
        /// target, frame counts, guide RMS and the outstanding-prompt badge all require a running
        /// <c>RemoteSessionMirror</c>. Without this sweep a board would render N cards showing nothing
        /// until each was clicked, and clicking would move the view.
        /// </para>
        /// <para>
        /// <b>Previews stay off.</b> <c>RemoteSessionMirror.Previews</c> is opt-in and nothing here sets
        /// it, so a sweep costs one small state poll per rig per tick and never a JPEG. N mirrors each
        /// pulling frames is the failure mode this design exists to avoid.
        /// </para>
        /// <para>
        /// Cheap to call: <see cref="RemoteRigConnection.TryConnect"/> makes no HTTP request at all, it
        /// resolves an endpoint and starts a poll loop. Best-effort per rig -- one unreachable rig or one
        /// unwritable binding file must not stop the others -- and idempotent, so calling it again after
        /// a rig comes online picks up only what is still missing.
        /// </para>
        /// </summary>
        public static async Task<ConnectAllOutcome> ConnectAllAsync(
            RemoteRigRegistry rigs,
            ViewContexts contexts,
            GuiAppState appState,
            IExternal external,
            ITimeProvider timeProvider,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            var connected = 0;
            var offline = 0;
            List<string>? unsaved = null;

            foreach (var binding in rigs.Bindings)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (rigs.IsConnected(binding.BindingId))
                {
                    continue;
                }

                var attempt = await ConnectAndPersistAsync(
                    binding, rigs, contexts, appState, external, timeProvider, logger, cancellationToken)
                    .ConfigureAwait(false);

                if (attempt.Connection is null)
                {
                    offline++;
                }
                else
                {
                    connected++;
                }

                if (attempt.SaveFailed)
                {
                    (unsaved ??= []).Add(binding.Alias);
                }
            }

            // Built lazily so the ordinary all-clear sweep reports `default` rather than an empty array
            // (see the remarks on ConnectAllOutcome.Unsaved for why those are not the same value).
            ImmutableArray<string> unsavedAliases = unsaved is null ? default : [.. unsaved];

            var outcome = new ConnectAllOutcome(connected, offline, unsavedAliases);
            if (outcome.DidAnything)
            {
                logger.LogInformation(
                    "Rig sweep: mirroring {Connected}, {Offline} with no reachable address, {Unsaved} binding(s) not saved",
                    connected, offline, unsaved?.Count ?? 0);
            }

            return outcome;
        }

        /// <summary>
        /// The one phrasing of "the binding file could not be written", shared by the select path and the
        /// sweep so the same failure cannot be reported two different ways. Callers pass at least one
        /// alias.
        /// <para>
        /// Worth a warning even when the connect itself succeeded: the rig is watchable right now and
        /// gone after a restart, and that discrepancy would otherwise be noticed days later, long after
        /// the log line explaining it has rolled away.
        /// </para>
        /// </summary>
        private static string DescribeSaveFailure(ImmutableArray<string> aliases) => aliases switch
        {
            [var only] => $"Could not save the binding for {only}: it will not reappear after a restart (see the log).",
            _ => $"Could not save {aliases.Length} rig bindings ({string.Join(", ", aliases)}): "
                 + "they will not reappear after a restart (see the log).",
        };

        /// <summary>
        /// One connect attempt: the mirror that was started (null when the rig had no usable address),
        /// and whether persisting its binding failed in a way worth telling the user about.
        /// </summary>
        private readonly record struct ConnectAttempt(RemoteRigConnection? Connection, bool SaveFailed);

        /// <summary>
        /// Connects one binding and records where it was reached, without deciding what is on screen.
        /// <see cref="ConnectAttempt.Connection"/> is null when the rig has no usable address (i.e. it is
        /// offline).
        /// <para>
        /// The binding is kept and persisted either way: "I own this rig and it is not answering" is
        /// information, and dropping it would look like the binding was lost rather than the rig being
        /// off. On success it is persisted with wherever the rig was actually reached, so the next run
        /// can try that address before discovery has caught up -- but <b>not</b> with a last-seen stamp,
        /// because nothing has answered yet; that lands on the first successful poll (see
        /// <see cref="RemoteRigConnection.TryClaimFirstContact"/>).
        /// </para>
        /// <para>
        /// The save is best-effort but not silent. Failing to write a binding file should not deny the
        /// caller the connection it asked for, and in a sweep one unwritable file must not abort the
        /// remaining rigs -- so it is reported back for a warning rather than thrown.
        /// </para>
        /// </summary>
        private static async Task<ConnectAttempt> ConnectAndPersistAsync(
            RemoteRigBinding binding,
            RemoteRigRegistry rigs,
            ViewContexts contexts,
            GuiAppState appState,
            IExternal external,
            ITimeProvider timeProvider,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            var connection = RemoteRigConnection.TryConnect(
                binding, contexts, appState.PeerTable, timeProvider, logger, cancellationToken);

            if (connection is not null)
            {
                rigs.Attach(connection);
            }

            var toPersist = connection?.BindingAsReached() ?? binding;
            rigs.Upsert(toPersist);
            var saved = await logger.CatchAsync(
                ct => RemoteRigPersistence.SaveAsync(toPersist, external, ct), cancellationToken)
                .ConfigureAwait(false);

            // A write cancelled by shutdown is not a failure to warn about -- the user quit, and telling
            // them a binding was lost on the way out would be noise about their own action.
            return new ConnectAttempt(connection, SaveFailed: !saved && !cancellationToken.IsCancellationRequested);
        }

        /// <summary>
        /// The parenthesised "(last seen ...)" tail for an offline rig, or an empty string when we have
        /// neither a time nor an address to report.
        /// <para>
        /// Prefers the age over the address because that is the question actually being asked -- "is this
        /// rig off for the night or has it been dead for a month" -- and falls back to the address for a
        /// binding written before it was ever reached. Both together would just be noise.
        /// </para>
        /// </summary>
        public static string DescribeLastSeen(RemoteRigBinding binding, DateTimeOffset now) =>
            binding.LastSeenUtc is { } seen ? $" (last seen {FormatAge(seen, now)})"
            : binding.LastAddress is { } address ? $" (last seen at {address})"
            : "";

        /// <summary>
        /// A coarse human age: "moments ago" / "12 min ago" / "3 h ago" / "5 days ago".
        /// <para>
        /// Relative on purpose -- an absolute time would have to be rendered in the <i>rig's</i> local
        /// zone to mean anything (a rig three timezones away going quiet at "23:40" tells you nothing),
        /// and an age sidesteps that entirely. A clock that has gone backwards (an NTP correction, a
        /// restored VM) yields a negative span, reported as "moments ago" rather than a negative age.
        /// </para>
        /// </summary>
        public static string FormatAge(DateTimeOffset then, DateTimeOffset now)
        {
            var age = now - then;
            return age < TimeSpan.FromMinutes(1) ? "moments ago"
                : age < TimeSpan.FromHours(1) ? $"{(int)age.TotalMinutes} min ago"
                : age < TimeSpan.FromDays(1) ? $"{(int)age.TotalHours} h ago"
                : $"{(int)age.TotalDays} day{((int)age.TotalDays == 1 ? "" : "s")} ago";
        }

        /// <summary>
        /// The binding for a rig announced under <paramref name="displayName"/>: an existing one when the
        /// node id already has a record, otherwise a fresh binding minted from the announcement.
        /// <para>
        /// Matched on <b>node id</b> via the peer table, never on the display name -- two rigs can
        /// legitimately announce the same name, and a renamed rig must keep its binding.
        /// </para>
        /// </summary>
        private static RemoteRigBinding? FindBinding(RemoteRigRegistry rigs, IPeerTable? peers, string displayName)
        {
            var candidates = peers?.PeersOf(RemoteRigConnection.NodeServiceName);
            if (candidates is null || candidates.Count == 0)
            {
                // Not announcing right now -- fall back to a binding whose alias matches, so an offline
                // rig can still be selected (and reported as offline) rather than vanishing.
                return rigs.Bindings.FirstOrDefault(b =>
                    string.Equals(b.Alias, displayName, StringComparison.OrdinalIgnoreCase));
            }

            // Same labelling the picker used, so the string the user clicked maps back to the right peer
            // even when two rigs share a name (LanPeer.ResolveLabels disambiguates by machine, then PID).
            var labels = LanPeer.ResolveLabels(candidates);
            var index = Array.FindIndex(labels, l => string.Equals(l, displayName, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return rigs.Bindings.FirstOrDefault(b =>
                    string.Equals(b.Alias, displayName, StringComparison.OrdinalIgnoreCase));
            }

            var peer = candidates[index];
            return rigs.Bindings.FirstOrDefault(b =>
                       string.Equals(b.NodeId, peer.NodeId, StringComparison.OrdinalIgnoreCase))
                ?? new RemoteRigBinding
                {
                    BindingId = Guid.NewGuid(),
                    NodeId = peer.NodeId,
                    RemoteProfileId = null,
                    Alias = labels[index],
                    LastAddress = $"http://{peer.EndPoint.Address}:{peer.EndPoint.Port}/",
                };
        }
    }
}
