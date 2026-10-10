#!/usr/bin/env python3
"""LoupixDeck Speedtest plugin icon.

The Material Design Icons "speedometer" glyph on a night-blue rounded tile, with a
teal-to-lime gradient arc, a white needle and download / upload arrows.

Requires: pip install pillow numpy
Usage:    python make_icon.py [output_folder]
Writes icon_{256,128,64,32,16}.png (RGBA, transparent corners).
"""
import math
import os
import re
import sys

import numpy as np
from PIL import Image, ImageDraw

DESIGN = 512  # design size (px), shared with the glyph transform below
SS = 4        # supersampling
N = DESIGN * SS

# Material Design Icons "speedometer" (Pictogrammers, Apache-2.0), 24x24 viewBox.
NEEDLE = ("M12,16A3,3 0 0,1 9,13C9,11.88 9.61,10.9 10.5,10.39L20.21,4.77L14.68,14.35"
          "C14.18,15.33 13.17,16 12,16Z")
ARC = ("M12,3C13.81,3 15.5,3.5 16.97,4.32L14.87,5.53C14,5.19 13,5 12,5A8,8 0 0,0 4,13"
       "C4,15.21 4.89,17.21 6.34,18.65H6.35C6.74,19.04 6.74,19.67 6.35,20.06"
       "C5.96,20.45 5.32,20.45 4.93,20.07V20.07C3.12,18.26 2,15.76 2,13A10,10 0 0,1 12,3"
       "M22,13C22,15.76 20.88,18.26 19.07,20.07V20.07C18.68,20.45 18.05,20.45 17.66,20.06"
       "C17.27,19.67 17.27,19.04 17.66,18.65V18.65C19.11,17.2 20,15.21 20,13"
       "C20,12 19.81,11 19.46,10.1L20.67,8C21.5,9.5 22,11.18 22,13Z")

# Glyph (24 units) -> design pixels.
GLYPH_X, GLYPH_Y, GLYPH_SCALE = 64.0, 56.0, 16.0

BG_TOP = "#25475E"
BG_BOTTOM = "#142E40"
TRACK = "#35566C"
ARC_FROM = "#19D3B0"
ARC_TO = "#B6F23A"
NEEDLE_COLOR = "#FFFFFF"
DOWN_COLOR = "#4DB2FF"
UP_COLOR = "#B6F23A"

# Value arc: gradient span and clip wedge (glyph units), round cap at the needle end.
GRADIENT_X = (2.0, 18.0)
VALUE_CLIP = [(12, 13), (41.1, 5.8), (41, -20), (-20, -20), (-20, 40), (12, 40)]
CAP_CENTER, CAP_RADIUS = (20.73, 10.82), 1.0

DOWN_ARROW = [[(222, 336), (222, 392)], [(204, 374), (222, 392), (240, 374)]]
UP_ARROW = [[(290, 392), (290, 336)], [(272, 354), (290, 336), (308, 354)]]
ARROW_WIDTH = 14
TILE_RADIUS = 112


def rgb(hex_color):
    h = hex_color.lstrip("#")
    return np.array([int(h[i:i + 2], 16) for i in (0, 2, 4)], dtype=np.float32) / 255.0


# ---------- SVG path flattening ----------
def _arc_points(p0, rx, ry, rot, large, sweep, p1, steps=48):
    (x1, y1), (x2, y2) = p0, p1
    if rx == 0 or ry == 0 or p0 == p1:
        return [p1]
    phi = math.radians(rot)
    cp, sp = math.cos(phi), math.sin(phi)
    dx, dy = (x1 - x2) / 2, (y1 - y2) / 2
    x1p, y1p = cp * dx + sp * dy, -sp * dx + cp * dy
    rx, ry = abs(rx), abs(ry)
    lam = x1p ** 2 / rx ** 2 + y1p ** 2 / ry ** 2
    if lam > 1:
        rx, ry = rx * math.sqrt(lam), ry * math.sqrt(lam)
    num = rx ** 2 * ry ** 2 - rx ** 2 * y1p ** 2 - ry ** 2 * x1p ** 2
    den = rx ** 2 * y1p ** 2 + ry ** 2 * x1p ** 2
    coef = math.sqrt(max(num / den, 0)) * (-1 if large == sweep else 1)
    cxp, cyp = coef * rx * y1p / ry, -coef * ry * x1p / rx
    cx = cp * cxp - sp * cyp + (x1 + x2) / 2
    cy = sp * cxp + cp * cyp + (y1 + y2) / 2

    def angle(ux, uy, vx, vy):
        a = math.atan2(ux * vy - uy * vx, ux * vx + uy * vy)
        return a

    t1 = angle(1, 0, (x1p - cxp) / rx, (y1p - cyp) / ry)
    dt = angle((x1p - cxp) / rx, (y1p - cyp) / ry, (-x1p - cxp) / rx, (-y1p - cyp) / ry)
    if not sweep and dt > 0:
        dt -= 2 * math.pi
    elif sweep and dt < 0:
        dt += 2 * math.pi
    pts = []
    for i in range(1, steps + 1):
        t = t1 + dt * i / steps
        x = cp * rx * math.cos(t) - sp * ry * math.sin(t) + cx
        y = sp * rx * math.cos(t) + cp * ry * math.sin(t) + cy
        pts.append((x, y))
    return pts


