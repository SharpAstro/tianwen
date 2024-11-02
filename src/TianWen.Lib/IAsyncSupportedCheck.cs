using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib;

public interface IAsyncSupportedCheck
{
    /// <summary>
    /// Indicates if the implementation is supported or properly setup on the executing system.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns>true if the implementation is supported on this platform/installed on the system.</returns>
    Task<bool> CheckSupportAsync(CancellationToken cancellationToken = default);
}
