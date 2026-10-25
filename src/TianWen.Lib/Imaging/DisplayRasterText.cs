using SharpAstro.Exif;
using SharpAstro.Jpeg;
using SharpAstro.Png;
using System;
using System.Collections.Generic;

namespace TianWen.Lib.Imaging;

/// <summary>
/// What a display raster says about itself, in the form its container carries: PNG iTXt chunks, or a JPEG's EXIF and XMP.
/// A TIFF carries none of it.
/// </summary>
/// <param name="Software">The program that wrote the file.</param>
/// <param name="Source">The file name of the image it was rendered from, if it has one.</param>
/// <param name="Rendition">How it was rendered, as JSON.</param>
public sealed record DisplayRasterText(string Software, string? Source, string Rendition)
{
    /// <summary>The PNG keyword <see cref="Rendition"/> is written under.</summary>
    public const string RenditionKeyword = "TianWen:Rendition";

    /// <summary>The XMP namespace <see cref="Rendition"/> is written in, as <c>tianwen:Rendition</c>.</summary>
    public const string XmpNamespaceUri = "https://github.com/SharpAstro/tianwen/ns/1.0/";

    /// <summary>The PNG text chunks: <c>Software</c>, <c>Source</c> and <see cref="RenditionKeyword"/>.</summary>
    public IReadOnlyList<PngText> ToPng()
    {
        var text = new List<PngText>(3) { new("Software", Software) };
        if (Source is { Length: > 0 } source)
        {
            text.Add(new("Source", source));
        }
        text.Add(new(RenditionKeyword, Rendition));
        return text;
    }

    /// <summary>
    /// The JPEG's EXIF blob: Software, the source as ImageDescription, the rendition as UserComment. EXIF's IFD0 strings
    /// are 7-bit ASCII, so a field that is not is left out there (the XMP carries every one of them as UTF-8).
    /// </summary>
    /// <remarks>A <see cref="ReadOnlyMemory{T}"/> and never a <c>byte[]</c>, so that <c>text?.ToExif()</c> is a null
    /// <see cref="JpegEncodeOptions.Exif"/>: a null array converts to an EMPTY memory, which the encoder refuses.</remarks>
    public ReadOnlyMemory<byte> ToExif() => ExifWriter.Build(new ExifWriteData(
        Software: IsAscii(Software) ? Software : null,
        ImageDescription: Source is { } source && IsAscii(source) ? source : null,
        UserComment: Rendition));

    /// <summary>The JPEG's XMP: <c>xmp:CreatorTool</c>, <c>dc:description</c> for the source and <c>tianwen:Rendition</c>.</summary>
    public JpegXmp ToXmp() => new()
    {
        CreatorTool = Software,
        Description = Source,
        CustomNamespaceUri = XmpNamespaceUri,
        CustomPrefix = "tianwen",
        CustomProperties = [new("Rendition", Rendition)],
    };

    private static bool IsAscii(string s)
    {
        foreach (var c in s)
        {
            if (c is < ' ' or > '~')
            {
                return false;
            }
        }
        return true;
    }
}
