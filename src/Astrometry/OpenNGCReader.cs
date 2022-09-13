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
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using static Astap.Lib.EnumHelper;

namespace Astap.Lib.Astrometry
{

    public record DeepSkyObject(CatalogIndex Index, ObjectType ObjType, double RA, double Dec, Constellation Constellation);

    public class OpenNGCReader
    {
        private readonly Dictionary<CatalogIndex, DeepSkyObject> _objectsByIndex = new(14000);
        private readonly Dictionary<CatalogIndex, CatalogIndex[]> _crossLookupTable = new(900);
        private readonly Dictionary<string, CatalogIndex[]> _objectsByCommonName = new(200);

        public OpenNGCReader() { }

        public bool TryLookupByIndex(string name, [NotNullWhen(true)] out DeepSkyObject? deepSkyObject)
        {
            if (TryGetCleanedUpCatalogName(name, out var cleanedUp) && TryLookupByIndex(AbbreviationToEnumMember<CatalogIndex>(cleanedUp), out deepSkyObject))
            {
                return true;
            }
            else
            {
                deepSkyObject = default;
                return false;
            }
        }

        public bool TryLookupByIndex(CatalogIndex indexEntry, [NotNullWhen(true)] out DeepSkyObject? deepSkyObject)
            => _objectsByIndex.TryGetValue(indexEntry, out deepSkyObject);

