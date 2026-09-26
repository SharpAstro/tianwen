using Shouldly;
using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices.Guider;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Guider")]
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
            DecAngleRad: 0.1 + Math.PI / 2.0,
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
        loaded.Value.DecAngleRad.ShouldBe(cal.DecAngleRad, 1e-10);
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
    public async Task GivenOlderFormatVersionFileWhenLoadThenReturnsNullAndDeletesIt()
    {
        var ct = TestContext.Current.CancellationToken;

        // Craft a file with correct magic but an OLDER format version (and another InputSize): a model
        // trained under a superseded calibration, stale for every build, so it is discarded.
        var dir = _tempDir.CreateSubdirectory("NeuralGuider");
        var filePath = Path.Combine(dir.FullName, "00000000.ngm");

        // Compute what the file would look like with wrong dims
        var wrongInputSize = 10; // doesn't match current 26
        var wrongTotalParams = (wrongInputSize * 32 + 32) + (32 * 16 + 16) + (16 * 2 + 2); // 882
        var wrongWeightsSize = wrongTotalParams * sizeof(float);
        var headerSize = 20; // magic(2) + version(2) + 4 ints(16)
        var calibrationSize = 48;
        var totalSize = headerSize + calibrationSize + wrongWeightsSize;

        var buffer = new byte[totalSize];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(span, 0x4E47); // magic
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], 0x0002); // older than the current 0x0003
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], wrongInputSize); // wrong!
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], 32); // Hidden1Size
        BinaryPrimitives.WriteInt32LittleEndian(span[12..], 16); // Hidden2Size
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 2); // OutputSize

        await File.WriteAllBytesAsync(filePath, buffer, ct);

        var model = new NeuralGuideModel();
        var result = await NeuralGuideModelPersistence.TryLoadAsync(model, _tempDir, ct);
        result.ShouldBeNull("an older format version should be rejected");
        File.Exists(filePath).ShouldBeFalse("a rejected stale model must be deleted, not left to be re-read on the next load");
    }

    [Fact]
    public async Task GivenTruncatedNewestFileBesideValidOlderOneWhenLoadThenReturnsTheOlderModel()
    {
        var ct = TestContext.Current.CancellationToken;

        var saved = new NeuralGuideModel();
        saved.InitializeRandom(seed: 7);
        var cal = MakeCalibration();
        await NeuralGuideModelPersistence.SaveAsync(saved, cal, _tempDir, ct);

        var ngDir = new DirectoryInfo(Path.Combine(_tempDir.FullName, "NeuralGuider"));
        var valid = ngDir.GetFiles("*.ngm").ShouldHaveSingleItem();
        valid.LastWriteTimeUtc = DateTime.UtcNow.AddMinutes(-10);

        // What a crash mid-write used to leave: the first part of a model, under a name of its own, and NEWEST.
        var bytes = await File.ReadAllBytesAsync(valid.FullName, ct);
        var truncatedPath = Path.Combine(ngDir.FullName, "FFFFFFFF.ngm");
        await File.WriteAllBytesAsync(truncatedPath, bytes.AsSpan(0, bytes.Length / 2).ToArray(), ct);
        File.SetLastWriteTimeUtc(truncatedPath, DateTime.UtcNow);

        var loaded = new NeuralGuideModel();
        var loadedCal = await NeuralGuideModelPersistence.TryLoadAsync(loaded, _tempDir, ct);

        loadedCal.ShouldNotBeNull("a truncated newest file must not hide the valid older model");
        loadedCal.Value.CameraAngleRad.ShouldBe(cal.CameraAngleRad, 1e-10);

        Span<float> input = stackalloc float[NeuralGuideModel.InputSize];
        input[0] = 0.5f;
        var outLoaded = loaded.Forward(input).ToArray();
        var outSaved = saved.Forward(input).ToArray();
        outLoaded[0].ShouldBe(outSaved[0], 1e-6f);
        outLoaded[1].ShouldBe(outSaved[1], 1e-6f);

        File.Exists(truncatedPath).ShouldBeFalse("a truncated file is useless to every binary and is discarded");
        File.Exists(valid.FullName).ShouldBeTrue();
    }

    [Fact]
    public async Task GivenOtherArchitectureNewestFileWhenLoadThenKeepsItAndLoadsTheOlderModel()
    {
        var ct = TestContext.Current.CancellationToken;

        var saved = new NeuralGuideModel();
        saved.InitializeRandom(seed: 11);
        await NeuralGuideModelPersistence.SaveAsync(saved, MakeCalibration(), _tempDir, ct);

        var ngDir = new DirectoryInfo(Path.Combine(_tempDir.FullName, "NeuralGuider"));
        var valid = ngDir.GetFiles("*.ngm").ShouldHaveSingleItem();
        valid.LastWriteTimeUtc = DateTime.UtcNow.AddMinutes(-10);

        var foreignPath = Path.Combine(ngDir.FullName, "EEEEEEEE.ngm");
        await File.WriteAllBytesAsync(foreignPath, OtherArchitectureFile(), ct);
        File.SetLastWriteTimeUtc(foreignPath, DateTime.UtcNow);

        var loaded = new NeuralGuideModel();
        var loadedCal = await NeuralGuideModelPersistence.TryLoadAsync(loaded, _tempDir, ct);

        loadedCal.ShouldNotBeNull();
        File.Exists(foreignPath).ShouldBeTrue("another architecture's model belongs to another build and is left alone");
    }

    [Fact]
    public async Task GivenSaveWhenAnotherArchitectureFileExistsThenItSurvivesTheCleanup()
    {
        var ct = TestContext.Current.CancellationToken;

        var ngDir = _tempDir.CreateSubdirectory("NeuralGuider");
        var foreignPath = Path.Combine(ngDir.FullName, "EEEEEEEE.ngm");
        await File.WriteAllBytesAsync(foreignPath, OtherArchitectureFile(), ct);

        var model = new NeuralGuideModel();
        model.InitializeRandom(seed: 3);
        await NeuralGuideModelPersistence.SaveAsync(model, MakeCalibration(), _tempDir, ct);

        File.Exists(foreignPath).ShouldBeTrue();
        ngDir.GetFiles("*.ngm").Length.ShouldBe(2);
        ngDir.GetFiles("*.tmp").ShouldBeEmpty("the atomic write leaves no staging file behind");
    }

    [Fact]
    public void GivenTheSameCalibrationThenTheFileNameIsStableAcrossProcesses()
    {
        // HashCode.Combine is seeded per process, so a name built from it could not be pinned here at all.
        NeuralGuideModelPersistence.GetFileName(MakeCalibration()).ShouldBe(NeuralGuideModelPersistence.GetFileName(MakeCalibration()));
        NeuralGuideModelPersistence.GetFileName(MakeCalibration()).ShouldBe("7B71A07C.ngm");
    }

    /// <summary>A well-formed file at the CURRENT format version from a model with a different InputSize.</summary>
    private static byte[] OtherArchitectureFile()
    {
        const int otherInputSize = 10;
        const int otherTotalParams = (otherInputSize * 32 + 32) + (32 * 16 + 16) + (16 * 2 + 2);
        var buffer = new byte[20 + 56 + otherTotalParams * sizeof(float)];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(span, 0x4E47);
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], 0x0003);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], otherInputSize);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(span[12..], 16);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 2);
        return buffer;
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
        var cal2 = new GuiderCalibrationResult(0.2, 0.2 + Math.PI / 2.0, 6.0, 5.5, 18.0, 16.5, 8.0);
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
