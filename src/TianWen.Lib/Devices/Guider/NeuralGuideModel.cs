using System;
using System.Numerics.Tensors;

namespace TianWen.Lib.Devices.Guider;

/// <summary>
/// Tiny 2-layer MLP for guide correction prediction.
/// Input: recent guide errors + mount state → Output: RA/Dec pulse corrections.
/// Hand-rolled inference using <see cref="TensorPrimitives"/> for zero-allocation hot path.
/// </summary>
/// <remarks>
/// Architecture: Input(10) → Dense(32, ReLU) → Dense(2, Linear)
/// ~354 parameters total. Inference is ~700 FMAs, well under 1µs on modern CPUs.
///
/// Input features (10):
///   [0] Current RA error (pixels)
///   [1] Current Dec error (pixels)
///   [2] Previous RA error (pixels)
///   [3] Previous Dec error (pixels)
///   [4] RA error rate (pixels/sec, finite difference)
///   [5] Dec error rate (pixels/sec, finite difference)
///   [6] Short-window RA RMS (pixels)
///   [7] Short-window Dec RMS (pixels)
///   [8] Time since last correction (seconds)
///   [9] Hour angle / 12 (normalized, for RA rate variation)
///
/// Output (2):
///   [0] RA correction (normalized: -1 to +1, maps to -MaxPulse to +MaxPulse)
///   [1] Dec correction (normalized: -1 to +1)
/// </remarks>
internal sealed class NeuralGuideModel
{
    /// <summary>Number of input features.</summary>
    internal const int InputSize = 10;

    /// <summary>Number of hidden units.</summary>
    internal const int HiddenSize = 32;

    /// <summary>Number of output values.</summary>
    internal const int OutputSize = 2;

    /// <summary>Total parameter count.</summary>
    internal const int TotalParams = (InputSize * HiddenSize + HiddenSize) + (HiddenSize * OutputSize + OutputSize);
    // = (10*32 + 32) + (32*2 + 2) = 320 + 32 + 64 + 2 = 418

    // Layer 1: Input → Hidden (weight matrix stored row-major: hidden × input)
    private readonly float[] _w1 = new float[HiddenSize * InputSize];
    private readonly float[] _b1 = new float[HiddenSize];

    // Layer 2: Hidden → Output
    private readonly float[] _w2 = new float[OutputSize * HiddenSize];
    private readonly float[] _b2 = new float[OutputSize];

    // Scratch buffers (avoid allocation on hot path)
    private readonly float[] _hidden = new float[HiddenSize];
    private readonly float[] _output = new float[OutputSize];

    /// <summary>
    /// Initializes the model with small random weights (Xavier initialization).
    /// </summary>
    /// <param name="seed">Random seed for reproducibility.</param>
    public void InitializeRandom(int seed = 42)
    {
        var rng = new Random(seed);

        // Xavier: std = sqrt(2 / (fan_in + fan_out))
        var std1 = MathF.Sqrt(2.0f / (InputSize + HiddenSize));
        FillNormal(rng, _w1, std1);
        Array.Clear(_b1);

        var std2 = MathF.Sqrt(2.0f / (HiddenSize + OutputSize));
        FillNormal(rng, _w2, std2);
        Array.Clear(_b2);
    }

    /// <summary>
    /// Loads model weights from a flat parameter array.
    /// </summary>
    /// <param name="parameters">Flat array of all parameters in order: w1, b1, w2, b2.</param>
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

        // Layer 1: hidden = ReLU(W1 @ input + b1)
        MatVecAdd(_w1, input, _b1, _hidden, HiddenSize, InputSize);
        ReLUInPlace(_hidden);

        // Layer 2: output = W2 @ hidden + b2
        MatVecAdd(_w2, _hidden, _b2, _output, OutputSize, HiddenSize);

        // Clamp output to [-1, 1]
        TanhClampInPlace(_output);

        return _output;
    }

    /// <summary>
    /// Runs forward inference with caller-provided scratch buffers.
    /// Thread-safe: does not use any mutable instance state beyond the weight arrays.
    /// </summary>
    /// <param name="input">Input feature vector of length <see cref="InputSize"/>.</param>
    /// <param name="hiddenScratch">Scratch buffer of length <see cref="HiddenSize"/>.</param>
    /// <param name="outputScratch">Scratch buffer of length <see cref="OutputSize"/>.</param>
    /// <returns>Output span (the outputScratch buffer, filled with results).</returns>
    public ReadOnlySpan<float> ForwardWithScratch(ReadOnlySpan<float> input, Span<float> hiddenScratch, Span<float> outputScratch)
    {
        if (input.Length != InputSize)
        {
            throw new ArgumentException($"Expected {InputSize} inputs, got {input.Length}");
        }

        // Layer 1: hidden = ReLU(W1 @ input + b1)
        MatVecAdd(_w1, input, _b1, hiddenScratch, HiddenSize, InputSize);
        ReLUInPlace(hiddenScratch);

        // Layer 2: output = W2 @ hidden + b2
        MatVecAdd(_w2, hiddenScratch, _b2, outputScratch, OutputSize, HiddenSize);

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
