using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Astap.Lib;

public class DynamicComObject : IDisposable
{
    private static readonly ConcurrentDictionary<string, Func<string, object?>> _initaliserMap = new();
    private static readonly ConcurrentDictionary<string, Type?> _progIdTypeCache = new();

    /// <summary>
    /// Register an <paramref name="initialiser"/> for a given <paramref name="progId"/>.
    /// </summary>
    /// <param name="progId"></param>
    /// <param name="initialiser"></param>
    public static void RegisterTypeCreator(string progId, Func<string, object?> initialiser)
        => _initaliserMap.AddOrUpdate(progId, initialiser, (_, _) => initialiser);

    private static Type? TryGetTypeFromProgID(string progId)
        => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Type.GetTypeFromProgID(progId) is { } type ? type : null;

    /// <summary>
    /// Uses <paramref name="progId"/> to lookup a registered initialiser that was previously registered with <see cref="RegisterTypeCreator(string, Func{string, object?})"/>.
    /// If none is found and the current platform is <see cref="OSPlatform.Windows"/> and a ProgID mapping exists then that is used to create an instance.
    /// </summary>
    /// <param name="progId"></param>
    /// <returns></returns>
    public static object? CreateInstanceFromProgId(string progId)
    {
        if (_initaliserMap.TryGetValue(progId, out var initialiser))
        {
            return initialiser(progId);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && _progIdTypeCache.GetOrAdd(progId, TryGetTypeFromProgID) is { } type)
        {
            return Activator.CreateInstance(type);
        }
        else
        {
            return null;
        }
    }

    protected readonly dynamic? _comObject;

    private bool _disposedValue;

    public DynamicComObject(string progId) => _comObject = CreateInstanceFromProgId(progId);

    public static IEnumerable<T> EnumerateProperty<T>(dynamic property)
    {
        if (property is null)
        {
            yield break;
        }

        foreach (T item in property)
        {
            yield return item;
        }
    }

    public static IEnumerable<KeyValuePair<string, string>> EnumerateKeyValueProperty(dynamic property)
    {
        if (property is null)
        {
            yield break;
        }

        foreach (var item in property)
        {
            if (item?.Key is string key && item.Value is string value)
            {
                yield return new(key, value);
            }
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                if (_comObject is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }

            _disposedValue = true;
        }
    }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}