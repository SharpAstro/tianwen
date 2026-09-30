using System;
using System.IO;
using System.Linq;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.IO;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A device's identity key is computed in ONE place, <see cref="DeviceUriExtensions.DeviceKey"/>, and compared with one
/// comparer. Thirteen places used to recompute it with their own <c>Uri.GetLeftPart</c> of the path, and
/// <see cref="DeviceBase.SameDevice"/> compared it ordinally while the hub's maps ignored case, so the two could
/// disagree about a path differing only in case. The day identity changes, a copy drifts.
/// </summary>
public class DeviceKeyHasOneDefinitionTests
{
    private const string Definition = "DeviceUriExtensions.cs";

    /// <summary>
    /// Walks up from the test binary to the repository's <c>src</c>. Null when the sources are not beside the binary
    /// (a packaged run), and the test then skips: not seeing the source is not a regression.
    /// </summary>
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

    [Fact]
    public void NothingButTheDefinitionComputesTheKey()
    {
        if (FindSourceRoot() is not { } root)
        {
            return;
        }

        // Built from parts, so this file does not match its own search.
        var copy = "GetLeftPart(" + "UriPartial.Path)";
        var sep = Path.DirectorySeparatorChar;
        var offenders = FileEnumeration.EnumerateFiles(root, [".cs", ".razor"], recursive: true)
            .Where(f => !f.Contains($"{sep}bin{sep}", StringComparison.Ordinal) && !f.Contains($"{sep}obj{sep}", StringComparison.Ordinal))
            .Where(static f => !Path.GetFileName(f).Equals(Definition, StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains(copy, StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root, f))
            .Order(StringComparer.Ordinal)
            .ToArray();

        offenders.ShouldBeEmpty("a device key is Uri.DeviceKey; a second computation of it drifts the day identity changes");
    }

    [Fact]
    public void TwoUrisDifferingOnlyInThePathsCaseNameOneDevice()
    {
        var a = new Uri("Camera://ZWODevice/ASI2600MC-Pro_ABC123?gain=100");
        var b = new Uri("Camera://ZWODevice/asi2600mc-pro_abc123?gain=200");

        DeviceBase.SameDevice(a, b).ShouldBeTrue("the hub's maps ignore case, so SameDevice must too");
        DeviceUriExtensions.DeviceKeyComparer.Equals(a.DeviceKey, b.DeviceKey).ShouldBeTrue();
    }
}
