using System;

namespace TianWen.Hosting.Dto
{
    /// <summary>
    /// One entry of the node's notification feed -- the hosted counterpart of the GUI's own feed.
    /// <para>
    /// Before this, a remote observer could see only <c>CurrentActivity</c> (which is overwritten on
    /// every sub-step, so anything transient is lost between two polls) and <c>FailureReason</c> (which
    /// exists only once the run has already failed). Everything in between -- a lost guide star that
    /// recovered, a plate solve that failed and was retried, a scout that skipped a target -- left no
    /// trace a client could read. The ring keeps that history so a client attaching mid-session still
    /// sees what it missed.
    /// </para>
    /// <para>
    /// <b>Severity travels as a string</b> matching the GUI's <c>NotificationSeverity</c> names
    /// (Info / Warning / Error). The enum lives in <c>TianWen.UI.Abstractions</c>, which the contracts
    /// assembly deliberately does not reference, and duplicating it here would create two enums to keep
    /// in step. A string also matches how every other enum already crosses this wire (the WebSocket
    /// payloads all send <c>ToString()</c>).
    /// </para>
    /// </summary>
    public sealed class NotificationDto
    {
        /// <summary>Info / Warning / Error, matching the GUI severity names.</summary>
        public required string Severity { get; init; }

        public required string Message { get; init; }

        /// <summary>When the node recorded it, from the node's <c>ITimeProvider</c> (so a simulated
        /// clock under TIANWEN_NOW stamps consistently with the rest of the session).</summary>
        public required DateTimeOffset TimestampUtc { get; init; }
    }
}
