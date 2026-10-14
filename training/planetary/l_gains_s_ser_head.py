"""Diagnostic: the first N frames of a SER capture written to scratch (the source is read, never written): the 178-byte header with its
frame count set to N, the first N frames byte for byte, and their N timestamps from the trailer when the capture has one.

  python l_gains_s_ser_head.py <source.ser> <N> <out.ser>
"""
import os, struct, sys

src, n, out = sys.argv[1], int(sys.argv[2]), sys.argv[3]
with open(src, "rb") as f:
    header = bytearray(f.read(178))
    width, height, depth = struct.unpack_from("<iii", header, 26)
    count = struct.unpack_from("<i", header, 38)[0]
    colour = struct.unpack_from("<i", header, 18)[0]
    planes = 3 if colour in (100, 101) else 1
    frame_bytes = width * height * planes * (1 if depth <= 8 else 2)
    n = min(n, count)
    frames = f.read(frame_bytes * n)
    f.seek(178 + frame_bytes * count)
    trailer = f.read(8 * count)
struct.pack_into("<i", header, 38, n)
os.makedirs(os.path.dirname(out), exist_ok=True)
with open(out + ".part", "wb") as g:
    g.write(header)
    g.write(frames)
    if len(trailer) == 8 * count:
        g.write(trailer[:8 * n])
os.replace(out + ".part", out)
print(f"{src}: {count} frames {width}x{height}, depth {depth}, colour id {colour}; wrote the first {n} to {out} ({'with' if len(trailer) == 8 * count else 'without'} timestamps)")
