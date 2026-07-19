using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using SdlVulkan.Renderer;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.UI.Abstractions;
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


    // Full-screen quad vertex shader (no vertex input, generates 2 triangles from gl_VertexIndex)


    // ────────────────────────────────────────────────── Milky Way fragment shader
    //
    // Full-screen quad: inverse stereographic -> J2000 unit vector -> equirectangular UV -> texture sample.
    // Vertex shader is skymap_mw.vert (identical GLSL to skymap_hzfill.vert). Push constant controls
    // brightness (sun altitude fade).


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

    // ── Spatial chunking ──────────────────────────────────────────────────
    // The star buffer is laid out grouped by spatial chunk (a coarse RA/Dec grid),
    // sorted by magnitude within each chunk. At draw time only the chunks whose
    // bounding cone intersects the view cone are submitted, and only their
    // magnitude-prefix -- so a deep zoom touches a handful of chunks instead of
    // streaming the whole catalog to the GPU (the unbounded version TDR'd the
    // Adreno X1-85, which froze the UI thread in the swapchain-recovery path).
    private const int StarGridCols = 12;                  // RA slices  (2h / 30deg each)
    private const int StarGridRows = 12;                  // Dec slices (15deg each)
    private const int StarChunkCount = StarGridCols * StarGridRows;
    private StarChunk[] _starChunks = [];
#if DEBUG
    // Slow-frame attribution: the star draw is GPU-side (instanced quads), so its cost never
    // shows in CPU phase timers -- it surfaces as a high 'begin=' (fence wait) in the event
    // loop's frame.slow split. Logging the visible-instance count whenever it moves by >25%
    // gives that fence pressure a correlatable cause (FOV/magnitude-limit changes).
    private uint _lastLoggedVisibleStars;
