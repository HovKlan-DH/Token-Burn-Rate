"""Rasterize the TokenBurnRate icon to PNG + ICO using only the stdlib.

Kept dependency-free on purpose: neither ImageMagick nor Inkscape is installed on
the machines this app is built on, and the icon changes too rarely to justify one.

Geometry mirrors Assets/icon.svg and the live tray ring drawn at runtime by
Services/TrayIconRenderer.cs (see Assets/live-ring-icon.md) - a filled arc around a
flame, frozen at 65% so the static app identity and the live tray icon read as one
mark. Edit all three together. Shapes live in a 256x256 design space and are
sampled with supersampling; sizes below 24px swap the flame for a plain dot, same
as the live tray icon's small-size cut - the flame turns to mush below that.

    python Assets/render-icon.py Assets
"""
import math
import struct
import zlib

W = 256.0
FRACTION = 0.65


# ---------- geometry helpers ----------

def bez(p0, p1, p2, p3, n=24):
    """Flatten a cubic bezier into line segments."""
    pts = []
    for i in range(1, n + 1):
        t = i / n
        u = 1 - t
        x = u * u * u * p0[0] + 3 * u * u * t * p1[0] + 3 * u * t * t * p2[0] + t * t * t * p3[0]
        y = u * u * u * p0[1] + 3 * u * u * t * p1[1] + 3 * u * t * t * p2[1] + t * t * t * p3[1]
        pts.append((x, y))
    return pts


def rounded_rect(x, y, w, h, r):
    r = min(r, w / 2, h / 2)
    k = r * 0.5522847498
    p = [(x + r, y), (x + w - r, y)]
    p += bez((x + w - r, y), (x + w - r + k, y), (x + w, y + r - k), (x + w, y + r))
    p.append((x + w, y + h - r))
    p += bez((x + w, y + h - r), (x + w, y + h - r + k), (x + w - r + k, y + h), (x + w - r, y + h))
    p.append((x + r, y + h))
    p += bez((x + r, y + h), (x + r - k, y + h), (x, y + h - r + k), (x, y + h - r))
    p.append((x, y + r))
    p += bez((x, y + r), (x, y + r - k), (x + r - k, y), (x + r, y))
    return p


def path(start, curves):
    """curves: list of (c1, c2, end) cubic segments."""
    pts = [start]
    cur = start
    for c1, c2, end in curves:
        pts += bez(cur, c1, c2, end)
        cur = end
    return pts


def circle(cx, cy, r, n=96):
    return [(cx + r * math.cos(2 * math.pi * i / n), cy + r * math.sin(2 * math.pi * i / n))
            for i in range(n)]


def ring(cx, cy, r, width, n=96):
    """An annulus (ring) as a single polygon: outer boundary then inner boundary
    reversed, which the even-odd fill rule below turns into a hole."""
    outer = circle(cx, cy, r + width / 2, n)
    inner = list(reversed(circle(cx, cy, r - width / 2, n)))
    return outer + [outer[0]] + inner + [inner[0]]


