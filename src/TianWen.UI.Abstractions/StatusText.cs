using System;
using System.Globalization;
using System.Text;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Turns an exception message into something safe to paint in a one-line status bar.
    ///
    /// The motivating case: FITS.Lib reports a bad magic as <c>Not FITS format at {offset}:{cbuf}</c>,
    /// where <c>cbuf</c> is the RAW BYTES it read. Hand it a TIFF and the status bar gets
    /// <c>Not FITS format at 0:II*</c> followed by whatever the next bytes decode to -- unprintable
    /// control characters and .notdef boxes. The useful half of that message is the first clause; the
    /// rest is a hex dump wearing a font.
    ///
    /// So this is deliberately NOT about that one exception. Any library is entitled to put bytes,
    /// newlines, or a stack-trace-length string in <c>Message</c>, and a status bar is a single line of
    /// a proportional font -- the boundary between "an exception" and "a line of UI text" is where the
    /// conversion belongs, not the individual call site.
    /// </summary>
    public static class StatusText
    {
        /// <summary>
        /// Cap for a status line. Generous enough for a real sentence plus a file name, short enough
        /// that a runaway message cannot push the rest of the bar off the widget.
        /// </summary>
        public const int MaxLength = 200;

        /// <summary>Sanitised <see cref="Exception.Message"/>, safe for a status line.</summary>
        public static string FromException(Exception ex) => Sanitise(ex.Message);

        /// <summary>
        /// Collapses every run of whitespace (including newlines and tabs) to one space, DROPS
        /// unprintable characters outright, and truncates on a word boundary.
        ///
        /// Dropping rather than substituting is the point: a replacement character per stray byte
        /// produces a row of boxes that looks like a font bug, whereas dropping them leaves the
        /// message's readable prefix reading as a sentence. Printable non-ASCII is KEPT -- a file name
        /// with an accent or a CJK path is legitimate text, and stripping to ASCII would corrupt it.
        /// </summary>
        public static string Sanitise(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var sb = new StringBuilder(Math.Min(text.Length, MaxLength + 16));
            var pendingSpace = false;
            foreach (var rune in text.EnumerateRunes())
            {
                if (Rune.IsWhiteSpace(rune))
                {
                    // Note the ordering: a space is only emitted once something printable follows it,
                    // which trims leading and trailing whitespace without a second pass.
                    pendingSpace = sb.Length > 0;
                    continue;
                }

                if (!IsPrintable(rune))
                {
                    continue;
                }

                if (pendingSpace)
                {
                    sb.Append(' ');
                    pendingSpace = false;
                }

                if (sb.Length + rune.Utf16SequenceLength > MaxLength)
                {
                    return TrimToWord(sb) + "...";
                }

                sb.Append(rune.ToString());
            }

            return sb.ToString();
        }

        /// <summary>
        /// Printable means "the font could plausibly draw it". Control and format characters cannot be
        /// drawn; surrogates and private-use are excluded because a raw byte pair readily lands there
        /// and would paint as .notdef. <see cref="UnicodeCategory.OtherNotAssigned"/> is the common
        /// case for binary reinterpreted as text.
        /// </summary>
        private static bool IsPrintable(Rune rune) => Rune.GetUnicodeCategory(rune) switch
        {
            UnicodeCategory.Control => false,
            UnicodeCategory.Format => false,
            UnicodeCategory.Surrogate => false,
            UnicodeCategory.PrivateUse => false,
            UnicodeCategory.OtherNotAssigned => false,
            _ => true,
        };

        /// <summary>Back off to the last space so a truncation does not cut a word in half.</summary>
        private static string TrimToWord(StringBuilder sb)
        {
            var s = sb.ToString();
            var lastSpace = s.LastIndexOf(' ');
            // Only honour a word boundary in the last quarter; a message whose single token is longer
            // than the cap would otherwise be trimmed back to almost nothing.
            return lastSpace > MaxLength * 3 / 4 ? s[..lastSpace] : s;
        }
    }
}
