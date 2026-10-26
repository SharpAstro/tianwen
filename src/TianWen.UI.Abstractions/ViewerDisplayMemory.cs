using System;
using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Geometry;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Abstractions;

/// <summary>
/// How a file was last shown (#1814): the display half of a <see cref="ViewerState"/>, which is
/// <see cref="ViewerState.ForAnnotatedExport"/>'s display set without its overlays and window, plus <see cref="ViewerState.DisplayCrop"/>.
/// Kept one JSON per file under AppData's <c>Viewer/</c>, named by a hash of the file's full path and stamped with its size and
/// modification time, so a rewritten file reads as none. Not carried: <c>WaveletDerived</c> and <c>WaveletLimb</c>, which belong to the
/// capture the limb was fitted on and cannot be written; a restored wavelet is its gains as dialled.
/// </summary>
public static class ViewerDisplayMemory
{
    /// <summary>The record the state holds now.</summary>
    public static ViewerDisplayRecord Capture(ViewerState state) => new()
    {
        StretchMode = state.StretchMode,
        StretchParameters = state.StretchParameters,
        ChannelView = state.ChannelView,
        DebayerAlgorithm = state.DebayerAlgorithm,
        ColorCalibrationEnabled = state.ColorCalibrationEnabled,
        BackgroundNeutralizationEnabled = state.BackgroundNeutralizationEnabled,
        BackgroundNeutralizationMethod = state.BackgroundNeutralizationMethod,
        BackgroundNeutralizationStrength = state.BackgroundNeutralizationStrength,
        BackgroundReduceEnabled = state.BackgroundReduceEnabled,
        BackgroundSkyLevel = state.BackgroundSkyLevel,
        WhiteBalanceR = state.ManualWhiteBalance.R,
        WhiteBalanceG = state.ManualWhiteBalance.G,
        WhiteBalanceB = state.ManualWhiteBalance.B,
        CurvesBoost = state.CurvesBoost,
        CurvesMode = state.CurvesMode,
        CurveData = state.CurveData,
        HdrAmount = state.HdrAmount,
        HdrKnee = state.HdrKnee,
        FinishContrast = state.FinishContrast,
        FinishSaturation = state.FinishSaturation,
        StarStretch = state.StarStretch,
        StarSaturation = state.StarSaturation,
        StarMethod = state.StarMethod,
        StarAmount = state.StarAmount,
        WaveletSharpenEnabled = state.WaveletSharpenEnabled,
        WaveletGains = state.WaveletGains,
        DisplayCrop = state.DisplayCrop,
    };

    /// <summary>Puts the record's settings on the state and asks for a redraw.</summary>
    public static void Apply(ViewerDisplayRecord record, ViewerState state)
    {
        state.StretchMode = record.StretchMode;
        state.StretchParameters = record.StretchParameters;
        state.ChannelView = record.ChannelView;
        state.DebayerAlgorithm = record.DebayerAlgorithm;
        state.ColorCalibrationEnabled = record.ColorCalibrationEnabled;
        state.BackgroundNeutralizationEnabled = record.BackgroundNeutralizationEnabled;
        state.BackgroundNeutralizationMethod = record.BackgroundNeutralizationMethod;
        state.BackgroundNeutralizationStrength = record.BackgroundNeutralizationStrength;
        state.BackgroundReduceEnabled = record.BackgroundReduceEnabled;
        state.BackgroundSkyLevel = record.BackgroundSkyLevel;
        state.ManualWhiteBalance = (record.WhiteBalanceR, record.WhiteBalanceG, record.WhiteBalanceB);
        state.CurvesBoost = record.CurvesBoost;
        state.CurvesMode = record.CurvesMode;
        state.CurveData = record.CurveData;
        state.HdrAmount = record.HdrAmount;
        state.HdrKnee = record.HdrKnee;
        state.FinishContrast = record.FinishContrast;
        state.FinishSaturation = record.FinishSaturation;
        state.StarStretch = record.StarStretch;
        state.StarSaturation = record.StarSaturation;
        state.StarMethod = record.StarMethod;
        state.StarAmount = record.StarAmount;
        state.WaveletSharpenEnabled = record.WaveletSharpenEnabled;
        state.WaveletGains = record.WaveletGains;
        state.DisplayCrop = record.DisplayCrop;
        state.NeedsRedraw = true;
        state.NeedsTextureUpdate = true;
    }

    /// <summary>Where a file's record is kept: <c>Viewer/&lt;hash of its full path&gt;.json</c>, the path compared ignoring case.</summary>
    public static string PathFor(IExternal external, string filePath)
        => Path.Combine(external.AppDataFolder.FullName, "Viewer", NormalizedHash(filePath) + ".json");

