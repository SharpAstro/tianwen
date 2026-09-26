using Meziantou.Extensions.Logging.Xunit.v3;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting;
using TianWen.Hosting.Extensions;
using TianWen.Lib.Devices;
using TianWen.Lib.Extensions;
using TianWen.Lib.Sequencing;
using TianWen.RemoteClient;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// A real host over real HTTP whose session factory makes sessions that end only when the test says so:
/// the harness the node's lifecycle, configuration and event-stream tests share (P0b of
/// docs/plans/hardware-in-the-server.md, #752).
/// </summary>
internal sealed class NodeHarness : IAsyncDisposable
{
    public static readonly Guid ProfileId = new Guid("5a1ec7ed-0b0e-4e5d-9a5e-000000000001");

    private NodeHarness(WebApplication app, NodeTransport transport, ControlledSessionFactory factory, IExternal external, NodeLock? held)
    {
        App = app;
        Transport = transport;
        Client = transport.CreateHttpClient();
        Factory = factory;
        External = external;
        _held = held;
    }

    private readonly NodeLock? _held;

    public WebApplication App { get; }

    /// <summary>How a client reaches this node: its own socket when started on one, else loopback TCP.</summary>
    public NodeTransport Transport { get; }

    public HttpClient Client { get; }

    public IExternal External { get; }

    public ControlledSessionFactory Factory { get; }

    public IHostedSession Node => App.Services.GetRequiredService<IHostedSession>();

    /// <param name="configure">Registers services last, over the node's own (a discovery a test controls).</param>
    /// <param name="socketPath">Listens on this socket, and on nothing else, as the machine's node does: taking its
    /// lock first, as every node must. Null listens on loopback TCP.</param>
    public static async Task<NodeHarness> StartAsync(ITestOutputHelper outputHelper, CancellationToken cancellationToken,
        Action<IServiceCollection>? configure = null, string? socketPath = null)
    {
        var builder = WebApplication.CreateBuilder();
        NodeLock? held = null;
        if (socketPath is null)
        {
            builder.WebHost.UseUrls("http://127.0.0.1:0");
        }
        else if (NodeLock.TryAcquire(socketPath, out held, out var refusal))
        {
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenOnNodeSocket(held));
            builder.Services.AddSingleton(new NodeListening(socketPath, LanPort: null));
        }
        else
        {
            throw new InvalidOperationException($"The test node could not take the lock on {socketPath}", refusal);
        }
        // The node's own log goes to the test's output, which a TRX keeps for a failure: a node test that fails with
        // nothing but its assertion cannot say what the node was doing (#940). Timestamped, since what goes wrong
        // here is usually WHEN.
        builder.Logging.ClearProviders();
        builder.Logging.AddXunit(outputHelper, new XUnitLoggerOptions { IncludeCategory = true, IncludeLogLevel = true, TimestampFormat = "HH:mm:ss.fff" });
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);

        var external = new FakeExternal(outputHelper, Directory.CreateTempSubdirectory("tw_" + Guid.NewGuid().ToString("D")));
        var factory = new ControlledSessionFactory();
        builder.Services.AddSingleton<IExternal>(external);
        // A REAL clock: the node's background loops (the broadcaster's 1 s poll, the limit watcher) must wait
        // as they do in production. The fake clock's auto-advancing SleepAsync made them spin, which hid the
        // second a run's broadcaster used to miss (P0b item 13): attaching from a spinning poll is instant.
        builder.Services.AddSingleton<ITimeProvider>(new SystemTimeProvider());
        builder.Services.AddAstrometry();
        builder.Services.AddFake();
        builder.Services.AddDevices();
        builder.Services.AddProfiles();
        builder.Services.AddSessionFactory();
        builder.Services.AddHostedSession();
        // Registered last, so it is the factory every endpoint resolves.
        builder.Services.AddSingleton<ISessionFactory>(factory);
        configure?.Invoke(builder.Services);

        var app = builder.Build();
        app.UseWebSockets();
        app.MapHostingApi();
        if (held is not null)
        {
            app.RestrictNodeSocketToItsOwner(held);
        }
        try
        {
            await app.StartAsync(cancellationToken);
        }
        catch
        {
            held?.Dispose();
            throw;
        }

        var transport = held is not null ? NodeTransport.OverSocket(held.SocketPath) : NodeTransport.OverTcp(new Uri(app.Urls.First()));
        return new NodeHarness(app, transport, factory, external, held);
    }

    /// <summary>Starts a session over HTTP and waits until its run has begun.</summary>
    public async Task<ControlledSession> StartSessionAsync(CancellationToken ct)
    {
        (await EnvelopeStatusAsync(Client.PostAsync($"/api/v1/session/start?profileId={ProfileId}", null, ct), ct)).ShouldBe(200);
        var session = Factory.Created.Last();
        await session.Started.Task.WaitAsync(ct);
        return session;
    }

    /// <summary>The envelope's status code, whatever the HTTP status says.</summary>
    public static async Task<int> EnvelopeStatusAsync(Task<HttpResponseMessage> request, CancellationToken ct)
    {
        using var response = await request;
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return body.RootElement.GetProperty("statusCode").GetInt32();
    }

    /// <summary>The node's run task completes a moment after the session's own run returns.</summary>
    public async Task UntilTheNodeIsIdleAsync(CancellationToken ct)
    {
        for (var i = 0; i < 100 && Node.IsRunning; i++)
        {
            await Task.Delay(20, ct);
        }
        Node.IsRunning.ShouldBeFalse();
    }

    public async ValueTask DisposeAsync()
    {
        // Whatever a test left running ends, so the host can stop.
        foreach (var session in Factory.Created)
        {
            session.EndsOnItsOwn.TrySetResult();
            session.Finalise.TrySetResult();
        }
        Factory.Initialised.TrySetResult();

        Client.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
        _held?.Dispose();
    }
}

