using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Sequencing;

public interface ISession : IAsyncDisposable
{
    Observation? ActiveObservation { get; }

    IReadOnlyList<Observation> PlannedObservations { get; }

    Task RunAsync(CancellationToken cancellationToken);
}
