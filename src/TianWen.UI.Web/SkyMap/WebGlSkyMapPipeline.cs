using System;
using System.Runtime.InteropServices;
using DIR.Lib;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.UI.Abstractions;
using TianWen.UI.Abstractions.Overlays;
using WebGl.Renderer;

namespace TianWen.UI.Web.SkyMap
{
    /// <summary>
    /// WebGL2 sky-map pipeline over WebGl.Renderer 1.3's custom-pipeline seam - the browser
    /// mirror of <c>VkSkyMapPipeline</c> (TianWen.UI.Shared). Stars are instanced quads from a
    /// persistent buffer of J2000 unit vectors (the HR/HIP bright-star seed: the Lightweight
    /// build has no Tycho-2); lines (constellation figures/boundaries, grid, ecliptic, horizon,
    /// meridian, Alt/Az) share one LINES pipeline with a per-draw uColor. All geometry comes
    /// from the shared <see cref="SkyMapGpuGeometry"/> builders; the per-frame view state is the
    /// shared 112-byte <see cref="SkyMapUbo"/> block - pan/zoom re-uploads only that.
    ///
    /// <para>Shader delta vs the Vulkan sources (transcribed, ASCII-only per the GLSL rule):
    /// GLSL ES 3.00, the push-constant color becomes <c>uniform vec4 uColor</c>, and the final
    /// NDC mapping negates Y (GL NDC Y is up; screen + Vulkan NDC Y are down). The web map draws
    /// full-canvas, so viewportCenter == canvas centre and viewportSize == canvas size.</para>
    /// </summary>
    internal sealed class WebGlSkyMapPipeline
    {
        private const string ProjectionGlsl = """
            // Stereographic projection: camera-space unit vector to screen pixel position.
            // Returns vec3(screenX, screenY, cosD) where cosD <= -0.99 means antipode.
            vec3 stereoProject(vec3 camPos) {
                float cosD = -camPos.z;  // camera looks along -Z
                if (cosD <= -0.99) return vec3(0.0, 0.0, -2.0);
                float k = 2.0 / (1.0 + cosD);
                float sx = viewportCenter.x + camPos.x * k * pixelsPerRadian;
                float sy = viewportCenter.y - camPos.y * k * pixelsPerRadian;
                return vec3(sx, sy, cosD);
            }
            """;

        private const string UboGlsl = """
            layout(std140) uniform SkyMapUBO {
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
            };
            """;

        private static readonly string StarVertexSource = $$"""
            #version 300 es
            precision highp float;
            precision highp int;

            layout(location = 0) in vec2 aCorner;      // per-vertex quad corner (-1,-1)..(1,1)
            layout(location = 1) in vec3 aUnitPos;     // per-instance J2000 unit vector
            layout(location = 2) in float aMagnitude;  // per-instance
            layout(location = 3) in float aBvColor;    // per-instance

            {{UboGlsl}}

            out vec2 vCorner;
            out vec3 vColor;
            out float vAlpha;

            {{ProjectionGlsl}}

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

            float rawStarRadius(float vMag, float fov) {
                float r = 4.0 * pow(10.0, -0.14 * vMag);
                float zoomScale = sqrt(60.0 / max(1.0, fov));
                return min(r * zoomScale, 15.0);
            }

            void main() {
                if (aMagnitude > magnitudeLimit) {
                    gl_Position = vec4(0.0, 0.0, 0.0, 0.0);
                    return;
                }

                if (horizonClip != 0) {
                    float sinAlt = sinLat * aUnitPos.z
                        + cosLat * (cosLST * aUnitPos.x + sinLST * aUnitPos.y);
                    if (sinAlt < 0.0) {
                        gl_Position = vec4(0.0, 0.0, 0.0, 0.0);
                        return;
                    }
                }

                vec3 camPos = (viewMatrix * vec4(aUnitPos, 1.0)).xyz;
                vec3 proj = stereoProject(camPos);
                if (proj.z <= -0.99) {
                    gl_Position = vec4(0.0, 0.0, 0.0, 0.0);
                    return;
                }

                float rawR = rawStarRadius(aMagnitude, fovDeg);
                float kMin = 1.0;
                float subPixelFade = clamp(rawR / kMin, 0.35, 1.0);
                float radius = max(rawR, kMin);

                float magFadeWidth = 0.8;
                float magFade = clamp((magnitudeLimit - aMagnitude) / magFadeWidth, 0.0, 1.0);

                vec2 screenPos = proj.xy + aCorner * (radius + 3.0);

                // Window-absolute pixels -> GL NDC (Y negated vs the Vulkan shader: GL NDC Y is
                // up; the web map draws full-canvas so viewportCenter/Size describe the canvas).
                gl_Position = vec4(
                    (screenPos.x - viewportCenter.x) / (viewportSize.x * 0.5),
                    -(screenPos.y - viewportCenter.y) / (viewportSize.y * 0.5),
                    0.0, 1.0);

                vCorner = aCorner;
                vColor = bvToRgb(aBvColor);

                float brightness = clamp(1.0 - aMagnitude / magnitudeLimit, 0.0, 1.0);
                vAlpha = (0.75 + 0.25 * brightness) * subPixelFade * magFade;
            }
            """;

