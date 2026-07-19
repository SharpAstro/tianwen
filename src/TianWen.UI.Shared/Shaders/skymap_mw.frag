#version 450

layout(location = 0) in vec2 vScreenPos;
layout(location = 0) out vec4 FragColor;

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

layout(set = 1, binding = 0) uniform sampler2D milkyWayTex;

layout(push_constant) uniform PushConstants {
    float alpha;
} pc;

const float PI    = 3.14159265358979;
const float TWO_PI = 6.28318530717959;

void main() {
    // Inverse stereographic projection: screen pixel -> camera-space direction
    float x = (vScreenPos.x - ubo.viewportCenter.x) / ubo.pixelsPerRadian;
    float y = -(vScreenPos.y - ubo.viewportCenter.y) / ubo.pixelsPerRadian;

    float rho = length(vec2(x, y));
    vec3 camDir;
    if (rho < 0.00001) {
        camDir = vec3(0.0, 0.0, -1.0);
    } else {
        float c = 2.0 * atan(rho * 0.5);
        float sinC = sin(c);
        float cosC = cos(c);
        camDir = vec3(sinC * x / rho, sinC * y / rho, -cosC);
    }

    // Rotate back to J2000 (view matrix is orthogonal, inverse = transpose)
    vec3 j2000 = transpose(mat3(ubo.viewMatrix)) * camDir;

    // J2000 unit vector -> equirectangular UV
    float ra = atan(j2000.y, j2000.x);       // [-PI, PI]
    float u = ra / TWO_PI + 0.5;              // [0, 1]
    float dec = asin(clamp(j2000.z, -1.0, 1.0)); // [-PI/2, PI/2]
    float v = 0.5 - dec / PI;                 // [0, 1], north at top

    vec4 mw = texture(milkyWayTex, vec2(u, v));
    FragColor = vec4(mw.rgb, mw.a * pc.alpha);
}
