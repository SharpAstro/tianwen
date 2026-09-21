using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using nom.tam.fits;
using nom.tam.util;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Geometry;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Dataset;
using TianWen.Lib.Imaging.Stacking;

namespace TianWen.AI.Imaging;

/// <summary>
/// What a bake's retained masters ARE, one entry per master, answered by the product's own rules and
/// handed out as data (<c>tianwen dataset masters</c>).
/// </summary>
/// <remarks>
/// <para><b>This exists so that nothing outside the product has to know how a master is named.</b>
/// The dataset gallery's row builder was a Python script that listed <c>session-masters/</c> itself,
/// and to do that it had to restate three rules the product owns: which <c>.fits</c> beside a master
/// is its coverage sidecar (<see cref="IntegrationFitsWriter.RejectionMapSuffix"/>), which suffix
/// marks one pier side of a flipped night (<see cref="ImagingSession.FlipSideKey"/> through
/// <see cref="DatasetTileExporter.Sanitize"/>), and how a stats record's session id becomes a file
/// name (<see cref="RetainedMasterStore.PathFor"/>). It also decided where a master's sky is by a
/// rule of its own. Each of those had already cost a wrong number once: the sidecar rule missing
/// counted 278 masters in a 139-master store, and a hand-rolled slug joined 92 rows to no stats at
/// all. Every one of them is answered here by the code that made the files.</para>
/// <para>The sky patch is <see cref="Image.FindBackgroundRegion"/>, the same square background
/// neutralisation measures, so a patch cut there is by construction the sky the render neutralised
/// and not a quiet corner of nebulosity. It is the one part of the inventory that reads pixels
/// (about a second per master); pass <c>skyPatchSize</c> null to skip it.</para>
/// </remarks>
public static class DatasetMasterInventory
{
    /// <summary>
    /// One retained master. Strings are the header's, trimmed; a missing card is an empty string,
    /// and a value the store does not carry (a session with no PSF record, a patch not asked for) is
    /// null rather than a guess.
    /// </summary>
    /// <param name="Name">The file stem, which is the sanitised session id.</param>
    /// <param name="Path">Absolute path of the master.</param>
    /// <param name="SessionId">The session id from the PSF store, when a record matched this file.</param>
    /// <param name="FlipSide"><c>"a"</c> / <c>"b"</c> for one pier side of a flipped night, <c>"both"</c>
    /// for the combined master of a night that was split, null for a night that never flipped.</param>
    /// <param name="FlipGroup">The stem shared by the three masters of a flipped night; null otherwise.</param>
    /// <param name="HasCoverageSidecar">Whether the coverage / rejection sidecar sits beside the master.</param>
    /// <param name="Solved">Whether the header carries a plate solution the product can read.</param>
    /// <param name="StackedFrames"><c>STACK_N</c>: subs that reached the master.</param>
    /// <param name="Lights">Subs the session's PSF record measured, when there is one.</param>
    /// <param name="MasterFwhm">The first fitted master profile's FWHM in pixels, when one fitted.</param>
    /// <param name="SkyPatchX">Left edge of the background square, when asked for.</param>
    /// <param name="SkyPatchY">Top edge of the background square, when asked for.</param>
    public sealed record Entry(
        string Name,
        string Path,
        string? SessionId,
        string? FlipSide,
        string? FlipGroup,
        bool HasCoverageSidecar,
        bool Solved,
        int Width,
        int Height,
        int Channels,
        string Object,
        string DateObs,
        string Camera,
        string Filter,
        double ExposureSeconds,
        string Strategy,
        int StackedFrames,
        int? Lights,
        string? OpticalTrain,
        float? MasterFwhm,
        int? SkyPatchX,
        int? SkyPatchY);

    /// <summary>The combined master of a night that was split, in <see cref="Entry.FlipSide"/>.</summary>
    public const string BothSides = "both";