        private const string StarFragmentSource = """
            #version 300 es
            precision highp float;
            precision highp int;

            in vec2 vCorner;
            in vec3 vColor;
            in float vAlpha;

            out vec4 FragColor;

            void main() {
                float dist = length(vCorner);
                // Analytic PSF - flat core + Gaussian-like halo (see the Vulkan source's tuning notes).
                float core  = 1.0 - smoothstep(0.32, 0.55, dist);
                float halo  = exp(-dist * dist * 3.2);
                float alpha = max(core, halo);
                if (alpha < 0.005) discard;

                FragColor = vec4(vColor * alpha * vAlpha, alpha * vAlpha);
            }
            """;

        private static readonly string LineVertexSource = $$"""
            #version 300 es
            precision highp float;
            precision highp int;

            layout(location = 0) in vec3 aUnitPos;

            {{UboGlsl}}

            uniform vec4 uColor;  // the push-constant color analog

            out vec4 vColor;

            {{ProjectionGlsl}}

            void main() {
                vec3 camPos = (viewMatrix * vec4(aUnitPos, 1.0)).xyz;
                vec3 proj = stereoProject(camPos);
                if (proj.z <= -0.99) {
                    gl_Position = vec4(2.0, 2.0, 0.0, 1.0);
                    vColor = vec4(0.0);
                    return;
                }

                gl_Position = vec4(
                    (proj.x - viewportCenter.x) / (viewportSize.x * 0.5),
                    -(proj.y - viewportCenter.y) / (viewportSize.y * 0.5),
                    0.0, 1.0);
                vColor = uColor;
            }
            """;

        private const string LineFragmentSource = """
            #version 300 es
            precision highp float;
            precision highp int;

            in vec4 vColor;
            out vec4 FragColor;

            void main() {
                FragColor = vColor;
            }
            """;

        // Overlay ellipse: instanced quads for the [O]/[D] DSO markers, transcribed from
        // Shaders/skymap_overlay.vert|frag. The instance stream is the shared
        // OverlayEllipseInstances layout, so the browser and the desktop feed identical bytes to
        // identical arithmetic; the only delta is the flipped NDC Y that every shader here carries.
        //
        // What it replaces on this surface is not one draw call per marker but one POLYLINE per
        // marker: the CPU path tessellated each ellipse into 8-32 stroke segments and pushed 36
        // floats per segment. At a full-sky zoom that measured 7,072 ellipse + circle markers,
        // 46,986 segments and about 6.8 MB of vertex data built and marshalled EVERY repaint --
        // and the browser has no render loop, so every repaint is an input event.
        private static readonly string OverlayVertexSource = $$"""
            #version 300 es
            precision highp float;
            precision highp int;

            layout(location = 0) in vec2  aCorner;       // per-vertex unit quad, [-1, 1]
            layout(location = 1) in vec3  aUnitVec;      // per-instance J2000 unit vec
            layout(location = 2) in vec2  aSizeArcmin;   // per-instance semi-axes (arcmin)
            layout(location = 3) in float aPaFromNorth;  // per-instance PA from north (rad)
            layout(location = 4) in float aThickness;    // per-instance stroke (px)
            layout(location = 5) in vec4  aColor;        // per-instance color

            {{UboGlsl}}

            out vec2  vLocal;
            out vec2  vSize;
            out float vThickness;
            out vec4  vColor;

            {{ProjectionGlsl}}

            void main() {
                vec3 camPos = (viewMatrix * vec4(aUnitVec, 1.0)).xyz;
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

                // Local north tangent from the unit vector alone. The clamped cosDec avoids the
                // pole singularity where cosDec -> 0 would blow up the division.
                float cosDec = sqrt(max(1e-6, 1.0 - aUnitVec.z * aUnitVec.z));
                vec3 nTangent = vec3(-aUnitVec.z * aUnitVec.x / cosDec,
                                     -aUnitVec.z * aUnitVec.y / cosDec,
                                      cosDec);

                // Project a tip one arcmin north and measure the screen-space angle to it.
                float stepRad = 2.908882e-4; // 1 arcmin in radians
                vec3 tipUnit = normalize(aUnitVec + nTangent * stepRad);
                vec3 tipProj = stereoProject((viewMatrix * vec4(tipUnit, 1.0)).xyz);
                vec2 north2d = tipProj.xy - center;
                // Measured in SCREEN space (Y down), exactly as the Vulkan source does -- the GL
                // NDC flip belongs at the gl_Position write and nowhere else. Flipping earlier
                // would mirror every position angle.
                float screenNorthAngle = atan(north2d.y, north2d.x);
                float totalAngle = screenNorthAngle - aPaFromNorth;

                float arcminToPx = pixelsPerRadian * 0.00029088820866;  // pi / (180 * 60)
                vec2 sizePx = aSizeArcmin * arcminToPx;

                // Pad the quad for the ring SDF antialias, then rotate + expand.
                float pad = max(aThickness * 0.75 + 1.0, 1.5);
                vec2 local = aCorner * (sizePx + vec2(pad));
                float cs = cos(totalAngle);
                float sn = sin(totalAngle);
                vec2 rotated = vec2(local.x * cs - local.y * sn,
                                    local.x * sn + local.y * cs);
                vec2 screenPos = center + rotated;

                gl_Position = vec4(
                    (screenPos.x - viewportCenter.x) / (viewportSize.x * 0.5),
                    -(screenPos.y - viewportCenter.y) / (viewportSize.y * 0.5),
                    0.0, 1.0);

                vLocal = local;
                vSize = sizePx;
                vThickness = aThickness;
                vColor = aColor;
            }
            """;

