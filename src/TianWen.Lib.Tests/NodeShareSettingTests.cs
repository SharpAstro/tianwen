using Microsoft.Win32;
using Shouldly;
using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using TianWen.Hosting;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The pieces of "Share this rig on the LAN" (P1 of docs/plans/hardware-in-the-server.md, #917, decision 3): where a
/// node listens given how it was started and the setting, the setting kept in the data root, and the logon entry each
/// OS keeps, written to places of the test's own, never the user's.
/// </summary>
public class NodeShareSettingTests
{
    [Theory]
    // Run by hand: the LAN, as a mini PC's node does, whatever the setting; --local-only takes it away.
    [InlineData(false, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, true, true, false)]
    // Started by a client: the LAN only while the rig is shared, so a laptop's GUI opens no port nobody asked for.
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public void ANodeListensOnTheLanAsItWasStartedAndTheSettingSay(bool spawned, bool shared, bool localOnly, bool onTheLan)
    {
        var node = new NodeArguments(Path.Combine(Path.GetTempPath(), "node.sock"), NodeArguments.DefaultPort, localOnly, Keeper: false, spawned, FakeDevicesOnly: false);

        var listening = NodeListeningDecision.For(node, new NodeSettings(shared));

        listening.IsShared.ShouldBe(onTheLan);
        listening.SocketPath.ShouldBe(node.SocketPath, "the socket always");
    }

    [Fact]
    public async Task TheSettingIsReadFromTheDataRootAndDefaultsToNotShared()
    {
        var root = Directory.CreateTempSubdirectory("twset");

        (await NodeSettings.LoadAsync(root, TestContext.Current.CancellationToken)).ShouldBe(NodeSettings.Default);
        NodeSettings.Default.ShareOnLan.ShouldBeFalse("a rig is shared only when the user says so");

        await File.WriteAllTextAsync(NodeSettings.PathIn(root), """{ "shareOnLan": true }""", TestContext.Current.CancellationToken);
        (await NodeSettings.LoadAsync(root, TestContext.Current.CancellationToken)).ShareOnLan.ShouldBeTrue();

        await File.WriteAllTextAsync(NodeSettings.PathIn(root), "not json", TestContext.Current.CancellationToken);
        (await NodeSettings.LoadAsync(root, TestContext.Current.CancellationToken)).ShouldBe(NodeSettings.Default, "an unreadable file is not a reason to share");
    }

    [Fact]
    public void AnXdgAutostartEntryStartsTheKeeperAndGoesWhenUnset()
    {
        var entries = new NodeLogonStart.XdgAutostart(Directory.CreateTempSubdirectory("twxdg").FullName);
        const string server = "/opt/Tian Wen/bin \"new\"/tianwen-server";

        entries.Set(true, server);

        entries.IsSet.ShouldBeTrue();
        var text = File.ReadAllText(entries.EntryPath);
        text.ShouldContain("[Desktop Entry]");
        // The path quoted, its reserved characters escaped, as the desktop entry specification asks.
        text.ShouldContain("Exec=\"/opt/Tian Wen/bin \\\"new\\\"/tianwen-server\" --keeper");

        entries.Set(false, server);
        entries.IsSet.ShouldBeFalse();
    }

    [Fact]
    public void ALaunchAgentStartsTheKeeperAtLoadAndGoesWhenUnset()
    {
        var agents = new NodeLogonStart.LaunchAgent(Directory.CreateTempSubdirectory("twla").FullName);

        agents.Set(true, "/Applications/Tian & Wen.app/Contents/MacOS/tianwen-server");

        var plist = File.ReadAllText(agents.PlistPath);
        plist.ShouldContain("<string>/Applications/Tian &amp; Wen.app/Contents/MacOS/tianwen-server</string>");
        plist.ShouldContain("<string>--keeper</string>");
        plist.ShouldContain("<key>RunAtLoad</key>");

        agents.Set(false, "unused");
        agents.IsSet.ShouldBeFalse();
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void OnWindowsARunValueStartsTheKeeperAndGoesWhenUnset()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the Run key is Windows's");
        // A key of the test's own, never the user's Run key, and nothing of it left behind.
        const string TestKeys = @"Software\TianWen.Tests";
        var keyPath = $@"{TestKeys}\{Guid.NewGuid():N}";
        try
        {
            var run = new NodeLogonStart.RunKey(keyPath);
            run.Set(true, @"C:\Program Files\TianWen\tianwen-server.exe");

            run.IsSet.ShouldBeTrue();
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
            {
                key.ShouldNotBeNull().GetValue(NodeLogonStart.Name).ShouldBe(@"""C:\Program Files\TianWen\tianwen-server.exe"" --keeper");
            }

            run.Set(false, "unused");
            run.IsSet.ShouldBeFalse();
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
            using (var parent = Registry.CurrentUser.OpenSubKey(TestKeys))
            {
                if (parent is { SubKeyCount: 0, ValueCount: 0 })
                {
                    parent.Close();
                    Registry.CurrentUser.DeleteSubKey(TestKeys, throwOnMissingSubKey: false);
                }
            }
        }
    }
}
