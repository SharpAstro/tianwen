using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Sequencing;

public interface ISessionFactory
{
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);

    ISession Create(Guid profileId, in SessionConfiguration configuration, IReadOnlyList<Observation> observations);
}
