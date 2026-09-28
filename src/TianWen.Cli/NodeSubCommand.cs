using System.CommandLine;
using System.Globalization;
using System.Net;
using TianWen.Hosting.Dto;
using TianWen.RemoteClient;
using TianWen.UI.Abstractions;

namespace TianWen.Cli;

/// <summary>
/// Who may command this computer's rig over the LAN (P6b of docs/plans/hardware-in-the-server.md, decision 13, #1021),
/// from a terminal: how a headless rig's owner, over SSH, answers a laptop that asks for control, and manages the grants
/// and the other applications the rig's Sharing panel shows. Every verb goes to this computer's node over its socket,
/// which needs no grant.
/// </summary>
internal class NodeSubCommand(IConsoleHost consoleHost)
{
    public Command Build()
    {
        var requests = new Command("requests", "Show the request for control waiting, and the other applications this rig refused");
        requests.SetAction((_, ct) => RequestsAsync(ct));

        var grants = new Command("grants", "Show who may command this rig: the clients granted control and the applications allowed");
        grants.SetAction((_, ct) => GrantsAsync(ct));

        var allowAddress = new Argument<string?>("address")
        {
            Description = "An application's address to allow. Leave it out to allow the request for control that is waiting.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var always = new Option<bool>("--always")
        {
            Description = "Allow the application's host name from now on, rather than its address until the node restarts",
        };
        var allow = new Command("allow", "Allow the request for control waiting, or another application at an address")
        {
            Arguments = { allowAddress },
            Options = { always },
        };
        allow.SetAction((parseResult, ct) => AllowAsync(parseResult.GetValue(allowAddress), parseResult.GetValue(always), ct));

        var decline = new Command("decline", "Decline the request for control waiting");
        decline.SetAction((_, ct) => DeclineAsync(ct));

        var revokeWho = new Argument<string>("who")
        {
            Description = "A grant (its id or its label), an application's address, or a host name always allowed",
        };
        var revoke = new Command("revoke", "Take away a grant, an application's address, or a host name always allowed")
        {
            Arguments = { revokeWho },
        };
        revoke.SetAction((parseResult, ct) => RevokeAsync(parseResult.GetRequiredValue(revokeWho), ct));

        var ignoreAddress = new Argument<string>("address") { Description = "The address of an application this rig refused" };
        var ignore = new Command("ignore", "Forget an application's refused command; its next refusal is recorded again")
        {
            Arguments = { ignoreAddress },
        };
        ignore.SetAction((parseResult, ct) => IgnoreAsync(parseResult.GetRequiredValue(ignoreAddress), ct));

        var shareState = new Argument<string>("state") { Description = "on or off" };
        shareState.AcceptOnlyFromAmong("on", "off");
        var share = new Command("share", "Share this rig on the LAN, or stop sharing it") { Arguments = { shareState } };
        share.SetAction((parseResult, ct) => ShareAsync(parseResult.GetRequiredValue(shareState) is "on", ct));

        return new Command("node", "Who may command this computer's rig over the LAN")
        {
            Subcommands = { requests, grants, allow, decline, revoke, ignore, share },
        };
    }

    private async Task<int> RequestsAsync(CancellationToken cancellationToken)
    {
        if (await AccessAsync(cancellationToken) is not { } access)
        {
            return 1;
        }

        consoleHost.WriteScrollable(access.Pending is { } pending
            ? $"{Describe(pending)} asks to control this rig: 'tianwen node allow' or 'tianwen node decline'"
            : "No request for control is waiting.");
        if (access.Refused.Length == 0)
        {
            consoleHost.WriteScrollable("No other application has been refused.");
            return 0;
        }
        consoleHost.WriteScrollable("Refused, until allowed ('tianwen node allow <address> [--always]', or 'ignore'):");
        foreach (var refused in access.Refused)
        {
            consoleHost.WriteScrollable($"  {Describe(refused)}");
        }
        return 0;
    }

    private async Task<int> GrantsAsync(CancellationToken cancellationToken)
    {
        if (await AccessAsync(cancellationToken) is not { } access)
        {
            return 1;
        }

        consoleHost.WriteScrollable($"{RigSharing.DescribeLan(access)}.");
        if (access.Grants.Length == 0 && access.AppsAllowed.Length == 0 && access.HostsAlwaysAllowed.Length == 0)
        {
            consoleHost.WriteScrollable("Only this computer may command the rig.");
            return 0;
        }
        foreach (var grant in access.Grants)
        {
            consoleHost.WriteScrollable($"  {grant.Label}: granted control {When(grant.GrantedAt)} (grant {grant.Id})");
        }
        foreach (var app in access.AppsAllowed)
        {
            consoleHost.WriteScrollable($"  Applications at {AddressAndHost(app.Address, app.Host)}: allowed until the node restarts");
        }
        foreach (var host in access.HostsAlwaysAllowed)
        {
            consoleHost.WriteScrollable($"  Applications on {host}: always allowed");
        }
        return 0;
    }

    private async Task<int> AllowAsync(string? address, bool always, CancellationToken cancellationToken)
    {
        if (await consoleHost.NodeAsync(cancellationToken) is not { } node)
        {
            return 1;
        }

        if (address is null)
        {
            if (always)
            {
                consoleHost.WriteError("--always is for another application's address: a grant is kept until it is revoked anyway");
                return 1;
            }
            return await AnswerAsync(node, allow: true, cancellationToken);
        }

        if (!IPAddress.TryParse(address, out _))
        {
            consoleHost.WriteError($"'{address}' is not an address; 'tianwen node requests' lists the applications refused");
            return 1;
        }
        var allowed = await node.AllowAppAsync(address, always, cancellationToken);
        if (!allowed.IsSuccess)
        {
            consoleHost.WriteError(allowed.Error ?? $"{address} was not allowed");
            return 1;
        }
        consoleHost.WriteScrollable(always
            ? $"Applications at {address} are allowed to command this rig, and so is their host name from now on."
            : $"Applications at {address} are allowed to command this rig until its node restarts.");
        return 0;
    }

    private async Task<int> DeclineAsync(CancellationToken cancellationToken) =>
        await consoleHost.NodeAsync(cancellationToken) is { } node ? await AnswerAsync(node, allow: false, cancellationToken) : 1;

    /// <summary>Answers the request waiting, which is the only one: a person answers one at a time.</summary>
    private async Task<int> AnswerAsync(TianWenNodeClient node, bool allow, CancellationToken cancellationToken)
    {
        var access = await node.GetAccessAsync(cancellationToken);
        if (access is not { IsSuccess: true, Value: { } read })
        {
            consoleHost.WriteError(access.Error ?? "The node did not say who asks to control it");
            return 1;
        }
        if (read.Pending is not { } pending)
        {
            consoleHost.WriteError("No request for control is waiting.");
            return 1;
        }
        var answered = await node.AnswerControlRequestAsync(pending.Id, allow, cancellationToken);
        if (!answered.IsSuccess)
        {
            consoleHost.WriteError(answered.Error ?? "The request was not answered");
            return 1;
        }
        consoleHost.WriteScrollable(allow
            ? $"{Describe(pending)} may control this rig once its window collects the grant, which it does at its next poll."
            : $"{Describe(pending)} was declined.");
        return 0;
    }

    private async Task<int> RevokeAsync(string who, CancellationToken cancellationToken)
    {
        if (await consoleHost.NodeAsync(cancellationToken) is not { } node)
        {
            return 1;
        }

        if (IPAddress.TryParse(who, out _))
        {
            return Said(await node.RevokeAppAsync(who, cancellationToken), $"Applications at {who} may no longer command this rig.");
        }

        var access = await node.GetAccessAsync(cancellationToken);
        if (access is not { IsSuccess: true, Value: { } read })
        {
            consoleHost.WriteError(access.Error ?? "The node did not say who may command it");
            return 1;
        }

        // A grant by its id first, then by its label, which is what a person reads off 'tianwen node grants'.
        var grants = read.Grants.Where(grant => string.Equals(grant.Id, who, StringComparison.Ordinal)).ToArray();
        if (grants.Length == 0)
        {
            grants = [.. read.Grants.Where(grant => string.Equals(grant.Label, who, StringComparison.OrdinalIgnoreCase))];
        }
        if (grants.Length > 1)
        {
            consoleHost.WriteError($"{grants.Length} grants are labelled '{who}': revoke one by its id ({string.Join(", ", grants.Select(static grant => grant.Id))})");
            return 1;
        }
        if (grants is [var only])
        {
            return Said(await node.RevokeGrantAsync(only.Id, cancellationToken), $"{only.Label} may no longer command this rig.");
        }

        if (read.HostsAlwaysAllowed.FirstOrDefault(host => string.Equals(host, who, StringComparison.OrdinalIgnoreCase)) is { } named)
        {
            return Said(await node.RevokeHostAsync(named, cancellationToken), $"Applications on {named} are no longer always allowed.");
        }

        consoleHost.WriteError($"Nothing called '{who}' may command this rig; 'tianwen node grants' lists who may");
        return 1;
    }

    private async Task<int> IgnoreAsync(string address, CancellationToken cancellationToken) =>
        await consoleHost.NodeAsync(cancellationToken) is { } node
            ? Said(await node.IgnoreRefusedAppAsync(address, cancellationToken), $"The refusal of {address} is forgotten.")
            : 1;

    private async Task<int> ShareAsync(bool shared, CancellationToken cancellationToken)
    {
        if (await consoleHost.NodeAsync(cancellationToken) is not { } node)
        {
            return 1;
        }
        var set = await node.SetShareAsync(shared, cancellationToken);
        if (set is not { IsSuccess: true, Value: { } share })
        {
            consoleHost.WriteError(set.Error ?? "Sharing was not changed");
            return 1;
        }
        // The node says what it did in its own words: shared now, at its next start, or restarting to apply it.
        consoleHost.WriteScrollable(share.Message);
        return 0;
    }

    private async Task<NodeAccessDto?> AccessAsync(CancellationToken cancellationToken)
    {
        if (await consoleHost.NodeAsync(cancellationToken) is not { } node)
        {
            return null;
        }
        var access = await node.GetAccessAsync(cancellationToken);
        if (access is { IsSuccess: true, Value: { } read })
        {
            return read;
        }
        consoleHost.WriteError(access.Error ?? "The node did not say who may command it");
        return null;
    }

    private int Said(NodeResult<string> result, string done)
    {
        if (!result.IsSuccess)
        {
            consoleHost.WriteError(result.Error ?? "The node did not do it");
            return 1;
        }
        consoleHost.WriteScrollable(done);
        return 0;
    }

    private static string Describe(PendingControlRequestDto pending) =>
        pending.Address is { } address ? $"'{pending.Label}' ({address})" : $"'{pending.Label}'";

    private static string Describe(RefusedAppDto refused)
    {
        var who = refused.UserAgent ?? (refused.Protocol is AppProtocol.Alpaca ? "An Alpaca client" : "A ninaAPI client");
        var id = refused.ClientId is { } clientId ? $", client {clientId}" : "";
        var times = refused.Attempts == 1 ? "once" : $"{refused.Attempts} times";
        return $"{who} at {AddressAndHost(refused.Address, refused.Host)}{id}: {refused.What}, {times}, last {When(refused.LastAt)}";
    }

    private static string AddressAndHost(string address, string? host) => host is { } name ? $"{address} ({name})" : address;

    private static string When(DateTimeOffset at) => at.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
