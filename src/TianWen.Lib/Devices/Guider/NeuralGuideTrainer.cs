using System;
using System.Numerics.Tensors;

namespace TianWen.Lib.Devices.Guider;

/// <summary>
/// Offline trainer for the neural guide model. Uses simulated guide error
/// trajectories with the P-controller as teacher (supervised learning).
/// Implements mini-batch SGD with MSE loss.
/// </summary>
internal sealed class NeuralGuideTrainer
{
    private readonly NeuralGuideModel _model;
    private readonly float _learningRate;
    private readonly int _batchSize;

    // Gradient accumulators (same layout as model parameters)
    private readonly float[] _gradW1;
    private readonly float[] _gradB1;
    private readonly float[] _gradW2;
    private readonly float[] _gradB2;
    private readonly float[] _gradW3;
    private readonly float[] _gradB3;

    // Forward pass cache for backpropagation
    private readonly float[] _hidden1PreAct;
    private readonly float[] _hidden1PostAct;
    private readonly float[] _hidden2PreAct;
    private readonly float[] _hidden2PostAct;
    private readonly float[] _outputPreAct;
    private readonly float[] _output;

    // Model weight references (accessed via ExportParameters/LoadParameters)
    private float[] _w1;
    private float[] _b1;
    private float[] _w2;
    private float[] _b2;
    private float[] _w3;
    private float[] _b3;

    public NeuralGuideTrainer(NeuralGuideModel model, float learningRate = 0.001f, int batchSize = 32)
    {
        _model = model;
        _learningRate = learningRate;
        _batchSize = batchSize;

        _gradW1 = new float[NeuralGuideModel.Hidden1Size * NeuralGuideModel.InputSize];
        _gradB1 = new float[NeuralGuideModel.Hidden1Size];
        _gradW2 = new float[NeuralGuideModel.Hidden2Size * NeuralGuideModel.Hidden1Size];
        _gradB2 = new float[NeuralGuideModel.Hidden2Size];
        _gradW3 = new float[NeuralGuideModel.OutputSize * NeuralGuideModel.Hidden2Size];
        _gradB3 = new float[NeuralGuideModel.OutputSize];

        _hidden1PreAct = new float[NeuralGuideModel.Hidden1Size];
        _hidden1PostAct = new float[NeuralGuideModel.Hidden1Size];
        _hidden2PreAct = new float[NeuralGuideModel.Hidden2Size];
        _hidden2PostAct = new float[NeuralGuideModel.Hidden2Size];
        _outputPreAct = new float[NeuralGuideModel.OutputSize];
        _output = new float[NeuralGuideModel.OutputSize];

        // Extract current weights from model
        var p = model.ExportParameters();
        var offset = 0;
        _w1 = p[offset..(offset + NeuralGuideModel.Hidden1Size * NeuralGuideModel.InputSize)];
        offset += _w1.Length;
        _b1 = p[offset..(offset + NeuralGuideModel.Hidden1Size)];
        offset += _b1.Length;
        _w2 = p[offset..(offset + NeuralGuideModel.Hidden2Size * NeuralGuideModel.Hidden1Size)];
        offset += _w2.Length;
        _b2 = p[offset..(offset + NeuralGuideModel.Hidden2Size)];
        offset += _b2.Length;
        _w3 = p[offset..(offset + NeuralGuideModel.OutputSize * NeuralGuideModel.Hidden2Size)];
        offset += _w3.Length;
        _b3 = p[offset..];
    }

