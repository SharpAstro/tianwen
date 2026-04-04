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
    int   bayerPat;        // offset 156: offsetX + offsetY*65536
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
    float raGrid  = ra  / ubo.gridSpacingRA;
    float decGrid = dec / ubo.gridSpacingDec;
    float raFrac  = abs(raGrid  - round(raGrid))  * ubo.gridSpacingRA;
    float decFrac = abs(decGrid - round(decGrid)) * ubo.gridSpacingDec;
    float raWidth = ubo.gridLineWidth / max(cos(dec), 0.01);
    float raLine  = 1.0 - smoothstep(0.0, raWidth,          raFrac);
    float decLine = 1.0 - smoothstep(0.0, ubo.gridLineWidth, decFrac);
    return max(raLine, decLine);
}

void main() {
    int src = ubo.imgSource;
    float r, g, b;

    if (src == 2) {
        // Raw Bayer: TODO bilinear debayer, for now show as mono
        r = texture(uChannel0, vTexCoord).r;
        g = r; b = r;
    } else if (src == 1 || ubo.channelCount < 3) {
        // Raw mono or processed mono
        r = texture(uChannel0, vTexCoord).r;
        g = r; b = r;
    } else {
        // Pre-debayered RGB channels
        r = texture(uChannel0, vTexCoord).r;
        g = texture(uChannel1, vTexCoord).r;
        b = texture(uChannel2, vTexCoord).r;
    }

    // Mono path: stretch + output, then return early
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
                mix(r, gridColor.r, grid * 0.7),
                mix(r, gridColor.g, grid * 0.7),
                mix(r, gridColor.b, grid * 0.7),
                1.0);
        } else {
            FragColor = vec4(r, r, r, 1.0);
        }
        return;
    }

    // RGB path (pre-debayered or Bayer)
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
        vec3 gridColor = vec3(0.0, 0.8, 0.0);
        r = mix(r, gridColor.r, grid * 0.7);
        g = mix(g, gridColor.g, grid * 0.7);
        b = mix(b, gridColor.b, grid * 0.7);
    }

    FragColor = vec4(r, g, b, 1.0);
}