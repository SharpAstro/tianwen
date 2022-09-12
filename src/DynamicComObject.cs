using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Astap.Lib;

public class DynamicComObject : IDisposable
{
    public static dynamic? NewComObject(string progId) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Type.GetTypeFromProgID(progId) is Type type
            ? Activator.CreateInstance(type)
            : null as dynamic;

    protected readonly dynamic? _comObject;

    private bool _disposedValue;


    public DynamicComObject(string progId) => _comObject = NewComObject(progId);

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

    public static IEnumerable<(string key, string value)> EnumerateKeyValueProperty(dynamic property)
    {
        if (property is null)
        {
            yield break;
        }

        foreach (var item in property)
        {
            if (item is not null)
            {
                yield return ((string)item.Key, (string)item.Value);
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