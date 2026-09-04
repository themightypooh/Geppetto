"""
Diner checkerboard tile floor -- generates the texture set and the .vmat.

Run:
  python tools/diner_tile_gen.py

Writes Assets/materials/diner/diner_tile_floor{_color,_normal,_rough,_ao}.png and
diner_tile_floor.vmat. Deterministic: the same SEED always produces the same floor,
so re-running overwrites with an identical result rather than a new random one.

No PIL, no numpy -- this machine has neither, so the PNG encoder is the ~40 lines
at the bottom of the file (zlib and struct are stdlib).

The look is 1950s vinyl composition tile, not ceramic:
  - Off-white and charcoal, never #fff and #000. Pure black/white tiles read as a
    debug checkerboard, which is exactly what a diner floor must not look like.
  - Flecks. The signature of VCT is the fine mottled grit suspended in each tile,
    pale in the dark tiles and grey in the light ones. Without it the tiles are
    flat plastic.
  - A hairline seam, not a grout channel. VCT is laid edge to edge; a wide grout
    line is a bathroom floor.
  - Per-tile offsets into the noise field, so no two tiles share a fleck pattern
    even though they all sample one wrapping texture.

Everything wraps. The noise is a wrapping value-noise grid, the flecks are clipped
inside tile bounds, and the tile count is even, so checker parity matches across
the seam when the texture repeats.
"""
from __future__ import annotations

import math
import os
import random
import struct
import zlib

# --- geometry ------------------------------------------------------------------
SIZE = 1024          # texture is SIZE x SIZE
TILES = 4            # tiles across one repeat; must be EVEN or the checker breaks
TS = SIZE // TILES   # 256 px per tile
SEAM = 2             # half-width of the seam line, px
BEVEL = 8            # px over which the tile edge rolls down into the seam
SEED = 0xD1DE12

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "Assets", "materials", "diner")

# --- palette (sRGB bytes) ------------------------------------------------------
PALE = (231, 226, 214)    # bone white, warmed -- diner tile yellows with age
DARK = (31, 30, 33)       # charcoal with a blue tilt, not black
SEAM_COL = (46, 43, 40)   # grime packed into the joint


def clamp(v, lo=0.0, hi=1.0):
    return lo if v < lo else hi if v > hi else v


def smoothstep(t):
    t = clamp(t)
    return t * t * (3.0 - 2.0 * t)


def value_noise(size, cells, seed):
    """Wrapping bilinear value noise, smoothstep-interpolated. Returns a flat list."""
    rnd = random.Random(seed)
    grid = [[rnd.random() for _ in range(cells)] for _ in range(cells)]
    scale = cells / size
    # Precompute the x axis once; it is identical for every row.
    xs = []
    for x in range(size):
        fx = x * scale
        x0 = int(fx)
        xs.append((x0 % cells, (x0 + 1) % cells, smoothstep(fx - x0)))
    out = [0.0] * (size * size)
    for y in range(size):
        fy = y * scale
        y0 = int(fy)
        ty = smoothstep(fy - y0)
        r0 = grid[y0 % cells]
        r1 = grid[(y0 + 1) % cells]
        base = y * size
        for x in range(size):
            x0, x1, tx = xs[x]
            a = r0[x0] + (r0[x1] - r0[x0]) * tx
            b = r1[x0] + (r1[x1] - r1[x0]) * tx
            out[base + x] = a + (b - a) * ty
    return out


def octaves(size, spec, seed):
    """Sum of value_noise layers, normalised to 0..1. spec is [(cells, amplitude)]."""
    total = [0.0] * (size * size)
    norm = 0.0
    for i, (cells, amp) in enumerate(spec):
        layer = value_noise(size, cells, seed + i * 7919)
        for j in range(size * size):
            total[j] += layer[j] * amp
        norm += amp
    return [v / norm for v in total]


print("generating noise fields...")
# Fine grit suspended in the tile, plus a broader cloudiness across it.
MOTTLE = octaves(SIZE, [(32, 1.0), (64, 0.6), (128, 0.35), (256, 0.2)], SEED)
# Very low frequency: traffic wear, where the shine gets walked off.
WEAR = octaves(SIZE, [(3, 1.0), (6, 0.5)], SEED + 4001)

# --- per-tile state ------------------------------------------------------------
rnd = random.Random(SEED + 99)
tile_offset = {}   # (i,j) -> noise sample offset, so tiles don't repeat each other
tile_shade = {}    # (i,j) -> brightness jitter; cut sheets never match exactly
fleck_map = {}     # (i,j) -> {(x,y): strength}

