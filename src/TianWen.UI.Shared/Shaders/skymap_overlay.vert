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
    // Screen-space angle of celestial north, measured from screen +x (right).
    // Hand-maintained mirror of OverlayEngine.ComputeEllipseScreenAxes (the CPU
    // selection marker shares the same convention): the major axis points along
    // (cos(totalAngle), sin(totalAngle)) with totalAngle = northAngle - PA, so a
    // positive PA rotates the major axis from north toward east. The sky map is
    // east-left, so this is true sky position angle (PA = 0 -> major along north).
    float screenNorthAngle = atan(north2d.y, north2d.x);
    float totalAngle = screenNorthAngle - aPaFromNorth;

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
