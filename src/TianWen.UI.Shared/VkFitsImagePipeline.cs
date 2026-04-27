using SdlVulkan.Renderer;
using Vortice.ShaderCompiler;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace TianWen.UI.Shared;

/// <summary>
/// Vulkan side-car pipeline for the FITS image viewer.
/// Owns its own descriptor set layouts, descriptor pool, pipeline layout, and pipelines
/// for image rendering (stretch + WCS grid) and histogram rendering.
/// </summary>
public sealed unsafe class VkFitsImagePipeline : IDisposable
{
    private const int ChannelCount = 3;
    private const int HistogramBins = 512;

    // ------------------------------------------------------------------ UBO sizes

    /// <summary>
    /// std140 StretchUBO — see field layout in struct definition below.
    /// Total: 192 bytes.
    /// </summary>
    private const int StretchUboSize = 192;

    /// <summary>
    /// std140 HistogramUBO — 4 x int/float fields = 16 bytes.
    /// </summary>
    private const int HistogramUboSize = 16;

    // ------------------------------------------------------------------ Shader sources

    private const string QuadVertexSource = """
        #version 450
        layout(location = 0) in vec2 aPos;
        layout(location = 1) in vec2 aTexCoord;
        layout(push_constant) uniform PC { mat4 proj; } pc;
        layout(location = 0) out vec2 vTexCoord;
        void main() {
            gl_Position = pc.proj * vec4(aPos, 0.0, 1.0);
            vTexCoord = aTexCoord;
        }
        """;

