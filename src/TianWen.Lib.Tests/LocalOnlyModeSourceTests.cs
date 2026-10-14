using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Shouldly;
using TianWen.Lib.IO;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Source guards for the local-only mode (<see cref="LocalOnlyMode"/>): its variable is read in ONE place; every DEBUG inspector
/// a host attaches is told whether to answer multicast discovery; and every place in the shipping code that binds a network
/// address, joins a multicast group or broadcasts is a KNOWN one, gated on the mode. A new such site fails the last test until
/// it asks the mode and is listed here, which is the point: the mode promises nothing binds but loopback, and a new UDP socket
/// written next month would break that promise without a word.
/// </summary>
public class LocalOnlyModeSourceTests
{
    /// <summary>
    /// Each file that binds the network, and the file holding the gate that keeps it from doing so in the mode. Paths are
    /// relative to <c>src</c>, with forward slashes.
    /// </summary>
    private static readonly Dictionary<string, string> GatedBinds = new(StringComparer.Ordinal)
    {
        // Kestrel's TCP 1888 and the LAN announcement: only with a LAN port, which a local-only node never has.
        ["TianWen.Server/Program.cs"] = "TianWen.Server/Program.cs",
        // LAN peer discovery, whose UDP transport binds on every interface: not registered in the mode.
        ["TianWen.UI.Gui/Program.cs"] = "TianWen.UI.Gui/Program.cs",
        // mDNS scans (UDP 5353 on every interface, a multicast join).
        ["TianWen.Lib/Devices/Canon/CanonDeviceSource.cs"] = "TianWen.Lib/Devices/Canon/CanonDeviceSource.cs",
        ["TianWen.Lib/Devices/OnStep/OnStepDeviceSource.cs"] = "TianWen.Lib/Devices/OnStep/OnStepDeviceSource.cs",
        // Broadcasts from a socket the OS binds on every interface (Alpaca asks loopback from loopback in the mode).
        ["TianWen.Lib/Devices/Alpaca/AlpacaDeviceSource.cs"] = "TianWen.Lib/Devices/Alpaca/AlpacaDeviceSource.cs",
        ["TianWen.Lib/Devices/Skywatcher/SkywatcherDeviceSource.cs"] = "TianWen.Lib/Devices/Skywatcher/SkywatcherDeviceSource.cs",
        // A WiFi mount's UDP socket: its driver does not open it in the mode.
        ["TianWen.Lib/Devices/Skywatcher/SkywatcherUdpConnection.cs"] = "TianWen.Lib/Devices/Skywatcher/SkywatcherMountDriver.cs",
    };

    // A bind on every interface, a broadcast, a multicast join, a listener, or the LAN discovery that does all three.
    private static readonly Regex NetworkBind = new(
        @"IPAddress\.(Any|IPv6Any|Broadcast)\b|new\s+(System\.Net\.Sockets\.)?(UdpClient|TcpListener)\s*\(|new\s+(System\.Net\.)?HttpListener\s*\(|"
        + @"JoinMulticastGroup\s*\(|ListenAnyIP\s*\(|AddLanDiscovery\s*\(",
        RegexOptions.NonBacktracking);

    [Fact]
    public void EveryNetworkBindInTheShippingCodeIsAKnownOneGatedOnTheMode()
    {
        var root = FindSourceRoot();

        var found = ShippingSources(root)
            .Where(f => NetworkBind.IsMatch(Code(File.ReadAllText(f))))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        found.ShouldBe(GatedBinds.Keys.Order(StringComparer.Ordinal).ToArray(),
            "a file that binds the network (or no longer does) must ask LocalOnlyMode and be listed in GatedBinds: "
            + string.Join(", ", found.Except(GatedBinds.Keys).Concat(GatedBinds.Keys.Except(found))));

        foreach (var (bind, gate) in GatedBinds)
        {
            File.ReadAllText(Path.Combine(root, gate)).ShouldContain(nameof(LocalOnlyMode), customMessage: $"{bind} is gated in {gate}");
        }
    }

