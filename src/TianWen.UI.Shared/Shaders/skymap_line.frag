#version 450

layout(location = 0) in vec4 vColor;
// The altitude's sine at this point of the line, interpolated between its vertices; 1 when the ground is not drawn.
layout(location = 1) in float vSinAlt;
// Short of 1 only on a segment with a culled end (skymap_line.vert), which is not a line of the sky at all.
layout(location = 2) in float vProjected;
layout(location = 0) out vec4 FragColor;

layout(push_constant) uniform PC {
    vec4 color;
    // What of the line the ground leaves visible below the horizon, as a factor on its alpha
    // (SkyMapGpuGeometry.BelowHorizon): 0 hidden, a fraction traced faintly, 1 whole.
    float belowHorizon;
} pc;

void main() {
    if (vProjected < 0.999) discard;
    if (vSinAlt < 0.0) {
        float a = vColor.a * pc.belowHorizon;
        if (a <= 0.0) discard;
        FragColor = vec4(vColor.rgb, a);
        return;
    }
    FragColor = vColor;
}