        private const string OverlayFragmentSource = """
            #version 300 es
            precision highp float;
            precision highp int;

            in vec2  vLocal;
            in vec2  vSize;
            in float vThickness;
            in vec4  vColor;

            out vec4 FragColor;

            void main() {
                // Axis-aligned ellipse SDF: (x/a)^2 + (y/b)^2 = 1 on the boundary, scaled by the
                // mean semi-axis to approximate a pixel distance from the ring.
                vec2 s = max(vSize, vec2(0.5));
                vec2 n = vLocal / s;
                float normDist = sqrt(dot(n, n));
                float avgR = (s.x + s.y) * 0.5;
                float pixelDist = abs(normDist - 1.0) * avgR;

                float halfT = max(vThickness * 0.5, 0.5);
                float alpha = 1.0 - smoothstep(halfT, halfT + 1.0, pixelDist);
                if (alpha < 0.01) discard;

                FragColor = vec4(vColor.rgb * alpha, vColor.a * alpha);
            }
            """;

        // Horizon ground shading: an attributeless full-screen pass (gl_VertexID generates the
        // quad; no vertex data consumed) whose FS inverse-stereographic-projects each pixel and
        // tints below-horizon directions with depth-scaled alpha - the port of the Vulkan
        // HorizonFill pipeline (gl_VertexIndex -> gl_VertexID; NDC Y flipped for GL).
        private static readonly string HorizonFillVertexSource = $$"""
            #version 300 es
            precision highp float;
            precision highp int;

            {{UboGlsl}}

            out vec2 vScreenPos;

            void main() {
                vec2 pos;
                if (gl_VertexID == 0)      pos = vec2(0.0, 0.0);
                else if (gl_VertexID == 1) pos = vec2(viewportSize.x, 0.0);
                else if (gl_VertexID == 2) pos = vec2(0.0, viewportSize.y);
                else if (gl_VertexID == 3) pos = vec2(viewportSize.x, 0.0);
                else if (gl_VertexID == 4) pos = vec2(viewportSize.x, viewportSize.y);
                else                       pos = vec2(0.0, viewportSize.y);

                // Screen-pixel position for the fragment shader (window-absolute, top-left origin).
                vScreenPos = pos + vec2(viewportCenter.x - viewportSize.x * 0.5,
                                        viewportCenter.y - viewportSize.y * 0.5);

                // Map to NDC - GL Y up, so the top of the screen (pos.y = 0) is +1.
                gl_Position = vec4(
                    pos.x / viewportSize.x * 2.0 - 1.0,
                    1.0 - pos.y / viewportSize.y * 2.0,
                    0.0, 1.0);
            }
            """;

