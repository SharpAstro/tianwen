using System;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// Aborts a session run with a user-facing reason. The message is written for the OBSERVER, not the
/// developer -- it surfaces verbatim as <see cref="ISession.FailureReason"/> (GUI notification, hosted
/// <c>/state</c>, CLI output), while the technical cause travels as <see cref="Exception.InnerException"/>
/// for the log. Throw it where a failure has a clear, user-actionable explanation (which device to check,
/// what to do about it); anything else falls through to the generic catch-all, which reports a generic
/// "Unexpected error" reason instead.
/// </summary>
public class SessionFailedException(string userMessage, Exception? innerException = null) : Exception(userMessage, innerException);
