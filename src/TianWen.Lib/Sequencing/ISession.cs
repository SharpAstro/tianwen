using System;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Sequencing;

public interface ISession : IAsyncDisposable
{
    ScheduledObservation? ActiveObservation { get; }

    ScheduledObservationTree Observations { get; }

    Task RunAsync(CancellationToken cancellationToken);
}
