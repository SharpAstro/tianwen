using TianWen.Lib.Devices;
using Microsoft.Extensions.DependencyInjection;

namespace TianWen.Lib.Extensions;

public static class SystemServiceCollectionExtensions
{
    public static IServiceCollection UseSystemExternal(this IServiceCollection services) => services.AddSingleton<IExternal, SystemExternal>();
}