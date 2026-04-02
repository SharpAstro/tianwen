using System;
using System.Numerics.Tensors;

namespace TianWen.Lib.Devices.Guider;

/// <summary>
/// Tiny 3-layer MLP for guide correction prediction.
/// Input: recent guide errors + mount state → Output: RA/Dec pulse corrections.
/// Hand-rolled inference using <see cref="TensorPrimitives"/> for zero-allocation hot path.
/// </summary>
/// <remarks>
/// Architecture: Input(26) → Dense(32, ReLU) → Dense(16, ReLU) → Dense(2, Tanh)
/// 1,426 parameters total. Inference is ~2K FMAs, well under 1µs on modern CPUs.
/// The middle layer provides denoising capacity for gear noise and seeing jitter.
///
/// Input features (26):
///   [0-1]   Current RA/Dec error (pixels)
///   [2-3]   t-1 RA/Dec error (pixels)
///   [4-5]   t-2 RA/Dec error (pixels)
///   [6-7]   t-3 RA/Dec error (pixels)
///   [8-9]   Short-term mean RA/Dec error (10 frames, ~20s)
///   [10-11] Short-window RA/Dec RMS (pixels)
///   [12-13] Medium-term mean RA/Dec error (60 frames, ~2min)
///   [14-15] Long-term mean RA/Dec error (300 frames, ~10min ≈ 1 PE cycle)
///   [16-17] Accumulated gear error RA/Dec (normalized by running delta RMS)
///   [18]    Hour angle / 12 (normalized to [-1, 1])
///   [19]    Altitude / 90 (normalized to [0, 1])
///   [20]    Declination / 90 (normalized to [-1, 1])
///   [21]    Time since last correction (seconds, clamped to 30)
///   [22-23] RA encoder phase (sin, cos) — worm gear PE phase (0 when unavailable)
///   [24-25] Dec encoder phase (sin, cos) — Dec gear phase (0 when unavailable)
///
/// Output (2):
///   [0] RA correction (normalized: -1 to +1, maps to -MaxPulse to +MaxPulse)
///   [1] Dec correction (normalized: -1 to +1)
/// </remarks>
internal sealed class NeuralGuideModel
{
    /// <summary>Number of input features.</summary>
    internal const int InputSize = 26;

    /// <summary>Number of hidden units in layer 1.</summary>
    internal const int Hidden1Size = 32;

    /// <summary>Number of hidden units in layer 2.</summary>
    internal const int Hidden2Size = 16;

    /// <summary>Number of output values.</summary>
    internal const int OutputSize = 2;

    /// <summary>Total parameter count.</summary>
    internal const int TotalParams =
        (InputSize * Hidden1Size + Hidden1Size)     // Layer 1: 26*32 + 32 = 864
        + (Hidden1Size * Hidden2Size + Hidden2Size) // Layer 2: 32*16 + 16 = 528
        + (Hidden2Size * OutputSize + OutputSize);  // Layer 3: 16*2  + 2  = 34
    // Total: 1,426

    // Layer 1: Input → Hidden1 (weight matrix stored row-major: hidden1 × input)
    private readonly float[] _w1 = new float[Hidden1Size * InputSize];
    private readonly float[] _b1 = new float[Hidden1Size];

    // Layer 2: Hidden1 → Hidden2
    private readonly float[] _w2 = new float[Hidden2Size * Hidden1Size];
    private readonly float[] _b2 = new float[Hidden2Size];

    // Layer 3: Hidden2 → Output
    private readonly float[] _w3 = new float[OutputSize * Hidden2Size];
    private readonly float[] _b3 = new float[OutputSize];

    // Scratch buffers (avoid allocation on hot path)
    private readonly float[] _hidden1 = new float[Hidden1Size];
    private readonly float[] _hidden2 = new float[Hidden2Size];
    private readonly float[] _output = new float[OutputSize];

    /// <summary>
    /// Initializes the model with small random weights (Xavier initialization).
    /// </summary>
    /// <param name="seed">Random seed for reproducibility.</param>
    public void InitializeRandom(int seed = 42)
    {
        var rng = new Random(seed);

        // Xavier: std = sqrt(2 / (fan_in + fan_out))
        var std1 = MathF.Sqrt(2.0f / (InputSize + Hidden1Size));
        FillNormal(rng, _w1, std1);
        Array.Clear(_b1);

        var std2 = MathF.Sqrt(2.0f / (Hidden1Size + Hidden2Size));
        FillNormal(rng, _w2, std2);
        Array.Clear(_b2);

        var std3 = MathF.Sqrt(2.0f / (Hidden2Size + OutputSize));
        FillNormal(rng, _w3, std3);
        Array.Clear(_b3);
    }

    /// <summary>
    /// Loads model weights from a flat parameter array.
    /// </summary>
    /// <param name="parameters">Flat array of all parameters in order: w1, b1, w2, b2, w3, b3.</param>
    public void LoadParameters(ReadOnlySpan<float> parameters)
    {
        if (parameters.Length != TotalParams)
        {
            throw new ArgumentException($"Expected {TotalParams} parameters, got {parameters.Length}");
        }

        var offset = 0;
        parameters.Slice(offset, _w1.Length).CopyTo(_w1);
        offset += _w1.Length;
        parameters.Slice(offset, _b1.Length).CopyTo(_b1);
        offset += _b1.Length;
        parameters.Slice(offset, _w2.Length).CopyTo(_w2);
        offset += _w2.Length;
        parameters.Slice(offset, _b2.Length).CopyTo(_b2);
        offset += _b2.Length;
        parameters.Slice(offset, _w3.Length).CopyTo(_w3);
        offset += _w3.Length;
        parameters.Slice(offset, _b3.Length).CopyTo(_b3);
    }

