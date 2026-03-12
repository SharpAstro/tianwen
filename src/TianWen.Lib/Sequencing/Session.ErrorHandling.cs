using System;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Sequencing;

internal partial record Session
{
    internal bool Catch(Action action) => External.Catch(action);

    internal T Catch<T>(Func<T> func, T @default = default) where T : struct => External.Catch(func, @default);
    internal ValueTask<bool> CatchAsync(Func<CancellationToken, ValueTask> asyncFunc, CancellationToken cancellationToken)
        => External.CatchAsync(asyncFunc, cancellationToken);
    internal Task<bool> CatchAsync(Func<CancellationToken, Task> asyncFunc, CancellationToken cancellationToken)
        => External.CatchAsync(asyncFunc, cancellationToken);


    internal ValueTask<T> CatchAsync<T>(Func<CancellationToken, ValueTask<T>> asyncFunc, CancellationToken cancellationToken, T @default = default) where T : struct
        => External.CatchAsync(asyncFunc, cancellationToken, @default);

}