    /// <summary>
    /// Lists every retained master in <paramref name="outDir"/>, in name order.
    /// </summary>
    /// <param name="outDir">The bake output root (the directory holding <c>session-masters/</c>).</param>
    /// <param name="skyPatchSize">Side of the sky square to locate on each master, or null to read no pixels.</param>
    public static async Task<ImmutableArray<Entry>> ListAsync(
        string outDir, int? skyPatchSize, ILogger? logger = null, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Ordinal, case-sensitive: the gallery numbers its cards by position in this list and its
        // pictures are named by that number, so the order is part of the contract and must not
        // depend on a culture or on case folding.
        var masters = new List<string>(RetainedMasterStore.EnumerateMasters(outDir));
        masters.Sort(static (a, b) => string.CompareOrdinal(
            System.IO.Path.GetFileNameWithoutExtension(a), System.IO.Path.GetFileNameWithoutExtension(b)));
        var stems = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in masters)
        {
            stems.Add(System.IO.Path.GetFileNameWithoutExtension(path));
        }

        // The PSF store keys on the session id and the file is named by RetainedMasterStore.PathFor,
        // so the join goes through PathFor and never through a re-derived slug.
        var byPath = new Dictionary<string, DatasetPsfNoiseReport.SessionPsf>(StringComparer.OrdinalIgnoreCase);
        var psfPath = System.IO.Path.Combine(outDir, "stats", DatasetPsfStore.FileName);
        if (File.Exists(psfPath))
        {
            var records = await DatasetPsfStore.ReadAsync(psfPath, logger, cancellationToken).ConfigureAwait(false);
            foreach (var (sessionId, record) in records)
            {
                byPath[RetainedMasterStore.PathFor(outDir, sessionId)] = record;
            }
        }

