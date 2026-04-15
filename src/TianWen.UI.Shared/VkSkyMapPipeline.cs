using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using SdlVulkan.Renderer;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.UI.Abstractions;
using Vortice.ShaderCompiler;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace TianWen.UI.Shared;

/// <summary>
/// Vulkan side-car pipeline for the 3D sky map. Owns its own pipeline layout,
/// descriptor set layout, UBO, and sub-pipelines for stars (instanced quads)
/// and lines (constellation figures, boundaries, grid, horizon, meridian).
///
/// Stars and static lines are stored as J2000 unit vectors in persistent GPU
/// vertex buffers, projected to screen coordinates in the vertex shader via a
/// view rotation matrix + stereographic projection. Pan/zoom only updates the
/// UBO — no geometry re-upload.
/// </summary>
public sealed unsafe class VkSkyMapPipeline : IDisposable
{
    // ────────────────────────────────────────────────── UBO layout (std140)
    //
    // mat4  viewMatrix       offset  0  (64 bytes)
    // vec2  viewportCenter   offset 64  ( 8 bytes)
    // float pixelsPerRadian  offset 72  ( 4 bytes)
    // float magnitudeLimit   offset 76  ( 4 bytes)
    // float fovDeg           offset 80  ( 4 bytes)
    // float sinLat           offset 84  ( 4 bytes)
    // vec2  viewportSize     offset 88  ( 8 bytes)
    // float cosLat           offset 96  ( 4 bytes)
    // float sinLST           offset 100 ( 4 bytes)
    // float cosLST           offset 104 ( 4 bytes)
    // int   horizonClip      offset 108 ( 4 bytes)  1 = clip below horizon
    //                        total: 112 bytes
    private const int UboSize = 112;

    // ────────────────────────────────────────────────── Shader sources

    /// <summary>
    /// Shared GLSL functions for stereographic projection used by both star and line shaders.
    /// </summary>
    private const string ProjectionGlsl = """
        // Stereographic projection: camera-space unit vector to screen pixel position.
        // Returns vec3(screenX, screenY, cosD) where cosD <= -0.99 means antipode.
        vec3 stereoProject(vec3 camPos) {
            float cosD = -camPos.z;  // camera looks along -Z
            if (cosD <= -0.99) return vec3(0.0, 0.0, -2.0);
            float k = 2.0 / (1.0 + cosD);
            // View matrix right vector already points toward decreasing RA (leftward),
            // so camPos.x is positive rightward on screen -- no extra negation needed.
            float sx = ubo.viewportCenter.x + camPos.x * k * ubo.pixelsPerRadian;
            float sy = ubo.viewportCenter.y - camPos.y * k * ubo.pixelsPerRadian;
            return vec3(sx, sy, cosD);
        }
        """;

    private const string StarVertexSource = """
        #version 450

        // Per-vertex (quad corners)
        layout(location = 0) in vec2 aCorner;      // (-1,-1)..(1,1)
        // Per-instance (star data)
        layout(location = 1) in vec3 aUnitPos;      // J2000 unit vector
        layout(location = 2) in float aMagnitude;
        layout(location = 3) in float aBvColor;

        layout(set = 0, binding = 0, std140) uniform SkyMapUBO {
            mat4  viewMatrix;
            vec2  viewportCenter;
            float pixelsPerRadian;
            float magnitudeLimit;
            float fovDeg;
            float sinLat;
            vec2  viewportSize;
            float cosLat;
            float sinLST;
            float cosLST;
            int   horizonClip;
        } ubo;

        layout(location = 0) out vec2 vCorner;       // for radial falloff in FS
        layout(location = 1) out vec3 vColor;         // B-V to RGB
        layout(location = 2) out float vAlpha;

        // Stereographic projection
        PROJECTION_PLACEHOLDER

        // B-V color index to approximate RGB (piecewise linear, matches SkyMapProjection.StarColor)
        vec3 bvToRgb(float bv) {
            bv = clamp(bv, -0.4, 2.0);
            if (bv < 0.0) {
                float t = (bv + 0.4) / 0.4;
                return vec3(155.0 + 100.0 * t, 175.0 + 80.0 * t, 255.0) / 255.0;
            } else if (bv < 0.4) {
                float t = bv / 0.4;
                return vec3(255.0, 255.0 - 25.0 * t, 255.0 - 55.0 * t) / 255.0;
            } else if (bv < 0.8) {
                float t = (bv - 0.4) / 0.4;
                return vec3(255.0, 230.0 - 40.0 * t, 200.0 - 80.0 * t) / 255.0;
            } else if (bv < 1.2) {
                float t = (bv - 0.8) / 0.4;
                return vec3(255.0, 190.0 - 50.0 * t, 120.0 - 60.0 * t) / 255.0;
            } else {
                float t = min((bv - 1.2) / 0.8, 1.0);
                return vec3(255.0, 140.0 - 40.0 * t, 60.0 - 40.0 * t) / 255.0;
            }
        }

        // Raw (un-clamped) star radius in pixels, derived from magnitude and FOV.
        // Matches SkyMapProjection.StarRadius pre-clamp. We clamp in main() so the
        // fragment path can apply a Stellarium-style cubic sub-pixel fade.
        float rawStarRadius(float vMag, float fovDeg) {
            float r = 4.0 * pow(10.0, -0.14 * vMag);
            float zoomScale = sqrt(60.0 / max(1.0, fovDeg));
            return min(r * zoomScale, 15.0);
        }

        void main() {
            // Skip stars beyond magnitude limit
            if (aMagnitude > ubo.magnitudeLimit) {
                gl_Position = vec4(0.0, 0.0, 0.0, 0.0);
                return;
            }

            // Skip stars below the horizon:
            // sin(alt) = sinLat*z + cosLat*(cosLST*x + sinLST*y)
            if (ubo.horizonClip != 0) {
                float sinAlt = ubo.sinLat * aUnitPos.z
                    + ubo.cosLat * (ubo.cosLST * aUnitPos.x + ubo.sinLST * aUnitPos.y);
                if (sinAlt < 0.0) {
                    gl_Position = vec4(0.0, 0.0, 0.0, 0.0);
                    return;
                }
            }

            // Rotate to camera space and project
            vec3 camPos = (ubo.viewMatrix * vec4(aUnitPos, 1.0)).xyz;
            vec3 proj = stereoProject(camPos);
            if (proj.z <= -0.99) {
                gl_Position = vec4(0.0, 0.0, 0.0, 0.0);
                return;
            }

            // Two-part gradual reveal so stars appear smoothly as zoom widens the
            // effective magnitude limit:
            //
            //   1. Sub-pixel fade: stars with rawR < 1.0 px get clamped to 1.0 px
            //      with a mild linear luminance drop (no harsh cubic, without eye
            //      adaptation that just blacks out the sky).
            //   2. Magnitude-edge fade: the last 0.8 mag below the effective limit
            //      fades linearly from 1.0 to 0.0. When the limit rises (zoom in)
            //      new stars bloom gently instead of popping in at full brightness.
            float rawR = rawStarRadius(aMagnitude, ubo.fovDeg);
            float kMin = 1.0;
            float subPixelFade = clamp(rawR / kMin, 0.35, 1.0);
            float radius = max(rawR, kMin);

            float magFadeWidth = 0.8;
            float magFade = clamp((ubo.magnitudeLimit - aMagnitude) / magFadeWidth, 0.0, 1.0);

            // Expand quad generously - analytic Gaussian PSF in the FS needs headroom
            // for the soft halo. +3 matches Stellarium's pre-baked 16x16 halo texture.
            vec2 screenPos = proj.xy + aCorner * (radius + 3.0);

            // Map screen pixels to Vulkan NDC: x in [-1,1], y in [-1,1] (Y-down)
            gl_Position = vec4(
                screenPos.x / ubo.viewportSize.x * 2.0 - 1.0,
                screenPos.y / ubo.viewportSize.y * 2.0 - 1.0,
                0.0, 1.0);

            vCorner = aCorner;
            vColor = bvToRgb(aBvColor);

            // Final alpha = base magnitude brightness * sub-pixel fade * edge fade.
            // The edge fade is what produces the "gradual reveal" as zoom rises the limit.
            float brightness = clamp(1.0 - aMagnitude / ubo.magnitudeLimit, 0.0, 1.0);
            vAlpha = (0.75 + 0.25 * brightness) * subPixelFade * magFade;
        }
        """;