internal sealed class ControlledSessionFactory : ISessionFactory
{
    public TaskCompletionSource Initialised { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public int InitializeCalls => Volatile.Read(ref _initializeCalls);

    public ConcurrentQueue<ControlledSession> Created { get; } = new ConcurrentQueue<ControlledSession>();

    /// <summary>Applied to each session as it is created, before the node runs it.</summary>
    public Action<ControlledSession>? OnCreated { get; set; }

    private int _initializeCalls;

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _initializeCalls);
        return new ValueTask(Initialised.Task.WaitAsync(cancellationToken));
    }

    public ISession Create(Guid profileId, in SessionConfiguration configuration, ReadOnlySpan<ScheduledObservation> observations)
    {
        var session = new ControlledSession(configuration);
        OnCreated?.Invoke(session);
        Created.Enqueue(session);
        return session.Session;
    }
}

/// <summary>A session that runs until cancelled or told it ended, then "finalises" until told it is done.</summary>
internal sealed class ControlledSession
{
    private int _running;
    private int _disposals;
    private int _disposedWhileRunning;

    public ControlledSession(SessionConfiguration configuration)
    {
        Configuration = configuration;
        Session = Substitute.For<ISession>();
        Session.RunAsync(Arg.Any<CancellationToken>()).Returns(call => RunAsync(call.Arg<CancellationToken>()));
        Session.RunFlatsOnlyAsync(Arg.Any<TwilightPeriod>(), Arg.Any<CancellationToken>())
            .Returns(call => RunAsync(call.ArgAt<CancellationToken>(1)));
        Session.DisposeAsync().Returns(_ =>
        {
            Interlocked.Increment(ref _disposals);
            if (Volatile.Read(ref _running) == 1)
            {
                Interlocked.Increment(ref _disposedWhileRunning);
            }
            return ValueTask.CompletedTask;
        });
    }

    public ISession Session { get; }
    public SessionConfiguration Configuration { get; }
    public CancellationToken RunToken { get; private set; }
    public TaskCompletionSource Started { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Cancelled { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource EndsOnItsOwn { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Finalise { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Ended { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Disposals => Volatile.Read(ref _disposals);
    public int DisposedWhileRunning => Volatile.Read(ref _disposedWhileRunning);

    /// <summary>Run as the node releases the run's body, before anything else it does: a run's first act.</summary>
    public Action? AtRunStart { get; set; }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        AtRunStart?.Invoke();
        RunToken = cancellationToken;
        Volatile.Write(ref _running, 1);
        using var registration = cancellationToken.Register(() => Cancelled.TrySetResult());
        Started.TrySetResult();
        try
        {
            await EndsOnItsOwn.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Aborted: the real session goes on to its Finalise, and so does this one.
        }
        await Finalise.Task;
        Volatile.Write(ref _running, 0);
        Ended.TrySetResult();
    }
}

/// <summary>A request body that never arrives, so the request stays in flight until the client abandons it.</summary>
internal sealed class NeverEndingStream : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return 0;
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
