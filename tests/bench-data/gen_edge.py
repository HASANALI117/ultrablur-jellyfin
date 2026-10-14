#!/usr/bin/env python3
# Generate edge-case images for the managed-port bench: resize paths (>8.3 MP,
# exact 2x area-fast, near-1 scale, >2x), EXIF orientations 2-8 (MM + II),
# PNG variants (RGB/RGBA/gray/16-bit/palette), WebP lossy/lossless, JPEG
# progressive/gray/444, and degenerate sizes. Output: edge/ (gitignored).
# Expected values come from the bit-exact CLI (run under WSL):
#   for f in edge/*; do echo "$(basename $f) $(ultrablur_cli $f 2>&1)"; done > edge_cli.txt
# Usage: python -I gen_edge.py <dataset/thumbs>
import os
import struct
import sys

import cv2
import numpy as np
from PIL import Image

src = sys.argv[1]
out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "edge")
os.makedirs(out, exist_ok=True)
rng = np.random.default_rng(1)

a = cv2.imread(os.path.join(src, "1754.art.jpg"), cv2.IMREAD_COLOR)
b = cv2.imread(os.path.join(src, "2695.art.jpg"), cv2.IMREAD_COLOR)


def big(img, w, h):
    r = cv2.resize(img, (w, h), interpolation=cv2.INTER_CUBIC).astype(np.int16)
    r += rng.integers(-12, 13, r.shape, dtype=np.int16)
    return np.clip(r, 0, 255).astype(np.uint8)


def w(name, img, params=()):
    ok, buf = cv2.imencode(os.path.splitext(name)[1], img, list(params))
    assert ok
    open(os.path.join(out, name), "wb").write(buf.tobytes())


# --- resize paths (MAX_PIXELS = 0x7e9000 = 8294400) ---
w("big_generic.jpg", big(a, 4000, 2600), [cv2.IMWRITE_JPEG_QUALITY, 90])
w("big_2x.jpg", big(b, 7680, 4320), [cv2.IMWRITE_JPEG_QUALITY, 85])
w("big_near1.jpg", big(a, 3841, 2160), [cv2.IMWRITE_JPEG_QUALITY, 90])
w("big_3x.jpg", big(b, 9000, 6000), [cv2.IMWRITE_JPEG_QUALITY, 80])
w("big_odd.png", big(b, 3001, 2999))


# --- EXIF orientation (APP1 inserted after the JFIF APP0) ---
def with_exif(jpg, orient, le):
    bo = "<" if le else ">"
    tiff = (b"II*\x00" if le else b"MM\x00*") + struct.pack(bo + "I", 8)
    tiff += struct.pack(bo + "H", 1) + struct.pack(bo + "HHIHH", 0x0112, 3, 1, orient, 0)
    tiff += struct.pack(bo + "I", 0)
    payload = b"Exif\x00\x00" + tiff
    app1 = b"\xff\xe1" + struct.pack(">H", len(payload) + 2) + payload
    assert jpg[:4] == b"\xff\xd8\xff\xe0"
    app0_end = 4 + struct.unpack(">H", jpg[4:6])[0]
    return jpg[:app0_end] + app1 + jpg[app0_end:]


base = cv2.imencode(".jpg", b, [cv2.IMWRITE_JPEG_QUALITY, 92])[1].tobytes()
for o in range(1, 9):
    for le in (False, True):
        name = "exif_%d_%s.jpg" % (o, "ii" if le else "mm")
        open(os.path.join(out, name), "wb").write(with_exif(base, o, le))

# --- PNG variants ---
w("png_rgb.png", b)
alpha = np.tile(np.linspace(0, 255, b.shape[1], dtype=np.uint8), (b.shape[0], 1))
w("png_rgba.png", np.dstack([b, alpha]))
w("png_gray.png", cv2.cvtColor(b, cv2.COLOR_BGR2GRAY))
w("png_16.png", (b.astype(np.uint16) * 257 + rng.integers(0, 257, b.shape, dtype=np.uint16)))
Image.fromarray(cv2.cvtColor(a, cv2.COLOR_BGR2RGB)).quantize(64).save(os.path.join(out, "png_palette.png"))

# --- WebP ---
w("webp_lossy.webp", a, [cv2.IMWRITE_WEBP_QUALITY, 80])
w("webp_lossless.webp", b, [cv2.IMWRITE_WEBP_QUALITY, 101])

# --- JPEG variants ---
w("jpg_progressive.jpg", a, [cv2.IMWRITE_JPEG_QUALITY, 88, cv2.IMWRITE_JPEG_PROGRESSIVE, 1])
w("jpg_gray.jpg", cv2.cvtColor(a, cv2.COLOR_BGR2GRAY))
w("jpg_444.jpg", b, [cv2.IMWRITE_JPEG_SAMPLING_FACTOR, cv2.IMWRITE_JPEG_SAMPLING_FACTOR_444])
w("jpg_422.jpg", b, [cv2.IMWRITE_JPEG_SAMPLING_FACTOR, cv2.IMWRITE_JPEG_SAMPLING_FACTOR_422])

# --- degenerate ---
w("tiny_1x1.png", b[:1, :1])
w("tiny_2x2.png", b[:2, :2])
w("tiny_9x7.png", b[100:107, 200:209])
w("odd_801x601.png", b[:601, :801])
w("all_white.png", np.full((300, 400, 3), 255, np.uint8))
half = b[:400, :600].copy()
half[:200, :300] = 252
w("white_tl.png", half)
w("flat_gray.png", np.full((300, 400, 3), 128, np.uint8))
print("wrote", len(os.listdir(out)), "files to", out)
