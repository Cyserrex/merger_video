"""Generate assets/vmerge.ico without any third-party dependency.

Draws a "merge" glyph: two feeder bars funnelling into one output bar that ends
in a play triangle, on a rounded blue gradient tile. Rendered with 4x
supersampling, emitted as a PNG-payload .ico (Vista+ reads PNG entries).
"""

import os
import struct
import zlib

SIZES = [16, 24, 32, 48, 64, 128, 256]
SS = 4  # supersampling factor


# ---------------------------------------------------------------- geometry --

def _rounded_rect(x, y, w, h, r):
    """Return a predicate telling whether (px, py) is inside the rounded rect."""

    def inside(px, py):
        if px < x or py < y or px >= x + w or py >= y + h:
            return False
        # Clamp to the rect inset by r; in the straight bands the clamped point
        # equals the sample itself, so only true corners get the radius test.
        cx = min(max(px, x + r), x + w - r)
        cy = min(max(py, y + r), y + h - r)
        dx, dy = px - cx, py - cy
        return dx * dx + dy * dy <= r * r

    return inside


def _capsule(x0, y0, x1, y1, thickness):
    """Predicate for a thick line segment with round caps."""
    half = thickness / 2.0
    dx, dy = x1 - x0, y1 - y0
    seg_len_sq = dx * dx + dy * dy or 1.0

    def inside(px, py):
        t = ((px - x0) * dx + (py - y0) * dy) / seg_len_sq
        t = 0.0 if t < 0.0 else (1.0 if t > 1.0 else t)
        cx, cy = x0 + t * dx, y0 + t * dy
        ex, ey = px - cx, py - cy
        return ex * ex + ey * ey <= half * half

    return inside


def _triangle(ax, ay, bx, by, cx, cy):
    def sign(px, py, x1, y1, x2, y2):
        return (px - x2) * (y1 - y2) - (x1 - x2) * (py - y2)

    def inside(px, py):
        d1 = sign(px, py, ax, ay, bx, by)
        d2 = sign(px, py, bx, by, cx, cy)
        d3 = sign(px, py, cx, cy, ax, ay)
        has_neg = (d1 < 0) or (d2 < 0) or (d3 < 0)
        has_pos = (d1 > 0) or (d2 > 0) or (d3 > 0)
        return not (has_neg and has_pos)

    return inside


# ------------------------------------------------------------------ render --

def render(size):
    """Render one RGBA frame at `size` px, returning a flat bytearray."""
    n = size * SS
    px = bytearray(n * n * 4)

    tile = _rounded_rect(0.02 * n, 0.02 * n, 0.96 * n, 0.96 * n, 0.20 * n)

    # Glyph: two feeders on the left converging into one bar + play head.
    th = 0.085 * n
    mid = 0.50 * n
    join_x = 0.545 * n
    feed_a = _capsule(0.20 * n, 0.295 * n, join_x, mid, th)
    feed_b = _capsule(0.20 * n, 0.705 * n, join_x, mid, th)
    trunk = _capsule(0.20 * n, mid, 0.60 * n, mid, th)
    head = _triangle(0.585 * n, 0.325 * n, 0.585 * n, 0.675 * n, 0.845 * n, mid)

    for y in range(n):
        # vertical gradient #3B82F6 -> #1E3A8A
        t = y / float(n - 1)
        br = int(0x3B + (0x1E - 0x3B) * t)
        bg = int(0x82 + (0x3A - 0x82) * t)
        bb = int(0xF6 + (0x8A - 0xF6) * t)
        row = y * n * 4
        for x in range(n):
            if not tile(x + 0.5, y + 0.5):
                continue
            i = row + x * 4
            fx, fy = x + 0.5, y + 0.5
            if (trunk(fx, fy) or feed_a(fx, fy) or feed_b(fx, fy)
                    or head(fx, fy)):
                px[i] = px[i + 1] = px[i + 2] = 0xFF
            else:
                px[i], px[i + 1], px[i + 2] = br, bg, bb
            px[i + 3] = 0xFF
    return downsample(px, n, size)


def downsample(px, n, size):
    """Box-filter n*n RGBA down to size*size RGBA (premultiplied-safe)."""
    out = bytearray(size * size * 4)
    area = SS * SS
    for oy in range(size):
        for ox in range(size):
            r = g = b = a = 0
            for sy in range(oy * SS, oy * SS + SS):
                base = sy * n * 4
                for sx in range(ox * SS, ox * SS + SS):
                    i = base + sx * 4
                    av = px[i + 3]
                    r += px[i] * av
                    g += px[i + 1] * av
                    b += px[i + 2] * av
                    a += av
            o = (oy * size + ox) * 4
            if a:
                out[o] = r // a
                out[o + 1] = g // a
                out[o + 2] = b // a
            out[o + 3] = a // area
    return out


# --------------------------------------------------------------- encoding --

def to_png(rgba, size):
    raw = bytearray()
    stride = size * 4
    for y in range(size):
        raw.append(0)  # filter type 0
        raw += rgba[y * stride:(y + 1) * stride]

    def chunk(tag, data):
        return (struct.pack('>I', len(data)) + tag + data
                + struct.pack('>I', zlib.crc32(tag + data) & 0xFFFFFFFF))

    ihdr = struct.pack('>IIBBBBB', size, size, 8, 6, 0, 0, 0)
    return (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', ihdr)
            + chunk(b'IDAT', zlib.compress(bytes(raw), 9))
            + chunk(b'IEND', b''))


def write_ico(path, frames):
    """frames: list of (size, png_bytes)."""
    header = struct.pack('<HHH', 0, 1, len(frames))
    entries, blobs = b'', b''
    offset = len(header) + 16 * len(frames)
    for size, png in frames:
        dim = 0 if size >= 256 else size
        entries += struct.pack('<BBBBHHII', dim, dim, 0, 0, 1, 32,
                               len(png), offset)
        offset += len(png)
        blobs += png
    with open(path, 'wb') as fh:
        fh.write(header + entries + blobs)


def main():
    here = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    assets = os.path.join(here, 'assets')
    os.makedirs(assets, exist_ok=True)

    frames = []
    for size in SIZES:
        rgba = render(size)
        frames.append((size, to_png(rgba, size)))
        if size == 256:
            with open(os.path.join(assets, 'vmerge.png'), 'wb') as fh:
                fh.write(frames[-1][1])
        print(f'  rendered {size}x{size}')

    ico = os.path.join(assets, 'vmerge.ico')
    write_ico(ico, frames)
    print(f'OK -> {ico} ({os.path.getsize(ico)} bytes)')


if __name__ == '__main__':
    main()
