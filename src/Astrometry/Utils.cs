using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Astap.Lib.Astrometry
{
    public static class Utils
    {
        public static double HMSToDegree(string? hms)
        {
            const double minToDeg = 15 / 60.0;
            const double secToDeg = 15 / 3600.0;

            var split = hms?.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            if (split?.Length == 3
                && double.TryParse(split[0], NumberStyles.Number, CultureInfo.InvariantCulture, out var hours)
                && double.TryParse(split[1], NumberStyles.Number, CultureInfo.InvariantCulture, out var min)
                && double.TryParse(split[2], NumberStyles.Number, CultureInfo.InvariantCulture, out var sec)
            )
            {
                return (15 * hours) + (min * minToDeg) + (sec * secToDeg);
            }
            else
            {
                return double.NaN;
            }
        }

        public static double DMSToDegree(string dms)
        {
            const double minToDeg = 15 / 60.0;
            const double secToDeg = 15 / 3600.0;

            var split = dms?.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            if (split?.Length == 3
                && double.TryParse(split[0], NumberStyles.Number, CultureInfo.InvariantCulture, out var deg)
                && double.TryParse(split[1], NumberStyles.Number, CultureInfo.InvariantCulture, out var min)
                && double.TryParse(split[2], NumberStyles.Number, CultureInfo.InvariantCulture, out var sec)
            )
            {
                return deg + (min * minToDeg) + (sec * secToDeg);
            }
            else
            {
                return double.NaN;
            }
        }

        static readonly Regex ExtendedCatalogEntryPattern = new(@"^(N|I|NGC|IC) ([0-9]{1,4}) (?:(N(?:ED)? ([0-9]{1,2})) | [_]?([A-Z]{1,2}))$",
            RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace);

        public static bool TryGetCleanedUpCatalogName(string? input, out CatalogIndex catalogIndex)
        {
            var (chars, digits, catalog) = GuessCatalogFormat(input, out var trimmedInput);

            if (digits <= 0 || catalog is null)
            {
                catalogIndex = 0;
                return false;
            }

            string? cleanedUp;
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
                    }
                    else
                    {
                        var letterSuffix = match.Groups[5].Value;
                        cleanedUp = string.Concat(NorI, number, '_', letterSuffix);
                    }
                }
                else
                {
                    cleanedUp = null;
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

                cleanedUp = foundDigits > 0 ? new string(chars) : null;
            }

            if (cleanedUp is not null)
            {
                catalogIndex = EnumHelper.AbbreviationToEnumMember<CatalogIndex>(cleanedUp);
                return true;
            }
            else
            {
                catalogIndex = 0;
                return false;
            }
        }

        /// <summary>
        /// Tries to guess the <see cref="Catalog"/> and format from user input.
        /// </summary>
        /// <param name="input">input to guess catalog format from</param>
        /// <param name="trimmedInput"></param>
        /// <returns>(catalog index template, number of free digits, guessed <see cref="Catalog"/>)</returns>
        public static (char[] template, int digits, Catalog? catalog) GuessCatalogFormat(string? input, out string trimmedInput)
        {
            trimmedInput = input?.Replace(" ", "") ?? "";
            if (string.IsNullOrEmpty(trimmedInput) || trimmedInput.Length < 2)
            {
                return (Array.Empty<char>(), 0, null);
            }

            return trimmedInput[0] switch
            {
                'H' => trimmedInput[1] switch
                {
                    'C' => (new char[7] { 'H', 'C', 'G', '0', '0', '0', '0' }, 4, Catalog.HCG),
                    'R' => (new char[6] { 'H', 'R', '0', '0', '0', '0'}, 4, Catalog.HR),
                    'D' => (new char[8] { 'H', 'D', '0', '0', '0', '0', '0', '0'}, 6, Catalog.HD),
                    _ => (new char[3] { 'H', '0', '0' }, 2, Catalog.H)
                },
                'N' => (new char[5] { 'N', '0', '0', '0', '0' }, 4, Catalog.NGC),
                'I' => (new char[5] { 'I', '0', '0', '0', '0' }, 4, Catalog.IC),
                'M' => trimmedInput[1] == 'e' && trimmedInput.Length > 2 && trimmedInput[2] == 'l'
                    ? (new char[6] { 'M', 'e', 'l', '0', '0', '0' }, 3, Catalog.Melotte)
                    : (new char[4] { 'M', '0', '0', '0' }, 3, Catalog.Messier),
                'B' => (new char[4] { 'B', '0', '0', '0' }, 3, Catalog.Barnard),
                'C' => trimmedInput[1] == 'l' || trimmedInput[1] == 'r'
                    ? (new char[5] { 'C', 'r', '0', '0', '0' }, 3, Catalog.Collinder)
                    : (new char[4] { 'C', '0', '0', '0' }, 3, Catalog.Caldwell),
                'U' => (new char[5] { 'U', '0', '0', '0', '0' }, 5, Catalog.UGC),
                'E' => (new char[8] { 'E', '0', '0', '0', '-', '0', '0', '0' }, 7, Catalog.ESO),
                _ => (Array.Empty<char>(), 0, null)
            };
        }
    }
}
