using System.Collections.Generic;
using TianWen.Hosting.Dto;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Who may command a rig, as its Home card shows it (P6b of docs/plans/hardware-in-the-server.md, decision 13, #1021):
/// whether this client may (its own node always; a rig once it granted this client control), where this client's own
/// request for control stands, and, when this client may manage the rig, who else may: the Sharing panel's read.
/// </summary>
public sealed record RigSharing(bool MayCommand, ControlAsk Ask, NodeAccessDto? Access)
{
    /// <summary>A node, as its connection knows it now.</summary>
    public static RigSharing Of(NodeConnection node) => new RigSharing(node.MayCommand, node.Ask, node.Access);

    /// <summary>
    /// The machine's share setting against what the node does now, in ONE wording for the Sharing panel and
    /// <c>tianwen node grants</c>. Only the present is stated: when a change takes effect depends on how the node
    /// was started (a node a client started follows the setting from its next start, one run by hand listens as its
    /// command line says), which the node does not report.
    /// </summary>
    public static string DescribeLan(NodeAccessDto access) => (access.Shared, access.Listening) switch
    {
        (true, true) => "Shared on the LAN",
        (true, false) => "Shared, but not listening on the LAN now",
        (false, true) => "Not shared, but listening on the LAN now",
        (false, false) => "Not shared on the LAN",
    };

    /// <summary>Whether this client is asking for control now, so the card offers to stop in place of asking.</summary>
    public bool IsAsking => Ask.State is ControlAskState.Asking;

    /// <summary>The card's one line: who may command the rig, or, for a rig this client only watches, where its request stands.</summary>
    public string Describe(bool isLocal)
    {
        if (!MayCommand)
        {
            return Ask switch
            {
                { State: ControlAskState.Asking } => "Asking its owner for control",
                { State: ControlAskState.Declined } => "Watching: its owner declined control",
                { State: ControlAskState.Failed, Message: { } why } => $"Watching: {why}",
                _ => "Watching: this computer may not command it",
            };
        }

        var parts = new List<string>(4)
        {
            isLocal ? Access is { Shared: true } ? "Shared on the LAN" : "Not shared on the LAN" : "This computer controls it",
        };
        if (Access is { } access)
        {
            if (access.Grants.Length > 0)
            {
                parts.Add(access.Grants.Length == 1 ? "1 client granted control" : $"{access.Grants.Length} clients granted control");
            }
            if (access.Pending is { } pending)
            {
                parts.Add($"{pending.Label} asks for control");
            }
            if (access.Refused.Length > 0)
            {
                parts.Add(access.Refused.Length == 1 ? "1 application refused" : $"{access.Refused.Length} applications refused");
            }
        }
        return string.Join(", ", parts);
    }
}
