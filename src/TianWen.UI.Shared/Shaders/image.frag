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
    vec4  stretchBlend;        // offset 400  (x = lumaBlend in [0,1], y = normalizeScale, z = debayerMode 0=bilinear/1=MHC, w = texelsPerPixel: mosaic texels per screen pixel, 0 = unknown = 1)
    // offset 416 (xyz = RA/Dec grid line RGB, w = its alpha). Written from
    // SkyMapGpuGeometry.GridLineColor, which is the ONE definition of the EQ grid's colour: the same
    // value reaches the sky map's own grid, so the two grids this shader hands over to each other
    // cannot drift apart. It is a constant, not a per-draw choice, which is why no caller passes it.
    vec4  gridColor;
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
    // Clamped because asin is UNDEFINED outside [-1, 1] and this argument is only
    // mathematically inside it: rounding in the products and the divide can land a
    // fragment at the pole on 1.0000001, which returns NaN and paints a NaN pixel.
    // skymap_mw.frag already clamps its own asin for this reason; the two had
    // drifted apart.
    float dec = asin(clamp(cosC * sinDec0 + eta * sinC * cosDec0 / rho, -1.0, 1.0));
    // atan(y, x) is UNDEFINED at (0, 0), and this pair reaches it. The rho early return
    // above keeps sinC positive, so the first argument vanishes only where xi is zero;
    // the second then vanishes where dec0 + c = PI/2, which is the fragment sitting on
    // the celestial pole, an ordinary thing for a frame that contains one. Right
    // ascension is undefined at a pole rather than merely hard to compute, so fall back
    // to the frame centre's and let dec, computed above, carry the position.
    float raY = xi * sinC;
    float raX = rho * cosDec0 * cosC - eta * sinDec0 * sinC;
    float raLen = length(vec2(raY, raX));
    float ra  = ra0 + (raLen > 1e-12 ? atan(raY, raX) : 0.0);
    return vec2(ra, dec);
}

