using System;
using System.Runtime.InteropServices;
using DIR.Lib;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.UI.Abstractions;
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
        private static readonly RGBAColor32 GridColor = new(0x30, 0x60, 0xA0, 0x70);
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

        private bool _geometryBuilt;
        private GpuBufferHandle _cornerQuad;
        private GpuBufferHandle _stars;
        private int _starCount;
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
        }

        /// <summary>Builds the persistent static geometry once (bright stars + figures +
        /// boundaries + grid scales + ecliptic). Cheap no-op afterwards.</summary>
        public void EnsureGeometry(ICelestialObjectDB db)
        {
            if (_geometryBuilt)
            {
                return;
            }

            // Unit quad corners as two triangles (per-vertex stream of the instanced star draw).
            _cornerQuad = _renderer.CreateBuffer([-1f, -1f, 1f, -1f, 1f, 1f, -1f, -1f, 1f, 1f, -1f, 1f]);

            // The HR bright-star catalog is the browser star field (~9k naked-eye stars; the
            // Lightweight build has no Tycho-2, and HR needs no HIP cross-identity resolution).
            var stars = SkyMapGpuGeometry.BuildHrStarInstances(db);
            _starCount = stars.Count / SkyMapState.FloatsPerStar;
            _stars = _renderer.CreateBuffer(CollectionsMarshal.AsSpan(stars));

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
                    var (_, _, minFov, maxFov) = SkyMapGpuGeometry.GridScales[i];
                    if (fov >= minFov && fov <= maxFov && _grids[i].VertexCount > 0)
                    {
                        _renderer.SetPipelineColor(GridColor);
                        _renderer.DrawBuffer(_grids[i].Buffer, 0, _grids[i].VertexCount);
                    }
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

            if (_starCount > 0)
            {
                _renderer.UsePipeline(_starPipeline);
                _renderer.DrawInstanced(_cornerQuad, 6, _stars, _starCount);
            }
        }
    }
}