        private static readonly string HorizonFillFragmentSource = $$"""
            #version 300 es
            precision highp float;
            precision highp int;

            {{UboGlsl}}

            in vec2 vScreenPos;
            out vec4 FragColor;

            void main() {
                // Inverse stereographic projection: screen pixel -> camera-space unit vector
                float x = (vScreenPos.x - viewportCenter.x) / pixelsPerRadian;
                float y = -(vScreenPos.y - viewportCenter.y) / pixelsPerRadian;

                // Warm earthy-brown tint for the below-horizon region. Mixed on top of
                // the sky background with depth-scaled alpha so pixels just below the
                // horizon are lightly hazed, and pixels deep below are mostly ground.
                vec3 groundTint = vec3(0.18, 0.11, 0.06);

                float rho = length(vec2(x, y));
                if (rho < 0.00001) {
                    vec3 j2000 = transpose(mat3(viewMatrix)) * vec3(0.0, 0.0, -1.0);
                    float sinAlt = sinLat * j2000.z
                        + cosLat * (cosLST * j2000.x + sinLST * j2000.y);
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
                vec3 j2000 = transpose(mat3(viewMatrix)) * camDir;

                float sinAlt = sinLat * j2000.z
                    + cosLat * (cosLST * j2000.x + sinLST * j2000.y);

                if (sinAlt >= 0.0) discard;

                // Fade from 0.45 at horizon to 0.88 at -10 degrees (sin(10deg) ~ 0.17)
                float depth = clamp(-sinAlt / 0.17, 0.0, 1.0);
                float alpha = 0.45 + 0.43 * depth;
                FragColor = vec4(groundTint, alpha);
            }
            """;

        // Line colors mirror VkSkyMapPipeline.Draw's PushLineColor constants.
        // The RA/Dec grid colour now comes from SkyMapGpuGeometry.GridColorAt(fade), shared with the
        // Vulkan pipeline; this local copy was also a flat 0x70 against the desktop's 0xB0 at full
        // fade, so the browser grid was dimmer than the desktop one at every zoom.
        private static readonly RGBAColor32 AltAzColor = new(0x80, 0xA0, 0x30, 0x80);
        private static readonly RGBAColor32 MeridianColor = new(0x30, 0xDD, 0x30, 0xA0);
        private static readonly RGBAColor32 EclipticColor = new(0xE0, 0xC0, 0x40, 0xB0);
        private static readonly RGBAColor32 BoundaryColor = new(0xAA, 0x44, 0x44, 0x80);
        private static readonly RGBAColor32 FigureColor = new(0x40, 0x80, 0xDD, 0x90);
        private static readonly RGBAColor32 HorizonColor = new(0x80, 0x40, 0x20, 0xFF);

        private readonly WebGlRenderer _renderer;
        private readonly PipelineHandle _starPipeline;
        private readonly PipelineHandle _linePipeline;
        private readonly PipelineHandle _horizonFillPipeline;
        private readonly PipelineHandle _overlayPipeline;

        // The overlay instance buffer, re-uploaded only when the overlay's own inputs move (see
        // SubmitOverlayInstances). A pan changes none of them, so a drag uploads nothing.
        private GpuBufferHandle _overlayInstances;
        private bool _overlayBufferCreated;
        private int _overlayInstanceCount;

        private bool _geometryBuilt;
        private GpuBufferHandle _cornerQuad;
        private GpuBufferHandle _stars;
        private int _starCount;

        // Both star buffers are grouped by sky region, brightest-first within each region, so the draw
        // submits only the regions the view can see and only their magnitude prefix. See the star draw.
        private StarChunk[] _starChunks = [];
        private StarChunk[] _tycho2Chunks = [];

        // The full ~2.5M-star Tycho-2 field, lazily fetched + decoded on first atlas-open (the
        // ~30 MB catalog is stripped from the WASM bundle, so the browser host fetches it as a
        // static asset and hands the built instance buffer to SubmitTycho2Stars). Until it lands
        // the HR bright-star seed above IS the field; once applied it replaces HR in the star draw
        // - the browser analogue of the desktop VkSkyMapPipeline's HIP-seed -> Tycho-2 swap. The
        // HR buffer stays allocated (~180 KB) as the bootstrap/fallback rather than being freed.
        private GpuBufferHandle _tycho2Stars;
        private int _tycho2StarCount;
        private bool _tycho2Applied;
        private float[]? _pendingTycho2Verts;
        private int _pendingTycho2Count;
        private StarChunk[] _pendingTycho2Chunks = [];
        private GpuBufferHandle _figures;
        private int _figureVertexCount;
        private GpuBufferHandle _boundaries;
        private int _boundaryVertexCount;
        private GpuBufferHandle _ecliptic;
        private int _eclipticVertexCount;
        private readonly (GpuBufferHandle Buffer, int VertexCount)[] _grids
            = new (GpuBufferHandle, int)[SkyMapGpuGeometry.GridScales.Length];

