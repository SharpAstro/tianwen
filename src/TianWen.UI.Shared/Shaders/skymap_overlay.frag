#version 450

layout(location = 0) in vec2  vLocal;
layout(location = 1) in vec2  vSize;
layout(location = 2) in float vThickness;
layout(location = 3) in vec4  vColor;

layout(location = 0) out vec4 FragColor;

void main() {
    // Ellipse ring by pixel distance: (x/a)^2 + (y/b)^2 = 1 on the boundary,
    // and dividing the normalised distance by its own screen-space derivative
    // turns it into an exact pixel distance at every point of the ring, so an
    // eccentric marker is stroked as evenly as a round one. The mean semi-axis
    // this replaces was exact only for a circle. Same rule as DIR.Lib's affine
    // ellipse and the WebGL twin of this shader.
    vec2 s = max(vSize, vec2(0.5));
    vec2 n = vLocal / s;
    float normDist = sqrt(dot(n, n));
    float g = max(length(vec2(dFdx(normDist), dFdy(normDist))), 1e-6);
    float pixelDist = abs(normDist - 1.0) / g;

    float halfT = max(vThickness * 0.5, 0.5);
    // Antialiased ring: full alpha inside halfT, fade to 0 over 1 px.
    float alpha = 1.0 - smoothstep(halfT, halfT + 1.0, pixelDist);
    if (alpha < 0.01) discard;

    FragColor = vec4(vColor.rgb * alpha, vColor.a * alpha);
}