    private const string ImageFragmentSource = """
        #version 450

        layout(location = 0) in vec2 vTexCoord;
        layout(location = 0) out vec4 FragColor;

        layout(set = 0, binding = 0, std140) uniform StretchUBO {
            int   channelCount;     // offset   0
            int   stretchMode;      // offset   4
            float normFactor;       // offset   8
            float curvesBoost;      // offset  12
            float curvesMidpoint;   // offset  16
            float hdrAmount;        // offset  20
            float hdrKnee;          // offset  24
            float _pad0;            // offset  28
            vec4  pedestal;         // offset  32  (xyz = pedestal RGB, w = pad)
            vec4  shadows;          // offset  48  (xyz = shadows RGB,  w = pad)
            vec4  midtones;         // offset  64  (xyz = midtones RGB, w = pad)
            vec4  highlights;       // offset  80  (xyz = highlights RGB, w = pad)
            vec4  rescale;          // offset  96  (xyz = rescale RGB,  w = pad)
            int   gridEnabled;      // offset 112
            float gridSpacingRA;    // offset 116
            float gridSpacingDec;   // offset 120
            float gridLineWidth;    // offset 124
            vec2  imageSize;        // offset 128
            vec2  crPix;            // offset 136
            vec2  crVal;            // offset 144
            int   imgSource;        // offset 152: 0=processed, 1=rawMono, 2=rawBayer
            int   bayerPat;         // offset 156: offsetX + offsetY*65536
            // mat2 stored as 2 vec4 columns (std140 mat2 = 2 x vec4 = 32 bytes)
            vec4  cdCol0;           // offset 160
            vec4  cdCol1;           // offset 176
        } ubo;

        layout(set = 1, binding = 0) uniform sampler2D uChannel0;
        layout(set = 1, binding = 1) uniform sampler2D uChannel1;
        layout(set = 1, binding = 2) uniform sampler2D uChannel2;

        const float PI = 3.14159265358979323846;

        float mtf(float m, float v) {
            float c = clamp(v, 0.0, 1.0);
            if (v != c) return c;
            return (m - 1.0) * v / ((2.0 * m - 1.0) * v - m);
        }

        float stretchChannel(float raw, int ch) {
            float norm = raw * ubo.normFactor - ubo.pedestal[ch];
            float rescaled = (norm - ubo.shadows[ch]) * ubo.rescale[ch];
            return mtf(ubo.midtones[ch], rescaled);
        }

        float applyCurve(float v, float boost) {
            float bg = ubo.curvesMidpoint;
            float hp = 0.85;
            if (v <= 0.0 || v >= 1.0 || bg <= 0.0 || bg >= hp) return v;
            float sp = bg * (1.0 + 0.1 * boost);
            sp = min(sp, hp - 0.01);
            if (v <= sp) {
                float t = v / sp;
                float darkPower = 1.0 + boost * 3.0;
                return sp * pow(t, darkPower);
            } else if (v < hp) {
                float t = (v - sp) / (hp - sp);
                return sp + (hp - sp) * pow(t, 1.0 / (1.0 + boost));
            } else {
                return v;
            }
        }

        float applyHdr(float v, float amount, float knee) {
            if (v <= knee) return v;
            float range = 1.0 - knee;
            float t = (v - knee) / range;
            return knee + range * t / (1.0 + amount * t);
        }

        vec2 pixelToSky(vec2 pixel) {
            vec2 dp = pixel - ubo.crPix;
            mat2 cd = mat2(ubo.cdCol0.xy, ubo.cdCol1.xy);
            vec2 uv = cd * dp;
            float xi  = uv.x;
            float eta = uv.y;
            float rho = length(uv);
            float ra0 = ubo.crVal.x;
            float dec0 = ubo.crVal.y;
            float sinDec0 = sin(dec0);
            float cosDec0 = cos(dec0);
            if (rho < 1e-10) return ubo.crVal;
            float c = atan(rho);
            float sinC = sin(c);
            float cosC = cos(c);
            float dec = asin(cosC * sinDec0 + eta * sinC * cosDec0 / rho);
            float ra  = ra0 + atan(xi * sinC, rho * cosDec0 * cosC - eta * sinDec0 * sinC);
            return vec2(ra, dec);
        }

        float gridIntensity(vec2 pixel) {
            vec2 sky = pixelToSky(pixel);
            float ra  = sky.x;
            float dec = sky.y;
            // Distance from celestial pole, in radians (= colatitude).
            // Within ~5 arcmin of the pole, suppress grid lines: meridians all
            // converge to a single point and the inner declination circles get
            // arbitrarily small, creating a green moire that hides the sky.
            // The CPU-side WcsAnnotationLayer draws the pole crosshair + a 30'
            // reference ring on top, both well outside this 5' suppression
            // radius so they sit visibly inside the active grid.
            float poleDist = 1.5707963 - abs(dec);
            if (poleDist < 0.00145) {
                return 0.0;
            }
            float raGrid  = ra  / ubo.gridSpacingRA;
            float decGrid = dec / ubo.gridSpacingDec;
            float raFrac  = abs(raGrid  - round(raGrid))  * ubo.gridSpacingRA;
            float decFrac = abs(decGrid - round(decGrid)) * ubo.gridSpacingDec;
            float raWidth = ubo.gridLineWidth / max(cos(dec), 0.01);
            float raLine  = 1.0 - smoothstep(0.0, raWidth,          raFrac);
            float decLine = 1.0 - smoothstep(0.0, ubo.gridLineWidth, decFrac);
            return max(raLine, decLine);
        }

        // Bilinear Bayer demosaic from single-channel raw mosaic
        vec3 debayerBilinear(vec2 uv) {
            vec2 texSize = ubo.imageSize;
            vec2 pixCoord = uv * texSize - 0.5;
            ivec2 px = ivec2(floor(pixCoord));
            int offX = ubo.bayerPat % 65536;
            int offY = ubo.bayerPat / 65536;
            int bx = (px.x + offX) % 2;
            int by = (px.y + offY) % 2;

            float cc = texelFetch(uChannel0, px, 0).r;
            float n  = texelFetch(uChannel0, px + ivec2( 0,-1), 0).r;
            float s  = texelFetch(uChannel0, px + ivec2( 0, 1), 0).r;
            float e  = texelFetch(uChannel0, px + ivec2( 1, 0), 0).r;
            float w  = texelFetch(uChannel0, px + ivec2(-1, 0), 0).r;
            float ne = texelFetch(uChannel0, px + ivec2( 1,-1), 0).r;
            float nw = texelFetch(uChannel0, px + ivec2(-1,-1), 0).r;
            float se = texelFetch(uChannel0, px + ivec2( 1, 1), 0).r;
            float sw = texelFetch(uChannel0, px + ivec2(-1, 1), 0).r;

            float rr, gg, bb;
            if (bx == 0 && by == 0) {
                rr = cc;
                gg = (n + s + e + w) * 0.25;
                bb = (ne + nw + se + sw) * 0.25;
            } else if (bx == 1 && by == 1) {
                bb = cc;
                gg = (n + s + e + w) * 0.25;
                rr = (ne + nw + se + sw) * 0.25;
            } else if (bx == 1 && by == 0) {
                gg = cc;
                rr = (w + e) * 0.5;
                bb = (n + s) * 0.5;
            } else {
                gg = cc;
                bb = (w + e) * 0.5;
                rr = (n + s) * 0.5;
            }
            return vec3(rr, gg, bb);
        }

        void main() {
            int src = ubo.imgSource;
            float r, g, b;

            if (src == 2) {
                vec3 rgb = debayerBilinear(vTexCoord);
                r = rgb.r; g = rgb.g; b = rgb.b;
            } else if (src == 1 || ubo.channelCount < 3) {
                r = texture(uChannel0, vTexCoord).r;
                g = r; b = r;
            } else {
                r = texture(uChannel0, vTexCoord).r;
                g = texture(uChannel1, vTexCoord).r;
                b = texture(uChannel2, vTexCoord).r;
            }

            // Mono path
            if (src <= 1 && ubo.channelCount < 3) {
                if (ubo.stretchMode >= 1) {
                    r = stretchChannel(r, 0);
                }
                if (ubo.curvesBoost > 0.0) {
                    r = applyCurve(r, ubo.curvesBoost);
                }
                if (ubo.hdrAmount > 0.0) {
                    r = applyHdr(r, ubo.hdrAmount, ubo.hdrKnee);
                }
                if (ubo.gridEnabled != 0) {
                    vec2 pixel = vec2(vTexCoord.x * ubo.imageSize.x + 1.0,
                                     vTexCoord.y * ubo.imageSize.y + 1.0);
                    float grid = gridIntensity(pixel);
                    vec3 gridColor = vec3(0.0, 0.8, 0.0);
                    FragColor = vec4(
                        mix(r, gridColor.r, grid * 0.45),
                        mix(r, gridColor.g, grid * 0.45),
                        mix(r, gridColor.b, grid * 0.45),
                        1.0);
                } else {
                    FragColor = vec4(r, r, r, 1.0);
                }
                return;
            }

            // RGB path
            if (ubo.stretchMode == 1) {
                r = stretchChannel(r, 0);
                g = stretchChannel(g, 1);
                b = stretchChannel(b, 2);
            } else if (ubo.stretchMode == 2) {
                float nr = r * ubo.normFactor;
                float ng = g * ubo.normFactor;
                float nb = b * ubo.normFactor;
                float prr = nr - ubo.pedestal[0];
                float prg = ng - ubo.pedestal[1];
                float prb = nb - ubo.pedestal[2];
                float Ynorm = 0.2126 * prr + 0.7152 * prg + 0.0722 * prb;
                float rescaled = (Ynorm - ubo.shadows.x) * ubo.rescale.x;
                float Yp = mtf(ubo.midtones.x, rescaled);
                float scale = Ynorm > 1e-7 ? Yp / Ynorm : 0.0;
                float maxCh = max(prr, max(prg, prb));
                if (maxCh > 1e-7) scale = min(scale, 1.0 / maxCh);
                r = clamp(prr * scale, 0.0, 1.0);
                g = clamp(prg * scale, 0.0, 1.0);
                b = clamp(prb * scale, 0.0, 1.0);
            }

            if (ubo.curvesBoost > 0.0) {
                r = applyCurve(r, ubo.curvesBoost);
                g = applyCurve(g, ubo.curvesBoost);
                b = applyCurve(b, ubo.curvesBoost);
            }
            if (ubo.hdrAmount > 0.0) {
                r = applyHdr(r, ubo.hdrAmount, ubo.hdrKnee);
                g = applyHdr(g, ubo.hdrAmount, ubo.hdrKnee);
                b = applyHdr(b, ubo.hdrAmount, ubo.hdrKnee);
            }

            if (ubo.gridEnabled != 0) {
                vec2 pixel = vec2(vTexCoord.x * ubo.imageSize.x + 1.0,
                                 vTexCoord.y * ubo.imageSize.y + 1.0);
                float grid = gridIntensity(pixel);
                vec3 gridColor = vec3(0.55, 0.85, 0.95);
                r = mix(r, gridColor.r, grid * 0.45);
                g = mix(g, gridColor.g, grid * 0.45);
                b = mix(b, gridColor.b, grid * 0.45);
            }

            FragColor = vec4(r, g, b, 1.0);
        }
        """;

