# UltraBlur Jellyfin Plugin

Serves Plex-identical 4-corner ambient colors (Plex "UltraBlur") for Jellyfin
items. Runs a managed C# port of PMS's UltraBlur algorithm in-process
(`UltraBlur.Plugin/Algorithm/`, bit-exact against Plex ground truth; see
Bit-exactness) on each item's on-disk artwork, caches the result in SQLite,
and:

- **web**: auto-injects a client script into jellyfin-web (request-time
  middleware, no theme edits, survives web updates) that paints the ambient
  gradient on detail pages;
- **any other client** (Flutter/Plezy, Android, custom apps): plain
  authorized REST — `GET /UltraBlur/Colors/{itemId}`, a batch
  `POST /UltraBlur/Colors`, and `GET /UltraBlur/Colors/Url` for TMDB images
  outside the library.

## Architecture

```
jellyfin-web            ← ScriptInjectionStartupFilter injects
                          <script src="../UltraBlur/ClientScript"> into index.html
  ultrablur.js          → GET /UltraBlur/Colors/{itemId}   (authorized)
native clients          → GET  /UltraBlur/Colors/{itemId}
                        → POST /UltraBlur/Colors  [ids…]   (batch, max 100)
    → UltraBlurService.GetOrComputeAsync
      → cache hit (SQLite + in-memory)? return
      → ResolveImage: inherited backdrop-first chain (episode → season → series), JPEG/PNG/WebP
      → ImageLoader.Load        (host SkiaSharp decode + EXIF + PMS 8.3 MP resize)
      → UltraBlurCore.Extract   (histogram → median-cut → OKLab rank → render)
      → cache, return
```

- **Why managed, in-process:** nothing to install besides the plugin zip, any
  CPU arch/OS Jellyfin runs on, no process spawn per image (~50 ms per 4K
  backdrop). Bit-exactness comes from reproducing PMS's exact numerics:
  musl libm `cbrtf/powf/sinf/cosf/atan2f` (`MuslMath.cs`), PMS's old
  libc++ `std::sort` tie order (`LibCxx.cs`), and
  OpenCV 4.5.0 `INTER_LINEAR`/area-fast fixed-point resize (`ImageLoader.cs`).
  Decoding uses the host's SkiaSharp, which decodes JPEG/PNG/WebP
  byte-identical to Plex's OpenCV/libjpeg-turbo build (see Bit-exactness).
- **Cache invalidation:** rows are keyed `(ItemId, ImageType)` and carry an
  image tag derived from the image path + mtime; when artwork changes the tag
  mismatches and colors are recomputed.
- **Warm-up:** optional daily scheduled task precomputes colors for the most
  recently added movies/series/episodes.

## API

`GET /UltraBlur/Colors/{itemId}?imageType=Backdrop` (imageType optional)

```json
{
  "ItemId": "…",
  "ImageType": "Backdrop",
  "ImageTag": "…",
  "TopLeft": "#072065",
  "TopRight": "#2d519f",
  "BottomRight": "#1e5ea3",
  "BottomLeft": "#0e3c92",
  "Cached": true
}
```

Keys are PascalCase like the rest of the Jellyfin API; send
`Accept: application/json; profile="CamelCase"` for camelCase.

- `404` — item not found, or no usable JPEG image
- `422` — extraction failed (undecodable image, or no usable pixels, e.g. all-white)

Remote TMDB image (for titles that are not in the library, e.g. Seerr/Jellyseerr results):

```
GET /UltraBlur/Colors/Url?url=https://image.tmdb.org/t/p/original/<file>.jpg
```

Same response shape. Only `https://image.tmdb.org/t/p/<size>/<file>` (jpg/png/webp, no query) is
fetched, so the server cannot be pointed at other hosts; anything else is `400`. The image is
downloaded once (max 25 MB), run through the same bit-exact extractor, and cached by URL.

Batch (for native clients priming list views):

```
POST /UltraBlur/Colors
["itemId1", "itemId2", …]            (max 100)
→ { "itemId1": { …colors… }, "itemId2": null, … }
```

