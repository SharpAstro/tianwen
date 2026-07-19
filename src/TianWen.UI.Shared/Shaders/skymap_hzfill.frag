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

    // Warm earthy-brown tint for the below-horizon region. Mixed on top of
    // the sky background with depth-scaled alpha so pixels just below the
    // horizon are lightly hazed, and pixels deep below are mostly ground.
    vec3 groundTint = vec3(0.18, 0.11, 0.06);

    float rho = length(vec2(x, y));
    if (rho < 0.00001) {
        vec3 j2000 = transpose(mat3(ubo.viewMatrix)) * vec3(0.0, 0.0, -1.0);
        float sinAlt = ubo.sinLat * j2000.z
            + ubo.cosLat * (ubo.cosLST * j2000.x + ubo.sinLST * j2000.y);
        if (sinAlt >= 0.0) discard;
        FragColor = vec4(groundTint, 0.85);
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

    // Fade from 0.45 at horizon to 0.88 at -10 degrees (sin(10deg) ~ 0.17)
    float depth = clamp(-sinAlt / 0.17, 0.0, 1.0);
    float alpha = 0.45 + 0.43 * depth;
    FragColor = vec4(groundTint, alpha);
}
