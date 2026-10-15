using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DIR.Lib;
using Microsoft.Extensions.Logging;
using SharpAstro.Png;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging.Planetary;
using TianWen.UI.Abstractions;
using WebGl.Renderer;

namespace TianWen.UI.Web.SkyMap
{
    /// <summary>
    /// The orrery's bodies ray-traced in the browser (docs/plans/atlas-planet-detail.md, A5, "The bodies on the GPU"): the WebGL 2 twin of
    /// the desktop's <c>VkPlanetBodies</c>, one instanced draw a body, its frame and lighting one instance in a buffer of the body's own
    /// (<see cref="PlanetBodyInstances{TBuffer}"/>, the push constants' very layout), its colour map a texture loaded ONCE per body and texture
    /// version. Nothing is built per frame, so Play costs a draw a body and no more.
    /// </summary>
    /// <remarks>
    /// The fragment shader is <c>planet_body.frag</c>'s trace, the SAME TEXT between its two "The trace" markers (PlanetBodyGpuTests checks
    /// it): only the declarations above it differ. The renderer takes a texture only from a URL, so a map goes over once as an 8-bit PNG
    /// data URL (<see cref="PlanetBodyMap.Rgba8"/>, within half a level of the desktop's float texels); until it has loaded the draw answers
    /// false and the caller draws the flat dot, and its landing asks the page for a frame.
    /// </remarks>
    internal sealed class WebGlPlanetBodies(WebGlRenderer renderer, Action requestRedraw, ILogger? logger) : IDisposable
    {
        // The body's Body block arrives as ONE instance of seven vec4 attributes (PlanetBodyInstances, PlanetBodyMap.WriteBlock's
        // layout) and is handed to the fragment stage flat. Not a uniform block: WebGl.Renderer sets a block at once and replays the
        // draws at Present, so every body read the last body's block (review round 1 of PR #1589).
        private const string VertexSource = """
            #version 300 es
            precision highp float;
            layout(location = 0) in vec2 aCorner;
            layout(location = 1) in vec4 aDisc;
            layout(location = 2) in vec4 aFrame;
            layout(location = 3) in vec4 aRight;
            layout(location = 4) in vec4 aToward;
            layout(location = 5) in vec4 aSun;
            layout(location = 6) in vec4 aRingAlbedo;
            layout(location = 7) in vec4 aTint;
            flat out vec4 vDisc;
            flat out vec4 vFrame;
            flat out vec4 vRight;
            flat out vec4 vToward;
            flat out vec4 vSun;
            flat out vec4 vRingAlbedo;
            flat out vec4 vTint;
            void main() {
                vDisc = aDisc;
                vFrame = aFrame;
                vRight = aRight;
                vToward = aToward;
                vSun = aSun;
                vRingAlbedo = aRingAlbedo;
                vTint = aTint;
                float half_ = aDisc.z * aDisc.w + 1.5;
                vec2 px = aDisc.xy + (aCorner * 2.0 - 1.0) * half_;
                // Screen px (y down) to clip space (y up).
                vec2 ndc = px / aFrame.xy * 2.0 - 1.0;
                gl_Position = vec4(ndc.x, -ndc.y, 0.0, 1.0);
            }
            """;