Corner semantics match Plex's `/service/ultrablur/colors`: top-left is
systematically the darkest (V_CEIL 0.2 vs 0.4) to keep overlaid text legible.

## Native clients (non-web)

Colors are plain Jellyfin-authorized HTTP — any client that talks to the
server can use them. Flutter/Plezy example:

```dart
final res = await dio.get(
  '$serverUrl/UltraBlur/Colors/$itemId',
  options: Options(headers: {
    'Authorization': 'MediaBrowser Token="$accessToken"',
  }),
);
// res.data: { TopLeft: "#072065", TopRight: …, BottomRight: …, BottomLeft: … }
// Feed straight into the existing UltraBlurBackground painter (same corner
// order as Plex's inline UltraBlurColors metadata).
```

Use the batch `POST` when entering a library grid so detail navigation is
instant; the plugin caches server-side either way.

## Build

Two build targets (pattern from Jellyfin-Enhanced), one artifact per runtime:

```bash
dotnet build UltraBlur.Plugin -c Release -p:JellyfinTarget=jf10   # Jellyfin 10.11, .NET 9 (default)
dotnet build UltraBlur.Plugin -c Release -p:JellyfinTarget=jf12   # Jellyfin 12, .NET 10
```

Packages are pinned to the lowest version of each line (10.11.0 / 12.0.0-rc1)
so the artifact loads across the whole line.

## Install (plugin repository)

In Jellyfin 10.11: **Dashboard → Plugins → Repositories → +** (the
"Manage repositories" button on the Catalog page), add

```
https://raw.githubusercontent.com/HASANALI117/ultrablur-jellyfin/main/manifest.json
```

then **Catalog → UltraBlur → Install** and restart Jellyfin. Updates show up
in the catalog like any other plugin.

Releasing a new version (maintainer):

```bash
# bump <AssemblyVersion> in UltraBlur.Plugin.csproj, then
./package.sh jf10        # → out/UltraBlur_<version>_jf10.zip + manifest.json entry
# write the entry's changelog, then publish the zip as release v<version>
gh release create v<version> out/UltraBlur_<version>_jf10.zip --title "UltraBlur <version>" --notes "<changelog>"
# only then commit + push manifest.json to main (the feed points at the release asset)
```

Run `./package.sh jf12` too and attach its zip to the same release
(`gh release upload`): the feed then has a 12.0.0.0 entry (listed first, so
Jellyfin 12 picks it) next to the 10.11.0.0 one.

## Deploy (manual copy)

Alternative to the repository feed. No native binary is needed (0.2.x
required `/opt/ultrablur/ultrablur_cli`; it can be deleted).

```bash
ssh root@<lxc> mkdir -p /var/lib/jellyfin/plugins/UltraBlur_0.3.2.0
curl -LO https://github.com/HASANALI117/ultrablur-jellyfin/releases/download/v0.3.2.0/UltraBlur_0.3.2.0_jf10.zip
scp UltraBlur_0.3.2.0_jf10.zip root@<lxc>:/tmp/
ssh root@<lxc> 'cd /var/lib/jellyfin/plugins/UltraBlur_0.3.2.0 && unzip -o /tmp/UltraBlur_0.3.2.0_jf10.zip'
ssh root@<lxc> systemctl restart jellyfin
```

## Verify

```bash
# 1. Endpoint (item id from the web UI URL or /Items API)
curl -H 'Authorization: MediaBrowser Token=<apikey>' \
  'http://<lxc>:8096/UltraBlur/Colors/<itemId>'

# 2. Byte-for-byte check: find the item's backdrop path (metadata dir),
#    run ../pms/cpp-linux/dist/standalone/ultrablur_cli on that exact file,
#    and compare with the JSON response.

# 3. Dashboard → Scheduled Tasks → "UltraBlur Color Warm-up"; afterwards a
#    second curl returns "cached": true and rows exist in
#    /var/lib/jellyfin/data/plugins/UltraBlur/colors.db
```