#endif

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

    /// <summary>
    /// Month-key the star buffer was built for, encoded as <c>year*12 + (month-1)</c>.
    /// Sentinel <see cref="int.MinValue"/> means "not built yet". When this drifts
    /// from the key of the current viewing epoch by even one month, the star
    /// buffer is rebuilt with positions propagated to the new epoch via
    /// per-star proper motion. Half-month worst-case pm drift at median
    /// Tycho-2 pm of 7 mas/yr is ~0.3 mas -- well below any usable sky-map
    /// pixel scale, so month-grain caching is conservative enough.
    /// </summary>
    private int _starBufferMonthKey = int.MinValue;

    /// <summary>
    /// Last month-key requested by <see cref="BuildGeometry"/>. The async
    /// rebuild path uses this to discard stale results: if the user scrubs
    /// past while a rebuild is in flight, the completed task may carry
    /// data for a month we no longer want -- spotting that via
    /// <c>result.MonthKey != _starBufferRequestedMonthKey</c> lets the
    /// render thread skip the swap and kick off a fresh rebuild on the
    /// next BuildGeometry call.
    /// </summary>
    private int _starBufferRequestedMonthKey = int.MinValue;

    /// <summary>
    /// In-flight async star-buffer rebuild, if any. Mirrors the
    /// <c>_loadTask</c> pattern in <c>ViewerController</c>: the background
    /// thread runs the CPU-bound vertex compute + sort + mag-bins, the
    /// render thread later installs the result via the GPU upload step
    /// inside <see cref="TryApplyPendingStarBuild"/>. Only one rebuild
    /// can be in flight at a time -- a same-month request becomes a noop;
    /// a different-month request waits for the current one to complete
    /// (and either install or get discarded as stale) before starting.
    /// </summary>
    private Task<StarBuildResult>? _starRebuildTask;

    /// <summary>
    /// One spatial chunk's slice of the contiguous star buffer: its instance range
    /// (<see cref="Offset"/> + <see cref="Count"/> within the magnitude-sorted, chunk-grouped
    /// buffer), the per-chunk magnitude -> prefix-count lookup (brightest-first, same 0.5-mag
    /// bins as the old global table), and a bounding cone (axis unit vector + angular radius in
    /// radians) used to cull the chunk against the view cone at draw time.
    /// </summary>
    private readonly record struct StarChunk(
        uint Offset, uint Count, uint[] MagBins,
        float ConeX, float ConeY, float ConeZ, float ConeRadiusRad);

    /// <summary>
    /// Output of an async star-buffer rebuild: a flat float[] of star vertices
    /// (5 floats / star) laid out grouped by spatial chunk and sorted by magnitude
    /// within each chunk, the count actually written (NaN-mag stars dropped during
    /// the loop), the per-chunk layout, and the target month-key so a stale build
    /// can be discarded on swap.
    /// </summary>
    private readonly record struct StarBuildResult(
        float[] Verts, uint StarCount, StarChunk[] Chunks, int MonthKey);

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
    /// True once the full Tycho-2 star buffer has been installed for some month. False only
    /// during the very first frames, while the HIP bright-star seed is showing and the async
    /// Tycho-2 build is still in flight (a routine month-crossing rebuild keeps the previous
    /// full buffer displayed, so this stays true through it). The tab shows a "Loading stars..."
    /// hint while this is false.
    /// </summary>
    public bool FullStarsReady => _starBufferMonthKey != int.MinValue;

    /// <summary>
    /// Optional callback invoked (on a thread-pool thread) when an async star-buffer build
    /// completes, so the host can wake its <c>NeedsRedraw</c>-gated render loop and let
    /// <see cref="TryApplyPendingStarBuild"/> swap the new buffer in. Without it the swap frame
    /// would not fire while the user sits idle on the tab after the first (HIP-seed) frame.
    /// </summary>
    public Action? RequestRedraw { get; set; }

    /// <summary>
    /// Build all persistent vertex buffers from the star catalog, with Tycho-2
    /// stars propagated from J2000 to <paramref name="starEpoch"/> via per-star
    /// proper motion. Safe to call every frame -- early-returns when the
    /// already-built buffer is current for the requested epoch's month
    /// (Tycho-2 pm-induced drift inside a single month is sub-arcsec, far
    /// below sky-map pixel scale, so monthly cache granularity is correct).
    /// Crossing a month boundary rebuilds only the star buffer, off-thread via
    /// <see cref="StartStarBufferRebuildAsync"/> (the CPU pass is ~1 s on a 2.5M-star
    /// catalog, so it never blocks the render thread); constellations, grid, and the
    /// ecliptic are epoch-independent and stay in place.
    /// </summary>
    public void BuildGeometry(ICelestialObjectDB db, DateTimeOffset starEpoch)
    {
        var monthKey = StarBufferMonthKey(starEpoch);
        _starBufferRequestedMonthKey = monthKey;

        if (!_geometryBuilt)
        {
            // First build is PROGRESSIVE so the tab paints immediately instead of freezing
            // ~1 s on the synchronous 2.5M-star Tycho-2 vertex build:
            //   1. Cheap epoch-independent geometry (constellations, grid, ecliptic) -- all small.
            //   2. Seed the star field from the ~1000 constellation-figure HIP stars, so the
            //      figures sit on visible dots immediately (renders at J2000; pm drift is
            //      sub-pixel at sky-map scale until the full buffer swaps in).
            //   3. Dispatch the full ~2.5M-star Tycho-2 build on the async path; a later frame's
            //      TryApplyPendingStarBuild installs it and frees the seed in the swap.
            // _starBufferMonthKey is deliberately LEFT at its sentinel (int.MinValue) so the
            // steady-state branch (next frame) does not mistake the seed for the current
            // month's full buffer -- it keeps no-op'ing until the async Tycho build lands.
            var sw = System.Diagnostics.Stopwatch.StartNew();

            BuildConstellationFigureBuffer(db);
            BuildConstellationBoundaryBuffer();
            BuildGridBuffers();
            BuildEclipticBuffer();
            BuildStarBufferFromHip(db);

            _geometryBuilt = true;
            System.Console.Error.WriteLine(
                $"[VkSkyMapPipeline] first build (seed, month-key {monthKey}): " +
                $"{_starCount} figure stars + geometry in {sw.Elapsed.TotalMilliseconds:N0} ms; " +
                $"full Tycho-2 build dispatched async");

            // monthKey was stored in _starBufferRequestedMonthKey at the top of BuildGeometry,
            // so TryApplyPendingStarBuild's stale-check is valid for this dispatch.
            StartStarBufferRebuildAsync(db, monthKey);
            return;
        }

        // Geometry exists -- apply any completed rebuild from a prior frame's
        // request before deciding whether to kick off a new one.
        TryApplyPendingStarBuild();

        if (monthKey == _starBufferMonthKey)
        {
            return;
        }

        // A rebuild is already in flight: noop. If it lands on a stale month
        // (user scrubbed past while building), TryApplyPendingStarBuild on a
        // later frame will discard the result and a fresh build kicks off here.
        if (_starRebuildTask is { IsCompleted: false })
        {
            return;
        }

        StartStarBufferRebuildAsync(db, monthKey);
    }

    /// <summary>
    /// Kicks off the background task that produces a fresh star vertex array + magnitude bins
    /// for <paramref name="monthKey"/>. Pure-CPU work (Tycho-2 walk, pm propagation, sort, mag
    /// bins) -- no Vulkan calls happen on the background thread. The render-thread swap runs in
    /// <see cref="TryApplyPendingStarBuild"/> next frame.
    /// </summary>
    private void StartStarBufferRebuildAsync(ICelestialObjectDB db, int monthKey)
    {
        var year = monthKey / 12;
        var month = (monthKey % 12) + 1;
        var bake = new DateTimeOffset(year, month, 1, 12, 0, 0, TimeSpan.Zero);
        var dtYr = bake.JulianYearsSinceJ2000();
        const int floatsPerStar = SkyMapState.FloatsPerStar;

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // The Tycho-2 bulk decode is kicked off (unawaited) during catalog init. Chain the vertex
        // build off EnsureTycho2DataLoadedAsync so it only runs once the binary is loaded --
        // otherwise Tycho2StarCount returns 0 (it does NOT block) and we'd swap an empty buffer
        // over the HIP seed on the first build. EnsureTycho2DataLoadedAsync returns the SAME single
        // in-flight decode task (no second decode). ContinueWith rather than async/await because
        // this class is `unsafe` and await is disallowed in an unsafe context; the continuation is
        // pure CPU on the thread pool (TaskScheduler.Default), never the render thread.
        _starRebuildTask = db.EnsureTycho2DataLoadedAsync().ContinueWith(_ =>
        {
            var tycCount = db.Tycho2StarCount;
            var verts = new float[tycCount * floatsPerStar];
            var written = SkyMapState.FillTycho2StarVertices(db, dtYr, verts);
            var chunks = ChunkAndSortStars(verts.AsSpan(0, written * floatsPerStar), floatsPerStar);
            System.Console.Error.WriteLine(
                $"[VkSkyMapPipeline] async star build month-key {monthKey} (dtYr={dtYr:F3}): " +
                $"{sw.Elapsed.TotalMilliseconds:N0} ms incl. decode wait ({written} stars)");

            // Wake the render loop so TryApplyPendingStarBuild runs and swaps this result in. The
            // GUI loop is NeedsRedraw-gated, not continuous, so without this nudge the swap frame
            // would never fire while the user sits idle on the tab after the seed paints.
            RequestRedraw?.Invoke();
            return new StarBuildResult(verts, (uint)written, chunks, monthKey);
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Render-thread half of the async rebuild: when a background task has
    /// completed, do the actual GPU buffer creation + atomic field swap.
    /// Called every frame from the tab's render path before <see cref="Draw"/>.
    /// Cheap when no task is in flight; runs the GPU upload + drain only on
    /// the one frame where the task transitions to completed.
    /// </summary>
    public void TryApplyPendingStarBuild()
    {
        if (_starRebuildTask is not { IsCompletedSuccessfully: true } task)
        {
            return;
        }
        var result = task.Result;
        _starRebuildTask = null;

        // Stale: user scrubbed past while we were building. Discard so the
        // next BuildGeometry kicks off a fresh request for the latest month.
        if (result.MonthKey != _starBufferRequestedMonthKey)
        {
            System.Console.Error.WriteLine(
                $"[VkSkyMapPipeline] discarded stale build for month-key {result.MonthKey} " +
                $"(latest requested {_starBufferRequestedMonthKey})");
            return;
        }

        // Empty result: the catalog has no Tycho-2 data for this build (legacy DB without the
        // binary, or a decode fault). Keep the HIP bright-star seed rather than swapping in an
        // empty buffer that would blank the star field. Record the month-key anyway so we stop
        // re-dispatching and the "Loading stars..." hint clears (FullStarsReady flips true).
        if (result.StarCount == 0)
        {
            _starBufferMonthKey = result.MonthKey;
            System.Console.Error.WriteLine(
                $"[VkSkyMapPipeline] async build for month-key {result.MonthKey} produced 0 stars; " +
                $"keeping HIP seed");
            return;
        }

        // GPU swap. vkDeviceWaitIdle drains any in-flight draw still
        // referencing the old buffer; CreatePersistentVertexBuffer allocates +
        // uploads the new one; DestroyBuffer frees the old. Atomic from the
        // render thread's perspective -- the swap completes inside this method
        // and the next Draw uses the new buffer. Skip the drain when the GPU is
        // known wedged: an unbounded wait on a stuck device would hang the render
        // thread (the event loop already short-circuits OnRender while stuck, so
        // this is a belt-and-suspenders guard against ever blocking here).
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (!_ctx.IsGpuStuck)
        {
            _ctx.DeviceApi.vkDeviceWaitIdle();
        }
        var waitMs = sw.Elapsed.TotalMilliseconds;

        DestroyBuffer(_starBuffer, _starMemory);
        var uploadStart = sw.Elapsed;
        var floats = result.Verts.AsSpan(0, (int)result.StarCount * SkyMapState.FloatsPerStar);
        (_starBuffer, _starMemory) = _ctx.CreatePersistentVertexBuffer(floats);
        var uploadMs = (sw.Elapsed - uploadStart).TotalMilliseconds;

        _starCount = result.StarCount;
        _starChunks = result.Chunks;
        _starBufferMonthKey = result.MonthKey;

        System.Console.Error.WriteLine(
            $"[VkSkyMapPipeline] async swap installed month-key {result.MonthKey}: " +
            $"GPU drain {waitMs:N0} ms + upload {uploadMs:N0} ms = render-thread {sw.Elapsed.TotalMilliseconds:N0} ms ({_starCount} stars)");
    }

    /// <summary>
    /// Encodes a viewing epoch as an integer comparable month key
    /// (<c>year * 12 + (month - 1)</c>). Equality on this key means
    /// the cached star buffer's pm-propagated positions are still
    /// accurate to within a half-month of pm drift.
    /// </summary>
    private static int StarBufferMonthKey(DateTimeOffset epoch)
    {
        var u = epoch.UtcDateTime;
        return u.Year * 12 + (u.Month - 1);
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
        // Composition (incl. the CurrentViewMatrix stamp) lives in the shared SkyMapUbo writer
        // (one layout for both GPU backends); this method only targets the mapped frame slot.
        _currentUboFrame = frameIndex;
        Span<byte> block = stackalloc byte[SkyMapUbo.Size];
        SkyMapUbo.Write(block, state, viewportWidth, viewportHeight, offsetX, offsetY, site);
        block.CopyTo(new Span<byte>(_uboMapped[frameIndex], SkyMapUbo.Size));
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
        // The star buffer is laid out grouped by spatial chunk, sorted by magnitude
        // within each chunk. Cull chunks whose bounding cone misses the view cone, then
        // draw only each visible chunk's magnitude-prefix. A deep zoom (small FOV, high
        // effective magnitude) touches a handful of chunks instead of streaming the whole
        // ~2M-star catalog to the GPU — the unbounded version TDR'd the Adreno X1-85.
        if (_starChunks.Length > 0 && _starCount > 0)
        {
            var effMag = state.EffectiveMagnitudeLimit;

            // View cone in J2000: axis = the look-at direction (identical to the view
            // matrix's forward vector, so the cull is exact in both equatorial and horizon
            // modes). Radius = the full FOV — generous enough to cover the viewport diagonal
            // for any reasonable aspect, so chunks never pop in/out at the screen edges.
            var (vx, vy, vz) = SkyMapState.RaDecToUnitVec(state.CenterRA, state.CenterDec);
            var viewRadiusRad = (float)double.DegreesToRadians(Math.Min(180.0, state.FieldOfViewDeg));

            api.vkCmdBindPipeline(cmd, VkPipelineBindPoint.Graphics, _starPipeline);

            // Bind quad (binding 0) and star instance data (binding 1) once; each chunk is a
            // contiguous instance range drawn via a firstInstance offset into the same buffer.
            var offsets = stackalloc ulong[2];
            offsets[0] = 0;
            offsets[1] = 0;
            var buffers = stackalloc VkBuffer[2];
            buffers[0] = _quadBuffer;
            buffers[1] = _starBuffer;
            api.vkCmdBindVertexBuffers(cmd, 0, 2, buffers, offsets);

            uint drawn = 0;
            for (var c = 0; c < _starChunks.Length; c++)
            {
                var chunk = _starChunks[c];
                if (chunk.Count == 0)
                {
                    continue;
                }

                // View-cone cull: skip when the two cones cannot intersect, i.e. the angular
                // separation of their axes exceeds the sum of their radii.
                var dot = vx * chunk.ConeX + vy * chunk.ConeY + vz * chunk.ConeZ;
                var sep = MathF.Acos(Math.Clamp(dot, -1f, 1f));
                if (sep > viewRadiusRad + chunk.ConeRadiusRad)
                {
                    continue;
                }

                // Magnitude cull: only this chunk's brightest-first prefix at the current limit.
                var n = GetVisibleStarCount(chunk.MagBins, effMag);
                if (n == 0)
                {
                    continue;
                }

                // 6 vertices per quad, n instances starting at this chunk's offset.
                api.vkCmdDraw(cmd, 6, n, 0, chunk.Offset);
                drawn += n;
            }
#if DEBUG
            if (drawn > _lastLoggedVisibleStars + (_lastLoggedVisibleStars >> 2)
                || drawn < _lastLoggedVisibleStars - (_lastLoggedVisibleStars >> 2))
            {
                Console.Error.WriteLine(
                    $"[rdiag] skymap.stars drawn={drawn} effMag={effMag:F1} fov={state.FieldOfViewDeg:F0} (was {_starCount} unculled)");
                _lastLoggedVisibleStars = drawn;
            }
#endif
        }
    }

    // ────────────────────────────────────────────────── Grid drawing

    // Grid scale definitions live in the shared SkyMapGpuGeometry (one source for both backends).
    private static (double RaStep, double DecStep, double MinFov, double MaxFov)[] GridScales
        => SkyMapGpuGeometry.GridScales;

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
    /// floats (see <c>Shaders/skymap_overlay.vert</c>). Binds its own viewport/scissor
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

    /// <summary>
    /// Sorts the flat star float array by the magnitude field (index 3 of each 5-float record).
    /// Uses Array.Sort on a temporary index array to avoid copying 20-byte records.
    /// </summary>
    private static void SortStarsByMagnitude(Span<float> span, int floatsPerStar)
    {
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
    /// Partitions the flat star vertex span into a coarse RA/Dec grid, reorders it in place so
    /// each chunk's stars are contiguous, sorts each chunk brightest-first, and returns the
    /// per-chunk layout (instance range + 0.5-mag prefix bins + bounding cone). The draw then
    /// culls whole chunks by view cone and submits only each visible chunk's magnitude prefix,
    /// bounding GPU work so a deep zoom can't stream the whole catalog and TDR the GPU.
    /// </summary>
    private static StarChunk[] ChunkAndSortStars(Span<float> verts, int floatsPerStar)
    {
        var count = verts.Length / floatsPerStar;
        var chunks = new StarChunk[StarChunkCount];
        if (count == 0)
        {
            return chunks; // every chunk Count == 0 -> all culled at draw
        }

        const float raCellDeg = 360f / StarGridCols;
        const float decCellDeg = 180f / StarGridRows;
        const float rad2deg = 180f / MathF.PI;

        // 1. Assign each star to a chunk (RA column x Dec row) from its unit vector.
        var chunkOf = new int[count];
        var counts = new int[StarChunkCount];
        for (var i = 0; i < count; i++)
        {
            var b = i * floatsPerStar;
            float x = verts[b], y = verts[b + 1], z = verts[b + 2];
            var decDeg = MathF.Asin(Math.Clamp(z, -1f, 1f)) * rad2deg;            // [-90, 90]
            var raDeg = MathF.Atan2(y, x) * rad2deg;                              // (-180, 180]
            if (raDeg < 0f)
            {
                raDeg += 360f;                                                    // [0, 360)
            }
            var col = Math.Clamp((int)(raDeg / raCellDeg), 0, StarGridCols - 1);
            var row = Math.Clamp((int)((decDeg + 90f) / decCellDeg), 0, StarGridRows - 1);
            var c = row * StarGridCols + col;
            chunkOf[i] = c;
            counts[c]++;
        }

        // 2. Prefix offsets: the instance index where each chunk begins in the grouped buffer.
        var offsets = new int[StarChunkCount];
        for (int c = 0, running = 0; c < StarChunkCount; c++)
        {
            offsets[c] = running;
            running += counts[c];
        }

        // 3. Stable scatter into a chunk-grouped copy, then write it back over the input span.
        var grouped = new float[count * floatsPerStar];
        var cursor = (int[])offsets.Clone();
        for (var i = 0; i < count; i++)
        {
            var dst = cursor[chunkOf[i]]++ * floatsPerStar;
            verts.Slice(i * floatsPerStar, floatsPerStar).CopyTo(grouped.AsSpan(dst, floatsPerStar));
        }
        grouped.AsSpan().CopyTo(verts);

        // 4. Per chunk: sort by magnitude, then compute prefix bins + bounding cone.
        for (var c = 0; c < StarChunkCount; c++)
        {
            var n = counts[c];
            if (n == 0)
            {
                chunks[c] = new StarChunk(0, 0, [], 0f, 0f, 1f, 0f);
                continue;
            }
            var sub = verts.Slice(offsets[c] * floatsPerStar, n * floatsPerStar);
            SortStarsByMagnitude(sub, floatsPerStar);
            var bins = ComputeMagBins(sub, floatsPerStar);
            var (cx, cy, cz, radRad) = ComputeChunkCone(sub, floatsPerStar);
            chunks[c] = new StarChunk((uint)offsets[c], (uint)n, bins, cx, cy, cz, radRad);
        }
        return chunks;
    }

    /// <summary>
    /// Bounding cone for a chunk's stars: axis = the normalized mean of the member unit vectors,
    /// radius = the maximum angular distance (radians) from that axis to any member. Drives the
    /// rotation-invariant view-cone cull in <see cref="Draw"/> (correct in equatorial + horizon).
    /// </summary>
    private static (float X, float Y, float Z, float RadiusRad) ComputeChunkCone(
        ReadOnlySpan<float> span, int floatsPerStar)
    {
        var n = span.Length / floatsPerStar;
        double sx = 0, sy = 0, sz = 0;
        for (var i = 0; i < n; i++)
        {
            var b = i * floatsPerStar;
            sx += span[b]; sy += span[b + 1]; sz += span[b + 2];
        }
        var len = Math.Sqrt(sx * sx + sy * sy + sz * sz);
        if (len < 1e-9)
        {
            return (0f, 0f, 1f, MathF.PI); // antipodal cancellation -> whole-sky cone, never culled
        }
        float ax = (float)(sx / len), ay = (float)(sy / len), az = (float)(sz / len);

        var minDot = 1f;
        for (var i = 0; i < n; i++)
        {
            var b = i * floatsPerStar;
            var dot = ax * span[b] + ay * span[b + 1] + az * span[b + 2];
            if (dot < minDot)
            {
                minDot = dot;
            }
        }
        return (ax, ay, az, MathF.Acos(Math.Clamp(minDot, -1f, 1f)));
    }

    /// <summary>
    /// Computes the magnitude → instance-count lookup table from a brightest-first sorted star
    /// vertex span. 30 bins covering V 0..15 in 0.5-mag steps. Pure function so the async rebuild
    /// can compute each chunk's table on a background thread.
    /// </summary>
    private static uint[] ComputeMagBins(ReadOnlySpan<float> sortedSpan, int floatsPerStar)
    {
        const int bins = 30;
        var magBins = new uint[bins];
        var count = (uint)(sortedSpan.Length / floatsPerStar);

        uint idx = 0;
        for (var bin = 0; bin < bins; bin++)
        {
            var magThreshold = (bin + 1) * 0.5f;
            while (idx < count && sortedSpan[(int)(idx * floatsPerStar + 3)] <= magThreshold)
            {
                idx++;
            }
            magBins[bin] = idx;
        }
        return magBins;
    }

    /// <summary>
    /// Number of star instances to draw from a chunk for the given magnitude limit. Stars are
    /// sorted brightest-first within the chunk, so this is just the prefix count from its bins.
    /// </summary>
    private static uint GetVisibleStarCount(uint[] magBins, float magLimit)
    {
        if (magBins.Length == 0)
        {
            return 0;
        }
        var bin = Math.Clamp((int)(magLimit * 2) - 1, 0, magBins.Length - 1);
        return magBins[bin];
    }

    /// <summary>
    /// Instant seed star buffer: just the ~1000 HIP stars referenced by the constellation
    /// figures (<see cref="ConstellationFigures.AllFigureStarHipNumbers"/>), so the figures sit
    /// on visible dots the moment the tab opens. Bounding to that set is what makes the seed
    /// cheap -- each HIP lookup is an O(partition) Tycho-2 scan, so walking all ~118k HIP would
    /// cost ~hundreds of ms; ~1000 lookups is a couple ms. The async build swaps in the full
    /// ~2.5M-star Tycho-2 catalogue a beat later (<see cref="StartStarBufferRebuildAsync"/>).
    /// </summary>
    // Geometry math lives in the backend-agnostic SkyMapGpuGeometry (shared with the WebGL
    // pipeline); these methods only upload the built float lists into Vulkan buffers.

    private void BuildStarBufferFromHip(ICelestialObjectDB db)
    {
        var floats = SkyMapGpuGeometry.BuildFigureStarInstances(db);
        _starCount = (uint)(floats.Count / SkyMapState.FloatsPerStar);
        var floatsSpan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(floats);
        _starChunks = ChunkAndSortStars(floatsSpan, SkyMapState.FloatsPerStar);
        (_starBuffer, _starMemory) = _ctx.CreatePersistentVertexBuffer(floatsSpan);
    }

    private void BuildConstellationFigureBuffer(ICelestialObjectDB db)
    {
        var floats = SkyMapGpuGeometry.BuildConstellationFigureLines(db);
        _figureVertexCount = (uint)(floats.Count / 3);
        if (_figureVertexCount > 0)
        {
            (_figureBuffer, _figureMemory) = _ctx.CreatePersistentVertexBuffer(
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(floats));
        }
    }

    private void BuildConstellationBoundaryBuffer()
    {
        var floats = SkyMapGpuGeometry.BuildConstellationBoundaryLines();
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
            var floats = SkyMapGpuGeometry.BuildGridLines(i);
            var vertexCount = (uint)(floats.Count / 3);
            if (vertexCount > 0)
            {
                var (buffer, memory) = _ctx.CreatePersistentVertexBuffer(
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(floats));
                _gridBuffers[i] = (buffer, memory, vertexCount);
            }
        }
    }

    // Dynamic-line builders + Alt/Az conversion moved to the shared SkyMapGpuGeometry;
    // thin delegating wrappers keep VkSkyMapTab's existing call sites stable.

    /// <inheritdoc cref="SkyMapGpuGeometry.BuildHorizonLine"/>
    public static void BuildHorizonLine(Lib.Astrometry.SOFA.SiteContext site, List<float> floats)
        => SkyMapGpuGeometry.BuildHorizonLine(site, floats);

    /// <inheritdoc cref="SkyMapGpuGeometry.BuildMeridianLine"/>
    public static void BuildMeridianLine(double lst, List<float> floats)
        => SkyMapGpuGeometry.BuildMeridianLine(lst, floats);

    /// <inheritdoc cref="SkyMapGpuGeometry.BuildAltAzGrid"/>
    public static void BuildAltAzGrid(Lib.Astrometry.SOFA.SiteContext site, List<float> floats)
        => SkyMapGpuGeometry.BuildAltAzGrid(site, floats);

    /// <inheritdoc cref="SkyMapGpuGeometry.AltAzToRaDec"/>
    public static void AltAzToRaDec(
        double altDeg, double azDeg, Lib.Astrometry.SOFA.SiteContext site,
        out double raHours, out double decDeg)
        => SkyMapGpuGeometry.AltAzToRaDec(altDeg, azDeg, site, out raHours, out decDeg);

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

        // Inject shared projection code into shaders

        var starVertModule = LoadShaderModule("skymap_star.vert");
        var starFragModule = LoadShaderModule("skymap_star.frag");
        var lineVertModule = LoadShaderModule("skymap_line.vert");
        var lineFragModule = LoadShaderModule("skymap_line.frag");

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
            var hzFillVertModule = LoadShaderModule("skymap_hzfill.vert");
            var hzFillFragModule = LoadShaderModule("skymap_hzfill.frag");

            _horizonFillPipeline = CreateGraphicsPipeline(
                hzFillVertModule, hzFillFragModule,
                null, 0, null, 0,
                VkPrimitiveTopology.TriangleList,
                additive: false);

            _ctx.DeviceApi.vkDestroyShaderModule(hzFillVertModule);
            _ctx.DeviceApi.vkDestroyShaderModule(hzFillFragModule);

            // ── Milky Way pipeline: full-screen quad + texture, own layout ──
            var mwVertModule = LoadShaderModule("skymap_mw.vert");
            var mwFragModule = LoadShaderModule("skymap_mw.frag");

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
            var ovVertModule = LoadShaderModule("skymap_overlay.vert");
            var ovFragModule = LoadShaderModule("skymap_overlay.frag");

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

        // stackalloc with explicit lifetime spanning vkCreateGraphicsPipeline. Same
        // dangling-pAttachments fix pattern as SdlVulkan.Renderer/VkPipelineSet.cs 3.4.471
        // and VkFitsImagePipeline above -- the Vortice.Vulkan single-attachment ctor stores
        // pAttachments pointing at its own stack frame, which is reclaimed when the ctor
        // returns and surfaces as black/zero fragment output on lavapipe x86_64.
        var blendAttachments = stackalloc VkPipelineColorBlendAttachmentState[1];
        if (additive)
        {
            // Additive blending: stars add light (like real sky)
            blendAttachments[0] = new VkPipelineColorBlendAttachmentState
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
            blendAttachments[0] = new VkPipelineColorBlendAttachmentState
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

        VkPipelineColorBlendStateCreateInfo colorBlend = new()
        {
            attachmentCount = 1,
            pAttachments = blendAttachments
        };

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

    // Loads a pre-baked SPIR-V shader (Shaders/spirv/<shaderName>.spv, embedded by the csproj) and
    // creates a VkShaderModule. GLSL is compiled to SPIR-V at build time by tools/BakeShaders, so there
    // is no runtime shaderc (SdlVulkan.Renderer 6.23 dropped the transitive dependency; baking is what
    // makes an Android build possible - shaderc ships no android RID - and trims AOT/first-frame cost).
    // Re-bake and commit the .spv on a shader edit (the TWSH0001 build warning flags a stale bake).
    private VkShaderModule LoadShaderModule(string shaderName)
    {
        var api = _ctx.DeviceApi;
        var resource = $"TianWen.UI.Shared.Shaders.{shaderName}.spv";
        using var stream = typeof(VkSkyMapPipeline).Assembly.GetManifestResourceStream(resource)
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
        // Skip the pre-teardown drain when the GPU is known wedged — an unbounded wait on a stuck
        // device would hang Dispose (matches the renderer's recovery/teardown guards).
        if (!_ctx.IsGpuStuck)
        {
            api.vkDeviceWaitIdle();
        }

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
