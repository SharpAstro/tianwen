using System;

namespace TianWen.Lib.IO;

/// <summary>
/// A "keep the system awake" request: while a handle from <see cref="Acquire"/> is alive the computer does not go to sleep
/// on idle, although the display may (#1957). One seam for the node's hardware hold (<c>NodeKeepsAwake</c>) and for a
/// planetary recording (#1673), which reuses it.
/// </summary>
/// <remarks>
/// Each platform asks the OS in its own way and releases when the handle is disposed, and the OS releases it when the
/// process exits. <b>Windows takes a power REQUEST object</b> (<c>PowerCreateRequest</c>, <c>PowerRequestSystemRequired</c>),
/// never <c>SetThreadExecutionState</c>, which is per THREAD: a background service's continuation hops threads, so the
/// state would be set on one and cleared (or lost with it) on another. macOS and Linux hold a child process
/// (<see cref="ProcessPowerRequest"/>). A platform that cannot ask says so once in the log and hands out inert handles.
/// </remarks>
public interface IPowerRequest
{
    /// <summary>Asks the OS to keep the system awake for as long as the returned handle is not disposed.</summary>
    /// <param name="reason">Why, in words an operator reading the OS's own power report would understand.</param>
    /// <returns>A handle whose <see cref="IDisposable.Dispose"/> releases the request; calling it again does nothing.</returns>
    IDisposable Acquire(string reason);
}
