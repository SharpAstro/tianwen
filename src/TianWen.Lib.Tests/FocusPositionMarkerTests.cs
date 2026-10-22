using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Shouldly;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.IO;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// "No focus position" is <see cref="FocuserPositions.None"/>, never -1, which is a position a QHY QFOC can be at (#1764,
/// #1763): what a camera, the wire and a label do with it (a frame's header: <c>FitsPixelScaleTests</c>), and a source guard that no <see cref="ImageMeta"/> is built
/// with a literal -1 for its focus position.
/// </summary>
public class FocusPositionMarkerTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(-559)]
    [InlineData(0)]
    [InlineData(1441)]
    public void EveryPositionCrossesTheWireAndBack(int position)
    {
        FocuserPositions.ForWire(position).ShouldBe(position);
        FocuserPositions.FromWire(FocuserPositions.ForWire(position)).ShouldBe(position);
        LiveSessionActions.FocusPositionLabel(position).ShouldBe(position.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void NoPositionIsNullOnTheWireAndADashOnScreen()
    {
        FocuserPositions.ForWire(FocuserPositions.None).ShouldBeNull();
        FocuserPositions.FromWire(null).ShouldBe(FocuserPositions.None);
        LiveSessionActions.FocusPositionLabel(FocuserPositions.None).ShouldBe("--");
    }

    [Fact]
    public void AFocuserWhoseReadFailedCrossesTheWireAsNoPosition()
    {
        var reading = new FocuserReading(FocuserPositions.None, double.NaN, IsMoving: false);
        var dto = FocuserDeviceStateDto.FromReading(reading);

        dto.Position.ShouldBeNull();
        dto.ToReading().ShouldBe(reading);
    }

    [Fact]
    public void ACameraBeforeItsFirstFrameHasNoFocusPosition()
    {
        var states = CameraExposureState.UnsetFor(2);

        states.ShouldAllBe(s => s.FocusPosition == FocuserPositions.None && double.IsNaN(s.FocuserTemperature));
    }

    [Fact]
    public void NoImageMetaIsBuiltWithALiteralMinusOneFocusPosition()
    {
        if (FindSourceRoot() is not { } root)
        {
            return;
        }

        var sep = Path.DirectorySeparatorChar;
        var offenders = FileEnumeration.EnumerateFiles(root, ".cs", recursive: true)
            .Where(f => !f.Contains($"{sep}bin{sep}", StringComparison.Ordinal) && !f.Contains($"{sep}obj{sep}", StringComparison.Ordinal))
            .SelectMany(f =>
            {
                var text = File.ReadAllText(f);
                return FocusArguments(text)
                    .Where(arg => MinusOne.IsMatch(arg.Text))
                    .Select(arg => $"{Path.GetRelativePath(root, f)}:{text.AsSpan(0, arg.Index).Count('\n') + 1}  {arg.Text.Trim()}");
            })
            .Order(StringComparer.Ordinal)
            .ToArray();

        offenders.ShouldBeEmpty("a focus position of -1 is a position; \"none\" is FocuserPositions.None (#1764):\n" + string.Join('\n', offenders));
    }

    // The ninth positional argument, or a FocusPos: argument wherever it stands, that is -1 and nothing else.
    private static readonly Regex MinusOne = new(@"^\s*(FocusPos\s*:\s*)?-\s*1\s*$");

    private const string Construction = "new " + "ImageMeta(";

    /// <summary>Each <c>new ImageMeta(...)</c>'s focus-position argument: the one named <c>FocusPos</c>, else the ninth.</summary>
    private static IEnumerable<(int Index, string Text)> FocusArguments(string text)
    {
        for (var at = text.IndexOf(Construction, StringComparison.Ordinal); at >= 0; at = text.IndexOf(Construction, at + 1, StringComparison.Ordinal))
        {
            var args = Arguments(text, at + Construction.Length);
            var named = args.FirstOrDefault(a => Regex.IsMatch(a.Text, @"^\s*FocusPos\s*:"));
            if (named.Text is not null)
            {
                yield return named;
            }
            else if (args.Count > 8 && !Regex.IsMatch(args[8].Text, @"^\s*\w+\s*:"))
            {
                yield return args[8];
            }
        }
    }

    /// <summary>The top-level arguments of a call whose opening parenthesis ends just before <paramref name="start"/>.</summary>
    private static List<(int Index, string Text)> Arguments(string text, int start)
    {
        var args = new List<(int, string)>();
        var depth = 0;
        var argStart = start;
        var inString = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }
            }
            else if (c == '"')
            {
                inString = true;
            }
            else if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                if (depth == 0)
                {
                    args.Add((argStart, text[argStart..i]));
                    break;
                }

                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                args.Add((argStart, text[argStart..i]));
                argStart = i + 1;
            }
        }

        return args;
    }

    /// <summary>The repository's <c>src</c>, walking up from the test binary; null when the sources are not beside it.</summary>
    private static string? FindSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src");
            if (File.Exists(Path.Combine(candidate, "TianWen.slnx")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
