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

    // Forward pass cache for backpropagation
    private readonly float[] _hiddenPreAct;
    private readonly float[] _hiddenPostAct;
    private readonly float[] _outputPreAct;
    private readonly float[] _output;

    // Model weight references (accessed via ExportParameters/LoadParameters)
    private float[] _w1;
    private float[] _b1;
    private float[] _w2;
    private float[] _b2;

    public NeuralGuideTrainer(NeuralGuideModel model, float learningRate = 0.001f, int batchSize = 32)
    {
        _model = model;
        _learningRate = learningRate;
        _batchSize = batchSize;

        _gradW1 = new float[NeuralGuideModel.HiddenSize * NeuralGuideModel.InputSize];
        _gradB1 = new float[NeuralGuideModel.HiddenSize];
        _gradW2 = new float[NeuralGuideModel.OutputSize * NeuralGuideModel.HiddenSize];
        _gradB2 = new float[NeuralGuideModel.OutputSize];

        _hiddenPreAct = new float[NeuralGuideModel.HiddenSize];
        _hiddenPostAct = new float[NeuralGuideModel.HiddenSize];
        _outputPreAct = new float[NeuralGuideModel.OutputSize];
        _output = new float[NeuralGuideModel.OutputSize];

        // Extract current weights from model
        var p = model.ExportParameters();
        var offset = 0;
        _w1 = p[offset..(offset + NeuralGuideModel.HiddenSize * NeuralGuideModel.InputSize)];
        offset += _w1.Length;
        _b1 = p[offset..(offset + NeuralGuideModel.HiddenSize)];
        offset += _b1.Length;
        _w2 = p[offset..(offset + NeuralGuideModel.OutputSize * NeuralGuideModel.HiddenSize)];
        offset += _w2.Length;
        _b2 = p[offset..];
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
    /// <returns>Average MSE loss for the epoch.</returns>
    public float TrainEpoch(
        GuiderCalibrationResult calibration,
        ProportionalGuideController pController,
        double maxPulseMs,
        int numSamples = 256,
        int seed = 0)
    {
        var rng = new Random(seed);
        var totalLoss = 0.0f;
        var batchCount = 0;

        // Clear gradient accumulators
        ClearGradients();

        var features = new NeuralGuideFeatures();
        Span<float> inputBuffer = stackalloc float[NeuralGuideModel.InputSize];

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
            var timeSinceCorrection = rng.NextDouble() * 10.0;

            // Build features
            features.Reset();
            features.Build(prevRaError, prevDecError, 0, raRms, decRms, hourAngle, inputBuffer);
            features.Build(raError, decError, 2.0, raRms, decRms, hourAngle, inputBuffer);

            // Compute teacher target
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
            Span<float> dOutput = stackalloc float[NeuralGuideModel.OutputSize];
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

        for (var s = 0; s < count; s++)
        {
            ref readonly var exp = ref buffer.GetAt(indices[s]);

            ForwardWithCache(exp.Features);

            var errRa = _output[0] - exp.TargetRa;
            var errDec = _output[1] - exp.TargetDec;
            var loss = errRa * errRa + errDec * errDec;
            totalLoss += loss * exp.PriorityWeight;
            totalWeight += exp.PriorityWeight;

            Span<float> dOutput = stackalloc float[NeuralGuideModel.OutputSize];
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
        // Layer 1: hidden = ReLU(W1 @ input + b1)
        for (var i = 0; i < NeuralGuideModel.HiddenSize; i++)
        {
            var row = _w1.AsSpan(i * NeuralGuideModel.InputSize, NeuralGuideModel.InputSize);
            _hiddenPreAct[i] = TensorPrimitives.Dot(row, input) + _b1[i];
            _hiddenPostAct[i] = Math.Max(0, _hiddenPreAct[i]);
        }

        // Layer 2: output = tanh(W2 @ hidden + b2)
        for (var i = 0; i < NeuralGuideModel.OutputSize; i++)
        {
            var row = _w2.AsSpan(i * NeuralGuideModel.HiddenSize, NeuralGuideModel.HiddenSize);
            _outputPreAct[i] = TensorPrimitives.Dot(row, _hiddenPostAct) + _b2[i];
            _output[i] = MathF.Tanh(_outputPreAct[i]);
        }
    }

    private void AccumulateGradients(ReadOnlySpan<float> input, ReadOnlySpan<float> dOutput)
    {
        // Backprop through layer 2
        for (var i = 0; i < NeuralGuideModel.OutputSize; i++)
        {
            _gradB2[i] += dOutput[i];
            for (var j = 0; j < NeuralGuideModel.HiddenSize; j++)
            {
                _gradW2[i * NeuralGuideModel.HiddenSize + j] += dOutput[i] * _hiddenPostAct[j];
            }
        }

        // Compute dHidden
        Span<float> dHidden = stackalloc float[NeuralGuideModel.HiddenSize];
        for (var j = 0; j < NeuralGuideModel.HiddenSize; j++)
        {
            var sum = 0.0f;
            for (var i = 0; i < NeuralGuideModel.OutputSize; i++)
            {
                sum += dOutput[i] * _w2[i * NeuralGuideModel.HiddenSize + j];
            }
            // ReLU derivative
            dHidden[j] = _hiddenPreAct[j] > 0 ? sum : 0;
        }

        // Backprop through layer 1
        for (var i = 0; i < NeuralGuideModel.HiddenSize; i++)
        {
            _gradB1[i] += dHidden[i];
            for (var j = 0; j < NeuralGuideModel.InputSize; j++)
            {
                _gradW1[i * NeuralGuideModel.InputSize + j] += dHidden[i] * input[j];
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
    }

    private void ClipGradients(float clipNorm)
    {
        ClipArray(_gradW1, clipNorm);
        ClipArray(_gradB1, clipNorm);
        ClipArray(_gradW2, clipNorm);
        ClipArray(_gradB2, clipNorm);

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
    }

    private void SyncToModel()
    {
        var p = new float[NeuralGuideModel.TotalParams];
        var offset = 0;
        _w1.CopyTo(p, offset); offset += _w1.Length;
        _b1.CopyTo(p, offset); offset += _b1.Length;
        _w2.CopyTo(p, offset); offset += _w2.Length;
        _b2.CopyTo(p, offset);
        _model.LoadParameters(p);
    }
}
