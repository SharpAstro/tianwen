using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using TianWen.Lib.IO;

namespace TianWen.Lib.Astrometry.Catalogs;

/// <summary>
/// One verified article and every catalogue index the bake resolved to it. Our catalogue lists one
/// object under several indices (the Carina Nebula is NGC 3372, C92, GUM 33 and RCW 53), so the table
/// stores each article once and names its indices beside it.
/// </summary>
internal readonly record struct ObjectArticleRow(ObjectArticle Article, ImmutableArray<CatalogIndex> Indices);

/// <summary>
/// Reads and writes <c>object_articles.gs.gz</c>, the table <c>tools/bake-object-imagery</c> produces:
/// the ASCII-separated format every other <c>.gs.gz</c> catalogue uses (<see cref="AsciiRecordReader"/>),
/// gzipped.
/// </summary>
/// <remarks>
/// <para>The first record is a header, <c>TianWenObjectArticles</c> and the schema version. Each record
/// after it is one article, fields in this order: Wikidata item number, title, the catalogue indices
/// (raw <see cref="CatalogIndex"/> values, decimal, unit-separated), then the lead image's file name, hash
/// prefix, licence, artist, credit, attribution flag (<c>1</c> or <c>0</c>), width and height. An article
/// with no image leaves all eight image fields empty.</para>
/// <para>Indices are stored as their raw values, as the other catalogue snapshots store them, so a change
/// to the <see cref="CatalogIndex"/> encoding invalidates this table exactly as it does those.</para>
/// </remarks>
internal static class ObjectArticleTable
{
    public const string ResourceSuffix = ".object_articles.gs.gz";

    private const string Magic = "TianWenObjectArticles";
    private const int SchemaVersion = 2;

    /// <summary>
    /// Reads the table, or answers false when its header names another schema or it has none, or when the
    /// bytes are not gzip at all: a table this build cannot read, which a consumer to whom the table is an
    /// enrichment simply goes without. The header is checked before any record is parsed; a table whose
    /// header matches but whose records do not is corrupt, and that still throws.
    /// </summary>
    /// <remarks>
    /// A TRUNCATED stream is not refused here, because it cannot be told apart: .NET's gzip decoder ends a
    /// cut-short stream silently (measured; no trailer, no CRC, no exception), so the records before the cut
    /// read as a whole table and only the record the cut fell inside can fail, and only when it tore a field
    /// the parser needs (a torn number still parses, as a different number). Telling would take a row count
    /// in the header, a schema bump and a re-bake; the consumer contains the failure instead.
    /// </remarks>
    public static bool TryRead(Stream gzipped, out FrozenDictionary<CatalogIndex, ObjectArticle> table)
    {
        using var decompressed = new MemoryStream();
        try
        {
            using var gz = new GZipStream(gzipped, CompressionMode.Decompress, leaveOpen: true);
            gz.CopyTo(decompressed);
        }
        catch (InvalidDataException)
        {
            // Not gzip: the header cannot even be looked at. The same answer as a header this build does not
            // know.
            table = FrozenDictionary<CatalogIndex, ObjectArticle>.Empty;
            return false;
        }

        var payload = new ReadOnlyMemory<byte>(decompressed.GetBuffer(), 0, (int)decompressed.Length);
        var byIndex = new Dictionary<CatalogIndex, ObjectArticle>();
        var first = true;

        foreach (var recordMemory in AsciiRecordReader.EnumerateRecords(payload))
        {
            var record = recordMemory.Span;
            if (first)
            {
                first = false;
                var magic = AsciiRecordReader.ReadString(AsciiRecordReader.TakeField(ref record));
                var version = AsciiRecordReader.ReadString(AsciiRecordReader.TakeField(ref record));
                if (magic != Magic || version != SchemaVersion.ToString(CultureInfo.InvariantCulture))
                {
                    table = FrozenDictionary<CatalogIndex, ObjectArticle>.Empty;
                    return false;
                }
                continue;
            }

            if (record.IsEmpty)
            {
                continue;
            }

            var item = uint.Parse(AsciiRecordReader.TakeField(ref record), NumberStyles.None, CultureInfo.InvariantCulture);
            var title = AsciiRecordReader.ReadString(AsciiRecordReader.TakeField(ref record));
            var indices = AsciiRecordReader.ReadStringArray(AsciiRecordReader.TakeField(ref record));
            var fileName = AsciiRecordReader.ReadString(AsciiRecordReader.TakeField(ref record));
            var hashPrefix = AsciiRecordReader.ReadString(AsciiRecordReader.TakeField(ref record));
            var licence = AsciiRecordReader.ReadString(AsciiRecordReader.TakeField(ref record));
            var artist = AsciiRecordReader.ReadString(AsciiRecordReader.TakeField(ref record));
            var credit = AsciiRecordReader.ReadString(AsciiRecordReader.TakeField(ref record));
            var attribution = AsciiRecordReader.TakeField(ref record);
            var width = AsciiRecordReader.TakeField(ref record);
            var height = AsciiRecordReader.TakeField(ref record);

            ObjectArticleImage? image = fileName.Length == 0
                ? null
                : new ObjectArticleImage(fileName, hashPrefix, licence, artist, credit,
                    AttributionRequired: attribution.SequenceEqual("1"u8),
                    Width: int.Parse(width, NumberStyles.None, CultureInfo.InvariantCulture),
                    Height: int.Parse(height, NumberStyles.None, CultureInfo.InvariantCulture));

            var article = new ObjectArticle(item, title, image);
            foreach (var index in indices)
            {
                byIndex[(CatalogIndex)ulong.Parse(index, NumberStyles.None, CultureInfo.InvariantCulture)] = article;
            }
        }

        table = byIndex.ToFrozenDictionary();
        return !first;
    }

