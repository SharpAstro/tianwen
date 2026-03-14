using System;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Sequencing;

public interface ISession : IAsyncDisposable
{
    Setup Setup { get; }

    ScheduledObservation? ActiveObservation { get; }

    ScheduledObservationTree Observations { get; }

    Task RunAsync(CancellationToken cancellationToken);
}
