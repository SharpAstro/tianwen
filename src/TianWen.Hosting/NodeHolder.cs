using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting.Api;
using TianWen.Hosting.Dto;

namespace TianWen.Hosting;

/// <summary>
/// What a node refused its socket's lock says about the one holding it: "a node is already running (pid N)", asked over
/// the socket (Node.Lib's <c>NodeLock.DescribeHolderAsync</c>, which falls back to the lock refusal when nothing answers).
/// </summary>
public static class NodeHolder
{
    public static Task<string> DescribeAsync(string socketPath, IOException refusal, CancellationToken cancellationToken) =>
        NodeLock.DescribeHolderAsync(socketPath, refusal, async (socket, budget) =>
        {
            using var handler = NodeSocket.CreateHandler(socket);
            using var http = new HttpClient(handler, disposeHandler: false) { BaseAddress = NodeSocket.BaseAddress };
            await using var body = await http.GetStreamAsync("api/v1/node", budget).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync(body, HostingJsonContext.Default.ResponseEnvelopeNodeInfoDto, budget).ConfigureAwait(false)
                is { Response: { } node }
                ? $"A TianWen node is already running (pid {node.ProcessId}, node {node.NodeId}) on {socket}"
                : throw new IOException("the node did not say what it is");
        }, cancellationToken);
}