    private const string StarFragmentSource = """
        #version 450

        layout(location = 0) in vec2 vCorner;
        layout(location = 1) in vec3 vColor;
        layout(location = 2) in float vAlpha;

        layout(location = 0) out vec4 FragColor;

        void main() {
            // Radial distance from quad center [0, sqrt(2)]
            float dist = length(vCorner);

            // Analytic PSF - soft halo without a bound texture.
            //   core  : wide flat disc at full brightness (covers the visible star body).
            //           With the quad expanded by +3 px, the star's nominal radius sits
            //           near dist ~ r/(r+3), so the core must stay flat into that range.
            //   halo  : Gaussian-like falloff for the glow.
            // Numbers tuned so small stars stay bright near the center and dim stars
            // aren't washed out by the Gaussian shoulder.
            float core  = 1.0 - smoothstep(0.32, 0.55, dist);
            float halo  = exp(-dist * dist * 3.2);
            float alpha = max(core, halo);
            if (alpha < 0.005) discard;

            FragColor = vec4(vColor * alpha * vAlpha, alpha * vAlpha);
        }
        """;

    private const string LineVertexSource = """
        #version 450

        layout(location = 0) in vec3 aUnitPos;

        layout(set = 0, binding = 0, std140) uniform SkyMapUBO {
            mat4  viewMatrix;
            vec2  viewportCenter;
            float pixelsPerRadian;
            float magnitudeLimit;
            float fovDeg;
            float sinLat;
            vec2  viewportSize;
            float cosLat;
            float sinLST;
            float cosLST;
            int   horizonClip;
        } ubo;

        layout(push_constant) uniform PC {
            vec4 color;
        } pc;

        layout(location = 0) out vec4 vColor;

        PROJECTION_PLACEHOLDER

        void main() {
            vec3 camPos = (ubo.viewMatrix * vec4(aUnitPos, 1.0)).xyz;
            vec3 proj = stereoProject(camPos);
            if (proj.z <= -0.99) {
                // Move to clip-space boundary so the line segment is clipped
                gl_Position = vec4(2.0, 2.0, 0.0, 1.0);
                vColor = vec4(0.0);
                return;
            }

            gl_Position = vec4(
                proj.x / ubo.viewportSize.x * 2.0 - 1.0,
                proj.y / ubo.viewportSize.y * 2.0 - 1.0,
                0.0, 1.0);
            vColor = pc.color;
        }
        """;

    private const string LineFragmentSource = """
        #version 450

        layout(location = 0) in vec4 vColor;
        layout(location = 0) out vec4 FragColor;

        void main() {
            FragColor = vColor;
        }
        """;

    // Full-screen quad vertex shader (no vertex input, generates 2 triangles from gl_VertexIndex)
    private const string HorizonFillVertexSource = """
        #version 450

        layout(location = 0) out vec2 vScreenPos;

        layout(set = 0, binding = 0, std140) uniform SkyMapUBO {
            mat4  viewMatrix;
            vec2  viewportCenter;
            float pixelsPerRadian;
            float magnitudeLimit;
            float fovDeg;
            float sinLat;
            vec2  viewportSize;
            float cosLat;
            float sinLST;
            float cosLST;
            int   horizonClip;
        } ubo;

        void main() {
            // Generate full-screen triangle strip from vertex index (0..5 = 2 triangles)
            vec2 pos;
            if (gl_VertexIndex == 0)      pos = vec2(0.0, 0.0);
            else if (gl_VertexIndex == 1) pos = vec2(ubo.viewportSize.x, 0.0);
            else if (gl_VertexIndex == 2) pos = vec2(0.0, ubo.viewportSize.y);
            else if (gl_VertexIndex == 3) pos = vec2(ubo.viewportSize.x, 0.0);
            else if (gl_VertexIndex == 4) pos = vec2(ubo.viewportSize.x, ubo.viewportSize.y);
            else                          pos = vec2(0.0, ubo.viewportSize.y);

            // Output screen-pixel position for the fragment shader
            vScreenPos = pos + vec2(ubo.viewportCenter.x - ubo.viewportSize.x * 0.5,
                                    ubo.viewportCenter.y - ubo.viewportSize.y * 0.5);

            // Map to NDC
            gl_Position = vec4(
                pos.x / ubo.viewportSize.x * 2.0 - 1.0,
                pos.y / ubo.viewportSize.y * 2.0 - 1.0,
                0.0, 1.0);
        }
        """;

    private const string HorizonFillFragmentSource = """
        #version 450

        layout(location = 0) in vec2 vScreenPos;
        layout(location = 0) out vec4 FragColor;

        layout(set = 0, binding = 0, std140) uniform SkyMapUBO {
            mat4  viewMatrix;
            vec2  viewportCenter;
            float pixelsPerRadian;
            float magnitudeLimit;
            float fovDeg;
            float sinLat;
            vec2  viewportSize;
            float cosLat;
            float sinLST;
            float cosLST;
            int   horizonClip;
        } ubo;

        void main() {
            // Inverse stereographic projection: screen pixel -> camera-space unit vector
            float x = (vScreenPos.x - ubo.viewportCenter.x) / ubo.pixelsPerRadian;
            float y = -(vScreenPos.y - ubo.viewportCenter.y) / ubo.pixelsPerRadian;

            // Warm earthy-brown tint for the below-horizon region. Mixed on top of
            // the sky background with depth-scaled alpha so pixels just below the
            // horizon are lightly hazed, and pixels deep below are mostly ground.
            vec3 groundTint = vec3(0.18, 0.11, 0.06);

            float rho = length(vec2(x, y));
            if (rho < 0.00001) {
                vec3 j2000 = transpose(mat3(ubo.viewMatrix)) * vec3(0.0, 0.0, -1.0);
                float sinAlt = ubo.sinLat * j2000.z
                    + ubo.cosLat * (ubo.cosLST * j2000.x + ubo.sinLST * j2000.y);
                if (sinAlt >= 0.0) discard;
                FragColor = vec4(groundTint, 0.85);
                return;
            }

            float c = 2.0 * atan(rho * 0.5);
            float sinC = sin(c);
            float cosC = cos(c);

            // Camera-space unit vector
            vec3 camDir = vec3(
                sinC * x / rho,
                sinC * y / rho,
                -cosC
            );

            // Rotate back to J2000 (view matrix is orthogonal, inverse = transpose)
            vec3 j2000 = transpose(mat3(ubo.viewMatrix)) * camDir;

            // Compute altitude: sin(alt) = sinLat*z + cosLat*(cosLST*x + sinLST*y)
            float sinAlt = ubo.sinLat * j2000.z
                + ubo.cosLat * (ubo.cosLST * j2000.x + ubo.sinLST * j2000.y);

            if (sinAlt >= 0.0) discard;

            // Fade from 0.45 at horizon to 0.88 at -10 degrees (sin(10deg) ~ 0.17)
            float depth = clamp(-sinAlt / 0.17, 0.0, 1.0);
            float alpha = 0.45 + 0.43 * depth;
            FragColor = vec4(groundTint, alpha);
        }
        """;