    /// <summary>
    /// Trains the model for one epoch using simulated guide data.
    /// Generates random error trajectories, computes P-controller targets,
    /// and updates model weights via backpropagation.
    /// </summary>
    /// <param name="calibration">Calibration result for the P-controller.</param>
    /// <param name="pController">P-controller providing target corrections.</param>
    /// <param name="maxPulseMs">Maximum pulse in ms for normalizing targets.</param>
    /// <param name="numSamples">Number of training samples per epoch.</param>
    /// <param name="seed">Random seed for reproducibility.</param>
    /// <param name="inputNoiseStd">Standard deviation of Gaussian noise added to input features
    /// for robustness training. Simulates gear noise, encoder jitter, and seeing-induced
    /// measurement uncertainty. Recommended: 0.1–0.3 pixels. 0 = no augmentation.</param>
    /// <returns>Average MSE loss for the epoch.</returns>
    public float TrainEpoch(
        GuiderCalibrationResult calibration,
        ProportionalGuideController pController,
        double maxPulseMs,
        int numSamples = 256,
        int seed = 0,
        float inputNoiseStd = 0.15f)
    {
        var rng = new Random(seed);
        var totalLoss = 0.0f;
        var batchCount = 0;

        // Clear gradient accumulators
        ClearGradients();

        var features = new NeuralGuideFeatures(siteLatitude: 45.0);
        Span<float> inputBuffer = stackalloc float[NeuralGuideModel.InputSize];
        Span<float> dOutput = stackalloc float[NeuralGuideModel.OutputSize];

        for (var s = 0; s < numSamples; s++)
        {
            // Generate random guide error scenario
            var raError = (rng.NextDouble() - 0.5) * 6.0; // ±3 pixels
            var decError = (rng.NextDouble() - 0.5) * 4.0; // ±2 pixels
            var prevRaError = raError + (rng.NextDouble() - 0.5) * 1.0;
            var prevDecError = decError + (rng.NextDouble() - 0.5) * 0.5;
            var raRms = 0.2 + rng.NextDouble() * 1.5;
            var decRms = 0.1 + rng.NextDouble() * 1.0;
            var hourAngle = (rng.NextDouble() - 0.5) * 24.0;
            var declination = (rng.NextDouble() - 0.5) * 120.0; // ±60°

            // Build features from clean errors (2 frames of history per sample)
            // Simulate a small correction for the previous frame for gear error accumulation
            var prevCorrRa = -prevRaError * 0.7; // simulate P-controller correction in pixels
            var prevCorrDec = -prevDecError * 0.7;
            features.Reset();
            features.Build(prevRaError, prevDecError, 0, 0, 0, raRms, decRms, hourAngle, declination, inputBuffer);
            features.Build(raError, decError, prevCorrRa, prevCorrDec, 2.0, raRms, decRms, hourAngle, declination, inputBuffer);

            // Input noise augmentation: jitter the features while keeping the teacher target
            // computed from clean errors. This teaches the model to be conservative when
            // inputs are noisy (gear noise, encoder jitter, seeing).
            if (inputNoiseStd > 0)
            {
                for (var f = 0; f < inputBuffer.Length; f++)
                {
                    inputBuffer[f] += inputNoiseStd * (float)NextGaussian(rng);
                }
            }

            // Compute teacher target from clean errors (not noisy inputs)
            var correction = pController.Compute(calibration, raError, decError);
            var targetRa = (float)Math.Clamp(correction.RaPulseMs / maxPulseMs, -1.0, 1.0);
            var targetDec = (float)Math.Clamp(correction.DecPulseMs / maxPulseMs, -1.0, 1.0);

            // Forward pass with gradient tracking
            ForwardWithCache(inputBuffer);

            // Compute MSE loss
            var errRa = _output[0] - targetRa;
            var errDec = _output[1] - targetDec;
            totalLoss += errRa * errRa + errDec * errDec;

            // Backpropagation
            dOutput[0] = 2.0f * errRa * (1.0f - _output[0] * _output[0]); // tanh derivative
            dOutput[1] = 2.0f * errDec * (1.0f - _output[1] * _output[1]);

            AccumulateGradients(inputBuffer, dOutput);
            batchCount++;

            // Apply gradients at batch boundary
            if (batchCount >= _batchSize)
            {
                ApplyGradients(batchCount);
                ClearGradients();
                batchCount = 0;
            }
        }

        // Apply remaining gradients
        if (batchCount > 0)
        {
            ApplyGradients(batchCount);
        }

        // Sync weights back to model
        SyncToModel();

        return totalLoss / numSamples;
    }

