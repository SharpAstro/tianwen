using Shouldly;
using System;
using TianWen.Lib.Devices.Guider;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Guider")]
public class NeuralGuideTrainerTests(ITestOutputHelper output)
{
    private static GuiderCalibrationResult MakeCalibration()
    {
        return new GuiderCalibrationResult(
            CameraAngleRad: 0,
            RaRatePixPerSec: 5.0,
            DecRatePixPerSec: 5.0,
            RaDisplacementPx: 15.0,
            DecDisplacementPx: 15.0,
            TotalCalibrationTimeSec: 6.0);
    }

    [Fact]
    public void GivenTrainerWhenTrainEpochThenLossDecreases()
    {
        var model = new NeuralGuideModel();
        model.InitializeRandom(seed: 42);

        var cal = MakeCalibration();
        var pController = new ProportionalGuideController
        {
            AggressivenessRa = 0.7,
            AggressivenessDec = 0.7
        };

        var trainer = new NeuralGuideTrainer(model, learningRate: 0.01f, batchSize: 16);

        var loss0 = trainer.TrainEpoch(cal, pController, maxPulseMs: 2000, numSamples: 128, seed: 1);
        output.WriteLine($"Epoch 0 loss: {loss0:F6}");

        var prevLoss = loss0;
        for (var epoch = 1; epoch <= 20; epoch++)
        {
            var loss = trainer.TrainEpoch(cal, pController, maxPulseMs: 2000, numSamples: 128, seed: epoch + 1);
            output.WriteLine($"Epoch {epoch} loss: {loss:F6}");
            prevLoss = loss;
        }

        prevLoss.ShouldBeLessThan(loss0, "loss should decrease over training epochs");
    }

    [Fact]
    public void GivenTrainedModelWhenInferenceThenOutputReasonable()
    {
        var model = new NeuralGuideModel();
        model.InitializeRandom(seed: 42);

        var cal = MakeCalibration();
        var pController = new ProportionalGuideController
        {
            AggressivenessRa = 0.7,
            AggressivenessDec = 0.7
        };

        var trainer = new NeuralGuideTrainer(model, learningRate: 0.01f, batchSize: 16);

        // Train for several epochs (more needed for 16-input model with 610 params)
        for (var epoch = 0; epoch < 200; epoch++)
        {
            trainer.TrainEpoch(cal, pController, maxPulseMs: 2000, numSamples: 512, seed: epoch);
        }

        // Test: positive RA error should produce positive correction
        var features = new NeuralGuideFeatures(siteLatitude: 45.0);
        Span<float> input = stackalloc float[NeuralGuideModel.InputSize];
        features.Build(0, 0, 0, 0, 0, 0.5, 0.3, 0, 45.0, double.NaN, double.NaN, input);
        features.Build(2.0, 0, 0, 0, 2.0, 0.5, 0.3, 0, 45.0, double.NaN, double.NaN, input);

        var result = model.Forward(input);
        output.WriteLine($"RA error=2.0 → model RA correction={result[0]:F3}, Dec={result[1]:F3}");

        // P-controller reference
        var pCorr = pController.Compute(cal, 2.0, 0);
        var targetRa = (float)Math.Clamp(pCorr.RaPulseMs / 2000.0, -1.0, 1.0);
        output.WriteLine($"P-controller target: RA={targetRa:F3}");

        // Model output should have the same sign as the P-controller
        (result[0] * targetRa).ShouldBeGreaterThan(0,
            "model correction should have same sign as P-controller");
    }

    [Fact]
    public void GivenTrainerWhenZeroErrorThenSmallOutput()
    {
        var model = new NeuralGuideModel();
        model.InitializeRandom(seed: 42);

        var cal = MakeCalibration();
        var pController = new ProportionalGuideController();
        var trainer = new NeuralGuideTrainer(model, learningRate: 0.01f);

        // Train for a few epochs
        for (var epoch = 0; epoch < 30; epoch++)
        {
            trainer.TrainEpoch(cal, pController, maxPulseMs: 2000, numSamples: 128, seed: epoch);
        }

        // Zero error input should produce near-zero output
        var features = new NeuralGuideFeatures(siteLatitude: 45.0);
        Span<float> input = stackalloc float[NeuralGuideModel.InputSize];
        features.Build(0, 0, 0, 0, 0, 0.1, 0.1, 0, 45.0, double.NaN, double.NaN, input);
        features.Build(0, 0, 0, 0, 2.0, 0.1, 0.1, 0, 45.0, double.NaN, double.NaN, input);

        var result = model.Forward(input);
        output.WriteLine($"Zero error → RA={result[0]:F4}, Dec={result[1]:F4}");

        Math.Abs(result[0]).ShouldBeLessThan(0.3f, "zero error should produce small RA correction");
        Math.Abs(result[1]).ShouldBeLessThan(0.3f, "zero error should produce small Dec correction");
    }
}
