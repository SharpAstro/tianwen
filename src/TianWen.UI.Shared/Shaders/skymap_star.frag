#version 450

layout(location = 0) in vec2 vCorner;
layout(location = 1) in vec3 vColor;
layout(location = 2) in float vAlpha;

layout(location = 0) out vec4 FragColor;

void main() {
    // Radial distance from quad center [0, sqrt(2)]
    float dist = length(vCorner);

    // Analytic PSF - soft halo without a bound texture.
    //   core  : wide flat disc at full brightness (covers the visible star body).
    //           With the quad expanded by +3 px, the star's nominal radius sits
    //           near dist ~ r/(r+3), so the core must stay flat into that range.
    //   halo  : Gaussian-like falloff for the glow.
    // Numbers tuned so small stars stay bright near the center and dim stars
    // aren't washed out by the Gaussian shoulder.
    float core  = 1.0 - smoothstep(0.32, 0.55, dist);
    float halo  = exp(-dist * dist * 3.2);
    float alpha = max(core, halo);
    if (alpha < 0.005) discard;

    FragColor = vec4(vColor * alpha * vAlpha, alpha * vAlpha);
}
