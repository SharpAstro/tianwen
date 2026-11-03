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

        /// <summary>The marks a frame's grade can carry, in the order a row shows them.</summary>
        internal enum GradeMark { Rejected, Kept, Cloud, Twilight }

        private static ImmutableArray<IconBaker.CoverageMask> MasksOf(GradeMark mark) => mark switch
        {
            GradeMark.Rejected => BakedIcons.ThumbsDown,
            GradeMark.Kept => BakedIcons.ThumbsUp,
            GradeMark.Cloud => BakedIcons.Cloud,
            _ => BakedIcons.Sun,
        };

        /// <summary>A <paramref name="size"/>-unit square that draws <paramref name="mark"/> in <paramref name="ink"/>.</summary>
        internal static Layout.Node GradeMarkNode(GradeMark mark, float size, RGBAColor32 ink)
            => Layout.Builder.Fill(key: string.Create(CultureInfo.InvariantCulture, $"{GradeMarkKeyPrefix}{mark}:{ink.Red:X2}{ink.Green:X2}{ink.Blue:X2}{ink.Alpha:X2}"))
                .WFixed(size).HFixed(size);

        /// <summary>The marks a file-list row carries: its name's rejection, then what its grade's verdict held it down for.</summary>
        internal static ImmutableArray<GradeMark> MarksFor(string fileName, LightGradeVerdict? verdict)
        {
            var marks = ImmutableArray.CreateBuilder<GradeMark>(3);
            if (CaptureRejection.IsRejectedAtCapture(fileName))
            {
                marks.Add(GradeMark.Rejected);
            }
            if (verdict is { } v)
            {
                if ((v.Causes & GradeCause.Cloud) != 0)
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

        /// <summary>A row's marks as nodes, each inked as the row's name is and saying its cause on hover.</summary>
        internal static ImmutableArray<Layout.Node> MarkNodes(ImmutableArray<GradeMark> marks, LightGradeVerdict? verdict, float size, RGBAColor32 ink)
        {
            var nodes = ImmutableArray.CreateBuilder<Layout.Node>(marks.Length);
            foreach (var mark in marks)
            {
                nodes.Add(GradeMarkNode(mark, size, ink).WithTooltip(TooltipOf(mark, verdict)));
            }
            return nodes.MoveToImmutable();
        }

        /// <summary>What a mark says on hover: the cause in words.</summary>
        internal static string TooltipOf(GradeMark mark, LightGradeVerdict? verdict) => mark switch
        {
            GradeMark.Rejected => "Rejected: named BAD_, so a stack leaves it out",
            GradeMark.Kept => "Kept",
            GradeMark.Cloud => verdict is { } v ? LightGrading.CloudInWords(v) : "Cloud",
            _ => verdict is { Causes: var c } && (c & GradeCause.Sunrise) != 0
                ? "Sunrise: taken past astronomical dark, the Sun rising"
                : "Sunset: taken past astronomical dark, the Sun setting",
        };

        /// <summary>Draws a grade mark's fill; false for any other fill, which the caller draws as before.</summary>
        private bool DrawGradeMark(Layout.Content.Fill fill, RectF32 rect)
        {
            if (fill.Key is not { } key || !key.StartsWith(GradeMarkKeyPrefix, StringComparison.Ordinal))
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
