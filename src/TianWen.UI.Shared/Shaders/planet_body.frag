#version 450

// One body of the orrery, ray-traced in its own frame (docs/plans/atlas-planet-detail.md, A5, "The bodies on the GPU"): the same
// scene as the CPU's PlanetaryRender.RenderRgba (its Scene and SceneRay), which stays the reference this is held to
// (PlanetBodyGpuTests). The body frame has x along the prime meridian, z along the IAU north pole and y = z x x, in equatorial
// radii, so a point's west longitude is -atan2(y, x). Every pixel is the mean of 3 x 3 rays, each along -toward from the screen
// point it starts at: the oblate spheroid met first, Minnaert's law (mu0^k mu^(k-1)), the colour map at the point's planetographic
// latitude and west longitude, and with Saturn's rings the ring-plane crossing in depth order, the globe's shadow on the rings and
// theirs on the globe. Coverage is the alpha, straight (not premultiplied), as every host blends it. A copy of this file, for
// WebGL 2, is src/TianWen.UI.Web/SkyMap/WebGlPlanetBodies.cs: change both.

layout(push_constant) uniform Body {
    vec4 disc;        // centre x, y (px, y down), equatorial radius (px), extent (equatorial radii: the rings' outer edge, or 1)
    vec4 frame;       // viewport width, height (px), polar over equatorial radius, Minnaert k
    vec4 right;       // xyz: the screen's +x at the body, in the body frame; w: 1 with Saturn's rings
    vec4 toward;      // xyz: toward the camera, in the body frame; w: opacity
    vec4 sun;         // xyz: toward the Sun, in the body frame; w: 1 under Night
    vec4 ringAlbedo;  // rgb: the colour map's mean albedo per plane, what a ring's level is stated against
    vec4 tint;        // rgb: Night's tint, the luminance drawn in it
} pc;

// The colour map: planetographic latitude and WEST longitude, row 0 the north edge, column 0 at 0 degrees with longitude falling
// to the right (PlanetMap's convention), one texel per sample, read with texelFetch and interpolated here as PlanetMap.Sample does.
layout(set = 0, binding = 0) uniform sampler2D map;

layout(location = 0) out vec4 FragColor;

const float PI = 3.14159265358979;

// SaturnRings.Main, Saturn's equatorial radius 60268 km: inner and outer edge (km), the lit face's level over the map's mean
// albedo, the normal optical depth. PlanetBodyGpuTests holds this table to SaturnRings.Main.
const int RingCount = 4;
const vec4 Rings[RingCount] = vec4[RingCount](
    vec4(74658.0, 92000.0, 0.12, 0.1),
    vec4(92000.0, 117580.0, 0.85, 2.0),
    vec4(117580.0, 122170.0, 0.1, 0.1),
    vec4(122170.0, 136775.0, 0.6, 0.5));
const float SaturnEquatorialKm = 60268.0;

// The ring at `radius` equatorial radii: (level, optical depth), or a negative level in a gap.
vec2 ringAt(float radius) {
    float km = radius * SaturnEquatorialKm;
    for (int i = 0; i < RingCount; i++) {
        if (km >= Rings[i].x && km < Rings[i].y) {
            return Rings[i].zw;
        }
    }
    return vec2(-1.0, 0.0);
}

// A ring's transmission at a sine of elevation over the ring plane (SaturnRing.Transmission).
float transmission(float tau, float sinElevation) {
    return sinElevation == 0.0 ? 0.0 : exp(-tau / abs(sinElevation));
}

// PlanetMap.Sample, per plane at once: bilinear between sample centres, wrapping in longitude, held at the poles' rows.
vec3 sampleMap(float latitudeDeg, float westDeg) {
    ivec2 size = textureSize(map, 0);
    float west = mod(westDeg, 360.0);
    float fx = (360.0 - west) * float(size.x) / 360.0 - 0.5;
    float fy = clamp((90.0 - latitudeDeg) * float(size.y) / 180.0 - 0.5, 0.0, float(size.y - 1));
    float x0f = floor(fx);
    float tx = fx - x0f;
    int x0 = int(mod(x0f, float(size.x)));
    int x1 = (x0 + 1) % size.x;
    int y0 = min(int(floor(fy)), size.y - 2);
    float ty = fy - float(y0);
    vec3 top = mix(texelFetch(map, ivec2(x0, y0), 0).rgb, texelFetch(map, ivec2(x1, y0), 0).rgb, tx);
    vec3 bottom = mix(texelFetch(map, ivec2(x0, y0 + 1), 0).rgb, texelFetch(map, ivec2(x1, y0 + 1), 0).rgb, tx);
    return mix(top, bottom, ty);
}