    private const string HistogramFragmentSource = """
        #version 450

        layout(location = 0) in vec2 vTexCoord;
        layout(location = 0) out vec4 FragColor;

        layout(set = 0, binding = 0, std140) uniform HistogramUBO {
            int   channelCount; // offset  0
            float logPeak;      // offset  4
            float linearPeak;   // offset  8
            int   logScale;     // offset 12
        } ubo;

        layout(set = 1, binding = 0) uniform sampler2D uHist0;
        layout(set = 1, binding = 1) uniform sampler2D uHist1;
        layout(set = 1, binding = 2) uniform sampler2D uHist2;

        float scaleValue(float v) {
            if (ubo.logScale != 0) {
                return log(1.0 + v) / ubo.logPeak;
            } else {
                return v / ubo.linearPeak;
            }
        }

        void main() {
            float x = vTexCoord.x;
            float y = 1.0 - vTexCoord.y; // Flip Y: Vulkan UV has 0 at top, histogram needs 0 at bottom

            if (ubo.logPeak <= 0.0) {
                FragColor = vec4(0.0);
                return;
            }

            float n0 = scaleValue(texture(uHist0, vec2(x, 0.5)).r);

            vec3 color = vec3(0.0);
            float alpha = 0.0;

            if (ubo.channelCount >= 3) {
                float n1 = scaleValue(texture(uHist1, vec2(x, 0.5)).r);
                float n2 = scaleValue(texture(uHist2, vec2(x, 0.5)).r);
                if (y <= n0) { color.r += 0.85; alpha = max(alpha, 0.7); }
                if (y <= n1) { color.g += 0.85; alpha = max(alpha, 0.7); }
                if (y <= n2) { color.b += 0.85; alpha = max(alpha, 0.7); }
            } else {
                if (y <= n0) { color = vec3(0.8); alpha = 0.7; }
            }

            FragColor = vec4(color, alpha);
        }
        """;

    // ------------------------------------------------------------------ Vulkan objects

    private readonly VulkanContext _ctx;

    // Descriptor set layouts (shared by both pipelines)
    private VkDescriptorSetLayout _uboSetLayout;       // set 0: UBO
    private VkDescriptorSetLayout _samplerSetLayout;   // set 1: 3x samplers

    // Descriptor pool
    private VkDescriptorPool _descriptorPool;

    // Descriptor sets: [0] = image UBO, [1] = histogram UBO, [2] = image samplers, [3] = histogram samplers
    private VkDescriptorSet _imageUboSet;
    private VkDescriptorSet _histogramUboSet;
    private VkDescriptorSet _imageSamplerSet;
    private VkDescriptorSet _histogramSamplerSet;

    // Shared pipeline layout
    private VkPipelineLayout _pipelineLayout;

    // Pipelines
    private VkPipeline _imagePipeline;
    private VkPipeline _histogramPipeline;

    // Shared sampler
    private VkSampler _linearSampler;

    // Channel textures (3x R32_SFLOAT 2D)
    private readonly VkImage[] _channelImages = new VkImage[ChannelCount];
    private readonly VkDeviceMemory[] _channelMemories = new VkDeviceMemory[ChannelCount];
    private readonly VkImageView[] _channelViews = new VkImageView[ChannelCount];
    private readonly int[] _channelWidth = new int[ChannelCount];
    private readonly int[] _channelHeight = new int[ChannelCount];

    // Histogram textures (3x R32F stored as 512×1 2D)
    private readonly VkImage[] _histImages = new VkImage[ChannelCount];
    private readonly VkDeviceMemory[] _histMemories = new VkDeviceMemory[ChannelCount];
    private readonly VkImageView[] _histViews = new VkImageView[ChannelCount];

    // Stretch UBO buffer (persistently mapped)
    private VkBuffer _stretchUboBuffer;
    private VkDeviceMemory _stretchUboMemory;
    private byte* _stretchUboMapped;

    // Histogram UBO buffer (persistently mapped)
    private VkBuffer _histogramUboBuffer;
    private VkDeviceMemory _histogramUboMemory;
    private byte* _histogramUboMapped;

    // Staging buffer for texture uploads
    private VkBuffer _stagingBuffer;
    private VkDeviceMemory _stagingMemory;
    private ulong _stagingSize;

    private bool _disposed;

    // ------------------------------------------------------------------ Constructor

    public VkFitsImagePipeline(VulkanContext ctx)
    {
        _ctx = ctx;

        CreateDescriptorSetLayouts();
        CreateDescriptorPool();
        AllocateDescriptorSets();
        CreatePipelineLayout();
        CreateSampler();
        CreateUboBuffers();
        CreatePlaceholderTextures();
        CreatePipelines();
    }

    // ------------------------------------------------------------------ Public API

    /// <summary>
    /// Uploads a R32_SFLOAT 2D image into channel slot <paramref name="channel"/> (0-based).
    /// Creates or recreates the texture if dimensions changed.
    /// </summary>
    public void UploadChannelTexture(ReadOnlySpan<float> data, int channel, int width, int height)
    {
        if ((uint)channel >= ChannelCount)
            throw new ArgumentOutOfRangeException(nameof(channel));

        var byteSize = (ulong)(data.Length * sizeof(float));
        EnsureStagingBuffer(byteSize);
        CopyToStaging(data, byteSize);

        if (_channelWidth[channel] != width || _channelHeight[channel] != height)
        {
            DestroyChannelTexture(channel);
            CreateChannelTexture(channel, width, height);
            BindChannelSampler(channel, _channelViews[channel], _imageSamplerSet);
        }

        UploadToImage(_channelImages[channel], (uint)width, (uint)height, byteSize, VkFormat.R32Sfloat);
        _channelWidth[channel] = width;
        _channelHeight[channel] = height;
    }

    /// <summary>
    /// Uploads 512 R32F histogram bins into histogram slot <paramref name="channel"/> (0-based).
    /// The data is stored as a 512×1 2D texture.
    /// </summary>
    public void UploadHistogramTexture(ReadOnlySpan<float> data, int channel)
    {
        if ((uint)channel >= ChannelCount)
            throw new ArgumentOutOfRangeException(nameof(channel));

        var byteSize = (ulong)(data.Length * sizeof(float));
        EnsureStagingBuffer(byteSize);
        CopyToStaging(data, byteSize);
        UploadToImage(_histImages[channel], HistogramBins, 1, byteSize, VkFormat.R32Sfloat);
    }

