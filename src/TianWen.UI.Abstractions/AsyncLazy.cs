using System;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Lazy-initialized async value. The factory runs on first access and the result is cached.
/// Use <see cref="IsReady"/> for non-blocking checks and <see cref="Value"/> for synchronous access
/// after the task has completed.
/// </summary>
public sealed class AsyncLazy<T>
{
    private readonly Lazy<Task<T>> _lazy;

    public AsyncLazy(Func<Task<T>> factory)
    {
        _lazy = new Lazy<Task<T>>(factory, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Gets the task that produces the value. Starts the factory on first access.</summary>
    public Task<T> Task => _lazy.Value;

    /// <summary>Whether the value has been produced successfully (non-blocking).</summary>
    public bool IsReady => _lazy.IsValueCreated && _lazy.Value.IsCompletedSuccessfully;

    /// <summary>
    /// Returns the value if ready, or <c>default</c> otherwise. Never blocks.
    /// </summary>
    public T? ValueOrDefault => IsReady ? _lazy.Value.Result : default;

    /// <summary>Awaits the value.</summary>
    public TaskAwaiter GetAwaiter() => new TaskAwaiter(Task);

    /// <summary>Minimal awaiter so <c>await asyncLazy;</c> works.</summary>
    public readonly struct TaskAwaiter : System.Runtime.CompilerServices.INotifyCompletion
    {
        private readonly System.Runtime.CompilerServices.TaskAwaiter<T> _inner;

        internal TaskAwaiter(Task<T> task) => _inner = task.GetAwaiter();

        public bool IsCompleted => _inner.IsCompleted;
        public T GetResult() => _inner.GetResult();
        public void OnCompleted(Action continuation) => _inner.OnCompleted(continuation);
    }
}
