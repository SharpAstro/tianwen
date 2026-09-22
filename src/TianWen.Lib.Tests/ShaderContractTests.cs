using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Rules the atlas and viewer shaders must obey that a compiler will not enforce and a GPU need not
/// reveal, checked against the GLSL SOURCE the apps ship.
/// </summary>
/// <remarks>
/// <para><b>Why the source and not the pixels.</b> The defect these exist for is undefined behaviour,
/// and undefined behaviour is not a bug a conforming driver has to show you. The 2026-09-22 wedge was
/// reproduced on a desktop GPU with the Khronos validation layer live -- the exact sequence, the same
/// star counts -- and it never wedged, with zero validation messages and zero sync hazards, before or
/// after the fix. A rendered-pixel test would therefore have been green in BOTH arms and would have
/// proved nothing. What can be decided without a GPU at all is whether the source still says the
/// thing that is safe on every implementation, which is what these assert.</para>
/// <para><b>Why the text and not the baked SPIR-V.</b> The <c>.spv</c> cannot be read back into an
/// expression without a disassembler, and the rule is one a PERSON edits. The sources are copied into
/// the output by this project's own Content items, flat under <c>Shaders/</c>, so nothing here needs a
/// path out of the output directory.</para>
/// <para><b>The WebGL twin is in the set.</b> Its shaders are string literals inside
/// <c>WebGlSkyMapPipeline.cs</c>, which arrives here as text under a <c>.txt</c> link and is never
/// compiled by this suite. It is the build that runs on phone GPUs, which is exactly where the rule
/// below matters most, and it is the copy most likely to be forgotten: it drifted from the Vulkan
/// shaders once already.</para>
/// <para><b>What these do NOT cover.</b> A computed <c>gl_Position</c> -- every real projection --
/// cannot be checked statically, so only CONSTANT positions are judged. A two-argument
/// <c>atan(y, x)</c> is undefined at (0, 0) and is deliberately not linted here: two calls in the
/// codebase reach it only on a texture-coordinate path where a NaN costs one wrong pixel and cannot
/// reach a vertex position, and a blanket rule with exemptions carved into it stops being a rule.
/// The one that DID feed <c>gl_Position</c> is guarded, and is pinned by name below.</para>
/// </remarks>
public class ShaderContractTests
{
    private static string ShaderDir => Path.Combine(AppContext.BaseDirectory, "Shaders");

    /// <summary>Every shader-bearing file: the Vulkan GLSL, plus the WebGL twin's C# holding its own.</summary>
    private static string[] ShaderFileNames()
        => [.. Directory.EnumerateFiles(ShaderDir)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => name.EndsWith(".vert", StringComparison.Ordinal)
                || name.EndsWith(".frag", StringComparison.Ordinal)
                || name.EndsWith(".cs.txt", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)];