        // Site/time-dependent line sets, rebuilt only when their inputs move (render is
        // event-driven; an idle frame re-uploads nothing).
        private GpuBufferHandle _horizon;
        private int _horizonVertexCount = -1;
        private GpuBufferHandle _meridianAltAz;
        private int _meridianAltAzVertexCount = -1;
        private double _dynamicLstKey = double.NaN;
        private double _dynamicLatKey = double.NaN;

        public WebGlSkyMapPipeline(WebGlRenderer renderer)
        {
            _renderer = renderer;
            _starPipeline = renderer.RegisterPipeline(new CustomPipelineDescriptor(
                StarVertexSource, StarFragmentSource,
                Attribs:
                [
                    new VertexAttrib(0, 2),
                    new VertexAttrib(1, 3, PerInstance: true),
                    new VertexAttrib(2, 1, PerInstance: true),
                    new VertexAttrib(3, 1, PerInstance: true),
                ],
                Blend: PipelineBlend.Additive,
                UniformBlockName: "SkyMapUBO"));
            _linePipeline = renderer.RegisterPipeline(new CustomPipelineDescriptor(
                LineVertexSource, LineFragmentSource,
                Attribs: [new VertexAttrib(0, 3)],
                Topology: PipelineTopology.Lines,
                UniformBlockName: "SkyMapUBO"));
            // Attributeless (gl_VertexID) - the empty layout means DrawBuffer enables nothing.
            _horizonFillPipeline = renderer.RegisterPipeline(new CustomPipelineDescriptor(
                HorizonFillVertexSource, HorizonFillFragmentSource,
                Attribs: [],
                UniformBlockName: "SkyMapUBO"));
            // Per-instance widths 3+2+1+1+4 = OverlayEllipseInstances.FloatsPerInstance, in
            // declaration order -- the descriptor interleaves same-divisor attributes into one
            // buffer, which is exactly how the shared builder writes them. AlphaOver matches the
            // Vulkan pipeline's non-additive state (SrcAlpha/OneMinusSrcAlpha colour,
            // One/OneMinusSrcAlpha alpha), so a marker composites the same on both surfaces.
            _overlayPipeline = renderer.RegisterPipeline(new CustomPipelineDescriptor(
                OverlayVertexSource, OverlayFragmentSource,
                Attribs:
                [
                    new VertexAttrib(0, 2),
                    new VertexAttrib(1, 3, PerInstance: true),
                    new VertexAttrib(2, 2, PerInstance: true),
                    new VertexAttrib(3, 1, PerInstance: true),
                    new VertexAttrib(4, 1, PerInstance: true),
                    new VertexAttrib(5, 4, PerInstance: true),
                ],
                Blend: PipelineBlend.AlphaOver,
                UniformBlockName: "SkyMapUBO"));
        }

        /// <summary>Builds the persistent static geometry once (bright stars + figures +
        /// boundaries + grid scales + ecliptic). Cheap no-op afterwards. Waits for the catalog:
        /// the atlas is reachable while InitDBAsync still runs (the view chips don't block on
        /// the load), and building here pre-init would LATCH an empty star field forever.</summary>
        public void EnsureGeometry(ICelestialObjectDB db)
        {
            if (_geometryBuilt || !db.IsInitialized)
            {
                return;
            }

            // Unit quad corners as two triangles (per-vertex stream of the instanced star draw).
            _cornerQuad = _renderer.CreateBuffer([-1f, -1f, 1f, -1f, 1f, 1f, -1f, -1f, 1f, 1f, -1f, 1f]);

            // The HR bright-star catalog is the browser star field (~9k naked-eye stars; the
            // Lightweight build has no Tycho-2, and HR needs no HIP cross-identity resolution).
            var stars = SkyMapGpuGeometry.BuildHrStarInstances(db);
            _starCount = stars.Count / SkyMapState.FloatsPerStar;
            // Sorted + indexed before upload, on the same terms as the Tycho-2 buffer, so the seed
            // and the full field cull identically instead of the switch between them changing which
            // stars a given zoom shows.
            var starSpan = CollectionsMarshal.AsSpan(stars);
            _starChunks = StarChunkIndex.Build(starSpan);
            _stars = _renderer.CreateBuffer(starSpan);

            var figures = SkyMapGpuGeometry.BuildConstellationFigureLines(db);
            _figureVertexCount = figures.Count / 3;
            _figures = _renderer.CreateBuffer(CollectionsMarshal.AsSpan(figures));

            var boundaries = SkyMapGpuGeometry.BuildConstellationBoundaryLines();
            _boundaryVertexCount = boundaries.Count / 3;
            _boundaries = _renderer.CreateBuffer(CollectionsMarshal.AsSpan(boundaries));

            var ecliptic = SkyMapGpuGeometry.BuildEclipticLine();
            _eclipticVertexCount = ecliptic.Count / 3;
            _ecliptic = _renderer.CreateBuffer(CollectionsMarshal.AsSpan(ecliptic));

            for (var i = 0; i < _grids.Length; i++)
            {
                var grid = SkyMapGpuGeometry.BuildGridLines(i);
                _grids[i] = (_renderer.CreateBuffer(CollectionsMarshal.AsSpan(grid)), grid.Count / 3);
            }

            Console.WriteLine(
                $"[tianwen-web] sky geometry: {_starCount} HR stars, {_figureVertexCount / 2} figure segments, "
                + $"{_boundaryVertexCount / 2} boundary segments, buffers star={_stars.Id} corner={_cornerQuad.Id}");
            _geometryBuilt = true;
        }

