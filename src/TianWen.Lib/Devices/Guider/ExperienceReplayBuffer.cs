using System;
using System.Threading;

namespace TianWen.Lib.Devices.Guider;

/// <summary>
/// Fixed-capacity circular ring buffer for guide experience tuples.
/// The guide loop writes one entry per frame; the background trainer samples batches.
/// Thread-safe for single-writer (guide thread) / single-reader (training thread).
/// </summary>
internal sealed class ExperienceReplayBuffer
{
    private readonly OnlineGuideExperience[] _buffer;
    private int _writeIndex;
    private int _totalWritten;

    /// <summary>
    /// Creates a new experience replay buffer.
    /// </summary>
    /// <param name="capacity">Maximum number of experiences to store. Default 2048 (~68 min at 2s cadence, multiple PE cycles).</param>
    public ExperienceReplayBuffer(int capacity = 2048)
    {
        _buffer = new OnlineGuideExperience[capacity];
    }

    /// <summary>
    /// Number of experiences currently available (up to capacity).
    /// </summary>
    public int Count => Math.Min(Volatile.Read(ref _totalWritten), _buffer.Length);

    /// <summary>
    /// Total number of experiences ever written.
    /// </summary>
    public int TotalWritten => Volatile.Read(ref _totalWritten);

    /// <summary>
    /// Buffer capacity.
    /// </summary>
    public int Capacity => _buffer.Length;

    /// <summary>
    /// Adds an experience to the ring buffer.
    /// </summary>
    public void Add(in OnlineGuideExperience experience)
    {
        var idx = _writeIndex % _buffer.Length;
        _buffer[idx] = experience;
        _writeIndex = idx + 1;
        Interlocked.Increment(ref _totalWritten);
    }

    /// <summary>
    /// Updates the outcome of the most recently written experience.
    /// Called from the guide thread at the start of the next frame.
    /// Computes the hindsight-optimal correction target: what correction would have
    /// zeroed the next-frame error, given the correction that was actually applied.
    /// </summary>
    /// <param name="nextRaError">RA error observed in the next frame (pixels).</param>
    /// <param name="nextDecError">Dec error observed in the next frame (pixels).</param>
    /// <param name="prevRaError">RA error from the frame that produced the experience.</param>
    /// <param name="prevDecError">Dec error from the frame that produced the experience.</param>
    /// <param name="raRateScale">Conversion factor: pixels → normalized correction = 1000 / (raRate * maxPulseMs).</param>
    /// <param name="decRateScale">Conversion factor: pixels → normalized correction = 1000 / (decRate * maxPulseMs).</param>
    public void UpdateOutcome(
        double nextRaError, double nextDecError,
        double prevRaError, double prevDecError,
        double raRateScale, double decRateScale)
    {
        if (_totalWritten == 0)
        {
            return;
        }

        var idx = (_writeIndex - 1 + _buffer.Length) % _buffer.Length;
        ref var exp = ref _buffer[idx];

        if (exp.OutcomeKnown)
        {
            return;
        }

        // Hindsight-optimal target: applied correction + residual correction to zero the next-frame error.
        // If we applied C and still had residual E, the ideal correction was C + (-E / rate * 1000 / maxPulse).
        // The sign convention matches the P-controller: negative error → positive correction.
        exp.TargetRa = (float)Math.Clamp(exp.AppliedRaNorm - nextRaError * raRateScale, -1.0, 1.0);
        exp.TargetDec = (float)Math.Clamp(exp.AppliedDecNorm - nextDecError * decRateScale, -1.0, 1.0);

        // Priority weight: higher when error got worse (model needs more training on these)
        var prevMag = Math.Sqrt(prevRaError * prevRaError + prevDecError * prevDecError);
        var nextMag = Math.Sqrt(nextRaError * nextRaError + nextDecError * nextDecError);

        // Ratio > 1 means error grew (bad correction), ratio < 1 means error shrank (good)
        // Weight range: [0.5, 2.0] — upweight failures, downweight successes
        var ratio = prevMag > 0.01 ? nextMag / prevMag : 1.0;
        exp.PriorityWeight = (float)Math.Clamp(ratio, 0.5, 2.0);
        exp.OutcomeKnown = true;
    }

    /// <summary>
    /// Samples a batch of random indices from populated, outcome-known slots.
    /// </summary>
    /// <param name="indices">Span to fill with sampled indices.</param>
    /// <param name="rng">Random number generator.</param>
    /// <returns>Actual number of indices sampled (may be less than indices.Length).</returns>
    public int SampleBatch(Span<int> indices, Random rng)
    {
        var count = Count;
        if (count == 0)
        {
            return 0;
        }

        var sampled = 0;
        var attempts = 0;
        var maxAttempts = indices.Length * 4;

        while (sampled < indices.Length && attempts < maxAttempts)
        {
            attempts++;
            var idx = rng.Next(count);

            if (!_buffer[idx].OutcomeKnown)
            {
                continue;
            }

            indices[sampled++] = idx;
        }

        return sampled;
    }

    /// <summary>
    /// Gets a read-only reference to the experience at the given index.
    /// </summary>
    public ref readonly OnlineGuideExperience GetAt(int index)
    {
        return ref _buffer[index];
    }

    /// <summary>
    /// Resets the buffer, clearing all experiences.
    /// </summary>
    public void Reset()
    {
        _writeIndex = 0;
        Volatile.Write(ref _totalWritten, 0);
        Array.Clear(_buffer);
    }
}