        var builder = ImmutableArray.CreateBuilder<Entry>(masters.Count);
        for (var i = 0; i < masters.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = masters[i];
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            var (side, group) = FlipSideOf(name, stems);
            byPath.TryGetValue(System.IO.Path.GetFullPath(path), out var record);
            if (record is null)
            {
                byPath.TryGetValue(path, out record);
            }

            var header = ReadHeader(path);
            // Either form: compressed, as they are written now, or plain, as every store written
            // before that holds them. File.Exists on the logical name alone stopped seeing them.
            var hasSidecar = IntegrationFitsWriter.ExistingSidecarPath(
                IntegrationFitsWriter.RejectionPathFor(path)) is not null;

            int? patchX = null, patchY = null;
            if (skyPatchSize is { } patch && Image.TryReadFitsFile(path, out var image))
            {
                (patchX, patchY) = SkyPatchOf(image, path, patch);
            }

            float? fwhm = null;
            if (record?.MasterProfiles is { } profiles)
            {
                foreach (var profile in profiles)
                {
                    if (profile is { } fitted && double.IsFinite(fitted.Fwhm))
                    {
                        fwhm = (float)fitted.Fwhm;
                        break;
                    }
                }
            }

            builder.Add(new Entry(
                name,
                System.IO.Path.GetFullPath(path),
                record?.SessionId,
                side,
                group,
                hasSidecar,
                // A solution, not a hint: FromHeader also answers for a bare RA/DEC or OBJCTRA/OBJCTDEC
                // pair (a centre with no CD matrix) and for a centre plus a pixel scale (a synthesised
                // CD, marked approximate). Neither is a plate solution.
                Solved: header is { } h && WCS.FromHeader(h) is { HasCDMatrix: true, IsApproximate: false },
                Width: header?.GetIntValue("NAXIS1", 0) ?? 0,
                Height: header?.GetIntValue("NAXIS2", 0) ?? 0,
                Channels: header?.GetIntValue("NAXIS3", 1) ?? 1,
                Object: Card(header, "OBJECT"),
                DateObs: Card(header, "DATE-OBS"),
                Camera: Card(header, "INSTRUME"),
                Filter: Card(header, "FILTER"),
                ExposureSeconds: header?.GetDoubleValue("EXPTIME", 0d) ?? 0d,
                Strategy: Card(header, "STRATEGY"),
                StackedFrames: header?.GetIntValue("STACK_N", 0) ?? 0,
                Lights: record?.SubFile?.Length,
                OpticalTrain: record?.OpticalTrain,
                MasterFwhm: fwhm,
                SkyPatchX: patchX,
                SkyPatchY: patchY));
            progress?.Report($"[masters] ({i + 1}/{masters.Count}) {name}");
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// Where a <paramref name="patch"/>-sized window of sky is on the master, in full-canvas pixels:
    /// the renderer's own darkest 32 px square, found where the renderer finds it, with the patch
    /// centred on it.
    /// </summary>
    /// <remarks>
    /// <para><b>The question has to be asked on the frame the render sees, which is the CROPPED one.</b>
    /// A retained master keeps its canvas ring, and <see cref="Image.FindBackgroundRegion"/>'s inner
    /// margin (5 percent) sits inside that ring on most of them; every square there fails the luma
    /// floor, nothing passes, and the fallback is the margin's corner. Asked on the uncropped master
    /// the first version of this answered (153, 153) for 3,000 px frames, which is a black patch of
    /// nothing, for every master in the store. The crop is the coverage plane's exact rectangle where
    /// the sidecar exists and the pixel tier's union otherwise, the same two tiers
    /// <c>ViewerActions.ScanForCrop</c> uses.</para>
    /// <para><b>And it is the renderer's 32 px square, not a 320 px one.</b> The scan steps four
    /// squares at a time, so a 320 px square visits three positions per axis on a 3,000 px frame and
    /// "darkest" means little; the render measures its background at 32 px, and a patch that shows
    /// that square with 144 px of context either side is what "the same sky the render neutralised"
    /// means.</para>
    /// </remarks>
    internal static (int X, int Y) SkyPatchOf(Image image, string path, int patch)
    {
        var covered = new PixelRect(0, 0, image.Width, image.Height);
        if (IntegrationFitsWriter.TryReadCoverageMap(path, out var coverage))
        {
            try
            {
                var exact = image.LargestCoveredRectangle(coverage);
                if (exact.Width > 0 && exact.Height > 0)
                {
                    covered = exact;
                }
            }
            finally
            {
                coverage.Release();
            }
        }
        else
        {
            var union = image.LargestCoveredRectangle();
            if (union.Width > 0 && union.Height > 0)
            {
                covered = union;
            }
        }

        var view = covered.Width < image.Width || covered.Height < image.Height ? image.Crop(covered) : image;
        var region = view.FindBackgroundRegion();
        var size = Math.Min(patch, Math.Min(view.Width, view.Height));
        var x = Math.Clamp(region.X + (region.Width / 2) - (size / 2), 0, view.Width - size);
        var y = Math.Clamp(region.Y + (region.Height / 2) - (size / 2), 0, view.Height - size);
        return (covered.X + x, covered.Y + y);
    }

    /// <summary>
    /// Which of a flipped night's three masters this is, from the file name alone, through the same
    /// sanitiser that made the name: a side carries <c>|flip=a</c> in its session id, the combined
    /// master is the same id without it, so "there is a sibling called this plus the side suffix" is
    /// what identifies the combined one and nothing has to parse a session id.
    /// </summary>
    internal static (string? Side, string? Group) FlipSideOf(string stem, IReadOnlySet<string> stems)
    {
        foreach (var side in new[] { "a", "b" })
        {
            var suffix = DatasetTileExporter.Sanitize($"|{ImagingSession.FlipSideKey}={side}");
            if (stem.EndsWith(suffix, StringComparison.Ordinal))
            {
                return (side, stem[..^suffix.Length]);
            }
        }

        var sideA = stem + DatasetTileExporter.Sanitize($"|{ImagingSession.FlipSideKey}=a");
        return stems.Contains(sideA) ? (BothSides, stem) : (null, null);
    }

    /// <summary>Serialises the inventory as a JSON array, AOT-safe through the source-generated context.</summary>
    public static async Task WriteJsonAsync(ImmutableArray<Entry> entries, Stream destination, CancellationToken cancellationToken = default)
        => await JsonSerializer.SerializeAsync(destination, entries, DatasetMasterInventoryJsonContext.Default.ImmutableArrayEntry, cancellationToken)
            .ConfigureAwait(false);

    private static Header? ReadHeader(string path)
    {
        try
        {
            using var fitsFile = Image.OpenFits(path);
            return fitsFile.ReadFirstImageHduHeaderOnly()?.Header;
        }
        catch (Exception ex) when (ex is IOException or FitsException)
        {
            return null;
        }
    }

    private static string Card(Header? header, string key)
        => header?.GetStringValue(key)?.Trim() ?? "";
}

[JsonSerializable(typeof(ImmutableArray<DatasetMasterInventory.Entry>))]
[JsonSourceGenerationOptions(WriteIndented = false)]
internal partial class DatasetMasterInventoryJsonContext : JsonSerializerContext;
