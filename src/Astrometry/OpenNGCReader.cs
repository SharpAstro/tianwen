using CsvHelper;
using CsvHelper.Configuration;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using static Astap.Lib.EnumHelper;

namespace Astap.Lib.Astrometry
{

    public record DeepSkyObject(CatalogIndex Index, ObjectType ObjType, string RA, string Dec, Constellation Constellation);

    public class OpenNGCReader
    {
        const string NGC = nameof(NGC);
        const string IC = nameof(IC);
        const string M = nameof(M);

        private readonly Dictionary<CatalogIndex, DeepSkyObject> _objectsByIndex = new(14000);
        private readonly Dictionary<CatalogIndex, CatalogIndex[]> _crossLookupTable = new(900);
        private readonly Dictionary<string, CatalogIndex[]> _objectsByCommonName = new(200);

        public OpenNGCReader() { }

        public bool TryLookupByIndex(string name, [NotNullWhen(true)] out DeepSkyObject? deepSkyObject)
        {
            if (TryGetCleanedUpCatalogName(name, out var cleanedUp))
            {
                return TryLookupByIndex(AbbreviationToEnumMember<CatalogIndex>(cleanedUp), out deepSkyObject);
            }
            else
            {
                deepSkyObject = default;
                return false;
            }
        }

        private bool TryLookupByIndex(CatalogIndex indexEntry, [NotNullWhen(true)] out DeepSkyObject? deepSkyObject)
            => _objectsByIndex.TryGetValue(indexEntry, out deepSkyObject);

        public async Task<(int processed, int failed)> ReadEmbeddedDataAsync()
        {
            int processed = 0;
            int failed = 0;
            var assy = typeof(OpenNGCReader).Assembly;
            var ngcCSV = assy.GetManifestResourceNames().FirstOrDefault(p => p.EndsWith("NGC.csv"));
            if (ngcCSV is null || assy.GetManifestResourceStream(ngcCSV) is not Stream ngcCSVStream)
            {
                return (processed, failed);
            }

            using var ngcCSVTextReader = new StreamReader(ngcCSVStream, new UTF8Encoding(false));
            using var ngcCSVReader = new CsvReader(ngcCSVTextReader, new CsvConfiguration(CultureInfo.InvariantCulture) { Delimiter = ";" });

            if (!await ngcCSVReader.ReadAsync() || !ngcCSVReader.ReadHeader())
            {
                return (processed, failed);
            }

            while (await ngcCSVReader.ReadAsync())
            {
                if (ngcCSVReader.TryGetField<string>("Name", out var entryName)
                    && ngcCSVReader.TryGetField<string>("Type", out var objectTypeAbbr)
                    && ngcCSVReader.TryGetField<string>("RA", out var raHMS)
                    && ngcCSVReader.TryGetField<string>("Dec", out var decDMS)
                    && ngcCSVReader.TryGetField<string>("Const", out var constAbbr)
                    && TryGetCleanedUpCatalogName(entryName, out var cleanedUpName)
                )
                {
                    var objectType = AbbreviationToEnumMember<ObjectType>(objectTypeAbbr);
                    var @const = AbbreviationToEnumMember<Constellation>(constAbbr);
                    var indexEntry = AbbreviationToEnumMember<CatalogIndex>(cleanedUpName);
                    _objectsByIndex[indexEntry] = new DeepSkyObject(indexEntry, objectType, "", "", @const);

                    if (objectType == ObjectType.Duplicate)
                    {
                        if (ngcCSVReader.TryGetField<string>(NGC, out var ngcDupSuffix) && TryGetCleanedUpCatalogName(NGC + ngcDupSuffix, out var ngcDup))
                        {
                            AddLookupEntry(_crossLookupTable, indexEntry, AbbreviationToEnumMember<CatalogIndex>(ngcDup));
                        }
                        if (ngcCSVReader.TryGetField<string>(IC, out var icDupSuffix) && TryGetCleanedUpCatalogName(IC + icDupSuffix, out var icDup))
                        {
                            AddLookupEntry(_crossLookupTable, indexEntry, AbbreviationToEnumMember<CatalogIndex>(icDup));
                        }
                    }

                    if (ngcCSVReader.TryGetField<int>(M, out var messierEntry))
                    {
                        AddLookupEntry(_crossLookupTable, AbbreviationToEnumMember<CatalogIndex>($"{M[0]}{messierEntry:000}"), indexEntry);
                    }

                    if (ngcCSVReader.TryGetField<string>("Common names", out var commonNamesEntry))
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

        static readonly Regex ExtendedCatalogEntryPattern = new(@"^(N|I|NGC|IC) \s* ([0-9]{1,4}) \s* (?:(N(?:ED)? \s* ([0-9]{1,2})) | [_]?([A-Z]{1,2}))$",
            RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace);

        public static bool TryGetCleanedUpCatalogName(string? input, [NotNullWhen(true)] out string? cleanedUp)
        {
            var trimmedInput = input?.Trim();
            if (string.IsNullOrEmpty(trimmedInput))
            {
                cleanedUp = default;
                return false;
            }

            var (chars, digits) = trimmedInput[0] switch
            {
                'N' => (new char[5] { 'N', '0', '0', '0', '0' }, 4),
                'I' => (new char[5] { 'I', '0', '0', '0', '0' }, 4),
                'M' => (new char[4] { 'M', '0', '0', '0' }, 3),
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
                    if (trimmedInput[fromRight] is < '0' or > '9')
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
