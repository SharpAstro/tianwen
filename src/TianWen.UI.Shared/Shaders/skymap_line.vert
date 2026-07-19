#version 450

layout(location = 0) in vec3 aUnitPos;

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

layout(push_constant) uniform PC {
    vec4 color;
} pc;

layout(location = 0) out vec4 vColor;

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
    vec3 camPos = (ubo.viewMatrix * vec4(aUnitPos, 1.0)).xyz;
    vec3 proj = stereoProject(camPos);
    if (proj.z <= -0.99) {
        // Move to clip-space boundary so the line segment is clipped
        gl_Position = vec4(2.0, 2.0, 0.0, 1.0);
        vColor = vec4(0.0);
        return;
    }

    // Same NDC-from-absolute-pixel mapping as the star shader -- see comment there.
    gl_Position = vec4(
        (proj.x - ubo.viewportCenter.x) / (ubo.viewportSize.x * 0.5),
        (proj.y - ubo.viewportCenter.y) / (ubo.viewportSize.y * 0.5),
        0.0, 1.0);
    vColor = pc.color;
}
