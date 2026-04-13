using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Extensions;

namespace TianWen.Lib.Sequencing;

internal partial record Session
{
    internal bool Catch(Action action) => _logger.Catch(action);

    internal T Catch<T>(Func<T> func, T @default = default) where T : struct => _logger.Catch(func, @default);
    internal ValueTask<bool> CatchAsync(Func<CancellationToken, ValueTask> asyncFunc, CancellationToken cancellationToken)
        => _logger.CatchAsync(asyncFunc, cancellationToken);
    internal Task<bool> CatchAsync(Func<CancellationToken, Task> asyncFunc, CancellationToken cancellationToken)
        => _logger.CatchAsync(asyncFunc, cancellationToken);


    internal ValueTask<T> CatchAsync<T>(Func<CancellationToken, ValueTask<T>> asyncFunc, CancellationToken cancellationToken, T @default = default) where T : struct
        => _logger.CatchAsync(asyncFunc, cancellationToken, @default);

}
