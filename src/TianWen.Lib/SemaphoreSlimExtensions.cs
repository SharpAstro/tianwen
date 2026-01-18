using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib;

public static class SemaphoreSlimExtensions
{
    public static async ValueTask<ResourceLock> AcquireLockAsync(this SemaphoreSlim semaphore, CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);

        return new ResourceLock(semaphore);
    }
}