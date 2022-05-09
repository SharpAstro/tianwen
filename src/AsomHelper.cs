using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Astap.Lib;

public static class AsomHelper
{
    public static dynamic NewComObject(string progId) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Type.GetTypeFromProgID(progId) is Type type
            ? Activator.CreateInstance(type)
            : null as dynamic;

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

        foreach (dynamic item in property)
        {
            if (item is not null)
            {
                yield return ((string)item.Key, (string)item.Value);
            }
        }
    }
}