    // ────────────────────────────────────────────────── Fields

    private readonly VulkanContext _ctx;

    // Descriptor set layout + pool + set for the UBO
    private VkDescriptorSetLayout _uboSetLayout;
    private VkDescriptorPool _descriptorPool;
    private VkDescriptorSet _uboSet;

    // Pipeline layout (UBO at set 0, push constants for line color)
    private VkPipelineLayout _pipelineLayout;

    // Sub-pipelines
    private VkPipeline _starPipeline;
    private VkPipeline _linePipeline;
    private VkPipeline _horizonFillPipeline;

    // UBO buffer (persistently mapped)
    private VkBuffer _uboBuffer;
    private VkDeviceMemory _uboMemory;
    private byte* _uboMapped;

    // Quad vertex buffer for star instancing (6 vertices: 2 triangles)
    private VkBuffer _quadBuffer;
    private VkDeviceMemory _quadMemory;

    // Persistent vertex buffers
    private VkBuffer _starBuffer;
    private VkDeviceMemory _starMemory;
    private uint _starCount;

    private VkBuffer _figureBuffer;
    private VkDeviceMemory _figureMemory;
    private uint _figureVertexCount;

    private VkBuffer _boundaryBuffer;
    private VkDeviceMemory _boundaryMemory;
    private uint _boundaryVertexCount;

    // Grid: one buffer per scale level, each a line list of unit vectors
    private readonly (VkBuffer Buffer, VkDeviceMemory Memory, uint VertexCount)[] _gridBuffers = new (VkBuffer, VkDeviceMemory, uint)[5];

    private bool _disposed;
    private bool _geometryBuilt;

    // ────────────────────────────────────────────────── Construction

    public VkSkyMapPipeline(VulkanContext ctx)
    {
        _ctx = ctx;

        CreateDescriptorSetLayout();
        CreateDescriptorPool();
        AllocateDescriptorSet();
        CreatePipelineLayout();
        CreateUboBuffer();
        CreateQuadBuffer();
        CreatePipelines();
    }

    // ────────────────────────────────────────────────── Public API

    /// <summary>True once <see cref="BuildGeometry"/> has been called with a valid catalog.</summary>
    public bool GeometryReady => _geometryBuilt;

    /// <summary>
    /// Build all persistent vertex buffers from the star catalog. Call once when
    /// <see cref="ICelestialObjectDB"/> becomes available.
    /// </summary>
    public void BuildGeometry(ICelestialObjectDB db)
    {
        if (_geometryBuilt)
        {
            return;
        }

        BuildStarBuffer(db);
        BuildConstellationFigureBuffer(db);
        BuildConstellationBoundaryBuffer();
        BuildGridBuffers();

        _geometryBuilt = true;
    }

    /// <summary>
    /// Update the UBO with current view parameters. Call once per frame before drawing.
    /// </summary>
    public void UpdateUbo(
        SkyMapState state,
        float viewportWidth, float viewportHeight,
        float offsetX, float offsetY,
        Lib.Astrometry.SOFA.SiteContext site)
    {
        // Compute LST trig + zenith direction in J2000 for Alt/Az mode
        var (fSinLST, fCosLST) = site.IsValid
            ? Math.SinCos(site.LST * (Math.PI / 12.0))
            : (0.0, 1.0);
        float zenithX = (float)(site.CosLat * fCosLST);
        float zenithY = (float)(site.CosLat * fSinLST);
        float zenithZ = (float)site.SinLat;
        var viewMatrix = state.ComputeViewMatrix(zenithX, zenithY, zenithZ);
        state.CurrentViewMatrix = viewMatrix;
        var ppr = (float)SkyMapProjection.PixelsPerRadian(viewportHeight, state.FieldOfViewDeg);

        var p = _uboMapped;

        // mat4 viewMatrix at offset 0 (64 bytes) — Matrix4x4 is row-major in memory,
        // but GLSL mat4 expects column-major. Transpose before writing.
        var transposed = Matrix4x4.Transpose(viewMatrix);
        Unsafe.CopyBlock(p, Unsafe.AsPointer(ref transposed), 64);

        // vec2 viewportCenter at offset 64
        WriteFloat(p, 64, offsetX + viewportWidth * 0.5f);
        WriteFloat(p, 68, offsetY + viewportHeight * 0.5f);

        // float pixelsPerRadian at offset 72
        WriteFloat(p, 72, ppr);

        // float magnitudeLimit at offset 76 — use the FOV-aware effective limit so
        // zooming in automatically reveals fainter stars (Stellarium computeRCMag idea).
        WriteFloat(p, 76, state.EffectiveMagnitudeLimit);

        // float fovDeg at offset 80
        WriteFloat(p, 80, (float)state.FieldOfViewDeg);

        // float sinLat at offset 84
        WriteFloat(p, 84, (float)site.SinLat);

        // vec2 viewportSize at offset 88
        WriteFloat(p, 88, viewportWidth);
        WriteFloat(p, 92, viewportHeight);

        // float cosLat at offset 96
        WriteFloat(p, 96, (float)site.CosLat);

        // float sinLST, cosLST at offset 100, 104
        WriteFloat(p, 100, (float)fSinLST);
        WriteFloat(p, 104, (float)fCosLST);

        // int horizonClip at offset 108
        WriteInt(p, 108, state.ShowHorizon && site.IsValid ? 1 : 0);
    }

