using System;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// Output backing for the per-pixel master canvas an integrator writes to.
/// The hot-loop integrator code asks for one row at a time via
/// <see cref="GetRow"/>; the implementation decides whether that row span
/// points into a managed <c>float[,]</c> (today's default, see
/// <see cref="ArraySink"/>) or a memory-mapped file region (Phase 10's
/// <c>MemoryMappedFitsSink</c>, lands in step 2). Either way the per-pixel
/// rejector + combiner kernels stay identical.
/// </summary>
/// <remarks>
/// <para>This is the seam Phase 9's plan called "<c>IIntegrationSink</c>".
/// It was deferred until Phase 10 because the in-RAM concrete sink was the
/// only consumer and pre-introducing the interface would have been YAGNI.
/// With Phase 10's mmap path coming, the seam is now load-bearing.</para>
/// <para>Sinks own their underlying canvas; <see cref="IDisposable"/>
/// covers any file handle / mmap teardown. <see cref="ArraySink"/> is a
/// no-op disposer; the mmap sink will close + truncate-to-disk on dispose.</para>
/// </remarks>
public interface IIntegrationSink : IDisposable
{
    /// <summary>Canvas shape. Width and height match the output master;
    /// channel count matches the input frames (1 for mono, 3 for debayered
    /// RGB), or 1 for the rejection map sink.</summary>
    (int ChannelCount, int Width, int Height) Shape { get; }

    /// <summary>Mutable row span into the canvas at (<paramref name="channel"/>,
    /// <paramref name="row"/>). Caller writes up to <see cref="Shape"/>.Width
    /// floats. The span's lifetime is "until the sink is disposed"; the
    /// integrator's parallel-over-rows pattern only writes its own row
    /// concurrently, so no two workers ever hold spans into the same
    /// (channel, row) at the same time.</summary>
    Span<float> GetRow(int channel, int row);

    /// <summary>Materialise the finished canvas as an <see cref="Image"/>.
    /// <see cref="ArraySink"/> wraps its own array zero-copy; the mmap sink
    /// will either return a buffered copy or do the final file write here
    /// and return a thin reader-backed Image. Either way the caller gets a
    /// usable <see cref="Image"/> for the result record.</summary>
    Image FinaliseAsImage(BitDepth bitDepth, float maxValue, float minValue, float pedestal, ImageMeta meta);
}