    /// <summary>
    /// Trains on a batch sampled from an experience replay buffer.
    /// Used for online learning during guiding.
    /// </summary>
    /// <param name="buffer">Experience replay buffer to sample from.</param>
    /// <param name="batchSize">Number of samples per training step.</param>
    /// <param name="rng">Random number generator for sampling.</param>
    /// <param name="clipNorm">Maximum gradient component magnitude.</param>
    /// <returns>Average weighted loss for the batch, or 0 if no valid samples.</returns>
    public float TrainOnBatch(ExperienceReplayBuffer buffer, int batchSize, Random rng, float clipNorm = 1.0f)
    {
        Span<int> indices = stackalloc int[batchSize];
        var count = buffer.SampleBatch(indices, rng);
        if (count == 0)
        {
            return 0;
        }

        ClearGradients();
        var totalLoss = 0.0f;
        var totalWeight = 0.0f;
        Span<float> dOutput = stackalloc float[NeuralGuideModel.OutputSize];

        for (var s = 0; s < count; s++)
        {
            ref readonly var exp = ref buffer.GetAt(indices[s]);

            ForwardWithCache(exp.Features);

            var errRa = _output[0] - exp.TargetRa;
            var errDec = _output[1] - exp.TargetDec;
            var loss = errRa * errRa + errDec * errDec;
            totalLoss += loss * exp.PriorityWeight;
            totalWeight += exp.PriorityWeight;

            dOutput[0] = 2.0f * errRa * (1.0f - _output[0] * _output[0]) * exp.PriorityWeight;
            dOutput[1] = 2.0f * errDec * (1.0f - _output[1] * _output[1]) * exp.PriorityWeight;

            AccumulateGradients(exp.Features, dOutput);
        }

        ClipGradients(clipNorm);
        ApplyGradients(count);
        SyncToModel();

        return totalWeight > 0 ? totalLoss / totalWeight : 0;
    }

    private void ForwardWithCache(ReadOnlySpan<float> input)
    {
        // Layer 1: hidden1 = ReLU(W1 @ input + b1)
        for (var i = 0; i < NeuralGuideModel.Hidden1Size; i++)
        {
            var row = _w1.AsSpan(i * NeuralGuideModel.InputSize, NeuralGuideModel.InputSize);
            _hidden1PreAct[i] = TensorPrimitives.Dot(row, input) + _b1[i];
            _hidden1PostAct[i] = Math.Max(0, _hidden1PreAct[i]);
        }

        // Layer 2: hidden2 = ReLU(W2 @ hidden1 + b2)
        for (var i = 0; i < NeuralGuideModel.Hidden2Size; i++)
        {
            var row = _w2.AsSpan(i * NeuralGuideModel.Hidden1Size, NeuralGuideModel.Hidden1Size);
            _hidden2PreAct[i] = TensorPrimitives.Dot(row, _hidden1PostAct) + _b2[i];
            _hidden2PostAct[i] = Math.Max(0, _hidden2PreAct[i]);
        }

        // Layer 3: output = tanh(W3 @ hidden2 + b3)
        for (var i = 0; i < NeuralGuideModel.OutputSize; i++)
        {
            var row = _w3.AsSpan(i * NeuralGuideModel.Hidden2Size, NeuralGuideModel.Hidden2Size);
            _outputPreAct[i] = TensorPrimitives.Dot(row, _hidden2PostAct) + _b3[i];
            _output[i] = MathF.Tanh(_outputPreAct[i]);
        }
    }