    /// <summary>The file's record, or null when none was saved or the file's size or modification time is not the one it was saved at.</summary>
    public static async Task<ViewerDisplayRecord?> LoadAsync(IExternal external, string filePath, CancellationToken cancellationToken)
    {
        if (StampOf(filePath) is not { } stamp
            || await external.TryReadJsonAsync(PathFor(external, filePath), ViewerDisplayMemoryJsonContext.Default.ViewerDisplayFileDto, logger: null, cancellationToken) is not { } saved)
        {
            return null;
        }

        return saved.SizeBytes == stamp.Size && saved.ModifiedUtcTicks == stamp.Ticks
            && string.Equals(saved.FilePath, Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase)
            ? saved.Display
            : null;
    }

    /// <summary>Saves the state's display for the file as it stands now; a file that is not there keeps nothing.</summary>
    public static Task SaveAsync(IExternal external, string filePath, ViewerState state, CancellationToken cancellationToken)
        => SaveAsync(external, filePath, Capture(state), cancellationToken);

    /// <summary>Saves an already captured record (taken on the render thread, written elsewhere) for the file as it stands now.</summary>
    public static Task SaveAsync(IExternal external, string filePath, ViewerDisplayRecord record, CancellationToken cancellationToken)
        => StampOf(filePath) is { } stamp
            ? external.AtomicWriteJsonAsync(PathFor(external, filePath),
                new ViewerDisplayFileDto(Path.GetFullPath(filePath), stamp.Size, stamp.Ticks, record),
                ViewerDisplayMemoryJsonContext.Default.ViewerDisplayFileDto, cancellationToken)
            : Task.CompletedTask;

    /// <summary>Forgets the file's record, so the next open shows its defaults.</summary>
    public static void Forget(IExternal external, string filePath)
    {
        try
        {
            File.Delete(PathFor(external, filePath));
        }
        catch (IOException)
        {
            // Held open by a reader for a moment; the record is stamped, so a stale one is worth little and the next Reset clears it.
        }
    }

    private static (long Size, long Ticks)? StampOf(string filePath)
        => new FileInfo(filePath) is { Exists: true } info ? (info.Length, info.LastWriteTimeUtc.Ticks) : null;

    private static string NormalizedHash(string filePath)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(filePath).ToUpperInvariant())), 0, 16);
}

/// <summary>
/// The display settings of one file. Enums go numeric; no nullable struct is the last property
/// (<c>JsonStreamReadTrapTests</c>), so the version ends it.
/// </summary>
public sealed record ViewerDisplayRecord
{
    public StretchMode StretchMode { get; init; }
    public StretchParameters StretchParameters { get; init; }
    public ChannelView ChannelView { get; init; }
    public DebayerAlgorithm DebayerAlgorithm { get; init; }
    public bool ColorCalibrationEnabled { get; init; }
    public bool BackgroundNeutralizationEnabled { get; init; }
    public BackgroundNeutralizationMethod BackgroundNeutralizationMethod { get; init; }
    public float BackgroundNeutralizationStrength { get; init; }
    public bool BackgroundReduceEnabled { get; init; }
    public float BackgroundSkyLevel { get; init; }
    public float WhiteBalanceR { get; init; }
    public float WhiteBalanceG { get; init; }
    public float WhiteBalanceB { get; init; }
    public float CurvesBoost { get; init; }
    public int CurvesMode { get; init; }
    public ImmutableArray<float> CurveData { get; init; }
    public float HdrAmount { get; init; }
    public float HdrKnee { get; init; }
    public float FinishContrast { get; init; }
    public float FinishSaturation { get; init; }
    public float StarStretch { get; init; }
    public float StarSaturation { get; init; }
    public StarStretchMethod StarMethod { get; init; }
    public float StarAmount { get; init; }
    public bool WaveletSharpenEnabled { get; init; }
    public ImmutableArray<float> WaveletGains { get; init; }
    public PixelRect? DisplayCrop { get; init; }
    public int Version { get; init; } = 1;
}

/// <summary>A file's record with what it was saved against: its full path, size and modification time (UTC ticks).</summary>
public sealed record ViewerDisplayFileDto(string FilePath, long SizeBytes, long ModifiedUtcTicks, ViewerDisplayRecord Display);

[JsonSerializable(typeof(ViewerDisplayFileDto))]
internal partial class ViewerDisplayMemoryJsonContext : JsonSerializerContext;
