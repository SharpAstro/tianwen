using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using static Astap.Lib.Astrometry.Catalogs.CatalogUtils;
using static Astap.Lib.EnumHelper;

namespace Astap.Lib.Astrometry.Catalogs;

public class IAUNamedStarDB : ICelestialObjectDB
{
    private readonly Dictionary<CatalogIndex, CelestialObject> _stellarObjectsByCatalogIndex = new(460);
    private readonly Dictionary<string, CatalogIndex> _namesToCatalogIndex = new(460);
    private readonly Dictionary<CatalogIndex, (CatalogIndex i1, CatalogIndex[]? ext)> _crossIndexLookuptable = new(460);

    private HashSet<CatalogIndex>? _catalogIndicesCache;
    private HashSet<Catalog>? _catalogCache;

    public IReadOnlyCollection<string> CommonNames => _namesToCatalogIndex.Keys;

    public IReadOnlySet<Catalog> Catalogs
    {
        get
        {
            if (_catalogCache is var cache and not null)
            {
                return cache;
            }

            if (_stellarObjectsByCatalogIndex.Count > 0)
            {
                return _catalogCache ??= this.IndicesToCatalogs<HashSet<Catalog>>();
            }
            return new HashSet<Catalog>(0);
        }
    }

    /// <inheritdoc/>
    public IReadOnlySet<CatalogIndex> ObjectIndices
    {
        get
        {
            if (_catalogIndicesCache is var cache and not null)
            {
                return cache;
            }

            var objs = _stellarObjectsByCatalogIndex.Count;
            if (objs > 0)
            {
                cache = new HashSet<CatalogIndex>(objs);
                cache.UnionWith(_stellarObjectsByCatalogIndex.Keys);

                return _catalogIndicesCache ??= cache;
            }
            return new HashSet<CatalogIndex>(0);
        }
    }

    /// <inheritdoc/>
    public async Task<(int processed, int failed)> InitDBAsync()
    {
        var processed = 0;
        var failed = 0;
        var assembly = typeof(IAUNamedStarDB).Assembly;
        var namedStarsGzippedJsonFileName = assembly.GetManifestResourceNames().FirstOrDefault(p => p.EndsWith(".iau-named-stars.json.gz"));

        if (namedStarsGzippedJsonFileName is not null && assembly.GetManifestResourceStream(namedStarsGzippedJsonFileName) is Stream stream)
        {
            using var gzipStream = new GZipStream(stream, CompressionMode.Decompress);
            await foreach (var record in JsonSerializer.DeserializeAsyncEnumerable<IAUNamedStarDTO>(gzipStream))
            {
                if (record is not null && TryGetCleanedUpCatalogName(record.Designation, out var catalogIndex))
                {
                    var objType = catalogIndex.ToCatalog() == Catalog.PSR ? ObjectType.Pulsar : ObjectType.Star;
                    var constellation = AbbreviationToEnumMember<Constellation>(record.Constellation);
                    var commonNames = new HashSet<string>(record.ID is not null ? 2 : 1)
                    {
                        record.IAUName
                    };

                    string? latinisedBayerName;
                    if (record.ID is not null)
                    {
                        var commonPart = string.Join(' ', constellation.ToIAUAbbreviation(), record.WDSComponentId).TrimEnd();
                        var bayerName = string.Join(' ', record.ID, commonPart).TrimStart();
                        latinisedBayerName = string.Join(' ', LatiniseBayerID(record.ID), commonPart).TrimStart();

                        commonNames.Add(bayerName);
                    }
                    else
                    {
                        latinisedBayerName = null;
                    }

                    var stellarObject = _stellarObjectsByCatalogIndex[catalogIndex] = new CelestialObject(
                        catalogIndex,
                        objType,
                        record.RA_J2000 / 15.0,
                        record.Dec_J2000,
                        constellation,
                        record.Vmag ?? float.NaN,
                        float.NaN,
                        commonNames
                    );

                    if (record.WDS_J is string wdsJ && TryGetCleanedUpCatalogName($"{Catalog.WDS.ToCanonical()}J{wdsJ}", out var wdsCatalogIndex))
                    {
                        _crossIndexLookuptable.AddLookupEntry(catalogIndex, wdsCatalogIndex);
                        _crossIndexLookuptable.AddLookupEntry(wdsCatalogIndex, catalogIndex);
                    }

                    foreach (var commonName in commonNames)
                    {
                        _namesToCatalogIndex[commonName] = stellarObject.Index;
                    }
                    if (latinisedBayerName is not null)
                    {
                        _namesToCatalogIndex[latinisedBayerName] = stellarObject.Index;
                    }
                    processed++;
                }
                else
                {
                    failed++;
                }
            }
        }

        return (processed, failed);
    }

    /// <inheritdoc/>
    public bool TryLookupByIndex(CatalogIndex catalogIndex, out CelestialObject namedStar)
        => _stellarObjectsByCatalogIndex.TryGetValue(catalogIndex, out namedStar);

    /// <inheritdoc/>
    public bool TryResolveCommonName(string name, out IReadOnlyList<CatalogIndex> starIndices)
    {
        if (_namesToCatalogIndex.TryGetValue(name, out var idx))
        {
            starIndices = new[] { idx };
            return true;
        }

        starIndices = Array.Empty<CatalogIndex>();
        return false;
    }

    /// <inheritdoc/>
    public bool TryGetCrossIndices(CatalogIndex catalogIndex, out IReadOnlyList<CatalogIndex> crossIndices)
        => _crossIndexLookuptable.TryGetLookupEntries(catalogIndex, out crossIndices);

    static string LatiniseBayerID(in ReadOnlySpan<char> text)
    {
        var sb = new StringBuilder(text.Length + 8);

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsAscii(c))
            {
                sb.Append(c);
            }
            else
            {
                sb.Append(c switch
                {
                    'α' => "Alpha",   //  1
                    'β' => "Beta",    //  2
                    'γ' => "Gamma",   //  3
                    'δ' => "Delta",   //  4
                    'ε' => "Epsilon", //  5
                    'ζ' => "Zeta",    //  6
                    'η' => "Eta",     //  7
                    'θ' => "Theta",   //  8
                    'ι' => "Iota",    //  9
                    'κ' => "Kappa",   // 10
                    'λ' => "Lambda",  // 11
                    'μ' => "Mu",      // 12
                    'ν' => "Nu",      // 13
                    'ξ' => "Xi",      // 14
                    'ο' => "Omicron", // 15
                    'π' => "Pi",      // 16
                    'ρ' => "Rho",     // 17
                    'σ' => "Sigma",   // 18
                    'τ' => "Tau",     // 19
                    'υ' => "Upsilon", // 20
                    'φ' or 'ϕ' => "Phi",     // 21
                    'χ' => "Chi",     // 22
                    'ψ' => "Psi",     // 23
                    'ω' => "Omega",   // 24
                    _ => throw new ArgumentException($"Unhandled char '{c}' in {text}", nameof(text))
                });
            }
        }

        return sb.ToString();
    }
}
