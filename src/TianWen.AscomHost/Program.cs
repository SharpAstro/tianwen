using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.AscomHost;
using TianWen.Lib.Connections;

// TianWen out-of-process ASCOM COM host (tianwen-ascomhost).
//
// A tiny, AOT-native, CET-off helper that hosts a legacy in-proc .NET Framework ASCOM COM driver -- the
// kind whose Connected=true DoEvents pump trips a native fastfail (0xC0000409) under the CET shadow stack
// the main TianWen app runs with. The main app keeps CET on and drives the driver from here over the
// same JSON-RPC-over-TCP protocol PHD2 uses (loopback only).
//
// Port handshake: the OS assigns the loopback port (JsonRpcServer binds port 0), and we report it back to
// the parent by printing "PORT <n>" as the first stdout line. The parent reads that line, then connects a
// JsonRpcClient to 127.0.0.1:<n>. Reporting the port after the socket is already bound avoids the TOCTOU
// race a "parent picks a free port and passes it in" scheme would have.

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

using var sta = new StaComThread();
using var host = new AscomComHost(sta);
using var server = new JsonRpcServer(
    host.HandleAsync,
    onError: (ex, context) => Console.Error.WriteLine($"[tianwen-ascomhost] {context}: {ex.Message}"));

var port = server.Start();
Console.Out.WriteLine($"PORT {port}");
Console.Out.Flush();

try
{
    await server.RunAsync(cancellation.Token);
}
catch (OperationCanceledException)
{
    // shutdown requested
}

return 0;
