using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TianWen.Hosting.Dto;

namespace TianWen.Hosting.Api;

/// <summary>
/// Who may command the node (P6b of docs/plans/hardware-in-the-server.md, decision 13, #1021): a client over TCP asks
/// and polls; the rig's machine, or a client granted control, answers, revokes, allows another application and reads it
/// all for the Sharing panel. Every route here that is not a read goes through <see cref="NodeAccessGate"/> like any
/// command, except asking and polling, which a client without control must be able to do.
/// </summary>
internal static class AccessEndpoints
{
    public static void MapAccessApi(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/v1/node/control/requests", (ControlRequestDto request, HttpContext context, NodeAccess access) =>
        {
            if (NodeEndpoints.CameOverTheSocket(context))
            {
                return EnvelopeResults.Json(
                    ResponseEnvelope<ControlRequestTicketDto>.Fail("A client on this machine may command the node without a grant", 400),
                    HostingJsonContext.Default.ResponseEnvelopeControlRequestTicketDto);
            }
            return access.Request(request.Label, NodeAccess.AddressOf(context)) is { } ticket
                ? EnvelopeResults.Json(ResponseEnvelope<ControlRequestTicketDto>.Accepted(ticket), HostingJsonContext.Default.ResponseEnvelopeControlRequestTicketDto)
                : EnvelopeResults.Json(
                    ResponseEnvelope<ControlRequestTicketDto>.Fail("The rig's owner is answering another request for control: ask again in a moment", 409),
                    HostingJsonContext.Default.ResponseEnvelopeControlRequestTicketDto);
        }).OpenToAsk();

        routes.MapPost("/api/v1/node/control/requests/{id}/poll", async (string id, ControlRequestPollDto poll, NodeAccess access, CancellationToken ct) =>
            EnvelopeResults.Json(
                ResponseEnvelope<ControlRequestOutcomeDto>.Ok(await access.PollAsync(id, poll.Secret, ct)),
                HostingJsonContext.Default.ResponseEnvelopeControlRequestOutcomeDto)).OpenToAsk();

        routes.MapPost("/api/v1/node/control/requests/{id}/answer", (string id, ControlAnswerDto answer, NodeAccess access) =>
            access.Answer(id, answer.Allow)
                ? EnvelopeResults.Json(ResponseEnvelope<string>.Ok(answer.Allow ? "Allowed" : "Declined"), HostingJsonContext.Default.ResponseEnvelopeString)
                : EnvelopeResults.Json(ResponseEnvelope<string>.NotFound("No request for control waits under that id: it was answered, or its asker has gone"),
                    HostingJsonContext.Default.ResponseEnvelopeString));

        // A read, so the gate lets it through: who may command the rig is for those who may manage it, so it asks itself.
        routes.MapGet("/api/v1/node/access", async (HttpContext context, NodeAccess access, NodeListening listening, CancellationToken ct) =>
            await access.MayCommandAsync(context, ct)
                ? EnvelopeResults.Json(ResponseEnvelope<NodeAccessDto>.Ok(await access.DescribeAsync(listening, ct)), HostingJsonContext.Default.ResponseEnvelopeNodeAccessDto)
                : EnvelopeResults.Json(ResponseEnvelope<NodeAccessDto>.Fail("Who may command this rig is for its owner, or a client granted control", 401),
                    HostingJsonContext.Default.ResponseEnvelopeNodeAccessDto));

        routes.MapDelete("/api/v1/node/access/grants/{id}", async (string id, NodeAccess access, CancellationToken ct) =>
            await access.RevokeAsync(id, ct)
                ? EnvelopeResults.Json(ResponseEnvelope<string>.Ok("Revoked"), HostingJsonContext.Default.ResponseEnvelopeString)
                : EnvelopeResults.Json(ResponseEnvelope<string>.NotFound("No such grant"), HostingJsonContext.Default.ResponseEnvelopeString));

        routes.MapPost("/api/v1/node/access/apps", async (AppAllowDto allow, NodeAccess access, CancellationToken ct) =>
        {
            if (!IPAddress.TryParse(allow.Address, out var address))
            {
                return EnvelopeResults.Json(ResponseEnvelope<string>.Fail($"'{allow.Address}' is not an address", 400), HostingJsonContext.Default.ResponseEnvelopeString);
            }
            return await access.AllowAppAsync(address, allow.Always, ct) is NodeAccess.AppAllowOutcome.Allowed
                ? EnvelopeResults.Json(ResponseEnvelope<string>.Ok(allow.Always ? "Always allowed" : "Allowed until the node restarts"), HostingJsonContext.Default.ResponseEnvelopeString)
                : EnvelopeResults.Json(
                    ResponseEnvelope<string>.Fail($"{address} has no host name that resolves back to it, so it can be allowed only until the node restarts", 409),
                    HostingJsonContext.Default.ResponseEnvelopeString);
        });

        routes.MapDelete("/api/v1/node/access/apps/{address}", (string address, NodeAccess access) =>
            IPAddress.TryParse(address, out var parsed) && access.RevokeApp(parsed)
                ? EnvelopeResults.Json(ResponseEnvelope<string>.Ok("Revoked"), HostingJsonContext.Default.ResponseEnvelopeString)
                : EnvelopeResults.Json(ResponseEnvelope<string>.NotFound("That address is not allowed"), HostingJsonContext.Default.ResponseEnvelopeString));

        routes.MapDelete("/api/v1/node/access/hosts/{host}", async (string host, NodeAccess access, CancellationToken ct) =>
            await access.RevokeHostAsync(host, ct)
                ? EnvelopeResults.Json(ResponseEnvelope<string>.Ok("Revoked"), HostingJsonContext.Default.ResponseEnvelopeString)
                : EnvelopeResults.Json(ResponseEnvelope<string>.NotFound("That host is not always allowed"), HostingJsonContext.Default.ResponseEnvelopeString));

        routes.MapDelete("/api/v1/node/access/refused/{address}", (string address, NodeAccess access) =>
            IPAddress.TryParse(address, out var parsed) && access.IgnoreRefusal(parsed)
                ? EnvelopeResults.Json(ResponseEnvelope<string>.Ok("Ignored"), HostingJsonContext.Default.ResponseEnvelopeString)
                : EnvelopeResults.Json(ResponseEnvelope<string>.NotFound("Nothing from that address was refused"), HostingJsonContext.Default.ResponseEnvelopeString));
    }
}
