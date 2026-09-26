using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using TianWen.Hosting;
using TianWen.Hosting.Api;
using TianWen.Hosting.Dto;
using TianWen.RemoteClient;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// "Share this rig on the LAN" (P1 of docs/plans/hardware-in-the-server.md, #917, decision 3): a machine setting the
/// node keeps, changed only over its socket, with the logon entry that starts the node while it is on. An idle node a
/// client started restarts to apply it; one holding the rig applies it at its next start. The logon entry here is a
/// stand-in that records, so no test writes the user's real one.
/// </summary>
[Collection("Hosting")]
public class NodeShareTests(ITestOutputHelper outputHelper)
{
    private sealed class RecordedLogonStart : INodeLogonStart
    {
        public string? ServerPath { get; private set; }

        public bool IsSet => ServerPath is not null;

        public void Set(bool startAtLogon, string serverPath) => ServerPath = startAtLogon ? serverPath : null;
    }

    private static string NewSocketPath() => Path.Combine(Directory.CreateTempSubdirectory("tws").FullName, "node.sock");

    private static async Task<(int Status, NodeShareDto? Share)> ShareAsync(HttpClient client, bool shared)
    {
        using var response = await client.PutAsJsonAsync("api/v1/node/share", new NodeShareRequest { Shared = shared }, HostingJsonContext.Default.NodeShareRequest);
        var envelope = await response.Content.ReadFromJsonAsync(HostingJsonContext.Default.ResponseEnvelopeNodeShareDto);
        return (envelope.ShouldNotBeNull().StatusCode, envelope.Response);
    }

    [Fact(Timeout = 30_000)]
    public async Task SharingIsKeptAndStartsTheNodeAtLogonAndUnsharingUndoesBoth()
    {
        var ct = TestContext.Current.CancellationToken;
        var logon = new RecordedLogonStart();
        await using var node = await NodeHarness.StartAsync(outputHelper, ct,
            services => services.AddSingleton<INodeLogonStart>(logon), socketPath: NewSocketPath());
        var settingsFile = NodeSettings.PathIn(node.External.AppDataFolder);

        var (status, share) = await ShareAsync(node.Client, shared: true);

        status.ShouldBe(200);
        share.ShouldNotBeNull().Shared.ShouldBeTrue();
        logon.ServerPath.ShouldBe(Environment.ProcessPath, "the entry starts the executable the node runs from");
        JsonDocument.Parse(await File.ReadAllTextAsync(settingsFile, ct)).RootElement.GetProperty("shareOnLan").GetBoolean().ShouldBeTrue();
        (await new TianWenNodeClient(node.Client).GetNodeAsync(ct)).Value.ShouldNotBeNull().ShareOnLan.ShouldBeTrue();

        (status, share) = await ShareAsync(node.Client, shared: false);

        status.ShouldBe(200);
        logon.IsSet.ShouldBeFalse("turning it off removes the entry");
        JsonDocument.Parse(await File.ReadAllTextAsync(settingsFile, ct)).RootElement.GetProperty("shareOnLan").GetBoolean().ShouldBeFalse();
    }

    [Fact(Timeout = 30_000)]
    public async Task SharingIsRefusedOverTcp()
    {
        // Only a user of this machine may expose it: a LAN client keeps today's rights (decision 4's rule).
        var ct = TestContext.Current.CancellationToken;
        var logon = new RecordedLogonStart();
        await using var node = await NodeHarness.StartAsync(outputHelper, ct, services => services.AddSingleton<INodeLogonStart>(logon));

        (await ShareAsync(node.Client, shared: true)).Status.ShouldBe(403);

        logon.IsSet.ShouldBeFalse();
        File.Exists(NodeSettings.PathIn(node.External.AppDataFolder)).ShouldBeFalse();
    }

    [Fact(Timeout = 30_000)]
    public async Task AnIdleNodeAClientStartedRestartsToApplySharing()
    {
        var ct = TestContext.Current.CancellationToken;
        var role = new NodeRole(spawned: true);
        await using var node = await NodeHarness.StartAsync(outputHelper, ct,
            services => services.AddSingleton(role).AddSingleton<INodeLogonStart>(new RecordedLogonStart()), socketPath: NewSocketPath());

        var (status, share) = await ShareAsync(node.Client, shared: true);

        status.ShouldBe(202);
        share.ShouldNotBeNull().Restarting.ShouldBeTrue();
        node.App.Lifetime.ApplicationStopping.IsCancellationRequested.ShouldBeTrue("it stops at once, for its keeper to start it again");
        role.ExitCode.ShouldBe(NodeExitCodes.Restart);
    }

    [Fact(Timeout = 30_000)]
    public async Task ANodeHoldingTheRigAppliesSharingAtItsNextStart()
    {
        var ct = TestContext.Current.CancellationToken;
        var role = new NodeRole(spawned: true);
        await using var node = await NodeHarness.StartAsync(outputHelper, ct,
            services => services.AddSingleton(role).AddSingleton<INodeLogonStart>(new RecordedLogonStart()), socketPath: NewSocketPath());
        node.Factory.Initialised.SetResult();
        await node.StartSessionAsync(ct);

        var (status, share) = await ShareAsync(node.Client, shared: true);

        status.ShouldBe(200);
        share.ShouldNotBeNull().Restarting.ShouldBeFalse();
        share.Shared.ShouldBeTrue("it is saved");
        node.App.Lifetime.ApplicationStopping.IsCancellationRequested.ShouldBeFalse("a restart would end the night");
        role.ExitCode.ShouldBe(NodeExitCodes.Stopped);
    }

    [Fact(Timeout = 30_000)]
    public async Task AHostThatCannotStartTheNodeAtLogonCannotShare()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NodeHarness.StartAsync(outputHelper, ct, socketPath: NewSocketPath());

        (await ShareAsync(node.Client, shared: true)).Status.ShouldBe(501);

        File.Exists(NodeSettings.PathIn(node.External.AppDataFolder)).ShouldBeFalse("nothing half-done: no setting without its entry");
    }
}