        public async Task<(int processed, int failed)> ReadEmbeddedDataFilesAsync()
        {
            var assy = typeof(OpenNGCReader).Assembly;
            int totalProcessed = 0;
            int totalFailed = 0;

            foreach (var csvName in new[] { "NGC.csv", "addendum.csv" })
            {
                var (processed, failed) = await ReadEmbeddedDataFileAsync(assy, csvName);
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
                    && TryGetCleanedUpCatalogName(entryName, out var cleanedUpName)
                )
                {
                    var objectType = AbbreviationToEnumMember<ObjectType>(objectTypeAbbr);
                    var @const = AbbreviationToEnumMember<Constellation>(constAbbr);
                    var indexEntry = AbbreviationToEnumMember<CatalogIndex>(cleanedUpName);
                    _objectsByIndex[indexEntry] = new DeepSkyObject(indexEntry, objectType, Utils.HMSToDegree(raHMS), Utils.DMSToDegree(decDMS), @const);

                    if (objectType == ObjectType.Duplicate)
                    {
                        // when the entry is a duplicate, use the cross lookup table to list the entries it duplicates
                        if (csvReader.TryGetField<string>(NGC, out var ngcDupSuffix) && TryGetCleanedUpCatalogName(NGC + ngcDupSuffix, out var ngcDup))
                        {
                            AddLookupEntry(_crossLookupTable, indexEntry, AbbreviationToEnumMember<CatalogIndex>(ngcDup));
                        }
                        if (csvReader.TryGetField<string>(IC, out var icDupSuffix) && TryGetCleanedUpCatalogName(IC + icDupSuffix, out var icDup))
                        {
                            AddLookupEntry(_crossLookupTable, indexEntry, AbbreviationToEnumMember<CatalogIndex>(icDup));
                        }
                        if (csvReader.TryGetField<string>(M, out var messierDupSuffix) && TryGetCleanedUpCatalogName(M + messierDupSuffix, out var mDup))
                        {
                            AddLookupEntry(_crossLookupTable, indexEntry, AbbreviationToEnumMember<CatalogIndex>(mDup));
                        }
                    }
                    else if (csvReader.TryGetField<string>(M, out var messierSuffix) && TryGetCleanedUpCatalogName(M + messierSuffix, out var messierEntry))
                    {
                        // Adds Messier to NGC/IC entry lookup
                        AddLookupEntry(_crossLookupTable, AbbreviationToEnumMember<CatalogIndex>(messierEntry), indexEntry);
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

        static readonly Regex ExtendedCatalogEntryPattern = new(@"^(N|I|NGC|IC) ([0-9]{1,4}) (?:(N(?:ED)? ([0-9]{1,2})) | [_]?([A-Z]{1,2}))$",
            RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace);

        public static bool TryGetCleanedUpCatalogName(string? input, [NotNullWhen(true)] out string? cleanedUp)
        {
            var trimmedInput = input?.Replace(" ", "");
            if (string.IsNullOrEmpty(trimmedInput) || trimmedInput.Length < 2)
            {
                cleanedUp = default;
                return false;
            }

            var (chars, digits) = trimmedInput[0] switch
            {
                'H' => trimmedInput[1] == 'C'
                    ? (new char[7] { 'H', 'C', 'G', '0', '0', '0', '0' }, 4) // HGC entry
                    : (new char[3] { 'H', '0', '0' }, 2), // H entry
                'N' => (new char[5] { 'N', '0', '0', '0', '0' }, 4), // Simple NGC entry
                'I' => (new char[5] { 'I', '0', '0', '0', '0' }, 4), // Simple IC entry
                'M' => trimmedInput[1] == 'e' && trimmedInput.Length > 2 && trimmedInput[2] == 'l'
                    ? (new char[6] { 'M', 'e', 'l', '0', '0', '0' }, 3) // Mel entry
                    : (new char[4] { 'M', '0', '0', '0' }, 3), // Messier entry
                'B' => (new char[4] { 'B', '0', '0', '0' }, 3), // B entry
                'C' => trimmedInput[1] == 'l'
                    ? (new char[5] { 'C', 'l', '0', '0', '0' }, 3) // Cl entry
                    : (new char[4] { 'C', '0', '0', '0' }, 3), // C entry
                'U' => (new char[5] { 'U', '0', '0', '0', '0' }, 5), // UGC entry
                'E' => (new char[8] { 'E', '0', '0', '0', '-', '0', '0', '0'}, 7), // ESO entry
                _ => (Array.Empty<char>(), 0)
            };

            if (digits <= 0)
            {
                cleanedUp = default;
                return false;
            }

            // test for slow path
            var firstDigit = trimmedInput.IndexOfAny(new[] { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9' });
            if (trimmedInput[0] != 'M' && firstDigit > 0 && trimmedInput.IndexOfAny(new[] { 'A', 'B', 'C', 'D', 'E', 'F', 'S', 'W', 'N' }, firstDigit) > firstDigit)
            {
                var match = ExtendedCatalogEntryPattern.Match(trimmedInput);
                if (match.Success && match.Groups.Count == 6)
                {
                    var NorI = match.Groups[1].ValueSpan[0..1].ToString();
                    var number = match.Groups[2].Value;
                    if (match.Groups[5].Length == 0)
                    {
                        var nedGroupSuffix = match.Groups[4].Value;
                        cleanedUp = string.Concat(NorI, number, 'N', nedGroupSuffix);
                        return true;
                    }
                    else
                    {
                        var letterSuffix = match.Groups[5].Value;
                        cleanedUp = string.Concat(NorI, number, '_', letterSuffix);
                        return true;
                    }
                }
                else
                {
                    cleanedUp = default;
                    return false;
                }
            }
            else
            {
                int foundDigits = 0;
                for (var i = 0; i < digits; i++)
                {
                    var fromRight = trimmedInput.Length - i - 1;
                    if (fromRight <= 0)
                    {
                        break;
                    }
                    if (chars[chars.Length - 1 - i] != trimmedInput[fromRight] && trimmedInput[fromRight] is < '0' or > '9')
                    {
                        break;
                    }

                    foundDigits++;
                    chars[chars.Length - 1 - i] = trimmedInput[fromRight];
                }

                if (foundDigits == 0)
                {
                    cleanedUp = default;
                    return false;
                }
                else
                {
                    cleanedUp = new string(chars);
                    return true;
                }
            }
        }
    }
}
