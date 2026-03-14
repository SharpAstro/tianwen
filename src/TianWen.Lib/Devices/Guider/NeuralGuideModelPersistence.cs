using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices.Guider;

/// <summary>
/// Persists neural guide model weights alongside the calibration result
/// that was active when the model was trained. Keyed by optical train identity.
/// </summary>
/// <remarks>
/// File format (little-endian):
///   [0..1]  Magic: 0x4E47 ('NG')
///   [2..3]  Version: 0x0001
///   [4..51] CalibrationResult: 6 doubles (CameraAngleRad, RaRate, DecRate, RaDisp, DecDisp, TotalTime)
///   [52..1723] Model weights: 418 floats
///   Total: 1724 bytes
/// </remarks>
internal static class NeuralGuideModelPersistence
{
    private const ushort Magic = 0x4E47;
    private const ushort Version = 0x0001;
    private const int HeaderSize = 4; // magic + version
    private const int CalibrationSize = 6 * sizeof(double); // 48 bytes
    private const int WeightsSize = NeuralGuideModel.TotalParams * sizeof(float); // 1672 bytes
    private const int TotalFileSize = HeaderSize + CalibrationSize + WeightsSize; // 1724 bytes

    private const string SubDirectory = "NeuralGuider";

    /// <summary>
    /// Saves the model weights and calibration to disk.
    /// </summary>
    public static async ValueTask SaveAsync(
        NeuralGuideModel model,
        GuiderCalibrationResult calibration,
        DirectoryInfo profileFolder,
        CancellationToken cancellationToken)
    {
        var dir = profileFolder.CreateSubdirectory(SubDirectory);
        var filePath = Path.Combine(dir.FullName, GetFileName(calibration));

        var buffer = new byte[TotalFileSize];
        var span = buffer.AsSpan();

        // Header
        BinaryPrimitives.WriteUInt16LittleEndian(span, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], Version);

        // Calibration (6 doubles)
        var calSpan = span[HeaderSize..];
        BinaryPrimitives.WriteDoubleLittleEndian(calSpan, calibration.CameraAngleRad);
        BinaryPrimitives.WriteDoubleLittleEndian(calSpan[8..], calibration.RaRatePixPerSec);
        BinaryPrimitives.WriteDoubleLittleEndian(calSpan[16..], calibration.DecRatePixPerSec);
        BinaryPrimitives.WriteDoubleLittleEndian(calSpan[24..], calibration.RaDisplacementPx);
        BinaryPrimitives.WriteDoubleLittleEndian(calSpan[32..], calibration.DecDisplacementPx);
        BinaryPrimitives.WriteDoubleLittleEndian(calSpan[40..], calibration.TotalCalibrationTimeSec);

        // Model weights
        var weights = model.ExportParameters();
        var weightSpan = span[(HeaderSize + CalibrationSize)..];
        for (var i = 0; i < weights.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(weightSpan[(i * sizeof(float))..], weights[i]);
        }

        await File.WriteAllBytesAsync(filePath, buffer, cancellationToken);
    }

    /// <summary>
    /// Attempts to load saved model weights and calibration from disk.
    /// </summary>
    /// <returns>The loaded calibration result, or null if no saved state was found or the file was invalid.</returns>
    public static async ValueTask<GuiderCalibrationResult?> TryLoadAsync(
        NeuralGuideModel model,
        DirectoryInfo profileFolder,
        CancellationToken cancellationToken)
    {
        var dir = new DirectoryInfo(Path.Combine(profileFolder.FullName, SubDirectory));
        if (!dir.Exists)
        {
            return null;
        }

        // Find the most recently written file
        FileInfo? newest = null;
        foreach (var file in dir.GetFiles("*.ngm"))
        {
            if (newest is null || file.LastWriteTimeUtc > newest.LastWriteTimeUtc)
            {
                newest = file;
            }
        }

        if (newest is null)
        {
            return null;
        }

        var buffer = await File.ReadAllBytesAsync(newest.FullName, cancellationToken);
        if (buffer.Length != TotalFileSize)
        {
            return null;
        }

        var span = buffer.AsSpan();

        // Validate header
        var magic = BinaryPrimitives.ReadUInt16LittleEndian(span);
        var version = BinaryPrimitives.ReadUInt16LittleEndian(span[2..]);
        if (magic != Magic || version != Version)
        {
            return null;
        }

        // Read calibration
        var calSpan = span[HeaderSize..];
        var calibration = new GuiderCalibrationResult(
            CameraAngleRad: BinaryPrimitives.ReadDoubleLittleEndian(calSpan),
            RaRatePixPerSec: BinaryPrimitives.ReadDoubleLittleEndian(calSpan[8..]),
            DecRatePixPerSec: BinaryPrimitives.ReadDoubleLittleEndian(calSpan[16..]),
            RaDisplacementPx: BinaryPrimitives.ReadDoubleLittleEndian(calSpan[24..]),
            DecDisplacementPx: BinaryPrimitives.ReadDoubleLittleEndian(calSpan[32..]),
            TotalCalibrationTimeSec: BinaryPrimitives.ReadDoubleLittleEndian(calSpan[40..]));

        // Read weights
        var weightSpan = span[(HeaderSize + CalibrationSize)..];
        var weights = new float[NeuralGuideModel.TotalParams];
        for (var i = 0; i < weights.Length; i++)
        {
            weights[i] = BinaryPrimitives.ReadSingleLittleEndian(weightSpan[(i * sizeof(float))..]);
        }

        model.LoadParameters(weights);
        return calibration;
    }

    /// <summary>
    /// Generates a file name based on the calibration's key properties.
    /// Uses camera angle and guide rates rounded to 2 decimals.
    /// </summary>
    private static string GetFileName(GuiderCalibrationResult calibration)
    {
        var hash = HashCode.Combine(
            Math.Round(calibration.CameraAngleRad, 2),
            Math.Round(calibration.RaRatePixPerSec, 2),
            Math.Round(calibration.DecRatePixPerSec, 2));
        return $"{hash:X8}.ngm";
    }
}
