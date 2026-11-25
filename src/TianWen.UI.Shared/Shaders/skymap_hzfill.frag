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

void main() {
    // Inverse stereographic projection: screen pixel -> camera-space unit vector
    float x = (vScreenPos.x - ubo.viewportCenter.x) / ubo.pixelsPerRadian;
    float y = -(vScreenPos.y - ubo.viewportCenter.y) / ubo.pixelsPerRadian;

    // Warm earthy-brown ground, OPAQUE: you cannot see through the ground (the owner, 2026-11-25), so nothing of the sky
    // drawn before it (the Milky Way) shows through, at the horizon or below it. It darkens with depth instead.
    vec3 groundTint = vec3(0.18, 0.11, 0.06);
    vec3 groundDeep = vec3(0.10, 0.06, 0.035);

    float rho = length(vec2(x, y));
    if (rho < 0.00001) {
        vec3 j2000 = transpose(mat3(ubo.viewMatrix)) * vec3(0.0, 0.0, -1.0);
        float sinAlt = ubo.sinLat * j2000.z
            + ubo.cosLat * (ubo.cosLST * j2000.x + ubo.sinLST * j2000.y);
        if (sinAlt >= 0.0) discard;
        FragColor = vec4(mix(groundTint, groundDeep, clamp(-sinAlt / 0.17, 0.0, 1.0)), 1.0);
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
    vec3 j2000 = transpose(mat3(ubo.viewMatrix)) * camDir;

    // Compute altitude: sin(alt) = sinLat*z + cosLat*(cosLST*x + sinLST*y)
    float sinAlt = ubo.sinLat * j2000.z
        + ubo.cosLat * (ubo.cosLST * j2000.x + ubo.sinLST * j2000.y);

    if (sinAlt >= 0.0) discard;

    // Darker with depth, down to -10 degrees (sin(10deg) ~ 0.17); never translucent.
    float depth = clamp(-sinAlt / 0.17, 0.0, 1.0);
    FragColor = vec4(mix(groundTint, groundDeep, depth), 1.0);
}