        private const string FragmentSource = """
            #version 300 es
            precision highp float;
            precision highp int;

            // planet_body.frag's Body block, from this body's instance attributes (PlanetBodyMap.WriteBlock writes both), so the
            // trace below reads pc.disc and the rest as on the desktop.
            flat in vec4 vDisc;
            flat in vec4 vFrame;
            flat in vec4 vRight;
            flat in vec4 vToward;
            flat in vec4 vSun;
            flat in vec4 vRingAlbedo;
            flat in vec4 vTint;
            struct Body {
                vec4 disc;
                vec4 frame;
                vec4 right;
                vec4 toward;
                vec4 sun;
                vec4 ringAlbedo;
                vec4 tint;
            };
            #define pc Body(vDisc, vFrame, vRight, vToward, vSun, vRingAlbedo, vTint)

            uniform highp sampler2D uTexture;

            out vec4 FragColor;

            // The pixel's centre on screen, px, y down: WebGL's framebuffer is y UP, the drawing buffer the renderer's own size.
            vec2 screenPixel() {
                return vec2(gl_FragCoord.x, pc.frame.y - gl_FragCoord.y);
            }

            // ---- The trace (the same text in WebGlPlanetBodies.cs) ----

            // SaturnRings.Main, Saturn's equatorial radius 60268 km: inner and outer edge (km), the lit face's level over the map's mean
            // albedo, the normal optical depth. PlanetBodyGpuTests holds this table to SaturnRings.Main.
            const int RingCount = 4;
            const vec4 Rings[RingCount] = vec4[RingCount](
                vec4(74658.0, 92000.0, 0.12, 0.1),
                vec4(92000.0, 117580.0, 0.85, 2.0),
                vec4(117580.0, 122170.0, 0.1, 0.1),
                vec4(122170.0, 136775.0, 0.6, 0.5));
            const float SaturnEquatorialKm = 60268.0;

            // The ring at `radius` equatorial radii: (level, optical depth), or a negative level in a gap.
            vec2 ringAt(float radius) {
                float km = radius * SaturnEquatorialKm;
                for (int i = 0; i < RingCount; i++) {
                    if (km >= Rings[i].x && km < Rings[i].y) {
                        return Rings[i].zw;
                    }
                }
                return vec2(-1.0, 0.0);
            }

            // A ring's transmission at a sine of elevation over the ring plane (SaturnRing.Transmission).
            float transmission(float tau, float sinElevation) {
                return sinElevation == 0.0 ? 0.0 : exp(-tau / abs(sinElevation));
            }

            // PlanetMap.Sample, per plane at once: bilinear between sample centres, wrapping in longitude, held at the poles' rows.
            vec3 sampleMap(float latitudeDeg, float westDeg) {
                ivec2 size = textureSize(uTexture, 0);
                float west = mod(westDeg, 360.0);
                float fx = (360.0 - west) * float(size.x) / 360.0 - 0.5;
                float fy = clamp((90.0 - latitudeDeg) * float(size.y) / 180.0 - 0.5, 0.0, float(size.y - 1));
                float x0f = floor(fx);
                float tx = fx - x0f;
                int x0 = int(mod(x0f, float(size.x)));
                int x1 = (x0 + 1) % size.x;
                int y0 = min(int(floor(fy)), size.y - 2);
                float ty = fy - float(y0);
                vec3 top = mix(texelFetch(uTexture, ivec2(x0, y0), 0).rgb, texelFetch(uTexture, ivec2(x1, y0), 0).rgb, tx);
                vec3 bottom = mix(texelFetch(uTexture, ivec2(x0, y0 + 1), 0).rgb, texelFetch(uTexture, ivec2(x1, y0 + 1), 0).rgb, tx);
                return mix(top, bottom, ty);
            }

            // Whether the point p of the ring plane, outside the globe, lies in the globe's shadow: the ray from it toward the Sun meets the
            // spheroid at some s > 0 (PlanetaryProjection.InGlobeShadow).
            bool inGlobeShadow(vec2 p, vec3 s, float q2Inverse) {
                float a = s.x * s.x + s.y * s.y + s.z * s.z * q2Inverse;
                float b = 2.0 * (p.x * s.x + p.y * s.y);
                float c = p.x * p.x + p.y * p.y - 1.0;
                float d = b * b - 4.0 * a * c;
                return d >= 0.0 && (-b + sqrt(d)) > 0.0;
            }

            void main() {
                vec3 right = pc.right.xyz;
                vec3 toward = pc.toward.xyz;
                vec3 up = cross(toward, right);
                vec3 s = pc.sun.xyz;
                float q = pc.frame.z;
                float q2Inverse = 1.0 / (q * q);
                float k = pc.frame.w;
                bool hasRings = pc.right.w > 0.5;
                // A ray starts in front of everything the body has, and runs away from the camera.
                float start = pc.disc.w + 1.0;
                vec3 dir = -toward;
                vec3 dirScaled = vec3(dir.xy, dir.z / q);
                float a = dot(dirScaled, dirScaled);
                vec2 centre = screenPixel();

                vec3 colour = vec3(0.0);
                float coverage = 0.0;
                for (int j = 0; j < 3; j++) {
                    for (int i = 0; i < 3; i++) {
                        // The rays sit at the centres of the pixel's 3 x 3 sub-pixels.
                        vec2 p = centre - 0.5 + (vec2(float(i), float(j)) + 0.5) / 3.0;
                        vec2 disc = (p - pc.disc.xy) / pc.disc.z;
                        vec3 origin = disc.x * right - disc.y * up + start * toward;

                        // The spheroid x^2 + y^2 + z^2 / q^2 = 1; the nearer meeting is the smaller t.
                        vec3 o = vec3(origin.xy, origin.z / q);
                        float b = 2.0 * dot(o, dirScaled);
                        float c = dot(o, o) - 1.0;
                        float d = b * b - 4.0 * a * c;
                        bool onGlobe = d >= 0.0;
                        float tGlobe = onGlobe ? (-b - sqrt(d)) / (2.0 * a) : 0.0;

                        vec3 globe = vec3(0.0);
                        if (onGlobe) {
                            vec3 hit = origin + tGlobe * dir;
                            vec3 normal = normalize(vec3(hit.xy, hit.z * q2Inverse));
                            float mu = dot(normal, toward);
                            float mu0 = dot(normal, s);
                            if (mu > 0.0 && mu0 > 0.0) {
                                float latitude = degrees(asin(clamp(normal.z, -1.0, 1.0)));
                                // atan(y, x) is UNDEFINED at (0, 0), which a ray meeting the globe at a pole reaches; the CPU's Atan2 reads 0.
                                float axisLen = length(hit.xy);
                                float west = axisLen > 1e-9 ? -degrees(atan(hit.y, hit.x)) : 0.0;
                                globe = sampleMap(latitude, west) * pow(mu0, k) * pow(mu, k - 1.0);
                                // The rings' shadow: the Sun's light through the ring its ray from this point crosses.
                                if (hasRings && s.z != 0.0 && -hit.z / s.z > 0.0) {
                                    vec2 shade = hit.xy + (-hit.z / s.z) * s.xy;
                                    vec2 ring = ringAt(length(shade));
                                    if (ring.x >= 0.0) {
                                        globe *= transmission(ring.y, s.z);
                                    }
                                }
                            }
                        }

                        vec3 radiance = globe;
                        float covered = onGlobe ? 1.0 : 0.0;
                        if (hasRings && abs(toward.z) >= 1e-9) {
                            // The ring plane z = 0, where a ring nearer the camera than the globe lies over it.
                            float tRing = -origin.z / dir.z;
                            vec2 crossing = origin.xy + tRing * dir.xy;
                            vec2 ring = ringAt(length(crossing));
                            if (ring.x >= 0.0 && (!onGlobe || tRing < tGlobe)) {
                                // The face the Sun lights is the one the camera sees when both stand on the same side of the plane.
                                bool lit = sign(s.z) == sign(toward.z) && !inGlobeShadow(crossing, s, q2Inverse);
                                float through = transmission(ring.y, toward.z);
                                radiance = (lit ? ring.x : 0.0) * pc.ringAlbedo.rgb + through * globe;
                                covered = onGlobe ? 1.0 : 1.0 - through;
                            }
                        }
                        colour += radiance;
                        coverage += covered;
                    }
                }
                if (coverage <= 0.0) {
                    discard;
                }
                // The colour of what the covering rays saw (un-premultiplied), and how much of the pixel they cover.
                vec3 rgb = colour / coverage;
                if (pc.sun.w > 0.5) {
                    rgb = dot(rgb, vec3(0.2126, 0.7152, 0.0722)) * pc.tint.rgb;
                }
                FragColor = vec4(clamp(rgb, 0.0, 1.0), coverage / 9.0 * pc.toward.w);
            }

            // ---- End of the trace ----
            """;

