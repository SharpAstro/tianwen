using System;
using System.Threading;

namespace TianWen.Lib;

public readonly record struct ResourceLock(SemaphoreSlim? Semaphore) : IDisposable
{
    public static readonly ResourceLock AlwaysUnlocked = new ResourceLock(null);

    public readonly void Dispose() => Semaphore?.Release();
}