def arc_stroke(cx, cy, r, width, fraction, round_cap, n=96):
    """A clockwise arc from 12 o'clock, stroked to `width`, as a closed polygon.
    Degenerates to a full ring at fraction >= 1, matching TrayIconRenderer's
    "a 360-degree arc draws nothing" workaround."""
    if fraction >= 1:
        return ring(cx, cy, r, width, n)

    angle = fraction * 360.0
    steps = max(2, int(n * fraction))
    outer_r, inner_r = r + width / 2, r - width / 2

    def radial(deg):
        """Unit vector pointing outward from the centre at `deg` (0 = 12 o'clock,
        clockwise), and the tangential unit vector 90 degrees clockwise from it -
        the direction of travel along the arc at that point."""
        rad = math.radians(deg - 90)
        rx, ry = math.cos(rad), math.sin(rad)
        return (rx, ry), (-ry, rx)

    def pt(deg, rr):
        (rx, ry), _ = radial(deg)
        return (cx + rr * rx, cy + rr * ry)

    outer = [pt(angle * i / steps, outer_r) for i in range(steps + 1)]
    inner = [pt(angle * i / steps, inner_r) for i in range(steps + 1)]

    if round_cap:
        # Semicircular caps at each end, so the stroke end reads the same as
        # Avalonia's PenLineCap.Round rather than a flat chop. Each cap is a
        # semicircle centred on the arc's centreline at that endpoint, swept
        # through the half that bulges away from the stroke body (backward past
        # the start, forward past the end).
        cap_r = width / 2

        def cap(deg, forward):
            (rx, ry), (tx, ty) = radial(deg)
            base = (cx + r * rx, cy + r * ry)
            sign = 1 if forward else -1
            pts = []
            for i in range(9):
                # Sweep from the outer edge (+radial) to the inner edge (-radial),
                # bulging along +/- tangential.
                a = math.pi * i / 8
                ox = math.cos(a) * rx + sign * math.sin(a) * tx
                oy = math.cos(a) * ry + sign * math.sin(a) * ty
                pts.append((base[0] + cap_r * ox, base[1] + cap_r * oy))
            return pts

        start_cap = cap(0, forward=False)
        end_cap = cap(angle, forward=True)
        return outer + end_cap + list(reversed(inner)) + list(reversed(start_cap))
    else:
        return outer + list(reversed(inner))


# The fraction the static icon is frozen at - see module docstring.
TRACK = ring(128, 128, 86, 26)
ARC = arc_stroke(128, 128, 86, 26, FRACTION, round_cap=True)

# Small-size cut geometry (see Assets/live-ring-icon.md): thicker stroke, square
# cap, solid dot instead of the flame.
TRACK_SMALL = ring(128, 128, 88, 34)
ARC_SMALL = arc_stroke(128, 128, 88, 34, FRACTION, round_cap=False)
DOT_SMALL = circle(128, 128, 34)

GROUND = rounded_rect(0, 0, 256, 256, 56)

# Flame, scaled 0.5 and translated (64, 67) - identical to TrayIconRenderer.FlameGeometry.
def flame_point(x, y):
    return (64 + x * 0.5, 67 + y * 0.5)


FLAME = path(flame_point(128, 22), [
    (flame_point(152, 74), flame_point(188, 96), flame_point(188, 142)),
    (flame_point(188, 184), flame_point(161, 214), flame_point(128, 214)),
    (flame_point(95, 214), flame_point(68, 184), flame_point(68, 142)),
    (flame_point(68, 110), flame_point(92, 98), flame_point(102, 64)),
    (flame_point(112, 88), flame_point(122, 78), flame_point(128, 22)),
])


def inside(poly, px, py):
    """Even-odd point-in-polygon."""
    c = False
    n = len(poly)
    j = n - 1
    for i in range(n):
        xi, yi = poly[i]
        xj, yj = poly[j]
        if (yi > py) != (yj > py):
            if px < (xj - xi) * (py - yi) / (yj - yi) + xi:
                c = not c
        j = i
    return c


def bbox(poly):
    xs = [p[0] for p in poly]
    ys = [p[1] for p in poly]
    return min(xs), min(ys), max(xs), max(ys)


# ---------- colors ----------

def hexc(h):
    h = h.lstrip('#')
    return (int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16))


BG = hexc('1A1B1E')
TRACKC = hexc('3A3D44')
ACCENT = hexc('D97757')      # ClaudeAccent


# ---------- rasterizer ----------

