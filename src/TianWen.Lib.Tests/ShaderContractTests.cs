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
/// cannot be checked statically, so only CONSTANT positions are judged.</para>
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
    /// <b>Every two-argument <c>atan</c> is guarded.</b> <c>atan(y, x)</c> is undefined when both
    /// arguments are zero, and all three calls in this codebase reach that point on real input:
    /// the overlay's screen north angle near the antipode, and the two inverse projections at a
    /// celestial pole, where right ascension is genuinely undefined rather than merely awkward.
    /// </summary>
    /// <remarks>
    /// <para>The overlay's is the one that matters most, because it feeds <c>gl_Position</c> through
    /// a cosine and a sine: an undefined angle there puts a VISIBLE primitive with no finite
    /// position into the stream, which is worse than a cull gone wrong. The other two only reach a
    /// texture coordinate, where the cost is one wrong pixel.</para>
    /// <para>This began as a rule pinned to the overlay shader by name, because the other two were
    /// unguarded and a blanket rule with exemptions carved into it stops being a rule. They are
    /// guarded now, so the rule is the blanket one, which is the version that catches the call
    /// nobody has written yet.</para>
    /// <para>A single-argument <c>atan(x)</c> is defined everywhere and is not matched: the
    /// distinction is a top-level comma inside the call's own parentheses.</para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(ShaderSources))]
    public void EveryTwoArgumentAtanIsGuarded(string fileName)
    {
        var text = StripComments(File.ReadAllText(Path.Combine(ShaderDir, fileName)));

        foreach (Match call in Regex.Matches(text, @"\batan\s*\("))
        {
            var open = text.IndexOf('(', call.Index);
            var argument = BalancedArgument(text, open);
            if (!HasTopLevelComma(argument))
            {
                continue;
            }

            var statement = StatementAround(text, call.Index);

            (statement.Contains('?', StringComparison.Ordinal)
                && Regex.IsMatch(statement, @"length\s*\(|Len\b")).ShouldBeTrue(
                $"{fileName}: atan({Compact(argument)}) is undefined at (0, 0), so it has to be "
                + "chosen against a length test rather than taken unconditionally -- found: "
                + $"{Compact(statement)}");
        }
    }

    /// <summary>
    /// The set must hold the three calls the rule above was written for, or a refactor that renames
    /// them past the pattern would leave it passing over nothing.
    /// </summary>
    [Fact]
    public void TheThreeKnownTwoArgumentAtansAreStillThere()
    {
        var expected = new (string File, string Marker)[]
        {
            ("skymap_overlay.vert", "north2d"),
            ("skymap_mw.frag", "j2000"),
            ("image.frag", "raY"),
            ("WebGlSkyMapPipeline.cs.txt", "north2d"),
            ("WebGlSkyMapPipeline.cs.txt", "j2000"),
        };

        foreach (var (file, marker) in expected)
        {
            var text = StripComments(File.ReadAllText(Path.Combine(ShaderDir, file)));
            Regex.IsMatch(text, @"atan\s*\([^)]*" + Regex.Escape(marker)).ShouldBeTrue(
                $"{file} should still measure an angle from {marker}; if that moved, move this with it");
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

    /// <summary>
    /// Whether a call's argument list has a comma at its own nesting level, which is what separates
    /// the two-argument <c>atan(y, x)</c> from the one-argument form. A comma inside a nested call,
    /// as in <c>atan(max(a, b))</c>, belongs to that call and not to this one.
    /// </summary>
    private static bool HasTopLevelComma(string argument)
    {
        var depth = 0;
        foreach (var ch in argument)
        {
            switch (ch)
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                case ',' when depth == 0:
                    return true;
            }
        }

        return false;
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