    /// <summary>
    /// Record all sky map draw commands into the current command buffer.
    /// Call between <c>renderer.BeginFrame()</c> and <c>renderer.EndFrame()</c>.
    /// </summary>
    public void Draw(
        VkCommandBuffer cmd,
        SkyMapState state,
        float viewportWidth, float viewportHeight,
        float offsetX, float offsetY,
        // Dynamic line buffers — written to ring buffer by caller
        (VkBuffer Buffer, uint ByteOffset, uint VertexCount) horizon,
        (VkBuffer Buffer, uint ByteOffset, uint VertexCount) meridian,
        (VkBuffer Buffer, uint ByteOffset, uint VertexCount) altAzGrid)
    {
        if (!_geometryBuilt)
        {
            return;
        }

        var api = _ctx.DeviceApi;

        // Set viewport and scissor for the sky map area
        VkViewport viewport = new()
        {
            x = offsetX,
            y = offsetY,
            width = viewportWidth,
            height = viewportHeight,
            minDepth = 0f,
            maxDepth = 1f
        };
        VkRect2D scissor = new()
        {
            offset = new VkOffset2D((int)offsetX, (int)offsetY),
            extent = new VkExtent2D((uint)viewportWidth, (uint)viewportHeight)
        };
        api.vkCmdSetViewport(cmd, 0, 1, &viewport);
        api.vkCmdSetScissor(cmd, 0, 1, &scissor);

        // Bind UBO descriptor set (shared by all sub-pipelines)
        var uboSet = _uboSet;
        api.vkCmdBindDescriptorSets(cmd, VkPipelineBindPoint.Graphics, _pipelineLayout,
            0, 1, &uboSet, 0, null);

        // ── Horizon fill (drawn first, behind everything) ──
        if (state.ShowHorizon && _horizonFillPipeline != VkPipeline.Null)
        {
            api.vkCmdBindPipeline(cmd, VkPipelineBindPoint.Graphics, _horizonFillPipeline);
            // Re-bind UBO after pipeline change
            api.vkCmdBindDescriptorSets(cmd, VkPipelineBindPoint.Graphics, _pipelineLayout,
                0, 1, &uboSet, 0, null);
            api.vkCmdDraw(cmd, 6, 1, 0, 0); // full-screen quad (6 vertices from gl_VertexIndex)
        }

        // ── Lines: grid, meridian, boundaries, constellation figures, horizon ──
        api.vkCmdBindPipeline(cmd, VkPipelineBindPoint.Graphics, _linePipeline);

        // Grid (back to front: coarsest first)
        if (state.ShowGrid)
        {
            DrawGrid(cmd, state);
        }

        // Alt/Az grid
        if (state.ShowAltAzGrid && altAzGrid.VertexCount > 0)
        {
            PushLineColor(cmd, 0x80, 0xA0, 0x30, 0x80); // olive/yellow-green, semi-transparent
            DrawLineBuffer(cmd, altAzGrid.Buffer, altAzGrid.ByteOffset, altAzGrid.VertexCount);
        }

        // Meridian
        if (meridian.VertexCount > 0)
        {
            PushLineColor(cmd, 0x30, 0xDD, 0x30, 0xA0); // green
            DrawLineBuffer(cmd, meridian.Buffer, meridian.ByteOffset, meridian.VertexCount);
        }

        // Constellation boundaries
        if (state.ShowConstellationBoundaries && _boundaryVertexCount > 0)
        {
            PushLineColor(cmd, 0xAA, 0x44, 0x44, 0x80); // red, semi-transparent
            DrawLineBuffer(cmd, _boundaryBuffer, 0, _boundaryVertexCount);
        }

        // Constellation figures
        if (state.ShowConstellationFigures && _figureVertexCount > 0)
        {
            PushLineColor(cmd, 0x40, 0x80, 0xDD, 0x90); // blue stick figures
            DrawLineBuffer(cmd, _figureBuffer, 0, _figureVertexCount);
        }

        // Horizon
        if (state.ShowHorizon && horizon.VertexCount > 0)
        {
            PushLineColor(cmd, 0x80, 0x40, 0x20, 0xFF); // brown
            DrawLineBuffer(cmd, horizon.Buffer, horizon.ByteOffset, horizon.VertexCount);
        }

        // ── Stars ──
        if (_starCount > 0)
        {
            api.vkCmdBindPipeline(cmd, VkPipelineBindPoint.Graphics, _starPipeline);

            // Bind quad (binding 0) and star instance data (binding 1)
            var quadBuf = _quadBuffer;
            var starBuf = _starBuffer;
            var offsets = stackalloc ulong[2];
            offsets[0] = 0;
            offsets[1] = 0;
            var buffers = stackalloc VkBuffer[2];
            buffers[0] = quadBuf;
            buffers[1] = starBuf;
            api.vkCmdBindVertexBuffers(cmd, 0, 2, buffers, offsets);

            // 6 vertices per quad, _starCount instances
            api.vkCmdDraw(cmd, 6, _starCount, 0, 0);
        }
    }

    // ────────────────────────────────────────────────── Grid drawing

    // Grid scale definitions: (raStepHours, decStepDeg, minFov, maxFov)
    private static readonly (double RaStep, double DecStep, double MinFov, double MaxFov)[] GridScales =
    [
        (6.0,  30.0,  30.0, 999.0),
        (3.0,  15.0,  10.0, 120.0),
        (1.0,  10.0,   3.0,  40.0),
        (0.5,   5.0,   1.0,  15.0),
        (10.0 / 60.0, 1.0, 0.2, 5.0),
    ];

    private void DrawGrid(VkCommandBuffer cmd, SkyMapState state)
    {
        var fov = state.FieldOfViewDeg;

        for (var i = 0; i < GridScales.Length; i++)
        {
            var (_, _, minFov, maxFov) = GridScales[i];
            if (fov > maxFov)
            {
                continue;
            }

            // Fade in: full alpha when FOV < minFov*2, zero when FOV >= maxFov
            var fade = fov < minFov * 2
                ? 1.0
                : Math.Clamp((maxFov - fov) / (maxFov - minFov * 2), 0, 1);
            if (fade < 0.05)
            {
                continue;
            }

            var alpha = (byte)(0xB0 * fade);
            PushLineColor(cmd, 0x30, 0x60, 0xA0, alpha);

            var (buffer, _, vertexCount) = _gridBuffers[i];
            if (vertexCount > 0)
            {
                DrawLineBuffer(cmd, buffer, 0, vertexCount);
            }
        }
    }

    // ────────────────────────────────────────────────── Draw helpers

    private void PushLineColor(VkCommandBuffer cmd, byte r, byte g, byte b, byte a)
    {
        var color = stackalloc float[4];
        color[0] = r / 255f;
        color[1] = g / 255f;
        color[2] = b / 255f;
        color[3] = a / 255f;
        _ctx.DeviceApi.vkCmdPushConstants(cmd, _pipelineLayout,
            VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment, 0, 16, color);
    }

    private void DrawLineBuffer(VkCommandBuffer cmd, VkBuffer buffer, uint byteOffset, uint vertexCount)
    {
        var api = _ctx.DeviceApi;
        var buf = buffer;
        var offset = (ulong)byteOffset;
        api.vkCmdBindVertexBuffers(cmd, 0, 1, &buf, &offset);
        api.vkCmdDraw(cmd, vertexCount, 1, 0, 0);
    }

    // ────────────────────────────────────────────────── Geometry builders

