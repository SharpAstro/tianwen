using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Imaging.BackgroundExtraction;
using TianWen.Lib.Imaging.Enhancement;

namespace TianWen.Lib.Extensions
{
    public static class BackgroundExtractionServiceCollectionExtensions
    {
        /// <summary>
        /// Registers the classical (AI-free) background extractor as itself and as
        /// <see cref="IBackgroundExtractor"/>, one singleton behind both, without claiming the
        /// <see cref="IGradientCorrector"/> role. Idempotent (<c>TryAdd</c>). This is what
        /// <c>AddTianWenAi()</c> calls, so it can put its own GraXpert-or-classical pick in front of the
        /// pipeline role.
        /// </summary>
        /// <param name="options">The options the gradient-corrector entry points run with; the reference
        /// defaults when null.</param>
        public static IServiceCollection AddClassicalBackgroundExtractor(this IServiceCollection services, BackgroundExtractionOptions? options = null)
        {
            // A factory lambda, not the generic registration: the ctor takes an optional ILogger<T>, which
            // the short form would satisfy but the shared instance must be ONE object behind two interfaces.
            services.TryAddSingleton(sp => new ClassicalBackgroundExtractor(options, sp.GetService<ILogger<ClassicalBackgroundExtractor>>()));
            services.TryAddSingleton<IBackgroundExtractor>(sp => sp.GetRequiredService<ClassicalBackgroundExtractor>());
            return services;
        }

        /// <summary>
        /// <see cref="AddClassicalBackgroundExtractor"/> plus the <see cref="IGradientCorrector"/> role, so the
        /// classical fit backs the sharpen pipeline's gradient step with no AI project referenced at all.
        /// Idempotent (<c>TryAdd</c>), so the FIRST registration of <see cref="IGradientCorrector"/> wins:
        /// call this before <c>AddTianWenAi()</c> to force the classical fit, after it to keep that call's
        /// GraXpert-when-installed pick.
        /// </summary>
        public static IServiceCollection AddClassicalBackgroundExtraction(this IServiceCollection services, BackgroundExtractionOptions? options = null)
        {
            services.AddClassicalBackgroundExtractor(options);
            services.TryAddSingleton<IGradientCorrector>(sp => sp.GetRequiredService<ClassicalBackgroundExtractor>());
            return services;
        }
    }
}