    /// <summary>
    /// Writes all stretch parameters into the persistently-mapped stretch UBO.
    /// The <paramref name="cmd"/> parameter is unused (coherent memory, no flush needed) but
    /// kept for API symmetry with future non-coherent implementations.
    /// </summary>
    /// <summary>Image source mode for the fragment shader.</summary>
    public enum ImageSource
    {
        /// <summary>Pre-debayered channels (existing path: 1-3 separate R32F textures).</summary>
        ProcessedChannels = 0,
        /// <summary>Raw mono: single R32F texture, no debayer needed.</summary>
        RawMono = 1,
        /// <summary>Raw Bayer mosaic: single R32F texture, bilinear debayer in shader.</summary>
        RawBayer = 2,
    }

    public void UpdateStretchUBO(
        VkCommandBuffer cmd,
        int channelCount, int stretchMode, float normFactor,
        float curvesBoost, float curvesMidpoint, float hdrAmount, float hdrKnee,
        (float R, float G, float B) pedestal,
        (float R, float G, float B) shadows,
        (float R, float G, float B) midtones,
        (float R, float G, float B) highlights,
        (float R, float G, float B) rescale,
        bool gridEnabled, float gridSpacingRA, float gridSpacingDec, float gridLineWidth,
        float imageW, float imageH, float crPix1, float crPix2,
        float crValRA, float crValDec,
        ReadOnlySpan<float> cdMatrix,
        ImageSource imageSource = ImageSource.ProcessedChannels,
        int bayerOffsetX = 0, int bayerOffsetY = 0)
    {
        var p = _stretchUboMapped;

        WriteInt(p, 0, channelCount);
        WriteInt(p, 4, stretchMode);
        WriteFloat(p, 8, normFactor);
        WriteFloat(p, 12, curvesBoost);
        WriteFloat(p, 16, curvesMidpoint);
        WriteFloat(p, 20, hdrAmount);
        WriteFloat(p, 24, hdrKnee);
        WriteFloat(p, 28, 0f);                  // _pad0

        // pedestal (vec4 at offset 32)
        WriteFloat(p, 32, pedestal.R);
        WriteFloat(p, 36, pedestal.G);
        WriteFloat(p, 40, pedestal.B);
        WriteFloat(p, 44, 0f);

        // shadows (vec4 at offset 48)
        WriteFloat(p, 48, shadows.R);
        WriteFloat(p, 52, shadows.G);
        WriteFloat(p, 56, shadows.B);
        WriteFloat(p, 60, 0f);

        // midtones (vec4 at offset 64)
        WriteFloat(p, 64, midtones.R);
        WriteFloat(p, 68, midtones.G);
        WriteFloat(p, 72, midtones.B);
        WriteFloat(p, 76, 0f);

        // highlights (vec4 at offset 80)
        WriteFloat(p, 80, highlights.R);
        WriteFloat(p, 84, highlights.G);
        WriteFloat(p, 88, highlights.B);
        WriteFloat(p, 92, 0f);

        // rescale (vec4 at offset 96)
        WriteFloat(p, 96, rescale.R);
        WriteFloat(p, 100, rescale.G);
        WriteFloat(p, 104, rescale.B);
        WriteFloat(p, 108, 0f);

        WriteInt(p, 112, gridEnabled ? 1 : 0);
        WriteFloat(p, 116, gridSpacingRA);
        WriteFloat(p, 120, gridSpacingDec);
        WriteFloat(p, 124, gridLineWidth);

        // imageSize (vec2 at offset 128)
        WriteFloat(p, 128, imageW);
        WriteFloat(p, 132, imageH);

        // crPix (vec2 at offset 136)
        WriteFloat(p, 136, crPix1);
        WriteFloat(p, 140, crPix2);

        // crVal (vec2 at offset 144)
        WriteFloat(p, 144, crValRA);
        WriteFloat(p, 148, crValDec);

        WriteInt(p, 152, (int)imageSource);
        WriteInt(p, 156, bayerOffsetX + bayerOffsetY * 65536);

        // cdMatrix stored col-major as 2 vec4s:
        // cdCol0 at offset 160: (cd[0,0], cd[1,0], 0, 0)
        // cdCol1 at offset 176: (cd[0,1], cd[1,1], 0, 0)
        var cd00 = cdMatrix.Length > 0 ? cdMatrix[0] : 0f;
        var cd10 = cdMatrix.Length > 1 ? cdMatrix[1] : 0f;
        var cd01 = cdMatrix.Length > 2 ? cdMatrix[2] : 0f;
        var cd11 = cdMatrix.Length > 3 ? cdMatrix[3] : 0f;

        WriteFloat(p, 160, cd00);
        WriteFloat(p, 164, cd10);
        WriteFloat(p, 168, 0f);
        WriteFloat(p, 172, 0f);

        WriteFloat(p, 176, cd01);
        WriteFloat(p, 180, cd11);
        WriteFloat(p, 184, 0f);
        WriteFloat(p, 188, 0f);
    }

    /// <summary>
    /// Writes histogram parameters into the persistently-mapped histogram UBO.
    /// </summary>
    public void UpdateHistogramUBO(
        VkCommandBuffer cmd,
        int channelCount, float logPeak, float linearPeak, bool logScale)
    {
        var p = _histogramUboMapped;
        WriteInt(p, 0, channelCount);
        WriteFloat(p, 4, logPeak);
        WriteFloat(p, 8, linearPeak);
        WriteInt(p, 12, logScale ? 1 : 0);
    }

    /// <summary>
    /// Records the image quad draw into <paramref name="cmd"/>.
    /// Binds the image pipeline, descriptor sets, push constants, quad vertices, and calls vkCmdDraw.
    /// </summary>
    public void RecordImageDraw(
        VkCommandBuffer cmd,
        VulkanContext ctx,
        float left, float top, float right, float bottom,
        float projW, float projH)
    {
        var api = ctx.DeviceApi;

        api.vkCmdBindPipeline(cmd, VkPipelineBindPoint.Graphics, _imagePipeline);

        // Bind set 0 (UBO) and set 1 (samplers)
        var uboSet = _imageUboSet;
        var samplerSet = _imageSamplerSet;
        api.vkCmdBindDescriptorSets(cmd, VkPipelineBindPoint.Graphics, _pipelineLayout,
            0, 1, &uboSet, 0, null);
        api.vkCmdBindDescriptorSets(cmd, VkPipelineBindPoint.Graphics, _pipelineLayout,
            1, 1, &samplerSet, 0, null);

        PushProjectionAndDraw(cmd, ctx, left, top, right, bottom, projW, projH);
    }

