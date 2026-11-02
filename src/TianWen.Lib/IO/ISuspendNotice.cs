using System;
using System.Threading.Tasks;

namespace TianWen.Lib.IO;

/// <summary>
/// The OS's notice that the computer is about to suspend, and that it has woken (#1957). One seam, so the node can stop its
/// mounts first where the OS gives notice, and a test raises the notice itself.
/// </summary>
/// <remarks>
/// <b>Windows</b> gives about two seconds between the notice and the suspend, through
/// <c>PowerRegisterSuspendResumeNotification</c> with a callback (no window, no message pump): the callback waits for every
/// <see cref="Suspending"/> handler, up to a bound. <b>Elsewhere there is no notice to take</b> (logind's
/// <c>PrepareForSleep</c> needs D-Bus; macOS needs a run loop), so <see cref="NoSuspendNotice"/> says so once and never raises;
/// the sleep inhibitor <see cref="IPowerRequest"/> holds is the protection there.
/// </remarks>
public interface ISuspendNotice
{
    /// <summary>
    /// Raised before the system suspends. A handler returns a task that ends when its work is done; the OS is made to wait for
    /// it, within its own limit, so a handler bounds itself well inside that.
    /// </summary>
    event Func<Task>? Suspending;

    /// <summary>Raised when the system has resumed.</summary>
    event Action? Resumed;
}
