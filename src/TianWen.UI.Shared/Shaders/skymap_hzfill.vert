#version 450

layout(location = 0) out vec2 vScreenPos;

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
    // Generate full-screen triangle strip from vertex index (0..5 = 2 triangles)
    vec2 pos;
    if (gl_VertexIndex == 0)      pos = vec2(0.0, 0.0);
    else if (gl_VertexIndex == 1) pos = vec2(ubo.viewportSize.x, 0.0);
    else if (gl_VertexIndex == 2) pos = vec2(0.0, ubo.viewportSize.y);
    else if (gl_VertexIndex == 3) pos = vec2(ubo.viewportSize.x, 0.0);
    else if (gl_VertexIndex == 4) pos = vec2(ubo.viewportSize.x, ubo.viewportSize.y);
    else                          pos = vec2(0.0, ubo.viewportSize.y);

    // Output screen-pixel position for the fragment shader
    vScreenPos = pos + vec2(ubo.viewportCenter.x - ubo.viewportSize.x * 0.5,
                            ubo.viewportCenter.y - ubo.viewportSize.y * 0.5);

    // Map to NDC
    gl_Position = vec4(
        pos.x / ubo.viewportSize.x * 2.0 - 1.0,
        pos.y / ubo.viewportSize.y * 2.0 - 1.0,
        0.0, 1.0);
}
