using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Hashing;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.IO;

namespace TianWen.Lib.Devices.Guider;

/// <summary>
/// Persists neural guide model weights alongside the calibration result
/// that was active when the model was trained. Keyed by optical train identity.
/// </summary>
/// <remarks>
/// File format (little-endian):
///   [0..1]   Magic: 0x4E47 ('NG')
///   [2..3]   Version: 0x0003
///   [4..7]   InputSize (int32)
///   [8..11]  Hidden1Size (int32)
///   [12..15] Hidden2Size (int32)
///   [16..19] OutputSize (int32)
///   [20..75] CalibrationResult: 7 doubles (CameraAngleRad, RaRate, DecRate, RaDisp, DecDisp, TotalTime, DecAngleRad)
///   [76..]   Model weights: TotalParams floats
///   Total: 20 + 56 + TotalParams*4 bytes
///
/// A save is atomic (staged under a name of its own and renamed over the target), and TryLoadAsync
/// walks the files newest first and takes the first that passes every gate, so a file a crash cut
/// short never hides an older valid one (#786). A file useless to every build (truncated, bad magic,
/// an older format version) is DELETED so it is not re-read; a file from another architecture or a
/// newer format version is LEFT, since another build of TianWen sharing the profile can still load it.
/// The gates:
///   * Architecture dimensions -- the four size ints must equal the current model constants.
///     (The InputSize 22 -> 26 bump that added the encoder-phase features is caught here:
///     a 1,298-param file no longer matches the current 1,426-param model.)
///   * Format version -- bumped 0x0001 -> 0x0002 by the meridian-side calibration fix
///     (commit 173e3b4). That fix made guider calibration learn the Dec guide sense EAST of
///     the meridian, on the pre-flip pier side. A model trained online under the old (west,
///     possibly inverted-Dec) calibration has that sense baked into its weights; the runtime
///     meridian-flip handler can negate the *calibration* but not the model's learned output.
///     So every pre-fix model is discarded and retrained under the corrected calibration.
///   * v2 -> v3 adds the measured Dec-axis angle (DecAngleRad) so calibration carries the Dec
///     sense / non-orthogonality from the measurement instead of assuming RA + 90deg. The Dec
///     sense changed for flipped-sensor configs (southern hemisphere), so v2 models are discarded.
///   * Length -- checked LAST, once the header says the file is this build's, because another
///     architecture's file has another length by construction and is not a truncated one.
/// When no file passes, the model is left untouched and re-initialised with fresh weights.
/// </remarks>
internal static class NeuralGuideModelPersistence
{
    private const ushort Magic = 0x4E47;
    // v2: the meridian-side calibration fix (173e3b4) changed the learned Dec guide sense.
    // v3: the measured Dec-axis angle was added (2-axis transform), again changing the Dec sense
    // for flipped-sensor configs -- every model trained under an earlier version is invalidated.
    private const ushort Version = 0x0003;
    private const int HeaderSize = 4 + 16; // magic(2) + version(2) + 4 ints(16)
    private const int CalibrationSize = 7 * sizeof(double); // 56 bytes
    private const int WeightsSize = NeuralGuideModel.TotalParams * sizeof(float);
    private const int TotalFileSize = HeaderSize + CalibrationSize + WeightsSize;

    private const string SubDirectory = "NeuralGuider";

    private enum FileVerdict
    {
        /// <summary>This build's model, whole.</summary>
        Usable,
        /// <summary>Useless to every build: truncated, not a model, or a superseded format.</summary>
        Discard,
        /// <summary>Another architecture or a newer format: another build's model, not this one's to delete.</summary>
        OtherBuild
    }

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