    /// <summary>
    /// Exports all parameters as a flat array.
    /// </summary>
    public float[] ExportParameters()
    {
        var result = new float[TotalParams];
        var offset = 0;
        _w1.CopyTo(result.AsSpan(offset));
        offset += _w1.Length;
        _b1.CopyTo(result.AsSpan(offset));
        offset += _b1.Length;
        _w2.CopyTo(result.AsSpan(offset));
        offset += _w2.Length;
        _b2.CopyTo(result.AsSpan(offset));
        offset += _b2.Length;
        _w3.CopyTo(result.AsSpan(offset));
        offset += _w3.Length;
        _b3.CopyTo(result.AsSpan(offset));
        return result;
    }

    /// <summary>
    /// Runs forward inference. Zero-allocation on the hot path (uses pre-allocated buffers).
    /// </summary>
    /// <param name="input">Input feature vector of length <see cref="InputSize"/>.</param>
    /// <returns>Output span of length <see cref="OutputSize"/> (RA, Dec corrections normalized to [-1, 1]).</returns>
    public ReadOnlySpan<float> Forward(ReadOnlySpan<float> input)
    {
        if (input.Length != InputSize)
        {
            throw new ArgumentException($"Expected {InputSize} inputs, got {input.Length}");
        }

        // Layer 1: hidden1 = ReLU(W1 @ input + b1)
        MatVecAdd(_w1, input, _b1, _hidden1, Hidden1Size, InputSize);
        ReLUInPlace(_hidden1);

        // Layer 2: hidden2 = ReLU(W2 @ hidden1 + b2)
        MatVecAdd(_w2, _hidden1, _b2, _hidden2, Hidden2Size, Hidden1Size);
        ReLUInPlace(_hidden2);

        // Layer 3: output = W3 @ hidden2 + b3
        MatVecAdd(_w3, _hidden2, _b3, _output, OutputSize, Hidden2Size);

        // Clamp output to [-1, 1]
        TanhClampInPlace(_output);

        return _output;
    }

    /// <summary>
    /// Runs forward inference with caller-provided scratch buffers.
    /// Thread-safe: does not use any mutable instance state beyond the weight arrays.
    /// </summary>
    /// <param name="input">Input feature vector of length <see cref="InputSize"/>.</param>
    /// <param name="hidden1Scratch">Scratch buffer of length <see cref="Hidden1Size"/>.</param>
    /// <param name="hidden2Scratch">Scratch buffer of length <see cref="Hidden2Size"/>.</param>
    /// <param name="outputScratch">Scratch buffer of length <see cref="OutputSize"/>.</param>
    /// <returns>Output span (the outputScratch buffer, filled with results).</returns>
    public ReadOnlySpan<float> ForwardWithScratch(ReadOnlySpan<float> input, Span<float> hidden1Scratch, Span<float> hidden2Scratch, Span<float> outputScratch)
    {
        if (input.Length != InputSize)
        {
            throw new ArgumentException($"Expected {InputSize} inputs, got {input.Length}");
        }

        // Layer 1: hidden1 = ReLU(W1 @ input + b1)
        MatVecAdd(_w1, input, _b1, hidden1Scratch, Hidden1Size, InputSize);
        ReLUInPlace(hidden1Scratch);

        // Layer 2: hidden2 = ReLU(W2 @ hidden1 + b2)
        MatVecAdd(_w2, hidden1Scratch, _b2, hidden2Scratch, Hidden2Size, Hidden1Size);
        ReLUInPlace(hidden2Scratch);

        // Layer 3: output = W3 @ hidden2 + b3
        MatVecAdd(_w3, hidden2Scratch, _b3, outputScratch, OutputSize, Hidden2Size);

        // Clamp output to [-1, 1]
        TanhClampInPlace(outputScratch);

        return outputScratch;
    }

    /// <summary>
    /// Performs output = W @ x + b using TensorPrimitives for the dot products.
    /// W is stored row-major with dimensions [rows × cols].
    /// </summary>
    private static void MatVecAdd(
        ReadOnlySpan<float> w, ReadOnlySpan<float> x, ReadOnlySpan<float> b,
        Span<float> output, int rows, int cols)
    {
        for (var i = 0; i < rows; i++)
        {
            var row = w.Slice(i * cols, cols);
            output[i] = TensorPrimitives.Dot(row, x) + b[i];
        }
    }

    private static void ReLUInPlace(Span<float> data)
    {
        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] < 0)
            {
                data[i] = 0;
            }
        }
    }

    private static void TanhClampInPlace(Span<float> data)
    {
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = MathF.Tanh(data[i]);
        }
    }

    private static void FillNormal(Random rng, Span<float> data, float std)
    {
        for (var i = 0; i < data.Length; i++)
        {
            // Box-Muller transform for normal distribution
            var u1 = rng.NextDouble();
            var u2 = rng.NextDouble();
            var z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            data[i] = (float)(z * std);
        }
    }
}
