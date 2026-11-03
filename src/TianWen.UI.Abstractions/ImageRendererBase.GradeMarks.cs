using System;
using System.Collections.Immutable;
using System.Globalization;
using DIR.Lib;
using TianWen.Lib.Imaging.Calibration;

namespace TianWen.UI.Abstractions
{
    partial class ImageRendererBase<TSurface>
    {
        // The grade's marks (#1951): a baked mask (never a glyph from a font, which a colour face cannot tint and a host
        // without the face draws as nothing), declared as a fixed-size Fill whose KEY names the mark and its ink, so the
        // strip's and the file list's fill painters draw it with no state beside the tree.
        private const string GradeMarkKeyPrefix = "grade-mark:";

        // The file list's marks in COLOUR (the owner, 2026-11-03: "emoji is a bit clearer graphically"): an emoji drawn from the
        // viewer's emoji face, where nothing needs the mark to change colour. The verdict buttons stay baked (they light and dim),
        // and so does every mark in Night, whose red-on-black palette a colour glyph would break, or with no emoji face resolved.
        private const string GradeEmojiKeyPrefix = "grade-emoji:";

        /// <summary>The marks a frame's grade can carry, in the order a row shows them.</summary>
        internal enum GradeMark { Rejected, Kept, Cloud, Twilight, Streaky, Defocused, Obstruction }

        private static ImmutableArray<IconBaker.CoverageMask> MasksOf(GradeMark mark) => mark switch
        {
            GradeMark.Rejected => BakedIcons.ThumbsDown,
            GradeMark.Kept => BakedIcons.ThumbsUp,
            GradeMark.Cloud => BakedIcons.Cloud,
            GradeMark.Streaky => BakedIcons.Dizzy,
            GradeMark.Defocused => BakedIcons.Eyeglasses,
            GradeMark.Obstruction => BakedIcons.Evergreen,
            _ => BakedIcons.Sun,
        };

        /// <summary>A <paramref name="size"/>-unit square that draws <paramref name="mark"/> in <paramref name="ink"/>.</summary>
        internal static Layout.Node GradeMarkNode(GradeMark mark, float size, RGBAColor32 ink)
            => Layout.Builder.Fill(key: string.Create(CultureInfo.InvariantCulture, $"{GradeMarkKeyPrefix}{mark}:{ink.Red:X2}{ink.Green:X2}{ink.Blue:X2}{ink.Alpha:X2}"))
                .WFixed(size).HFixed(size);

        /// <summary>The mark the owner's rejection cause draws as, a bare BAD_ the thumbs down.</summary>
        internal static GradeMark MarkOf(CaptureRejection.Cause cause) => cause switch
        {
            CaptureRejection.Cause.Cloud => GradeMark.Cloud,
            CaptureRejection.Cause.Streaky => GradeMark.Streaky,
            CaptureRejection.Cause.Defocused => GradeMark.Defocused,
            CaptureRejection.Cause.Obstruction => GradeMark.Obstruction,
            _ => GradeMark.Rejected,
        };

        /// <summary>
        /// The marks a file-list row carries: the cause its name records (BAD_CLOUD_ a cloud, a bare BAD_ the thumbs down),
        /// then what its grade's verdict held it down for; a cloud the owner named and the grade found is drawn once.
        /// </summary>
        internal static ImmutableArray<GradeMark> MarksFor(string fileName, LightGradeVerdict? verdict)
        {
            var marks = ImmutableArray.CreateBuilder<GradeMark>(3);
            if (CaptureRejection.CauseOf(fileName) is { } cause)
            {
                marks.Add(MarkOf(cause));
            }
            if (verdict is { } v)
            {
                if ((v.Causes & GradeCause.Cloud) != 0 && !marks.Contains(GradeMark.Cloud))
                {
                    marks.Add(GradeMark.Cloud);
                }
                if ((v.Causes & (GradeCause.Sunrise | GradeCause.Sunset)) != 0)
                {
                    marks.Add(GradeMark.Twilight);
                }
            }
            return marks.ToImmutable();
        }

