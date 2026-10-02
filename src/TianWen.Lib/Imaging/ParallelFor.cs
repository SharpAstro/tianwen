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

    /// <summary>
    /// <paramref name="body"/> over [0, <paramref name="count"/>) in contiguous bands, a few per core, each given as its first
    /// index and one past its last: for a kernel whose rows are independent, so a band's set-up (a stackalloc, a span) is paid
    /// once a band rather than once a row. A kernel that only GATHERS into its own row, as a stack's fold does, gives the same
    /// bits in bands as in one walk, since no output pixel's sum changes order.
    /// </summary>
    public static void RunBands(int count, Action<int, int> body)
    {
        if (count <= 0)
        {
            return;
        }

        var bands = Math.Min(count, Environment.ProcessorCount * 4);
        Run(bands, b => body((int)((long)b * count / bands), (int)((long)(b + 1) * count / bands)));
    }
}
