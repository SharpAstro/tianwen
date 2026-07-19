#version 450

layout(location = 0) in vec2  vLocal;
layout(location = 1) in vec2  vSize;
layout(location = 2) in float vThickness;
layout(location = 3) in vec4  vColor;

layout(location = 0) out vec4 FragColor;

void main() {
    // Axis-aligned ellipse SDF: (x/a)^2 + (y/b)^2 = 1 on the boundary.
    // Scale by the mean semi-axis to convert the normalised distance back
    // to an approximate pixel distance from the ring -- good enough for
    // typical DSO aspect ratios (eccentric shapes get a slightly uneven
    // stroke, still visually clean at overlay marker sizes).
    vec2 s = max(vSize, vec2(0.5));
    vec2 n = vLocal / s;
    float normDist = sqrt(dot(n, n));
    float avgR = (s.x + s.y) * 0.5;
    float pixelDist = abs(normDist - 1.0) * avgR;

    float halfT = max(vThickness * 0.5, 0.5);
    // Antialiased ring: full alpha inside halfT, fade to 0 over 1 px.
    float alpha = 1.0 - smoothstep(halfT, halfT + 1.0, pixelDist);
    if (alpha < 0.01) discard;

    FragColor = vec4(vColor.rgb * alpha, vColor.a * alpha);
}