    /// <summary>
    /// Records the histogram quad draw into <paramref name="cmd"/>.
    /// </summary>
    public void RecordHistogramDraw(
        VkCommandBuffer cmd,
        VulkanContext ctx,
        float left, float top, float right, float bottom,
        float projW, float projH)
    {
        var api = ctx.DeviceApi;

        api.vkCmdBindPipeline(cmd, VkPipelineBindPoint.Graphics, _histogramPipeline);

        var uboSet = _histogramUboSet;
        var samplerSet = _histogramSamplerSet;
        api.vkCmdBindDescriptorSets(cmd, VkPipelineBindPoint.Graphics, _pipelineLayout,
            0, 1, &uboSet, 0, null);
        api.vkCmdBindDescriptorSets(cmd, VkPipelineBindPoint.Graphics, _pipelineLayout,
            1, 1, &samplerSet, 0, null);

        PushProjectionAndDraw(cmd, ctx, left, top, right, bottom, projW, projH);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var api = _ctx.DeviceApi;

        api.vkDeviceWaitIdle();

        // Pipelines
        if (_imagePipeline != VkPipeline.Null)
            api.vkDestroyPipeline(_imagePipeline);
        if (_histogramPipeline != VkPipeline.Null)
            api.vkDestroyPipeline(_histogramPipeline);

        // Pipeline layout
        if (_pipelineLayout != VkPipelineLayout.Null)
            api.vkDestroyPipelineLayout(_pipelineLayout);

        // Descriptor set layouts
        if (_uboSetLayout != VkDescriptorSetLayout.Null)
            api.vkDestroyDescriptorSetLayout(_uboSetLayout);
        if (_samplerSetLayout != VkDescriptorSetLayout.Null)
            api.vkDestroyDescriptorSetLayout(_samplerSetLayout);

        // Descriptor pool
        if (_descriptorPool != VkDescriptorPool.Null)
            api.vkDestroyDescriptorPool(_descriptorPool);

        // Sampler
        if (_linearSampler != VkSampler.Null)
            api.vkDestroySampler(_linearSampler);

        // Channel textures
        for (var i = 0; i < ChannelCount; i++)
        {
            DestroyChannelTexture(i);
            DestroyHistogramTexture(i);
        }

        // UBO buffers
        if (_stretchUboBuffer != VkBuffer.Null)
        {
            api.vkUnmapMemory(_stretchUboMemory);
            api.vkDestroyBuffer(_stretchUboBuffer);
            api.vkFreeMemory(_stretchUboMemory);
        }
        if (_histogramUboBuffer != VkBuffer.Null)
        {
            api.vkUnmapMemory(_histogramUboMemory);
            api.vkDestroyBuffer(_histogramUboBuffer);
            api.vkFreeMemory(_histogramUboMemory);
        }

        // Staging buffer
        if (_stagingBuffer != VkBuffer.Null)
        {
            api.vkDestroyBuffer(_stagingBuffer);
            api.vkFreeMemory(_stagingMemory);
        }
    }

    // ------------------------------------------------------------------ Private helpers

    private void CreateDescriptorSetLayouts()
    {
        var api = _ctx.DeviceApi;

        // Set 0: single UBO binding (vertex + fragment)
        VkDescriptorSetLayoutBinding uboBinding = new()
        {
            binding = 0,
            descriptorType = VkDescriptorType.UniformBuffer,
            descriptorCount = 1,
            stageFlags = VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment
        };
        VkDescriptorSetLayoutCreateInfo uboLayoutCI = new()
        {
            bindingCount = 1,
            pBindings = &uboBinding
        };
        api.vkCreateDescriptorSetLayout(&uboLayoutCI, null, out _uboSetLayout).CheckResult();

        // Set 1: 3x combined image sampler bindings (fragment only)
        var samplerBindings = stackalloc VkDescriptorSetLayoutBinding[ChannelCount];
        for (uint i = 0; i < ChannelCount; i++)
        {
            samplerBindings[i] = new VkDescriptorSetLayoutBinding
            {
                binding = i,
                descriptorType = VkDescriptorType.CombinedImageSampler,
                descriptorCount = 1,
                stageFlags = VkShaderStageFlags.Fragment
            };
        }
        VkDescriptorSetLayoutCreateInfo samplerLayoutCI = new()
        {
            bindingCount = ChannelCount,
            pBindings = samplerBindings
        };
        api.vkCreateDescriptorSetLayout(&samplerLayoutCI, null, out _samplerSetLayout).CheckResult();
    }

    private void CreateDescriptorPool()
    {
        var api = _ctx.DeviceApi;

        // 2 UBO descriptors (image + histogram) + 6 sampler descriptors (3 image + 3 histogram)
        var poolSizes = stackalloc VkDescriptorPoolSize[2];
        poolSizes[0] = new VkDescriptorPoolSize
        {
            type = VkDescriptorType.UniformBuffer,
            descriptorCount = 2
        };
        poolSizes[1] = new VkDescriptorPoolSize
        {
            type = VkDescriptorType.CombinedImageSampler,
            descriptorCount = 6
        };

        VkDescriptorPoolCreateInfo dpCI = new()
        {
            maxSets = 4, // imageUBO + histUBO + imageSamplers + histSamplers
            poolSizeCount = 2,
            pPoolSizes = poolSizes
        };
        api.vkCreateDescriptorPool(&dpCI, null, out _descriptorPool).CheckResult();
    }

    private void AllocateDescriptorSets()
    {
        var api = _ctx.DeviceApi;

        // Allocate all 4 sets at once
        var layouts = stackalloc VkDescriptorSetLayout[4];
        layouts[0] = _uboSetLayout;       // image UBO
        layouts[1] = _uboSetLayout;       // histogram UBO
        layouts[2] = _samplerSetLayout;   // image samplers
        layouts[3] = _samplerSetLayout;   // histogram samplers

        var sets = stackalloc VkDescriptorSet[4];
        VkDescriptorSetAllocateInfo dsAI = new()
        {
            descriptorPool = _descriptorPool,
            descriptorSetCount = 4,
            pSetLayouts = layouts
        };
        api.vkAllocateDescriptorSets(&dsAI, sets).CheckResult();

        _imageUboSet = sets[0];
        _histogramUboSet = sets[1];
        _imageSamplerSet = sets[2];
        _histogramSamplerSet = sets[3];
    }

    private void CreatePipelineLayout()
    {
        var api = _ctx.DeviceApi;

        // Push constant: mat4 (64 bytes), vertex stage only
        VkPushConstantRange pushRange = new()
        {
            stageFlags = VkShaderStageFlags.Vertex,
            offset = 0,
            size = 64
        };

        var setLayouts = stackalloc VkDescriptorSetLayout[2];
        setLayouts[0] = _uboSetLayout;
        setLayouts[1] = _samplerSetLayout;

        VkPipelineLayoutCreateInfo plCI = new()
        {
            setLayoutCount = 2,
            pSetLayouts = setLayouts,
            pushConstantRangeCount = 1,
            pPushConstantRanges = &pushRange
        };
        api.vkCreatePipelineLayout(&plCI, null, out _pipelineLayout).CheckResult();
    }