        /// <summary>
        /// Hands the pipeline a fetched + decoded Tycho-2 star-instance buffer (5 floats/star, the
        /// same <see cref="SkyMapState.FloatsPerStar"/> layout as the HR seed) built by the browser
        /// host's fetch task. Only stashes it - the GPU upload + draw swap happens on the next
        /// render frame in <see cref="ApplyPendingTycho2"/> (buffer creation must be on the render
        /// thread). Safe to call off the render loop; it stores references only.
        /// </summary>
        public void SubmitTycho2Stars(float[] verts, int starCount)
        {
            // Group + sort + index HERE, not in ApplyPendingTycho2: this is the off-render-loop entry
            // point (the host's fetch task), while the apply step runs on the render thread and has to
            // stay cheap. This is what makes the cull in Draw a pair of array lookups.
            if (starCount > 0)
            {
                _pendingTycho2Chunks = StarChunkIndex.Build(verts.AsSpan(0, starCount * SkyMapState.FloatsPerStar));
            }

            _pendingTycho2Verts = verts;
            _pendingTycho2Count = starCount;
        }

        /// <summary>Render-thread swap-in of a submitted Tycho-2 build: uploads the instance buffer
        /// once and flips the star draw over to it. Cheap no-op until a build is submitted (and once
        /// applied). Called every frame from <see cref="Draw"/>.</summary>
        private void ApplyPendingTycho2()
        {
            if (_pendingTycho2Verts is not { } verts)
            {
                return;
            }
            _pendingTycho2Verts = null;

            if (_pendingTycho2Count <= 0)
            {
                _tycho2Applied = true; // nothing to draw; keep the HR seed on screen
                return;
            }

            _tycho2Stars = _renderer.CreateBuffer(verts.AsSpan(0, _pendingTycho2Count * SkyMapState.FloatsPerStar));
            _tycho2StarCount = _pendingTycho2Count;
            _tycho2Chunks = _pendingTycho2Chunks;
            _pendingTycho2Chunks = [];
            _tycho2Applied = true;

            uint atDefault = 0;
            foreach (var chunk in _tycho2Chunks)
            {
                atDefault += StarMagnitudeIndex.VisibleCount(chunk.MagBins, 8.5f);
            }
            Console.WriteLine(
                $"[tianwen-web] sky geometry: upgraded to Tycho-2 ({_tycho2StarCount} stars in "
                + $"{StarChunkIndex.ChunkCount} chunks, {atDefault} of them at V<=8.5)");
        }

