using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace TianWen.Lib.Imaging;

/// <summary>
/// Ref-counted owner of a <c>float[,]</c> image channel buffer.
/// When the last holder calls <see cref="Release"/>, the <paramref name="onRelease"/>
/// callback fires (typically returning the buffer to the camera for reuse).
/// <para>
/// Born with refCount=1 (creator holds it). Call <see cref="AddRef"/> before handing
/// to additional consumers. Each consumer calls <see cref="Release"/> when done.
/// </para>
/// </summary>
internal sealed class ChannelBuffer(
    float[,] data,
    Action<float[,]>? onRelease = null,
    [CallerMemberName] string producer = "",
    [CallerLineNumber] int producerLine = 0)
{
    private int _refCount = 1;
    private volatile bool _released;

    /// <summary>
    /// Handle into the DEBUG-only <see cref="ChannelBufferLeakTracker"/> table, cleared on final
    /// release so anything left over is a buffer nobody released. Always zero in Release, where
    /// <c>Register</c> has an empty body and inlines away.
    /// </summary>
    /// <remarks>
    /// The caller-info parameters above are what attribute a survivor to its producer. Two literals
    /// pushed per buffer is per-FRAME-CHANNEL work on a type wrapping a multi-megabyte array, which
    /// is the granularity the ownership policy allows; <c>[CallerFilePath]</c> is deliberately NOT
    /// among them, because it would bake the build machine's absolute source paths into a shipped
    /// package to buy a detail the member and line already give.
    /// </remarks>
    private readonly long _trackingId = ChannelBufferLeakTracker.Register(data, producer, producerLine);

    /// <summary>The backing pixel data (row-major [Height, Width]).</summary>
    /// <exception cref="ObjectDisposedException">Thrown if accessed after all refs released.</exception>
    public float[,] Data => !_released ? data : throw new ObjectDisposedException(nameof(ChannelBuffer));

    /// <summary>Image height (rows).</summary>
    public int Height => data.GetLength(0);

    /// <summary>Image width (columns).</summary>
    public int Width => data.GetLength(1);

    /// <summary>Whether all references have been released.</summary>
    public bool IsReleased => _released;

    /// <summary>Current reference count (for diagnostics).</summary>
    public int RefCount => Volatile.Read(ref _refCount);

    /// <summary>
    /// Increments the reference count. Call before handing the buffer to an additional consumer.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown if already fully released.</exception>
    public ChannelBuffer AddRef()
        => TryAddRef()
            ? this
            : throw new ObjectDisposedException(nameof(ChannelBuffer), "Cannot AddRef on a released ChannelBuffer");

    /// <summary>
    /// Takes a reference only if the buffer is still alive, returning <see langword="false"/> once it
    /// has reached zero. The caller owns a ref on success and must <see cref="Release"/> it.
    /// <para>
    /// <b>This has to be a CAS loop, not a check followed by an increment.</b> The obvious shape
    /// (<c>if (!_released) Interlocked.Increment(...)</c>) lets a borrower pass the liveness check
    /// on one thread while the last holder takes the count to zero on another, recycling the
    /// backing array before the increment lands. The borrower then holds a "live" ref to a buffer
    /// the camera has already reused, so it reads pixels from a frame it was never handed. A
    /// zero refcount is terminal here (nothing resurrects a released buffer), so comparing against
    /// the observed count and only incrementing from a positive value closes that window: the
    /// loser of the race learns it lost and answers <see langword="false"/>.
    /// </para>
    /// <para>
    /// The motivating borrower is a hosted preview encoding a live guide frame while the guide loop
    /// swaps and releases it on the very next exposure, where losing the race is normal and means
    /// "no frame right now", not an error.
    /// </para>
    /// </summary>
    public bool TryAddRef()
    {
        var observed = Volatile.Read(ref _refCount);
        while (observed > 0)
        {
            var actual = Interlocked.CompareExchange(ref _refCount, observed + 1, observed);
            if (actual == observed)
            {
                return true;
            }

            observed = actual;
        }

        return false;
    }

    /// <summary>
    /// Decrements the reference count. When it reaches zero, fires the <c>onRelease</c>
    /// callback so the camera can recycle the backing <c>float[,]</c>.
    /// </summary>
    /// <exception cref="ObjectDisposedException">
    /// Thrown when more releases arrive than refs were ever taken.
    /// </exception>
    /// <remarks>
    /// <para><b>This used to be idempotent, and the idempotency was hiding the one failure the
    /// refcount exists to prevent.</b> The old body clamped a negative count and returned, which
    /// guards the BENIGN case (a lone holder releasing twice, where the array was going back
    /// anyway) and is silent on the dangerous one: with two holders at count 2, a holder that
    /// releases twice takes the count to zero and recycles the array while the other is still
    /// reading it. Measured before the change -- two holders, one releasing twice, and the
    /// <c>onRelease</c> callback fired with a live borrower outstanding.</para>
    /// <para><b>It DETECTS a double-release; it does not prevent one, and the difference matters.</b>
    /// A buffer cannot see WHICH holder is calling, so the offending call is byte-identical to a
    /// legitimate last release: same method, same resulting count, no identity anywhere. It is
    /// therefore served -- the array goes back while the other holder is still reading -- and the
    /// throw lands on the NEXT release, the innocent one, which finds the till empty. So read the
    /// exception as "someone in this frame's chain released a ref it did not take", evidence that it
    /// happened rather than evidence of who did it, and look at the borrowers and not only at the
    /// stack. What the throw buys over the old clamp is that the excess stops being absorbed in
    /// silence; what it cannot buy is the window between the bad release and the next one.</para>
    /// <para><b>Prevention lives in the token, not in the counter.</b> The only production holder is
    /// an <see cref="Image"/>, whose <c>Release</c> spends its refs exactly once however many times
    /// it is called (an <see cref="Interlocked"/> exchange nulls the buffer array, so every later
    /// call finds nothing to decrement). That is what makes the hazard above unreachable from
    /// production today, and it is the shape to copy if a second kind of holder is ever added:
    /// give it a one-shot handle, do not hand it the shared count.</para>
    /// <para><b>It can throw from a <c>finally</c>, and that is accepted deliberately.</b> Most
    /// releases sit in cleanup, so an over-release during unwinding will replace the original
    /// exception with this one. That is a real cost, paid because the alternative is a recycled
    /// buffer silently feeding one holder's pixels to another -- corruption that surfaces later,
    /// somewhere else, as bad data rather than as an error.</para>
    /// </remarks>
    public void Release()
    {
        var count = Interlocked.Decrement(ref _refCount);
        if (count == 0)
        {
            _released = true;
            ChannelBufferLeakTracker.Unregister(_trackingId);
            onRelease?.Invoke(data);
        }
        else if (count < 0)
        {
            // Put the count back before throwing: zero is terminal and TryAddRef reads it, so
            // leaving it negative would be a second, quieter lie on top of the one we throw about.
            Interlocked.Increment(ref _refCount);
            throw new ObjectDisposedException(
                nameof(ChannelBuffer),
                "More releases than refs were taken on this buffer. Somewhere in this frame's chain a "
                + "holder released a ref it did not take, which recycles the array while another holder "
                + "is still reading it. The throwing call is not necessarily the offending one.");
        }
    }
}
