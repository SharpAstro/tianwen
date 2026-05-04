using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace TianWen.Lib.IO
{
    /// <summary>
    /// Helpers for reading the ASCII-separated catalog format produced by
    /// <c>tools/preprocess-catalog.ps1</c>. The format uses three control bytes:
    /// <list type="bullet">
    /// <item><description><c>0x1D</c> (GS) terminates / separates records</description></item>
    /// <item><description><c>0x1E</c> (RS) separates fields within a record</description></item>
    /// <item><description><c>0x1F</c> (US) separates sub-items inside a variable-length field (e.g. SIMBAD <c>Ids[]</c>)</description></item>
    /// </list>
    /// None of these bytes appear in any catalog string value, so no escaping is needed.
    /// All numeric fields are encoded in invariant culture (doubles via <c>G17</c> for round-trip).
    /// Empty string in a numeric field means <see langword="null"/>.
    /// </summary>
    internal static class AsciiRecordReader
    {
        public const byte GroupSeparator = 0x1D;
        public const byte RecordSeparator = 0x1E;
        public const byte UnitSeparator = 0x1F;

        /// <summary>
        /// Iterates record slices in <paramref name="payload"/>. Each yielded span
        /// is the bytes between two <see cref="GroupSeparator"/>s (exclusive),
        /// trimmed of any final empty record produced by a trailing GS.
        /// </summary>
        public static IEnumerable<ReadOnlyMemory<byte>> EnumerateRecords(ReadOnlyMemory<byte> payload)
        {
            // We yield ReadOnlyMemory because foreach over a method returning
            // ref-struct spans doesn't play well with iterator state machines.
            var remaining = payload;
            while (remaining.Length > 0)
            {
                var idx = remaining.Span.IndexOf(GroupSeparator);
                if (idx < 0)
                {
                    yield return remaining;
                    yield break;
                }
                yield return remaining[..idx];
                remaining = remaining[(idx + 1)..];
            }
        }

        /// <summary>
        /// Slices one field off the front of <paramref name="record"/> (up to the
        /// next <see cref="RecordSeparator"/>) and advances <paramref name="record"/>
        /// past the separator. The final field of a record has no trailing RS;
        /// in that case the whole remainder is returned and <paramref name="record"/>
        /// becomes empty.
        /// </summary>
        public static ReadOnlySpan<byte> TakeField(ref ReadOnlySpan<byte> record)
        {
            var idx = record.IndexOf(RecordSeparator);
            if (idx < 0)
            {
                var all = record;
                record = default;
                return all;
            }
            var head = record[..idx];
            record = record[(idx + 1)..];
            return head;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static string ReadString(ReadOnlySpan<byte> field) =>
            field.IsEmpty ? string.Empty : Encoding.UTF8.GetString(field);

        public static double ReadDouble(ReadOnlySpan<byte> field)
        {
            // Invariant-culture double parse from UTF-8 bytes. The catalog encoder
            // uses G17 so all values round-trip exactly.
            if (field.IsEmpty)
            {
                throw new FormatException("Expected a double but field was empty.");
            }
            return double.Parse(field, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double? ReadNullableDouble(ReadOnlySpan<byte> field) =>
            field.IsEmpty ? null : ReadDouble(field);

        /// <summary>
        /// Splits <paramref name="field"/> by <see cref="UnitSeparator"/> into a
        /// freshly allocated <see cref="string"/>[]. Empty input yields an empty array.
        /// </summary>
        public static string[] ReadStringArray(ReadOnlySpan<byte> field)
        {
            if (field.IsEmpty)
            {
                return [];
            }

            // Two-pass: count separators, allocate exactly, then fill.
            var count = 1;
            for (var i = 0; i < field.Length; i++)
            {
                if (field[i] == UnitSeparator) count++;
            }

            var result = new string[count];
            var slot = 0;
            var cursor = field;
            while (true)
            {
                var idx = cursor.IndexOf(UnitSeparator);
                if (idx < 0)
                {
                    result[slot++] = ReadString(cursor);
                    break;
                }
                result[slot++] = ReadString(cursor[..idx]);
                cursor = cursor[(idx + 1)..];
            }
            return result;
        }
    }
}
