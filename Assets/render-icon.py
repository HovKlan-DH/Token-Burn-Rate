"""Rasterize the TokenBurnRate icon to PNG + ICO using only the stdlib.

Kept dependency-free on purpose: neither ImageMagick nor Inkscape is installed on
the machines this app is built on, and the icon changes too rarely to justify one.

Geometry mirrors Assets/icon.svg, which stays the editable source of truth - edit
both together. Shapes live in a 256x256 design space and are sampled with
supersampling; sizes below 40px drop the hot-core highlight, which turns to mush.

    python Assets/render-icon.py Assets
"""
import struct
import zlib

W = 256.0


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


# ---------- shapes ----------

GROUND = rounded_rect(0, 0, 256, 256, 56)

# The fuse sits low in the tile and the flame rises above it, carrying the mass.
# The bar spans 26..230 and is deliberately chunky: at 16-32px a slim bar drops to
# a single grey thread, so the whole group is scaled to fill the frame instead.
TRACK = rounded_rect(148, 186, 82, 38, 19)
FILL = rounded_rect(26, 186, 138, 38, 19)

# A broad teardrop drawn as one solid mass with a single sharp tip. Detail beyond
# this disappears below ~32px, so the silhouette carries the whole read.
FLAME = path((146, 192), [
    ((110, 152), (128, 100), (170, 30)),
    ((165, 78), (190, 88), (192, 56)),
    ((230, 96), (242, 148), (220, 186)),
    ((207, 208), (174, 214), (155, 205)),
    ((146, 200), (143, 196), (146, 192)),
])

# Hot core: small and low, a hint of heat rather than a shape of its own.
CORE = path((174, 194), [
    ((163, 180), (169, 158), (185, 134)),
    ((184, 158), (196, 160), (197, 148)),
    ((210, 164), (210, 186), (200, 197)),
    ((191, 205), (179, 201), (174, 194)),
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


def lerp(a, b, t):
    return tuple(a[i] + (b[i] - a[i]) * t for i in range(3))


BG = hexc('1A1B1E')
TRACKC = hexc('3A3D44')
CORE_C = hexc('FFE9A8')

BAR_A, BAR_B = hexc('E8B44A'), hexc('D97757')
FL_0, FL_1, FL_2 = hexc('D9603F'), hexc('F0873C'), hexc('FFC24A')


def bar_color(x, y):
    """Horizontal gradient across the fill bar (x 26..164)."""
    t = (x - 26) / 136.0
    t = 0 if t < 0 else 1 if t > 1 else t
    t = t / 0.55 if t < 0.55 else 1.0
    return lerp(BAR_A, BAR_B, min(t, 1.0))


def flame_color(x, y):
    """Vertical gradient, from the fuse line (y=214) up to the tip (y=38)."""
    t = (214 - y) / 176.0
    t = 0 if t < 0 else 1 if t > 1 else t
    if t < 0.45:
        return lerp(FL_0, FL_1, t / 0.45)
    return lerp(FL_1, FL_2, (t - 0.45) / 0.55)


# ---------- rasterizer ----------

def render(size, with_core=True, ss=4):
    scale = size / W
    px = [[[0.0, 0.0, 0.0, 0.0] for _ in range(size)] for _ in range(size)]

    layers = [
        ('ground', GROUND, lambda x, y: BG, 1.0),
        ('track', TRACK, lambda x, y: TRACKC, 1.0),
        ('fill', FILL, bar_color, 1.0),
        ('flame', FLAME, flame_color, 1.0),
    ]
    if with_core:
        layers.append(('core', CORE, lambda x, y: CORE_C, 0.75))

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
        core = s >= 40
        ss = 4 if s > 64 else 6
        pxm = render(s, with_core=core, ss=ss)
        blob = png_bytes(pxm)
        entries.append((s, blob))
        if s in (16, 32, 48, 128, 256):
            open(os.path.join(outdir, 'icon-%d.png' % s), 'wb').write(blob)
        print('rendered %3d %s %6d bytes' % (s, 'core' if core else 'flat', len(blob)), flush=True)
    write_ico(os.path.join(outdir, 'icon.ico'), entries)
    print('ico written')
