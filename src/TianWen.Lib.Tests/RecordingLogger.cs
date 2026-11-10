using System;
using System.Collections.Concurrent;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace TianWen.Lib.Tests;

/// <summary>Keeps every entry logged, formatted, which is the one thing a test of "this was said" asks of a log.</summary>
internal sealed class RecordingLogger : ILogger
{
    private readonly ConcurrentQueue<(LogLevel Level, Exception? Exception, string Message)> _entries = new();

    public (LogLevel Level, Exception? Exception, string Message)[] Entries => [.. _entries];

    public string[] Messages => [.. _entries.Select(e => e.Message)];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => _entries.Enqueue((logLevel, exception, formatter(state, exception)));
}