def render(size, big, ss=4):
    scale = size / W
    px = [[[0.0, 0.0, 0.0, 0.0] for _ in range(size)] for _ in range(size)]

    if big:
        layers = [
            ('ground', GROUND, lambda x, y: BG, 1.0),
            ('track', TRACK, lambda x, y: TRACKC, 1.0),
            ('arc', ARC, lambda x, y: ACCENT, 1.0),
            ('flame', FLAME, lambda x, y: ACCENT, 1.0),
        ]
    else:
        layers = [
            ('ground', GROUND, lambda x, y: BG, 1.0),
            ('track', TRACK_SMALL, lambda x, y: TRACKC, 1.0),
            ('arc', ARC_SMALL, lambda x, y: ACCENT, 1.0),
            ('dot', DOT_SMALL, lambda x, y: ACCENT, 1.0),
        ]

    boxes = {name: bbox(poly) for name, poly, _, _ in layers}

    for py in range(size):
        for pxi in range(size):
            r = g = b = a = 0.0
            for name, poly, colf, alpha in layers:
                x0, y0, x1, y1 = boxes[name]
                if (pxi / scale > x1 + 1 or (pxi + 1) / scale < x0 - 1
                        or py / scale > y1 + 1 or (py + 1) / scale < y0 - 1):
                    continue
                cov = 0
                cr = cg = cb = 0.0
                for sy in range(ss):
                    for sx in range(ss):
                        dx = (pxi + (sx + 0.5) / ss) / scale
                        dy = (py + (sy + 0.5) / ss) / scale
                        if inside(poly, dx, dy):
                            cov += 1
                            c = colf(dx, dy)
                            cr += c[0]
                            cg += c[1]
                            cb += c[2]
                if cov == 0:
                    continue
                f = (cov / (ss * ss)) * alpha
                sc = (cr / cov, cg / cov, cb / cov)
                na = f + a * (1 - f)
                if na > 0:
                    r = (sc[0] * f + r * a * (1 - f)) / na
                    g = (sc[1] * f + g * a * (1 - f)) / na
                    b = (sc[2] * f + b * a * (1 - f)) / na
                a = na
            px[py][pxi] = [r, g, b, a]

    return px


def to_rgba_bytes(px):
    out = bytearray()
    for row in px:
        out.append(0)
        for r, g, b, a in row:
            out += bytes((int(r + 0.5), int(g + 0.5), int(b + 0.5), int(a * 255 + 0.5)))
    return bytes(out)


def png_bytes(px):
    size = len(px)
    raw = to_rgba_bytes(px)

    def chunk(tag, data):
        c = struct.pack('>I', len(data)) + tag + data
        return c + struct.pack('>I', zlib.crc32(tag + data) & 0xffffffff)

    png = b'\x89PNG\r\n\x1a\n'
    png += chunk(b'IHDR', struct.pack('>IIBBBBB', size, size, 8, 6, 0, 0, 0))
    png += chunk(b'IDAT', zlib.compress(raw, 9))
    png += chunk(b'IEND', b'')
    return png


def write_ico(path_, entries):
    """entries: list of (size, png_bytes)"""
    n = len(entries)
    hdr = struct.pack('<HHH', 0, 1, n)
    offset = 6 + 16 * n
    dir_ = b''
    data = b''
    for size, blob in entries:
        d = 0 if size >= 256 else size
        dir_ += struct.pack('<BBBBHHII', d, d, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)
        data += blob
    open(path_, 'wb').write(hdr + dir_ + data)


if __name__ == '__main__':
    import sys
    import os

    outdir = sys.argv[1]
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    entries = []
    for s in sizes:
        big = s >= 24
        ss = 4 if s > 64 else 6
        pxm = render(s, big=big, ss=ss)
        blob = png_bytes(pxm)
        entries.append((s, blob))
        if s in (16, 32, 48, 128, 256):
            open(os.path.join(outdir, 'icon-%d.png' % s), 'wb').write(blob)
        print('rendered %3d %s %6d bytes' % (s, 'flame' if big else 'dot', len(blob)), flush=True)
    write_ico(os.path.join(outdir, 'icon.ico'), entries)
    print('ico written')