    private void CreateSampler()
    {
        VkSamplerCreateInfo samplerCI = new()
        {
            magFilter = VkFilter.Nearest,
            minFilter = VkFilter.Linear,
            addressModeU = VkSamplerAddressMode.ClampToEdge,
            addressModeV = VkSamplerAddressMode.ClampToEdge,
            addressModeW = VkSamplerAddressMode.ClampToEdge,
            mipmapMode = VkSamplerMipmapMode.Linear,
            maxLod = 1.0f
        };
        _ctx.DeviceApi.vkCreateSampler(&samplerCI, null, out _linearSampler).CheckResult();
    }

    private void CreateUboBuffers()
    {
        CreateMappedBuffer(StretchUboSize, out _stretchUboBuffer, out _stretchUboMemory, out _stretchUboMapped);
        CreateMappedBuffer(HistogramUboSize, out _histogramUboBuffer, out _histogramUboMemory, out _histogramUboMapped);

        // Write initial UBO descriptors
        BindUboDescriptor(_imageUboSet, _stretchUboBuffer, StretchUboSize);
        BindUboDescriptor(_histogramUboSet, _histogramUboBuffer, HistogramUboSize);
    }

    private void CreateMappedBuffer(int size, out VkBuffer buffer, out VkDeviceMemory memory, out byte* mapped)
    {
        var api = _ctx.DeviceApi;
        var usize = (ulong)size;

        VkBufferCreateInfo bufCI = new()
        {
            size = usize,
            usage = VkBufferUsageFlags.UniformBuffer,
            sharingMode = VkSharingMode.Exclusive
        };
        api.vkCreateBuffer(&bufCI, null, out buffer).CheckResult();

        api.vkGetBufferMemoryRequirements(buffer, out var memReqs);
        VkMemoryAllocateInfo allocInfo = new()
        {
            allocationSize = memReqs.size,
            memoryTypeIndex = _ctx.FindMemoryType(memReqs.memoryTypeBits,
                VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent)
        };
        api.vkAllocateMemory(&allocInfo, null, out memory).CheckResult();
        api.vkBindBufferMemory(buffer, memory, 0);

        void* ptr;
        api.vkMapMemory(memory, 0, usize, 0, &ptr);
        mapped = (byte*)ptr;

        // Zero-initialise
        new Span<byte>(ptr, size).Clear();
    }

    private void BindUboDescriptor(VkDescriptorSet set, VkBuffer buffer, int size)
    {
        var api = _ctx.DeviceApi;
        VkDescriptorBufferInfo bufInfo = new()
        {
            buffer = buffer,
            offset = 0,
            range = (ulong)size
        };
        VkWriteDescriptorSet write = new()
        {
            dstSet = set,
            dstBinding = 0,
            dstArrayElement = 0,
            descriptorType = VkDescriptorType.UniformBuffer,
            descriptorCount = 1,
            pBufferInfo = &bufInfo
        };
        api.vkUpdateDescriptorSets(1, &write, 0, null);
    }

    private void CreatePlaceholderTextures()
    {
        // Create 1×1 placeholder images so descriptor sets are always valid
        var placeholder = new float[] { 0f };
        var byteSize = (ulong)(placeholder.Length * sizeof(float));
        EnsureStagingBuffer(byteSize);
        CopyToStaging(placeholder.AsSpan(), byteSize);

        for (var i = 0; i < ChannelCount; i++)
        {
            CreateChannelTexture(i, 1, 1);
            UploadToImage(_channelImages[i], 1, 1, byteSize, VkFormat.R32Sfloat);
            _channelWidth[i] = 1;
            _channelHeight[i] = 1;
            BindChannelSampler(i, _channelViews[i], _imageSamplerSet);

            CreateHistogramTexture(i);
            UploadToImage(_histImages[i], HistogramBins, 1, byteSize, VkFormat.R32Sfloat);
            BindChannelSampler(i, _histViews[i], _histogramSamplerSet);
        }
    }

