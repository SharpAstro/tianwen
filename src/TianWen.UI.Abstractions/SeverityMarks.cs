using System;
using System.Collections.Immutable;
using System.Globalization;
using DIR.Lib;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// The FORM a warning or an error carries beside its colour (#2025): a baked triangle for a warning and a baked cross
    /// for an error, so a warning is told from an accented control (the accent is the logo's orange now, and Warn an amber)
    /// by shape as well as hue. ONE component for the notification rows and the GUI status bar, never a copy per surface.
    /// </summary>
    /// <remarks>
    /// DIR.Lib's <see cref="Layout.IconKind"/> has no warning or error member, so the marks are baked in this repo
    /// (<c>icons.recipe</c>) and declared as a keyed <see cref="Layout.Content.Fill"/>, as the grade marks are: the host's
    /// <c>drawFill</c> hands the key to <see cref="TryResolve"/> and draws the mask it returns with its own
    /// <c>DrawCoverageMask</c>. Never a symbol character in a <c>Text</c> run, which a missing face draws as nothing.
    /// An Info row shows no mark and reserves the same slot (<see cref="Slot"/>), so the column after it never moves.
    /// </remarks>
    public static class SeverityMarks
    {
        /// <summary>The prefix of a severity mark's Fill key; the rest is the severity and the ink as RRGGBBAA hex.</summary>
        public const string KeyPrefix = "severity-mark:";

        /// <summary>Whether <paramref name="severity"/> carries a mark. Info does not: it is the ordinary row.</summary>
        public static bool HasMark(NotificationSeverity severity) => severity is NotificationSeverity.Warning or NotificationSeverity.Error;

        /// <summary>The ink a severity is drawn in, from the palette (Warn, Error; Info is the palette's Info).</summary>
        public static RGBAColor32 InkOf(NotificationSeverity severity) => severity switch
        {
            NotificationSeverity.Error => GuiTheme.Palette.Error,
            NotificationSeverity.Warning => GuiTheme.Palette.Warn,
            _ => GuiTheme.Palette.Info,
        };

        /// <summary>
        /// A <paramref name="width"/>-unit-wide slot, as tall as its row: the mark for a Warning or an Error, an empty
        /// spacer of the same width for anything else, so a row without a mark lines up with one that has it.
        /// </summary>
        public static Layout.Node Slot(NotificationSeverity severity, float width)
            => HasMark(severity)
                ? Mark(severity, width, InkOf(severity))
                : Layout.Builder.Spacer().WFixed(width).HStar();

        /// <summary>
        /// The design-unit width <see cref="Lead"/> takes: the mark's slot plus the gap after it. The ONE statement of that
        /// footprint, so a caller that truncates its text to the room left reads it here instead of re-adding the two.
        /// </summary>
        public static float LeadWidth(float markWidth, float gap) => markWidth + gap;

        /// <summary>The mark slot for <paramref name="severity"/> followed by a <paramref name="gap"/>, <see cref="LeadWidth"/> wide in all.</summary>
        public static Layout.Node Lead(NotificationSeverity severity, float markWidth, float gap)
            => Layout.Builder.HStack(Slot(severity, markWidth), Layout.Builder.Spacer().WFixed(gap).HStar())
                .WFixed(LeadWidth(markWidth, gap)).HStar();

        /// <summary>
        /// The square a mark is drawn in inside its Fill <paramref name="rect"/> (the slot's width, centred on the row), with the
        /// mask and ink to draw it: the one painter body for every host's <c>drawFill</c>. False when <paramref name="key"/> is not
        /// a severity mark.
        /// </summary>
        public static bool TryPlace(string? key, RectF32 rect, out IconBaker.CoverageMask mask, out RectF32 square, out RGBAColor32 ink)
        {
            var size = MathF.Min(rect.Width, rect.Height);
            square = new RectF32(rect.X + (rect.Width - size) / 2f, rect.Y + (rect.Height - size) / 2f, size, size);
            mask = default;
            ink = default;
            return key is not null && TryResolve(key, size, out mask, out ink);
        }

        /// <summary>A <paramref name="width"/>-unit-wide, row-tall Fill drawing the mark of <paramref name="severity"/> in <paramref name="ink"/>.</summary>
        public static Layout.Node Mark(NotificationSeverity severity, float width, RGBAColor32 ink)
            => Layout.Builder.Fill(key: string.Create(CultureInfo.InvariantCulture,
                    $"{KeyPrefix}{severity}:{ink.Red:X2}{ink.Green:X2}{ink.Blue:X2}{ink.Alpha:X2}"))
                .WFixed(width).HStar();

        /// <summary>
        /// Reads a Fill <paramref name="key"/> back into the mask to draw (the nearest baked size to
        /// <paramref name="size"/> pixels) and its ink. False when the key is not a severity mark.
        /// </summary>
        public static bool TryResolve(string key, float size, out IconBaker.CoverageMask mask, out RGBAColor32 ink)
        {
            mask = default;
            ink = default;
            if (!key.StartsWith(KeyPrefix, StringComparison.Ordinal))
            {
                return false;
            }
            var parts = key.AsSpan(KeyPrefix.Length);
            var colon = parts.IndexOf(':');
            if (colon < 0 || !Enum.TryParse<NotificationSeverity>(parts[..colon], out var severity)
                || !uint.TryParse(parts[(colon + 1)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgba))
            {
                return false;
            }
            ImmutableArray<IconBaker.CoverageMask> masks = severity == NotificationSeverity.Error ? BakedIcons.Cross : BakedIcons.Warning;
            mask = IconBaker.NearestSize(masks, size);
            ink = new RGBAColor32((byte)(rgba >> 24), (byte)(rgba >> 16), (byte)(rgba >> 8), (byte)rgba);
            return true;
        }
    }
}
