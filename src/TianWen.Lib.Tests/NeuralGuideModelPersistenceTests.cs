using Shouldly;
using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices.Guider;
using Xunit;

namespace TianWen.Lib.Tests;

public class NeuralGuideModelPersistenceTests : IDisposable
{
    private readonly DirectoryInfo _tempDir;

    public NeuralGuideModelPersistenceTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("neural_guide_test_");
    }

    public void Dispose()
    {
        try { _tempDir.Delete(true); } catch { /* best effort */ }
    }

    private static GuiderCalibrationResult MakeCalibration()
    {
        return new GuiderCalibrationResult(
            CameraAngleRad: 0.1,
            RaRatePixPerSec: 5.0,
            DecRatePixPerSec: 4.5,
            RaDisplacementPx: 15.0,
            DecDisplacementPx: 13.5,
            TotalCalibrationTimeSec: 6.0);
    }

    [Fact]
    public async Task GivenModelWhenSaveAndLoadThenWeightsPreserved()
    {
        var ct = TestContext.Current.CancellationToken;

        var model1 = new NeuralGuideModel();
        model1.InitializeRandom(seed: 42);
        var cal = MakeCalibration();

        // Save
        await NeuralGuideModelPersistence.SaveAsync(model1, cal, _tempDir, ct);

        // Load into fresh model
        var model2 = new NeuralGuideModel();
        var loaded = await NeuralGuideModelPersistence.TryLoadAsync(model2, _tempDir, ct);

        loaded.ShouldNotBeNull();
        loaded.Value.CameraAngleRad.ShouldBe(cal.CameraAngleRad, 1e-10);
        loaded.Value.RaRatePixPerSec.ShouldBe(cal.RaRatePixPerSec, 1e-10);
        loaded.Value.DecRatePixPerSec.ShouldBe(cal.DecRatePixPerSec, 1e-10);

        // Verify model outputs match
        Span<float> input = stackalloc float[NeuralGuideModel.InputSize];
        input[0] = 1.0f;
        input[1] = -0.5f;
        var out1 = model1.Forward(input).ToArray();
        var out2 = model2.Forward(input).ToArray();

        out1[0].ShouldBe(out2[0], 1e-6f);
        out1[1].ShouldBe(out2[1], 1e-6f);
    }

    [Fact]
    public async Task GivenNoSavedFileWhenLoadThenReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var model = new NeuralGuideModel();

        var result = await NeuralGuideModelPersistence.TryLoadAsync(model, _tempDir, ct);
        result.ShouldBeNull();
    }

    [Fact]
    public async Task GivenCorruptFileWhenLoadThenReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;

        // Create a corrupt file
        var dir = _tempDir.CreateSubdirectory("NeuralGuider");
        var filePath = Path.Combine(dir.FullName, "00000000.ngm");
        await File.WriteAllBytesAsync(filePath, new byte[] { 0, 1, 2, 3 }, ct);

        var model = new NeuralGuideModel();
        var result = await NeuralGuideModelPersistence.TryLoadAsync(model, _tempDir, ct);
        result.ShouldBeNull();
    }

    [Fact]
    public async Task GivenWrongArchitectureFileWhenLoadThenReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;

        // Craft a file with correct magic/version but wrong InputSize
        var dir = _tempDir.CreateSubdirectory("NeuralGuider");
        var filePath = Path.Combine(dir.FullName, "00000000.ngm");

        // Compute what the file would look like with wrong dims
        var wrongInputSize = 10; // old size, doesn't match current 16
        var wrongTotalParams = (wrongInputSize * 32 + 32) + (32 * 2 + 2); // 418
        var wrongWeightsSize = wrongTotalParams * sizeof(float);
        var headerSize = 16; // magic(2) + version(2) + 3 ints(12)
        var calibrationSize = 48;
        var totalSize = headerSize + calibrationSize + wrongWeightsSize;

        var buffer = new byte[totalSize];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(span, 0x4E47); // magic
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], 0x0001); // version
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], wrongInputSize); // wrong!
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], 32); // HiddenSize
        BinaryPrimitives.WriteInt32LittleEndian(span[12..], 2); // OutputSize

        await File.WriteAllBytesAsync(filePath, buffer, ct);

        var model = new NeuralGuideModel();
        var result = await NeuralGuideModelPersistence.TryLoadAsync(model, _tempDir, ct);
        result.ShouldBeNull("wrong architecture should be rejected");
    }

    [Fact]
    public async Task GivenMultipleSavesWhenLoadThenGetsNewest()
    {
        var ct = TestContext.Current.CancellationToken;

        var model1 = new NeuralGuideModel();
        model1.InitializeRandom(seed: 1);
        var cal1 = MakeCalibration();
        await NeuralGuideModelPersistence.SaveAsync(model1, cal1, _tempDir, ct);

        // Backdate all existing files so the second save is unambiguously newer
        var ngDir = new DirectoryInfo(Path.Combine(_tempDir.FullName, "NeuralGuider"));
        foreach (var f in ngDir.GetFiles("*.ngm"))
        {
            f.LastWriteTimeUtc = DateTime.UtcNow.AddMinutes(-10);
        }

        // Save again with different weights
        var model2 = new NeuralGuideModel();
        model2.InitializeRandom(seed: 99);
        var cal2 = new GuiderCalibrationResult(0.2, 6.0, 5.5, 18.0, 16.5, 8.0);
        await NeuralGuideModelPersistence.SaveAsync(model2, cal2, _tempDir, ct);

        // Load should get the most recent
        var loaded = new NeuralGuideModel();
        var loadedCal = await NeuralGuideModelPersistence.TryLoadAsync(loaded, _tempDir, ct);
        loadedCal.ShouldNotBeNull();

        // Verify by comparing outputs
        Span<float> input = stackalloc float[NeuralGuideModel.InputSize];
        input[0] = 0.5f;
        var outLoaded = loaded.Forward(input).ToArray();
        var outModel2 = model2.Forward(input).ToArray();

        outLoaded[0].ShouldBe(outModel2[0], 1e-6f);
        outLoaded[1].ShouldBe(outModel2[1], 1e-6f);
    }
}
