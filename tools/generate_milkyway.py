"""
Generate a Milky Way equirectangular texture for TianWen's sky map.
Output: milkyway.bgra.lz (lzip-compressed raw BGRA with 8-byte header).

The texture uses an analytical model based on galactic coordinates:
- Bright band along the galactic plane (b=0)
- Brighter at the galactic center (l=0)
- Warm yellowish color at center, cooler bluish in spiral arms

Requires: lzip command-line tool (same as used by Get-Tycho2Catalogs.ps1).

Usage: python generate_milkyway.py [--width 2048] [--height 1024] [--output path]
"""

import argparse
import struct
import subprocess
import math
import os
import tempfile

# Galactic coordinate conversion constants (J2000 -> Galactic)
# North galactic pole: RA=192.85948, Dec=27.12825 degrees
# Galactic center: RA=266.405, Dec=-28.936 degrees
NGP_RA  = math.radians(192.85948)
NGP_DEC = math.radians(27.12825)
GC_L    = math.radians(122.93192) # ascending node of galactic plane on equator

SIN_NGP_DEC = math.sin(NGP_DEC)
COS_NGP_DEC = math.cos(NGP_DEC)


def equatorial_to_galactic(ra_rad, dec_rad):
    """Convert J2000 RA/Dec (radians) to galactic (l, b) in radians."""
    sin_dec = math.sin(dec_rad)
    cos_dec = math.cos(dec_rad)
    da = ra_rad - NGP_RA
    sin_da = math.sin(da)
    cos_da = math.cos(da)

    sin_b = SIN_NGP_DEC * sin_dec + COS_NGP_DEC * cos_dec * cos_da
    b = math.asin(max(-1.0, min(1.0, sin_b)))

    y = cos_dec * sin_da
    x = COS_NGP_DEC * sin_dec - SIN_NGP_DEC * cos_dec * cos_da
    l = GC_L - math.atan2(y, x)

    # Normalize l to [0, 2*pi)
    l = l % (2 * math.pi)
    return l, b


def bv_to_rgb(bv):
    """Convert B-V color index to approximate RGB (Planckian locus)."""
    # Clamp to valid range
    bv = max(-0.4, min(2.0, bv))
    t = 4600.0 * (1.0 / (0.92 * bv + 1.7) + 1.0 / (0.92 * bv + 0.62))

    # Planckian approximation
    if t <= 6600:
        r = 1.0
        g = max(0, min(1, 0.39 * math.log(t / 100.0) - 0.634))
    else:
        r = max(0, min(1, 1.293 * ((t / 100.0 - 60) ** -0.1332)))
        g = max(0, min(1, 1.129 * ((t / 100.0 - 60) ** -0.0755)))

    if t >= 6600:
        b_val = 1.0
    elif t <= 1900:
        b_val = 0.0
    else:
        b_val = max(0, min(1, 0.543 * math.log(t / 100.0 - 10.0) - 1.186))

    return r, g, b_val


def generate_milkyway(width, height):
    """Generate equirectangular Milky Way BGRA texture."""
    TWO_PI = 2.0 * math.pi
    PI = math.pi

    # Allocate BGRA buffer
    buf = bytearray(width * height * 4)

    for py in range(height):
        # v = py / height, dec = pi/2 - v * pi
        v = py / height
        dec = PI / 2.0 - v * PI

        for px in range(width):
            # Must match the GLSL UV convention: u = atan2(y,x)/(2*PI) + 0.5
            # So RA=0h maps to u=0.5, RA=12h maps to u=0 and u=1
            u = px / width
            ra = (u - 0.5) * TWO_PI

            l, b = equatorial_to_galactic(ra, dec)

            # Galactic latitude brightness: Gaussian with sigma ~ 5 degrees
            # Wider component (thick disk) + narrow component (thin disk)
            b_deg = math.degrees(b)
            thin = math.exp(-b_deg * b_deg / (2.0 * 3.0 * 3.0))
            thick = 0.3 * math.exp(-b_deg * b_deg / (2.0 * 12.0 * 12.0))
            lat_brightness = thin + thick

            # Galactic longitude brightness: brighter near center (l=0),
            # secondary peaks in spiral arms
            l_deg = math.degrees(l)
            if l_deg > 180:
                l_deg -= 360  # range [-180, 180]

            # Central bulge
            bulge = 0.8 * math.exp(-l_deg * l_deg / (2.0 * 15.0 * 15.0))
            # Broad disk (always present)
            disk = 0.5
            # Spiral arm hints (simple sinusoidal modulation)
            arms = 0.15 * (1.0 + math.cos(math.radians(l_deg * 2.5)))
            lon_brightness = disk + bulge + arms

            brightness = lat_brightness * lon_brightness

            # Color: warm yellowish at center, bluish in outer regions
            center_weight = math.exp(-(l_deg * l_deg + b_deg * b_deg) / (2.0 * 30.0 * 30.0))
            bv_center = 1.2   # reddish-yellow
            bv_outer = 0.3    # bluish-white
            bv = bv_center * center_weight + bv_outer * (1.0 - center_weight)
            r, g, bl = bv_to_rgb(bv)

            # Apply brightness with gentle tone mapping (kept subtle for wide-FOV views)
            scale = min(brightness * 0.35, 1.0)
            rb = int(max(0, min(255, r * scale * 255)))
            gb = int(max(0, min(255, g * scale * 255)))
            bb = int(max(0, min(255, bl * scale * 255)))
            ab = int(max(0, min(255, scale * 255)))

            # BGRA order
            idx = (py * width + px) * 4
            buf[idx + 0] = bb
            buf[idx + 1] = gb
            buf[idx + 2] = rb
            buf[idx + 3] = ab

    return bytes(buf)


def main():
    parser = argparse.ArgumentParser(description='Generate Milky Way texture for TianWen sky map')
    parser.add_argument('--width', type=int, default=2048)
    parser.add_argument('--height', type=int, default=1024)
    parser.add_argument('--output', default=None,
                        help='Output path (default: src/TianWen.UI.Gui/Resources/milkyway.bgra.lz)')
    args = parser.parse_args()

    if args.output is None:
        script_dir = os.path.dirname(os.path.abspath(__file__))
        repo_root = os.path.dirname(script_dir)
        resources_dir = os.path.join(repo_root, 'src', 'TianWen.UI.Gui', 'Resources')
        os.makedirs(resources_dir, exist_ok=True)
        args.output = os.path.join(resources_dir, 'milkyway.bgra.lz')

    print(f'Generating {args.width}x{args.height} Milky Way texture...')
    bgra = generate_milkyway(args.width, args.height)

    # Prepend 8-byte header: width (i32 LE) + height (i32 LE)
    header = struct.pack('<ii', args.width, args.height)
    raw = header + bgra

    print(f'Raw size: {len(raw):,} bytes')

    # Write raw file, then compress with lzip command-line tool
    raw_path = args.output.replace('.lz', '')
    with open(raw_path, 'wb') as f:
        f.write(raw)

    print('Compressing with lzip...')
    subprocess.run(['lzip', '-9', '-b', '4MiB', '-f', raw_path], check=True)

    compressed_size = os.path.getsize(args.output)
    print(f'Compressed size: {compressed_size:,} bytes ({compressed_size * 100 // len(raw)}%)')
    print(f'Written to {args.output}')


if __name__ == '__main__':
    main()