    private void BuildStarBuffer(ICelestialObjectDB db)
    {
        // Stream the full Tycho-2 catalog (~2.5M stars). HIP stars are a subset so we
        // don't lose any bright stars by skipping the dedicated HIP loop. The existing
        // vertex-shader magnitude cull means the GPU only rasterises the few thousand
        // stars that actually pass the current EffectiveMagnitudeLimit each frame.
        var tycCount = db.Tycho2StarCount;
        if (tycCount == 0)
        {
            // Fallback: older databases without the Tycho-2 binary still populate HIP.
            BuildStarBufferFromHip(db);
            return;
        }

        // Per-star GPU vertex layout: vec3 pos + float vMag + float bv = 5 floats.
        const int floatsPerStar = 5;

        // Read Tycho-2 records in chunks — keeps the temp alloc bounded (~16 MB) while
        // still minimising the number of CopyTycho2Stars calls.
        const int chunkSize = 200_000;
        var chunk = new Tycho2StarLite[chunkSize];

        // Worst case: one output slot per input star. Right-size to avoid List<T> growth.
        var floats = new List<float>(tycCount * floatsPerStar);

        var copied = 0;
        while (copied < tycCount)
        {
            var wanted = Math.Min(chunkSize, tycCount - copied);
            var n = db.CopyTycho2Stars(chunk.AsSpan(0, wanted), copied);
            if (n == 0)
            {
                break;
            }

            for (int i = 0; i < n; i++)
            {
                var s = chunk[i];
                if (float.IsNaN(s.VMag))
                {
                    continue;
                }
                var (x, y, z) = SkyMapState.RaDecToUnitVec(s.RaHours, s.DecDeg);
                floats.Add(x);
                floats.Add(y);
                floats.Add(z);
                floats.Add(s.VMag);
                floats.Add(float.IsNaN(s.BMinusV) ? 0.65f : s.BMinusV);
            }

            copied += n;
        }

        _starCount = (uint)(floats.Count / floatsPerStar);
        (_starBuffer, _starMemory) = _ctx.CreatePersistentVertexBuffer(
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(floats));
    }

    /// <summary>
    /// Legacy HIP-only path. Used when no Tycho-2 binary is available
    /// (e.g. stripped-down test doubles of <see cref="ICelestialObjectDB"/>).
    /// </summary>
    private void BuildStarBufferFromHip(ICelestialObjectDB db)
    {
        var floats = new List<float>(db.HipStarCount * 5);

        for (var hip = 1; hip <= db.HipStarCount; hip++)
        {
            if (!db.TryLookupHIP(hip, out var ra, out var dec, out var vMag, out var bv))
            {
                continue;
            }

            if (float.IsNaN(vMag))
            {
                continue;
            }

            var (x, y, z) = SkyMapState.RaDecToUnitVec(ra, dec);
            floats.Add(x);
            floats.Add(y);
            floats.Add(z);
            floats.Add(vMag);
            floats.Add(float.IsNaN(bv) ? 0.65f : bv); // default to solar B-V if unknown
        }

        _starCount = (uint)(floats.Count / 5);
        (_starBuffer, _starMemory) = _ctx.CreatePersistentVertexBuffer(
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(floats));
    }

    private void BuildConstellationFigureBuffer(ICelestialObjectDB db)
    {
        var floats = new List<float>(4096);

        foreach (var constellation in Enum.GetValues<Constellation>())
        {
            if (constellation is Constellation.SerpensCaput or Constellation.SerpensCauda)
            {
                continue;
            }

            var figure = constellation.Figure;
            if (figure.IsDefaultOrEmpty)
            {
                continue;
            }

            foreach (var polyline in figure)
            {
                float prevX = 0, prevY = 0, prevZ = 0;
                var hasPrev = false;

                foreach (var hip in polyline)
                {
                    if (!db.TryLookupHIP(hip, out var ra, out var dec, out _, out _))
                    {
                        hasPrev = false;
                        continue;
                    }

                    var (x, y, z) = SkyMapState.RaDecToUnitVec(ra, dec);

                    if (hasPrev)
                    {
                        // Emit line segment: prev → current
                        floats.Add(prevX); floats.Add(prevY); floats.Add(prevZ);
                        floats.Add(x); floats.Add(y); floats.Add(z);
                    }

                    prevX = x; prevY = y; prevZ = z;
                    hasPrev = true;
                }
            }
        }

        _figureVertexCount = (uint)(floats.Count / 3);
        if (_figureVertexCount > 0)
        {
            (_figureBuffer, _figureMemory) = _ctx.CreatePersistentVertexBuffer(
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(floats));
        }
    }

    private void BuildConstellationBoundaryBuffer()
    {
        var floats = new List<float>(65536);

        // IAU constellation boundaries are defined in B1875 (Delporte 1930 standard).
        // Our stars + RA/Dec grid are J2000. Without precession the boundaries sit
        // ~1.74 degrees offset from the stars they delimit, and the stereographic
        // projection amplifies that offset non-uniformly across the screen — which
        // the user perceives as boundaries and stars moving at different speeds
        // during pan. Precess each tessellated point from B1875 -> J2000.
        const double FromEpoch = 1875.0;
        const double ToEpoch = 2000.0;

        static (double RA, double Dec) Precess1875ToJ2000(double ra, double dec)
            => CoordinateUtils.Precess(ra, dec, FromEpoch, ToEpoch);

        foreach (var edge in ConstellationEdges.Edges)
        {
            if (edge.Type == ConstellationEdges.EdgeType.Parallel)
            {
                // Constant B1875 Dec arc from RA1 to RA2. We tessellate in B1875
                // (the shape is correct there) and precess each point to J2000
                // before mapping to the unit sphere.
                var raRange = edge.RA2 - edge.RA1;
                if (raRange < -12) raRange += 24;
                if (raRange > 12) raRange -= 24;
                var steps = Math.Max(5, (int)(Math.Abs(raRange) * 8));
                TessellateArc(floats, steps, i =>
                    Precess1875ToJ2000(edge.RA1 + i * raRange / steps, edge.Dec1));
            }
            else if (edge.Type == ConstellationEdges.EdgeType.Meridian)
            {
                // Constant B1875 RA arc from Dec1 to Dec2 — precessed per step.
                var decRange = edge.Dec2 - edge.Dec1;
                var steps = Math.Max(5, (int)(Math.Abs(decRange) / 2));
                TessellateArc(floats, steps, i =>
                    Precess1875ToJ2000(edge.RA1, edge.Dec1 + i * decRange / steps));
            }
            else
            {
                // Straight segment: just two endpoints, both precessed.
                var (p1RA, p1Dec) = Precess1875ToJ2000(edge.RA1, edge.Dec1);
                var (p2RA, p2Dec) = Precess1875ToJ2000(edge.RA2, edge.Dec2);
                var (x1, y1, z1) = SkyMapState.RaDecToUnitVec(p1RA, p1Dec);
                var (x2, y2, z2) = SkyMapState.RaDecToUnitVec(p2RA, p2Dec);
                floats.Add(x1); floats.Add(y1); floats.Add(z1);
                floats.Add(x2); floats.Add(y2); floats.Add(z2);
            }
        }

        _boundaryVertexCount = (uint)(floats.Count / 3);
        if (_boundaryVertexCount > 0)
        {
            (_boundaryBuffer, _boundaryMemory) = _ctx.CreatePersistentVertexBuffer(
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(floats));
        }
    }

    private void BuildGridBuffers()
    {
        for (var i = 0; i < GridScales.Length; i++)
        {
            var (raStep, decStep, _, _) = GridScales[i];
            var floats = new List<float>(32768);

            // RA lines (constant RA, varying Dec — great circles)
            // Skip values already drawn by coarser scales
            var lineSteps = Math.Max(60, Math.Min(200, (int)(180.0 / decStep * 2)));
            for (var ra = 0.0; ra < 24.0; ra += raStep)
            {
                if (IsCoarserRaLine(ra, i))
                {
                    continue;
                }
                TessellateArc(floats, lineSteps, j => (ra, -90.0 + j * 180.0 / lineSteps));
            }

            // Dec lines (constant Dec, varying RA — small circles)
            // Skip values already drawn by coarser scales
            var raSteps = Math.Max(60, Math.Min(200, (int)(24.0 / raStep * 2)));
            for (var dec = -90.0 + decStep; dec < 90.0; dec += decStep)
            {
                if (IsCoarserDecLine(dec, i))
                {
                    continue;
                }
                TessellateArc(floats, raSteps, j => (j * 24.0 / raSteps, dec));
            }

            var vertexCount = (uint)(floats.Count / 3);
            if (vertexCount > 0)
            {
                var (buffer, memory) = _ctx.CreatePersistentVertexBuffer(
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(floats));
                _gridBuffers[i] = (buffer, memory, vertexCount);
            }
        }
    }

