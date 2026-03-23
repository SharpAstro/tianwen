using System;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Sequencing;

public interface ISessionFactory
{
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);

    ISession Create(Guid profileId, in SessionConfiguration configuration, ReadOnlySpan<ScheduledObservation> observations);
}