        // Header: magic + version + architecture dims
        BinaryPrimitives.WriteUInt16LittleEndian(span, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], Version);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], NeuralGuideModel.InputSize);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], NeuralGuideModel.Hidden1Size);
        BinaryPrimitives.WriteInt32LittleEndian(span[12..], NeuralGuideModel.Hidden2Size);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], NeuralGuideModel.OutputSize);

        // Calibration (7 doubles)
        var calSpan = span[HeaderSize..];
        BinaryPrimitives.WriteDoubleLittleEndian(calSpan, calibration.CameraAngleRad);
        BinaryPrimitives.WriteDoubleLittleEndian(calSpan[8..], calibration.RaRatePixPerSec);
        BinaryPrimitives.WriteDoubleLittleEndian(calSpan[16..], calibration.DecRatePixPerSec);
        BinaryPrimitives.WriteDoubleLittleEndian(calSpan[24..], calibration.RaDisplacementPx);
        BinaryPrimitives.WriteDoubleLittleEndian(calSpan[32..], calibration.DecDisplacementPx);
        BinaryPrimitives.WriteDoubleLittleEndian(calSpan[40..], calibration.TotalCalibrationTimeSec);
        BinaryPrimitives.WriteDoubleLittleEndian(calSpan[48..], calibration.DecAngleRad);

        // Model weights
        var weights = model.ExportParameters();
        var weightSpan = span[(HeaderSize + CalibrationSize)..];
        for (var i = 0; i < weights.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(weightSpan[(i * sizeof(float))..], weights[i]);
        }

        // Staged and renamed over the target: a crash or power loss mid-write leaves the previous
        // model, never the first part of this one (#786).
        await SharedFile.WriteAsync(filePath, (stream, ct) => stream.WriteAsync(buffer, ct).AsTask(), cancellationToken);

        // Clean up superseded weight files: keep the one just written, and any another build can still use.
        foreach (var oldFile in dir.GetFiles("*.ngm"))
        {
            if (!string.Equals(oldFile.FullName, filePath, StringComparison.OrdinalIgnoreCase)
                && await ClassifyAsync(oldFile, cancellationToken) is not FileVerdict.OtherBuild)
            {
                try { oldFile.Delete(); } catch { /* ignore cleanup failures */ }
            }
        }
    }

    /// <summary>
    /// Attempts to load saved model weights and calibration from disk, trying the files newest first
    /// and taking the first that passes every compatibility gate.
    /// </summary>
    /// <returns>The loaded calibration result, or null if no usable saved state was found.</returns>
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

        var files = dir.GetFiles("*.ngm");
        Array.Sort(files, static (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));

        foreach (var file in files)
        {
            byte[] buffer;
            try
            {
                await using var stream = await SharedFile.TryOpenReadAsync(file.FullName, cancellationToken);
                if (stream is null)
                {
                    continue;
                }
                buffer = new byte[stream.Length];
                await stream.ReadExactlyAsync(buffer, cancellationToken);
            }
            catch (IOException)
            {
                // Unreadable right now: try the next one, and leave this one rather than guess.
                continue;
            }

            switch (Classify(buffer, buffer.Length))
            {
                case FileVerdict.Usable:
                    var calibration = ReadCalibration(buffer);
                    model.LoadParameters(ReadWeights(buffer));
                    return calibration;

                case FileVerdict.Discard:
                    // Delete it so it is not re-read on the next load, then go on to the next newest,
                    // which a failed write left intact.
                    try { file.Delete(); } catch { /* ignore cleanup failures */ }
                    break;

                case FileVerdict.OtherBuild:
                    break;
            }
        }

        return null;
    }

    /// <summary>
    /// Judges a file from its header and total length. The version and architecture are read before
    /// the length, since another build's file has another length by construction and must not be
    /// taken for a truncated one.
    /// </summary>
    private static FileVerdict Classify(ReadOnlySpan<byte> header, long length)
    {
        if (header.Length < HeaderSize || BinaryPrimitives.ReadUInt16LittleEndian(header) != Magic)
        {
            return FileVerdict.Discard;
        }

        var version = BinaryPrimitives.ReadUInt16LittleEndian(header[2..]);
        if (version < Version)
        {
            return FileVerdict.Discard;
        }

        if (version > Version
            || BinaryPrimitives.ReadInt32LittleEndian(header[4..]) != NeuralGuideModel.InputSize
            || BinaryPrimitives.ReadInt32LittleEndian(header[8..]) != NeuralGuideModel.Hidden1Size
            || BinaryPrimitives.ReadInt32LittleEndian(header[12..]) != NeuralGuideModel.Hidden2Size
            || BinaryPrimitives.ReadInt32LittleEndian(header[16..]) != NeuralGuideModel.OutputSize)
        {
            return FileVerdict.OtherBuild;
        }

        return length == TotalFileSize ? FileVerdict.Usable : FileVerdict.Discard;
    }

    private static async ValueTask<FileVerdict> ClassifyAsync(FileInfo file, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await SharedFile.TryOpenReadAsync(file.FullName, cancellationToken);
            if (stream is null)
            {
                return FileVerdict.OtherBuild; // gone already, nothing to delete
            }

            var header = new byte[HeaderSize];
            var read = await stream.ReadAtLeastAsync(header, HeaderSize, throwOnEndOfStream: false, cancellationToken);
            return Classify(header.AsSpan(0, read), stream.Length);
        }
        catch (IOException)
        {
            return FileVerdict.OtherBuild; // unreadable right now: leave it rather than guess
        }
    }

    private static GuiderCalibrationResult ReadCalibration(ReadOnlySpan<byte> span)
    {
        var calSpan = span[HeaderSize..];
        return new GuiderCalibrationResult(
            CameraAngleRad: BinaryPrimitives.ReadDoubleLittleEndian(calSpan),
            DecAngleRad: BinaryPrimitives.ReadDoubleLittleEndian(calSpan[48..]),
            RaRatePixPerSec: BinaryPrimitives.ReadDoubleLittleEndian(calSpan[8..]),
            DecRatePixPerSec: BinaryPrimitives.ReadDoubleLittleEndian(calSpan[16..]),
            RaDisplacementPx: BinaryPrimitives.ReadDoubleLittleEndian(calSpan[24..]),
            DecDisplacementPx: BinaryPrimitives.ReadDoubleLittleEndian(calSpan[32..]),
            TotalCalibrationTimeSec: BinaryPrimitives.ReadDoubleLittleEndian(calSpan[40..]));
    }

    private static float[] ReadWeights(ReadOnlySpan<byte> span)
    {
        var weightSpan = span[(HeaderSize + CalibrationSize)..];
        var weights = new float[NeuralGuideModel.TotalParams];
        for (var i = 0; i < weights.Length; i++)
        {
            weights[i] = BinaryPrimitives.ReadSingleLittleEndian(weightSpan[(i * sizeof(float))..]);
        }
        return weights;
    }

    /// <summary>
    /// Generates a file name from the calibration's key properties (camera angle and guide rates
    /// rounded to 2 decimals), hashed with XxHash32 so the same calibration gets the same name in
    /// every process (<see cref="HashCode"/> is seeded per process). The name is not a lookup key --
    /// the loader reads by write time -- but a stable one means a re-save replaces its own file.
    /// </summary>
    internal static string GetFileName(GuiderCalibrationResult calibration)
    {
        Span<byte> key = stackalloc byte[3 * sizeof(double)];
        BinaryPrimitives.WriteDoubleLittleEndian(key, Math.Round(calibration.CameraAngleRad, 2));
        BinaryPrimitives.WriteDoubleLittleEndian(key[8..], Math.Round(calibration.RaRatePixPerSec, 2));
        BinaryPrimitives.WriteDoubleLittleEndian(key[16..], Math.Round(calibration.DecRatePixPerSec, 2));
        return $"{XxHash32.HashToUInt32(key):X8}.ngm";
    }
}
