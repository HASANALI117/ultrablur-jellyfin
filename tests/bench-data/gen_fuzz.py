#!/usr/bin/env python3
# Synthetic images aimed at UltraBlur's edge paths: exact grays (NaN hue),
# near-grays, few-color palettes (empty/one-pixel boxes in median-cut),
# near-white (pixel filter, white corners), saturated (render high branch),
# gradients, noise, tiny and >8.3 MP sizes, JPEG at several qualities.
# Also 4-quadrant solid swatches (random colors straight through the color
# math). Deterministic per seed (default 1015). Output: fuzz/ (gitignored).
# Expected values (gt_fuzz.txt) are Plex GT from PMS /service/ultrablur/colors.
# Usage: python -I gen_fuzz.py [outdir] [seed]
import os
import sys

import numpy as np
from PIL import Image

out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(__file__), "fuzz")
os.makedirs(out, exist_ok=True)
rng = np.random.default_rng(int(sys.argv[2]) if len(sys.argv) > 2 else 1015)


def size(lo=48, hi=900):
    return int(rng.integers(lo, hi)), int(rng.integers(lo, hi))


def save(name, a, fmt="png", q=90):
    img = Image.fromarray(np.clip(a, 0, 255).astype(np.uint8), "RGB")
    if fmt == "png":
        img.save(os.path.join(out, name + ".png"))
    else:
        img.save(os.path.join(out, name + ".jpg"), quality=q)


def blocks(w, h, colors, n):
    a = np.empty((h, w, 3), np.uint8)
    a[:] = colors[0]
    for _ in range(n):
        x0, y0 = rng.integers(0, w), rng.integers(0, h)
        x1, y1 = x0 + rng.integers(1, w // 2 + 2), y0 + rng.integers(1, h // 2 + 2)
        a[y0:y1, x0:x1] = colors[rng.integers(len(colors))]
    return a


def gradient(w, h, c0, c1, radial=False):
    if radial:
        yy, xx = np.mgrid[0:h, 0:w]
        t = np.hypot(xx - w / 2, yy - h / 2) / np.hypot(w / 2, h / 2)
    else:
        ang = rng.uniform(0, np.pi)
        yy, xx = np.mgrid[0:h, 0:w]
        t = xx * np.cos(ang) + yy * np.sin(ang)
        t = (t - t.min()) / max(np.ptp(t), 1)
    return np.asarray(c0) * (1 - t[..., None]) + np.asarray(c1) * t[..., None]


def rand_color():
    return rng.integers(0, 256, 3)


def sat_color():
    c = np.zeros(3)
    c[rng.integers(3)] = 255
    c[rng.integers(3)] = rng.integers(0, 256)
    return c


for i in range(40):
    w, h = size()
    save(f"noise_{i:02d}", rng.integers(0, 256, (h, w, 3)))
for i in range(60):
    w, h = size()
    k = int(rng.integers(1, 13))
    save(f"palette_{i:02d}", blocks(w, h, [rand_color() for _ in range(k)], int(rng.integers(0, 30))))
for i in range(40):
    w, h = size()
    levels = rng.integers(0, 256, int(rng.integers(1, 6)))
    g = blocks(w, h, [np.array([v, v, v]) for v in levels], int(rng.integers(0, 20)))
    if i % 2:
        g = g.astype(int) + rng.integers(-20, 21, (h, w, 1))  # gray noise, still R==G==B
    save(f"gray_{i:02d}", g)
for i in range(30):
    w, h = size()
    v = rng.integers(0, 256, (h, w, 1))
    save(f"neargray_{i:02d}", v + rng.integers(-int(i % 3 + 1), int(i % 3 + 2), (h, w, 3)))
for i in range(40):
    w, h = size()
    save(f"grad_{i:02d}", gradient(w, h, rand_color(), rand_color(), radial=bool(i % 3 == 0)))
for i in range(30):
    w, h = size()
    save(f"sat_{i:02d}", blocks(w, h, [sat_color() for _ in range(int(rng.integers(1, 6)))], int(rng.integers(0, 15))))
for i in range(30):
    w, h = size()
    a = np.full((h, w, 3), 255, int) - rng.integers(0, 6, (h, w, 3))  # mostly filtered (>= 251)
    for _ in range(int(rng.integers(0, 6))):
        x0, y0 = rng.integers(0, w), rng.integers(0, h)
        a[y0:y0 + rng.integers(1, h // 3 + 2), x0:x0 + rng.integers(1, w // 3 + 2)] = rand_color()
    save(f"white_{i:02d}", a)
for i in range(20):
    w, h = size()
    save(f"dark_{i:02d}", rng.integers(0, int(rng.integers(2, 24)), (h, w, 3)))
for i in range(20):
    w, h = int(rng.integers(2, 17)), int(rng.integers(2, 17))
    save(f"tiny_{i:02d}", rng.integers(0, 256, (h, w, 3)))
for i in range(30):
    w, h = size(200, 1600)
    kind = i % 3
    a = (gradient(w, h, rand_color(), rand_color()) + rng.normal(0, 12, (h, w, 3)) if kind == 0
         else blocks(w, h, [rand_color() for _ in range(6)], 20) if kind == 1
         else rng.integers(0, 256, (h, w, 3)))
    save(f"jpg_{i:02d}", a, "jpg", q=int(rng.integers(40, 100)))
for i, (w, h) in enumerate([(3840, 2200), (4096, 2304), (5000, 3000), (3600, 2400), (7680, 4320)]):
    # Upscaled small gradient + int16 noise keeps memory sane at 33 MP.
    small = Image.fromarray(np.clip(gradient(w // 16, h // 16, rand_color(), rand_color()), 0, 255).astype(np.uint8))
    a = np.asarray(small.resize((w, h), Image.BILINEAR), np.int16) + rng.integers(-8, 9, (h, w, 3), dtype=np.int16)
    save(f"big_{i}", a, "jpg", q=85)

for i in range(600):
    q = int(rng.integers(2, 12))
    cols = rng.integers(0, 256, (4, 3))
    if i % 4 == 1:
        cols[:] = rng.integers(0, 256, (4, 1))  # exact grays
    elif i % 4 == 2:
        cols[np.arange(4), rng.integers(0, 3, 4)] = rng.choice([0, 255], 4)  # saturated
    a = np.empty((2 * q, 2 * q, 3), int)
    a[:q, :q], a[:q, q:], a[q:, q:], a[q:, :q] = cols
    save(f"swatch_{i:03d}", a)

print(len(os.listdir(out)), "files in", out)
