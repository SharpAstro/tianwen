using System;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using TianWen.Hosting.Dto;
using TianWen.Lib.Sequencing;
using TianWen.RemoteClient;

namespace TianWen.UI.Abstractions
{
    // AppSignalHandler.Access.cs -- who may command a node (P6b of docs/plans/hardware-in-the-server.md, decision 13, #1021):
    // asking a rig for control, and managing who may command this computer's rig, or a rig this client was granted.
    public partial class AppSignalHandler
    {
        /// <summary>
        /// What a request for control from this computer is labelled on the rig's machine: the computer, which is who a
        /// person answering it knows.
        /// </summary>
        internal static string ControlRequestLabel => $"{Environment.MachineName}, TianWen";

        /// <summary>Wires asking for control and managing who may command a node.</summary>
        private void SubscribeAccess(SignalBus bus)
        {
            bus.Subscribe<AskForControlSignal>(sig =>
            {
                if (NodeOrSay(sig.BindingId) is not { } node) return;
                if (node.MayCommand)
                {
                    Notify(NotificationSeverity.Info, $"This computer already controls {NameOf(node)}");
                    return;
                }
                Notify(NotificationSeverity.Info,
                    $"Asking {NameOf(node)} for control: its owner answers on the rig, or with 'tianwen node allow' there");
                RunTracked("AskForControl", "Asking for control failed", async ct =>
                {
                    await node.AskForControlAsync(ControlRequestLabel, ct).ConfigureAwait(false);
                    var (severity, said) = node.Ask switch
                    {
                        { State: ControlAskState.None } when node.MayCommand => (NotificationSeverity.Info, $"This computer controls {NameOf(node)} now"),
                        { State: ControlAskState.Declined } => (NotificationSeverity.Warning, $"{NameOf(node)} declined control"),
                        { State: ControlAskState.Failed, Message: var why } => (NotificationSeverity.Warning, $"{NameOf(node)} did not grant control: {why}"),
                        _ => (NotificationSeverity.Info, $"Stopped asking {NameOf(node)} for control"),
                    };
                    Notify(severity, said);
                }, onFinally: () => _appState.NeedsRedraw = true);
            });

            bus.Subscribe<CancelControlAskSignal>(sig => ConnectionOf(sig.BindingId)?.CancelAsk());

            bus.Subscribe<AnswerControlRequestSignal>(sig =>
            {
                if (ManagedNodeOrSay(sig.BindingId) is not { } node) return;
                if (sig.BindingId is null)
                {
                    // Answered here, so the question drawn over this computer's view is done with.
                    LocalLiveSession.ControlRequest = null;
                }
                RunTracked("AnswerControlRequest", "Answering the request for control failed", async ct =>
                {
                    var answered = await node.Client.AnswerControlRequestAsync(sig.RequestId, sig.Allow, ct).ConfigureAwait(false);
                    Notify(answered.IsSuccess ? NotificationSeverity.Info : NotificationSeverity.Warning, answered.IsSuccess
                        ? sig.Allow ? "Allowed: it controls the rig once its window collects the grant" : "Declined"
                        : answered.Error ?? "The request was not answered");
                    await node.RefreshAccessNowAsync(ct).ConfigureAwait(false);
                }, onFinally: () => _appState.NeedsRedraw = true);
            });

            // Answered later, from the Sharing panel: the question goes, and does not come back for this request.
            bus.Subscribe<DismissControlRequestSignal>(_ => LocalLiveSession.ControlRequest = null);

            bus.Subscribe<RevokeGrantSignal>(sig => Manage(sig.BindingId, "RevokeGrant", "Revoked: it sees, and asks to command",
                (client, ct) => client.RevokeGrantAsync(sig.GrantId, ct)));

            bus.Subscribe<AllowAppSignal>(sig => Manage(sig.BindingId, "AllowApp", sig.Always
                    ? $"Applications at {sig.Address} may command the rig, and their host name from now on"
                    : $"Applications at {sig.Address} may command the rig until its node restarts",
                (client, ct) => client.AllowAppAsync(sig.Address, sig.Always, ct)));

            bus.Subscribe<RevokeAppSignal>(sig => Manage(sig.BindingId, "RevokeApp", $"Applications at {sig.Address} may no longer command the rig",
                (client, ct) => client.RevokeAppAsync(sig.Address, ct)));

            bus.Subscribe<RevokeHostSignal>(sig => Manage(sig.BindingId, "RevokeHost", $"Applications on {sig.Host} are no longer always allowed",
                (client, ct) => client.RevokeHostAsync(sig.Host, ct)));

            bus.Subscribe<IgnoreRefusedAppSignal>(sig => Manage(sig.BindingId, "IgnoreRefusedApp", $"The refusal of {sig.Address} is forgotten",
                (client, ct) => client.IgnoreRefusedAppAsync(sig.Address, ct)));

            bus.Subscribe<SetLanShareSignal>(sig =>
            {
                if (ManagedNodeOrSay(sig.BindingId) is not { } node) return;
                RunTracked("SetLanShare", "Changing the LAN share failed", async ct =>
                {
                    var set = await node.Client.SetShareAsync(sig.Shared, ct).ConfigureAwait(false);
                    Notify(set.IsSuccess ? NotificationSeverity.Info : NotificationSeverity.Warning,
                        set.Value?.Message ?? set.Error ?? "Sharing was not changed");
                    await node.RefreshAccessNowAsync(ct).ConfigureAwait(false);
                }, onFinally: () => _appState.NeedsRedraw = true);
            });
        }