for j in range(TILES):
    for i in range(TILES):
        tile_offset[(i, j)] = (rnd.randrange(SIZE), rnd.randrange(SIZE))
        tile_shade[(i, j)] = rnd.uniform(-0.022, 0.022)
        # Flecks live strictly inside the tile so they never cross the seam, which
        # keeps the texture wrapping without any edge special-casing.
        m = {}
        # Dense and low-contrast. Sparse bright flecks read as a starfield, which
        # was the first thing wrong with this floor.
        for _ in range(760):
            r = rnd.choice((1, 1, 1, 1, 2, 2))
            cx = rnd.randrange(SEAM + r, TS - SEAM - r)
            cy = rnd.randrange(SEAM + r, TS - SEAM - r)
            s = rnd.uniform(0.35, 1.0)
            for dy in range(-r, r + 1):
                for dx in range(-r, r + 1):
                    if dx * dx + dy * dy > r * r:
                        continue
                    # Soften the rim so flecks don't alias into hard squares far off.
                    edge = 1.0 if r == 1 else clamp(1.2 - math.hypot(dx, dy) / r)
                    key = (cx + dx, cy + dy)
                    m[key] = max(m.get(key, 0.0), s * edge)
        fleck_map[(i, j)] = m


def seam_factor(tx, ty):
    """0 deep in the seam, 1 on the flat of the tile."""
    d = min(tx, TS - 1 - tx, ty, TS - 1 - ty)
    if d <= SEAM:
        return 0.0
    return smoothstep((d - SEAM) / BEVEL)


print("painting color...")
color_rows = []
for y in range(SIZE):
    row = bytearray()
    tj, ty = divmod(y, TS)
    for x in range(SIZE):
        ti, tx = divmod(x, TS)
        key = (ti, tj)
        pale = (ti + tj) % 2 == 0
        f = seam_factor(tx, ty)

        ox, oy = tile_offset[key]
        n = MOTTLE[((y + oy) % SIZE) * SIZE + ((x + ox) % SIZE)]
        w = WEAR[y * SIZE + x]

        base = PALE if pale else DARK
        # Mottle swings wider on the dark tiles -- grit shows more against charcoal.
        amp = 0.055 if pale else 0.16
        k = 1.0 + (n - 0.5) * 2.0 * amp + tile_shade[key]
        # Wear lifts the dark tiles (scuffed) and dulls the pale ones (ground-in dirt).
        k += (w - 0.5) * (-0.055 if pale else 0.10)

        r = base[0] * k
        g = base[1] * k
        b = base[2] * k

        fl = fleck_map[key].get((tx, ty))
        if fl:
            if pale:
                r += (176 - r) * 0.34 * fl
                g += (174 - g) * 0.34 * fl
                b += (170 - b) * 0.34 * fl
            else:
                # Pale grit in the black tile -- the signature VCT speckle.
                r += (152 - r) * 0.50 * fl
                g += (150 - g) * 0.50 * fl
                b += (146 - b) * 0.50 * fl

        if f < 1.0:
            # Roll into the seam: darken through the bevel, then hit the joint colour.
            t = f * f
            r = SEAM_COL[0] + (r - SEAM_COL[0]) * t
            g = SEAM_COL[1] + (g - SEAM_COL[1]) * t
            b = SEAM_COL[2] + (b - SEAM_COL[2]) * t

        row += bytes((int(clamp(r, 0, 255)), int(clamp(g, 0, 255)), int(clamp(b, 0, 255))))
    color_rows.append(row)

print("building height + normal...")
# Height field: flat tile faces, a groove at every seam, a whisper of surface texture.
height = [0.0] * (SIZE * SIZE)
for y in range(SIZE):
    tj, ty = divmod(y, TS)
    for x in range(SIZE):
        ti, tx = divmod(x, TS)
        key = (ti, tj)
        ox, oy = tile_offset[key]
        n = MOTTLE[((y + oy) % SIZE) * SIZE + ((x + ox) % SIZE)]
        f = seam_factor(tx, ty)
        h = 0.42 + 0.58 * f              # seam sits well below the tile face
        h += (n - 0.5) * 0.012 * f       # faint orange-peel on the face only
        fl = fleck_map[key].get((tx, ty))
        if fl:
            h += 0.010 * fl * f          # flecks stand a hair proud of the binder
        height[y * SIZE + x] = h

NORMAL_STRENGTH = 42.0
normal_rows = []
for y in range(SIZE):
    row = bytearray()
    up = ((y - 1) % SIZE) * SIZE
    dn = ((y + 1) % SIZE) * SIZE
    cur = y * SIZE
    for x in range(SIZE):
        xl = (x - 1) % SIZE
        xr = (x + 1) % SIZE
        dx = (height[cur + xr] - height[cur + xl]) * NORMAL_STRENGTH
        dy = (height[dn + x] - height[up + x]) * NORMAL_STRENGTH
        # OpenGL convention: +Y (green) points up the texture, so the downward-in-
        # image-space gradient is the one that stays positive here.
        nx, ny, nz = -dx, dy, 1.0
        inv = 1.0 / math.sqrt(nx * nx + ny * ny + 1.0)
        row += bytes((
            int(clamp(nx * inv * 0.5 + 0.5) * 255),
            int(clamp(ny * inv * 0.5 + 0.5) * 255),
            int(clamp(nz * inv * 0.5 + 0.5) * 255),
        ))
    normal_rows.append(row)