## Bit-exactness

`tests/UltraBlur.Bench` compiles `Algorithm/*.cs` standalone and diffs it
against expected corner hex (exit 1 on any mismatch):

```bash
cd tests/UltraBlur.Bench
dotnet run -c Release -- ../bench-data/gt_recap.txt   ../../../dataset/thumbs   # 99/99 vs Plex GT
dotnet run -c Release -- ../bench-data/gt_dataset.txt ../../../dataset/thumbs   # 199/199 vs Plex GT
python -I ../bench-data/gen_edge.py ../../../dataset/thumbs                      # regenerate edge/
dotnet run -c Release -- ../bench-data/gt_edge.txt    ../bench-data/edge        # 39/39 vs Plex GT
python -I ../bench-data/gen_fuzz.py                                              # regenerate fuzz/
dotnet run -c Release -- ../bench-data/gt_fuzz.txt    ../bench-data/fuzz        # 945/945 vs Plex GT
```

- Expected values are Plex ground truth from PMS 1.43.4's
  `/service/ultrablur/colors?url=` (each image served over HTTP). That
  endpoint returns HTTP 500 for WebP, so the two WebP edge rows come from the
  C++ CLI instead (they check decode, not the algorithm).
- PMS's median-cut is MMCQ (quantize.js-style): a vector re-sorted before
  every pop, empty/zero-width second boxes still count as colors, and the
  cut walks down to the first non-empty slice when nothing lies right of it.
  Exact grays give NaN hue (`a/C`, no guard) which rank and render read as
  S = 0. These, plus float association order in `get_Cs` and the render
  inverse curve, were the three former GT misses (1762, 2139, 2630); the
  C++ CLI in `../pms/cpp-linux` still has them.
- Fuzz set (`gen_fuzz.py`, seed 1015): noise, few-color palettes, exact and
  near grays, near-white, saturated, gradients, dark, tiny, >8.3 MP JPEG,
  JPEG q40-99, and 600 four-quadrant solid swatches (2400 random colors). A
  second seed (`gen_fuzz.py fuzz2 7`) is also 945/945. The fuzz set found what
  library art never hit: Björn's `(1 - mid)` is `1.0f - 0.8f` = 0x3e4ccccc,
  one ULP below `0.2f`, in both the forward score and the render curve.
- Edge set: >8.3 MP resize (generic, exact-2x area-fast, near-1, 3x), EXIF
  orientations 1-8 (MM + II), PNG RGB/RGBA/gray/16-bit/palette, WebP
  lossy/lossless, progressive/gray/4:4:4/4:2:2 JPEG, degenerate sizes.
- Also passes on linux-x64 with SkiaSharp 3.116.1 (Jellyfin 10.11) and 3.119.4
  (Jellyfin 12): `dotnet publish -c Release -r linux-x64 --self-contained
  -p:SkiaVersion=3.119.4 -o bin/linux-3.119.4`, then run the binary in WSL.
- PMS 1.43.4.10903: UltraBlur code is byte-identical to 1.42.2 (modulo
  relocations), so the port is current. Diff tooling: `../pms/RE/diff_1.43/`.

## Web client script (bundled — no theme edits needed)

`ScriptInjectionStartupFilter` injects `js/ultrablur.js` (embedded resource,
served anonymously from `GET /UltraBlur/ClientScript`) into jellyfin-web's
`index.html` at request time. On detail pages it fetches the item's colors,
and paints the Plex-style 4-corner gradient as the page background. jellyfin-web
stacks an opaque `.backgroundContainer` over `.backdropContainer`, so the
gradient goes on `.backgroundContainer`; the title ribbon is made transparent
and the banner fades into it. Colors are CSS custom properties on `<html>`:

```
--ultrablur-tl  --ultrablur-tr  --ultrablur-br  --ultrablur-bl
```

plus an `ultrablur-active` class — so a custom theme can override the default
gradient with its own CSS while the plugin handles fetching/caching. Disable
the script entirely via the **Enable web client script** config toggle if the
theme does its own fetching.
