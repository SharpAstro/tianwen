using System;

namespace TianWen.Lib.Astrometry.Catalogs;

/// <summary>
/// The English Wikipedia article about a catalogued object, as the <c>tools/bake-object-imagery</c> bake
/// VERIFIED it: a candidate found by catalogue code or by title, accepted only because the Wikidata item's
/// right ascension and declination agree with the catalogue's. Never a guess from a designation string;
/// an object the bake could not verify has no article at all. See <c>docs/plans/object-imagery.md</c>.
/// </summary>
/// <param name="WikidataItem">The Wikidata item number, the digits of <c>Q13903</c>.</param>
/// <param name="Title">The article title as English Wikipedia spells it, spaces and all.</param>
/// <param name="Image">The article's lead image, or null when it has none the bake would keep.</param>
public readonly record struct ObjectArticle(uint WikidataItem, string Title, ObjectArticleImage? Image)
{
    private const string ArticleBase = "https://en.wikipedia.org/wiki/";

    /// <summary>
    /// The article URL. Spaces map to '_' (the MediaWiki title convention) and the rest is
    /// percent-encoded, which MediaWiki decodes, so a title carrying '/', '(' or an en dash still links.
    /// </summary>
    public string Url => ArticleBase + Uri.EscapeDataString(Title.Replace(' ', '_'));
}

/// <summary>
/// An article's lead image: a pointer and its credit, never pixels. Licences differ per file and the
/// files change upstream, so the client fetches the picture itself, at a standard thumbnail width
/// (Wikimedia refuses arbitrary ones).
/// </summary>
/// <param name="FileName">The Commons file name without the <c>File:</c> prefix, spaces and all.</param>
/// <param name="Licence">Commons' <c>LicenseShortName</c>, e.g. <c>CC BY 4.0</c> or <c>Public domain</c>.</param>
/// <param name="Artist">Commons' <c>Artist</c> as plain text (the HTML stripped); may be empty.</param>
/// <param name="Credit">Commons' <c>Credit</c> as plain text; may be empty.</param>
/// <param name="AttributionRequired">Whether the licence requires a credit where the image is shown.</param>
/// <param name="Width">Full-size width in pixels.</param>
/// <param name="Height">Full-size height in pixels.</param>
public readonly record struct ObjectArticleImage(
    string FileName,
    string Licence,
    string Artist,
    string Credit,
    bool AttributionRequired,
    int Width,
    int Height)
{
    private const string FilePageBase = "https://commons.wikimedia.org/wiki/File:";

    /// <summary>The Commons file page, which is where a credit line links: it carries the full licence.</summary>
    public string FilePageUrl => FilePageBase + Uri.EscapeDataString(FileName.Replace(' ', '_'));
}
