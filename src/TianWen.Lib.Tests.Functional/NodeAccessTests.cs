using LAN.Lib;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting;
using TianWen.Hosting.Api;
using TianWen.Hosting.Dto;
using TianWen.Hosting.WebSocket;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.RemoteClient;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// Control over the LAN by grant (P6b of docs/plans/hardware-in-the-server.md, decision 13, #1021), against a real node
/// over loopback TCP, which the node treats as the LAN. The harness's own client holds a grant (it is the rig's owner
/// here); each test makes the clients it needs without one.
/// </summary>
[Collection("Hosting")]
#pragma warning disable CS8774 // MemberNotNull on InitializeAsync; xUnit guarantees init before tests
#pragma warning disable CS8602 // Dereference of possibly null; same reason
public class NodeAccessTests(ITestOutputHelper outputHelper) : IAsyncLifetime
{
    /// <summary>What a reverse lookup of loopback answers here, forward-confirmed, as a router's DHCP registration would.</summary>
    private const string LoopbackHost = "laptop.lan";

    private NodeHarness? _harness;

    /// <summary>A resolver that names loopback, and nothing else, so an Always allow has a name to remember.</summary>
    private sealed class LoopbackNamed : IHostNameResolver
    {
        public Task<string?> ReverseAsync(IPAddress address, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(IPAddress.IsLoopback(address) ? LoopbackHost : null);

        public Task<IReadOnlyList<IPAddress>> ForwardAsync(string hostName, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IPAddress>>(hostName == LoopbackHost ? [IPAddress.Loopback, IPAddress.IPv6Loopback] : []);
    }

    [MemberNotNull(nameof(_harness))]
    public async ValueTask InitializeAsync() => _harness = await NodeHarness.StartAsync(outputHelper, TestContext.Current.CancellationToken,
        services => services.Replace(ServiceDescriptor.Singleton<IHostNameResolver, LoopbackNamed>()));

    public async ValueTask DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }

    private NodeAccess Access => _harness.App.Services.GetRequiredService<NodeAccess>();

    /// <summary>A client over TCP holding nothing: a laptop the rig's owner has not allowed.</summary>
    private HttpClient Stranger() => NodeTransport.OverTcp(_harness.Transport.BaseAddress).CreateHttpClient();

    /// <summary>The harness's own client, which holds a grant: the rig's owner, for these tests.</summary>
    private TianWenNodeClient Owner => new TianWenNodeClient(_harness.Client);

    [Fact(Timeout = 60_000)]
    public async Task OverTcpSeeingIsFreeAndACommandNeedsControl()
    {
        var ct = TestContext.Current.CancellationToken;
        using var stranger = Stranger();
        var client = new TianWenNodeClient(stranger);

        var node = await client.GetNodeAsync(ct);
        node.IsSuccess.ShouldBeTrue("a read needs nothing");
        node.Value.ShouldNotBeNull().CallerMayCommand.ShouldBeFalse();

        using var abort = await stranger.PostAsync("api/v1/session/abort", content: null, ct);
        abort.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "a command over TCP without a grant is refused");
        abort.Headers.WwwAuthenticate.ToString().ShouldStartWith("Bearer");

        (await Owner.GetNodeAsync(ct)).Value.ShouldNotBeNull().CallerMayCommand.ShouldBeTrue("the owner's client holds a grant");
        (await Owner.AbortSessionAsync(ct)).StatusCode.ShouldNotBe(401, "a granted client's command is let through to its route");
    }

