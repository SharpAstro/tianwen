using Shouldly;
using System;
using TianWen.Lib.Devices.Guider;
using Xunit;

namespace TianWen.Lib.Tests;

public class NeuralGuideModelTests
{
    [Fact]
    public void GivenModelWhenInitializedThenParameterCountCorrect()
    {
        // (16*32 + 32) + (32*2 + 2) = 512 + 32 + 64 + 2 = 610
        NeuralGuideModel.TotalParams.ShouldBe(610);
    }

    [Fact]
    public void GivenModelWhenForwardThenOutputSizeCorrect()
    {
        var model = new NeuralGuideModel();
        model.InitializeRandom();

        Span<float> input = stackalloc float[NeuralGuideModel.InputSize];
        input.Fill(0.5f);

        var output = model.Forward(input);

        output.Length.ShouldBe(NeuralGuideModel.OutputSize);
    }

    [Fact]
    public void GivenModelWhenForwardThenOutputInRange()
    {
        var model = new NeuralGuideModel();
        model.InitializeRandom();

        Span<float> input = stackalloc float[NeuralGuideModel.InputSize];
        var rng = new Random(123);
        for (var i = 0; i < input.Length; i++)
        {
            input[i] = (float)(rng.NextDouble() * 2 - 1);
        }

        var output = model.Forward(input);

        // Tanh clamp ensures output is in [-1, 1]
        for (var i = 0; i < output.Length; i++)
        {
            output[i].ShouldBeInRange(-1.0f, 1.0f);
        }
    }

    [Fact]
    public void GivenModelWhenSameInputThenDeterministic()
    {
        var model = new NeuralGuideModel();
        model.InitializeRandom(seed: 42);

        Span<float> input = stackalloc float[NeuralGuideModel.InputSize];
        input[0] = 1.0f;
        input[1] = -0.5f;

        var output1 = model.Forward(input).ToArray();
        var output2 = model.Forward(input).ToArray();

        output1[0].ShouldBe(output2[0]);
        output1[1].ShouldBe(output2[1]);
    }

    [Fact]
    public void GivenModelWhenExportImportThenOutputPreserved()
    {
        var model1 = new NeuralGuideModel();
        model1.InitializeRandom(seed: 99);

        Span<float> input = stackalloc float[NeuralGuideModel.InputSize];
        input[0] = 0.7f;
        input[1] = -0.3f;

        var output1 = model1.Forward(input).ToArray();

        // Export and import into a new model
        var parameters = model1.ExportParameters();
        parameters.Length.ShouldBe(NeuralGuideModel.TotalParams);

        var model2 = new NeuralGuideModel();
        model2.LoadParameters(parameters);

        var output2 = model2.Forward(input).ToArray();

        output2[0].ShouldBe(output1[0], 1e-6f);
        output2[1].ShouldBe(output1[1], 1e-6f);
    }

    [Fact]
    public void GivenZeroInputWhenForwardThenOutputIsZero()
    {
        var model = new NeuralGuideModel();
        model.InitializeRandom();

        Span<float> input = stackalloc float[NeuralGuideModel.InputSize];
        input.Clear(); // all zeros

        var output = model.Forward(input);

        // With zero input and zero biases (Xavier init sets b=0),
        // hidden = ReLU(0) = 0, output = 0 + 0 = 0
        output[0].ShouldBe(0f);
        output[1].ShouldBe(0f);
    }

    [Fact]
    public void GivenFeaturesWhenBuildThenCorrectSize()
    {
        var features = new NeuralGuideFeatures(siteLatitude: 45.0);
        Span<float> buffer = stackalloc float[NeuralGuideModel.InputSize];

        features.Build(
            raErrorPx: 1.5, decErrorPx: -0.3,
            timestampSec: 10.0,
            raRmsShort: 0.8, decRmsShort: 0.4,
            hourAngle: 2.0,
            declination: 45.0,
            buffer);

        buffer[0].ShouldBe(1.5f);      // current RA error
        buffer[1].ShouldBe(-0.3f);     // current Dec error
        buffer[2].ShouldBe(0f);        // t-1 RA (first call, no history)
        buffer[3].ShouldBe(0f);        // t-1 Dec (first call, no history)
        buffer[10].ShouldBe(0.8f);     // RA RMS short
        buffer[11].ShouldBe(0.4f);     // Dec RMS short
        buffer[13].ShouldBe(2.0f / 12f); // hour angle normalized
        buffer[15].ShouldBe(45.0f / 90f); // declination normalized
    }

    [Fact]
    public void GivenFeaturesWhenSecondCallThenPreviousPopulated()
    {
        var features = new NeuralGuideFeatures(siteLatitude: 45.0);
        Span<float> buffer = stackalloc float[NeuralGuideModel.InputSize];

        features.Build(1.0, -0.5, 10.0, 0.5, 0.3, 0, 45.0, buffer);
        features.Build(1.5, -0.8, 12.0, 0.6, 0.4, 0, 45.0, buffer);

        buffer[0].ShouldBe(1.5f);       // current RA
        buffer[1].ShouldBe(-0.8f);      // current Dec
        buffer[2].ShouldBe(1.0f);       // t-1 RA
        buffer[3].ShouldBe(-0.5f);      // t-1 Dec
        // [8-9] mean over 2 frames: mean RA = (1.0+1.5)/2=1.25, mean Dec = (-0.5-0.8)/2=-0.65
        buffer[8].ShouldBe(1.25f, 0.01f);
        buffer[9].ShouldBe(-0.65f, 0.01f);
    }

    [Fact]
    public void GivenFeaturesWhenResetThenPreviousCleared()
    {
        var features = new NeuralGuideFeatures(siteLatitude: 45.0);
        Span<float> buffer = stackalloc float[NeuralGuideModel.InputSize];

        features.Build(1.0, -0.5, 10.0, 0.5, 0.3, 0, 45.0, buffer);
        features.Reset();
        features.Build(2.0, 1.0, 20.0, 0.1, 0.1, 0, 45.0, buffer);

        buffer[2].ShouldBe(0f); // t-1 RA cleared by reset
        buffer[3].ShouldBe(0f); // t-1 Dec cleared by reset
    }

    [Fact]
    public void GivenWrongInputSizeWhenForwardThenThrows()
    {
        var model = new NeuralGuideModel();
        model.InitializeRandom();

        var input = new float[5]; // wrong size

        Should.Throw<ArgumentException>(() =>
        {
            model.Forward(input);
        });
    }
}
