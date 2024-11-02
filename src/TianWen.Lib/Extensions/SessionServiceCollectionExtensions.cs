using Microsoft.Extensions.DependencyInjection;
using TianWen.Lib.Sequencing;
namespace TianWen.Lib.Extensions;

public static class SessionServiceCollectionExtensions
{
    public static IServiceCollection AddSessionFactory(this IServiceCollection services) => services.AddSingleton<ISessionFactory, SessionFactory>();
}