    [Fact(Timeout = 60_000)]
    public async Task AClientAsksTheRigsOwnerAllowsAndOnlyTheAskerGetsTheToken()
    {
        var ct = TestContext.Current.CancellationToken;
        using var laptopHttp = Stranger();
        var laptop = new TianWenNodeClient(laptopHttp);

        var asked = await laptop.RequestControlAsync("Laptop, TianWen", ct);
        asked.IsSuccess.ShouldBeTrue(asked.Error);
        var ticket = asked.Value.ShouldNotBeNull();

        using var tabletHttp = Stranger();
        (await new TianWenNodeClient(tabletHttp).RequestControlAsync("Tablet", ct)).StatusCode
            .ShouldBe(409, "a person answers one request at a time");

        (await laptop.PollControlRequestAsync(ticket, ct)).Value.ShouldNotBeNull().State.ShouldBe(ControlRequestState.Pending);
        (await laptop.PollControlRequestAsync(new ControlRequestTicketDto { Id = ticket.Id, Secret = "not-the-secret" }, ct))
            .Value.ShouldNotBeNull().State.ShouldBe(ControlRequestState.Unknown, "the request's id alone collects nothing");

        var pending = (await Owner.GetAccessAsync(ct)).Value.ShouldNotBeNull().Pending.ShouldNotBeNull();
        pending.Id.ShouldBe(ticket.Id);
        pending.Label.ShouldBe("Laptop, TianWen");
        (await laptop.AnswerControlRequestAsync(ticket.Id, allow: true, ct)).StatusCode.ShouldBe(401, "an asker cannot answer itself");
        (await Owner.AnswerControlRequestAsync(ticket.Id, allow: true, ct)).IsSuccess.ShouldBeTrue();

        var granted = (await laptop.PollControlRequestAsync(ticket, ct)).Value.ShouldNotBeNull();
        granted.State.ShouldBe(ControlRequestState.Granted);
        var token = granted.Token.ShouldNotBeNull();
        (await laptop.PollControlRequestAsync(ticket, ct)).Value.ShouldNotBeNull().Token.ShouldBeNull("the token is handed over once");

        var grant = new NodeGrant(token);
        using var grantedHttp = NodeTransport.OverTcp(_harness.Transport.BaseAddress, grant).CreateHttpClient();
        (await new TianWenNodeClient(grantedHttp).GetNodeAsync(ct)).Value.ShouldNotBeNull().CallerMayCommand.ShouldBeTrue();
        (await Owner.GetAccessAsync(ct)).Value.ShouldNotBeNull().Grants.ShouldContain(held => held.Label == "Laptop, TianWen");
    }

    [Fact(Timeout = 60_000)]
    public async Task ADeclinedRequestGrantsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var laptopHttp = Stranger();
        var laptop = new TianWenNodeClient(laptopHttp);
        var ticket = (await laptop.RequestControlAsync("Laptop", ct)).Value.ShouldNotBeNull();

        (await Owner.AnswerControlRequestAsync(ticket.Id, allow: false, ct)).IsSuccess.ShouldBeTrue();

