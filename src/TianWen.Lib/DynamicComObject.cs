using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Astap.Lib;

public class DynamicComObject(string progId) : IDisposable
{
    private static readonly ConcurrentDictionary<string, Type?> _progIdTypeCache = new();

    /// <summary>
    /// Register an <paramref name="initialiser"/> for a given <paramref name="progId"/>.
    /// Overrides any existing type.
    /// </summary>
    /// <param name="progId">prog id</param>
    /// <param name="type">type to register, or null to unregister any previous type.</param>
    public static void RegisterTypeForProgId(string progId, Type? type)
        => _progIdTypeCache.AddOrUpdate(progId, type, (_, _) => type);

    private static Type? TryGetTypeFromProgID(string progId)
        => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Type.GetTypeFromProgID(progId) is { } type ? type : null;

    /// <summary>
    /// Uses <paramref name="progId"/> to lookup a registered <see cref="Type"/> that was previously registered with <see cref="RegisterTypeForProgId(string, Type)"/>.
    /// If none is found and the current platform is <see cref="OSPlatform.Windows"/> and a ProgID mapping exists then that is used to create an instance.
    /// </summary>
    /// <param name="progId"></param>
    /// <returns></returns>
    public static object? CreateInstanceFromProgId(string progId)
        => _progIdTypeCache.GetOrAdd(progId, TryGetTypeFromProgID) is { } type ? Activator.CreateInstance(type) : null;

    protected readonly dynamic? _comObject = CreateInstanceFromProgId(progId);

    private bool _disposedValue;

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