using System;
using Microsoft.Extensions.DependencyInjection;
using TianWen.Lib.Devices;

namespace TianWen.Lib;

public static class ServiceProviderExtensions
{
    extension(IServiceProvider sp)
    {
        public IExternal External => sp.GetRequiredService<IExternal>();
    }
}