// Where a fragment is in the frame's own pixel coordinates: the 0-based centroid frame every WCS
// number is in (crPix included), in which the centre of pixel i is i. A texture coordinate u puts
// the fragment at u * width along the quad, whose pixel i spans [i, i + 1), so the same position is
// half a pixel earlier in the centroid frame. This is the GPU half of the rule the CPU states once
// in WcsAnnotationLayer.ImageToScreen, and the two must keep agreeing: the CPU draws the grid's
// labels where its lines meet the edge of the picture. It used to add ONE instead, from the days a
// WCS carried its header's 1-based CRPIX verbatim, which put the whole grid 1.5 pixels off the sky.
vec2 wcsPixel(vec2 uv) {
    return uv * ubo.imageSize - 0.5;
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
//
// NO -0.5 HERE, and that is the whole of a bug that shipped: `uv * texSize - 0.5` is the correct
// fragment-centre-to-texel mapping ONLY when you are about to INTERPOLATE between px and px+1 using
// fract() as the weight. This is a nearest fetch -- the fractional part is never used -- so the -0.5
// is a pure half-texel shift, and floor(t - 0.5) == i paints mosaic pixel i over the screen band
// [i+0.5, i+1.5) instead of [i, i+1). The picture then sits half a pixel down-right of everything
// drawn in image coordinates on top of it, which is what made every star-detection circle look
// up-left of its blob. It is a DISPLAY-only error: nothing in detection, plate solving or stacking
// goes through this shader, and being constant it is invisible to all three anyway. The CPU mirror
// (Image.BilinearInterpolateColorFast) has always been centred on (x, y), i.e. correct.
//
// The -0.5 in debayerMono is NOT this and must stay -- see there.
vec3 debayerBilinear(vec2 uv) {
    vec2 texSize = ubo.imageSize;
    ivec2 px = ivec2(floor(uv * texSize));
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
    // Nearest fetch, so no -0.5: see debayerBilinear.
    ivec2 px = ivec2(floor(uv * texSize));
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

// ---------------------------------------------------------------------------------------------
// Variable Number of Gradients (VNG). The exact CPU mirror is Image.DebayerVNGAsync and its four
// Interpolate*VNG helpers, in the same way debayerMhc above mirrors Image.DebayerMHCAsync: every
// direction, weight, 1.5 threshold factor and epsilon below is transcribed from there, so what the
// screen shows and what a Save writes are the same demosaic rather than two that merely agree on
// average. VNG is what the viewer defaults to because it is the one algorithm here that leaves no
// dark rim around a bright star core -- measured on a real CR3, ring dip -0.36% against MHC's
// +2.86% -- while still resolving colour better than plain bilinear (fringe 1.35% vs 3.89%).
//
// The idea: interpolate along the directions the image is SMOOTH in and ignore the ones it has an
// edge in. Each candidate direction contributes a gradient and a value; the smallest gradient sets
// a threshold at 1.5x itself, and the directions inside it are averaged. A flat neighbourhood keeps
// all of them (an average, like bilinear); an edge keeps only the ones running along it, which is
// what stops the interpolator reaching across a star's rim and pulling the background inwards.
//
// THE EPSILONS ARE ABSOLUTE, so this is only correct on a mosaic normalised to [0, 1]. Both callers
// are: AstroImageDocument normalises on load and uploads that texture, and DisplayRasterExport
// debayers that same normalised image. Feeding it raw ADU would drown the 0.01 floor and turn every
// direction test into a coin toss on noise.
// ---------------------------------------------------------------------------------------------

// Frame border, within two pixels of an edge: the CPU's VNG inner loop is inset by that much and
// the rim is filled by Image.ProcessEdgePixels instead, which averages every same-colour sample in
// the clipped 5x5 window (BilinearInterpolateColorFast -- not a bilinear filter, despite the name).
// Mirrored literally, window clipping included, rather than leaning on rawAt's clamp: clamping
// would be defensible on its own but would disagree with the file at the one place the difference
// is easiest to see, a 2px band along all four edges.
vec3 vngEdge(ivec2 p, vec3 knownMask) {
    ivec2 m = ivec2(ubo.imageSize) - ivec2(1, 1);
    ivec2 lo = max(p - ivec2(2, 2), ivec2(0, 0));
    ivec2 hi = min(p + ivec2(2, 2), m);
    int offX = ubo.bayerPat % 65536;
    int offY = ubo.bayerPat / 65536;

    vec3 sum = vec3(0.0);
    vec3 count = vec3(0.0);
    for (int y = lo.y; y <= hi.y; y++) {
        for (int x = lo.x; x <= hi.x; x++) {
            int nx = (x + offX) % 2;
            int ny = (y + offY) % 2;
            bool isRed  = (nx == 0 && ny == 0);
            bool isBlue = (nx == 1 && ny == 1);
            vec3 hit = vec3(isRed ? 1.0 : 0.0, (isRed || isBlue) ? 0.0 : 1.0, isBlue ? 1.0 : 0.0);
            sum += hit * texelFetch(uChannel0, ivec2(x, y), 0).r;
            count += hit;
        }
    }

    // The known channel takes the raw sample verbatim; only the other two are interpolated, exactly
    // as ProcessEdgePixel's `if (c != knownColor)` does.
    vec3 avg = sum / max(count, vec3(1.0));
    return clamp(mix(avg, vec3(texelFetch(uChannel0, p, 0).r), knownMask), 0.0, 1.0);
}

// Green at a red or blue site. Four cardinal directions over a 5-tap cross; the value corrects the
// near green by half the same-colour slope. Mirrors InterpolateGreenAtRBVNG, which is the ONE helper
// with no epsilon on its threshold.
//
// EVERY GRADIENT TERM COMPARES TWO SAMPLES OF THE SAME COLOUR, which is what makes it a gradient:
// zero on a flat field whatever the sky's colour. This used to be |2g - v - c|, i.e. 2*(green -
// centre) on a flat field -- a colour difference, and an affine function of the value it selects
// (val = c + signed_grad/2), so "keep the smallest gradient" meant "keep the value nearest the centre
// pixel's own level". Measured on a real OSC sub (SV605CC GRBG; R 1472, G 2680, B 2888 ADU) green
// read 2779 at blue sites and correctly at red ones -- blue sits near green so the threshold really
// selected, red sits far so every direction cleared it. Blue sites are alternate rows AND columns, so
// that landed as a two-pixel alternation on both axes: the fine stripes at 1:1, 6.4 display levels
// against 12 of pixel noise, thirty times what MHC and AHD show on the same frame.
float vngGreenAtRB(ivec2 p, float c) {
    vec4 g = vec4(rawAt(p + ivec2( 0, -1)), rawAt(p + ivec2( 0, 1)),
                  rawAt(p + ivec2(-1,  0)), rawAt(p + ivec2( 1, 0)));
    vec4 v = vec4(rawAt(p + ivec2( 0, -2)), rawAt(p + ivec2( 0, 2)),
                  rawAt(p + ivec2(-2,  0)), rawAt(p + ivec2( 2, 0)));

    // Green variation ACROSS each axis, shared by the two directions along it: what lets an edge
    // running one way rule out the pair running the other way. Green-to-green, so zero when flat.
    float gVert = abs(g.x - g.y);
    float gHoriz = abs(g.z - g.w);

    // Per direction, the centre's OWN colour two away. Centre-to-centre, so zero when flat.
    vec4 grad = abs(v - vec4(c)) + vec4(gVert, gVert, gHoriz, gHoriz);
    vec4 val = g + (vec4(c) - v) * 0.5;

    float threshold = min(min(grad.x, grad.y), min(grad.z, grad.w)) * 1.5;
    vec4 keep = step(grad, vec4(threshold));
    float count = dot(keep, vec4(1.0));
    return count > 0.0 ? dot(keep, val) / count : val.x;
}

// Red or blue at a green site whose same-colour neighbours lie in the same ROW.
// Mirrors InterpolateHorizontalVNG. The candidates are not the centre's colour, so the gradient is
// taken on the GREEN two away -- same colour as the centre, zero on a flat field. See vngGreenAtRB.
float vngHorizontal(ivec2 p, float c) {
    vec2 n = vec2(rawAt(p + ivec2(-1, 0)), rawAt(p + ivec2(1, 0)));
    vec2 grad = abs(vec2(rawAt(p + ivec2(-2, 0)), rawAt(p + ivec2(2, 0))) - vec2(c));
    float threshold = min(grad.x, grad.y) * 1.5 + 0.01;
    vec2 keep = step(grad, vec2(threshold));
    float count = keep.x + keep.y;
    return count > 0.0 ? dot(keep, n) / count : (n.x + n.y) * 0.5;
}

// The transpose of the above: same-colour neighbours in the same COLUMN.
// Mirrors InterpolateVerticalVNG.
float vngVertical(ivec2 p, float c) {
    vec2 n = vec2(rawAt(p + ivec2(0, -1)), rawAt(p + ivec2(0, 1)));
    vec2 grad = abs(vec2(rawAt(p + ivec2(0, -2)), rawAt(p + ivec2(0, 2))) - vec2(c));
    float threshold = min(grad.x, grad.y) * 1.5 + 0.01;
    vec2 keep = step(grad, vec2(threshold));
    float count = keep.x + keep.y;
    return count > 0.0 ? dot(keep, n) / count : (n.x + n.y) * 0.5;
}

// Red at a blue site (or blue at a red one): the same-colour neighbours are the four diagonals.
// Each diagonal's gradient adds the difference between the two GREENS flanking it, which is the
// part that makes this better than a diagonal average -- green is sampled twice as densely, so it
// is the only channel that can tell a real edge from chroma noise at this scale.
// Mirrors InterpolateDiagonalVNG.
float vngDiagonal(ivec2 p, float c) {
    vec4 d = vec4(rawAt(p + ivec2(-1, -1)), rawAt(p + ivec2( 1, -1)),
                  rawAt(p + ivec2(-1,  1)), rawAt(p + ivec2( 1,  1)));   // NW, NE, SW, SE
    float gN = rawAt(p + ivec2( 0, -1));
    float gS = rawAt(p + ivec2( 0,  1));
    float gW = rawAt(p + ivec2(-1,  0));
    float gE = rawAt(p + ivec2( 1,  0));

    // Same-colour again: the diagonal TWO away carries the centre's own colour, while the candidates
    // one away do not. |candidate - centre| was red-minus-blue, 1416 ADU of pure colour on the frame
    // this was found on -- so large that the 1.5x threshold admitted all four and nothing selected.
    vec4 d2 = vec4(rawAt(p + ivec2(-2, -2)), rawAt(p + ivec2( 2, -2)),
                   rawAt(p + ivec2(-2,  2)), rawAt(p + ivec2( 2,  2)));
    vec4 grad = abs(d2 - vec4(c))
              + abs(vec4(gN, gN, gS, gS) - vec4(gW, gE, gW, gE));

    float threshold = min(min(grad.x, grad.y), min(grad.z, grad.w)) * 1.5 + 0.01;
    vec4 keep = step(grad, vec4(threshold));
    float count = dot(keep, vec4(1.0));
    return count > 0.0 ? dot(keep, d) / count : dot(d, vec4(0.25));
}

// Nearest fetch, so no -0.5: see debayerBilinear.
vec3 debayerVng(vec2 uv) {
    ivec2 px = ivec2(floor(uv * ubo.imageSize));
    int offX = ubo.bayerPat % 65536;
    int offY = ubo.bayerPat / 65536;
    int bx = (px.x + offX) % 2;
    int by = (px.y + offY) % 2;

    bool redSite = (bx == 0 && by == 0);
    bool blueSite = (bx == 1 && by == 1);
    vec3 knownMask = vec3(redSite ? 1.0 : 0.0, (redSite || blueSite) ? 0.0 : 1.0, blueSite ? 1.0 : 0.0);

    ivec2 m = ivec2(ubo.imageSize) - ivec2(1, 1);
    if (px.x < 2 || px.y < 2 || px.x > m.x - 2 || px.y > m.y - 2) {
        return vngEdge(px, knownMask);
    }

    float c = rawAt(px);
    float rr, gg, bb;
    if (redSite) {
        rr = c;  gg = vngGreenAtRB(px, c);  bb = vngDiagonal(px, c);
    } else if (blueSite) {
        bb = c;  gg = vngGreenAtRB(px, c);  rr = vngDiagonal(px, c);
    } else if (by == 0) {              // green on a red row: red neighbours horizontal, blue vertical
        gg = c;  rr = vngHorizontal(px, c);  bb = vngVertical(px, c);
    } else {                           // green on a blue row: blue horizontal, red vertical
        gg = c;  bb = vngHorizontal(px, c);  rr = vngVertical(px, c);
    }
    // Clamped like debayerMhc: the green-at-RB correction extrapolates and can overshoot [0, 1].
    return clamp(vec3(rr, gg, bb), 0.0, 1.0);
}

// No demosaic: the raw mosaic value at each pixel, shown as grey -- reveals the CFA checkerboard.
// Nearest fetch, so no -0.5: see debayerBilinear.
float debayerRaw(vec2 uv) {
    return rawAt(ivec2(floor(uv * ubo.imageSize)));
}

// Monochrome debayer: average the 2x2 Bayer quad to one luminance-ish grey value. Mirrors
// Image.DebayerBilinearMonoAsync (current + right + down + down-right, /4).
//
// KEEP THE -0.5. It looks like the one deleted from debayerBilinear above and is the opposite: it is
// not a texel-centre convention, it is the BilinearMonoGridOffset correction applied at display time.
// The quad starting at px has its centre at px + 0.5, so the value belongs half a pixel down-right of
// the texel it is stored in -- exactly why Image.StarDetection shifts mono-measured centroids by
// +0.5 to express them in mosaic coordinates. Sampling at floor(uv*S - 0.5) picks the quad whose
// CENTRE is nearest this fragment, which cancels that half pixel and puts the grey image on the same
// grid as the colour ones. A 2x2 average cannot be phase-correct on the integer grid by any other
// means, so removing this would tilt mono the other way rather than fix anything.
float debayerMono(vec2 uv) {
    ivec2 px = ivec2(floor(uv * ubo.imageSize - 0.5));
    return (rawAt(px) + rawAt(px + ivec2(1, 0)) + rawAt(px + ivec2(0, 1)) + rawAt(px + ivec2(1, 1))) * 0.25;
}

// Superpixel: each PATTERN-ALIGNED 2x2 quad is one R, two G and one B, so this is a half-resolution
// colour image with no interpolation in it -- and therefore no 2-texel period left to alias against
// the screen grid. That is the whole reason it exists: every interpolating demosaic above keeps a
// checkerboard of noise texture at the CFA period (native photosites carry full variance, the
// interpolated ones less), and sampling that once per screen pixel below 100% beats with the grid at
// 1/(zoom - 0.5): a square lattice near fit zoom, a fine mesh in the 50-60% band. What N.I.N.A. and
// PixInsight previews do at these zooms.
//
// Unlike debayerMono the quad is NOT the one nearest the fragment; it is the one the fragment's
// texel belongs to, anchored on the red corner, because a quad that straddled two pattern periods
// would put a G on the R tap. The quad's centre sits half a texel down-right of its red corner,
// which at the zooms this is chosen for (texelsPerPixel >= 2) is at most a quarter of a screen pixel.
vec3 debayerSuperpixel(vec2 uv) {
    ivec2 px = ivec2(floor(uv * ubo.imageSize));
    int offX = ubo.bayerPat % 65536;
    int offY = ubo.bayerPat / 65536;
    ivec2 q = px - ivec2((px.x + offX) % 2, (px.y + offY) % 2); // the quad's red corner
    float r = rawAt(q);
    float g = (rawAt(q + ivec2(1, 0)) + rawAt(q + ivec2(0, 1))) * 0.5;
    float b = rawAt(q + ivec2(1, 1));
    return vec3(r, g, b);
}

// The colour demosaic the mode asks for: 1 = MHC, 4 = VNG, else bilinear (fallback).
vec3 debayerColour(vec2 uv, int dm) {
    return (dm == 1) ? debayerMhc(uv)
         : (dm == 4) ? debayerVng(uv)
         : debayerBilinear(uv);
}

// The colour demosaic at the resolution the SCREEN is asking for. One demosaic sample per screen
// pixel is only right at 100% and above; below it the mosaic is being subsampled and its CFA-period
// noise texture aliases (see debayerSuperpixel). At half zoom and below the superpixel image is the
// exact half-res answer; between half and full, four demosaic samples a quarter of a screen pixel
// apart are averaged -- a 2x2 box over the screen pixel's footprint, enough to break the beat.
vec3 debayerForZoom(vec2 uv, int dm) {
    float tpp = ubo.stretchBlend.w; // mosaic texels per screen pixel; 0 = not supplied = 1
    if (tpp >= 2.0) {
        return debayerSuperpixel(uv);
    }
    if (tpp > 1.0) {
        // Half a TEXEL either side, not a fraction of the screen pixel: the demosaics fetch by
        // floor(), so taps that fall inside one mosaic texel are the same sample and average
        // nothing (at 76% a quarter-pixel spread is 0.33 texel -- the mesh stayed). At +-0.5 the
        // pair always straddles two adjacent texels, a 2x2 texel box centred on the fragment,
        // which covers the whole 2-texel CFA period whatever the zoom in this band.
        vec2 d = vec2(0.5) / ubo.imageSize;
        return 0.25 * (debayerColour(uv + vec2(-d.x, -d.y), dm)
                     + debayerColour(uv + vec2( d.x, -d.y), dm)
                     + debayerColour(uv + vec2(-d.x,  d.y), dm)
                     + debayerColour(uv + vec2( d.x,  d.y), dm));
    }
    return debayerColour(uv, dm);
}

void main() {
    // gridEnabled == 2: draw the GRID AND NOTHING ELSE, over whatever is already in the framebuffer.
    // The quad is the pane rather than the picture, and its texture coordinates run outside [0, 1] --
    // pixelToSky is a linear map followed by a deprojection, so it extrapolates to virtual image
    // pixels beyond the sensor exactly as it interpolates inside it. That is what lets ONE grid, at
    // this shader's per-pixel density, span the sky behind a photograph and the photograph itself
    // instead of stopping at its edge. Every sample outside a grid line is transparent (the pipeline
    // blends), so nothing else on the pane is disturbed.
    if (ubo.gridEnabled == 2) {
        float gridOnly = gridIntensity(wcsPixel(vTexCoord));
        FragColor = vec4(ubo.gridColor.rgb, gridOnly * ubo.gridColor.a);
        return;
    }

    int src = ubo.imgSource;
    // RawBayer demosaic mode (stretchBlend.z): 0 = bilinear colour, 1 = MHC colour, 2 = raw mosaic,
    // 3 = mono, 4 = VNG colour.
    int dm = (src == 2) ? int(ubo.stretchBlend.z) : -1;
    // Raw passthrough and mono both yield a single grey value -> route them through the mono stretch
    // path so they render as true greyscale (the per-channel colour stretch would tint an equal RGB triple).
    bool rawBayerGrey = (dm == 2 || dm == 3);
    float r, g, b;

    if (src == 2 && !rawBayerGrey) {
        // Colour, at the resolution the zoom asks for -- see debayerForZoom.
        vec3 rgb = debayerForZoom(vTexCoord, dm);
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
            float grid = gridIntensity(wcsPixel(vTexCoord));
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
    // already encoded in the per-channel uniforms. Linked writes ONE curve into all
    // three slots (the shared-curve STF: the white balance then survives as colour);
    // Unlinked writes each channel's own auto-normalised curve (which absorbs the
    // auto calibration and neutralises the background). See StretchSolver.
    // Luma is its own pipeline.
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
        float grid = gridIntensity(wcsPixel(vTexCoord));
        float gridA = grid * ubo.gridColor.a;
        r = mix(r, ubo.gridColor.r, gridA);
        g = mix(g, ubo.gridColor.g, gridA);
        b = mix(b, ubo.gridColor.b, gridA);
    }

    FragColor = vec4(clamp(r, 0.0, 1.0), clamp(g, 0.0, 1.0), clamp(b, 0.0, 1.0), 1.0);
}