        var outcome = (await laptop.PollControlRequestAsync(ticket, ct)).Value.ShouldNotBeNull();
        outcome.State.ShouldBe(ControlRequestState.Declined);
        outcome.Token.ShouldBeNull();
        (await Owner.GetAccessAsync(ct)).Value.ShouldNotBeNull().Grants.ShouldNotContain(held => held.Label == "Laptop");
    }

    [Fact(Timeout = 60_000)]
    public async Task ARevokedGrantIsRefusedFromItsNextCommandOn()
    {
        var ct = TestContext.Current.CancellationToken;
        var issued = await Access.GrantAsync("Laptop", ct);
        using var laptopHttp = NodeTransport.OverTcp(_harness.Transport.BaseAddress, new NodeGrant(issued.Token)).CreateHttpClient();
        var laptop = new TianWenNodeClient(laptopHttp);
        (await laptop.AbortSessionAsync(ct)).StatusCode.ShouldNotBe(401);

        (await Owner.RevokeGrantAsync(issued.Grant.Id, ct)).IsSuccess.ShouldBeTrue();

        (await laptop.AbortSessionAsync(ct)).StatusCode.ShouldBe(401);
        (await laptop.GetNodeAsync(ct)).Value.ShouldNotBeNull().CallerMayCommand.ShouldBeFalse();
    }

    [Fact(Timeout = 60_000)]
    public async Task AGrantIsRememberedAcrossARestartAndItsTokenIsNotKeptAtRest()
    {
        var ct = TestContext.Current.CancellationToken;
        var issued = await Access.GrantAsync("Laptop", ct);

        var restarted = new NodeAccess(_harness.External, _harness.App.Services.GetRequiredService<NodeSettingsStore>(),
            _harness.App.Services.GetRequiredService<EventHub>(), new SystemTimeProvider(), new LoopbackNamed(), NullLogger<NodeAccess>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer " + issued.Token;
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        (await restarted.MayCommandAsync(context, ct)).ShouldBeTrue("a grant is remembered until revoked");

        var file = await System.IO.File.ReadAllTextAsync(System.IO.Path.Combine(_harness.External.AppDataFolder.FullName, NodeAccess.GrantsFileName), ct);
        file.ShouldNotContain(issued.Token, Case.Sensitive, "a copy of the file must grant nothing");
    }

    [Fact(Timeout = 60_000)]
    public async Task AnAlpacaConnectFromAnAppNotAllowedIsRefusedAndIsItsRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        // The harness let other applications on loopback in; this one is a stranger.
        (await Owner.RevokeAppAsync(IPAddress.Loopback.ToString(), ct)).IsSuccess.ShouldBeTrue();
        (await Owner.RevokeAppAsync(IPAddress.IPv6Loopback.ToString(), ct)).IsSuccess.ShouldBeTrue();
        using var nina = new HttpClient { BaseAddress = _harness.Transport.BaseAddress };
        nina.DefaultRequestHeaders.UserAgent.ParseAdd("NINA/3.1");

        (await nina.GetAsync("management/v1/configureddevices", ct)).StatusCode.ShouldBe(HttpStatusCode.OK, "the rig's devices are there to see");
        var refused = await ConnectAsync(nina, ct);
        refused.GetProperty("ErrorNumber").GetInt32().ShouldBe(AlpacaErrorInvalidOperation, "Connected = true connects the hardware, so it is a command");
        refused.GetProperty("ErrorMessage").GetString().ShouldNotBeNull().ShouldContain("Sharing panel");

        var record = (await Owner.GetAccessAsync(ct)).Value.ShouldNotBeNull().Refused.ShouldHaveSingleItem();
        record.Protocol.ShouldBe(AppProtocol.Alpaca);
        record.UserAgent.ShouldBe("NINA/3.1");
        record.ClientId.ShouldBe("42");
        record.What.ShouldContain("PUT");
        await ConnectAsync(nina, ct);
        (await Owner.GetAccessAsync(ct)).Value.ShouldNotBeNull().Refused.ShouldHaveSingleItem().Attempts.ShouldBe(2, "a retry updates the record");

        (await Owner.AllowAppAsync(record.Address, always: false, ct)).IsSuccess.ShouldBeTrue();
        var letIn = await ConnectAsync(nina, ct);
        letIn.GetProperty("ErrorNumber").GetInt32().ShouldNotBe(AlpacaErrorInvalidOperation, "the next attempt is let in, to the device plane's own answer");
        (letIn.GetProperty("ErrorMessage").GetString() ?? "").ShouldNotContain("Sharing panel");
        (await Owner.GetAccessAsync(ct)).Value.ShouldNotBeNull().Refused.ShouldBeEmpty();
    }

    [Fact(Timeout = 60_000)]
    public async Task AnAllowLastsUntilTheNodeRestartsAndAnAlwaysAllowByItsHostName()
    {
        var ct = TestContext.Current.CancellationToken;
        var address = IPAddress.Loopback;
        var settings = _harness.App.Services.GetRequiredService<NodeSettingsStore>();
        NodeAccess Restarted() => new NodeAccess(_harness.External, settings, _harness.App.Services.GetRequiredService<EventHub>(),
            new SystemTimeProvider(), new LoopbackNamed(), NullLogger<NodeAccess>.Instance);

        (await Access.AppMayCommandAsync(address, ct)).ShouldBeTrue("the harness allowed loopback");
        (await Restarted().AppMayCommandAsync(address, ct)).ShouldBeFalse("an Allow lasts until the node restarts");

        (await Owner.AllowAppAsync(address.ToString(), always: true, ct)).IsSuccess.ShouldBeTrue();

        settings.Current.AlwaysAllowedHosts.ShouldNotBeNull().ShouldBe([LoopbackHost]);
        (await Restarted().AppMayCommandAsync(address, ct)).ShouldBeTrue("an Always allow is its host name, kept by the node");
        (await Owner.RevokeHostAsync(LoopbackHost, ct)).IsSuccess.ShouldBeTrue();
        (await Restarted().AppMayCommandAsync(address, ct)).ShouldBeFalse();
    }

    [Fact(Timeout = 60_000)]
    public async Task AnAddressWithNoConfirmedNameCanBeAllowedOnlyUntilTheNodeRestarts()
    {
        var ct = TestContext.Current.CancellationToken;

        var always = await Owner.AllowAppAsync("192.168.1.77", always: true, ct);

        always.StatusCode.ShouldBe(409);
        _harness.App.Services.GetRequiredService<NodeSettingsStore>().Current.AlwaysAllowedHosts.ShouldBeNull();
    }

    [Fact(Timeout = 60_000)]
    public async Task AninaApiReadIsFreeAndItsCommandIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        (await Owner.RevokeAppAsync(IPAddress.Loopback.ToString(), ct)).IsSuccess.ShouldBeTrue();
        (await Owner.RevokeAppAsync(IPAddress.IPv6Loopback.ToString(), ct)).IsSuccess.ShouldBeTrue();
        using var touchNStars = new HttpClient { BaseAddress = _harness.Transport.BaseAddress };

        using var version = await touchNStars.GetAsync("v2/api/version", ct);
        var read = (await version.Content.ReadFromJsonAsync<JsonElement>(ct)).Clone();
        read.GetProperty("Success").GetBoolean().ShouldBeTrue("a ninaAPI read needs nothing, and its refusal would be a 200 too");
        read.GetProperty("StatusCode").GetInt32().ShouldBe(200);
        using var park = await touchNStars.GetAsync("v2/api/equipment/mount/park", ct);

        var refused = (await park.Content.ReadFromJsonAsync<JsonElement>(ct)).Clone();
        refused.GetProperty("StatusCode").GetInt32().ShouldBe(403, "a ninaAPI command is a GET, refused all the same");
        refused.GetProperty("Success").GetBoolean().ShouldBeFalse();
        refused.GetProperty("Error").GetString().ShouldNotBeNull().ShouldContain("Sharing panel");
        (await Owner.GetAccessAsync(ct)).Value.ShouldNotBeNull().Refused.ShouldHaveSingleItem().Protocol.ShouldBe(AppProtocol.NinaV2);
    }

    [Fact(Timeout = 60_000)]
    public async Task WhoMayCommandTheRigIsForItsOwnerOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        using var stranger = Stranger();

        (await new TianWenNodeClient(stranger).GetAccessAsync(ct)).StatusCode.ShouldBe(401);
        (await new TianWenNodeClient(stranger).SetShareAsync(true, ct)).StatusCode.ShouldBe(401, "a stranger cannot put the rig on the LAN");
        (await Owner.GetAccessAsync(ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact(Timeout = 60_000)]
    public async Task AWatcherOverTcpIsPresentButAnswersNoPrompt()
    {
        var ct = TestContext.Current.CancellationToken;
        var hub = _harness.App.Services.GetRequiredService<EventHub>();
        await using var watcher = NodeTransport.OverTcp(_harness.Transport.BaseAddress).CreateEventStream(new SystemTimeProvider(), NullLogger.Instance);
        await using var owner = _harness.Transport.CreateEventStream(new SystemTimeProvider(), NullLogger.Instance);
        watcher.Start(ct);
        owner.Start(ct);

        await BeatingAsync([watcher, owner], async () =>
        {
            await NodeWait.UntilAsync("both streams present", _ =>
                ValueTask.FromResult((hub.PresentClientCount == 2, $"{hub.PresentClientCount} present")), ct);
            hub.AnsweringClientCount.ShouldBe(1, "only the granted one can answer a prompt");
            hub.CommandingClientCount.ShouldBe(1, "only the granted one is asked before its window closes");
            (await Owner.GetNodeAsync(ct)).Value.ShouldNotBeNull().ClientsAttached.ShouldBe(1, "a watcher is not a client a quit asks");
        }, ct);
    }

    [Fact(Timeout = 60_000)]
    public async Task AWatcherWithoutControlHoldsNoPrompt()
    {
        // Presence alone is not enough to wait for: a watcher could not answer, so the session's own unattended answer
        // comes at once, as with nobody attached.
        var ct = TestContext.Current.CancellationToken;
        _harness.Factory.Initialised.SetResult();
        var run = await _harness.StartSessionAsync(ct);
        var hub = _harness.App.Services.GetRequiredService<EventHub>();
        await using var watcher = NodeTransport.OverTcp(_harness.Transport.BaseAddress).CreateEventStream(new SystemTimeProvider(), NullLogger.Instance);
        watcher.Start(ct);
        await BeatingAsync([watcher], async () =>
        {
            await NodeWait.UntilAsync("the watcher present", _ => ValueTask.FromResult((hub.PresentClientCount == 1, $"{hub.PresentClientCount} present")), ct);

            var answer = RaisePrompt(run);

            answer.Task.IsCompletedSuccessfully.ShouldBeTrue("nobody who may answer is there, so the prompt is not held");
            (await answer.Task).ShouldBeFalse();
        }, ct);
    }

    [Fact(Timeout = 60_000)]
    public async Task APromptHeldForTheOwnerIsLetGoOnceOnlyAWatcherIsLeft()
    {
        var ct = TestContext.Current.CancellationToken;
        _harness.Factory.Initialised.SetResult();
        var run = await _harness.StartSessionAsync(ct);
        var hub = _harness.App.Services.GetRequiredService<EventHub>();
        await using var watcher = NodeTransport.OverTcp(_harness.Transport.BaseAddress).CreateEventStream(new SystemTimeProvider(), NullLogger.Instance);
        await using var owner = _harness.Transport.CreateEventStream(new SystemTimeProvider(), NullLogger.Instance);
        watcher.Start(ct);
        owner.Start(ct);
        await BeatingAsync([watcher], async () =>
        {
            TaskCompletionSource<bool>? answer = null;
            await BeatingAsync([owner], async () =>
            {
                await NodeWait.UntilAsync("both present", _ => ValueTask.FromResult((hub.AnsweringClientCount == 1 && hub.PresentClientCount == 2,
                    $"{hub.PresentClientCount} present, {hub.AnsweringClientCount} answering")), ct);
                answer = RaisePrompt(run);
                await Task.Delay(NodeWire.PresenceBeatInterval * 2, ct);
                answer.Task.IsCompleted.ShouldBeFalse("the owner's window is drawing, so its human may still answer");
            }, ct);

            // The owner's window froze; the watcher's goes on drawing, and it cannot answer.
            (await answer.ShouldNotBeNull().Task.WaitAsync(NodeWire.PresenceLapse + TimeSpan.FromSeconds(5), ct)).ShouldBeFalse();
        }, ct);
    }

    [Fact(Timeout = 60_000)]
    public async Task AGrantRevokedWhileItsSocketIsOpenStopsCountingAtOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var hub = _harness.App.Services.GetRequiredService<EventHub>();
        var issued = await Access.GrantAsync("Laptop", ct);
        await using var laptop = NodeTransport.OverTcp(_harness.Transport.BaseAddress, new NodeGrant(issued.Token))
            .CreateEventStream(new SystemTimeProvider(), NullLogger.Instance);
        var drops = 0;
        laptop.ConnectedChanged += (_, connected) =>
        {
            if (!connected)
            {
                Interlocked.Increment(ref drops);
            }
        };
        laptop.Start(ct);

        // Attached on BOTH sides: the node counts the laptop once it has accepted the socket, and the laptop says so only
        // once its own connect has returned, a moment later. Waiting on the node's count alone let a loaded run revoke
        // inside that moment and then read the laptop as not connected, which it had never yet been.
        await NodeWait.UntilAsync("the laptop attached", _ => ValueTask.FromResult((hub.CommandingClientCount == 1 && laptop.IsConnected,
            $"{hub.CommandingClientCount} commanding, laptop connected {laptop.IsConnected}")), ct);

        (await Access.RevokeAsync(issued.Grant.Id, ct)).ShouldBeTrue();

        hub.CommandingClientCount.ShouldBe(0, "its socket was upgraded with a grant it no longer holds");
        laptop.IsConnected.ShouldBeTrue("revoking a grant closes nothing; the laptop goes on seeing");
        Volatile.Read(ref drops).ShouldBe(0, "nor did the socket drop and come back");
    }

    [Fact(Timeout = 60_000)]
    public async Task AnIPv4AddressSeenThroughAnIPv6SocketIsTheIPv4Address()
    {
        // A dual-stack listener sees 192.168.1.5 as ::ffff:192.168.1.5; the Sharing panel shows and allows the first.
        var ct = TestContext.Current.CancellationToken;
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback.MapToIPv6();

        var address = NodeAccess.AddressOf(context).ShouldNotBeNull();

        address.ShouldBe(IPAddress.Loopback);
        (await Access.AppMayCommandAsync(address, ct)).ShouldBeTrue("the harness allowed 127.0.0.1, which this is");
    }

    [Fact(Timeout = 60_000)]
    public async Task AClientOfThisMachineNeedsNoGrantAndHasNoneToAskFor()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var local = await NodeHarness.StartAsync(outputHelper, ct,
            socketPath: Path.Combine(Directory.CreateTempSubdirectory("tws").FullName, "node.sock"));
        var client = new TianWenNodeClient(local.Client);

        (await client.GetNodeAsync(ct)).Value.ShouldNotBeNull().CallerMayCommand.ShouldBeTrue();
        (await client.RequestControlAsync("This machine", ct)).StatusCode.ShouldBe(400);
        (await client.GetAccessAsync(ct)).IsSuccess.ShouldBeTrue("this machine manages who may command its rig");

        var hub = local.App.Services.GetRequiredService<EventHub>();
        await using var window = local.Transport.CreateEventStream(new SystemTimeProvider(), NullLogger.Instance);
        window.Start(ct);
        await BeatingAsync([window], () => NodeWait.UntilAsync("this machine's window present", _ =>
            ValueTask.FromResult((hub.AnsweringClientCount == 1 && hub.CommandingClientCount == 1,
                $"{hub.PresentClientCount} present, {hub.AnsweringClientCount} answering, {hub.CommandingClientCount} commanding")), ct), ct);
    }

    [Fact]
    public void EveryRouteIsOnOneSurfaceAndWhatItMayDoIsDecidedByWhatItIs()
    {
        var endpoints = _harness.App.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToArray();
        endpoints.ShouldNotBeEmpty();

        endpoints.Where(endpoint => endpoint.Metadata.GetMetadata<NodeProtocolMetadata>() is null)
            .Select(endpoint => endpoint.RoutePattern.RawText).ShouldBeEmpty("a route mapped outside the three groups would escape the gate");

        endpoints.Where(endpoint => endpoint.Metadata.GetMetadata<OpenToAskMetadata>() is not null)
            .Select(endpoint => endpoint.RoutePattern.RawText ?? "").Order(StringComparer.Ordinal).ToArray()
            .ShouldBe(["/api/v1/node/control/requests", "/api/v1/node/control/requests/{id}/poll"], customMessage: "asking is the only command open to a stranger");

        string[] ninaReads =
        [
            "/v2/api/equipment/camera/info", "/v2/api/equipment/mount/info", "/v2/api/equipment/focuser/info", "/v2/api/equipment/filterwheel/info",
            "/v2/api/equipment/guider/info", "/v2/api/equipment/guider/graph", "/v2/api/equipment/weather/info", "/v2/api/image-history",
            "/v2/api/prepared-image", "/v2/api/sequence/state", "/v2/api/version", "/v2/api/time", "/v2/api/event-history", "/v2/api/profile/show",
            "/v2/api/profile/list-available", "/v2/socket",
        ];
        var reads = endpoints.Where(endpoint => endpoint.Metadata.GetMetadata<ReadsOnlyMetadata>() is not null)
            .Select(endpoint => endpoint.RoutePattern.RawText ?? "").ToHashSet(StringComparer.Ordinal);
        foreach (var read in ninaReads)
        {
            reads.ShouldContain(read);
        }
        reads.Where(read => read.EndsWith("/connect", StringComparison.Ordinal) || read.EndsWith("/park", StringComparison.Ordinal)
            || read.EndsWith("/start", StringComparison.Ordinal) || read.EndsWith("/switch", StringComparison.Ordinal)).ShouldBeEmpty();
    }

    /// <summary>A manual flat panel prompt on the run's session, which declines when nobody can answer it.</summary>
    private static TaskCompletionSource<bool> RaisePrompt(ControlledSession run)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Session.PromptRequested += Raise.EventWith(run.Session, new SessionPromptEventArgs(
            "Manual flat panel", "Switch on the flat panel for OTA 1, then Continue.",
            "Continue", "Cancel", answer, requiresPhysicalPresence: true, defaultIfUnanswerable: false));
        return answer;
    }

    /// <summary>Runs <paramref name="body"/> while <paramref name="streams"/> beat as a drawing window does, and stops their beats after.</summary>
    private static async Task BeatingAsync(TianWenEventStream[] streams, Func<Task> body, CancellationToken ct)
    {
        using var stopped = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var drawing = Task.Run(async () =>
        {
            while (!stopped.IsCancellationRequested)
            {
                foreach (var stream in streams)
                {
                    stream.Beat();
                }
                await Task.Delay(16, CancellationToken.None);
            }
        }, CancellationToken.None);
        try
        {
            await body();
        }
        finally
        {
            await stopped.CancelAsync();
            await drawing;
        }
    }

    private const int AlpacaErrorInvalidOperation = 0x40B;

    /// <summary>An Alpaca client's <c>PUT Connected = true</c> on camera 0, and the envelope it gets back.</summary>
    private static async Task<JsonElement> ConnectAsync(HttpClient alpaca, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["Connected"] = "True", ["ClientID"] = "42", ["ClientTransactionID"] = "7" });
        using var response = await alpaca.PutAsync("api/v1/camera/0/connected", content, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, "Alpaca reports a refusal inside a 200");
        return (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).Clone();
    }
}
