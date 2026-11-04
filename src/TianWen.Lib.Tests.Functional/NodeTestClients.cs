using Meziantou.Extensions.Logging.Xunit.v3;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TianWen.RemoteClient;
using Xunit;
using static TianWen.RemoteClient.TianWenNodeClient;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// The one source of node clients for the tests that drive a real node (#2000). A request has a generous budget here, and
/// what bounds a test is its own <c>[Fact(Timeout)]</c>: the shipping budgets (5 s state, 10 s control) are right for a
/// user's UI and wrong on a CI runner shared with other work, where a node that had not yet been scheduled answered a first
/// request with a 503 "No answer within 10s" and a one-shot read with a null. Every request is logged to the test's output
/// with its status and milliseconds, so a failure says how long things took.
/// </summary>
internal static class NodeTestClients
{
    /// <summary>
    /// Generous budgets, kept under <see cref="NodeTimeouts.ClientBackstop"/> (60 s), the timeout of the HTTP client
    /// <see cref="NodeTransport.CreateHttpClient"/> makes and which this wraps: a budget above it would never bite.
    /// </summary>
    public static readonly NodeTimeouts Timeouts = new NodeTimeouts(
        StatePoll: TimeSpan.FromSeconds(45),
        Preview: TimeSpan.FromSeconds(45),
        Control: TimeSpan.FromSeconds(45));

    /// <summary>An HTTP client over <paramref name="transport"/> that logs each request to the test's output, the caller's to dispose.</summary>
    public static HttpClient Create(NodeTransport transport)
    {
        var logger = TestContext.Current.TestOutputHelper is { } output
            ? XUnitLogger.CreateLogger(output, new XUnitLoggerOptions { IncludeLogLevel = true, TimestampFormat = "HH:mm:ss.fff" })
            : null;
        var handler = new LoggingHandler(transport.CreateHttpClient(), logger);
        try
        {
            var client = new HttpClient(handler) { BaseAddress = transport.BaseAddress };
            handler = null;
            return client;
        }
        finally
        {
            handler?.Dispose();
        }
    }

    /// <summary>A node client on <paramref name="http"/> with <see cref="Timeouts"/>.</summary>
    public static TianWenNodeClient ClientOf(HttpClient http) => new TianWenNodeClient(http, Timeouts);

    /// <summary>
    /// Sends through the transport's own client, which holds the socket or the grant, and logs. The outer client has the
    /// default 100 s timeout, above the inner backstop, so the inner one ends a request nothing else did.
    /// </summary>
    private sealed class LoggingHandler(HttpClient inner, ILogger? logger) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var clock = Stopwatch.StartNew();
            try
            {
                // The outer client has marked this message sent, and an HttpClient refuses one twice: pass on a copy.
                using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri) { Content = request.Content, Version = request.Version, VersionPolicy = request.VersionPolicy };
                foreach (var header in request.Headers)
                {
                    forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                var response = await inner.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                logger?.LogInformation("{Method} {Path} -> {Status} in {Ms} ms", request.Method, request.RequestUri?.PathAndQuery, (int)response.StatusCode, clock.ElapsedMilliseconds);
                return response;
            }
            catch (Exception ex)
            {
                logger?.LogInformation("{Method} {Path} -> {Error} after {Ms} ms", request.Method, request.RequestUri?.PathAndQuery, ex.GetType().Name, clock.ElapsedMilliseconds);
                throw;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
