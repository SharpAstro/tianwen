using DIR.Lib;
using Microsoft.Extensions.DependencyInjection;

namespace TianWen.UI.Abstractions.Extensions;

public static class FitsViewerServiceCollectionExtensions
{
    /// <summary>
    /// Registers the core FITS viewer services (state, caching, file dialogs).
    /// Display-specific registrations are done by the concrete display project.
    /// </summary>
    /// <remarks>
    /// The file dialogs are the WINDOW's (SdlVulkan.Renderer's <c>SdlFileDialogs</c>, DIR.Lib's <see cref="IFileDialogs"/>),
    /// and a host builds its services before its window, so what is registered is <see cref="HostFileDialogs"/>, which
    /// the host binds to the window once it exists.
    /// </remarks>
    public static IServiceCollection AddFitsViewer(this IServiceCollection services) => services
        .AddSingleton<ViewerState>()
        .AddSingleton<PlannerState>()
        .AddSingleton<IDocumentCache, DocumentCache>()
        .AddSingleton<HostFileDialogs>()
        .AddSingleton<IFileDialogs>(sp => sp.GetRequiredService<HostFileDialogs>());
}
