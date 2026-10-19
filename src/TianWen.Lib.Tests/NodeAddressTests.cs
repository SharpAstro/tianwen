using Shouldly;
using System;
using System.IO;
using TianWen.Hosting;
using TianWen.Hosting.Api;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Where the machine's node listens and which node it is (P1 of docs/plans/hardware-in-the-server.md, #917): the
/// socket path helper every node and client share, the lock that admits one node to a socket, and the node's stable
/// id.
/// </summary>
public class NodeAddressTests(ITestOutputHelper outputHelper) : IDisposable
{
    /// <summary>The temporary folders this test made, deleted after it (#1197).</summary>
    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    [Fact]
    public void ASocketPathThatFitsASocketAddressIsAccepted()
    {
        var path = PathOfBytes(NodeSocket.MaxPathBytes);

        NodeSocket.TryValidate(path, out var error).ShouldBeTrue(error);
    }

    [Fact]
    public void ASocketPathTooLongForASocketAddressIsRefusedByName()
    {
        // Bound truncated, the node would listen somewhere no client looks.
        var path = PathOfBytes(NodeSocket.MaxPathBytes + 1);

        NodeSocket.TryValidate(path, out var error).ShouldBeFalse();
        error.ShouldNotBeNull().ShouldContain(path);
    }

    [Fact]
    public void ASocketPathIsMeasuredInBytesNotCharacters()
    {
        // A user name with an accent costs two bytes per letter in sun_path.
        var path = PathOfBytes(NodeSocket.MaxPathBytes - 1) + "é";

        path.Length.ShouldBeLessThanOrEqualTo(NodeSocket.MaxPathBytes);
        NodeSocket.TryValidate(path, out _).ShouldBeFalse();
    }

    [Fact]
    public void TheDefaultSocketFitsAndLivesUnderTheAppDataRoot()
    {
        NodeSocket.TryValidate(NodeSocket.DefaultPath, out var error).ShouldBeTrue(error);
        Path.GetFileName(NodeSocket.DefaultPath).ShouldBe(NodeSocket.DefaultFileName);
        Path.GetDirectoryName(NodeSocket.DefaultPath).ShouldBe(TianWenDataRoot.Directory.FullName);
    }

    [Fact]
    public void ADataRootThatFitsKeepsItsSocketBesideItsData()
    {
        var root = _folders.Create("twsock").FullName;
        var shortFolder = Path.Combine(_folders.Create("twshort").FullName, NodeSocket.ShortFolderName);

        NodeSocket.DefaultPathFor(root, shortFolder).ShouldBe(Path.Combine(root, NodeSocket.DefaultFileName));
        Directory.Exists(shortFolder).ShouldBeFalse("no short folder is made for a root that needs none");
    }

    /// <summary>
    /// A data root kept apart for a screenshot's node, under the session's scratch folder, put the socket at 183 bytes against
    /// the 107 a socket address holds, and the node refused to start (2026-10-19).
    /// </summary>
    [Fact]
    public void ADataRootTooDeepForASocketGetsAShortSocketOfItsOwnThatEveryoneComputesAlike()
    {
        var parent = _folders.Create("twsock").FullName;
        var deep = Path.Combine(parent, new string('d', 60), new string('e', 60));
        var other = Path.Combine(parent, new string('d', 60), new string('f', 60));
        var shortFolder = Path.Combine(_folders.Create("twshort").FullName, NodeSocket.ShortFolderName);
        NodeSocket.TryValidate(Path.Combine(deep, NodeSocket.DefaultFileName), out _).ShouldBeFalse("premise: beside its data it does not fit");

        var socket = NodeSocket.DefaultPathFor(deep, shortFolder);
        outputHelper.WriteLine(socket);

        NodeSocket.TryValidate(socket, out var error).ShouldBeTrue(error);
        Path.GetDirectoryName(socket).ShouldBe(shortFolder);
        Directory.Exists(shortFolder).ShouldBeTrue("the node can bind there");
        NodeSocket.DefaultPathFor(deep + Path.DirectorySeparatorChar, shortFolder).ShouldBe(socket, "the same root, written with a separator at its end");
        if (OperatingSystem.IsWindows())
        {
            NodeSocket.DefaultPathFor(deep.ToUpperInvariant(), shortFolder).ShouldBe(socket, "Windows names a folder case-blind");
        }
        NodeSocket.DefaultPathFor(other, shortFolder).ShouldNotBe(socket, "two roots never share a socket");
        NodeSocket.LockPathFor(socket).ShouldBe(Path.ChangeExtension(socket, ".lock"), "its lock follows it");
    }

