using System;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging;

/// <summary>
/// <see cref="Parallel.For(int, int, Action{int})"/>, rethrowing a failing body's OWN exception, with its own
/// stack, instead of the <see cref="AggregateException"/> Parallel.For wraps it in.
/// </summary>
/// <remarks>
/// A document open walks its channels, and each walk its bands, in parallel. A channel whose statistics cannot
/// be taken throws <see cref="InvalidOperationException"/>, and the viewer shows that message as the reason a
/// file did not open. Wrapped, the reason read "One or more errors occurred", once per channel: the same
/// failure, told worse than the one-walk-at-a-time code told it. When several bodies fail they fail alike, so
/// the first recorded is the one rethrown.
/// </remarks>
internal static class ParallelFor
{
    public static void Run(int count, Action<int> body)
    {
        try
        {
            Parallel.For(0, count, body);
        }
        catch (AggregateException wrapped) when (wrapped.InnerExceptions.Count > 0)
        {
            ExceptionDispatchInfo.Capture(wrapped.InnerExceptions[0]).Throw();
        }
    }
}
