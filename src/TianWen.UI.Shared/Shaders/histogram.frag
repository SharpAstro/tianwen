#version 450

layout(location = 0) in vec2 vTexCoord;
layout(location = 0) out vec4 FragColor;

layout(set = 0, binding = 0, std140) uniform HistogramUBO {
    int   channelCount; // offset  0
    float logPeak;      // offset  4
    float linearPeak;   // offset  8
    int   logScale;     // offset 12
} ubo;

layout(set = 1, binding = 0) uniform sampler2D uHist0;
layout(set = 1, binding = 1) uniform sampler2D uHist1;
layout(set = 1, binding = 2) uniform sampler2D uHist2;

float scaleValue(float v) {
    if (ubo.logScale != 0) {
        return log(1.0 + v) / ubo.logPeak;
    } else {
        return v / ubo.linearPeak;
    }
}

void main() {
    float x = vTexCoord.x;
    float y = 1.0 - vTexCoord.y; // Flip Y: Vulkan UV has 0 at top, histogram needs 0 at bottom

    if (ubo.logPeak <= 0.0) {
        FragColor = vec4(0.0);
        return;
    }

    float n0 = scaleValue(texture(uHist0, vec2(x, 0.5)).r);

    vec3 color = vec3(0.0);
    float alpha = 0.0;

    if (ubo.channelCount >= 3) {
        float n1 = scaleValue(texture(uHist1, vec2(x, 0.5)).r);
        float n2 = scaleValue(texture(uHist2, vec2(x, 0.5)).r);
        if (y <= n0) { color.r += 0.85; alpha = max(alpha, 0.7); }
        if (y <= n1) { color.g += 0.85; alpha = max(alpha, 0.7); }
        if (y <= n2) { color.b += 0.85; alpha = max(alpha, 0.7); }
    } else {
        if (y <= n0) { color = vec3(0.8); alpha = 0.7; }
    }

    FragColor = vec4(color, alpha);
}
