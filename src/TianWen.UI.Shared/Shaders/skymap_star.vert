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
