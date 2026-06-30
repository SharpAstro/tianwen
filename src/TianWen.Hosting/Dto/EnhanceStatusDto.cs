namespace TianWen.Hosting.Dto;

/// <summary>
/// Snapshot of the server's single-flight image-enhance job, returned by
/// <c>GET /api/v1/image/enhance/status</c> for clients that poll instead of subscribing to the
/// <c>ENHANCE-PROGRESS</c> / <c>ENHANCE-COMPLETED</c> WebSocket events. Immutable: the
/// enhancer swaps a whole new instance atomically on each progress tick (lock-free read).
/// </summary>
public sealed class EnhanceStatusDto
{
    /// <summary>True while an enhance is running. Only one runs at a time (the POST 409s otherwise).</summary>
    public required bool IsEnhancing { get; init; }

    /// <summary>Input FITS path of the current or last run, or null if no run has happened yet.</summary>
    public string? InputPath { get; init; }

    /// <summary>Output FITS path: set while running and on success; null on failure / before the first run.</summary>
    public string? OutputPath { get; init; }

    /// <summary>Resolved backend for the current/last run (<c>Auto</c>/<c>ForceRcAstro</c>/<c>ForceSas</c>), or null.</summary>
    public string? Backend { get; init; }

    /// <summary>Current pipeline step name (e.g. "denoise-starless"), or null between runs.</summary>
    public string? CurrentStep { get; init; }

    /// <summary>Zero-based index of the current step.</summary>
    public int StepIndex { get; init; }

    /// <summary>Total number of steps in the current request.</summary>
    public int StepCount { get; init; }

    /// <summary>Overall completion in [0, 100], combining the step index and the within-step percent.</summary>
    public float Percent { get; init; }

    /// <summary>Failure / cancellation reason for the last run, or null on success / while running.</summary>
    public string? Error { get; init; }

    /// <summary>Outcome of the last completed run: true = success, false = failed/cancelled, null = no run yet or in progress.</summary>
    public bool? Succeeded { get; init; }
}