        /// <summary>The emoji a mark draws as in colour; a twilight says which one, as a baked mask cannot (its scene bakes solid).</summary>
        internal static string EmojiOf(GradeMark mark, LightGradeVerdict? verdict) => mark switch
        {
            GradeMark.Rejected => "👎",                    // thumbs down
            GradeMark.Kept => "👍",                        // thumbs up
            GradeMark.Cloud => "☁️",                     // cloud, emoji presentation asked for
            GradeMark.Streaky => "💫",                     // dizzy: a star with its trail
            GradeMark.Defocused => "👓",                   // eyeglasses
            GradeMark.Obstruction => "🌲",                 // evergreen tree
            _ => verdict is { Causes: var c } && (c & GradeCause.Sunset) != 0
                ? "🌇"                                     // sunset
                : "🌅",                                    // sunrise
        };

        /// <summary>
        /// A row's marks as nodes, each saying its cause on hover: in colour (<see cref="EmojiOf"/>) when <paramref name="colour"/>,
        /// else baked and inked as the row's name is.
        /// </summary>
        internal static ImmutableArray<Layout.Node> MarkNodes(ImmutableArray<GradeMark> marks, LightGradeVerdict? verdict, float size, RGBAColor32 ink,
            bool colour = false)
        {
            var nodes = ImmutableArray.CreateBuilder<Layout.Node>(marks.Length);
            foreach (var mark in marks)
            {
                var node = colour
                    ? Layout.Builder.Fill(key: GradeEmojiKeyPrefix + EmojiOf(mark, verdict)).WFixed(size).HFixed(size)
                    : GradeMarkNode(mark, size, ink);
                nodes.Add(node.WithTooltip(TooltipOf(mark, verdict)));
            }
            return nodes.MoveToImmutable();
        }

        /// <summary>Whether this frame's file list draws its marks in colour: an emoji face resolved and the theme not Night.</summary>
        private bool GradeMarksInColour => !string.IsNullOrEmpty(EmojiFontPath) && GuiTheme.State != UiThemeState.Night;

        /// <summary>What a mark says on hover: the cause in words.</summary>
        internal static string TooltipOf(GradeMark mark, LightGradeVerdict? verdict) => mark switch
        {
            GradeMark.Rejected => "Rejected: named BAD_, so a stack leaves it out",
            GradeMark.Streaky => "Rejected for streaky stars (BAD_STREAKY_)",
            GradeMark.Defocused => "Rejected as defocused (BAD_DEFOCUSED_)",
            GradeMark.Obstruction => "Rejected for an obstruction (BAD_OBSTRUCTION_)",
            GradeMark.Kept => "Kept",
            GradeMark.Cloud => verdict is { } v && (v.Causes & GradeCause.Cloud) != 0
                ? LightGrading.CloudInWords(v)
                : "Rejected as cloud (BAD_CLOUD_)",
            _ => verdict is { Causes: var c } && (c & GradeCause.Sunrise) != 0
                ? "Sunrise: taken past astronomical dark, the Sun rising"
                : "Sunset: taken past astronomical dark, the Sun setting",
        };

        /// <summary>Draws a grade mark's fill; false for any other fill, which the caller draws as before.</summary>
        private bool DrawGradeMark(Layout.Content.Fill fill, RectF32 rect)
        {
            if (fill.Key is not { } key)
            {
                return false;
            }
            if (key.StartsWith(GradeEmojiKeyPrefix, StringComparison.Ordinal))
            {
                var box = MathF.Min(rect.Width, rect.Height);
                DrawGlyphCentred(key[GradeEmojiKeyPrefix.Length..], EmojiFontPath ?? string.Empty, rect.X, rect.Y, rect.Width, rect.Height,
                    box * 0.85f, new RGBAColor32(255, 255, 255, 255));
                return true;
            }
            if (!key.StartsWith(GradeMarkKeyPrefix, StringComparison.Ordinal))
            {
                return false;
            }
            var parts = key.AsSpan(GradeMarkKeyPrefix.Length);
            var colon = parts.IndexOf(':');
            if (colon < 0 || !Enum.TryParse<GradeMark>(parts[..colon], out var mark)
                || !uint.TryParse(parts[(colon + 1)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgba))
            {
                return true;
            }
            var ink = new RGBAColor32((byte)(rgba >> 24), (byte)(rgba >> 16), (byte)(rgba >> 8), (byte)rgba);
            var size = MathF.Min(rect.Width, rect.Height);
            DrawCoverageMask(IconBaker.NearestSize(MasksOf(mark), size),
                rect.X + (rect.Width - size) / 2f, rect.Y + (rect.Height - size) / 2f, size, ink);
            return true;
        }
    }
}