def flatten(path):
    """Flatten an SVG path (M L H V C A Z, absolute) into a list of closed polygons."""
    tokens = re.findall(r"[MLHVCAZ]|-?\d*\.?\d+", path)
    polys, cur, pos, i = [], [], (0.0, 0.0), 0

    def num():
        nonlocal i
        v = float(tokens[i])
        i += 1
        return v

    cmd = None
    while i < len(tokens):
        if tokens[i].isalpha():
            cmd = tokens[i]
            i += 1
        if cmd == "M":
            if cur:
                polys.append(cur)
            pos = (num(), num())
            cur = [pos]
            cmd = "L"
        elif cmd == "L":
            pos = (num(), num())
            cur.append(pos)
        elif cmd == "H":
            pos = (num(), pos[1])
            cur.append(pos)
        elif cmd == "V":
            pos = (pos[0], num())
            cur.append(pos)
        elif cmd == "C":
            c1, c2, end = (num(), num()), (num(), num()), (num(), num())
            for k in range(1, 25):
                t = k / 24
                u = 1 - t
                cur.append((u ** 3 * pos[0] + 3 * u * u * t * c1[0] + 3 * u * t * t * c2[0] + t ** 3 * end[0],
                            u ** 3 * pos[1] + 3 * u * u * t * c1[1] + 3 * u * t * t * c2[1] + t ** 3 * end[1]))
            pos = end
        elif cmd == "A":
            rx, ry, rot = num(), num(), num()
            large, sweep = int(num()), int(num())
            end = (num(), num())
            cur.extend(_arc_points(pos, rx, ry, rot, large, sweep, end))
            pos = end
        elif cmd == "Z":
            polys.append(cur)
            cur = []
    if cur:
        polys.append(cur)
    return polys


# ---------- Masks ----------
def glyph_to_px(pt):
    return ((GLYPH_X + pt[0] * GLYPH_SCALE) * SS, (GLYPH_Y + pt[1] * GLYPH_SCALE) * SS)


def mask_from(draw_fn):
    im = Image.new("L", (N, N), 0)
    draw_fn(ImageDraw.Draw(im))
    return np.asarray(im, dtype=np.float32) / 255.0


def glyph_mask(path):
    polys = flatten(path)
    return mask_from(lambda d: [d.polygon([glyph_to_px(p) for p in poly], fill=255) for poly in polys])


def polygon_mask(points_glyph):
    return mask_from(lambda d: d.polygon([glyph_to_px(p) for p in points_glyph], fill=255))


def circle_mask(center_glyph, radius_glyph):
    cx, cy = glyph_to_px(center_glyph)
    r = radius_glyph * GLYPH_SCALE * SS
    return mask_from(lambda d: d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=255))


def stroke_mask(polylines, width):
    w, r = width * SS, width * SS / 2

    def draw(d):
        for line in polylines:
            pts = [(x * SS, y * SS) for x, y in line]
            d.line(pts, fill=255, width=int(w))
            for x, y in pts:  # round caps and joins
                d.ellipse([x - r, y - r, x + r, y + r], fill=255)

    return mask_from(draw)


def rounded_tile_mask():
    s = DESIGN * SS
    return mask_from(lambda d: d.rounded_rectangle([0, 0, s - 1, s - 1], radius=TILE_RADIUS * SS, fill=255))


# ---------- Compositing ----------
def paint(canvas, mask, color):
    """Alpha-composite `color` (3-vector or HxWx3 array) through `mask` onto the RGBA canvas."""
    a = mask[..., None]
    canvas[..., :3] = canvas[..., :3] * (1 - a) + (np.broadcast_to(color, canvas[..., :3].shape)) * a
    canvas[..., 3:] = canvas[..., 3:] + a * (1 - canvas[..., 3:])


def build():
    canvas = np.zeros((N, N, 4), dtype=np.float32)

    ys = ((np.arange(N, dtype=np.float32) + 0.5) / N)[:, None, None]
    bg = rgb(BG_TOP) * (1 - ys) + rgb(BG_BOTTOM) * ys
    paint(canvas, rounded_tile_mask(), np.broadcast_to(bg, (N, N, 3)))

    arc = glyph_mask(ARC)
    paint(canvas, arc, rgb(TRACK))

    xs = (np.arange(N, dtype=np.float32) + 0.5) / SS
    xg = (xs - GLYPH_X) / GLYPH_SCALE
    t = np.clip((xg - GRADIENT_X[0]) / (GRADIENT_X[1] - GRADIENT_X[0]), 0, 1)[None, :, None]
    gradient = rgb(ARC_FROM) * (1 - t) + rgb(ARC_TO) * t
    paint(canvas, arc * polygon_mask(VALUE_CLIP), np.broadcast_to(gradient, (N, N, 3)))
    paint(canvas, circle_mask(CAP_CENTER, CAP_RADIUS), rgb(ARC_TO))

    paint(canvas, glyph_mask(NEEDLE), rgb(NEEDLE_COLOR))
    paint(canvas, stroke_mask(DOWN_ARROW, ARROW_WIDTH), rgb(DOWN_COLOR))
    paint(canvas, stroke_mask(UP_ARROW, ARROW_WIDTH), rgb(UP_COLOR))

    # Un-premultiply: colors above were blended over transparent black.
    alpha = np.clip(canvas[..., 3:], 0, 1)
    rgb_out = np.where(alpha > 0, canvas[..., :3] / np.maximum(alpha, 1e-6), 0)
    out = np.concatenate([rgb_out, alpha], axis=-1)
    return Image.fromarray((np.clip(out, 0, 1) * 255 + 0.5).astype(np.uint8), "RGBA")


def main():
    out_dir = sys.argv[1] if len(sys.argv) > 1 else "."
    os.makedirs(out_dir, exist_ok=True)
    master = build()
    for size in (256, 128, 64, 32, 16):
        path = os.path.join(out_dir, f"icon_{size}.png")
        master.resize((size, size), Image.LANCZOS).save(path)
        print("wrote", path)


if __name__ == "__main__":
    main()
