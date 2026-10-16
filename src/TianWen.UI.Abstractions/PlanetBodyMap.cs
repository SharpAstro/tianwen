using System;
using DIR.Lib;
using TianWen.Lib.Imaging.Planetary;

namespace TianWen.UI.Abstractions;

/// <summary>
/// A body's colour map as a GPU host uploads it for <c>planet_body.frag</c> (docs/plans/atlas-planet-detail.md, A5, "The bodies on the
/// GPU"): one texel per map sample in <see cref="PlanetMap"/>'s own convention (row 0 the north edge, column 0 at 0 degrees west,
/// longitude falling to the right), read with <c>texelFetch</c> and interpolated in the shader as <see cref="PlanetMap.Sample"/> does; and
/// each plane's mean albedo, what a ring's level is stated against. A body with no map (one whose texture has not loaded, the Sun's and
/// the Earth's included) is a 2 x 2 map of its flat colour, as <see cref="PlanetDiscSprite.Build"/>'s flat map is. The one source of both hosts'
/// textures, so Vulkan's and WebGL's cannot differ.
/// </summary>
public static class PlanetBodyMap
{
    /// <summary>RGBA texels as floats, the planes exact (Vulkan's <c>R32G32B32A32Sfloat</c>).</summary>
    public static (float[] Texels, int Width, int Height) FloatTexels(PlanetColourMap? map, RGBAColor32 flat)
    {
        if (map is null)
        {
            var (r, g, b) = (flat.Red / 255f, flat.Green / 255f, flat.Blue / 255f);
            return ([r, g, b, 1, r, g, b, 1, r, g, b, 1, r, g, b, 1], 2, 2);
        }
        var (width, height) = (map.Width, map.Height);
        var texels = new float[width * height * 4];
        for (var row = 0; row < height; row++)
        {
            // Each sample at its own centre: column c at west 360 - (c + 0.5) * 360 / width, row r at 90 - (r + 0.5) * 180 / height.
            var latitude = 90 - ((row + 0.5) * 180 / height);
            for (var c = 0; c < width; c++)
            {
                var west = 360 - ((c + 0.5) * 360 / width);
                var i = ((row * width) + c) * 4;
                texels[i] = (float)map.Red.Sample(latitude, west);
                texels[i + 1] = (float)map.Green.Sample(latitude, west);
                texels[i + 2] = (float)map.Blue.Sample(latitude, west);
                texels[i + 3] = 1;
            }
        }
        return (texels, width, height);
    }

    /// <summary>The same texels as 8-bit RGBA (WebGL's, which takes a texture only as an image): within half a level of the floats.</summary>
    public static (byte[] Rgba, int Width, int Height) Rgba8(PlanetColourMap? map, RGBAColor32 flat)
    {
        var (texels, width, height) = FloatTexels(map, flat);
        var rgba = new byte[texels.Length];
        for (var i = 0; i < texels.Length; i++)
        {
            rgba[i] = (byte)Math.Clamp(MathF.Round(texels[i] * 255), 0, 255);
        }
        return (rgba, width, height);
    }

    /// <summary>Each plane's mean albedo (<see cref="PlanetMap.MeanAlbedo"/>), or the flat colour's.</summary>
    public static (float R, float G, float B) RingAlbedo(PlanetColourMap? map, RGBAColor32 flat)
        => map is null
            ? (flat.Red / 255f, flat.Green / 255f, flat.Blue / 255f)
            : ((float)map.Red.MeanAlbedo, (float)map.Green.MeanAlbedo, (float)map.Blue.MeanAlbedo);

    /// <summary>The floats in <c>planet_body.frag</c>'s <c>Body</c> block: seven vec4s.</summary>
    public const int BlockFloats = 7 * 4;

    /// <summary>
    /// Writes <c>planet_body.frag</c>'s <c>Body</c> block (Vulkan's push constants, WebGL's one instance of seven vec4 attributes,
    /// <see cref="PlanetBodyInstances{TBuffer}"/>; one layout, since it is all vec4s) for <paramref name="view"/> drawn centred on (<paramref name="x"/>, <paramref name="y"/>), its
    /// globe <paramref name="radiusPx"/>, in a target <paramref name="viewportWidth"/> by <paramref name="viewportHeight"/> px.
    /// </summary>
    public static void WriteBlock(Span<float> block, in PlanetView view, float x, float y, float radiusPx, float viewportWidth, float viewportHeight,
        (float R, float G, float B) ringAlbedo, RGBAColor32? nightTint, float opacity)
    {
        var rings = view.Body == Lib.Astrometry.Catalogs.CatalogIndex.Saturn;
        var tint = nightTint ?? default;
        ReadOnlySpan<float> values =
        [
            x, y, radiusPx, rings ? (float)SaturnRings.Main.OuterRadii : 1f,
            viewportWidth, viewportHeight, (float)(1 - view.Flattening), (float)PlanetDiscSprite.MinnaertK(view.Body),
            view.Right.X, view.Right.Y, view.Right.Z, rings ? 1f : 0f,
            view.Toward.X, view.Toward.Y, view.Toward.Z, opacity,
            view.Sun.X, view.Sun.Y, view.Sun.Z, nightTint.HasValue ? 1f : 0f,
            ringAlbedo.R, ringAlbedo.G, ringAlbedo.B, 0f,
            tint.Red / 255f, tint.Green / 255f, tint.Blue / 255f, (float)PlanetaryRender.LimbDarkening(view.Body),
        ];
        values.CopyTo(block);
    }
}
