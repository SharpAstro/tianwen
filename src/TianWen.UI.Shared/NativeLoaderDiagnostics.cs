using Microsoft.Extensions.Logging;
using System;
using System.Runtime.InteropServices;

namespace TianWen.UI.Shared;

/// <summary>
/// Diagnostics wrapper around the SDL3 + Vulkan native-library load path. The
/// default P/Invoke failure is an opaque <see cref="DllNotFoundException"/>
/// with no hint about the *actual* cause (missing VC++ runtime vs. missing
/// DLL vs. missing Vulkan ICD). This class does two things:
///
/// <list type="number">
///   <item><see cref="Install"/> registers a <see cref="NativeLibrary.SetDllImportResolver"/>
///         for the SDL3-CS assembly. The resolver fires only on the first
///         P/Invoke into <c>SDL3.dll</c> — steady-state cost is zero because
///         the runtime caches the returned handle.</item>
///   <item><see cref="InitNative{T}"/> wraps an init call (e.g.
///         <c>SdlVulkanWindow.Create</c>) in a try/catch that logs a hint-loaded
///         critical message before rethrowing. This catches the "no suitable
///         Vulkan physical device" failure, which is not a DLL load at all.</item>
/// </list>
/// </summary>
public static class NativeLoaderDiagnostics
{
    private static bool _installed;

    /// <summary>
    /// Registers a resolver on the SDL3-CS assembly so that the first failing
    /// <c>SDL3.dll</c> load is logged with the underlying OS error before the
    /// runtime raises <see cref="DllNotFoundException"/>. Idempotent — safe to
    /// call more than once, subsequent calls are no-ops.
    /// </summary>
    public static void Install(ILogger logger)
    {
        if (_installed)
        {
            return;
        }
        _installed = true;

        // Anchor the resolver on a type from the SDL3-CS assembly — SDL3.SDL is
        // the static class that declares the P/Invokes we want to intercept.
        var sdlAssembly = typeof(SDL3.SDL).Assembly;
        NativeLibrary.SetDllImportResolver(sdlAssembly, (name, assembly, searchPath) =>
        {
            // Only diagnose SDL3 itself; let every other import go through the
            // default resolver untouched.
            if (!string.Equals(name, "SDL3", StringComparison.Ordinal))
            {
                return IntPtr.Zero;
            }

            try
            {
                return NativeLibrary.Load(name, assembly, searchPath);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Failed to load native library '{Library}'. Most common causes on a minimal " +
                    "Windows install: (1) missing VC++ 2015-2022 x64 Redistributable — install from " +
                    "https://aka.ms/vs/17/release/vc_redist.x64.exe; (2) {Library}.dll is missing " +
                    "from the application directory. Run under Sysinternals Process Monitor " +
                    "(filtered on this process) to see the exact failing DLL path.",
                    name, name);
                throw;
            }
        });
    }

    /// <summary>
    /// Invoke a native-init factory and, on failure, log a critical message
    /// with a hint list of the usual Vulkan/GPU/runtime causes before
    /// rethrowing the original exception unchanged.
    /// </summary>
    /// <remarks>
    /// Covers both DLL-load failures (SDL3, vulkan-1) and logic failures like
    /// "no suitable Vulkan physical device found" from <c>VulkanContext</c>.
    /// The rethrow preserves the original stack so the .NET unhandled-exception
    /// handler still produces its usual output on stderr.
    /// </remarks>
    public static T InitNative<T>(ILogger logger, string stage, Func<T> factory)
    {
        try
        {
            return factory();
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "Native initialisation failed at stage '{Stage}'. On a machine without a dedicated " +
                "GPU the most common causes are: (a) VC++ 2015-2022 x64 Redistributable not installed " +
                "(https://aka.ms/vs/17/release/vc_redist.x64.exe); (b) no Vulkan ICD available — " +
                "install up-to-date GPU drivers, or the LunarG Vulkan Runtime " +
                "(https://vulkan.lunarg.com/sdk/home#windows); (c) very old CPU/iGPU with no Vulkan " +
                "1.0 support (rare on Windows 10+).",
                stage);
            throw;
        }
    }
}