// Whether the point p of the ring plane, outside the globe, lies in the globe's shadow: the ray from it toward the Sun meets the
// spheroid at some s > 0 (PlanetaryProjection.InGlobeShadow).
bool inGlobeShadow(vec2 p, vec3 s, float q2Inverse) {
    float a = s.x * s.x + s.y * s.y + s.z * s.z * q2Inverse;
    float b = 2.0 * (p.x * s.x + p.y * s.y);
    float c = p.x * p.x + p.y * p.y - 1.0;
    float d = b * b - 4.0 * a * c;
    return d >= 0.0 && (-b + sqrt(d)) > 0.0;
}

void main() {
    vec3 right = pc.right.xyz;
    vec3 toward = pc.toward.xyz;
    vec3 up = cross(toward, right);
    vec3 s = pc.sun.xyz;
    float q = pc.frame.z;
    float q2Inverse = 1.0 / (q * q);
    float k = pc.frame.w;
    bool hasRings = pc.right.w > 0.5;
    // A ray starts in front of everything the body has, and runs away from the camera.
    float start = pc.disc.w + 1.0;
    vec3 dir = -toward;
    vec3 dirScaled = vec3(dir.xy, dir.z / q);
    float a = dot(dirScaled, dirScaled);

    vec3 colour = vec3(0.0);
    float coverage = 0.0;
    for (int j = 0; j < 3; j++) {
        for (int i = 0; i < 3; i++) {
            // gl_FragCoord is the pixel's centre; the rays sit at the centres of its 3 x 3 sub-pixels.
            vec2 p = gl_FragCoord.xy - 0.5 + (vec2(float(i), float(j)) + 0.5) / 3.0;
            vec2 disc = (p - pc.disc.xy) / pc.disc.z;
            vec3 origin = disc.x * right - disc.y * up + start * toward;

            // The spheroid x^2 + y^2 + z^2 / q^2 = 1; the nearer meeting is the smaller t.
            vec3 o = vec3(origin.xy, origin.z / q);
            float b = 2.0 * dot(o, dirScaled);
            float c = dot(o, o) - 1.0;
            float d = b * b - 4.0 * a * c;
            bool onGlobe = d >= 0.0;
            float tGlobe = onGlobe ? (-b - sqrt(d)) / (2.0 * a) : 0.0;

            vec3 globe = vec3(0.0);
            if (onGlobe) {
                vec3 hit = origin + tGlobe * dir;
                vec3 normal = normalize(vec3(hit.xy, hit.z * q2Inverse));
                float mu = dot(normal, toward);
                float mu0 = dot(normal, s);
                if (mu > 0.0 && mu0 > 0.0) {
                    float latitude = degrees(asin(clamp(normal.z, -1.0, 1.0)));
                    // atan(y, x) is UNDEFINED at (0, 0), which a ray meeting the globe at a pole reaches; the CPU's Atan2 reads 0.
                    float axisLen = length(hit.xy);
                    float west = axisLen > 1e-9 ? -degrees(atan(hit.y, hit.x)) : 0.0;
                    globe = sampleMap(latitude, west) * pow(mu0, k) * pow(mu, k - 1.0);
                    // The rings' shadow: the Sun's light through the ring its ray from this point crosses.
                    if (hasRings && s.z != 0.0 && -hit.z / s.z > 0.0) {
                        vec2 shade = hit.xy + (-hit.z / s.z) * s.xy;
                        vec2 ring = ringAt(length(shade));
                        if (ring.x >= 0.0) {
                            globe *= transmission(ring.y, s.z);
                        }
                    }
                }
            }

            vec3 radiance = globe;
            float covered = onGlobe ? 1.0 : 0.0;
            if (hasRings && abs(toward.z) >= 1e-9) {
                // The ring plane z = 0, where a ring nearer the camera than the globe lies over it.
                float tRing = -origin.z / dir.z;
                vec2 crossing = origin.xy + tRing * dir.xy;
                vec2 ring = ringAt(length(crossing));
                if (ring.x >= 0.0 && (!onGlobe || tRing < tGlobe)) {
                    // The face the Sun lights is the one the camera sees when both stand on the same side of the plane.
                    bool lit = sign(s.z) == sign(toward.z) && !inGlobeShadow(crossing, s, q2Inverse);
                    float through = transmission(ring.y, toward.z);
                    radiance = (lit ? ring.x : 0.0) * pc.ringAlbedo.rgb + through * globe;
                    covered = onGlobe ? 1.0 : 1.0 - through;
                }
            }
            colour += radiance;
            coverage += covered;
        }
    }
    if (coverage <= 0.0) {
        discard;
    }
    // The colour of what the covering rays saw (un-premultiplied), and how much of the pixel they cover.
    vec3 rgb = colour / coverage;
    if (pc.sun.w > 0.5) {
        rgb = dot(rgb, vec3(0.2126, 0.7152, 0.0722)) * pc.tint.rgb;
    }
    FragColor = vec4(clamp(rgb, 0.0, 1.0), coverage / 9.0 * pc.toward.w);
}
