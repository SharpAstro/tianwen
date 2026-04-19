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

            // Map ABSOLUTE-window screen pixels to Vulkan NDC: x in [-1,1], y in [-1,1].
            // screenPos is in window-absolute coords because viewportCenter encodes the
            // sky map area's centre in window pixels (offsetX + width/2). The viewport
            // command (vkCmdSetViewport with x=offsetX, y=offsetY, w/h=mapSize) maps
            // NDC=0 to that centre, so we must translate screenPos to viewport-relative
            // coords first by subtracting viewportCenter, then divide by viewport half-size.
            // Without the subtraction, GPU-rendered geometry shifts right/down by offsetX/Y
            // relative to the CPU-drawn labels (which use the same SkyMapProjection but
            // already work in window-absolute coords).
            gl_Position = vec4(
                (screenPos.x - ubo.viewportCenter.x) / (ubo.viewportSize.x * 0.5),
                (screenPos.y - ubo.viewportCenter.y) / (ubo.viewportSize.y * 0.5),
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

            // Same NDC-from-absolute-pixel mapping as the star shader -- see comment there.
            gl_Position = vec4(
                (proj.x - ubo.viewportCenter.x) / (ubo.viewportSize.x * 0.5),
                (proj.y - ubo.viewportCenter.y) / (ubo.viewportSize.y * 0.5),
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

    // ────────────────────────────────────────────────── Overlay ellipse (DSO markers)
    //
    // Instanced quads for DSO ellipse / circle markers. Mirror of the star pipeline:
    // the instance buffer carries J2000 unit vectors, angular sizes (arcmin), and a
    // position angle measured from celestial north. The vertex shader stereographic-
    // projects the center AND a 1 arcmin tip toward north, measures the screen-space
    // delta to recover the rotation the sky projection induces at that point, then
    // adds the user-supplied PA. Result: no CPU projection anywhere on the ellipse
    // path -- hundreds of per-frame CPU projections + atan2 screen-PA calls go away.
    //
    // Stellarium does the equivalent work on the CPU every frame (see
    // Nebula::drawHints -- finite-difference screen-PA via two project() calls +
    // atan2, then CPU-tessellated ellipse loop). The approach below is the natural
    // instanced-rendering generalisation.
    //
    // Per-instance vertex layout (stride = 44 bytes = 11 floats):
    //   offset  0 : vec3  aUnitVec       J2000 unit sphere position
    //   offset 12 : vec2  aSizeArcmin    (semiMajor, semiMinor) in arcminutes
    //   offset 20 : float aPaFromNorth   radians CCW from celestial north
    //   offset 24 : float aThickness     stroke width in pixels
    //   offset 28 : vec4  aColor         RGBA
    private const string OverlayEllipseVertexSource = """
        #version 450

        layout(location = 0) in vec2  aCorner;       // per-vertex unit quad, [-1, 1]
        layout(location = 1) in vec3  aUnitVec;      // per-instance J2000 unit vec
        layout(location = 2) in vec2  aSizeArcmin;   // per-instance semi-axes (arcmin)
        layout(location = 3) in float aPaFromNorth;  // per-instance PA from north (rad)
        layout(location = 4) in float aThickness;    // per-instance stroke (px)
        layout(location = 5) in vec4  aColor;        // per-instance color

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

        layout(location = 0) out vec2  vLocal;    // ellipse-local px (pre-rotation)
        layout(location = 1) out vec2  vSize;     // semi-axes in px (for the SDF)
        layout(location = 2) out float vThickness;
        layout(location = 3) out vec4  vColor;

        PROJECTION_PLACEHOLDER

        void main() {
            // 1. Project center through view matrix + stereographic (shared with star + line shaders).
            vec3 camPos = (ubo.viewMatrix * vec4(aUnitVec, 1.0)).xyz;
            vec3 proj = stereoProject(camPos);
            if (proj.z <= -0.99) {
                // Anti-hemisphere: emit a degenerate vertex so the whole instance is culled.
                gl_Position = vec4(0.0, 0.0, 0.0, 0.0);
                vLocal = vec2(0.0);
                vSize = vec2(1.0);
                vThickness = 0.0;
                vColor = vec4(0.0);
                return;
            }
            vec2 center = proj.xy;

            // 2. Local north tangent from the unit vector alone. Clamped cosDec avoids the
            //    pole singularity where cosDec -> 0 would blow up the division.
            float cosDec = sqrt(max(1e-6, 1.0 - aUnitVec.z * aUnitVec.z));
            vec3 nTangent = vec3(-aUnitVec.z * aUnitVec.x / cosDec,
                                 -aUnitVec.z * aUnitVec.y / cosDec,
                                  cosDec);

            // 3. Project a tip one arcmin north and measure the screen-space angle to it.
            //    Stellarium uses the same finite-difference trick on the CPU.
            float stepRad = 2.908882e-4; // 1 arcmin in radians (1 / (60 * 180 / PI))
            vec3 tipUnit = normalize(aUnitVec + nTangent * stepRad);
            vec3 tipProj = stereoProject((ubo.viewMatrix * vec4(tipUnit, 1.0)).xyz);
            vec2 north2d = tipProj.xy - center;
            // Screen y grows downward, so "up on screen" is -Y; atan(x, -y) gives the
            // angle of the screen-north direction measured CCW from screen up.
            float screenNorthAngle = atan(north2d.x, -north2d.y);
            float totalAngle = screenNorthAngle + aPaFromNorth;

            // 4. Convert arcmin -> pixels using the UBO's pixelsPerRadian.
            float arcminToPx = ubo.pixelsPerRadian * 0.00029088820866;  // pi / (180 * 60)
            vec2 sizePx = aSizeArcmin * arcminToPx;

            // 5. Pad quad for the ring SDF antialias + rotate + expand.
            float pad = max(aThickness * 0.75 + 1.0, 1.5);
            vec2 local = aCorner * (sizePx + vec2(pad));
            float cs = cos(totalAngle);
            float sn = sin(totalAngle);
            vec2 rotated = vec2(local.x * cs - local.y * sn,
                                local.x * sn + local.y * cs);
            vec2 screenPos = center + rotated;

            // 6. Same NDC-from-absolute-pixel mapping as the star / line shaders.
            gl_Position = vec4(
                (screenPos.x - ubo.viewportCenter.x) / (ubo.viewportSize.x * 0.5),
                (screenPos.y - ubo.viewportCenter.y) / (ubo.viewportSize.y * 0.5),
                0.0, 1.0);

            vLocal = local;
            vSize = sizePx;
            vThickness = aThickness;
            vColor = aColor;
        }
        """;

    private const string OverlayEllipseFragmentSource = """
        #version 450

        layout(location = 0) in vec2  vLocal;
        layout(location = 1) in vec2  vSize;
        layout(location = 2) in float vThickness;
        layout(location = 3) in vec4  vColor;

        layout(location = 0) out vec4 FragColor;

        void main() {
            // Axis-aligned ellipse SDF: (x/a)^2 + (y/b)^2 = 1 on the boundary.
            // Scale by the mean semi-axis to convert the normalised distance back
            // to an approximate pixel distance from the ring -- good enough for
            // typical DSO aspect ratios (eccentric shapes get a slightly uneven
            // stroke, still visually clean at overlay marker sizes).
            vec2 s = max(vSize, vec2(0.5));
            vec2 n = vLocal / s;
            float normDist = sqrt(dot(n, n));
            float avgR = (s.x + s.y) * 0.5;
            float pixelDist = abs(normDist - 1.0) * avgR;

            float halfT = max(vThickness * 0.5, 0.5);
            // Antialiased ring: full alpha inside halfT, fade to 0 over 1 px.
            float alpha = 1.0 - smoothstep(halfT, halfT + 1.0, pixelDist);
            if (alpha < 0.01) discard;

            FragColor = vec4(vColor.rgb * alpha, vColor.a * alpha);
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

    // ────────────────────────────────────────────────── Milky Way fragment shader
    //
    // Full-screen quad: inverse stereographic -> J2000 unit vector -> equirectangular UV -> texture sample.
    // Vertex shader reuses HorizonFillVertexSource. Push constant controls brightness (sun altitude fade).

    private const string MilkyWayFragmentSource = """
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

        layout(set = 1, binding = 0) uniform sampler2D milkyWayTex;

        layout(push_constant) uniform PushConstants {
            float alpha;
        } pc;

        const float PI    = 3.14159265358979;
        const float TWO_PI = 6.28318530717959;

        void main() {
            // Inverse stereographic projection: screen pixel -> camera-space direction
            float x = (vScreenPos.x - ubo.viewportCenter.x) / ubo.pixelsPerRadian;
            float y = -(vScreenPos.y - ubo.viewportCenter.y) / ubo.pixelsPerRadian;

            float rho = length(vec2(x, y));
            vec3 camDir;
            if (rho < 0.00001) {
                camDir = vec3(0.0, 0.0, -1.0);
            } else {
                float c = 2.0 * atan(rho * 0.5);
                float sinC = sin(c);
                float cosC = cos(c);
                camDir = vec3(sinC * x / rho, sinC * y / rho, -cosC);
            }

            // Rotate back to J2000 (view matrix is orthogonal, inverse = transpose)
            vec3 j2000 = transpose(mat3(ubo.viewMatrix)) * camDir;

            // J2000 unit vector -> equirectangular UV
            float ra = atan(j2000.y, j2000.x);       // [-PI, PI]
            float u = ra / TWO_PI + 0.5;              // [0, 1]
            float dec = asin(clamp(j2000.z, -1.0, 1.0)); // [-PI/2, PI/2]
            float v = 0.5 - dec / PI;                 // [0, 1], north at top

            vec4 mw = texture(milkyWayTex, vec2(u, v));
            FragColor = vec4(mw.rgb, mw.a * pc.alpha);
        }
        """;

    // ────────────────────────────────────────────────── Fields

    private readonly VulkanContext _ctx;

    // Descriptor set layout + pool + sets for the UBO. One set per frame-in-flight
    // so the GPU reads from a stable UBO copy while the CPU writes the next frame's
    // copy -- eliminates the 1-frame label desync that was visible during fast pans.
    private VkDescriptorSetLayout _uboSetLayout;
    private VkDescriptorPool _descriptorPool;
    private readonly VkDescriptorSet[] _uboSets = new VkDescriptorSet[MaxFramesInFlight];

    // Pipeline layout (UBO at set 0, push constants for line color)
    private VkPipelineLayout _pipelineLayout;

    // Sub-pipelines
    private VkPipeline _starPipeline;
    private VkPipeline _linePipeline;
    private VkPipeline _horizonFillPipeline;
    private VkPipeline _milkyWayPipeline;
    private VkPipeline _overlayEllipsePipeline;

    // Milky Way texture (loaded from disk, may be null if file not present).
    // Uses VkTexture which owns its own descriptor set from the global pool.
    private VkTexture? _milkyWayTexture;
    private VkPipelineLayout _milkyWayPipelineLayout;

    // Per-frame UBO buffers (persistently mapped). Mirrors the vertex ring buffer
    // pattern in VulkanContext -- each frame-in-flight has its own copy so the GPU
    // never reads from a UBO that the CPU is currently overwriting.
    private const int MaxFramesInFlight = 2;
    private readonly VkBuffer[] _uboBuffers = new VkBuffer[MaxFramesInFlight];
    private readonly VkDeviceMemory[] _uboMemories = new VkDeviceMemory[MaxFramesInFlight];
    private readonly byte*[] _uboMapped = new byte*[MaxFramesInFlight];
    private int _currentUboFrame;

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

    // Ecliptic great circle (great circle of the Sun's apparent path) -- single
    // persistent line buffer, never changes since obliquity is a J2000 constant.
    private VkBuffer _eclipticBuffer;
    private VkDeviceMemory _eclipticMemory;
    private uint _eclipticVertexCount;

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
        BuildEclipticBuffer();

        _geometryBuilt = true;
    }

    /// <summary>
    /// Loads a Milky Way equirectangular texture from raw BGRA bytes.
    /// Call once from the render thread after decompressing the texture file.
    /// </summary>
    public void LoadMilkyWayTexture(ReadOnlySpan<byte> bgraData, int width, int height)
    {
        _milkyWayTexture?.Dispose();
        _milkyWayTexture = VkTexture.CreateFromBgra(_ctx, bgraData, width, height);
    }

    /// <summary>True when a Milky Way texture has been loaded.</summary>
    public bool HasMilkyWayTexture => _milkyWayTexture is not null;

    /// <summary>
    /// Build the ecliptic great circle (the Sun's annual path on the celestial sphere).
    /// Tessellated as a closed line strip in J2000 unit vectors. Inclination from the
    /// celestial equator is the obliquity of the ecliptic at J2000.0.
    /// </summary>
    private void BuildEclipticBuffer()
    {
        // Mean obliquity of the ecliptic at J2000.0 (IAU 2006 / SOFA).
        const double obliquityJ2000Deg = 23.4392911;
        var (sinE, cosE) = Math.SinCos(double.DegreesToRadians(obliquityJ2000Deg));

        const int steps = 360;
        var floats = new List<float>(steps * 6);
        float prevX = 0, prevY = 0, prevZ = 0;
        for (var i = 0; i <= steps; i++)
        {
            // lambda = ecliptic longitude in radians, sweeping the full circle.
            var lambda = i * (2.0 * Math.PI / steps);
            var (sinL, cosL) = Math.SinCos(lambda);
            // J2000 unit vector for ecliptic-longitude lambda, latitude 0.
            // x = cos(lambda), y = sin(lambda)*cos(eps), z = sin(lambda)*sin(eps).
            var x = (float)cosL;
            var y = (float)(sinL * cosE);
            var z = (float)(sinL * sinE);

            if (i > 0)
            {
                floats.Add(prevX); floats.Add(prevY); floats.Add(prevZ);
                floats.Add(x); floats.Add(y); floats.Add(z);
            }
            prevX = x; prevY = y; prevZ = z;
        }

        _eclipticVertexCount = (uint)(floats.Count / 3);
        if (_eclipticVertexCount > 0)
        {
            (_eclipticBuffer, _eclipticMemory) = _ctx.CreatePersistentVertexBuffer(
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(floats));
        }
    }

    /// <summary>
    /// Update the UBO with current view parameters. Call once per frame before drawing.
    /// Writes to the frame-slot selected by <paramref name="frameIndex"/> so each
    /// in-flight frame gets its own stable UBO copy (see <see cref="MaxFramesInFlight"/>).
    /// </summary>
    public void UpdateUbo(
        SkyMapState state,
        float viewportWidth, float viewportHeight,
        float offsetX, float offsetY,
        Lib.Astrometry.SOFA.SiteContext site,
        int frameIndex)
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

        _currentUboFrame = frameIndex;
        var p = _uboMapped[frameIndex];

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
        float milkyWayAlpha,
        // Dynamic line buffers — written to ring buffer by caller
        (VkBuffer Buffer, uint ByteOffset, uint VertexCount) horizon,
        (VkBuffer Buffer, uint ByteOffset, uint VertexCount) meridian,
        (VkBuffer Buffer, uint ByteOffset, uint VertexCount) altAzGrid,
        (VkBuffer Buffer, uint ByteOffset, uint VertexCount) fovOutlines = default)
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

        // Bind the current frame's UBO descriptor set (shared by all sub-pipelines).
        // _currentUboFrame was set by the most recent UpdateUbo call.
        var uboSet = _uboSets[_currentUboFrame];
        api.vkCmdBindDescriptorSets(cmd, VkPipelineBindPoint.Graphics, _pipelineLayout,
            0, 1, &uboSet, 0, null);

        // ── Milky Way background (drawn first, behind everything) ──
        if (state.ShowMilkyWay && milkyWayAlpha > 0.005f
            && _milkyWayTexture is not null && _milkyWayPipeline != VkPipeline.Null)
        {
            api.vkCmdBindPipeline(cmd, VkPipelineBindPoint.Graphics, _milkyWayPipeline);
            // Bind set 0 (UBO) with the Milky Way pipeline layout
            api.vkCmdBindDescriptorSets(cmd, VkPipelineBindPoint.Graphics,
                _milkyWayPipelineLayout, 0, 1, &uboSet, 0, null);
            // Bind set 1 (texture sampler from VkTexture)
            var mwSet = _milkyWayTexture.DescriptorSet;
            api.vkCmdBindDescriptorSets(cmd, VkPipelineBindPoint.Graphics,
                _milkyWayPipelineLayout, 1, 1, &mwSet, 0, null);
            // Push alpha constant
            api.vkCmdPushConstants(cmd, _milkyWayPipelineLayout,
                VkShaderStageFlags.Fragment, 0, 4, &milkyWayAlpha);
            api.vkCmdDraw(cmd, 6, 1, 0, 0);
        }

        // ── Horizon fill ──
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

        // Ecliptic -- Sun's annual path on the celestial sphere. Always drawn in
        // warm yellow so it reads as "Sun-related" against the cool blue grid and
        // constellation figures. Built once at startup as a great circle inclined
        // by the J2000 obliquity (~23.44 deg) -- planets stay within ~7 deg of it,
        // so it doubles as a "where to look for ecliptic objects" guide.
        if (_eclipticVertexCount > 0)
        {
            PushLineColor(cmd, 0xE0, 0xC0, 0x40, 0xB0); // warm yellow
            DrawLineBuffer(cmd, _eclipticBuffer, 0, _eclipticVertexCount);
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

        // Sensor FOV rectangle + mosaic panel outlines (on top of all line geometry,
        // below stars so the reticle and star dots remain visible)
        if (fovOutlines.VertexCount > 0)
        {
            PushLineColor(cmd, 0xDD, 0x33, 0x33, 0xDD); // Stellarium-style red
            DrawLineBuffer(cmd, fovOutlines.Buffer, fovOutlines.ByteOffset, fovOutlines.VertexCount);
        }

        // ── Stars ──
        // Stars are sorted by magnitude (brightest first). Only instance the prefix
        // that passes the current EffectiveMagnitudeLimit — at full zoom-out (FOV 180°,
        // mag ~6.5) we draw ~9k instances instead of all ~118k.
        var visibleStars = _magBinCounts.Length > 0
            ? GetVisibleStarCount(state.EffectiveMagnitudeLimit)
            : _starCount;
        if (visibleStars > 0)
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

            // 6 vertices per quad, only the visible prefix of the sorted star buffer
            api.vkCmdDraw(cmd, 6, visibleStars, 0, 0);
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

    /// <summary>
    /// Draws <paramref name="instanceCount"/> DSO overlay ellipses from an instance buffer
    /// that the caller has populated via <c>ctx.WriteVertices</c>. Each instance is 10
    /// floats (see <c>OverlayEllipseVertexSource</c>). Binds its own viewport/scissor
    /// to the sky-map content rect so the NDC math in the vertex shader lines up with
    /// the UBO's viewportCenter / viewportSize.
    /// </summary>
    /// <remarks>
    /// Callers should restore the full-window viewport after this draw if subsequent
    /// rendering (e.g. text labels through <c>VkRenderer</c>) expects the default.
    /// </remarks>
    public void DrawOverlayEllipses(
        VkCommandBuffer cmd,
        float offsetX, float offsetY, float viewportWidth, float viewportHeight,
        VkBuffer instanceBuffer, uint instanceByteOffset, uint instanceCount)
    {
        if (instanceCount == 0 || _overlayEllipsePipeline == VkPipeline.Null)
        {
            return;
        }

        var api = _ctx.DeviceApi;

        VkViewport vp = new()
        {
            x = offsetX, y = offsetY,
            width = viewportWidth, height = viewportHeight,
            minDepth = 0f, maxDepth = 1f
        };
        VkRect2D scissor = new(new VkOffset2D((int)offsetX, (int)offsetY),
            new VkExtent2D((uint)viewportWidth, (uint)viewportHeight));
        api.vkCmdSetViewport(cmd, 0, 1, &vp);
        api.vkCmdSetScissor(cmd, 0, 1, &scissor);

        api.vkCmdBindPipeline(cmd, VkPipelineBindPoint.Graphics, _overlayEllipsePipeline);

        var uboSet = _uboSets[_currentUboFrame];
        api.vkCmdBindDescriptorSets(cmd, VkPipelineBindPoint.Graphics, _pipelineLayout,
            0, 1, &uboSet, 0, null);

        var buffers = stackalloc VkBuffer[2];
        buffers[0] = _quadBuffer;
        buffers[1] = instanceBuffer;
        var offsets = stackalloc ulong[2];
        offsets[0] = 0;
        offsets[1] = instanceByteOffset;
        api.vkCmdBindVertexBuffers(cmd, 0, 2, buffers, offsets);

        api.vkCmdDraw(cmd, 6, instanceCount, 0, 0);
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

        // Sort by magnitude (brightest first) so we can limit instance count at draw
        // time based on EffectiveMagnitudeLimit — the GPU only processes the prefix of
        // stars that pass the current mag threshold, instead of all 118k every frame.
        SortStarsByMagnitude(floats, floatsPerStar);
        BuildMagnitudeLookup(floats, floatsPerStar);

        (_starBuffer, _starMemory) = _ctx.CreatePersistentVertexBuffer(
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(floats));
    }

    /// <summary>
    /// Sorts the flat star float array by the magnitude field (index 3 of each 5-float record).
    /// Uses Array.Sort on a temporary index array to avoid copying 20-byte records.
    /// </summary>
    private static void SortStarsByMagnitude(List<float> floats, int floatsPerStar)
    {
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(floats);
        var count = span.Length / floatsPerStar;
        if (count <= 1) return;

        // Build index + magnitude arrays for sorting
        var indices = new int[count];
        var mags = new float[count];
        for (var i = 0; i < count; i++)
        {
            indices[i] = i;
            mags[i] = span[i * floatsPerStar + 3]; // vMag field
        }

        Array.Sort(mags, indices);

        // Reorder the float data according to sorted indices
        var sorted = new float[span.Length];
        for (var i = 0; i < count; i++)
        {
            var srcOff = indices[i] * floatsPerStar;
            var dstOff = i * floatsPerStar;
            span.Slice(srcOff, floatsPerStar).CopyTo(sorted.AsSpan(dstOff, floatsPerStar));
        }
        sorted.AsSpan().CopyTo(span);
    }

    /// <summary>
    /// Pre-computed magnitude → instance count lookup. For a given mag limit, the number
    /// of stars to draw is <c>_magBinCounts[bin]</c> where <c>bin = (int)(mag * 2)</c>.
    /// Bins cover mag 0..15 in 0.5-mag steps (30 entries).
    /// </summary>
    private uint[] _magBinCounts = [];

    private void BuildMagnitudeLookup(List<float> sortedFloats, int floatsPerStar)
    {
        const int bins = 30; // mag 0..15 in 0.5 steps
        _magBinCounts = new uint[bins];
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(sortedFloats);
        var count = (uint)(span.Length / floatsPerStar);

        uint idx = 0;
        for (var bin = 0; bin < bins; bin++)
        {
            var magThreshold = (bin + 1) * 0.5f;
            while (idx < count && span[(int)(idx * floatsPerStar + 3)] <= magThreshold)
            {
                idx++;
            }
            _magBinCounts[bin] = idx;
        }
    }

    /// <summary>
    /// Returns the number of star instances to draw for the given magnitude limit.
    /// Stars are sorted brightest-first, so we just return the prefix count.
    /// </summary>
    private uint GetVisibleStarCount(float magLimit)
    {
        var bin = Math.Clamp((int)(magLimit * 2) - 1, 0, _magBinCounts.Length - 1);
        return _magBinCounts[bin];
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
        SortStarsByMagnitude(floats, 5);
        BuildMagnitudeLookup(floats, 5);
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
        // Pool size = MaxFramesInFlight uniform-buffer descriptors, one per frame slot.
        VkDescriptorPoolSize poolSize = new(VkDescriptorType.UniformBuffer, (uint)MaxFramesInFlight);
        VkDescriptorPoolCreateInfo poolCI = new()
        {
            maxSets = (uint)MaxFramesInFlight,
            poolSizeCount = 1,
            pPoolSizes = &poolSize
        };
        api.vkCreateDescriptorPool(&poolCI, null, out _descriptorPool).CheckResult();
    }

    private void AllocateDescriptorSet()
    {
        var api = _ctx.DeviceApi;
        // Allocate one descriptor set per frame-in-flight. Each points at its own
        // UBO buffer copy, so the GPU never reads from a UBO being overwritten.
        var layouts = stackalloc VkDescriptorSetLayout[MaxFramesInFlight];
        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            layouts[i] = _uboSetLayout;
        }
        VkDescriptorSetAllocateInfo allocInfo = new()
        {
            descriptorPool = _descriptorPool,
            descriptorSetCount = (uint)MaxFramesInFlight,
            pSetLayouts = layouts
        };
        var sets = stackalloc VkDescriptorSet[MaxFramesInFlight];
        api.vkAllocateDescriptorSets(&allocInfo, sets).CheckResult();
        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            _uboSets[i] = sets[i];
        }
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

        // Milky Way pipeline layout: set 0 = UBO, set 1 = texture sampler (global layout).
        // Push constant: float alpha (4 bytes, fragment only).
        VkPushConstantRange mwPushRange = new()
        {
            stageFlags = VkShaderStageFlags.Fragment,
            offset = 0,
            size = 4
        };

        var setLayouts = stackalloc VkDescriptorSetLayout[2];
        setLayouts[0] = _uboSetLayout;
        setLayouts[1] = _ctx.DescriptorSetLayout; // global CombinedImageSampler layout
        VkPipelineLayoutCreateInfo mwPlCI = new()
        {
            setLayoutCount = 2,
            pSetLayouts = setLayouts,
            pushConstantRangeCount = 1,
            pPushConstantRanges = &mwPushRange
        };
        api.vkCreatePipelineLayout(&mwPlCI, null, out _milkyWayPipelineLayout).CheckResult();
    }

    private void CreateUboBuffer()
    {
        var api = _ctx.DeviceApi;

        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            VkBufferCreateInfo bufCI = new()
            {
                size = UboSize,
                usage = VkBufferUsageFlags.UniformBuffer,
                sharingMode = VkSharingMode.Exclusive
            };
            api.vkCreateBuffer(&bufCI, null, out _uboBuffers[i]).CheckResult();

            api.vkGetBufferMemoryRequirements(_uboBuffers[i], out var memReqs);
            VkMemoryAllocateInfo allocInfo = new()
            {
                allocationSize = memReqs.size,
                memoryTypeIndex = _ctx.FindMemoryType(memReqs.memoryTypeBits,
                    VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent)
            };
            api.vkAllocateMemory(&allocInfo, null, out _uboMemories[i]).CheckResult();
            api.vkBindBufferMemory(_uboBuffers[i], _uboMemories[i], 0);

            void* ptr;
            api.vkMapMemory(_uboMemories[i], 0, (ulong)UboSize, 0, &ptr);
            _uboMapped[i] = (byte*)ptr;
            new Span<byte>(ptr, UboSize).Clear();

            // Point this frame's descriptor set at this frame's UBO buffer
            VkDescriptorBufferInfo bufInfo = new()
            {
                buffer = _uboBuffers[i],
                offset = 0,
                range = UboSize
            };
            VkWriteDescriptorSet write = new()
            {
                dstSet = _uboSets[i],
                dstBinding = 0,
                descriptorType = VkDescriptorType.UniformBuffer,
                descriptorCount = 1,
                pBufferInfo = &bufInfo
            };
            api.vkUpdateDescriptorSets(1, &write, 0, null);
        }
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

            // ── Milky Way pipeline: full-screen quad + texture, own layout ──
            var mwVertModule = CompileShaderModule(compiler, HorizonFillVertexSource, "skymap_mw.vert", ShaderKind.VertexShader);
            var mwFragModule = CompileShaderModule(compiler, MilkyWayFragmentSource, "skymap_mw.frag", ShaderKind.FragmentShader);

            _milkyWayPipeline = CreateGraphicsPipeline(
                mwVertModule, mwFragModule,
                null, 0, null, 0,
                VkPrimitiveTopology.TriangleList,
                additive: true,
                layoutOverride: _milkyWayPipelineLayout);

            _ctx.DeviceApi.vkDestroyShaderModule(mwVertModule);
            _ctx.DeviceApi.vkDestroyShaderModule(mwFragModule);

            // ── Overlay ellipse pipeline: instanced quads, GPU projection + ring SDF ──
            // Binding 0: quad corners (per-vertex, same buffer as star pipeline), stride 8
            // Binding 1: ellipse instance data (per-instance), stride 44 bytes (11 floats)
            var ovVert = OverlayEllipseVertexSource.Replace("PROJECTION_PLACEHOLDER", ProjectionGlsl);
            var ovVertModule = CompileShaderModule(compiler, ovVert, "skymap_overlay.vert", ShaderKind.VertexShader);
            var ovFragModule = CompileShaderModule(compiler, OverlayEllipseFragmentSource, "skymap_overlay.frag", ShaderKind.FragmentShader);

            var ovBindings = stackalloc VkVertexInputBindingDescription[2];
            ovBindings[0] = new VkVertexInputBindingDescription
            {
                binding = 0, stride = 2 * sizeof(float), inputRate = VkVertexInputRate.Vertex
            };
            ovBindings[1] = new VkVertexInputBindingDescription
            {
                binding = 1, stride = 11 * sizeof(float), inputRate = VkVertexInputRate.Instance
            };

            var ovAttrs = stackalloc VkVertexInputAttributeDescription[6];
            ovAttrs[0] = new VkVertexInputAttributeDescription // aCorner
            {
                location = 0, binding = 0, format = VkFormat.R32G32Sfloat, offset = 0
            };
            ovAttrs[1] = new VkVertexInputAttributeDescription // aUnitVec
            {
                location = 1, binding = 1, format = VkFormat.R32G32B32Sfloat, offset = 0
            };
            ovAttrs[2] = new VkVertexInputAttributeDescription // aSizeArcmin
            {
                location = 2, binding = 1, format = VkFormat.R32G32Sfloat, offset = 3 * sizeof(float)
            };
            ovAttrs[3] = new VkVertexInputAttributeDescription // aPaFromNorth
            {
                location = 3, binding = 1, format = VkFormat.R32Sfloat, offset = 5 * sizeof(float)
            };
            ovAttrs[4] = new VkVertexInputAttributeDescription // aThickness
            {
                location = 4, binding = 1, format = VkFormat.R32Sfloat, offset = 6 * sizeof(float)
            };
            ovAttrs[5] = new VkVertexInputAttributeDescription // aColor
            {
                location = 5, binding = 1, format = VkFormat.R32G32B32A32Sfloat, offset = 7 * sizeof(float)
            };

            _overlayEllipsePipeline = CreateGraphicsPipeline(
                ovVertModule, ovFragModule,
                ovBindings, 2, ovAttrs, 6,
                VkPrimitiveTopology.TriangleList,
                additive: false);

            _ctx.DeviceApi.vkDestroyShaderModule(ovVertModule);
            _ctx.DeviceApi.vkDestroyShaderModule(ovFragModule);
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
        VkPrimitiveTopology topology, bool additive,
        VkPipelineLayout layoutOverride = default)
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
            layout = layoutOverride != VkPipelineLayout.Null ? layoutOverride : _pipelineLayout,
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

        // Milky Way resources
        _milkyWayTexture?.Dispose();
        if (_milkyWayPipeline != VkPipeline.Null) api.vkDestroyPipeline(_milkyWayPipeline);
        if (_milkyWayPipelineLayout != VkPipelineLayout.Null) api.vkDestroyPipelineLayout(_milkyWayPipelineLayout);

        // Pipelines
        if (_starPipeline != VkPipeline.Null) api.vkDestroyPipeline(_starPipeline);
        if (_linePipeline != VkPipeline.Null) api.vkDestroyPipeline(_linePipeline);
        if (_horizonFillPipeline != VkPipeline.Null) api.vkDestroyPipeline(_horizonFillPipeline);
        if (_overlayEllipsePipeline != VkPipeline.Null) api.vkDestroyPipeline(_overlayEllipsePipeline);

        // Pipeline layout
        if (_pipelineLayout != VkPipelineLayout.Null) api.vkDestroyPipelineLayout(_pipelineLayout);

        // Descriptor pool (frees all sets allocated from it)
        if (_descriptorPool != VkDescriptorPool.Null) api.vkDestroyDescriptorPool(_descriptorPool);

        // Descriptor set layout
        if (_uboSetLayout != VkDescriptorSetLayout.Null) api.vkDestroyDescriptorSetLayout(_uboSetLayout);

        // Per-frame UBO buffers
        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            if (_uboBuffers[i] != VkBuffer.Null)
            {
                api.vkUnmapMemory(_uboMemories[i]);
                api.vkDestroyBuffer(_uboBuffers[i]);
                api.vkFreeMemory(_uboMemories[i]);
            }
        }

        // Quad buffer
        DestroyBuffer(_quadBuffer, _quadMemory);

        // Star buffer
        DestroyBuffer(_starBuffer, _starMemory);

        // Constellation figure buffer
        DestroyBuffer(_figureBuffer, _figureMemory);

        // Constellation boundary buffer
        DestroyBuffer(_boundaryBuffer, _boundaryMemory);

        // Ecliptic great-circle buffer
        DestroyBuffer(_eclipticBuffer, _eclipticMemory);

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