    private void AccumulateGradients(ReadOnlySpan<float> input, ReadOnlySpan<float> dOutput)
    {
        // Backprop through layer 3: Hidden2 → Output
        for (var i = 0; i < NeuralGuideModel.OutputSize; i++)
        {
            _gradB3[i] += dOutput[i];
            for (var j = 0; j < NeuralGuideModel.Hidden2Size; j++)
            {
                _gradW3[i * NeuralGuideModel.Hidden2Size + j] += dOutput[i] * _hidden2PostAct[j];
            }
        }

        // Compute dHidden2
        Span<float> dHidden2 = stackalloc float[NeuralGuideModel.Hidden2Size];
        for (var j = 0; j < NeuralGuideModel.Hidden2Size; j++)
        {
            var sum = 0.0f;
            for (var i = 0; i < NeuralGuideModel.OutputSize; i++)
            {
                sum += dOutput[i] * _w3[i * NeuralGuideModel.Hidden2Size + j];
            }
            // ReLU derivative
            dHidden2[j] = _hidden2PreAct[j] > 0 ? sum : 0;
        }

        // Backprop through layer 2: Hidden1 → Hidden2
        for (var i = 0; i < NeuralGuideModel.Hidden2Size; i++)
        {
            _gradB2[i] += dHidden2[i];
            for (var j = 0; j < NeuralGuideModel.Hidden1Size; j++)
            {
                _gradW2[i * NeuralGuideModel.Hidden1Size + j] += dHidden2[i] * _hidden1PostAct[j];
            }
        }

        // Compute dHidden1
        Span<float> dHidden1 = stackalloc float[NeuralGuideModel.Hidden1Size];
        for (var j = 0; j < NeuralGuideModel.Hidden1Size; j++)
        {
            var sum = 0.0f;
            for (var i = 0; i < NeuralGuideModel.Hidden2Size; i++)
            {
                sum += dHidden2[i] * _w2[i * NeuralGuideModel.Hidden1Size + j];
            }
            // ReLU derivative
            dHidden1[j] = _hidden1PreAct[j] > 0 ? sum : 0;
        }

        // Backprop through layer 1: Input → Hidden1
        for (var i = 0; i < NeuralGuideModel.Hidden1Size; i++)
        {
            _gradB1[i] += dHidden1[i];
            for (var j = 0; j < NeuralGuideModel.InputSize; j++)
            {
                _gradW1[i * NeuralGuideModel.InputSize + j] += dHidden1[i] * input[j];
            }
        }
    }

    private void ApplyGradients(int count)
    {
        var scale = -_learningRate / count;

        for (var i = 0; i < _w1.Length; i++)
        {
            _w1[i] += scale * _gradW1[i];
        }
        for (var i = 0; i < _b1.Length; i++)
        {
            _b1[i] += scale * _gradB1[i];
        }
        for (var i = 0; i < _w2.Length; i++)
        {
            _w2[i] += scale * _gradW2[i];
        }
        for (var i = 0; i < _b2.Length; i++)
        {
            _b2[i] += scale * _gradB2[i];
        }
        for (var i = 0; i < _w3.Length; i++)
        {
            _w3[i] += scale * _gradW3[i];
        }
        for (var i = 0; i < _b3.Length; i++)
        {
            _b3[i] += scale * _gradB3[i];
        }
    }

    private void ClipGradients(float clipNorm)
    {
        ClipArray(_gradW1, clipNorm);
        ClipArray(_gradB1, clipNorm);
        ClipArray(_gradW2, clipNorm);
        ClipArray(_gradB2, clipNorm);
        ClipArray(_gradW3, clipNorm);
        ClipArray(_gradB3, clipNorm);

        static void ClipArray(float[] arr, float norm)
        {
            for (var i = 0; i < arr.Length; i++)
            {
                arr[i] = Math.Clamp(arr[i], -norm, norm);
            }
        }
    }

    private void ClearGradients()
    {
        Array.Clear(_gradW1);
        Array.Clear(_gradB1);
        Array.Clear(_gradW2);
        Array.Clear(_gradB2);
        Array.Clear(_gradW3);
        Array.Clear(_gradB3);
    }

    private void SyncToModel()
    {
        var p = new float[NeuralGuideModel.TotalParams];
        var offset = 0;
        _w1.CopyTo(p, offset); offset += _w1.Length;
        _b1.CopyTo(p, offset); offset += _b1.Length;
        _w2.CopyTo(p, offset); offset += _w2.Length;
        _b2.CopyTo(p, offset); offset += _b2.Length;
        _w3.CopyTo(p, offset); offset += _w3.Length;
        _b3.CopyTo(p, offset);
        _model.LoadParameters(p);
    }

    /// <summary>
    /// Box-Muller transform for generating standard normal random variates.
    /// </summary>
    private static double NextGaussian(Random rng)
    {
        var u1 = rng.NextDouble();
        var u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
