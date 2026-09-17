using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.Catalogs;

namespace BakeObjectImagery;

/// <summary>
/// Bakes <c>object_articles.gs.gz</c>: for every catalogued object in scope, the English Wikipedia article
/// it is VERIFIED to have, with the article's lead image and that image's credit.
/// </summary>
/// <remarks>
/// Every rule here was measured before it was written (docs/plans/object-imagery.md, "P0 measured"):
/// <list type="bullet">
/// <item>Scope: every object with a common name, plus Messier and Caldwell.</item>
/// <item>Two candidate routes, because each misses what the other finds: Wikidata catalogue codes in
/// Wikidata's own spelling, and English Wikipedia titles with redirects followed.</item>
/// <item>A candidate is accepted only when the Wikidata item's position lies within a tolerance of ours,
/// and several survivors are ranked by route, then separation.</item>
/// <item>The image is the article's lead image, never an SVG, a chart (light curves, position charts,
/// constellation maps) or a file with no licence.</item>
/// </list>
/// Requests run one at a time with a pause between them, under a User-Agent that names the project, which
/// is what Wikimedia's policy asks of a bulk client.
/// </remarks>
internal static partial class Program
{
    private const string UserAgent = "TianWenObjectImageryBake/1.0 (https://github.com/SharpAstro/tianwen)";
    private const string Sparql = "https://query.wikidata.org/sparql";
    private const string EnWikipediaApi = "https://en.wikipedia.org/w/api.php";
    private const string CommonsApi = "https://commons.wikimedia.org/w/api.php";

    private static readonly TimeSpan SparqlPause = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ApiPause = TimeSpan.FromMilliseconds(500);

