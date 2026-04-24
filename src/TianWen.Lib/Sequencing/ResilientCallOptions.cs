using System;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// Per-call configuration for <see cref="ResilientCall.InvokeAsync"/>.
/// Canned presets cover the common cases; callers construct a custom value only
/// when they need an unusual attempt count / backoff shape.
/// </summary>
/// <param name="MaxAttempts">Total attempts (including the first). <c>1</c> disables retry.</param>
/// <param name="InitialBackoff">Delay before the second attempt. Scaled by <paramref name="BackoffMultiplier"/> for subsequent attempts.</param>
/// <param name="BackoffMultiplier">Per-attempt backoff growth factor. <c>1.0</c> = constant, <c>3.0</c> = 250ms / 750ms / 2250ms for the default read preset.</param>
/// <param name="IsIdempotent">If <see langword="true"/>, the op may be retried. Non-idempotent ops (slew, exposure start, dither) must set this to <see langword="false"/> — retry would double-issue.</param>
/// <param name="OperationName">Short label emitted in log scopes. Typically the verb: "read", "move", "slew", "expose".</param>
internal readonly record struct ResilientCallOptions(
    int MaxAttempts,
    TimeSpan InitialBackoff,
    double BackoffMultiplier,
    bool IsIdempotent,
    string OperationName)
{
    /// <summary>
    /// Default for idempotent reads (status polls, position gets, HA reads).
    /// 3 attempts, 250ms -> 750ms -> 2250ms backoff between them.
    /// </summary>
    public static readonly ResilientCallOptions IdempotentRead = new(
        MaxAttempts: 3,
        InitialBackoff: TimeSpan.FromMilliseconds(250),
        BackoffMultiplier: 3.0,
        IsIdempotent: true,
        OperationName: "read");

    /// <summary>
    /// Default for non-idempotent effectful calls (slew, exposure start, dither).
    /// Single attempt, no retry; pre-reconnect still runs when the driver reports
    /// disconnected before the first attempt.
    /// </summary>
    public static readonly ResilientCallOptions NonIdempotentAction = new(
        MaxAttempts: 1,
        InitialBackoff: TimeSpan.Zero,
        BackoffMultiplier: 1.0,
        IsIdempotent: false,
        OperationName: "action");

    /// <summary>
    /// Focuser and filter-wheel moves target absolute coordinates, so re-issuing
    /// after a mid-move disconnect is safe (the driver will just land on the same
    /// position). 2 attempts with a 500ms gap between them.
    /// </summary>
    public static readonly ResilientCallOptions AbsoluteMove = new(
        MaxAttempts: 2,
        InitialBackoff: TimeSpan.FromMilliseconds(500),
        BackoffMultiplier: 1.0,
        IsIdempotent: true,
        OperationName: "move");
}