        /// <summary>Uploads the shared 112-byte view block to both pipelines (each has its own
        /// UBO binding point) and refreshes the site/time-dependent line sets when LST/latitude
        /// moved. Call once per frame before <see cref="Draw"/>.</summary>
        public void UpdateFrame(SkyMapState state, float canvasWidth, float canvasHeight, SiteContext site)
        {
            Span<byte> block = stackalloc byte[SkyMapUbo.Size];
            SkyMapUbo.Write(block, state, canvasWidth, canvasHeight, offsetX: 0f, offsetY: 0f, site);
            _renderer.SetUniformBlock(_starPipeline, block);
            _renderer.SetUniformBlock(_linePipeline, block);
            _renderer.SetUniformBlock(_horizonFillPipeline, block);
            _renderer.SetUniformBlock(_overlayPipeline, block);

            // Horizon + meridian + Alt/Az geometry depends on (LST, latitude). LST moves ~15
            // arcsec/s of RA; a 30-second bucket keeps the lines visually glued to real time
            // while idle frames (pan/zoom bursts) re-upload nothing.
            var lstKey = site.IsValid ? Math.Round(site.LST * 120.0) / 120.0 : 0.0;
            var latKey = site.IsValid ? site.SinLat : 0.0;
            if (lstKey == _dynamicLstKey && latKey == _dynamicLatKey)
            {
                return;
            }
            _dynamicLstKey = lstKey;
            _dynamicLatKey = latKey;

            var horizon = new System.Collections.Generic.List<float>(768);
            SkyMapGpuGeometry.BuildHorizonLine(site, horizon);
            var horizonSpan = CollectionsMarshal.AsSpan(horizon);
            if (_horizonVertexCount < 0)
            {
                _horizon = _renderer.CreateBuffer(horizonSpan);
            }
            else
            {
                _renderer.UpdateBuffer(_horizon, horizonSpan);
            }
            _horizonVertexCount = horizon.Count / 3;

            // Meridian + Alt/Az share one dynamic buffer: [meridian | altAz] with draw offsets.
            var dyn = new System.Collections.Generic.List<float>(8192);
            SkyMapGpuGeometry.BuildMeridianLine(site.IsValid ? site.LST : 0.0, dyn);
            var meridianFloats = dyn.Count;
            SkyMapGpuGeometry.BuildAltAzGrid(site, dyn);
            var dynSpan = CollectionsMarshal.AsSpan(dyn);
            if (_meridianAltAzVertexCount < 0)
            {
                _meridianAltAz = _renderer.CreateBuffer(dynSpan);
            }
            else
            {
                _renderer.UpdateBuffer(_meridianAltAz, dynSpan);
            }
            _meridianVertexCount = meridianFloats / 3;
            _meridianAltAzVertexCount = dyn.Count / 3;
        }

        private int _meridianVertexCount;

        /// <summary>Records the frame's sky draws: lines back-to-front (grid, Alt/Az, meridian,
        /// ecliptic, boundaries, figures, horizon), then the instanced star field on top.</summary>
        public void Draw(SkyMapState state, SiteContext site)
        {
            if (!_geometryBuilt)
            {
                return;
            }

            // Swap in a lazily-fetched Tycho-2 field the frame after the host submits it.
            ApplyPendingTycho2();

            // Ground shading first, so lines/stars draw on top of it (the desktop order).
            if (state.ShowHorizon && site.IsValid)
            {
                _renderer.UsePipeline(_horizonFillPipeline);
                // The buffer satisfies the record's slot; the attributeless pipeline reads none of it.
                _renderer.DrawBuffer(_cornerQuad, 0, 6);
            }

            _renderer.UsePipeline(_linePipeline);

            if (state.ShowGrid)
            {
                var fov = state.FieldOfViewDeg;
                for (var i = 0; i < _grids.Length; i++)
                {
                    // Shared with the Vulkan pipeline. This loop used to require fov >= minFov,
                    // which reads as "this scale is for wider views" but the scales are
                    // COMPLEMENTARY (BuildGridLines omits every line a coarser scale draws), so
                    // below 30 degrees it deleted scale 0: no celestial equator, no +/-30 or +/-60
                    // parallels, no 0h/6h/12h/18h meridians, just the lines between them. It also
                    // drew at a flat alpha, so a crowded scale never faded out.
                    if (!SkyMapGpuGeometry.TryGetGridFade(i, fov, out var fade)
                        || _grids[i].VertexCount <= 0)
                    {
                        continue;
                    }

                    _renderer.SetPipelineColor(SkyMapGpuGeometry.GridColorAt(fade));
                    _renderer.DrawBuffer(_grids[i].Buffer, 0, _grids[i].VertexCount);
                }
            }

            if (state.ShowAltAzGrid && site.IsValid && _meridianAltAzVertexCount > _meridianVertexCount)
            {
                _renderer.SetPipelineColor(AltAzColor);
                _renderer.DrawBuffer(_meridianAltAz, _meridianVertexCount, _meridianAltAzVertexCount - _meridianVertexCount);
            }

            if (site.IsValid && _meridianVertexCount > 0)
            {
                _renderer.SetPipelineColor(MeridianColor);
                _renderer.DrawBuffer(_meridianAltAz, 0, _meridianVertexCount);
            }

            if (_eclipticVertexCount > 0)
            {
                _renderer.SetPipelineColor(EclipticColor);
                _renderer.DrawBuffer(_ecliptic, 0, _eclipticVertexCount);
            }

            if (state.ShowConstellationBoundaries && _boundaryVertexCount > 0)
            {
                _renderer.SetPipelineColor(BoundaryColor);
                _renderer.DrawBuffer(_boundaries, 0, _boundaryVertexCount);
            }

            if (state.ShowConstellationFigures && _figureVertexCount > 0)
            {
                _renderer.SetPipelineColor(FigureColor);
                _renderer.DrawBuffer(_figures, 0, _figureVertexCount);
            }

            if (state.ShowHorizon && _horizonVertexCount > 0)
            {
                _renderer.SetPipelineColor(HorizonColor);
                _renderer.DrawBuffer(_horizon, 0, _horizonVertexCount);
            }

            // Star field on top: the full Tycho-2 catalog once it has been fetched + swapped in,
            // otherwise the HR bright-star seed (the bundle bootstrap). Never both - additive blend
            // would double every star the two share, so this is a switch, not an overlay.
            //
            // TWO-AXIS CULL, matching the desktop pipeline. The buffer is grouped by sky region and
            // sorted brightest-first within each region, so a frame submits only the regions the view
            // cone can reach and only their magnitude prefix. Without it every frame submitted all
            // ~2.5M Tycho-2 instances (~15M vertices) whatever the view showed, which pinned the GPU
            // process at 59% during a drag and dropped 944 of 1287 frames; on the desktop the same
            // unbounded form did not merely drop frames, it TDR'd an Adreno X1-85.
            //
            // Both axes are load-bearing and neither covers the other: magnitude bounds a WIDE field
            // (~3% of the catalog at 60 degrees) but stops bounding anything as the limit climbs with
            // zoom (81% at V<=12), while the cone is what makes a deep zoom cheap and does nothing at
            // full sky. The per-chunk draw needs WebGl.Renderer 1.24's firstInstance, because WebGL2
            // has no base-instance draw argument.
            var magLimit = state.EffectiveMagnitudeLimit;
            var (chunks, buffer) = _tycho2Applied && _tycho2StarCount > 0
                ? (_tycho2Chunks, _tycho2Stars)
                : (_starChunks, _stars);
            if (chunks.Length > 0)
            {
                // View cone in J2000: axis = the look-at direction, radius = the FULL field of view,
                // generous enough to cover the viewport diagonal at any aspect so chunks never pop in
                // and out at the screen edges.
                var (vx, vy, vz) = SkyMapState.RaDecToUnitVec(state.CenterRA, state.CenterDec);
                var viewRadiusRad = (float)double.DegreesToRadians(Math.Min(180.0, state.FieldOfViewDeg));

                var pipelineBound = false;
                foreach (var chunk in chunks)
                {
                    if (chunk.Count == 0 || !StarChunkIndex.IsVisible(chunk, vx, vy, vz, viewRadiusRad))
                    {
                        continue;
                    }

                    var visible = (int)StarMagnitudeIndex.VisibleCount(chunk.MagBins, magLimit);
                    if (visible == 0)
                    {
                        continue;
                    }

                    if (!pipelineBound)
                    {
                        _renderer.UsePipeline(_starPipeline);
                        pipelineBound = true;
                    }
                    _renderer.DrawInstanced(_cornerQuad, 6, buffer, visible, (int)chunk.Offset);
                }
            }
        }

