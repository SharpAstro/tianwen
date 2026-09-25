using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using TianWen.Hosting.Dto;

namespace TianWen.Hosting.Api;

/// <summary>
/// Native v1 job endpoints: how a slow operation the node runs on its own stands, and how to stop it
/// (<see cref="NodeJobs"/>). A job is started by the endpoint of what it does (<c>POST /api/v1/devices/discover</c>),
/// which answers 202 with the job.
/// </summary>
internal static class JobEndpoints
{
    public static RouteGroupBuilder MapJobApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/jobs");

        // Every job the node knows about, running and recently ended, newest first: what a client that
        // reconnects reads to find the jobs it started.
        group.MapGet("", (NodeJobs jobs) =>
            EnvelopeResults.Json(
                ResponseEnvelope<JobDto[]>.Ok(jobs.List()),
                HostingJsonContext.Default.ResponseEnvelopeJobDtoArray));

        // Authoritative; the JOB-PROGRESS push is only the latency hint.
        group.MapGet("/{id}", (string id, NodeJobs jobs) =>
            EnvelopeResults.Json(
                jobs.TryGet(id, out var job) ? ResponseEnvelope<JobDto>.Ok(job) : ResponseEnvelope<JobDto>.NotFound($"No job {id}"),
                HostingJsonContext.Default.ResponseEnvelopeJobDto));

        // Asks the job to stop and answers how it stands now: still Running until its work notices, then
        // Cancelled. One that has already ended is left as it ended.
        group.MapDelete("/{id}", (string id, NodeJobs jobs) =>
            EnvelopeResults.Json(
                jobs.TryCancel(id, out var job) ? ResponseEnvelope<JobDto>.Ok(job) : ResponseEnvelope<JobDto>.NotFound($"No job {id}"),
                HostingJsonContext.Default.ResponseEnvelopeJobDto));

        return group;
    }
}