    private void CreateChannelTexture(int channel, int width, int height)
    {
        var api = _ctx.DeviceApi;

        VkImageCreateInfo imageCI = new()
        {
            imageType = VkImageType.Image2D,
            format = VkFormat.R32Sfloat,
            extent = new VkExtent3D((uint)width, (uint)height, 1),
            mipLevels = 1,
            arrayLayers = 1,
            samples = VkSampleCountFlags.Count1,
            tiling = VkImageTiling.Optimal,
            usage = VkImageUsageFlags.TransferDst | VkImageUsageFlags.Sampled,
            sharingMode = VkSharingMode.Exclusive,
            initialLayout = VkImageLayout.Undefined
        };
        api.vkCreateImage(&imageCI, null, out _channelImages[channel]).CheckResult();

        api.vkGetImageMemoryRequirements(_channelImages[channel], out var memReqs);
        VkMemoryAllocateInfo allocInfo = new()
        {
            allocationSize = memReqs.size,
            memoryTypeIndex = _ctx.FindMemoryType(memReqs.memoryTypeBits, VkMemoryPropertyFlags.DeviceLocal)
        };
        api.vkAllocateMemory(&allocInfo, null, out _channelMemories[channel]).CheckResult();
        api.vkBindImageMemory(_channelImages[channel], _channelMemories[channel], 0);

        _ctx.ExecuteOneShot(cmd =>
            TransitionImageLayout(cmd, _channelImages[channel],
                VkImageLayout.Undefined, VkImageLayout.ShaderReadOnlyOptimal));

        var viewCI = new VkImageViewCreateInfo(
            _channelImages[channel], VkImageViewType.Image2D, VkFormat.R32Sfloat,
            VkComponentMapping.Rgba,
            new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1));
        api.vkCreateImageView(&viewCI, null, out _channelViews[channel]).CheckResult();
    }

    private void CreateHistogramTexture(int channel)
    {
        var api = _ctx.DeviceApi;

        VkImageCreateInfo imageCI = new()
        {
            imageType = VkImageType.Image2D,
            format = VkFormat.R32Sfloat,
            extent = new VkExtent3D(HistogramBins, 1, 1),
            mipLevels = 1,
            arrayLayers = 1,
            samples = VkSampleCountFlags.Count1,
            tiling = VkImageTiling.Optimal,
            usage = VkImageUsageFlags.TransferDst | VkImageUsageFlags.Sampled,
            sharingMode = VkSharingMode.Exclusive,
            initialLayout = VkImageLayout.Undefined
        };
        api.vkCreateImage(&imageCI, null, out _histImages[channel]).CheckResult();

        api.vkGetImageMemoryRequirements(_histImages[channel], out var memReqs);
        VkMemoryAllocateInfo allocInfo = new()
        {
            allocationSize = memReqs.size,
            memoryTypeIndex = _ctx.FindMemoryType(memReqs.memoryTypeBits, VkMemoryPropertyFlags.DeviceLocal)
        };
        api.vkAllocateMemory(&allocInfo, null, out _histMemories[channel]).CheckResult();
        api.vkBindImageMemory(_histImages[channel], _histMemories[channel], 0);

        _ctx.ExecuteOneShot(cmd =>
            TransitionImageLayout(cmd, _histImages[channel],
                VkImageLayout.Undefined, VkImageLayout.ShaderReadOnlyOptimal));

        var viewCI = new VkImageViewCreateInfo(
            _histImages[channel], VkImageViewType.Image2D, VkFormat.R32Sfloat,
            VkComponentMapping.Rgba,
            new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1));
        api.vkCreateImageView(&viewCI, null, out _histViews[channel]).CheckResult();
    }

    private void DestroyChannelTexture(int channel)
    {
        var api = _ctx.DeviceApi;
        if (_channelViews[channel] != VkImageView.Null)
        {
            api.vkDestroyImageView(_channelViews[channel]);
            _channelViews[channel] = VkImageView.Null;
        }
        if (_channelImages[channel] != VkImage.Null)
        {
            api.vkDestroyImage(_channelImages[channel]);
            _channelImages[channel] = VkImage.Null;
        }
        if (_channelMemories[channel] != VkDeviceMemory.Null)
        {
            api.vkFreeMemory(_channelMemories[channel]);
            _channelMemories[channel] = VkDeviceMemory.Null;
        }
        _channelWidth[channel] = 0;
        _channelHeight[channel] = 0;
    }

    private void DestroyHistogramTexture(int channel)
    {
        var api = _ctx.DeviceApi;
        if (_histViews[channel] != VkImageView.Null)
        {
            api.vkDestroyImageView(_histViews[channel]);
            _histViews[channel] = VkImageView.Null;
        }
        if (_histImages[channel] != VkImage.Null)
        {
            api.vkDestroyImage(_histImages[channel]);
            _histImages[channel] = VkImage.Null;
        }
        if (_histMemories[channel] != VkDeviceMemory.Null)
        {
            api.vkFreeMemory(_histMemories[channel]);
            _histMemories[channel] = VkDeviceMemory.Null;
        }
    }

    private void BindChannelSampler(int channel, VkImageView view, VkDescriptorSet set)
    {
        var api = _ctx.DeviceApi;
        VkDescriptorImageInfo imageInfo = new()
        {
            imageLayout = VkImageLayout.ShaderReadOnlyOptimal,
            imageView = view,
            sampler = _linearSampler
        };
        VkWriteDescriptorSet write = new()
        {
            dstSet = set,
            dstBinding = (uint)channel,
            dstArrayElement = 0,
            descriptorType = VkDescriptorType.CombinedImageSampler,
            descriptorCount = 1,
            pImageInfo = &imageInfo
        };
        api.vkUpdateDescriptorSets(1, &write, 0, null);
    }

    private void EnsureStagingBuffer(ulong size)
    {
        if (_stagingBuffer != VkBuffer.Null && _stagingSize >= size)
            return;

        var api = _ctx.DeviceApi;

        if (_stagingBuffer != VkBuffer.Null)
        {
            api.vkDestroyBuffer(_stagingBuffer);
            api.vkFreeMemory(_stagingMemory);
        }

        VkBufferCreateInfo bufCI = new()
        {
            size = size,
            usage = VkBufferUsageFlags.TransferSrc,
            sharingMode = VkSharingMode.Exclusive
        };
        api.vkCreateBuffer(&bufCI, null, out _stagingBuffer).CheckResult();

        api.vkGetBufferMemoryRequirements(_stagingBuffer, out var memReqs);
        VkMemoryAllocateInfo allocInfo = new()
        {
            allocationSize = memReqs.size,
            memoryTypeIndex = _ctx.FindMemoryType(memReqs.memoryTypeBits,
                VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent)
        };
        api.vkAllocateMemory(&allocInfo, null, out _stagingMemory).CheckResult();
        api.vkBindBufferMemory(_stagingBuffer, _stagingMemory, 0);
        _stagingSize = size;
    }

    private void CopyToStaging(ReadOnlySpan<float> data, ulong byteSize)
    {
        var api = _ctx.DeviceApi;
        void* mapped;
        api.vkMapMemory(_stagingMemory, 0, byteSize, 0, &mapped);
        fixed (float* pSrc = data)
            Buffer.MemoryCopy(pSrc, mapped, (long)byteSize, (long)byteSize);
        api.vkUnmapMemory(_stagingMemory);
    }

    private void UploadToImage(VkImage image, uint width, uint height, ulong byteSize, VkFormat format)
    {
        _ctx.ExecuteOneShot(cmd =>
        {
            TransitionImageLayout(cmd, image,
                VkImageLayout.ShaderReadOnlyOptimal, VkImageLayout.TransferDstOptimal);

            VkBufferImageCopy region = new()
            {
                bufferOffset = 0,
                bufferRowLength = 0,
                bufferImageHeight = 0,
                imageSubresource = new VkImageSubresourceLayers(VkImageAspectFlags.Color, 0, 0, 1),
                imageOffset = new VkOffset3D(0, 0, 0),
                imageExtent = new VkExtent3D(width, height, 1)
            };
            _ctx.DeviceApi.vkCmdCopyBufferToImage(cmd, _stagingBuffer, image,
                VkImageLayout.TransferDstOptimal, 1, &region);

            TransitionImageLayout(cmd, image,
                VkImageLayout.TransferDstOptimal, VkImageLayout.ShaderReadOnlyOptimal);
        });
    }

    private void TransitionImageLayout(VkCommandBuffer cmd, VkImage image,
        VkImageLayout oldLayout, VkImageLayout newLayout)
    {
        VkImageMemoryBarrier barrier = new()
        {
            oldLayout = oldLayout,
            newLayout = newLayout,
            srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            image = image,
            subresourceRange = new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1)
        };

        VkPipelineStageFlags srcStage, dstStage;

        if (oldLayout == VkImageLayout.Undefined && newLayout == VkImageLayout.TransferDstOptimal)
        {
            barrier.srcAccessMask = 0;
            barrier.dstAccessMask = VkAccessFlags.TransferWrite;
            srcStage = VkPipelineStageFlags.TopOfPipe;
            dstStage = VkPipelineStageFlags.Transfer;
        }
        else if (oldLayout == VkImageLayout.TransferDstOptimal && newLayout == VkImageLayout.ShaderReadOnlyOptimal)
        {
            barrier.srcAccessMask = VkAccessFlags.TransferWrite;
            barrier.dstAccessMask = VkAccessFlags.ShaderRead;
            srcStage = VkPipelineStageFlags.Transfer;
            dstStage = VkPipelineStageFlags.FragmentShader;
        }
        else if (oldLayout == VkImageLayout.ShaderReadOnlyOptimal && newLayout == VkImageLayout.TransferDstOptimal)
        {
            barrier.srcAccessMask = VkAccessFlags.ShaderRead;
            barrier.dstAccessMask = VkAccessFlags.TransferWrite;
            srcStage = VkPipelineStageFlags.FragmentShader;
            dstStage = VkPipelineStageFlags.Transfer;
        }
        else if (oldLayout == VkImageLayout.Undefined && newLayout == VkImageLayout.ShaderReadOnlyOptimal)
        {
            barrier.srcAccessMask = 0;
            barrier.dstAccessMask = VkAccessFlags.ShaderRead;
            srcStage = VkPipelineStageFlags.TopOfPipe;
            dstStage = VkPipelineStageFlags.FragmentShader;
        }
        else
        {
            throw new ArgumentException($"Unsupported layout transition: {oldLayout} -> {newLayout}");
        }

        _ctx.DeviceApi.vkCmdPipelineBarrier(cmd, srcStage, dstStage, 0,
            0, null, 0, null, 1, &barrier);
    }

    private void CreatePipelines()
    {
        using var compiler = new Compiler();

        var vertModule = CompileShaderModule(compiler, QuadVertexSource, "quad.vert", ShaderKind.VertexShader);
        var imageFragModule = CompileShaderModule(compiler, ImageFragmentSource, "image.frag", ShaderKind.FragmentShader);
        var histFragModule = CompileShaderModule(compiler, HistogramFragmentSource, "histogram.frag", ShaderKind.FragmentShader);

        try
        {
            VkVertexInputBindingDescription binding = new(4 * sizeof(float));
            var attrs = stackalloc VkVertexInputAttributeDescription[2];
            attrs[0] = new VkVertexInputAttributeDescription(0, VkFormat.R32G32Sfloat, 0);
            attrs[1] = new VkVertexInputAttributeDescription(1, VkFormat.R32G32Sfloat, 2 * sizeof(float));

            _imagePipeline = CreateGraphicsPipeline(
                vertModule, imageFragModule, &binding, 1, attrs, 2);
            _histogramPipeline = CreateGraphicsPipeline(
                vertModule, histFragModule, &binding, 1, attrs, 2);
        }
        finally
        {
            var api = _ctx.DeviceApi;
            api.vkDestroyShaderModule(vertModule);
            api.vkDestroyShaderModule(imageFragModule);
            api.vkDestroyShaderModule(histFragModule);
        }
    }

    private VkPipeline CreateGraphicsPipeline(
        VkShaderModule vertModule, VkShaderModule fragModule,
        VkVertexInputBindingDescription* bindings, uint bindingCount,
        VkVertexInputAttributeDescription* attributes, uint attributeCount)
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

        VkPipelineInputAssemblyStateCreateInfo inputAssembly = new(VkPrimitiveTopology.TriangleList);
        VkPipelineViewportStateCreateInfo viewportState = new(1, 1);

        VkPipelineRasterizationStateCreateInfo rasterizer = new()
        {
            polygonMode = VkPolygonMode.Fill,
            lineWidth = 1.0f,
            cullMode = VkCullModeFlags.None,
            frontFace = VkFrontFace.Clockwise
        };

        VkPipelineMultisampleStateCreateInfo multisample = VkPipelineMultisampleStateCreateInfo.Default;

        VkPipelineColorBlendAttachmentState blendAttachment = new()
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
            throw new InvalidOperationException(
                $"Shader compilation failed ({fileName}): {result.ErrorMessage}");

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

    private void PushProjectionAndDraw(
        VkCommandBuffer cmd,
        VulkanContext ctx,
        float left, float top, float right, float bottom,
        float projW, float projH)
    {
        var api = ctx.DeviceApi;

        // Build orthographic projection matrix (column-major, Y-down Vulkan convention)
        // proj[col][row] layout used by vkCmdPushConstants (row-major float[16])
        var proj = stackalloc float[16];
        proj[0]  = 2f / projW;
        proj[1]  = 0f;
        proj[2]  = 0f;
        proj[3]  = 0f;
        proj[4]  = 0f;
        proj[5]  = 2f / projH;
        proj[6]  = 0f;
        proj[7]  = 0f;
        proj[8]  = 0f;
        proj[9]  = 0f;
        proj[10] = -1f;
        proj[11] = 0f;
        proj[12] = -1f;
        proj[13] = -1f;
        proj[14] = 0f;
        proj[15] = 1f;

        api.vkCmdPushConstants(cmd, _pipelineLayout,
            VkShaderStageFlags.Vertex, 0, 64, proj);

        // Quad vertices: 2 triangles, 6 vertices, each with vec2 pos + vec2 uv
        ReadOnlySpan<float> vertices =
        [
            left,  top,    0f, 0f,
            right, top,    1f, 0f,
            right, bottom, 1f, 1f,
            left,  top,    0f, 0f,
            right, bottom, 1f, 1f,
            left,  bottom, 0f, 1f
        ];

        var offset = ctx.WriteVertices(vertices);
        var vb = ctx.VertexBuffer;
        var vkOffset = (ulong)offset;
        api.vkCmdBindVertexBuffers(cmd, 0, 1, &vb, &vkOffset);
        api.vkCmdDraw(cmd, 6, 1, 0, 0);
    }

    // ------------------------------------------------------------------ Byte-level UBO write helpers

    private static void WriteInt(byte* base_, int offset, int value)
    {
        *(int*)(base_ + offset) = value;
    }

    private static void WriteFloat(byte* base_, int offset, float value)
    {
        *(float*)(base_ + offset) = value;
    }
}