    /// <summary>True if this RA value is already drawn by a coarser grid scale.</summary>
    private static bool IsCoarserRaLine(double ra, int scaleIndex)
    {
        for (var j = 0; j < scaleIndex; j++)
        {
            var coarserStep = GridScales[j].RaStep;
            // Check if ra is an exact multiple of the coarser step (within tolerance)
            var remainder = ra % coarserStep;
            if (remainder < 1e-9 || coarserStep - remainder < 1e-9)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>True if this Dec value is already drawn by a coarser grid scale.</summary>
    private static bool IsCoarserDecLine(double dec, int scaleIndex)
    {
        for (var j = 0; j < scaleIndex; j++)
        {
            var coarserStep = GridScales[j].DecStep;
            var remainder = Math.Abs(dec) % coarserStep;
            if (remainder < 1e-9 || coarserStep - remainder < 1e-9)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Tessellate an arc into line segments (line list topology: 2 vertices per segment).
    /// </summary>
    private static void TessellateArc(List<float> floats, int steps, Func<int, (double RA, double Dec)> coordFunc)
    {
        float prevX = 0, prevY = 0, prevZ = 0;
        var hasPrev = false;

        for (var i = 0; i <= steps; i++)
        {
            var (ra, dec) = coordFunc(i);
            var (x, y, z) = SkyMapState.RaDecToUnitVec(ra, dec);

            if (hasPrev)
            {
                floats.Add(prevX); floats.Add(prevY); floats.Add(prevZ);
                floats.Add(x); floats.Add(y); floats.Add(z);
            }

            prevX = x; prevY = y; prevZ = z;
            hasPrev = true;
        }
    }

    /// <summary>
    /// Build dynamic line geometry for the horizon curve. Returns floats for a line list
    /// of unit vectors. Caller writes these to the ring buffer via <c>ctx.WriteVertices</c>.
    /// </summary>
    public static void BuildHorizonLine(
        Lib.Astrometry.SOFA.SiteContext site,
        List<float> floats)
    {
        if (!site.IsValid)
        {
            return;
        }

        const int steps = 120;
        float prevX = 0, prevY = 0, prevZ = 0;
        var hasPrev = false;

        for (var i = 0; i <= steps; i++)
        {
            var ra = i * 24.0 / steps;
            var decHorizon = site.HorizonDec(ra);
            var (x, y, z) = SkyMapState.RaDecToUnitVec(ra, decHorizon);

            if (hasPrev)
            {
                floats.Add(prevX); floats.Add(prevY); floats.Add(prevZ);
                floats.Add(x); floats.Add(y); floats.Add(z);
            }

            prevX = x; prevY = y; prevZ = z;
            hasPrev = true;
        }
    }

    /// <summary>
    /// Build dynamic line geometry for the meridian (LST great circle).
    /// </summary>
    public static void BuildMeridianLine(double lst, List<float> floats)
    {
        const int steps = 200;
        var antiLst = (lst + 12.0) % 24.0;

        // First half: LST line from south pole to north pole
        TessellateArc(floats, steps / 2, i => (lst, -90.0 + i * 180.0 / (steps / 2)));
        // Second half: anti-LST line from north pole to south pole
        TessellateArc(floats, steps / 2, i => (antiLst, 90.0 - i * 180.0 / (steps / 2)));
    }

    /// <summary>
    /// Build dynamic Alt/Az grid lines. Altitude circles at 10/20/30/45/60/80 degrees,
    /// azimuth lines every 30 degrees. Converts (Alt, Az) to J2000 unit vectors using
    /// the observer's latitude and LST.
    /// </summary>
    public static void BuildAltAzGrid(
        Lib.Astrometry.SOFA.SiteContext site,
        List<float> floats)
    {
        if (!site.IsValid)
        {
            return;
        }

        // Altitude circles: constant altitude, sweep azimuth 0..360
        double[] altitudes = [10, 20, 30, 45, 60, 80];
        const int azSteps = 120;

        foreach (var alt in altitudes)
        {
            TessellateArc(floats, azSteps, i =>
            {
                var az = i * 360.0 / azSteps;
                AltAzToRaDec(alt, az, site, out var ra, out var dec);
                return (ra, dec);
            });
        }

        // Azimuth lines: constant azimuth, sweep altitude 0..89
        const int altSteps = 60;
        for (var az = 0.0; az < 360.0; az += 30.0)
        {
            TessellateArc(floats, altSteps, i =>
            {
                var a = i * 89.0 / altSteps;
                AltAzToRaDec(a, az, site, out var ra, out var dec);
                return (ra, dec);
            });
        }
    }

    /// <summary>
    /// Convert horizontal coordinates (Alt, Az in degrees) to equatorial (RA in hours, Dec in degrees).
    /// </summary>
    private static void AltAzToRaDec(
        double altDeg, double azDeg,
        Lib.Astrometry.SOFA.SiteContext site,
        out double raHours, out double decDeg)
    {
        var (sinAlt, cosAlt) = Math.SinCos(double.DegreesToRadians(altDeg));
        var (sinAz, cosAz) = Math.SinCos(double.DegreesToRadians(azDeg));

        var sinDec = site.SinLat * sinAlt + site.CosLat * cosAlt * cosAz;
        decDeg = double.RadiansToDegrees(Math.Asin(sinDec));

        var cosDec = Math.Cos(Math.Asin(sinDec));
        if (Math.Abs(cosDec) < 1e-12)
        {
            raHours = site.LST;
            return;
        }

        var sinHA = -sinAz * cosAlt / cosDec;
        var cosHA = (sinAlt - site.SinLat * sinDec) / (site.CosLat * cosDec);
        var ha = Math.Atan2(sinHA, cosHA); // radians

        raHours = (site.LST - ha / (Math.PI / 12.0)) % 24.0;
        if (raHours < 0) raHours += 24.0;
    }

    // ────────────────────────────────────────────────── Vulkan setup

    private void CreateDescriptorSetLayout()
    {
        var api = _ctx.DeviceApi;
        VkDescriptorSetLayoutBinding uboBinding = new()
        {
            binding = 0,
            descriptorType = VkDescriptorType.UniformBuffer,
            descriptorCount = 1,
            stageFlags = VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment
        };
        VkDescriptorSetLayoutCreateInfo layoutCI = new()
        {
            bindingCount = 1,
            pBindings = &uboBinding
        };
        api.vkCreateDescriptorSetLayout(&layoutCI, null, out _uboSetLayout).CheckResult();
    }

    private void CreateDescriptorPool()
    {
        var api = _ctx.DeviceApi;
        VkDescriptorPoolSize poolSize = new(VkDescriptorType.UniformBuffer, 1);
        VkDescriptorPoolCreateInfo poolCI = new()
        {
            maxSets = 1,
            poolSizeCount = 1,
            pPoolSizes = &poolSize
        };
        api.vkCreateDescriptorPool(&poolCI, null, out _descriptorPool).CheckResult();
    }

    private void AllocateDescriptorSet()
    {
        var api = _ctx.DeviceApi;
        var layout = _uboSetLayout;
        VkDescriptorSetAllocateInfo allocInfo = new()
        {
            descriptorPool = _descriptorPool,
            descriptorSetCount = 1,
            pSetLayouts = &layout
        };
        var set = stackalloc VkDescriptorSet[1];
        api.vkAllocateDescriptorSets(&allocInfo, set).CheckResult();
        _uboSet = set[0];
    }

    private void CreatePipelineLayout()
    {
        var api = _ctx.DeviceApi;

        // Push constants: vec4 color (16 bytes) for line pipeline
        VkPushConstantRange pushRange = new()
        {
            stageFlags = VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment,
            offset = 0,
            size = 16
        };

        var setLayout = _uboSetLayout;
        VkPipelineLayoutCreateInfo plCI = new()
        {
            setLayoutCount = 1,
            pSetLayouts = &setLayout,
            pushConstantRangeCount = 1,
            pPushConstantRanges = &pushRange
        };
        api.vkCreatePipelineLayout(&plCI, null, out _pipelineLayout).CheckResult();
    }

    private void CreateUboBuffer()
    {
        var api = _ctx.DeviceApi;

        VkBufferCreateInfo bufCI = new()
        {
            size = UboSize,
            usage = VkBufferUsageFlags.UniformBuffer,
            sharingMode = VkSharingMode.Exclusive
        };
        api.vkCreateBuffer(&bufCI, null, out _uboBuffer).CheckResult();

        api.vkGetBufferMemoryRequirements(_uboBuffer, out var memReqs);
        VkMemoryAllocateInfo allocInfo = new()
        {
            allocationSize = memReqs.size,
            memoryTypeIndex = _ctx.FindMemoryType(memReqs.memoryTypeBits,
                VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent)
        };
        api.vkAllocateMemory(&allocInfo, null, out _uboMemory).CheckResult();
        api.vkBindBufferMemory(_uboBuffer, _uboMemory, 0);

        void* ptr;
        api.vkMapMemory(_uboMemory, 0, (ulong)UboSize, 0, &ptr);
        _uboMapped = (byte*)ptr;
        new Span<byte>(ptr, UboSize).Clear();

        // Write UBO descriptor
        VkDescriptorBufferInfo bufInfo = new()
        {
            buffer = _uboBuffer,
            offset = 0,
            range = UboSize
        };
        VkWriteDescriptorSet write = new()
        {
            dstSet = _uboSet,
            dstBinding = 0,
            descriptorType = VkDescriptorType.UniformBuffer,
            descriptorCount = 1,
            pBufferInfo = &bufInfo
        };
        api.vkUpdateDescriptorSets(1, &write, 0, null);
    }

    private void CreateQuadBuffer()
    {
        // 6 vertices for 2 triangles forming a [-1,1] quad
        ReadOnlySpan<float> quadVerts =
        [
            -1f, -1f,   1f, -1f,  -1f,  1f,  // triangle 1
             1f, -1f,   1f,  1f,  -1f,  1f,  // triangle 2
        ];
        (_quadBuffer, _quadMemory) = _ctx.CreatePersistentVertexBuffer(quadVerts);
    }

    private void CreatePipelines()
    {
        using var compiler = new Compiler();

        // Inject shared projection code into shaders
        var starVert = StarVertexSource.Replace("PROJECTION_PLACEHOLDER", ProjectionGlsl);
        var lineVert = LineVertexSource.Replace("PROJECTION_PLACEHOLDER", ProjectionGlsl);

        var starVertModule = CompileShaderModule(compiler, starVert, "skymap_star.vert", ShaderKind.VertexShader);
        var starFragModule = CompileShaderModule(compiler, StarFragmentSource, "skymap_star.frag", ShaderKind.FragmentShader);
        var lineVertModule = CompileShaderModule(compiler, lineVert, "skymap_line.vert", ShaderKind.VertexShader);
        var lineFragModule = CompileShaderModule(compiler, LineFragmentSource, "skymap_line.frag", ShaderKind.FragmentShader);

        try
        {
            // ── Star pipeline: instanced quads ──
            // Binding 0: quad corners (per-vertex), stride = 2 floats
            // Binding 1: star data (per-instance), stride = 5 floats (vec3 pos, float mag, float bv)
            var starBindings = stackalloc VkVertexInputBindingDescription[2];
            starBindings[0] = new VkVertexInputBindingDescription
            {
                binding = 0, stride = 2 * sizeof(float), inputRate = VkVertexInputRate.Vertex
            };
            starBindings[1] = new VkVertexInputBindingDescription
            {
                binding = 1, stride = 5 * sizeof(float), inputRate = VkVertexInputRate.Instance
            };

            var starAttrs = stackalloc VkVertexInputAttributeDescription[4];
            starAttrs[0] = new VkVertexInputAttributeDescription              // aCorner
            {
                location = 0, binding = 0, format = VkFormat.R32G32Sfloat, offset = 0
            };
            starAttrs[1] = new VkVertexInputAttributeDescription              // aUnitPos
            {
                location = 1, binding = 1, format = VkFormat.R32G32B32Sfloat, offset = 0
            };
            starAttrs[2] = new VkVertexInputAttributeDescription              // aMagnitude
            {
                location = 2, binding = 1, format = VkFormat.R32Sfloat, offset = 3 * sizeof(float)
            };
            starAttrs[3] = new VkVertexInputAttributeDescription              // aBvColor
            {
                location = 3, binding = 1, format = VkFormat.R32Sfloat, offset = 4 * sizeof(float)
            };

            _starPipeline = CreateGraphicsPipeline(
                starVertModule, starFragModule,
                starBindings, 2, starAttrs, 4,
                VkPrimitiveTopology.TriangleList,
                additive: true);

            // ── Line pipeline ──
            // Binding 0: unit vectors (per-vertex), stride = 3 floats
            VkVertexInputBindingDescription lineBinding = new(3 * sizeof(float));
            VkVertexInputAttributeDescription lineAttr = new(0, VkFormat.R32G32B32Sfloat, 0); // aUnitPos

            _linePipeline = CreateGraphicsPipeline(
                lineVertModule, lineFragModule,
                &lineBinding, 1, &lineAttr, 1,
                VkPrimitiveTopology.LineList,
                additive: false);

            // ── Horizon fill pipeline: full-screen quad, no vertex input ──
            var hzFillVertModule = CompileShaderModule(compiler, HorizonFillVertexSource, "skymap_hzfill.vert", ShaderKind.VertexShader);
            var hzFillFragModule = CompileShaderModule(compiler, HorizonFillFragmentSource, "skymap_hzfill.frag", ShaderKind.FragmentShader);

            _horizonFillPipeline = CreateGraphicsPipeline(
                hzFillVertModule, hzFillFragModule,
                null, 0, null, 0,
                VkPrimitiveTopology.TriangleList,
                additive: false);

            _ctx.DeviceApi.vkDestroyShaderModule(hzFillVertModule);
            _ctx.DeviceApi.vkDestroyShaderModule(hzFillFragModule);
        }
        finally
        {
            var api = _ctx.DeviceApi;
            api.vkDestroyShaderModule(starVertModule);
            api.vkDestroyShaderModule(starFragModule);
            api.vkDestroyShaderModule(lineVertModule);
            api.vkDestroyShaderModule(lineFragModule);
        }
    }

    private VkPipeline CreateGraphicsPipeline(
        VkShaderModule vertModule, VkShaderModule fragModule,
        VkVertexInputBindingDescription* bindings, uint bindingCount,
        VkVertexInputAttributeDescription* attributes, uint attributeCount,
        VkPrimitiveTopology topology, bool additive)
    {
        var api = _ctx.DeviceApi;
        VkUtf8ReadOnlyString entryPoint = "main"u8;

        var stages = stackalloc VkPipelineShaderStageCreateInfo[2];
        stages[0] = new VkPipelineShaderStageCreateInfo
        {
            stage = VkShaderStageFlags.Vertex,
            module = vertModule,
            pName = entryPoint
        };
        stages[1] = new VkPipelineShaderStageCreateInfo
        {
            stage = VkShaderStageFlags.Fragment,
            module = fragModule,
            pName = entryPoint
        };

        VkPipelineVertexInputStateCreateInfo vertexInput = new()
        {
            vertexBindingDescriptionCount = bindingCount,
            pVertexBindingDescriptions = bindings,
            vertexAttributeDescriptionCount = attributeCount,
            pVertexAttributeDescriptions = attributes
        };

        VkPipelineInputAssemblyStateCreateInfo inputAssembly = new(topology);
        VkPipelineViewportStateCreateInfo viewportState = new(1, 1);

        VkPipelineRasterizationStateCreateInfo rasterizer = new()
        {
            polygonMode = VkPolygonMode.Fill,
            lineWidth = 1.0f,
            cullMode = VkCullModeFlags.None,
            frontFace = VkFrontFace.Clockwise
        };

        VkPipelineMultisampleStateCreateInfo multisample = new()
        {
            rasterizationSamples = _ctx.MsaaSamples
        };

        VkPipelineColorBlendAttachmentState blendAttachment;
        if (additive)
        {
            // Additive blending: stars add light (like real sky)
            blendAttachment = new()
            {
                colorWriteMask = VkColorComponentFlags.All,
                blendEnable = true,
                srcColorBlendFactor = VkBlendFactor.SrcAlpha,
                dstColorBlendFactor = VkBlendFactor.One,
                colorBlendOp = VkBlendOp.Add,
                srcAlphaBlendFactor = VkBlendFactor.One,
                dstAlphaBlendFactor = VkBlendFactor.One,
                alphaBlendOp = VkBlendOp.Add
            };
        }
        else
        {
            // Standard alpha blending for lines
            blendAttachment = new()
            {
                colorWriteMask = VkColorComponentFlags.All,
                blendEnable = true,
                srcColorBlendFactor = VkBlendFactor.SrcAlpha,
                dstColorBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
                colorBlendOp = VkBlendOp.Add,
                srcAlphaBlendFactor = VkBlendFactor.One,
                dstAlphaBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
                alphaBlendOp = VkBlendOp.Add
            };
        }

        VkPipelineColorBlendStateCreateInfo colorBlend = new(blendAttachment);

        var dynamicStates = stackalloc VkDynamicState[2];
        dynamicStates[0] = VkDynamicState.Viewport;
        dynamicStates[1] = VkDynamicState.Scissor;
        VkPipelineDynamicStateCreateInfo dynamicState = new()
        {
            dynamicStateCount = 2,
            pDynamicStates = dynamicStates
        };

        VkGraphicsPipelineCreateInfo pipelineCI = new()
        {
            stageCount = 2,
            pStages = stages,
            pVertexInputState = &vertexInput,
            pInputAssemblyState = &inputAssembly,
            pViewportState = &viewportState,
            pRasterizationState = &rasterizer,
            pMultisampleState = &multisample,
            pColorBlendState = &colorBlend,
            pDynamicState = &dynamicState,
            layout = _pipelineLayout,
            renderPass = _ctx.RenderPass,
            subpass = 0
        };

        api.vkCreateGraphicsPipeline(pipelineCI, out var pipeline).CheckResult();
        return pipeline;
    }

    private VkShaderModule CompileShaderModule(Compiler compiler, string source, string fileName, ShaderKind kind)
    {
        var api = _ctx.DeviceApi;
        var options = new CompilerOptions
        {
            TargetEnv = TargetEnvironmentVersion.Vulkan_1_0,
            ShaderStage = kind
        };

        var result = compiler.Compile(source, fileName, options);
        if (result.Status != CompilationStatus.Success)
        {
            throw new InvalidOperationException(
                $"Shader compilation failed ({fileName}): {result.ErrorMessage}");
        }

        var spirv = result.Bytecode;
        fixed (byte* pSpirv = spirv)
        {
            VkShaderModuleCreateInfo createInfo = new()
            {
                codeSize = (nuint)spirv.Length,
                pCode = (uint*)pSpirv
            };
            api.vkCreateShaderModule(&createInfo, null, out var module).CheckResult();
            return module;
        }
    }

    // ────────────────────────────────────────────────── Helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteFloat(byte* dest, int offset, float value)
    {
        Unsafe.WriteUnaligned(dest + offset, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteInt(byte* dest, int offset, int value)
    {
        Unsafe.WriteUnaligned(dest + offset, value);
    }

    // ────────────────────────────────────────────────── Dispose

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var api = _ctx.DeviceApi;
        api.vkDeviceWaitIdle();

        // Pipelines
        if (_starPipeline != VkPipeline.Null) api.vkDestroyPipeline(_starPipeline);
        if (_linePipeline != VkPipeline.Null) api.vkDestroyPipeline(_linePipeline);
        if (_horizonFillPipeline != VkPipeline.Null) api.vkDestroyPipeline(_horizonFillPipeline);

        // Pipeline layout
        if (_pipelineLayout != VkPipelineLayout.Null) api.vkDestroyPipelineLayout(_pipelineLayout);

        // Descriptor pool (frees all sets allocated from it)
        if (_descriptorPool != VkDescriptorPool.Null) api.vkDestroyDescriptorPool(_descriptorPool);

        // Descriptor set layout
        if (_uboSetLayout != VkDescriptorSetLayout.Null) api.vkDestroyDescriptorSetLayout(_uboSetLayout);

        // UBO buffer
        if (_uboBuffer != VkBuffer.Null)
        {
            api.vkUnmapMemory(_uboMemory);
            api.vkDestroyBuffer(_uboBuffer);
            api.vkFreeMemory(_uboMemory);
        }

        // Quad buffer
        DestroyBuffer(_quadBuffer, _quadMemory);

        // Star buffer
        DestroyBuffer(_starBuffer, _starMemory);

        // Constellation figure buffer
        DestroyBuffer(_figureBuffer, _figureMemory);

        // Constellation boundary buffer
        DestroyBuffer(_boundaryBuffer, _boundaryMemory);

        // Grid buffers
        foreach (var (buffer, memory, _) in _gridBuffers)
        {
            DestroyBuffer(buffer, memory);
        }
    }

    private void DestroyBuffer(VkBuffer buffer, VkDeviceMemory memory)
    {
        if (buffer != VkBuffer.Null)
        {
            _ctx.DestroyBuffer(buffer, memory);
        }
    }
}
