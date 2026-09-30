using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TianWen.Hosting.Api;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.RemoteClient;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// "Stop the rig and quit" in its one safe order (decision 1 of docs/plans/hardware-in-the-server.md, #936): the node's run
/// is stopped and followed to its end, and only then are its devices warmed up and disconnected. A node whose run ends
/// through its own Finalise disconnects that run's devices itself, so over a real node an out-of-order stop looks the same
/// at its end; the order is asked of a scripted node here, which says what it was asked and when.
/// </summary>
public class RigShutdownOrderTests
{
    private const string Camera = "fake://camera/1";

    /// <summary>
    /// A node with a session going on for <paramref name="pollsUntilEnded"/> more reads of it, and one camera; with
    /// <paramref name="previewRunning"/>, a preview exposure running on that camera until it is cancelled.
    /// </summary>
    private sealed class ScriptedNode(int pollsUntilEnded, bool previewRunning = false) : HttpMessageHandler
    {
        private int _runPolls = pollsUntilEnded;
        private int _previewCancelled;

        private static JobDto Preview(JobState state) => new JobDto { Id = "p1", Kind = JobDto.PreviewKind, DeviceUri = Camera, State = state };

        public ConcurrentQueue<string> Asked { get; } = new ConcurrentQueue<string>();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            var route = $"{request.Method} {path}";
            string body;
            switch (route)
            {
                case "GET /api/v1/node":
                    var running = Interlocked.Decrement(ref _runPolls) >= 0;
                    Asked.Enqueue(running ? "node: running" : "node: ended");
                    body = JsonSerializer.Serialize(ResponseEnvelope<NodeInfoDto>.Ok(new NodeInfoDto
                    {
                        NodeId = "node",
                        Version = "test",
                        Run = running ? new NodeRunDto { Kind = NodeRunKind.Session } : null,
                    }), HostingJsonContext.Default.ResponseEnvelopeNodeInfoDto);
                    break;
                case "POST /api/v1/session/abort":
                    Asked.Enqueue("abort");
                    body = JsonSerializer.Serialize(ResponseEnvelope<string>.Ok("Abort requested"), HostingJsonContext.Default.ResponseEnvelopeString);
                    break;
                case "GET /api/v1/jobs":
                    Asked.Enqueue("jobs");
                    body = JsonSerializer.Serialize(ResponseEnvelope<JobDto[]>.Ok(
                        previewRunning && Volatile.Read(ref _previewCancelled) == 0 ? [Preview(JobState.Running)] : []),
                        HostingJsonContext.Default.ResponseEnvelopeJobDtoArray);
                    break;
                case "DELETE /api/v1/jobs/p1":
                    Asked.Enqueue("cancel preview");
                    Volatile.Write(ref _previewCancelled, 1);
                    body = JsonSerializer.Serialize(ResponseEnvelope<JobDto>.Ok(Preview(JobState.Running)), HostingJsonContext.Default.ResponseEnvelopeJobDto);
                    break;
                case "GET /api/v1/jobs/p1":
                    Asked.Enqueue("preview job");
                    body = JsonSerializer.Serialize(ResponseEnvelope<JobDto>.Ok(Preview(JobState.Cancelled)), HostingJsonContext.Default.ResponseEnvelopeJobDto);
                    break;
                case "GET /api/v1/devices/state":
                    Asked.Enqueue("devices");
                    body = JsonSerializer.Serialize(ResponseEnvelope<DeviceStateDto[]>.Ok(
                        [new DeviceStateDto { DeviceUri = Camera, DeviceType = DeviceType.Camera, Connected = true }]),
                        HostingJsonContext.Default.ResponseEnvelopeDeviceStateDtoArray);
                    break;
                case "POST /api/v1/devices/warm-and-disconnect":
                    Asked.Enqueue("warm-and-disconnect");
                    body = JsonSerializer.Serialize(ResponseEnvelope<JobDto>.Ok(new JobDto { Id = "j1", Kind = "warm-and-disconnect", State = JobState.Running }),
                        HostingJsonContext.Default.ResponseEnvelopeJobDto);
                    break;
                case "GET /api/v1/jobs/j1":
                    Asked.Enqueue("job");
                    body = JsonSerializer.Serialize(ResponseEnvelope<JobDto>.Ok(new JobDto { Id = "j1", Kind = "warm-and-disconnect", State = JobState.Succeeded }),
                        HostingJsonContext.Default.ResponseEnvelopeJobDto);
                    break;
                default:
                    Asked.Enqueue($"unexpected {route}");
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task NoDeviceIsTouchedUntilTheNodeSaysTheRunHasEnded()
    {
        var node = new ScriptedNode(pollsUntilEnded: 3);
        using var http = new HttpClient(node) { BaseAddress = new Uri("http://node.local/") };
        var progress = new List<string>();

        await new RigShutdown(new FakeTimeProviderWrapper(), NullLogger.Instance)
            .StopAsync(new TianWenNodeClient(http), progress.Add, TestContext.Current.CancellationToken);

        node.Asked.ToArray().ShouldBe(
        [
            "node: running", "abort", "node: running", "node: running", "node: ended",
            "jobs", "devices", "warm-and-disconnect", "job",
        ], "the run first, followed to its end; the devices after");
        progress.First().ShouldBe(RigShutdown.Ending(NodeRunKind.Session));
    }

    [Fact(Timeout = 30_000)]
    public async Task ARunningPreviewIsStoppedAndFollowedToItsEndBeforeAnyDeviceIsTouched()
    {
        // The node refuses to disconnect a device a job holds, so a preview that does not end held the quit for ever.
        var node = new ScriptedNode(pollsUntilEnded: 0, previewRunning: true);
        using var http = new HttpClient(node) { BaseAddress = new Uri("http://node.local/") };
        var progress = new List<string>();

        await new RigShutdown(new FakeTimeProviderWrapper(), NullLogger.Instance)
            .StopAsync(new TianWenNodeClient(http), progress.Add, TestContext.Current.CancellationToken);

        node.Asked.ToArray().ShouldBe(
        [
            "node: ended", "jobs", "cancel preview", "preview job", "devices", "warm-and-disconnect", "job",
        ], "the preview asked to stop and followed to its end; the devices after");
        progress.ShouldContain("Stopping the preview exposure", "the window says what it is waiting for");
    }
}
