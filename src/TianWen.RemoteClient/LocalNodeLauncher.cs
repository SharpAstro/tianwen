using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Hosting.Api;
using TianWen.Hosting.Dto;
using TianWen.Lib;
using TianWen.Lib.Devices;
using TianWen.Lib.IO;

namespace TianWen.RemoteClient
{
    /// <summary>How <see cref="LocalNodeLauncher.FindOrStartAsync"/> came to its node, or why it has none.</summary>
    public enum LocalNodeOutcome
    {
        /// <summary>A node of this wire was already running on the socket.</summary>
        Found,

        /// <summary>No node was running, so this client started one, which outlives it.</summary>
        Started,

        /// <summary>
        /// Started, but the client's job would not let it break away, so it ends when that job does: a launcher or a
        /// debugger. The rig will not outlive the window, and the client says so.
        /// </summary>
        StartedWithTheClient,

        /// <summary>A node under another account (a service on the machine) answered on <c>localhost:1888</c>.</summary>
        AnotherAccount,

        /// <summary>
        /// A node of another wire version holds hardware (an older one still running a night after an update), so it
        /// was left alone. The client can mirror it read-only and offer to restart it once the run ends.
        /// </summary>
        BusyWithAnotherWire,

        /// <summary>A node of another wire version answers on a socket the client was pointed at, which it never replaces.</summary>
        AnotherWire,

        /// <summary>The client was pointed at a socket (<c>--node-socket</c>) where no node answers; it starts none there.</summary>
        NamedNodeUnreachable,

        /// <summary><c>tianwen-server</c> is not beside the client: a broken install, or one project built alone.</summary>
        ServerMissing,

        /// <summary>The node was started and did not come up; the message names its log.</summary>
        CouldNotStart,
    }

    /// <summary>
    /// The node a client ended up with. <see cref="Transport"/> is null only when there is none to reach; with one, the
    /// node's own answer is <see cref="Node"/>. <see cref="Message"/> says what happened in words a user can act on.
    /// </summary>
    public sealed record LocalNode(LocalNodeOutcome Outcome, NodeTransport? Transport, NodeInfoDto? Node, string Message);

    /// <summary>Where and how a client looks for the machine's node, and starts one.</summary>
    public sealed record LocalNodeOptions
    {
        /// <summary>A socket the client was pointed at (<c>--node-socket</c>); null reads <see cref="NodeSocket.EnvironmentVariable"/>.
        /// Either way a NAMED socket is only ever connected to: the client starts no node there.</summary>
        public string? NamedSocket { get; init; }

        /// <summary>Where the machine's node listens, and where a node this client starts will.</summary>
        public string SocketPath { get; init; } = NodeSocket.DefaultPath;

        /// <summary>Where <c>tianwen-server</c> is: the client's own directory, never <c>PATH</c>, so it gets its own build.</summary>
        public string ServerDirectory { get; init; } = AppContext.BaseDirectory;

        /// <summary>A node under another account answers here, if anywhere. Null skips the look.</summary>
        public Uri? AnotherAccountProbe { get; init; } = new Uri($"http://localhost:{NodeWire.LanPort}/");

        /// <summary>How long a started node has to answer.</summary>
        public TimeSpan ReadyBudget { get; init; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// The clock the node runs on, as an offset from the real one: this process's by default
        /// (<see cref="StartupTimeOverride"/>), so a simulated night is the same night in the client and the node.
        /// </summary>
        public IReadOnlyDictionary<string, string?> ClockEnvironment { get; init; } = StartupTimeOverride.ForAChildProcess();

        /// <summary>More environment for the node (a test's data root). A null value removes a variable.</summary>
        public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();

        /// <summary>More arguments for the node (a test's <c>--fake-devices</c>).</summary>
        public IReadOnlyList<string> NodeArguments { get; init; } = [];
    }

    /// <summary>
    /// Finds the machine's node, or starts one (docs/plans/hardware-in-the-server.md, "Spawn and lifetime"): a client
    /// never asks the user to start, stop or configure a server. It asks the socket first; a node there of this wire is
    /// the one. One of another wire is replaced if it is idle and left alone if it holds hardware. With nothing on the
    /// socket, a node under another account on <c>localhost:1888</c> is used as it is. Otherwise the client starts
    /// <c>tianwen-server --keeper</c> from its own directory, broken away from its job, and waits for the node to
    /// answer.
    /// </summary>
    public sealed class LocalNodeLauncher(LocalNodeOptions options, ILogger logger)
    {
        private static readonly TimeSpan AskBudget = TimeSpan.FromSeconds(2);

        public static string ServerFileName => OperatingSystem.IsWindows() ? "tianwen-server.exe" : "tianwen-server";

