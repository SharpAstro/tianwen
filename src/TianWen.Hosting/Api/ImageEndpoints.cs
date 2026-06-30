using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using TianWen.Hosting.Dto;
using TianWen.Lib.Imaging.Enhancement;

namespace TianWen.Hosting.Api;

/// <summary>
/// Native v1 image endpoints: AI enhance of a server-side FITS file.
/// </summary>
internal static class ImageEndpoints
{
    public static RouteGroupBuilder MapImageApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/image");

        // POST /api/v1/image/enhance -- kick a single-flight enhance of a FITS already on the
        // server's disk. Returns immediately; follow progress via the ENHANCE-PROGRESS /
        // ENHANCE-COMPLETED WebSocket events or GET /api/v1/image/enhance/status.
        group.MapPost("/enhance", (EnhanceRequestDto request, HostedImageEnhancer enhancer, IHostApplicationLifetime lifetime) =>
        {
            if (string.IsNullOrWhiteSpace(request.InputPath))
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail("InputPath is required"),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            if (!File.Exists(request.InputPath))
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail($"Input not found: {request.InputPath}", 404),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            // Same parser the CLI uses (single source of truth for auto/rc/sas + tuning).
            if (!EnhanceOptions.TryParse(
                    request.Backend, request.DeblurSharpen, request.DenoiseStrength, request.DenoiseIterations,
                    out var options, out var error))
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail(error!),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            // The work is tied to the host lifetime (ApplicationStopping), NOT the request, so it
            // outlives this POST returning and is cancelled only on server shutdown.
            if (!enhancer.TryStart(request, options, lifetime.ApplicationStopping))
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail("An enhance is already running", 409),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            return Results.Json(
                ResponseEnvelope<string>.Ok("Enhance started"),
                HostingJsonContext.Default.ResponseEnvelopeString);
        });

        // GET /api/v1/image/enhance/status -- poll the current/last enhance job (concrete
        // EnhanceStatusDto, not ResponseEnvelope<object>, so the source-gen context resolves it under AOT).
        group.MapGet("/enhance/status", (HostedImageEnhancer enhancer) =>
            Results.Json(
                ResponseEnvelope<EnhanceStatusDto>.Ok(enhancer.Status),
                HostingJsonContext.Default.ResponseEnvelopeEnhanceStatusDto));

        return group;
    }
}
