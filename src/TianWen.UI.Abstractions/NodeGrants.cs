using Microsoft.Extensions.Logging;
using System;
using TianWen.Lib.Devices;
using TianWen.RemoteClient;

namespace TianWen.UI.Abstractions;

/// <summary>
/// The control this client was granted on each node it reaches over TCP (P6b of docs/plans/hardware-in-the-server.md,
/// decision 13, #1021), kept in the credential store by the node's stable id: a token is a secret, so never in a rig's
/// binding file or a profile, and a grant is remembered until revoked, so a laptop asks a rig once, across restarts of
/// both. With no store (a test's host) nothing is kept past the process.
/// </summary>
public sealed class NodeGrants(ICredentialStore? store, ILogger logger)
{
    /// <summary>A grant for the node <paramref name="nodeId"/>, holding the token kept for it, if any.</summary>
    public NodeGrant For(string nodeId) => new NodeGrant(TokenOf(nodeId));

    /// <summary>The token kept for the node <paramref name="nodeId"/>, or null.</summary>
    public string? TokenOf(string nodeId)
    {
        if (store is null)
        {
            return null;
        }
        try
        {
            return store.Get(KeyOf(nodeId));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the grant kept for node {NodeId}", nodeId);
            return null;
        }
    }

    /// <summary>Keeps <paramref name="token"/> for the node <paramref name="nodeId"/>. A failure keeps it for this process only, and says so.</summary>
    public void Keep(string nodeId, string token) => Write(nodeId, token);

    /// <summary>Forgets the token kept for the node <paramref name="nodeId"/>: the node no longer holds it.</summary>
    public void Forget(string nodeId) => Write(nodeId, null);

    private void Write(string nodeId, string? token)
    {
        if (store is null)
        {
            return;
        }
        try
        {
            store.Set(KeyOf(nodeId), token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, token is null
                ? "Could not forget the grant kept for node {NodeId}"
                : "Could not keep the grant for node {NodeId}: it lasts until this program exits", nodeId);
        }
    }

    // Beside a device's secrets, under a name no device id takes.
    private static string KeyOf(string nodeId) => ICredentialStore.KeyFor($"tianwen-node-{nodeId}", "grant");
}
