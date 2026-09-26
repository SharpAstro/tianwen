using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting.Dto;

namespace TianWen.Hosting;

/// <summary>
/// A run of the node that is not an imaging session (P5 of docs/plans/hardware-in-the-server.md, #934): a dark library
/// first, and polar alignment and planetary capture after it. It starts through
/// <see cref="IHostedSession.TryStartAsync(INodeRun, Guid)"/> exactly as a session does, so it runs on the node's token (a
/// client's connection can neither start it twice nor cut it off), one run at a time with a session or a flat run, and
/// the crash journal records its kind.
/// </summary>
/// <remarks>
/// It releases what it holds, its device lease above all, as its BODY ends: a run that went on holding a device until the
/// node's next run replaced it would refuse every command to that device in between. Disposing it releases anything
/// left, for a run whose body never ran; the node disposes a run once the next start replaces it, or as the host stops,
/// never while the body is still going.
/// </remarks>
public interface INodeRun : IAsyncDisposable
{
    /// <summary>What kind of run it is: what the journal records and a refused start names.</summary>
    NodeRunKind Kind { get; }

    /// <summary>The run's body. Cancelled by an abort or the host stopping, and by nothing else.</summary>
    Task RunAsync(CancellationToken cancellationToken);
}
