using System;
using System.Runtime.InteropServices;
using DIR.Lib;
using SdlVulkan.Renderer;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

unsafe
{
    Console.WriteLine($"=== Lavapipe min-repro (all primitives) ===");
    const uint W = 256;
    const uint H = 256;

    vkInitialize().CheckResult();

    VkInstanceCreateInfo plainCI = new();
    vkCreateInstance(&plainCI, null, out var instance).CheckResult();
    using var ctx = VulkanContext.CreateOffscreen(instance, W, H);
    using var renderer = new VkRenderer(ctx, W, H);

    ctx.InstanceApi.vkGetPhysicalDeviceProperties(ctx.PhysicalDevice, out var props);
    var deviceName = System.Text.Encoding.UTF8.GetString(
        MemoryMarshal.CreateReadOnlySpanFromNullTerminated(props.deviceName));
    Console.WriteLine($"Physical device: {deviceName}");

    RGBAColor32 BLACK = new(0, 0, 0, 255);

    int CountNonzero(byte[] rgba)
    {
        var c = 0;
        for (var i = 0; i < rgba.Length; i += 4)
            if (rgba[i] != 0 || rgba[i + 1] != 0 || rgba[i + 2] != 0) c++;
        return c;
    }

    byte[] Draw(Action<VkRenderer> action, string label)
    {
        renderer.BeginOffscreenFrame(BLACK);
        action(renderer);
        renderer.EndOffscreenFrame();
        var rgba = ctx.ReadbackOffscreenRgba();
        var nz = CountNonzero(rgba);
        // Mid-of-canvas pixel for quick visual check.
        var i = (128 * (int)W + 128) * 4;
        Console.WriteLine($"  {label,-30}  nonzero={nz,6}  px(128,128)=({rgba[i]},{rgba[i+1]},{rgba[i+2]},{rgba[i+3]})");
        return rgba;
    }

    // Repro each test's primitive call -- same coords as VkRendererPrimitiveTests.
    var rectFill   = new RectInt(new PointInt(180, 200), new PointInt(50, 60));
    var rectStroke = new RectInt(new PointInt(216, 216), new PointInt(40, 40));
    var rectEllipse = new RectInt(new PointInt(200, 200), new PointInt(60, 60));

    Draw(r => r.FillRectangle(rectFill, new RGBAColor32(255, 0, 0, 255)),
         "FillRectangle (red)");
    Draw(r => r.DrawRectangle(rectStroke, new RGBAColor32(255, 255, 255, 255), 4),
         "DrawRectangle (white,sw=4)");
    Draw(r => r.DrawLine(10, 25, 190, 25, new RGBAColor32(255, 255, 255, 255)),
         "DrawLine horizontal");
    Draw(r => r.DrawLine(128, 10, 128, 246, new RGBAColor32(255, 255, 255, 255)),
         "DrawLine vertical");
    Draw(r => r.DrawLine(20, 20, 230, 230, new RGBAColor32(255, 255, 255, 255)),
         "DrawLine diagonal");
    Draw(r => r.FillEllipse(rectEllipse, new RGBAColor32(0, 255, 0, 255)),
         "FillEllipse (green)");
    Draw(r => r.DrawEllipse(rectEllipse, new RGBAColor32(0, 0, 255, 255), strokeWidth: 3f),
         "DrawEllipse (blue,sw=3)");
}