    public static async Task<int> Main(string[] args)
    {
        string? output = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--output" when i + 1 < args.Length: output = args[++i]; break;
                default:
                    Console.Error.WriteLine($"unknown argument '{args[i]}'");
                    return Usage();
            }
        }

        if (output is null)
        {
            return Usage();
        }

        var ct = CancellationToken.None;
        var db = new CelestialObjectDB();
        await db.InitDBAsync(waitForTycho2BulkLoad: false, cancellationToken: ct);

        var scope = BuildScope(db);
        Log($"scope: {scope.Count} indices ({scope.Values.Count(o => o.IsStar)} stars)");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

        var candidates = new Dictionary<CatalogIndex, Dictionary<uint, Route>>();
        await FindByCodeAsync(http, scope, candidates, ct);
        await FindByTitleAsync(http, scope, candidates, ct);

        var itemIds = candidates.Values.SelectMany(c => c.Keys).ToHashSet();
        var facts = await GetItemFactsAsync(http, itemIds, ct);

        var chosen = Choose(scope, candidates, facts);
        Log($"verified with an article: {chosen.Count} indices, {chosen.Values.Distinct().Count()} articles");

        var titlesByItem = chosen.Values.Distinct().ToDictionary(q => q, q => facts[q].Title ?? "");
        var leadImages = await GetLeadImagesAsync(http, titlesByItem, ct);
        var images = await GetImageCreditsAsync(http, leadImages.Values.ToHashSet(StringComparer.Ordinal), ct);

        var rows = chosen
            .GroupBy(kv => kv.Value)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                ObjectArticleImage? image = leadImages.TryGetValue(g.Key, out var file) && images.TryGetValue(file, out var credit)
                    ? credit
                    : null;
                var indices = g.Select(kv => kv.Key).OrderBy(i => (ulong)i).ToImmutableArray();
                return new ObjectArticleRow(new ObjectArticle(g.Key, titlesByItem[g.Key], image), indices);
            })
            .ToList();

        var previous = File.Exists(output) ? ReadTable(output) : null;
        WriteAtomically(output, rows);
        Report(db, scope, candidates, rows, previous, ReadTable(output));
        return 0;
    }

    private static int Usage()
    {
        Console.Error.WriteLine("usage: BakeObjectImagery --output <path to object_articles.gs.gz>");
        return 2;
    }

    private static void Log(string message) => Console.Error.WriteLine("[bake-object-imagery] " + message);

    // ---------------------------------------------------------------- scope

    private sealed record ScopedObject(
        CatalogIndex Index,
        ObjectType Type,
        double RaHours,
        double Dec,
        double MajorArcmin,
        ImmutableArray<CatalogIndex> Designations,
        ImmutableArray<string> Names)
    {
        public bool IsStar => Type.IsStar;
    }

    /// <summary>Every object with a common name, plus Messier and Caldwell by their main entries.</summary>
    private static Dictionary<CatalogIndex, ScopedObject> BuildScope(CelestialObjectDB db)
    {
        var scope = new Dictionary<CatalogIndex, ScopedObject>();

        void Add(CatalogIndex index)
        {
            if (scope.ContainsKey(index) || index.IsSolarSystemObject
                || !db.TryLookupByIndex(index, out var obj) || double.IsNaN(obj.RA) || double.IsNaN(obj.Dec))
            {
                return;
            }

            var designations = ImmutableArray.CreateBuilder<CatalogIndex>();
            designations.Add(index);
            if (db.TryGetCrossIndices(index, out var cross))
            {
                designations.AddRange(cross.Where(c => c != index));
            }

            var major = db.TryGetShape(index, out var shape) && !Half.IsNaN(shape.MajorAxis) ? (double)shape.MajorAxis : 0.0;
            scope[index] = new ScopedObject(index, obj.ObjectType, obj.RA, obj.Dec, major,
                designations.ToImmutable(), [.. obj.CommonNames.Order(StringComparer.Ordinal)]);
        }

        foreach (var index in db.AllObjectIndices)
        {
            if (db.TryLookupByIndex(index, out var obj) && obj.CommonNames.Count > 0)
            {
                Add(index);
            }
        }

        foreach (var (prefix, count) in new[] { ("M", 110), ("C", 109) })
        {
            for (var n = 1; n <= count; n++)
            {
                if (CatalogUtils.TryGetCleanedUpCatalogName(prefix + n.ToString(CultureInfo.InvariantCulture), out var index)
                    && db.TryLookupByIndex(index, out var obj))
                {
                    Add(obj.Index);
                }
            }
        }

        return scope;
    }

    // ---------------------------------------------------------------- candidates

    [Flags]
    private enum Route { None = 0, Code = 1, Title = 2 }

    /// <summary>
    /// A catalogue code as Wikidata spells it, which is neither ours nor consistent: <c>M 42</c> but
    /// <c>M99</c>, <c>SH 2-25</c>, <c>Gum 33</c>, <c>B 33</c>. Caldwell yields nothing: its codes on Wikidata
    /// carry cluster designations under the Caldwell qualifier, and a bare <c>C 99</c> answers a mazurka.
    /// </summary>
    private static IEnumerable<string> CodeSpellings(CatalogIndex index)
    {
        var canonical = index.ToCanonical();
        if (MessierCode().Match(canonical) is { Success: true } messier)
        {
            yield return "M " + messier.Groups[1].Value;
            yield return "M" + messier.Groups[1].Value;
        }
        else if (SharplessCode().Match(canonical) is { Success: true } sharpless)
        {
            yield return "SH 2-" + sharpless.Groups[1].Value;
        }
        else if (GumCode().Match(canonical) is { Success: true } gum)
        {
            yield return "Gum " + gum.Groups[1].Value;
        }
        else if (BarnardCode().Match(canonical) is { Success: true } barnard)
        {
            yield return "B " + barnard.Groups[1].Value;
        }
        else if (!CaldwellCode().IsMatch(canonical) && !canonical.StartsWith("TYC ", StringComparison.Ordinal))
        {
            yield return canonical;
        }
    }

    /// <summary>English Wikipedia titles worth asking about: Messier, NGC, IC and Sharpless designations, and
    /// the common names, capitalised the way a title is.</summary>
    private static IEnumerable<string> TitleSpellings(ScopedObject obj)
    {
        foreach (var designation in obj.Designations)
        {
            var canonical = designation.ToCanonical();
            if (MessierCode().Match(canonical) is { Success: true } messier)
            {
                yield return "Messier " + messier.Groups[1].Value;
            }
            else if (SharplessCode().Match(canonical) is { Success: true } sharpless)
            {
                yield return "Sh 2-" + sharpless.Groups[1].Value;
            }
            else if (NgcOrIc().IsMatch(canonical))
            {
                yield return canonical;
            }
        }

        foreach (var name in obj.Names)
        {
            if (name.Length > 3 && !InstrumentSuffix().IsMatch(name))
            {
                yield return char.ToUpperInvariant(name[0]) + name[1..];
            }
        }
    }

    private static async Task FindByCodeAsync(HttpClient http, Dictionary<CatalogIndex, ScopedObject> scope,
        Dictionary<CatalogIndex, Dictionary<uint, Route>> candidates, CancellationToken ct)
    {
        var objectsByCode = new Dictionary<string, List<CatalogIndex>>(StringComparer.Ordinal);
        foreach (var obj in scope.Values)
        {
            foreach (var code in obj.Designations.SelectMany(CodeSpellings).Distinct(StringComparer.Ordinal))
            {
                if (!objectsByCode.TryGetValue(code, out var list))
                {
                    objectsByCode[code] = list = [];
                }
                list.Add(obj.Index);
            }
        }

        var codes = objectsByCode.Keys.Order(StringComparer.Ordinal).ToArray();
        Log($"codes: {codes.Length}");
        const int batch = 250;
        for (var start = 0; start < codes.Length; start += batch)
        {
            var values = string.Join(' ', codes.Skip(start).Take(batch).Select(SparqlString));
            var query = $"SELECT ?code ?item WHERE {{ VALUES ?code {{ {values} }} ?item wdt:P528 ?code . }}";
            foreach (var binding in await SparqlAsync(http, query, ct))
            {
                var item = ItemNumber(binding.GetProperty("item").GetProperty("value").GetString());
                foreach (var index in objectsByCode[binding.GetProperty("code").GetProperty("value").GetString() ?? ""])
                {
                    AddCandidate(candidates, index, item, Route.Code);
                }
            }

            Log($"codes {Math.Min(start + batch, codes.Length)}/{codes.Length}");
            await Task.Delay(SparqlPause, ct);
        }
    }

    private static async Task FindByTitleAsync(HttpClient http, Dictionary<CatalogIndex, ScopedObject> scope,
        Dictionary<CatalogIndex, Dictionary<uint, Route>> candidates, CancellationToken ct)
    {
        var objectsByTitle = new Dictionary<string, List<CatalogIndex>>(StringComparer.Ordinal);
        foreach (var obj in scope.Values)
        {
            foreach (var title in TitleSpellings(obj).Distinct(StringComparer.Ordinal))
            {
                if (!objectsByTitle.TryGetValue(title, out var list))
                {
                    objectsByTitle[title] = list = [];
                }
                list.Add(obj.Index);
            }
        }

        var titles = objectsByTitle.Keys.Order(StringComparer.Ordinal).ToArray();
        Log($"titles: {titles.Length}");
        var disambiguation = 0;
        const int batch = 50;
        for (var start = 0; start < titles.Length; start += batch)
        {
            var asked = titles.Skip(start).Take(batch).ToArray();
            var json = await PostAsync(http, EnWikipediaApi, new Dictionary<string, string>
            {
                ["action"] = "query", ["format"] = "json", ["formatversion"] = "2", ["redirects"] = "1",
                ["titles"] = string.Join('|', asked), ["prop"] = "pageprops", ["ppprop"] = "wikibase_item|disambiguation",
            }, ct);

            var query = json.RootElement.GetProperty("query");
            var landedOn = FollowTitles(query, asked);
            foreach (var page in query.TryGetProperty("pages", out var pages) ? pages.EnumerateArray() : default)
            {
                if (!page.TryGetProperty("pageprops", out var props))
                {
                    continue;
                }
                if (props.TryGetProperty("disambiguation", out _))
                {
                    disambiguation++;
                    continue;
                }
                if (!props.TryGetProperty("wikibase_item", out var wikibase))
                {
                    continue;
                }

                var item = ItemNumber(wikibase.GetString());
                var pageTitle = page.GetProperty("title").GetString();
                foreach (var original in asked.Where(t => landedOn[t] == pageTitle))
                {
                    foreach (var index in objectsByTitle[original])
                    {
                        AddCandidate(candidates, index, item, Route.Title);
                    }
                }
            }

            if ((start / batch) % 20 == 0)
            {
                Log($"titles {Math.Min(start + batch, titles.Length)}/{titles.Length}");
            }
            await Task.Delay(ApiPause, ct);
        }
        Log($"disambiguation pages dropped: {disambiguation}");
    }

    /// <summary>Where each asked title landed, following the API's normalisation and then its redirects.</summary>
    private static Dictionary<string, string> FollowTitles(JsonElement query, IReadOnlyList<string> asked)
    {
        var landedOn = asked.ToDictionary(t => t, t => t, StringComparer.Ordinal);
        foreach (var step in new[] { "normalized", "redirects" })
        {
            if (!query.TryGetProperty(step, out var hops))
            {
                continue;
            }
            foreach (var hop in hops.EnumerateArray())
            {
                var from = hop.GetProperty("from").GetString();
                var to = hop.GetProperty("to").GetString() ?? "";
                foreach (var title in asked)
                {
                    if (landedOn[title] == from)
                    {
                        landedOn[title] = to;
                    }
                }
            }
        }
        return landedOn;
    }

    private static void AddCandidate(Dictionary<CatalogIndex, Dictionary<uint, Route>> candidates, CatalogIndex index, uint item, Route route)
    {
        if (!candidates.TryGetValue(index, out var items))
        {
            candidates[index] = items = [];
        }
        items[item] = items.GetValueOrDefault(item) | route;
    }

    // ---------------------------------------------------------------- verification

    private sealed record ItemFacts(double? RaDeg, double? Dec, string? Title);

    private static async Task<Dictionary<uint, ItemFacts>> GetItemFactsAsync(HttpClient http, IReadOnlyCollection<uint> items, CancellationToken ct)
    {
        var facts = new Dictionary<uint, ItemFacts>();
        var ordered = items.Order().ToArray();
        Log($"items: {ordered.Length}");
        const int batch = 300;
        for (var start = 0; start < ordered.Length; start += batch)
        {
            var values = string.Join(' ', ordered.Skip(start).Take(batch).Select(q => "wd:Q" + q.ToString(CultureInfo.InvariantCulture)));
            var query = $$"""
                SELECT ?item ?ra ?dec ?title WHERE {
                  VALUES ?item { {{values}} }
                  OPTIONAL { ?item wdt:P6257 ?ra . }
                  OPTIONAL { ?item wdt:P6258 ?dec . }
                  OPTIONAL { ?article schema:about ?item ; schema:isPartOf <https://en.wikipedia.org/> ; schema:name ?title . }
                }
                """;
            foreach (var binding in await SparqlAsync(http, query, ct))
            {
                var item = ItemNumber(binding.GetProperty("item").GetProperty("value").GetString());
                var known = facts.GetValueOrDefault(item) ?? new ItemFacts(null, null, null);
                facts[item] = new ItemFacts(
                    known.RaDeg ?? SparqlDouble(binding, "ra"),
                    known.Dec ?? SparqlDouble(binding, "dec"),
                    known.Title ?? (binding.TryGetProperty("title", out var title) ? title.GetProperty("value").GetString() : null));
            }
            await Task.Delay(SparqlPause, ct);
        }
        return facts;
    }

    /// <summary>
    /// For each index, the one verified item with an English article, or none. Accepted only inside the
    /// tolerance; among several, an item both routes found beats one found by code, which beats one found by
    /// title, and then the nearer wins. That rule picks the Carina Nebula over the Keyhole Nebula for
    /// NGC 3372, and the IC 434 article over the Flame Nebula for IC 434.
    /// </summary>
    private static Dictionary<CatalogIndex, uint> Choose(Dictionary<CatalogIndex, ScopedObject> scope,
        Dictionary<CatalogIndex, Dictionary<uint, Route>> candidates, Dictionary<uint, ItemFacts> facts)
    {
        var chosen = new Dictionary<CatalogIndex, uint>();
        foreach (var (index, items) in candidates)
        {
            var obj = scope[index];
            var tolerance = ToleranceDeg(obj);
            var best = items
                .Where(kv => facts.TryGetValue(kv.Key, out var f) && f is { RaDeg: not null, Dec: not null, Title: not null })
                .Select(kv => (Item: kv.Key, Rank: RouteRank(kv.Value),
                    Separation: SeparationDeg(obj.RaHours * 15.0, obj.Dec, facts[kv.Key].RaDeg ?? 0, facts[kv.Key].Dec ?? 0)))
                .Where(c => c.Separation <= tolerance)
                .OrderBy(c => c.Rank).ThenBy(c => c.Separation).ThenBy(c => c.Item)
                .FirstOrDefault();

            if (best.Item != 0)
            {
                chosen[index] = best.Item;
            }
        }
        return chosen;
    }

    private static int RouteRank(Route route) => route switch
    {
        Route.Code | Route.Title => 0,
        Route.Code => 1,
        _ => 2,
    };

    /// <summary>
    /// The object's catalogued major axis where we have one. Without it, one degree for an extended kind,
    /// because the catalogue gives no size for many of the largest (the Pleiades matched 6.6 arcminutes
    /// off, the Witch Head 58) and ten arcminutes for everything else. A wrong code match lands thousands
    /// of arcminutes away.
    /// </summary>
    private static double ToleranceDeg(ScopedObject obj)
    {
        var size = obj.MajorArcmin / 60.0;
        var floor = obj.MajorArcmin <= 0 && IsExtended(obj.Type) ? 1.0 : 10.0 / 60.0;
        return Math.Max(floor, size);
    }

    private static bool IsExtended(ObjectType type) => type is
        ObjectType.PlanetaryNeb or ObjectType.GlobCluster or ObjectType.OpenCluster or ObjectType.MouvGroup or
        ObjectType.Association or ObjectType.RefNeb or ObjectType.GalNeb or ObjectType.HIIReg or ObjectType.MolCld or
        ObjectType.Cloud or ObjectType.DarkNeb or ObjectType.SNRemnant or ObjectType.HVCld or ObjectType.GroupG or
        ObjectType.ClG or ObjectType.SuperClG or ObjectType.EmObj or ObjectType.PartofCloud or ObjectType.Region or
        ObjectType.GlClCandidate or ObjectType.SNRCandidate;

    private static double SeparationDeg(double ra1Deg, double dec1Deg, double ra2Deg, double dec2Deg)
    {
        var d1 = double.DegreesToRadians(dec1Deg);
        var d2 = double.DegreesToRadians(dec2Deg);
        var dRa = double.DegreesToRadians(ra1Deg - ra2Deg);
        var cos = Math.Sin(d1) * Math.Sin(d2) + Math.Cos(d1) * Math.Cos(d2) * Math.Cos(dRa);
        return double.RadiansToDegrees(Math.Acos(Math.Clamp(cos, -1.0, 1.0)));
    }

    // ---------------------------------------------------------------- images

    /// <summary>The lead image file of each article, the free image the article itself shows.</summary>
    private static async Task<Dictionary<uint, string>> GetLeadImagesAsync(HttpClient http, Dictionary<uint, string> titlesByItem, CancellationToken ct)
    {
        var itemsByTitle = titlesByItem.GroupBy(kv => kv.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToArray(), StringComparer.Ordinal);
        var titles = itemsByTitle.Keys.Order(StringComparer.Ordinal).ToArray();
        var lead = new Dictionary<uint, string>();
        const int batch = 50;
        for (var start = 0; start < titles.Length; start += batch)
        {
            var asked = titles.Skip(start).Take(batch).ToArray();
            var json = await PostAsync(http, EnWikipediaApi, new Dictionary<string, string>
            {
                ["action"] = "query", ["format"] = "json", ["formatversion"] = "2",
                ["titles"] = string.Join('|', asked), ["prop"] = "pageimages", ["piprop"] = "name",
            }, ct);

            var query = json.RootElement.GetProperty("query");
            var landedOn = FollowTitles(query, asked);
            foreach (var page in query.TryGetProperty("pages", out var pages) ? pages.EnumerateArray() : default)
            {
                if (!page.TryGetProperty("pageimage", out var image))
                {
                    continue;
                }
                var pageTitle = page.GetProperty("title").GetString();
                foreach (var original in asked.Where(t => landedOn[t] == pageTitle))
                {
                    foreach (var item in itemsByTitle[original])
                    {
                        lead[item] = (image.GetString() ?? "").Replace('_', ' ');
                    }
                }
            }
            await Task.Delay(ApiPause, ct);
        }
        Log($"lead images: {lead.Count} of {titlesByItem.Count} articles");
        return lead;
    }

    /// <summary>
    /// Commons' credit for each file that is a PICTURE of its object. No entry, so the article keeps its link
    /// and loses only the image, for: a file Commons does not hold (a local, non-free upload); an SVG (every
    /// SVG lead measured was a constellation map); a file with no licence, which no credit line can state;
    /// and a chart. Star articles often lead with a light curve, a position chart or a constellation map:
    /// 105 of the first bake's 425 kept files. Commons' own categories find 100 of them ("Light curves of
    /// Delta Scuti variables", "Star location maps") and the file name finds 103, 98 in common, so a file
    /// either one flags is dropped.
    /// </summary>
    private static async Task<Dictionary<string, ObjectArticleImage>> GetImageCreditsAsync(HttpClient http, IReadOnlyCollection<string> files, CancellationToken ct)
    {
        var credits = new Dictionary<string, ObjectArticleImage>(StringComparer.Ordinal);
        var ordered = files.Order(StringComparer.Ordinal).ToArray();
        var svg = 0;
        var notOnCommons = 0;
        var unlicensed = 0;
        var charts = 0;
        const int batch = 50;
        for (var start = 0; start < ordered.Length; start += batch)
        {
            var asked = ordered.Skip(start).Take(batch).Select(f => "File:" + f).ToArray();
            var json = await PostAsync(http, CommonsApi, new Dictionary<string, string>
            {
                ["action"] = "query", ["format"] = "json", ["formatversion"] = "2",
                ["titles"] = string.Join('|', asked), ["prop"] = "imageinfo", ["iiprop"] = "extmetadata|size|mime",
                ["iiextmetadatafilter"] = "LicenseShortName|Artist|Credit|AttributionRequired",
            }, ct);
            var categories = await GetCategoriesAsync(http, asked, ct);

            var query = json.RootElement.GetProperty("query");
            var landedOn = FollowTitles(query, asked);
            foreach (var page in query.TryGetProperty("pages", out var pages) ? pages.EnumerateArray() : default)
            {
                if (page.TryGetProperty("missing", out _)
                    || !page.TryGetProperty("imageinfo", out var infos) || infos.GetArrayLength() == 0)
                {
                    notOnCommons++;
                    continue;
                }

                var info = infos[0];
                if (info.GetProperty("mime").GetString() == "image/svg+xml")
                {
                    svg++;
                    continue;
                }

                var pageTitle = page.GetProperty("title").GetString() ?? "";
                if (ChartName().IsMatch(pageTitle)
                    || categories.GetValueOrDefault(pageTitle, []).Any(c => ChartCategory().IsMatch(c)))
                {
                    charts++;
                    continue;
                }

                var meta = info.TryGetProperty("extmetadata", out var m) && m.ValueKind == JsonValueKind.Object ? m : default;
                if (MetaText(meta, "LicenseShortName").Length == 0)
                {
                    unlicensed++;
                    continue;
                }

                var image = new ObjectArticleImage(
                    FileName: "",
                    Licence: MetaText(meta, "LicenseShortName"),
                    Artist: MetaText(meta, "Artist"),
                    Credit: MetaText(meta, "Credit"),
                    AttributionRequired: MetaText(meta, "AttributionRequired") == "true",
                    Width: info.GetProperty("width").GetInt32(),
                    Height: info.GetProperty("height").GetInt32());

                foreach (var original in asked.Where(t => landedOn[t] == pageTitle))
                {
                    var file = original["File:".Length..];
                    credits[file] = image with { FileName = file };
                }
            }
            await Task.Delay(ApiPause, ct);
        }
        Log($"image credits: {credits.Count} of {ordered.Length} files ({svg} SVG, {charts} charts, {unlicensed} unlicensed, {notOnCommons} not on Commons dropped)");
        return credits;
    }

    /// <summary>The visible (not hidden) Commons categories of each file, following the API's continuation:
    /// fifty files can carry more categories than one response returns.</summary>
    private static async Task<Dictionary<string, List<string>>> GetCategoriesAsync(HttpClient http, string[] fileTitles, CancellationToken ct)
    {
        var categories = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var form = new Dictionary<string, string>
        {
            ["action"] = "query", ["format"] = "json", ["formatversion"] = "2",
            ["titles"] = string.Join('|', fileTitles), ["prop"] = "categories", ["cllimit"] = "max", ["clshow"] = "!hidden",
        };

        while (true)
        {
            using var json = await PostAsync(http, CommonsApi, form, ct);
            if (json.RootElement.TryGetProperty("query", out var query) && query.TryGetProperty("pages", out var pages))
            {
                foreach (var page in pages.EnumerateArray())
                {
                    if (!page.TryGetProperty("categories", out var cats))
                    {
                        continue;
                    }
                    var title = page.GetProperty("title").GetString() ?? "";
                    if (!categories.TryGetValue(title, out var list))
                    {
                        categories[title] = list = [];
                    }
                    list.AddRange(cats.EnumerateArray().Select(c => c.GetProperty("title").GetString() ?? ""));
                }
            }

            if (!json.RootElement.TryGetProperty("continue", out var next))
            {
                return categories;
            }
            foreach (var property in next.EnumerateObject())
            {
                form[property.Name] = property.Value.ToString();
            }
            await Task.Delay(ApiPause, ct);
        }
    }

    /// <summary>An extmetadata value as plain text: tags stripped, entities decoded, whitespace collapsed.</summary>
    private static string MetaText(JsonElement meta, string key)
    {
        if (meta.ValueKind != JsonValueKind.Object || !meta.TryGetProperty(key, out var entry)
            || !entry.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.String)
        {
            return "";
        }

        var text = WebUtility.HtmlDecode(HtmlTag().Replace(value.GetString() ?? "", " "));
        text = Whitespace().Replace(text, " ").Trim();
        return text.Length <= 300 ? text : text[..300].TrimEnd() + "...";
    }

    // ---------------------------------------------------------------- output

    private static FrozenTable ReadTable(string path)
    {
        using var stream = File.OpenRead(path);
        return new FrozenTable(ObjectArticleTable.Read(stream));
    }

    private sealed record FrozenTable(IReadOnlyDictionary<CatalogIndex, ObjectArticle> ByIndex);

    private static void WriteAtomically(string output, IReadOnlyList<ObjectArticleRow> rows)
    {
        var full = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(full) ?? ".");
        var temp = full + ".tmp";
        using (var stream = File.Create(temp))
        {
            ObjectArticleTable.Write(stream, rows);
        }
        File.Move(temp, full, overwrite: true);
        Log($"wrote {full} ({new FileInfo(full).Length:N0} bytes, {rows.Count} articles)");
    }

    /// <summary>
    /// What a reviewer reads instead of a binary diff: the coverage, the Messier and Caldwell objects left
    /// without an article, the licence spread, and every index whose answer changed since the last bake.
    /// </summary>
    private static void Report(CelestialObjectDB db, Dictionary<CatalogIndex, ScopedObject> scope,
        Dictionary<CatalogIndex, Dictionary<uint, Route>> candidates, IReadOnlyList<ObjectArticleRow> rows,
        FrozenTable? previous, FrozenTable current)
    {
        var output = new StringBuilder();
        var stars = scope.Values.Where(o => o.IsStar).Select(o => o.Index).ToHashSet();
        output.AppendLine("# Object article bake");
        output.AppendLine();
        output.AppendLine("| | Indices in scope | With a candidate | With a verified article |");
        output.AppendLine("|---|---|---|---|");
        output.AppendLine($"| Non-stars | {scope.Count - stars.Count} | {candidates.Keys.Count(i => !stars.Contains(i))} | {current.ByIndex.Keys.Count(i => scope.ContainsKey(i) && !stars.Contains(i))} |");
        output.AppendLine($"| Stars | {stars.Count} | {candidates.Keys.Count(stars.Contains)} | {current.ByIndex.Keys.Count(stars.Contains)} |");
        output.AppendLine();

        var withImage = rows.Count(r => r.Article.Image is not null);
        output.AppendLine($"{rows.Count} articles, {withImage} with a lead image.");
        output.AppendLine();
        output.AppendLine("Licences: " + string.Join(", ", rows.Where(r => r.Article.Image is not null)
            .GroupBy(r => r.Article.Image?.Licence is { Length: > 0 } l ? l : "(none)")
            .OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}")));
        output.AppendLine();

        foreach (var (prefix, count, name) in new[] { ("M", 110, "Messier"), ("C", 109, "Caldwell") })
        {
            var missing = new List<string>();
            for (var n = 1; n <= count; n++)
            {
                var label = prefix + n.ToString(CultureInfo.InvariantCulture);
                if (!CatalogUtils.TryGetCleanedUpCatalogName(label, out var index)
                    || !db.TryLookupByIndex(index, out var obj)
                    || !(current.ByIndex.ContainsKey(obj.Index) || current.ByIndex.ContainsKey(index)))
                {
                    missing.Add(label);
                }
            }
            output.AppendLine($"{name} without an article ({missing.Count}): {string.Join(", ", missing)}");
        }
        output.AppendLine();

        var mapNamed = rows.Where(r => r.Article.Image is { } i && i.FileName.Contains("map", StringComparison.OrdinalIgnoreCase)).ToList();
        output.AppendLine($"Lead images with 'map' in the file name ({mapNamed.Count}): "
            + string.Join("; ", mapNamed.Take(25).Select(r => r.Article.Image?.FileName)));
        output.AppendLine();

        if (previous is not null)
        {
            var added = current.ByIndex.Keys.Where(i => !previous.ByIndex.ContainsKey(i)).ToList();
            var removed = previous.ByIndex.Keys.Where(i => !current.ByIndex.ContainsKey(i)).ToList();
            var retitled = current.ByIndex.Where(kv => previous.ByIndex.TryGetValue(kv.Key, out var old) && old.Title != kv.Value.Title).ToList();
            var reimaged = current.ByIndex.Where(kv => previous.ByIndex.TryGetValue(kv.Key, out var old)
                && old.Title == kv.Value.Title && old.Image?.FileName != kv.Value.Image?.FileName).ToList();
            output.AppendLine($"## Since the last bake: {added.Count} added, {removed.Count} removed, {retitled.Count} retitled, {reimaged.Count} with a different image");
            foreach (var index in removed.Take(50))
            {
                output.AppendLine($"- removed {index.ToCanonical()}: {previous.ByIndex[index].Title}");
            }
            foreach (var (index, article) in retitled.Take(50))
            {
                output.AppendLine($"- retitled {index.ToCanonical()}: {previous.ByIndex[index].Title} -> {article.Title}");
            }
            foreach (var index in added.Take(50))
            {
                output.AppendLine($"- added {index.ToCanonical()}: {current.ByIndex[index].Title}");
            }
        }

        Console.Write(output.ToString());
    }

    // ---------------------------------------------------------------- HTTP

    private static async Task<List<JsonElement>> SparqlAsync(HttpClient http, string query, CancellationToken ct)
    {
        using var json = await PostAsync(http, Sparql, new Dictionary<string, string> { ["query"] = query, ["format"] = "json" }, ct);
        return json.RootElement.GetProperty("results").GetProperty("bindings").EnumerateArray().Select(b => b.Clone()).ToList();
    }

    /// <summary>
    /// One POST, retried with a growing pause on a throttle, a server error or a timeout, honouring a
    /// Retry-After. POST rather than GET because a batch of titles or codes overruns a URL.
    /// </summary>
    private static async Task<JsonDocument> PostAsync(HttpClient http, string url, Dictionary<string, string> form, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
                request.Headers.Accept.ParseAdd("application/json");
                request.Headers.Accept.ParseAdd("application/sparql-results+json");
                using var response = await http.SendAsync(request, ct);
                if (response.IsSuccessStatusCode)
                {
                    return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                }

                var retryable = response.StatusCode is HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
                if (!retryable || attempt >= 5)
                {
                    throw new HttpRequestException($"{url} answered {(int)response.StatusCode} {response.ReasonPhrase}");
                }

                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(10 * attempt);
                Log($"{url} answered {(int)response.StatusCode}; waiting {wait.TotalSeconds:F0} s");
                await Task.Delay(wait, ct);
            }
            // A request timeout is not a cancellation, and only the token tells them apart (the lesson
            // bake-comets paid for): a slow endpoint is retried, a torn-down run propagates.
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && attempt < 5)
            {
                Log($"{url} timed out; retrying");
                await Task.Delay(TimeSpan.FromSeconds(10 * attempt), ct);
            }
        }
    }

    private static string SparqlString(string value) => JsonSerializer.Serialize(value);

    private static double? SparqlDouble(JsonElement binding, string name)
        => binding.TryGetProperty(name, out var v)
            && double.TryParse(v.GetProperty("value").GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d : null;

    private static uint ItemNumber(string? entityUrl)
    {
        var id = entityUrl?[(entityUrl.LastIndexOf('/') + 1)..] ?? "";
        return id.StartsWith('Q') && uint.TryParse(id.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var q)
            ? q
            : throw new InvalidDataException($"not a Wikidata item: '{entityUrl}'");
    }

    [GeneratedRegex(@"^M(\d+)$")] private static partial Regex MessierCode();
    [GeneratedRegex(@"^C(\d+)$")] private static partial Regex CaldwellCode();
    [GeneratedRegex(@"^Sh2-(\d+)$")] private static partial Regex SharplessCode();
    [GeneratedRegex(@"^GUM (\d+)$")] private static partial Regex GumCode();
    [GeneratedRegex(@"^Barnard (\d+)$")] private static partial Regex BarnardCode();
    [GeneratedRegex(@"^(NGC|IC) \d+$")] private static partial Regex NgcOrIc();
    [GeneratedRegex(@"\b(IRS|NED)\d*\b")] private static partial Regex InstrumentSuffix();
    [GeneratedRegex(@"light curves|location maps|constellation maps|astronomical maps|star charts|finder charts|diagrams|spectra|orbits|asterisms", RegexOptions.IgnoreCase)]
    private static partial Regex ChartCategory();
    [GeneratedRegex(@"light ?curve|constellation map|star ?map|^File:Position |asterism|chart|finder|diagram|orbit|spectrum|location of|location map", RegexOptions.IgnoreCase)]
    private static partial Regex ChartName();
    [GeneratedRegex("<[^>]+>")] private static partial Regex HtmlTag();
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
}
