using System;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using TianWen.AscomHost;
using TianWen.Lib.Connections;

// TianWen out-of-process ASCOM COM host (tianwen-ascomhost).
//
// A tiny, AOT-native, CET-off helper that hosts a legacy in-proc .NET Framework ASCOM COM driver -- the
// kind whose Connected=true DoEvents pump trips a native fastfail (0xC0000409) under the CET shadow stack
// the main TianWen app runs with. The main app keeps CET on and drives the driver from here over the
// same JSON-RPC protocol PHD2 uses, carried on a per-user named pipe (no network stack, no loopback port,
// no firewall/AV involvement).
//
// Transport: the PARENT creates the named-pipe server (before spawning us, so there is no race) and
// passes its GUID pipe name as argv[0]; we connect as the pipe client and serve. When the parent exits,
// the pipe breaks and our serve loop ends -- we exit on our own (the job object is the hard backstop).

if (args.Length < 1 || string.IsNullOrWhiteSpace(args[0]))
{
    await Console.Error.WriteLineAsync("usage: tianwen-ascomhost <pipe-name>");
    return 2;
}

var pipeName = args[0];

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

using var sta = new StaComThread();
using var host = new AscomComHost(sta);
var server = new JsonRpcServer(
    host.HandleAsync,
    onError: (ex, context) => Console.Error.WriteLine($"[tianwen-ascomhost] {context}: {ex.Message}"));

using var pipe = new NamedPipeClientStream(
    ".",
    pipeName,
    PipeDirection.InOut,
    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

try
{
    pipe.Connect(timeout: 15_000);
}
catch (Exception ex)
{
    await Console.Error.WriteLineAsync($"[tianwen-ascomhost] could not connect to pipe '{pipeName}': {ex.Message}");
    return 3;
}

using var connection = new NamedPipeConnection(pipe);

try
{
    await server.ServeAsync(connection, cancellation.Token);
}
catch (OperationCanceledException)
{
    // shutdown requested
}

return 0;
