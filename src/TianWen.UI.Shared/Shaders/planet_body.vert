#version 450

// One body of the orrery, ray-traced in its own frame by planet_body.frag (docs/plans/atlas-planet-detail.md, A5, "The bodies on
// the GPU"). This stage only covers the body's square on screen: the globe, or Saturn's ring system, and a pixel and a half of
// margin, as two triangles from gl_VertexIndex (no vertex buffer).

layout(push_constant) uniform Body {
    vec4 disc;        // centre x, y (px, y down), equatorial radius (px), extent (equatorial radii: the rings' outer edge, or 1)
    vec4 frame;       // viewport width, height (px), polar over equatorial radius, Minnaert k
    vec4 right;       // xyz: the screen's +x at the body, in the body frame; w: 1 with Saturn's rings
    vec4 toward;      // xyz: toward the camera, in the body frame; w: opacity
    vec4 sun;         // xyz: toward the Sun, in the body frame; w: 1 under Night
    vec4 ringAlbedo;  // rgb: the colour map's mean albedo per plane, what a ring's level is stated against
    vec4 tint;        // rgb: Night's tint, the luminance drawn in it
} pc;

void main() {
    // Corners of the two triangles: (0,0) (1,0) (1,1) / (0,0) (1,1) (0,1).
    const vec2 corners[6] = vec2[6](vec2(0.0, 0.0), vec2(1.0, 0.0), vec2(1.0, 1.0), vec2(0.0, 0.0), vec2(1.0, 1.0), vec2(0.0, 1.0));
    vec2 c = corners[gl_VertexIndex];
    float half_ = pc.disc.z * pc.disc.w + 1.5;
    vec2 px = pc.disc.xy + (c * 2.0 - 1.0) * half_;
    gl_Position = vec4(px / pc.frame.xy * 2.0 - 1.0, 0.0, 1.0);
}