        private sealed class Slot
        {
            public (int Version, RGBAColor32 Flat) Requested = (int.MinValue, default);
            public TextureHandle? Shown;
            public (float R, float G, float B) ShownAlbedo;
        }

        private readonly Dictionary<CatalogIndex, Slot> _slots = [];
        // Each body's block in a buffer of its own: the draws are replayed at Present, after every body's upload.
        private readonly PlanetBodyInstances<GpuBufferHandle> _instances = new(data => renderer.CreateBuffer(data), (buffer, data) => renderer.UpdateBuffer(buffer, data));
        private PipelineHandle? _pipeline;
        private GpuBufferHandle? _corners;

        /// <summary>How many colour maps have been sent: one a body and texture version, never one a frame.</summary>
        public int Uploads { get; private set; }

        /// <summary>
        /// Draws <paramref name="view"/> centred on (<paramref name="x"/>, <paramref name="y"/>), its globe <paramref name="radiusPx"/>. False
        /// until a texture of the body has loaded.
        /// </summary>
        public bool Draw(in PlanetView view, float x, float y, float radiusPx, PlanetColourMap? map, int textureVersion, RGBAColor32 flat,
            RGBAColor32? nightTint, float opacity = 1)
        {
            if (!_slots.TryGetValue(view.Body, out var slot))
            {
                _slots[view.Body] = slot = new Slot();
            }
            var key = (textureVersion, map is null ? flat : default);
            if (slot.Requested != key)
            {
                slot.Requested = key;
                Uploads++;
                _ = LoadAsync(view.Body, slot, key, map, flat);
            }
            if (slot.Shown is not { } texture)
            {
                return false;
            }
            float width = renderer.Width;
            float height = renderer.Height;
            if (width == 0f || height == 0f)
            {
                return false;
            }

            _pipeline ??= renderer.RegisterPipeline(new CustomPipelineDescriptor(
                VertexSource, FragmentSource,
                // The quad's corner per vertex, then the Body block's seven vec4s per instance (interleaved, 28 floats).
                Attribs:
                [
                    new VertexAttrib(0, 2),
                    new VertexAttrib(1, 4, PerInstance: true),
                    new VertexAttrib(2, 4, PerInstance: true),
                    new VertexAttrib(3, 4, PerInstance: true),
                    new VertexAttrib(4, 4, PerInstance: true),
                    new VertexAttrib(5, 4, PerInstance: true),
                    new VertexAttrib(6, 4, PerInstance: true),
                    new VertexAttrib(7, 4, PerInstance: true),
                ],
                Blend: PipelineBlend.AlphaOver));
            // Corners of the two triangles: (0,0) (1,0) (1,1) / (0,0) (1,1) (0,1).
            _corners ??= renderer.CreateBuffer([0f, 0f, 1f, 0f, 1f, 1f, 0f, 0f, 1f, 1f, 0f, 1f]);
            var block = _instances.Write(view, x, y, radiusPx, width, height, slot.ShownAlbedo, nightTint, opacity);
            renderer.UsePipeline(_pipeline.Value);
            renderer.BindTexture(texture);
            renderer.DrawInstanced(_corners.Value, 6, block, 1);
            return true;
        }