    [Fact]
    public void NoDebugInspectorIsToldToStopAnsweringDiscovery()
    {
        // The mode leaves the DEBUG inspector alone (R1 D1 of PR #1579): from DIR.Lib 11.21 its default discovery is a
        // socket-free file registry, and EnableDiscovery = false also takes the instance out of that registry, so an app told
        // false would be unfindable by the inspector's MCP sidecar. A worktree's build has no inspector at all; a build against
        // the sibling sources keeps the multicast bind on the path it was already allowed through.
        var root = FindSourceRoot();

        var initializers = ShippingSources(root)
            .SelectMany(f =>
            {
                var text = File.ReadAllText(f);
                return Regex.Matches(text, @"new\s+DebugInspectorOptions\b").Select(m =>
                {
                    // The initializer runs to the first line that closes it at the start of a line.
                    var end = text.IndexOf("\n}", m.Index, StringComparison.Ordinal);
                    return (File: Path.GetRelativePath(root, f), Body: end < 0 ? text[m.Index..] : text[m.Index..end]);
                });
            })
            .ToArray();

        initializers.Length.ShouldBeGreaterThanOrEqualTo(2, "the GUI and the viewer each attach one; fewer means this search no longer finds them");
        initializers.Where(i => i.Body.Contains("EnableDiscovery", StringComparison.Ordinal)).Select(i => i.File)
            .ShouldBeEmpty("an inspector keeps its default discovery: false would take the app out of the inspector's registry too");
    }

    [Fact]
    public void OnlyLocalOnlyModeReadsItsVariable()
    {
        var root = FindSourceRoot();

        var readers = ShippingSources(root)
            .Where(f => !f.EndsWith($"{Path.DirectorySeparatorChar}{nameof(LocalOnlyMode)}.cs", StringComparison.Ordinal))
            .Where(f =>
            {
                var code = Code(File.ReadAllText(f));
                return code.Contains('"' + LocalOnlyMode.EnvironmentVariable + '"', StringComparison.Ordinal)
                    || Regex.IsMatch(code, @"GetEnvironmentVariable\(\s*(TianWen\.Lib\.)?LocalOnlyMode\.EnvironmentVariable");
            })
            .Select(f => Path.GetRelativePath(root, f))
            .ToArray();

        readers.ShouldBeEmpty("the mode is decided once per process, by LocalOnlyMode.OfThisProcess, and handed to whatever binds");
    }

    /// <summary>The code of a C# file without its line comments, so a comment naming a call does not count as making it.</summary>
    private static string Code(string text) => string.Join('\n', text.Split('\n').Select(line => line.IndexOf("//", StringComparison.Ordinal) is var at and >= 0 ? line[..at] : line));

    /// <summary>Every C# file of the shipping projects: no test, benchmark or end-to-end project, nothing under bin or obj.</summary>
    private static IEnumerable<string> ShippingSources(string root)
    {
        var sep = Path.DirectorySeparatorChar;
        return FileEnumeration.EnumerateFiles(root, ".cs", recursive: true)
            .Where(f => !f.Contains($"{sep}bin{sep}", StringComparison.Ordinal) && !f.Contains($"{sep}obj{sep}", StringComparison.Ordinal))
            .Where(f => Path.GetRelativePath(root, f).Split(sep)[0] is var project
                && !project.Contains(".Tests", StringComparison.Ordinal) && !project.EndsWith(".E2E", StringComparison.Ordinal)
                && !project.EndsWith(".Benchmarks", StringComparison.Ordinal));
    }

    /// <summary>
    /// Walks up from the test binary to the repository's <c>src</c>, and FAILS when it is not there: these guards are the
    /// mode's promise that nothing binds but loopback, and one that passed without reading a line would promise nothing.
    /// </summary>
    private static string FindSourceRoot()
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

        throw new ShouldAssertException($"No src/TianWen.slnx above {AppContext.BaseDirectory}: the source guards have nothing to read");
    }
}
