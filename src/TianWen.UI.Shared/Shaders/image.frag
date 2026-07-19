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
    int   curvesMode;       // offset  28: 0=boost, 1=spline LUT
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
    vec4  whiteBalance;        // offset 192  (xyz = WB multipliers, w = pad)
    vec4  bgNeutralization;    // offset 208  (xyz = neutralization gains, w = pad)
    vec4  curveData[9];        // offset 224  (33 knots packed into 9 vec4s; last 3 floats unused)
    vec4  lumaWeights;         // offset 368  (xyz = R/G/B luma weights, w = pad). Rec.709 default.
    vec4  lumaStretch;         // offset 384  (x = lumaShadow, y = lumaMidtones, z = lumaRescale, w = pad)
    vec4  stretchBlend;        // offset 400  (x = lumaBlend in [0,1], y = normalizeScale, z = debayerMode 0=bilinear/1=MHC, w = pad)
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
    // Background neutralization: out = norm * g + (1-g)
    norm = norm * ubo.bgNeutralization[ch] + (1.0 - ubo.bgNeutralization[ch]);
    norm = max(norm * ubo.whiteBalance[ch], 0.0);
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

float applyCurveLUT(float v) {
    // 33 knots at i/32 for i in 0..32. Mirrors Image.ApplyCurveLut on the CPU side:
    // v in [0,1] -> idx in [0,32] -> floor to i in [0,31] -> mix(curveData[i],
    // curveData[i+1], idx-i). The previous implementation clamped i AFTER computing
    // frac, which produced an off-by-one at v=1.0 (returned knot 32 instead of 33).
    // min(31) keeps i in [0,31] and lets frac=1 cleanly select knot 33 at v=1.0.
    v = clamp(v, 0.0, 1.0);
    float idx = v * 32.0;
    int i = min(int(idx), 31);
    float frac = idx - float(i);
    int vi = i / 4;
    int ci = i % 4;
    int vj = (i + 1) / 4;
    int cj = (i + 1) % 4;
    float vi0 = ubo.curveData[vi][ci];
    float vi1 = ubo.curveData[vj][cj];
    return mix(vi0, vi1, frac);
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

// Clamp-to-edge texel fetch on the raw mosaic. texelFetch does NOT clamp coordinates;
// MHC's 5x5 reach would otherwise read 0 outside the image and stain a 2px border.
// Mirrors SerImaging.At / Image.AtClamped.
float rawAt(ivec2 p) {
    ivec2 m = ivec2(ubo.imageSize) - ivec2(1, 1);
    p = clamp(p, ivec2(0, 0), m);
    return texelFetch(uChannel0, p, 0).r;
}

// Malvar-He-Cutler (2004) gradient-corrected linear demosaic. The exact CPU mirror lives in
// Image.DebayerMHCAsync and SharpAstro.Ser SerImaging.DebayerMhc -- same 5x5 kernels, each
// summing to 8 (x0.125 = unity gain, no brightness shift). Clamped to [0,1] to match the CPU
// display reference. Bilinear-class cost, far fewer edge zipper / false-colour artifacts.
vec3 debayerMhc(vec2 uv) {
    vec2 texSize = ubo.imageSize;
    vec2 pixCoord = uv * texSize - 0.5;
    ivec2 px = ivec2(floor(pixCoord));
    int offX = ubo.bayerPat % 65536;
    int offY = ubo.bayerPat / 65536;
    int bx = (px.x + offX) % 2;
    int by = (px.y + offY) % 2;

    float c  = rawAt(px);
    float n  = rawAt(px + ivec2( 0,-1));
    float s  = rawAt(px + ivec2( 0, 1));
    float e  = rawAt(px + ivec2( 1, 0));
    float w  = rawAt(px + ivec2(-1, 0));
    float nn = rawAt(px + ivec2( 0,-2));
    float ss = rawAt(px + ivec2( 0, 2));
    float ee = rawAt(px + ivec2( 2, 0));
    float ww = rawAt(px + ivec2(-2, 0));
    float ne = rawAt(px + ivec2( 1,-1));
    float nw = rawAt(px + ivec2(-1,-1));
    float se = rawAt(px + ivec2( 1, 1));
    float sw = rawAt(px + ivec2(-1, 1));

    float orthoNear = n + s + e + w;
    float orthoFar  = nn + ss + ee + ww;
    float diag      = ne + nw + se + sw;

    // Green at a red/blue site (alpha = 1/2).
    float gAtRB  = (4.0 * c + 2.0 * orthoNear - orthoFar) * 0.125;
    // Red at a blue site / blue at a red site (gamma = 3/4); same-colour neighbours are the diagonals.
    float diagRB = (6.0 * c + 2.0 * diag - 1.5 * orthoFar) * 0.125;
    // Red/blue at a green site, same-colour neighbours in the same ROW (beta = 5/8).
    float hG = (5.0 * c + 4.0 * (w + e) - diag + 0.5 * (nn + ss) - (ww + ee)) * 0.125;
    // Red/blue at a green site, same-colour neighbours in the same COLUMN.
    float vG = (5.0 * c + 4.0 * (n + s) - diag + 0.5 * (ww + ee) - (nn + ss)) * 0.125;

    float rr, gg, bb;
    if (bx == 0 && by == 0) {          // red site
        rr = c;  gg = gAtRB;  bb = diagRB;
    } else if (bx == 1 && by == 1) {   // blue site
        bb = c;  gg = gAtRB;  rr = diagRB;
    } else if (bx == 1 && by == 0) {   // green on a red row: red horizontal, blue vertical
        gg = c;  rr = hG;  bb = vG;
    } else {                            // green on a blue row: blue horizontal, red vertical
        gg = c;  bb = hG;  rr = vG;
    }
    return clamp(vec3(rr, gg, bb), 0.0, 1.0);
}

// No demosaic: the raw mosaic value at each pixel, shown as grey -- reveals the CFA checkerboard.
float debayerRaw(vec2 uv) {
    return rawAt(ivec2(floor(uv * ubo.imageSize - 0.5)));
}

// Monochrome debayer: average the 2x2 Bayer quad to one luminance-ish grey value. Mirrors
// Image.DebayerBilinearMonoAsync (current + right + down + down-right, /4).
float debayerMono(vec2 uv) {
    ivec2 px = ivec2(floor(uv * ubo.imageSize - 0.5));
    return (rawAt(px) + rawAt(px + ivec2(1, 0)) + rawAt(px + ivec2(0, 1)) + rawAt(px + ivec2(1, 1))) * 0.25;
}

void main() {
    int src = ubo.imgSource;
    // RawBayer demosaic mode (stretchBlend.z): 0 = bilinear colour, 1 = MHC colour, 2 = raw mosaic, 3 = mono.
    int dm = (src == 2) ? int(ubo.stretchBlend.z) : -1;
    // Raw passthrough and mono both yield a single grey value -> route them through the mono stretch
    // path so they render as true greyscale (the per-channel colour stretch would tint an equal RGB triple).
    bool rawBayerGrey = (dm == 2 || dm == 3);
    float r, g, b;

    if (src == 2 && !rawBayerGrey) {
        // 1 = MHC, else bilinear (fallback). Both produce colour.
        vec3 rgb = (dm == 1) ? debayerMhc(vTexCoord) : debayerBilinear(vTexCoord);
        r = rgb.r; g = rgb.g; b = rgb.b;
    } else if (rawBayerGrey) {
        r = (dm == 2) ? debayerRaw(vTexCoord) : debayerMono(vTexCoord);
        g = r; b = r;
    } else if (src == 1 || ubo.channelCount < 3) {
        r = texture(uChannel0, vTexCoord).r;
        g = r; b = r;
    } else {
        r = texture(uChannel0, vTexCoord).r;
        g = texture(uChannel1, vTexCoord).r;
        b = texture(uChannel2, vTexCoord).r;
    }

    // Mono path (also covers the RawBayer raw / mono greyscale modes)
    if ((src <= 1 && ubo.channelCount < 3) || rawBayerGrey) {
        if (ubo.stretchMode >= 1) {
            r = stretchChannel(r, 0);
        }
        if (ubo.curvesMode == 1) {
            r = applyCurveLUT(r);
        } else if (ubo.curvesBoost > 0.0) {
            r = applyCurve(r, ubo.curvesBoost);
        }
        if (ubo.hdrAmount > 0.0) {
            r = applyHdr(r, ubo.hdrAmount, ubo.hdrKnee);
        }
        float nsMono = ubo.stretchBlend.y;
        if (nsMono != 1.0 && nsMono > 0.0) {
            r *= nsMono;
        }
        r = clamp(r, 0.0, 1.0);
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
    // stretchMode values are the C# StretchMode enum cast to int:
    //   0 = None (passthrough), 1 = Linked, 2 = Unlinked, 3 = Luma.
    // Linked and Unlinked both use per-channel stretchChannel; the difference is
    // already encoded in the per-channel uniforms (Linked replicates channel 0,
    // Unlinked uses each channel's own stats). Luma is its own pipeline.
    if (ubo.stretchMode == 1 || ubo.stretchMode == 2) {
        r = stretchChannel(r, 0);
        g = stretchChannel(g, 1);
        b = stretchChannel(b, 2);
    } else if (ubo.stretchMode == 3) {
        // Capture raw values *before* we trample r/g/b. The optional per-channel
        // linked branch below also needs them.
        float rawR = r;
        float rawG = g;
        float rawB = b;

        float nr = rawR * ubo.normFactor;
        float ng = rawG * ubo.normFactor;
        float nb = rawB * ubo.normFactor;
        float prr = max((nr - ubo.pedestal[0]) * ubo.whiteBalance[0], 0.0);
        float prg = max((ng - ubo.pedestal[1]) * ubo.whiteBalance[1], 0.0);
        float prb = max((nb - ubo.pedestal[2]) * ubo.whiteBalance[2], 0.0);
        float Ynorm = ubo.lumaWeights.x * prr + ubo.lumaWeights.y * prg + ubo.lumaWeights.z * prb;
        float rescaled = (Ynorm - ubo.lumaStretch.x) * ubo.lumaStretch.z;
        float Yp = mtf(ubo.lumaStretch.y, rescaled);
        float scale = Ynorm > 1e-7 ? Yp / Ynorm : 0.0;
        float maxCh = max(prr, max(prg, prb));
        if (maxCh > 1e-7) scale = min(scale, 1.0 / maxCh);
        float lumaR = clamp(prr * scale, 0.0, 1.0);
        float lumaG = clamp(prg * scale, 0.0, 1.0);
        float lumaB = clamp(prb * scale, 0.0, 1.0);

        // Optional blend with the per-channel linked branch. ubo.shadows/midtones/rescale
        // hold the per-channel linked params in Luma mode (the producer always populates
        // both LumaStretch and the per-channel linked stats). lumaBlend == 1 short-circuits
        // to the pure-luma result -- same numbers as before this field existed.
        float lb = clamp(ubo.stretchBlend.x, 0.0, 1.0);
        if (lb < 1.0) {
            float linkR = stretchChannel(rawR, 0);
            float linkG = stretchChannel(rawG, 1);
            float linkB = stretchChannel(rawB, 2);
            r = mix(linkR, lumaR, lb);
            g = mix(linkG, lumaG, lb);
            b = mix(linkB, lumaB, lb);
        } else {
            r = lumaR;
            g = lumaG;
            b = lumaB;
        }
    } else {
        // stretchMode == 0 (None / linear): no stretch curve to carry the WB multiply, so apply
        // WhiteBalance directly. stretchChannel already applies it for modes 1/2/3, so this only
        // runs for None and never double-applies. Neutral WB leaves the passthrough unchanged.
        r = max(r * ubo.whiteBalance[0], 0.0);
        g = max(g * ubo.whiteBalance[1], 0.0);
        b = max(b * ubo.whiteBalance[2], 0.0);
    }

    if (ubo.curvesMode == 1) {
        r = applyCurveLUT(r);
        g = applyCurveLUT(g);
        b = applyCurveLUT(b);
    } else if (ubo.curvesBoost > 0.0) {
        r = applyCurve(r, ubo.curvesBoost);
        g = applyCurve(g, ubo.curvesBoost);
        b = applyCurve(b, ubo.curvesBoost);
    }
    if (ubo.hdrAmount > 0.0) {
        r = applyHdr(r, ubo.hdrAmount, ubo.hdrKnee);
        g = applyHdr(g, ubo.hdrAmount, ubo.hdrKnee);
        b = applyHdr(b, ubo.hdrAmount, ubo.hdrKnee);
    }

    // Post-stretch normalize -- producer predicts max via Image.PredictPostStretchMaxScale
    // and stamps stretchBlend.y. Default 1.0 = no-op; only > 1 when the predicted peak
    // sits below 1 (e.g. HDR knee or S-curve compresses highlights).
    float ns = ubo.stretchBlend.y;
    if (ns != 1.0 && ns > 0.0) {
        r *= ns; g *= ns; b *= ns;
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

    FragColor = vec4(clamp(r, 0.0, 1.0), clamp(g, 0.0, 1.0), clamp(b, 0.0, 1.0), 1.0);
}