    /// <summary><see cref="TryRead"/> for a caller that needs the table: one it cannot read throws.</summary>
    public static FrozenDictionary<CatalogIndex, ObjectArticle> Read(Stream gzipped)
        => TryRead(gzipped, out var table)
            ? table
            : throw new InvalidDataException($"Not an object article table of schema {SchemaVersion}.");

    public static void Write(Stream destination, IEnumerable<ObjectArticleRow> rows)
    {
        using var gz = new GZipStream(destination, CompressionLevel.SmallestSize, leaveOpen: true);
        var text = new StringBuilder();

        text.Append(Magic).Append((char)AsciiRecordReader.RecordSeparator)
            .Append(SchemaVersion.ToString(CultureInfo.InvariantCulture))
            .Append((char)AsciiRecordReader.GroupSeparator);

        foreach (var row in rows)
        {
            var article = row.Article;
            text.Append(article.WikidataItem.ToString(CultureInfo.InvariantCulture)).Append((char)AsciiRecordReader.RecordSeparator)
                .Append(Clean(article.Title)).Append((char)AsciiRecordReader.RecordSeparator);

            for (var i = 0; i < row.Indices.Length; i++)
            {
                if (i > 0)
                {
                    text.Append((char)AsciiRecordReader.UnitSeparator);
                }
                text.Append(((ulong)row.Indices[i]).ToString(CultureInfo.InvariantCulture));
            }
            text.Append((char)AsciiRecordReader.RecordSeparator);

            if (article.Image is { } image)
            {
                text.Append(Clean(image.FileName)).Append((char)AsciiRecordReader.RecordSeparator)
                    .Append(Clean(image.HashPrefix)).Append((char)AsciiRecordReader.RecordSeparator)
                    .Append(Clean(image.Licence)).Append((char)AsciiRecordReader.RecordSeparator)
                    .Append(Clean(image.Artist)).Append((char)AsciiRecordReader.RecordSeparator)
                    .Append(Clean(image.Credit)).Append((char)AsciiRecordReader.RecordSeparator)
                    .Append(image.AttributionRequired ? '1' : '0').Append((char)AsciiRecordReader.RecordSeparator)
                    .Append(image.Width.ToString(CultureInfo.InvariantCulture)).Append((char)AsciiRecordReader.RecordSeparator)
                    .Append(image.Height.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                text.Append((char)AsciiRecordReader.RecordSeparator, 7);
            }

            text.Append((char)AsciiRecordReader.GroupSeparator);
        }

        var bytes = Encoding.UTF8.GetBytes(text.ToString());
        gz.Write(bytes, 0, bytes.Length);
    }

    /// <summary>
    /// A value from upstream can carry anything, and a separator byte inside a field would split the record.
    /// Every control character becomes a space.
    /// </summary>
    private static string Clean(string value)
    {
        foreach (var c in value)
        {
            if (char.IsControl(c))
            {
                var chars = value.ToCharArray();
                for (var i = 0; i < chars.Length; i++)
                {
                    if (char.IsControl(chars[i]))
                    {
                        chars[i] = ' ';
                    }
                }
                return new string(chars);
            }
        }

        return value;
    }
}
