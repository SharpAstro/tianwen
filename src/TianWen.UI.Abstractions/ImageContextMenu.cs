using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Abstractions
{
    /// <summary>What picking an entry does with its payload.</summary>
    public enum ImageContextMenuAction
    {
        /// <summary>Put the payload on the clipboard. The default, and what every entry did before an
        /// entry existed that could not sensibly be one.</summary>
        Copy,

        /// <summary>Open the payload as a URL in the reader's browser. Goes out as an
        /// <c>OpenUrlSignal</c> rather than starting a process, because opening a URL is a shell call
        /// and this assembly is shared with the WebAssembly build.</summary>
        OpenUrl,
    }

    /// <summary>One entry in the image context menu: what it says, what it carries, and how the status
    /// bar names it afterwards.</summary>
    /// <param name="Label">Menu text. Carries the value itself, so the menu answers the question
    /// without anything being copied at all.</param>
    /// <param name="Description">What the status line calls it ("Copied RA / Dec").</param>
    /// <param name="Payload">Clipboard text, or the URL for <see cref="ImageContextMenuAction.OpenUrl"/>.
    /// May be multi-line: a second line is an alternative notation of the same value, never a different
    /// value.</param>
    /// <param name="Action">What to do with <paramref name="Payload"/>. Copy unless stated, which is
    /// also what <c>default</c> gives.</param>
    public readonly record struct ImageContextMenuItem(
        string Label,
        string Description,
        string Payload,
        ImageContextMenuAction Action = ImageContextMenuAction.Copy);

    /// <summary>
    /// The catalogued object the click landed on, resolved by the caller because only it holds the
    /// object database. Both halves are worth their own entry: the NAME is what a person searches for
    /// and the DESIGNATION is what a tool takes.
    /// </summary>
    /// <param name="Name">Best common name, or the canonical designation when the object has none.</param>
    /// <param name="Designation">Canonical catalogue designation, e.g. "NGC 6523".</param>
    public readonly record struct ImageContextMenuObject(string Name, string Designation);

    /// <summary>
    /// Builds the right-click menu for a pixel. Pure and non-generic on purpose: the renderer that
    /// hosts the menu is <c>ImageRendererBase&lt;TSurface&gt;</c>, and formatting is not a property of
    /// a surface -- so this is testable without a GPU, the same split
    /// <see cref="InfoPanelData"/> makes for the info pane.
    /// </summary>
    public static class ImageContextMenu
    {
        /// <summary>
        /// The items for one pixel, in menu order: sky coordinates first (the reason most people
        /// right-click), then the sample, then the position.
        /// </summary>
        /// <remarks>
        /// Empty when the pixel carries neither a sample nor a WCS position -- an out-of-raster query
        /// answers that shape, and a menu whose only item is "the coordinates you clicked" is noise.
        /// </remarks>
        /// <param name="fovDeg">
        /// The frame's own field width in degrees, so a share link opens the atlas showing roughly what
        /// the image covers. Null when the frame carries no usable plate scale.
        /// </param>
        /// <param name="capturedUtc">
        /// When the frame was taken, for the share link's <c>t=</c>. Null (or a sentinel) drops that
        /// parameter and the atlas opens at the reader's "now".
        /// </param>
        /// <param name="nearest">
        /// The catalogued object under the cursor, when the caller could resolve one. Its two entries
        /// lead, because a click on a marked object is nearly always about the object.
        /// </param>
        public static ImmutableArray<ImageContextMenuItem> ItemsFor(
            PixelInfo pixel, double? fovDeg = null, DateTimeOffset? capturedUtc = null,
            ImageContextMenuObject? nearest = null)
        {
            var hasSky = pixel.RA.HasValue && pixel.Dec.HasValue;
            if (pixel.Values.Length == 0 && !hasSky)
            {
                return ImmutableArray<ImageContextMenuItem>.Empty;
            }

            var builder = ImmutableArray.CreateBuilder<ImageContextMenuItem>(6);

            if (nearest is { } obj)
            {
                builder.Add(new ImageContextMenuItem(
                    $"Copy object name   {obj.Name}", "object name", obj.Name));

                // Only when it says something the name did not: an object with no common name has its
                // designation AS its name, and two entries with the same value is noise.
                if (!string.Equals(obj.Designation, obj.Name, StringComparison.Ordinal))
                {
                    builder.Add(new ImageContextMenuItem(
                        $"Copy catalogue number   {obj.Designation}", "catalogue number", obj.Designation));
                }
            }

            if (hasSky)
            {
                // The formatters the info panel uses, so what is pasted is what was read on screen.
                // The decimal-degree pair rides along on a second line because that is the form most
                // tools take as input, and RA is stored in HOURS -- hence the x15.
                var ra = pixel.RA!.Value;
                var dec = pixel.Dec!.Value;
                var sexagesimal = $"{CoordinateUtils.HoursToHMS(ra)} {CoordinateUtils.DegreesToDMS(dec)}";
                builder.Add(new ImageContextMenuItem(
                    $"Copy RA / Dec   {sexagesimal}",
                    "RA / Dec",
                    string.Create(CultureInfo.InvariantCulture, $"{sexagesimal}\n{ra * 15.0:F6} {dec:F6}")));
            }

            if (pixel.Values.Length > 0)
            {
                // Both forms in one payload, for the same reason the panel prints both: the unit value
                // is what the pipeline works in, the 16-bit one is what a header or another tool quotes.
                var unit = string.Join(' ', pixel.Values.Select(
                    static v => v.ToString("F6", CultureInfo.InvariantCulture)));
                var adu = string.Join(' ', pixel.Values.Select(
                    static v => (v * 65535.0).ToString("F0", CultureInfo.InvariantCulture)));
                builder.Add(new ImageContextMenuItem($"Copy value   {unit}", "pixel value", $"{unit}\n{adu}"));
            }

            builder.Add(new ImageContextMenuItem(
                $"Copy position   ({pixel.X}, {pixel.Y})",
                "pixel position",
                string.Create(CultureInfo.InvariantCulture, $"{pixel.X} {pixel.Y}")));

            // Last, and only for a plate-solved frame: without RA/Dec there is nothing to point the
            // atlas at. The label does not carry the URL the way the others carry their values -- a
            // hundred-character link would be the widest thing in the menu and unreadable at that size,
            // so this is the one item where the payload is worth more than the preview.
            //
            // It OPENS the atlas rather than copying its link. Copying was the first shape and it made
            // the reader do the other half of the job: paste it somewhere, in a browser they had to find
            // themselves, to see the sky they had just clicked on. Nothing is lost -- a browser's address
            // bar holds the same link, now with the page it names in front of it.
            if (hasSky)
            {
                builder.Add(new ImageContextMenuItem(
                    "Open in sky atlas",
                    "sky atlas",
                    SkyAtlasLink.For(pixel.RA!.Value, pixel.Dec!.Value, fovDeg, capturedUtc),
                    ImageContextMenuAction.OpenUrl));
            }

            return builder.ToImmutable();
        }
    }
}
