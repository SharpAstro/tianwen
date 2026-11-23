using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging.Enhancement;

namespace TianWen.Lib.Imaging.Workbench;

/// <summary>
/// What an operation is handed to work on: a file, or the result of a derived operation that has no file.
/// </summary>
/// <remarks>
/// <para>A verb hands its operation files. In an edit list a stored step's result is a file too, so the step after it
/// reads that file (the plan's rule 2). Only a derived step (a crop) has none: its result is made when the step after it
/// asks, by <see cref="TryRead"/>, and is never written.</para>
/// <para>Each <see cref="TryRead"/> reads or derives again and the caller owns what it gets, so an operation reads an
/// input once.</para>
/// </remarks>
public sealed class OperationInput
{
    /// <summary>Reads or derives the image; false with the reason in words when there is none.</summary>
    public delegate bool Reader([NotNullWhen(true)] out Image? image, out WCS? wcs, [NotNullWhen(false)] out string? refusal);

    private readonly Reader _read;

    private OperationInput(string name, string? path, Reader read)
    {
        Name = name;
        Path = path;
        _read = read;
    }

    /// <summary>What a message calls this input: a file's name, or what a derived image was made from.</summary>
    public string Name { get; }

    /// <summary>The file, when there is one. An operation that carries what is beside or inside its input's file (a
    /// crop: the alignment cards, the map sidecars) can do so only then.</summary>
    public string? Path { get; }

    /// <summary>The image in <paramref name="path"/>, read when asked for.</summary>
    public static OperationInput FromFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new OperationInput(System.IO.Path.GetFileName(path), path, (out Image? image, out WCS? wcs, out string? refusal) =>
        {
            if (Image.TryReadFitsFile(path, out image, out wcs))
            {
                refusal = null;
                return true;
            }

            refusal = $"Failed to read FITS file: {path}";
            return false;
        });
    }

    /// <summary>The result of a derived operation, made by <paramref name="derive"/> each time it is asked for.</summary>
    public static OperationInput FromDerivation(string name, Reader derive)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(derive);
        return new OperationInput(name, null, derive);
    }

    /// <summary>
    /// The result of a derived operation that changes no pixel, so what sits beside <paramref name="path"/> (the map
    /// sidecars a crop reads and carries) still describes it: the derived input keeps the file's path.
    /// </summary>
    public static OperationInput FromDerivation(string name, string? path, Reader derive)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(derive);
        return new OperationInput(name, path, derive);
    }

    /// <summary>The image and its plate solution as they are in the file (or as derived); false with the reason in
    /// words when it cannot be had.</summary>
    public bool TryRead([NotNullWhen(true)] out Image? image, out WCS? wcs, [NotNullWhen(false)] out string? refusal)
        => _read(out image, out wcs, out refusal);

    /// <summary>
    /// The image read as a master: its absent pixels made absent (<see cref="MasterAlignment.MaskAbsent"/>: a master's
    /// uncovered ring is exact zero), which is how every step of a colour composition reads one.
    /// </summary>
    public bool TryReadMaster([NotNullWhen(true)] out Image? master, out WCS? wcs, [NotNullWhen(false)] out string? refusal)
    {
        if (!TryRead(out var image, out wcs, out refusal))
        {
            master = null;
            return false;
        }

        master = MasterAlignment.MaskAbsent(image);
        image.Release();
        return true;
    }
}

/// <summary>How an operation reads several images that have to be on one grid.</summary>
public static class OperationInputs
{
    /// <summary>What an operation says when its images are not on one grid.</summary>
    public const string NotOnOneGrid = "the masters are not on one grid: put them there first (tianwen image align).";

    /// <summary>
    /// Every input read as a master (<see cref="OperationInput.TryReadMaster"/>), in order, with the first plate
    /// solution among them; false with the reason when one will not read or they are not on one grid.
    /// </summary>
    public static bool TryReadMasters(IReadOnlyList<OperationInput> inputs, [NotNullWhen(true)] out Image[]? masters, out WCS? wcs,
        [NotNullWhen(false)] out string? refusal)
    {
        masters = null;
        wcs = null;
        var read = new Image[inputs.Count];
        for (var i = 0; i < inputs.Count; i++)
        {
            if (!inputs[i].TryReadMaster(out var master, out var masterWcs, out refusal))
            {
                return false;
            }

            read[i] = master;
            wcs ??= masterWcs;
        }

        if (!OnOneGrid(read))
        {
            refusal = NotOnOneGrid;
            return false;
        }

        masters = read;
        refusal = null;
        return true;
    }

    /// <summary>True when every image has the first one's width and height.</summary>
    public static bool OnOneGrid(params ReadOnlySpan<Image> images)
    {
        foreach (var image in images)
        {
            if (image.Width != images[0].Width || image.Height != images[0].Height)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>How an operation writes its result.</summary>
public static class OperationOutput
{
    /// <summary>
    /// Writes a step's result as every step writes one: on the image's own scale, never divided by its own peak, with
    /// the plate solution and the <c>SWMODIFY</c> card (<see cref="SharpenPipeline.SwModifyHeader"/>). Two files a step
    /// wrote still relate as their inputs did.
    /// </summary>
    public static void Write(Image image, string path, WCS? wcs)
        => image.WriteToFitsFile(path, wcs, SharpenPipeline.SwModifyHeader());
}
