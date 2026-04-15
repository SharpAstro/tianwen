using Microsoft.Extensions.DependencyInjection;

namespace TianWen.UI.Abstractions.Extensions;

public static class FitsViewerServiceCollectionExtensions
{
    /// <summary>
    /// Registers the core FITS viewer services (state, caching, file dialogs).
    /// Display-specific registrations are done by the concrete display project.
    /// </summary>
    public static IServiceCollection AddFitsViewer(this IServiceCollection services) => services
        .AddSingleton<ViewerState>()
        .AddSingleton<PlannerState>()
        .AddSingleton<IDocumentCache, DocumentCache>()
        .AddSingleton<IFileDialogHelper, FileDialogService>();
}
