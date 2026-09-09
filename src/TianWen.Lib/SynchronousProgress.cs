using System;

namespace TianWen.Lib
{
    /// <summary>
    /// An <see cref="IProgress{T}"/> that runs its sink INLINE, on whatever thread called
    /// <see cref="Report"/>.
    /// </summary>
    /// <remarks>
    /// <para><b><see cref="Progress{T}"/> is asynchronous, and that loses the last word.</b> It posts
    /// each report to the captured synchronization context, or to the thread pool when there is none --
    /// which is the case for anything started with <c>Task.Run</c>. A report queued during a run can
    /// therefore execute AFTER the caller has written its final state, overwriting it: the viewer's
    /// enhance finished and left the status line reading "Enhancing: gradient-correction (0%)" forever,
    /// caught by <c>EnhanceActionsTests</c> as a one-in-many-runs failure rather than by anyone looking
    /// at the screen. Reporting inline makes the ordering a fact rather than a race -- the last report
    /// happens before the awaited call returns, so the caller's final assignment always wins.</para>
    /// <para><b>The sink must be safe to run on the reporting thread</b>, which is the producer's, not
    /// the UI's. Both callers here write a handful of scalars to state a render loop reads as a
    /// snapshot, the pattern that is documented on those fields; a sink that needed the UI thread would
    /// have to marshal for itself, exactly as it would with any other callback.</para>
    /// </remarks>
    /// <typeparam name="T">The progress payload.</typeparam>
    /// <param name="sink">Invoked on every <see cref="Report"/>, synchronously.</param>
    public sealed class SynchronousProgress<T>(Action<T> sink) : IProgress<T>
    {
        private readonly Action<T> _sink = sink ?? throw new ArgumentNullException(nameof(sink));

        /// <inheritdoc />
        public void Report(T value) => _sink(value);
    }
}