    public static TheoryData<string> ShaderSources
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in ShaderFileNames())
            {
                data.Add(name);
            }

            return data;
        }
    }

    /// <summary>
    /// The set must not be empty and must hold both halves, or every rule below passes over nothing.
    /// A Content item that stops copying is silent otherwise: the theory simply runs zero cases.
    /// </summary>
    [Fact]
    public void TheShaderSetHoldsBothTheVulkanShadersAndTheWebGlTwin()
    {
        var names = ShaderFileNames();

        names.Length.ShouldBeGreaterThan(8, "the atlas alone ships five shader pairs");
        names.ShouldContain("skymap_star.vert", "the horizon cull lives here");
        names.ShouldContain("skymap_overlay.vert", "the anti-hemisphere cull and the north angle live here");
        names.ShouldContain("skymap_line.vert");
        names.ShouldContain("image.frag");
        names.ShouldContain("WebGlSkyMapPipeline.cs.txt", "the WebGL twin is the copy that drifted before");
    }

    /// <summary>
    /// <b>A culled vertex leaves the clip volume with a POSITIVE w.</b>
    /// </summary>
    /// <remarks>
    /// <para><c>vec4(0, 0, 0, 0)</c> reads like a degenerate vertex and is an UNDEFINED one. The
    /// perspective divide is 0/0, which the spec leaves undefined, and before it becomes NaN the clip
    /// test <c>-w &lt;= x &lt;= w</c> has already degenerated to <c>0 &lt;= 0 &lt;= 0</c>, so the
    /// vertex reads as INSIDE the view volume. A desktop rasteriser drops the primitive regardless; a
    /// tiling binner that derives a tile range from NaN need not, because every comparison against a
    /// NaN is false.</para>
    /// <para>Horizon mode sends every below-horizon star down this path, thousands of primitives per
    /// frame, which is the one thing the atlas does that the viewer's sky backdrop never does.</para>
    /// <para>Only the x and y planes are asserted, not z: Vulkan clips z to <c>[0, w]</c> and OpenGL
    /// ES to <c>[-w, w]</c>, and this one test covers files of both conventions. <c>x &gt; w</c> fails
    /// the x plane under either, whatever depth clamping is in force.</para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(ShaderSources))]
    public void ACulledVertexLeavesTheClipVolumeWithAPositiveW(string fileName)
    {
        var text = StripComments(File.ReadAllText(Path.Combine(ShaderDir, fileName)));
        var constants = ConstantVec4s(text);

        var found = 0;
        foreach (Match assignment in Regex.Matches(text, @"gl_Position\s*=\s*([^;]+);", RegexOptions.Singleline))
        {
            var rhs = assignment.Groups[1].Value.Trim();

            // A named constant is the idiomatic form (CULLED_VERTEX); resolve it to its own literal.
            if (constants.TryGetValue(rhs, out var named))
            {
                rhs = named;
            }

            if (ConstantComponents(rhs) is not { } v)
            {
                // A computed position: the real projections, which no static rule can judge.
                continue;
            }

            found++;
            var where = $"{fileName}: gl_Position = {rhs}";

            v.W.ShouldBeGreaterThan(0f,
                $"{where} -- w = 0 makes the perspective divide 0/0 and the clip test degenerate to "
                + "0 <= 0 <= 0, so the vertex reads as inside the volume on its way to being NaN");

            (MathF.Abs(v.X) > v.W || MathF.Abs(v.Y) > v.W).ShouldBeTrue(
                $"{where} -- a constant position is only ever written to CULL, so it has to fail a "
                + "clip plane; x or y beyond w fails one under both the Vulkan and the OpenGL "
                + "conventions for z");
        }

        // Not every shader culls, so finding none is legitimate; the two that carry the sites this
        // rule was written for are named here so a deleted cull cannot pass as "nothing to check".
        if (fileName is "skymap_star.vert" or "skymap_overlay.vert" or "skymap_line.vert")
        {
            found.ShouldBeGreaterThan(0, $"{fileName} culls vertices and must still do it by position");
        }
    }

    /// <summary>
    /// <b><c>asin</c> and <c>acos</c> take a clamped argument.</b> Both are undefined outside
    /// [-1, 1], and every argument here is only MATHEMATICALLY inside it: rounding through a product
    /// and a divide lands 1.0000001 at a projection pole, which returns NaN. <c>skymap_mw.frag</c>
    /// clamped its own from the start and <c>image.frag</c> did not, which is the shape of the whole
    /// problem -- two copies of one rule, one of them right.
    /// </summary>
    [Theory]
    [MemberData(nameof(ShaderSources))]
    public void AsinAndAcosTakeAClampedArgument(string fileName)
    {
        var text = StripComments(File.ReadAllText(Path.Combine(ShaderDir, fileName)));

        foreach (Match call in Regex.Matches(text, @"\b(asin|acos)\s*\("))
        {
            var open = text.IndexOf('(', call.Index);
            var argument = BalancedArgument(text, open);

            argument.Contains("clamp(", StringComparison.Ordinal).ShouldBeTrue(
                $"{fileName}: {call.Groups[1].Value}({Compact(argument)}) is undefined outside [-1, 1], "
                + "and this argument is only mathematically inside it");
        }
    }

    /// <summary>
    /// <b>The overlay's north angle is guarded.</b> This is the one <c>atan(y, x)</c> that feeds
    /// <c>gl_Position</c>, so an undefined result there is worse than a cull gone wrong: it puts a
    /// VISIBLE instance with no finite position into the stream. The pair reaches (0, 0) two ways --
    /// the tip landing on the antipode sentinel, and, near the antipode, the projection scale growing
    /// without bound so that centre and tip are large nearly-equal numbers cancelling to nothing in
    /// float. Pinned by name rather than by a blanket lint, for the reason in the class remarks.
    /// </summary>
    [Theory]
    [InlineData("skymap_overlay.vert")]
    [InlineData("WebGlSkyMapPipeline.cs.txt")]
    public void TheOverlaysNorthAngleIsGuardedBeforeItReachesAPosition(string fileName)
    {
        var text = StripComments(File.ReadAllText(Path.Combine(ShaderDir, fileName)));

        var calls = Regex.Matches(text, @"atan\s*\(\s*north2d\.y\s*,\s*north2d\.x\s*\)");
        calls.Count.ShouldBeGreaterThan(0, $"{fileName} measures the screen north angle");

        foreach (Match call in calls)
        {
            var statement = StatementAround(text, call.Index);

            statement.Contains('?', StringComparison.Ordinal).ShouldBeTrue(
                $"{fileName}: the north angle must be chosen, not taken unconditionally");
            statement.Contains("north", StringComparison.Ordinal).ShouldBeTrue(
                $"{fileName}: the guard must test the vector being measured");
            Regex.IsMatch(statement, @"length\s*\(\s*north2d\s*\)|northLen").ShouldBeTrue(
                $"{fileName}: the guard must test that north2d has a measurable length, since "
                + $"atan(0, 0) is undefined -- found: {Compact(statement)}");
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Reading GLSL as text. Deliberately small: enough to decide the rules above and nothing more.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Line and block comments out, so the prose that EXPLAINS these rules -- which necessarily
    /// quotes the forbidden forms -- cannot trip them. Handles the C# file too, whose comments use
    /// the same two forms.
    /// </summary>
    private static string StripComments(string source)
    {
        var withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Replace(withoutBlocks, @"//[^\n]*", string.Empty);
    }

    /// <summary>Every <c>const vec4 NAME = vec4(...);</c> in the file, by name.</summary>
    private static Dictionary<string, string> ConstantVec4s(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(text, @"const\s+vec4\s+(\w+)\s*=\s*([^;]+);", RegexOptions.Singleline))
        {
            map[m.Groups[1].Value] = m.Groups[2].Value.Trim();
        }

        return map;
    }

    /// <summary>
    /// The four components of a <c>vec4</c> whose arguments are all numeric literals, or null for
    /// anything computed. A single argument fills all four, which is how <c>vec4(0)</c> -- the same
    /// defect in shorter form -- is caught.
    /// </summary>
    private static (float X, float Y, float Z, float W)? ConstantComponents(string expression)
    {
        var m = Regex.Match(expression.Trim(), @"^vec4\s*\(([^()]*)\)$", RegexOptions.Singleline);
        if (!m.Success)
        {
            return null;
        }

        var parts = m.Groups[1].Value.Split(',');
        var values = new float[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
            {
                return null;
            }
        }

        return values.Length switch
        {
            1 => (values[0], values[0], values[0], values[0]),
            4 => (values[0], values[1], values[2], values[3]),
            _ => null,
        };
    }

    /// <summary>The text between a call's opening parenthesis and its matching close.</summary>
    private static string BalancedArgument(string text, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                depth++;
            }
            else if (text[i] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return text.Substring(openIndex + 1, i - openIndex - 1);
                }
            }
        }

        return text[openIndex..];
    }

    /// <summary>The statement an index sits in: back to the previous <c>;</c> or <c>{</c>, forward to the next <c>;</c>.</summary>
    private static string StatementAround(string text, int index)
    {
        var start = text.LastIndexOfAny([';', '{', '}'], index);
        var end = text.IndexOf(';', index);
        start = start < 0 ? 0 : start + 1;
        end = end < 0 ? text.Length : end;
        return text[start..end];
    }

    /// <summary>Whitespace collapsed, for a failure message that fits on a line.</summary>
    private static string Compact(string text)
    {
        var builder = new StringBuilder();
        var space = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }
}