        private async Task LoadAsync(CatalogIndex body, Slot slot, (int Version, RGBAColor32 Flat) key, PlanetColourMap? map, RGBAColor32 flat)
        {
            try
            {
                var (rgba, width, height) = PlanetBodyMap.Rgba8(map, flat);
                var url = "data:image/png;base64," + Convert.ToBase64String(PngWriter.Encode(rgba, width, height));
                var texture = await renderer.LoadTextureAsync(url);
                if (slot.Requested != key)
                {
                    // Superseded while it loaded: the newer map's load replaces it.
                    renderer.DestroyTexture(texture);
                    return;
                }
                if (slot.Shown is { } old)
                {
                    renderer.DestroyTexture(old);
                }
                slot.Shown = texture;
                slot.ShownAlbedo = PlanetBodyMap.RingAlbedo(map, flat);
                requestRedraw();
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Planet map for {Body} did not load", body);
            }
        }

        public void Dispose()
        {
            foreach (var slot in _slots.Values)
            {
                if (slot.Shown is { } texture)
                {
                    renderer.DestroyTexture(texture);
                }
            }
            _slots.Clear();
            if (_corners is { } buffer)
            {
                renderer.DestroyBuffer(buffer);
            }
            foreach (var block in _instances.Buffers)
            {
                renderer.DestroyBuffer(block);
            }
        }
    }
}
