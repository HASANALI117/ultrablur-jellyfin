# UltraBlur Jellyfin Plugin

Serves Plex-identical 4-corner ambient colors (Plex "UltraBlur") for Jellyfin
items. Wraps the bit-exact C++ port at `pms/cpp-linux` — the plugin shells out
to the statically linked `ultrablur_cli` (zero dependencies, x86_64 Linux) on
each item's on-disk artwork, caches the result in SQLite, and:

- **web**: auto-injects a client script into jellyfin-web (request-time
  middleware, no theme edits, survives web updates) that paints the ambient
  gradient on detail pages;
- **any other client** (Flutter/Plezy, Android, custom apps): plain
  authorized REST — `GET /UltraBlur/Colors/{itemId}` and a batch
  `POST /UltraBlur/Colors`.

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
      → subprocess: ultrablur_cli <image path>   (bit-exact Plex algorithm)
      → parse "topLeft=… topRight=… bottomRight=… bottomLeft=…", cache, return
```

- **Why subprocess, not P/Invoke:** matches the repo's other Jellyfin plugins
  (intro-detect, credit-detect); a native crash can't take down Jellyfin; the
  static musl binary needs nothing bundled. The `extern "C"
  ultrablur_extract_jpeg` ABI in `pms/cpp-linux` remains available if
  in-process P/Invoke is ever wanted (add a SHARED target to its CMakeLists).
- **Cache invalidation:** rows are keyed `(ItemId, ImageType)` and carry an
  image tag derived from the image path + mtime; when artwork changes the tag
  mismatches and colors are recomputed.
- **Warm-up:** optional daily scheduled task precomputes colors for the most
  recently added movies/series/episodes.

## API

`GET /UltraBlur/Colors/{itemId}?imageType=Backdrop` (imageType optional)

```json
{
  "itemId": "…",
  "imageType": "Backdrop",
  "imageTag": "…",
  "topLeft": "#072065",
  "topRight": "#2d519f",
  "bottomRight": "#1e5ea3",
  "bottomLeft": "#0e3c92",
  "cached": true
}
```

- `404` — item not found, or no usable JPEG image
- `422` — extraction failed (corrupt image, CLI timeout)

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
// res.data: { topLeft: "#072065", topRight: …, bottomRight: …, bottomLeft: … }
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

## Package + one-click install (plugin repository)

```bash
./package.sh jf10        # → out/UltraBlur_<version>_jf10.zip, patches manifest.json checksum
```

Host `manifest.json` + the zip on any HTTP server reachable by Jellyfin (edit
the `sourceUrl` host in `manifest.json`), then add the manifest URL under
**Dashboard → Plugins → Repositories**. UltraBlur then appears in the catalog
for one-click install and updates.

## Deploy (Jellyfin LXC)

1. Copy the CLI (already built — `pms/cpp-linux/dist/standalone/ultrablur_cli`):

   ```bash
   scp pms/cpp-linux/dist/standalone/ultrablur_cli root@<lxc>:/opt/ultrablur/
   ssh root@<lxc> chmod +x /opt/ultrablur/ultrablur_cli
   ```

2. Copy the plugin (manual alternative to the repository feed):

   ```bash
   ./package.sh jf10
   ssh root@<lxc> mkdir -p /var/lib/jellyfin/plugins/UltraBlur_0.2.0
   scp out/UltraBlur_0.2.0.0_jf10.zip root@<lxc>:/tmp/
   ssh root@<lxc> 'cd /var/lib/jellyfin/plugins/UltraBlur_0.2.0 && unzip -o /tmp/UltraBlur_0.2.0.0_jf10.zip'
   ssh root@<lxc> systemctl restart jellyfin
   ```

3. Configure in Dashboard → Plugins → UltraBlur if the CLI lives elsewhere
   than `/opt/ultrablur/ultrablur_cli`.

## Verify

```bash
# 1. CLI sanity on the server
/opt/ultrablur/ultrablur_cli /path/to/some/backdrop.jpg
# topLeft=… topRight=… bottomRight=… bottomLeft=…

# 2. Endpoint (item id from the web UI URL or /Items API)
curl -H 'Authorization: MediaBrowser Token=<apikey>' \
  'http://<lxc>:8096/UltraBlur/Colors/<itemId>'

# 3. Byte-for-byte check: find the item's backdrop path (metadata dir),
#    run the CLI on that exact file, and compare with the JSON response.

# 4. Dashboard → Scheduled Tasks → "UltraBlur Color Warm-up"; afterwards a
#    second curl returns "cached": true and rows exist in
#    /var/lib/jellyfin/data/plugins/UltraBlur/colors.db
```

## Web client script (bundled — no theme edits needed)

`ScriptInjectionStartupFilter` injects `js/ultrablur.js` (embedded resource,
served anonymously from `GET /UltraBlur/ClientScript`) into jellyfin-web's
`index.html` at request time. On detail pages it fetches the item's colors,
paints the Plex-style 4-corner gradient on `.backdropContainer`, and sets
CSS custom properties on `<html>`:

```
--ultrablur-tl  --ultrablur-tr  --ultrablur-br  --ultrablur-bl
```

plus an `ultrablur-active` class — so a custom theme can override the default
gradient with its own CSS while the plugin handles fetching/caching. Disable
the script entirely via the **Enable web client script** config toggle if the
theme does its own fetching.
