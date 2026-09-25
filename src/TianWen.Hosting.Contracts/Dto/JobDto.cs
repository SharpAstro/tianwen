using System;

namespace TianWen.Hosting.Dto;

/// <summary>Where a <see cref="JobDto"/> stands. Numeric on the wire, as every enum here is.</summary>
public enum JobState
{
    Running,
    Succeeded,
    Failed,
    Cancelled
}

/// <summary>
/// A slow operation the node runs on its OWN token, so that every request keeps its short budget: the request
/// that starts one answers 202 with this, <c>GET /api/v1/jobs/{id}</c> is authoritative, a <c>JOB-PROGRESS</c>
/// push is the latency hint, and <c>DELETE /api/v1/jobs/{id}</c> cancels. One model for every slow operation
/// (discovery first; connect, warm, a preview exposure and a move to follow), not a variant per endpoint:
/// docs/plans/hardware-in-the-server.md, "Slow operations are JOBS".
/// </summary>
/// <remarks>
/// A job carries no result of its own. What it produced is read where it lives (a discovery's devices from
/// <c>GET /api/v1/devices/structured</c>), so a job type never needs a payload type of its own on the wire.
/// </remarks>
public sealed class JobDto
{
    public required string Id { get; init; }

    /// <summary>What the job does: <c>discover</c>.</summary>
    public required string Kind { get; init; }

    public JobState State { get; init; }

    /// <summary>What it is doing now, or what it came to once it has ended.</summary>
    public string? Step { get; init; }

    /// <summary>Why it failed; null unless <see cref="State"/> is <see cref="JobState.Failed"/>.</summary>
    public string? Error { get; init; }

    public DateTimeOffset StartedUtc { get; init; }

    /// <summary>Null while it runs.</summary>
    public DateTimeOffset? EndedUtc { get; init; }
}
