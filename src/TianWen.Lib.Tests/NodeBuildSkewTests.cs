using System;
using System.IO;
using Shouldly;
using TianWen.Hosting.Dto;
using TianWen.Lib;
using TianWen.RemoteClient;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Whether the node on the socket runs the build a client would start (decision 2 of docs/plans/hardware-in-the-server.md,
/// amended 2026-09-30): a client of the same wire used to attach to any node without a word, and a CLI burst ran another
/// build's code. It is another build when it runs from another folder, or that folder has newer code than it had when the
/// node started, or the node does not say.
/// </summary>
public class NodeBuildSkewTests
{
    private static readonly string Here = Path.Combine(Path.GetTempPath(), "tianwen-build", "gui", "bin");
    private static readonly DateTime Written = new DateTime(2026, 9, 30, 9, 56, 26, DateTimeKind.Utc);

    private static NodeInfoDto Node(string? folder, DateTime? written) => new NodeInfoDto
    {
        NodeId = "node",
        Version = "9.0.0+88014150",
        InstallFolder = folder,
        BuildWrittenUtc = written is { } w ? new DateTimeOffset(w, TimeSpan.Zero) : null,
    };

    [Fact]
    public void A_node_from_this_folder_as_it_is_now_is_this_build()
    {
        LocalNodeLauncher.IsAnotherBuild(Node(Here, Written), Here, Written).ShouldBeFalse();
        LocalNodeLauncher.IsAnotherBuild(Node(Here + Path.DirectorySeparatorChar, Written), Here, Written).ShouldBeFalse("a trailing separator is the same folder");
    }

    [Fact]
    public void A_node_from_another_folder_is_another_build()
    {
        // The GUI's node, found by a CLI that would start its own from its own output.
        LocalNodeLauncher.IsAnotherBuild(Node(Path.Combine(Path.GetTempPath(), "tianwen-build", "cli", "bin"), Written), Here, Written).ShouldBeTrue();
    }

    [Fact]
    public void A_node_whose_folder_has_newer_code_than_when_it_started_is_another_build()
    {
        // Rebuilt under it: a Unix file system lets a running node's files be replaced.
        LocalNodeLauncher.IsAnotherBuild(Node(Here, Written), Here, Written.AddMinutes(5)).ShouldBeTrue();
    }

    [Fact]
    public void A_node_that_does_not_say_is_older_and_so_another_build()
    {
        LocalNodeLauncher.IsAnotherBuild(Node(null, null), Here, Written).ShouldBeTrue();
        LocalNodeLauncher.IsAnotherBuild(Node(Here, null), Here, Written).ShouldBeTrue();
    }

    [Fact]
    public void The_newest_code_is_the_newest_assembly_library_or_executable_and_nothing_else()
    {
        var folder = Directory.CreateTempSubdirectory("tianwen-newest-");
        try
        {
            var dll = Path.Combine(folder.FullName, "TianWen.Lib.dll");
            var native = Directory.CreateDirectory(Path.Combine(folder.FullName, "runtimes", "native")).FullName;
            var so = Path.Combine(native, "libSDL3.so");
            var exe = Path.Combine(folder.FullName, "tianwen-server");
            var log = Path.Combine(folder.FullName, "gui-stdout.log");
            foreach (var (path, when) in new[] { (dll, Written), (so, Written.AddMinutes(2)), (exe, Written.AddMinutes(1)), (log, Written.AddHours(1)) })
            {
                File.WriteAllText(path, "x");
                File.SetLastWriteTimeUtc(path, when);
            }

            BuildInfo.NewestCodeFileUtc(folder.FullName, exe).ShouldBe(Written.AddMinutes(2), "the native library below, not the log");

            File.SetLastWriteTimeUtc(exe, Written.AddMinutes(3));
            BuildInfo.NewestCodeFileUtc(folder.FullName, exe).ShouldBe(Written.AddMinutes(3), "an extensionless executable counts when named");
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }
}