    [Fact]
    public void ASocketThatFitsNowhereIsLeftForTheValidationToRefuseByName()
    {
        var parent = _folders.Create("twsock").FullName;
        var deep = Path.Combine(parent, new string('d', 60), new string('e', 60));
        var tooDeep = Path.Combine(parent, new string('s', 120));

        var socket = NodeSocket.DefaultPathFor(deep, tooDeep);

        socket.ShouldBe(Path.Combine(deep, NodeSocket.DefaultFileName));
        NodeSocket.TryValidate(socket, out var error).ShouldBeFalse();
        error.ShouldNotBeNull().ShouldContain(socket);
    }

    [Fact]
    public void ASocketsLockIsNamedAfterItSoAnIsolatedNodeTakesItsOwn()
    {
        var directory = Path.GetTempPath();

        NodeSocket.LockPathFor(Path.Combine(directory, "node.sock")).ShouldBe(Path.Combine(directory, "node.lock"));
        NodeSocket.LockPathFor(Path.Combine(directory, "test.sock")).ShouldBe(Path.Combine(directory, "test.lock"));
    }

    [Fact]
    public void ANamedSocketIsTheArgumentFirstThenTheEnvironmentAndOnlyThenForbidsASpawn()
    {
        var argument = Path.Combine(Path.GetTempPath(), "argument.sock");
        var environment = Path.Combine(Path.GetTempPath(), "environment.sock");
        var saved = Environment.GetEnvironmentVariable(NodeSocket.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(NodeSocket.EnvironmentVariable, environment);
            NodeSocket.TryGetNamed(argument, out var named).ShouldBeTrue();
            named.ShouldBe(argument);

            NodeSocket.TryGetNamed(null, out named).ShouldBeTrue();
            named.ShouldBe(environment);

            Environment.SetEnvironmentVariable(NodeSocket.EnvironmentVariable, null);
            NodeSocket.TryGetNamed(" ", out named).ShouldBeFalse("nothing named: the client may start the machine's node");
            named.ShouldBe(NodeSocket.DefaultPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(NodeSocket.EnvironmentVariable, saved);
        }
    }

    [Fact]
    public void OneNodeHoldsASocketsLockUntilItLetsGo()
    {
        var socketPath = Path.Combine(_folders.Create("twl").FullName, "node.sock");

        NodeLock.TryAcquire(socketPath, out var first, out var refusal).ShouldBeTrue(refusal?.Message);
        using (first)
        {
            NodeLock.TryAcquire(socketPath, out var second, out refusal).ShouldBeFalse("a second node on one socket is two owners of one rig");
            second.ShouldBeNull();
            refusal.ShouldNotBeNull();
        }

        // The file stays (deleting it would race the next holder), and the lock is free again.
        File.Exists(NodeSocket.LockPathFor(socketPath)).ShouldBeTrue();
        NodeLock.TryAcquire(socketPath, out var third, out _).ShouldBeTrue();
        third.Dispose();
    }

    [Fact]
    public void ANodesIdIsMintedOnceAndKept()
    {
        var external = new FakeExternal(outputHelper);

        var first = NodeIdentity.Load(external);
        var second = NodeIdentity.Load(external);

        first.NodeId.ShouldNotBeNullOrWhiteSpace();
        second.NodeId.ShouldBe(first.NodeId, "a restarted node is the same rig, and a binding persists on its id");
        File.ReadAllText(NodeIdentity.IdFilePath(external)).Trim().ShouldBe(first.NodeId);
    }

    [Fact]
    public void ANodeRunWithNoArgumentsIsTheMachinesNodeReachableFromTheLan()
    {
        NodeArguments.TryParse([], out var parsed, out var error).ShouldBeTrue(error);

        parsed.SocketPath.ShouldBe(NodeSocket.DefaultPath);
        parsed.Port.ShouldBe(NodeArguments.DefaultPort);
        parsed.LocalOnly.ShouldBeFalse();
    }

    [Fact]
    public void ABareSwitchDoesNotSwallowTheArgumentAfterIt()
    {
        // The host's command-line configuration reads "--local-only --socket x" as local-only = "--socket".
        var socket = Path.Combine(Path.GetTempPath(), "node.sock");

        NodeArguments.TryParse(["--local-only", "--socket", socket, "--port", "1999"], out var parsed, out var error).ShouldBeTrue(error);

        parsed.LocalOnly.ShouldBeTrue();
        parsed.SocketPath.ShouldBe(socket);
        parsed.Port.ShouldBe(1999);
    }

    [Theory]
    [InlineData("--port", "http")]
    [InlineData("--port", "70000")]
    [InlineData("--urls", "http://*:1888")]
    [InlineData("--socket")]
    public void AWrongCommandLineIsRefusedWithTheUsage(params string[] args)
    {
        NodeArguments.TryParse(args, out var parsed, out var error).ShouldBeFalse();

        parsed.ShouldBeNull();
        error.ShouldNotBeNull().ShouldContain(NodeArguments.Usage);
    }

    [Fact]
    public void ASocketTooLongToBindIsRefusedOnTheCommandLine()
    {
        NodeArguments.TryParse(["--socket", PathOfBytes(NodeSocket.MaxPathBytes + 1)], out _, out var error).ShouldBeFalse();

        error.ShouldNotBeNull().ShouldContain("longer than");
    }

    [Fact]
    public void AKeeperStartsItsNodeWithWhatItWasToldLessKeeperAndAsSpawned()
    {
        var socket = Path.Combine(Path.GetTempPath(), "kept.sock");
        NodeArguments.TryParse(["--keeper", "--socket", socket, "--port", "1999", "--local-only", "--fake-devices", "--detach-grace", "2.5", "--idle-exit", "3600"],
            out var keeper, out var error).ShouldBeTrue(error);
        keeper.Keeper.ShouldBeTrue();
        keeper.DetachGrace.ShouldBe(TimeSpan.FromSeconds(2.5));
        keeper.IdleExit.ShouldBe(TimeSpan.FromHours(1));

        NodeArguments.TryParse([.. keeper.ForTheNode()], out var node, out error).ShouldBeTrue(error);

        node.ShouldBe(keeper with { Keeper = false, Spawned = true });
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("soon")]
    public void ADetachGraceIsAPositiveNumberOfSeconds(string seconds)
    {
        NodeArguments.TryParse(["--detach-grace", seconds], out _, out var error).ShouldBeFalse();
        error.ShouldNotBeNull().ShouldStartWith("--detach-grace takes a number of seconds");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("soon")]
    public void AnIdleExitIsAPositiveNumberOfSeconds(string seconds)
    {
        NodeArguments.TryParse(["--idle-exit", seconds], out _, out var error).ShouldBeFalse();
        error.ShouldNotBeNull().ShouldStartWith("--idle-exit takes a number of seconds");
    }

    [Fact]
    public void ADataRootNamedByTheEnvironmentIsUsedAndMadeAndNoneNamedIsTheUsers()
    {
        var named = Path.Combine(_folders.Create("twroot-").FullName, "data");

        var root = SharedStaticData.ResolveCommonDataRoot(named);

        root.FullName.ShouldBe(Path.GetFullPath(named));
        root.Exists.ShouldBeTrue();
        SharedStaticData.ResolveCommonDataRoot(" ").FullName
            .ShouldBe(Environment.SpecialFolder.LocalApplicationData.CreateAppSubFolder(SharedStaticData.AppName).FullName);
    }

    /// <summary>A full path of exactly <paramref name="bytes"/> UTF-8 bytes.</summary>
    private static string PathOfBytes(int bytes)
    {
        var root = Path.GetTempPath();
        return root + new string('s', bytes - root.Length);
    }
}