print("painting roughness + ao...")
rough_rows = []
ao_rows = []
for y in range(SIZE):
    rrow = bytearray()
    arow = bytearray()
    tj, ty = divmod(y, TS)
    for x in range(SIZE):
        ti, tx = divmod(x, TS)
        key = (ti, tj)
        ox, oy = tile_offset[key]
        n = MOTTLE[((y + oy) % SIZE) * SIZE + ((x + ox) % SIZE)]
        w = WEAR[y * SIZE + x]
        f = seam_factor(tx, ty)

        # Buffed tile is nearly a mirror; walked-on lanes go satin.
        rough = 0.19 + (n - 0.5) * 0.06 + w * 0.26
        fl = fleck_map[key].get((tx, ty))
        if fl:
            rough += 0.10 * fl          # grit doesn't take polish the way the binder does
        rough = rough + (0.88 - rough) * (1.0 - f)   # the joint is matte

        # Contact shading in the groove only; the open face is unoccluded.
        ao = 0.30 + 0.70 * smoothstep(f * 1.15)

        rv = int(clamp(rough) * 255)
        av = int(clamp(ao) * 255)
        rrow += bytes((rv, rv, rv))
        arow += bytes((av, av, av))
    rough_rows.append(rrow)
    ao_rows.append(arow)


# --- PNG encoder ---------------------------------------------------------------
def _filtered(rows, bpp):
    """Per-row adaptive filtering (None/Sub/Up) by the standard min-abs-sum heuristic."""
    out = bytearray()
    prev = bytes(len(rows[0]))
    for raw in rows:
        n = len(raw)
        sub = bytearray(n)
        up = bytearray(n)
        for i in range(n):
            sub[i] = (raw[i] - (raw[i - bpp] if i >= bpp else 0)) & 0xFF
            up[i] = (raw[i] - prev[i]) & 0xFF
        cands = (
            (sum(v if v < 128 else 256 - v for v in raw), 0, raw),
            (sum(v if v < 128 else 256 - v for v in sub), 1, sub),
            (sum(v if v < 128 else 256 - v for v in up), 2, up),
        )
        _, ftype, data = min(cands, key=lambda c: c[0])
        out += bytes((ftype,)) + bytes(data)
        prev = raw
    return bytes(out)


def write_png(path, rows, width):
    def chunk(tag, data):
        c = struct.pack(">I", len(data)) + tag + data
        return c + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    ihdr = struct.pack(">IIBBBBB", width, len(rows), 8, 2, 0, 0, 0)  # 8-bit truecolor
    body = zlib.compress(_filtered(rows, 3), 9)
    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr)
                + chunk(b"IDAT", body) + chunk(b"IEND", b""))
    print("  %s  %.0f KB" % (os.path.basename(path), os.path.getsize(path) / 1024))


os.makedirs(OUT, exist_ok=True)
print("encoding...")
write_png(os.path.join(OUT, "diner_tile_floor_color.png"), color_rows, SIZE)
write_png(os.path.join(OUT, "diner_tile_floor_normal.png"), normal_rows, SIZE)
write_png(os.path.join(OUT, "diner_tile_floor_rough.png"), rough_rows, SIZE)
write_png(os.path.join(OUT, "diner_tile_floor_ao.png"), ao_rows, SIZE)

VMAT = '''// Diner checkerboard tile floor -- 1950s vinyl composition tile.
// Written by tools/diner_tile_gen.py; re-running it overwrites the textures.
// Do not hand-edit the maps -- edit the generator.
//
// SCALE: one texture repeat is %d tiles across. s&box units are inches, so for 12"
// tiles repeat the UV every %d units; for the tighter 9" VCT a period diner would
// actually have been laid with, every %d.
Layer0
{
\tshader "shaders/complex.shader"
\tTextureColor "materials/diner/diner_tile_floor_color.png"
\tTextureNormal "materials/diner/diner_tile_floor_normal.png"
\tTextureRoughness "materials/diner/diner_tile_floor_rough.png"
\tTextureAmbientOcclusion "materials/diner/diner_tile_floor_ao.png"
\tg_flModelTintAmount "0.000000"
\tg_flMetalness "0.000000"
\tg_flRoughness "1.000000"
}
''' % (TILES, TILES * 12, TILES * 9)

with open(os.path.join(OUT, "diner_tile_floor.vmat"), "w", newline="\n") as f:
    f.write(VMAT)
print("wrote diner_tile_floor.vmat")
