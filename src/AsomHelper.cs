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

    public static IEnumerable<string> EnumerateArrayList(dynamic list) => list is ArrayList arrayList ? arrayList.Cast<string>() : Array.Empty<string>();
}