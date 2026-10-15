using System;
using SdlVulkan.Renderer;
using Vortice.Vulkan;

namespace TianWen.UI.Shared;

/// <summary>
/// Loads a pre-baked SPIR-V shader (Shaders/spirv/&lt;shaderName&gt;.spv, embedded by the csproj) and creates a VkShaderModule. GLSL is
/// compiled to SPIR-V at build time by tools/BakeShaders, so there is no runtime shaderc (SdlVulkan.Renderer 6.23 dropped the transitive
/// dependency; baking is what makes an Android build possible - shaderc ships no android RID - and trims AOT/first-frame cost). Re-bake
/// and commit the .spv on a shader edit (the TWSH0001 build warning flags a stale bake). The caller destroys the module.
/// </summary>
internal static unsafe class VkShaderModules
{
    public static VkShaderModule Load(VulkanContext ctx, string shaderName)
    {
        var resource = $"TianWen.UI.Shared.Shaders.{shaderName}.spv";
        using var stream = typeof(VkShaderModules).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException(
                $"Embedded shader '{resource}' not found -- run tools/BakeShaders and commit Shaders/spirv/*.spv.");

        var spirv = new byte[stream.Length];
        stream.ReadExactly(spirv);
        fixed (byte* pSpirv = spirv)
        {
            VkShaderModuleCreateInfo createInfo = new()
            {
                codeSize = (nuint)spirv.Length,
                pCode = (uint*)pSpirv
            };
            ctx.DeviceApi.vkCreateShaderModule(&createInfo, null, out var module).CheckResult();
            return module;
        }
    }
}
