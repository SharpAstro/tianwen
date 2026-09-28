using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TianWen.Hosting.Api;
using TianWen.Hosting.Api.Alpaca;
using TianWen.Hosting.Api.NinaV2;
using TianWen.Hosting.Dto;

namespace TianWen.Hosting;

/// <summary>Which of the node's three surfaces a route belongs to, which decides what counts as a command on it.</summary>
internal enum NodeProtocol
{
    /// <summary>The native API (<c>/api/v1</c>): anything but a read (GET, HEAD, OPTIONS) is a command.</summary>
    Native,

    /// <summary>The Alpaca device plane: a PUT is a command, <c>Connected = true</c> included, since it connects the hardware.</summary>
    Alpaca,

    /// <summary>The ninaAPI shim, whose commands are GETs: every route is a command unless it says it only reads.</summary>
    NinaV2,
}

/// <summary>The surface a route belongs to, put on each protocol's routes where they are mapped (<c>MapHostingApi</c>).</summary>
internal sealed record NodeProtocolMetadata(NodeProtocol Protocol);

/// <summary>A ninaAPI route that only reads, and so needs no control. A route without it is a command: deny by default.</summary>
internal sealed class ReadsOnlyMetadata
{
    public static readonly ReadsOnlyMetadata Instance = new ReadsOnlyMetadata();
}

/// <summary>A native route a client without control may use, because it is how control is asked for.</summary>
internal sealed class OpenToAskMetadata
{
    public static readonly OpenToAskMetadata Instance = new OpenToAskMetadata();
}

internal static class NodeAccessConventions
{
    /// <summary>A ninaAPI route that only reads, and needs no control.</summary>
    public static TBuilder ReadsOnly<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(ReadsOnlyMetadata.Instance);

    /// <summary>A native route by which control is asked for, which a client without it may use.</summary>
    public static TBuilder OpenToAsk<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(OpenToAskMetadata.Instance);
}

/// <summary>
/// The one place a command over TCP is let through or refused (P6b of docs/plans/hardware-in-the-server.md, decision
/// 13, #1021). Seeing is free; a command needs a client of this machine (the node's socket), a grant the node holds, or,
/// for another application, an address or a host name the rig's owner allowed. A middleware, not a check in each route,
/// so a route added later is gated without remembering to be: a native route that is not a read, an Alpaca PUT and a
/// ninaAPI route that does not say it reads are commands by what they are.
/// </summary>
internal static class NodeAccessGate
{
    public static async Task GateAsync(HttpContext context, RequestDelegate next)
    {
        if (context.GetEndpoint() is not { } endpoint
            || endpoint.Metadata.GetMetadata<NodeProtocolMetadata>() is not { } protocol
            || NodeEndpoints.CameOverTheSocket(context)
            || !IsCommand(context.Request.Method, endpoint.Metadata, protocol.Protocol))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var access = context.RequestServices.GetRequiredService<NodeAccess>();
        var cancellationToken = context.RequestAborted;
        if (await access.GrantOfAsync(context, cancellationToken).ConfigureAwait(false) is not null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (protocol.Protocol is NodeProtocol.Native)
        {
            context.Response.Headers.WWWAuthenticate = "Bearer realm=\"tianwen\"";
            await EnvelopeResults.Json(
                ResponseEnvelope<string>.Fail("Commanding this rig needs control: ask its owner for it", StatusCodes.Status401Unauthorized),
                HostingJsonContext.Default.ResponseEnvelopeString).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        var address = NodeAccess.AddressOf(context);
        if (address is not null && await access.AppMayCommandAsync(address, cancellationToken).ConfigureAwait(false))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var appProtocol = protocol.Protocol is NodeProtocol.Alpaca ? AppProtocol.Alpaca : AppProtocol.NinaV2;
        if (address is not null)
        {
            access.RecordRefusal(address, appProtocol, UserAgentOf(context), await AlpacaClientIdOfAsync(context).ConfigureAwait(false),
                $"{context.Request.Method} {context.Request.Path}");
        }
        var message = $"This rig has not allowed {address?.ToString() ?? "this client"} to command it: its owner can allow it in the rig's Sharing panel";
        var refusal = appProtocol is AppProtocol.Alpaca
            ? AlpacaEndpoints.Fault(AlpacaError.InvalidOperation, message, context)
            : NinaEquipmentEndpoints.NinaFail(message, StatusCodes.Status403Forbidden);
        await refusal.ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Whether a request by <paramref name="method"/> to a route of <paramref name="protocol"/> is a command.</summary>
    internal static bool IsCommand(string method, EndpointMetadataCollection metadata, NodeProtocol protocol) => protocol switch
    {
        NodeProtocol.Native => !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method))
            && metadata.GetMetadata<OpenToAskMetadata>() is null,
        NodeProtocol.Alpaca => HttpMethods.IsPut(method),
        NodeProtocol.NinaV2 => metadata.GetMetadata<ReadsOnlyMetadata>() is null,
        _ => true,
    };

    private static string? UserAgentOf(HttpContext context) =>
        context.Request.Headers.UserAgent.ToString() is { Length: > 0 } agent ? agent : null;

    /// <summary>The Alpaca <c>ClientID</c> a request carries, in its query or its form, for the refusal's record.</summary>
    private static async Task<string?> AlpacaClientIdOfAsync(HttpContext context)
    {
        if (context.Request.Query.TryGetValue("ClientID", out var inQuery) && inQuery.ToString() is { Length: > 0 } fromQuery)
        {
            return fromQuery;
        }
        if (!context.Request.HasFormContentType)
        {
            return null;
        }
        var form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
        return form.TryGetValue("ClientID", out var inForm) && inForm.ToString() is { Length: > 0 } fromForm ? fromForm : null;
    }
}