        /// <summary>
        /// Replaces the overlay instance buffer. The caller decides WHEN, because it owns the inputs
        /// the instances are a function of (the cached candidate list, the arcmin-to-pixel scale, the
        /// wide-FOV fade and the horizon dimming) and none of them move during a pan. That matters
        /// because <c>UpdateBuffer</c> is a full <c>bufferData</c> reallocation: at a full-sky zoom
        /// this stream is ~7,000 instances, about 311 KB. It is still an order of magnitude less than
        /// the ~6.8 MB of stroke vertices the CPU path rebuilt per repaint, which is why uploading it
        /// on every FOV change is a good trade and uploading it on every pan event would not be.
        /// </summary>
        public void SubmitOverlayInstances(ReadOnlySpan<float> instances)
        {
            _overlayInstanceCount = instances.Length / OverlayEllipseInstances.FloatsPerInstance;
            if (_overlayInstanceCount == 0)
            {
                return;
            }

            if (_overlayBufferCreated)
            {
                _renderer.UpdateBuffer(_overlayInstances, instances);
            }
            else
            {
                _overlayInstances = _renderer.CreateBuffer(instances);
                _overlayBufferCreated = true;
            }
        }

        /// <summary>
        /// One instanced draw for every ellipse + circle overlay marker last submitted. Records after
        /// the star field so markers sit on top of it; the caller draws crosses and labels afterwards
        /// with the ordinary primitives, and the renderer rebinds its fixed pipeline for those.
        /// </summary>
        public void DrawOverlay()
        {
            if (!_geometryBuilt || !_overlayBufferCreated || _overlayInstanceCount == 0)
            {
                return;
            }

            _renderer.UsePipeline(_overlayPipeline);
            _renderer.DrawInstanced(_cornerQuad, 6, _overlayInstances, _overlayInstanceCount);
        }
    }
}