        /// <summary>One act of managing who may command a node, answered by the node, then read again.</summary>
        private void Manage(Guid? bindingId, string name, string done, Func<TianWenNodeClient, CancellationToken, Task<NodeResult<string>>> act)
        {
            if (ManagedNodeOrSay(bindingId) is not { } node) return;
            RunTracked(name, "Changing who may command the rig failed", async ct =>
            {
                var result = await act(node.Client, ct).ConfigureAwait(false);
                Notify(result.IsSuccess ? NotificationSeverity.Info : NotificationSeverity.Warning,
                    result.IsSuccess ? done : result.Error ?? "The rig did not do it");
                await node.RefreshAccessNowAsync(ct).ConfigureAwait(false);
            }, onFinally: () => _appState.NeedsRedraw = true);
        }

        /// <summary>This computer's node (<paramref name="bindingId"/> null) or a connected rig's, without a word when there is none.</summary>
        private NodeConnection? ConnectionOf(Guid? bindingId) =>
            bindingId is { } id ? _rigs.Find(id) : _appState.LocalNode;

        /// <summary>This computer's node or a connected rig's, or null with a note saying why there is none.</summary>
        private NodeConnection? NodeOrSay(Guid? bindingId)
        {
            if (bindingId is null)
            {
                return LocalNodeOrSay();
            }
            if (ConnectionOf(bindingId) is { } rig)
            {
                return rig;
            }
            Notify(NotificationSeverity.Warning, "That rig is not connected");
            return null;
        }

        /// <summary>
        /// A node this client may manage (its own, or one it was granted control of), or null with a note: who may command
        /// a rig is for the rig's machine and the clients it granted control.
        /// </summary>
        private NodeConnection? ManagedNodeOrSay(Guid? bindingId)
        {
            if (NodeOrSay(bindingId) is not { } node)
            {
                return null;
            }
            if (node.MayCommand)
            {
                return node;
            }
            Notify(NotificationSeverity.Warning, $"Who may command {NameOf(node)} is for its owner: ask it for control first");
            return null;
        }

        private static string NameOf(NodeConnection node) => node is RemoteRigConnection rig ? $"'{rig.Binding.Alias}'" : LocalNodeName;

        /// <summary>
        /// Keeps who may command each node current (P6b): this computer's, whose Sharing panel and request question read it,
        /// and each connected rig's, whose view commands or asks by it. Gated on the connection's own due-check, as the
        /// profile refresh is: a read every <see cref="NodeConnection.AccessRefreshInterval"/> at most, at once after an
        /// <c>ACCESS-CHANGED</c> push.
        /// </summary>
        private void RefreshNodeAccess()
        {
            if (_appState.LocalNode is { } local)
            {
                if (local.AccessRefreshDue)
                {
                    RefreshAccessOf(local, LocalNodeName);
                }
                AskAboutControlRequest(local.Access?.Pending);
            }
            foreach (var (_, connection) in _rigs.Connections)
            {
                if (connection.AccessRefreshDue)
                {
                    RefreshAccessOf(connection, connection.Binding.Alias);
                }
            }
        }

        private void RefreshAccessOf(NodeConnection connection, string name) =>
            RunTracked("RefreshNodeAccess", $"Could not read who may command {name}", async ct =>
            {
                if (await connection.MaybeRefreshAccessAsync(ct).ConfigureAwait(false))
                {
                    _appState.NeedsRedraw = true;
                }
            });

        // The request for control last put to whoever is at this computer. Per-frame poll only (RefreshNodeAccess, on the
        // UI thread), which is what keeps a question dismissed from coming back while the same request waits.
        private string? _askedAboutRequestId;

        /// <summary>
        /// Puts a request for control of this computer's rig to whoever is at it, over everything, as the quit question is
        /// (P6b): a NEW request brings this computer's Live Session view to the front with the question on it; one answered,
        /// lapsed or declined elsewhere takes the question away. Dismissed, it stays in the Sharing panel until it lapses.
        /// </summary>
        private void AskAboutControlRequest(PendingControlRequestDto? pending)
        {
            if (pending is null)
            {
                _askedAboutRequestId = null;
                if (LocalLiveSession.ControlRequest is not null)
                {
                    LocalLiveSession.ControlRequest = null;
                }
                return;
            }
            if (string.Equals(_askedAboutRequestId, pending.Id, StringComparison.Ordinal))
            {
                return;
            }
            _askedAboutRequestId = pending.Id;
            LocalLiveSession.ControlRequest = pending;
            _contexts.Activate(_contexts.Local);
            _appState.ActiveTab = GuiTab.LiveSession;
            LocalLiveSession.NeedsRedraw = true;
        }
    }
}