        public async Task<LocalNode> FindOrStartAsync(CancellationToken cancellationToken)
        {
            var named = NodeSocket.TryGetNamed(options.NamedSocket, out var socketPath);
            if (!named)
            {
                socketPath = options.SocketPath;
            }
            var transport = NodeTransport.OverSocket(socketPath);

            if (await AskAsync(transport, cancellationToken) is { } running)
            {
                if (running.WireVersion == NodeWire.Version)
                {
                    return new LocalNode(LocalNodeOutcome.Found, transport, running, $"Connected to the node on {socketPath}");
                }
                if (named)
                {
                    return new LocalNode(LocalNodeOutcome.AnotherWire, transport, running,
                        $"The node on {socketPath} speaks wire {running.WireVersion}, this client {NodeWire.Version}");
                }
                if (running.HoldsHardware)
                {
                    return new LocalNode(LocalNodeOutcome.BusyWithAnotherWire, transport, running,
                        $"A node of another version (build {running.Version}) is running the rig; it is left running, and can be restarted once its run ends");
                }
                if (!await StopAsync(transport, running, cancellationToken))
                {
                    return new LocalNode(LocalNodeOutcome.CouldNotStart, null, null,
                        $"An idle node of another version (pid {running.ProcessId}) would not stop, so no node of this version could start");
                }
                logger.LogInformation("Stopped an idle node of wire {Wire} (build {Build}) to start one of wire {Ours}", running.WireVersion, running.Version, NodeWire.Version);
            }
            else if (named)
            {
                return new LocalNode(LocalNodeOutcome.NamedNodeUnreachable, null, null, $"No node answers on {socketPath}");
            }
            else if (options.AnotherAccountProbe is { } probe && await AskAsync(NodeTransport.OverTcp(probe), cancellationToken) is { } service)
            {
                return new LocalNode(LocalNodeOutcome.AnotherAccount, NodeTransport.OverTcp(probe), service,
                    $"Using the node already running on this machine under another account, at {probe}");
            }

            return await StartAsync(transport, socketPath, cancellationToken);
        }

        private async Task<LocalNode> StartAsync(NodeTransport transport, string socketPath, CancellationToken cancellationToken)
        {
            var server = Path.Combine(options.ServerDirectory, ServerFileName);
            if (!File.Exists(server))
            {
                return new LocalNode(LocalNodeOutcome.ServerMissing, null, null,
                    $"tianwen-server is not beside this program, where it was looked for: {server}");
            }

            var environment = new Dictionary<string, string?>(options.ClockEnvironment);
            foreach (var (name, value) in options.Environment)
            {
                environment[name] = value;
            }
            var dataRoot = environment.GetValueOrDefault(TianWenDataRoot.EnvironmentVariable) is { Length: > 0 } root
                ? Path.GetFullPath(root)
                : TianWenDataRoot.Directory.FullName;
            Directory.CreateDirectory(dataRoot);

            using var keeper = DetachedProcess.Start(server, ["--keeper", "--socket", socketPath, .. options.NodeArguments], dataRoot, environment);
            logger.LogInformation("Started the node's keeper {Server}, pid {Pid}{Breakaway}", server, keeper.Id,
                keeper.OutlivesClient ? "" : ", which could not break away from this program's job");

            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < options.ReadyBudget)
            {
                if (await AskAsync(transport, cancellationToken) is { } node)
                {
                    return keeper.OutlivesClient
                        ? new LocalNode(LocalNodeOutcome.Started, transport, node, $"Started the node on {socketPath}")
                        : new LocalNode(LocalNodeOutcome.StartedWithTheClient, transport, node,
                            "Started the node, but it will not outlive this window: the program that started this one does not let it break away");
                }

                // Another client's node won the socket first: its keeper ended with AlreadyRunning, and that node is the one.
                if (keeper.TryGetExitCode(out var exit) && exit != NodeExitCodes.AlreadyRunning)
                {
                    return new LocalNode(LocalNodeOutcome.CouldNotStart, null, null,
                        $"The node's keeper ended with {exit} before the node answered; {DescribeLogs(dataRoot)}");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }

            return new LocalNode(LocalNodeOutcome.CouldNotStart, null, null,
                $"The node did not answer on {socketPath} within {options.ReadyBudget.TotalSeconds:0} s; {DescribeLogs(dataRoot)}");
        }

        /// <summary>Asks the node to stop, then waits until it no longer answers.</summary>
        private async Task<bool> StopAsync(NodeTransport transport, NodeInfoDto running, CancellationToken cancellationToken)
        {
            using var http = transport.CreateHttpClient();
            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                budget.CancelAfter(AskBudget);
                using var answer = await http.PostAsync("api/v1/node/shutdown", content: null, budget.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException or IOException)
            {
                logger.LogWarning(ex, "Asking node pid {Pid} to stop failed", running.ProcessId);
            }

            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < options.ReadyBudget)
            {
                if (await AskAsync(transport, cancellationToken) is null)
                {
                    return true;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }
            return false;
        }

        /// <summary>The node's answer, or null when nothing answers there within a short budget.</summary>
        private static async Task<NodeInfoDto?> AskAsync(NodeTransport transport, CancellationToken cancellationToken)
        {
            using var http = transport.CreateHttpClient();
            var client = new TianWenNodeClient(http, new NodeTimeouts(AskBudget, AskBudget, AskBudget));
            return (await client.GetNodeAsync(cancellationToken).ConfigureAwait(false)).Value;
        }

        /// <summary>Where a node that failed to come up wrote what happened: the newest server and keeper logs.</summary>
        private static string DescribeLogs(string dataRoot)
        {
            var logs = Path.Combine(dataRoot, "Logs");
            if (!Directory.Exists(logs))
            {
                return $"it wrote no log under {logs}";
            }

            var newest = FileEnumeration.EnumerateFiles(logs, ".log", recursive: true)
                .Select(static path => new FileInfo(path))
                .Where(static file => file.Name.StartsWith("Server_", StringComparison.Ordinal) || file.Name.StartsWith("Keeper_", StringComparison.Ordinal))
                .GroupBy(static file => file.Name.StartsWith("Server_", StringComparison.Ordinal))
                .Select(static group => group.MaxBy(static file => file.LastWriteTimeUtc))
                .OfType<FileInfo>()
                .Select(static file => file.FullName)
                .ToArray();
            return newest.Length > 0 ? $"see {string.Join(" and ", newest)}" : $"it wrote no log under {logs}";
        }
    }
}
