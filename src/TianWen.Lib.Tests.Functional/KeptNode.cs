using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting.Api;
using TianWen.Hosting.Dto;
using TianWen.RemoteClient;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// A real <c>tianwen-server --keeper</c>, and the node it keeps, as a client would start them: the server built
/// beside the tests (src/ExeBeside.targets), on a socket and a data root of the test's own, with the fake devices
/// only, so it touches neither the user's AppData nor any hardware of the machine it runs on.
/// </summary>
internal sealed class KeptNode : IAsyncDisposable
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;

    private KeptNode(Process keeper, string socketPath, string dataRoot)
    {
        Keeper = keeper;
        SocketPath = socketPath;
        DataRoot = dataRoot;
        _http = NodeTransport.OverSocket(socketPath).CreateHttpClient();
        Client = new TianWenNodeClient(_http);
    }

    public Process Keeper { get; }

    public string SocketPath { get; }

    public string DataRoot { get; }

    public TianWenNodeClient Client { get; }

    /// <summary>The node's socket as a plain HTTP client, for what <see cref="Client"/> does not speak (the Alpaca plane).</summary>
    public HttpClient Http => _http;

    public static string ServerPath => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "tianwen-server.exe" : "tianwen-server");

    /// <summary>Starts a keeper, on <paramref name="socketPath"/> when given, else a new socket of its own.</summary>
    public static Process StartKeeper(string socketPath, string dataRoot)
    {
        var start = new ProcessStartInfo(ServerPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in (ReadOnlySpan<string>)["--keeper", "--socket", socketPath, "--local-only", "--fake-devices"])
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment[TianWenDataRoot.EnvironmentVariable] = dataRoot;
        return Process.Start(start) ?? throw new InvalidOperationException($"Could not start {ServerPath}");
    }

    /// <param name="prepareDataRoot">Writes into the node's data root before it starts (a profile it should find).</param>
    public static async Task<KeptNode> StartAsync(CancellationToken cancellationToken, Func<string, Task>? prepareDataRoot = null)
    {
        var folder = Directory.CreateTempSubdirectory("twk").FullName;
        var dataRoot = Directory.CreateDirectory(Path.Combine(folder, "data")).FullName;
        if (prepareDataRoot is not null)
        {
            await prepareDataRoot(dataRoot);
        }
        var node = new KeptNode(StartKeeper(Path.Combine(folder, "node.sock"), dataRoot), Path.Combine(folder, "node.sock"), dataRoot);
        try
        {
            await node.WaitForNodeAsync(static _ => true, cancellationToken);
            return node;
        }
        catch
        {
            await node.DisposeAsync();
            throw;
        }
    }

    /// <summary>The node, once it answers as <paramref name="until"/> wants: a node still starting, or one being
    /// restarted, does not answer at all for a moment.</summary>
    public async Task<NodeInfoDto> WaitForNodeAsync(Func<NodeInfoDto, bool> until, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < Budget)
        {
            if (Keeper.HasExited)
            {
                throw new InvalidOperationException($"The keeper exited with {Keeper.ExitCode} while the test waited for its node");
            }
            if ((await Client.GetNodeAsync(cancellationToken)).Value is { } info && until(info))
            {
                return info;
            }
            await Task.Delay(100, cancellationToken);
        }
        throw new TimeoutException($"No node answered as expected on {SocketPath} within {Budget}");
    }

    /// <summary>The keeper's exit code, once it has ended.</summary>
    public async Task<int> KeeperExitAsync(CancellationToken cancellationToken)
    {
        await Keeper.WaitForExitAsync(cancellationToken).WaitAsync(Budget, cancellationToken);
        return Keeper.ExitCode;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!Keeper.HasExited)
            {
                // The node's own safe stop, which ends the keeper too.
                await _http.PostAsync("api/v1/node/shutdown", content: null);
                await Keeper.WaitForExitAsync().WaitAsync(Budget);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or IOException)
        {
            // The node was already gone or would not stop: take the whole tree down below.
        }
        finally
        {
            if (!Keeper.HasExited)
            {
                Keeper.Kill(entireProcessTree: true);
            }
            Keeper.Dispose();
            _http.Dispose();
        }
    }
}
