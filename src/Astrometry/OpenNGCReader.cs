using CsvHelper;
using CsvHelper.Configuration;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using static Astap.Lib.EnumHelper;
using static Astap.Lib.Astrometry.Utils;

namespace Astap.Lib.Astrometry
{
    public class OpenNGCReader
    {
        private readonly Dictionary<CatalogIndex, CelestialObject> _objectsByIndex = new(14000);
        private readonly Dictionary<CatalogIndex, CatalogIndex[]> _crossLookupTable = new(900);
        private readonly Dictionary<string, CatalogIndex[]> _objectsByCommonName = new(200);

        public OpenNGCReader() { }

        public bool TryLookupByIndex(string name, [NotNullWhen(true)] out CelestialObject? celestialObject)
        {
            if (TryGetCleanedUpCatalogName(name, out var maybeIndex) && maybeIndex is CatalogIndex index && TryLookupByIndex(index, out celestialObject))
            {
                return true;
            }
            else
            {
                celestialObject = default;
                return false;
            }
        }

        public bool TryLookupByIndex(CatalogIndex indexEntry, [NotNullWhen(true)] out CelestialObject? celestialObject)
            => _objectsByIndex.TryGetValue(indexEntry, out celestialObject);

        public async Task<(int processed, int failed)> ReadEmbeddedDataFilesAsync()
        {
            var assembly = typeof(OpenNGCReader).Assembly;
            int totalProcessed = 0;
            int totalFailed = 0;

            foreach (var csvName in new[] { "NGC.csv", "addendum.csv" })
            {
                var (processed, failed) = await ReadEmbeddedDataFileAsync(assembly, csvName);
                totalProcessed += processed;
                totalFailed += failed;
            }

            return (totalProcessed, totalFailed);
        }

        private async Task<(int processed, int failed)> ReadEmbeddedDataFileAsync(Assembly assembly, string csvName)
        {
            const string NGC = nameof(NGC);
            const string IC = nameof(IC);
            const string M = nameof(M);

            int processed = 0;
            int failed = 0;
            var manifestFileName = assembly.GetManifestResourceNames().FirstOrDefault(p => p.EndsWith(csvName));
            if (manifestFileName is null || assembly.GetManifestResourceStream(manifestFileName) is not Stream stream)
            {
                return (processed, failed);
            }

            using var streamReader = new StreamReader(stream, new UTF8Encoding(false));
            using var csvReader = new CsvReader(streamReader, new CsvConfiguration(CultureInfo.InvariantCulture) { Delimiter = ";" });

            if (!await csvReader.ReadAsync() || !csvReader.ReadHeader())
            {
                return (processed, failed);
            }

            while (await csvReader.ReadAsync())
            {
                if (csvReader.TryGetField<string>("Name", out var entryName)
                    && csvReader.TryGetField<string>("Type", out var objectTypeAbbr)
                    && csvReader.TryGetField<string>("RA", out var raHMS)
                    && csvReader.TryGetField<string>("Dec", out var decDMS)
                    && csvReader.TryGetField<string>("Const", out var constAbbr)
                    && TryGetCleanedUpCatalogName(entryName, out var maybeCatalogIndex)
                    && maybeCatalogIndex is CatalogIndex indexEntry
                )
                {
                    var objectType = AbbreviationToEnumMember<ObjectType>(objectTypeAbbr);
                    var @const = AbbreviationToEnumMember<Constellation>(constAbbr);
                    _objectsByIndex[indexEntry] = new CelestialObject(indexEntry, objectType, HMSToDegree(raHMS), DMSToDegree(decDMS), @const);

                    if (objectType == ObjectType.Duplicate)
                    {
                        // when the entry is a duplicate, use the cross lookup table to list the entries it duplicates
                        if (TryGetCatalogField(NGC, out var ngcIndexEntry))
                        {
                            AddLookupEntry(_crossLookupTable, indexEntry, ngcIndexEntry);
                        }
                        if (TryGetCatalogField(IC, out var icIndexEntry))
                        {
                            AddLookupEntry(_crossLookupTable, indexEntry, icIndexEntry);
                        }
                        if (TryGetCatalogField(M, out var messierIndexEntry))
                        {
                            AddLookupEntry(_crossLookupTable, indexEntry, messierIndexEntry);
                        }
                    }
                    else if (TryGetCatalogField(M, out var messierIndexEntry))
                    {
                        // Adds Messier to NGC/IC entry lookup
                        AddLookupEntry(_crossLookupTable, messierIndexEntry, indexEntry);
                    }

                    if (csvReader.TryGetField<string>("Common names", out var commonNamesEntry))
                    {
                        var commonNames = commonNamesEntry.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        foreach (var commonName in commonNames)
                        {
                            AddLookupEntry(_objectsByCommonName, commonName, indexEntry);
                        }
                    }

                    processed++;
                }
                else
                {
                    failed++;
                }
            }

            return (processed, failed);

            bool TryGetCatalogField(string catPrefix, out CatalogIndex entry)
            {
                entry = 0;
                return csvReader.TryGetField<string>(catPrefix, out var suffix) && TryGetCleanedUpCatalogName(catPrefix + suffix, out entry);
            }
        }

        private static void AddLookupEntry<T>(Dictionary<T, CatalogIndex[]> lookupTable, T master, CatalogIndex duplicate)
            where T : notnull
        {
            if (!lookupTable.TryAdd(master, new[] { duplicate }))
            {
                lookupTable[master] = ResizeAndAdd(lookupTable[master], duplicate);
            }
        }

        private static CatalogIndex[] ResizeAndAdd(CatalogIndex[] existing, CatalogIndex indexEntry)
        {
            var @new = new CatalogIndex[existing.Length + 1];
            Array.Copy(existing, @new, existing.Length);
            @new[^1] = indexEntry;
            return @new;
        }
    }
}
