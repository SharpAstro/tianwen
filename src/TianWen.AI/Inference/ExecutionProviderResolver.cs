using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace TianWen.AI.Inference;

/// <summary>
/// Builds an <see cref="SessionOptions"/> with the best available execution
/// provider chain for the current platform. Append order matters: the first
/// EP that successfully appends wins for each node, with later entries acting
/// as fallbacks for unsupported ops.
/// </summary>
/// <remarks>
/// The cross-platform EP matrix (.NET 10 publish):
/// <list type="bullet">
/// <item>Windows arm64 -> QNN (Snapdragon Hexagon NPU), fallback CPU. Note: pure FP32 models fall back per-node to CPU; only INT8/INT16-quantized or QNN-context-binary models actually use the NPU.</item>
/// <item>Windows x64 -> DirectML (D3D12; NVIDIA + AMD + Intel iGPU), fallback CPU.</item>
/// <item>Linux x64 -> CUDA, fallback CPU. Needs libcuda.so.1 + matching cudart on host.</item>
/// <item>Linux arm64 -> CPU only (no GPU EP shipped by default; Jetson is a separate TensorRT story).</item>
/// <item>macOS arm64 -> CoreML (bundled in base package), fallback CPU.</item>
/// <item>macOS x64 -> CPU only.</item>
/// </list>
/// Each <c>AppendExecutionProvider_X</c> call can fail (DLL missing, driver
/// missing, EP not compiled into the bundled native). We catch and log so a
/// missing CUDA driver downgrades to CPU silently instead of crashing.
/// </remarks>
public static class ExecutionProviderResolver
{
    /// <summary>
    /// Builds a <see cref="SessionOptions"/> with the best available EP chain
    /// for the current OS/architecture, falling back to CPU on any append failure.
    /// </summary>
    /// <param name="deviceId">GPU device index (CUDA/DirectML). 0 = first device.</param>
    /// <param name="logger">Optional logger; if null, EP probe failures are swallowed.</param>
    /// <returns>Populated session options. Caller owns disposal.</returns>
    public static SessionOptions CreateSessionOptions(int deviceId = 0, ILogger? logger = null)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };

        foreach (var ep in EnumeratePreferredProviders())
        {
            try
            {
                AppendProvider(options, ep, deviceId);
                logger?.LogInformation("ONNX EP appended: {Provider} (device {DeviceId})", ep, deviceId);
            }
            catch (Exception ex)
            {
                // Append failure typically means the native EP wasn't bundled
                // into the current RID's onnxruntime build (e.g. DirectML
                // requested on a CPU-only package). The CPU EP is always
                // appended last so we still get a working session.
                logger?.LogDebug(ex, "ONNX EP {Provider} unavailable, falling through.", ep);
            }
        }

        return options;
    }

    /// <summary>
    /// The ordered list of EPs we'd try for the current platform, best-first.
    /// CPU is implicit -- it's always available and is appended automatically
    /// by ORT after the explicit chain.
    /// </summary>
    public static IEnumerable<ExecutionProvider> EnumeratePreferredProviders()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // win-arm64 -> Snapdragon, prefer NPU. The QNN package's native
            // build doesn't ship DirectML, so we don't try DML as a fallback
            // here. win-x64 stays on DirectML (no NPU package available).
            if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            {
                yield return ExecutionProvider.Qnn;
            }
            else
            {
                yield return ExecutionProvider.DirectML;
            }
            yield break;
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
            {
                yield return ExecutionProvider.Cuda;
            }
            yield break;
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            {
                yield return ExecutionProvider.CoreML;
            }
            yield break;
        }
    }

    private static void AppendProvider(SessionOptions options, ExecutionProvider provider, int deviceId)
    {
        switch (provider)
        {
            case ExecutionProvider.DirectML:
                options.AppendExecutionProvider_DML(deviceId);
                break;
            case ExecutionProvider.Cuda:
                options.AppendExecutionProvider_CUDA(deviceId);
                break;
            case ExecutionProvider.CoreML:
                // CoreMLFlags.COREML_FLAG_ENABLE_ON_SUBGRAPH lets ORT
                // partition the graph so unsupported ops still run on CPU.
                options.AppendExecutionProvider_CoreML();
                break;
            case ExecutionProvider.Qnn:
                // QNN EP uses string-keyed provider options instead of a typed
                // helper. "backend_path" picks the Hexagon HTP (NPU) backend
                // -- "QnnHtp.dll" for arm64 Windows. Other options the user
                // may want later: "htp_performance_mode", "qnn_context_cache_*"
                // for serialized binary caching, "profiling_level".
                options.AppendExecutionProvider("QNN", new Dictionary<string, string>
                {
                    ["backend_path"] = "QnnHtp.dll"
                });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported provider.");
        }
    }
}

/// <summary>
/// Strongly-typed enum for the EPs this library knows how to append. CPU is
/// implicit (always appended by ORT) and intentionally omitted here.
/// </summary>
public enum ExecutionProvider
{
    DirectML,
    Cuda,
    CoreML,
    /// <summary>
    /// Qualcomm Neural Network EP backed by Hexagon HTP. Only meaningful on
    /// win-arm64 (Snapdragon). Appends successfully on any platform that
    /// bundles the QNN-flavored ORT native, but only accelerates ops the HTP
    /// backend supports at the model's data type -- INT8/INT16 quantized
    /// models or pre-compiled QNN context binaries actually land on the NPU;
    /// FP32 models silently CPU-fall-back at the node level.
    /// </summary>
    Qnn
}
