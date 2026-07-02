# Plex parity — deviation catalog & implementation plan

Status of the plugin vs Plex's real UltraBlur behavior, established from two RE
passes (2026-07-02):

- **PMS side** (Ghidra `pms/decompiled/00889cf8_UltraBlurProcessor_Process.c`,
  IDA `PlexMediaServer.i64`): one uniform rule per metadata item — use the
  item's `art` attribute if non-empty, else its `thumb`. Computed **once** and
  persisted in the SQLite `metadata_items.extra_data` blob
  (`ultraBlurTopLeft/TopRight/BottomRight/BottomLeft` + `ultraBlurChangestamp`),
  regenerated only when `lastUltraBlurChangedAt` bumps. Emitted **inline** as
  `<UltraBlurColors …>` in metadata XML.
- **Client side** (`android/decompiled-bundle/bundle.decomp.js`): detail pages
  read the **inline** colors; `/service/ultrablur/colors?url=…` is a
  Music/PostPlay-only fallback that uses `item.thumb`. The generic background
  resolver (`getPathForImageType`, `imageType='background'`) resolves
  `item.art ?? item.thumb` (playlist/artist: `grandparentArt ?? parentArt ??
  art`).

The critical fact: **Plex's `art` attribute is already parent-inherited**
(show → season → episode). A season's `art` is normally the show backdrop; an
episode's `art` is normally the show backdrop too. The item's own `thumb`
(season poster / episode still) is only the *fallback* when no art exists
anywhere up the chain.

Jellyfin does **no** automatic image inheritance: `BaseItem.GetImageInfo(type,
0)` returns only the item's own image. So to match Plex we must walk parents
ourselves.

---

## Deviation catalog

Severity: **P1** = visibly wrong colors vs Plex; **P2** = coverage/robustness
gap; **P3** = architectural or cosmetic, low/no action.

### P1 — Image-source resolution does not replicate Plex's art inheritance

Current [`UltraBlurService.BuildFallbackChain`](UltraBlur.Plugin/Services/UltraBlurService.cs):

| Item | Current chain (verified 2026-07-02) | Plex-faithful chain |
|------|---------------|---------------------|
| Movie / Series | Backdrop → Primary | Backdrop → Primary ✅ |
| **Season** | Backdrop → Primary | Backdrop → **series.Backdrop** → Primary ❌ |
| **Episode** | Backdrop → series.Backdrop → Primary → series.Primary | Backdrop → **season.Backdrop** → series.Backdrop → Primary (mostly ✅) |

> Correction (2026-07-02 review): the episode order is **not** inverted — the
> code already tries `series.Backdrop` before the episode's own Primary. The
> only episode gap is the missing `season.Backdrop` step (rare in Jellyfin).
> The trailing `series.Primary` is an extra coverage fallback beyond Plex;
> keep it (fires only when Plex would produce nothing).

Real bug:
- **Season inheritance missing.** A season with no own backdrop falls straight
  to its poster; Plex would use the inherited show backdrop first.

Net effect: backdrop-less season detail pages get **different colors than
Plex** — usually more saturated/darker, since posters differ from the show
backdrop the top-left-bias algorithm was tuned against.

### P1 — Music items use the video chain

`Audio` / `MusicAlbum` / `MusicArtist` fall through to the default `Backdrop →
Primary`. Plex's music path uses `thumb` (the album/artist poster) via the
Music/PostPlay endpoint. Music items rarely have backdrops, so we usually still
land on Primary (matching Plex by accident) — but the ordering is wrong and an
album *with* a backdrop would diverge.

### P2 — Warm-up task omits seasons and music

[`UltraBlurWarmupTask`](UltraBlur.Plugin/ScheduledTasks/UltraBlurWarmupTask.cs)
enumerates only `Movie, Series, Episode`. Plex computes inline colors for
**seasons** too (season detail pages use them). Seasons and music albums are
served on-demand but never precomputed, so their first view is slow and the
warm-up "instant detail pages" guarantee doesn't hold for them.

### P2 — JPEG-only gate skips PNG/WebP artwork

`IsJpeg()` magic-byte check (`FF D8`) drops any non-JPEG image before the CLI
runs. The standalone `ultrablur_cli` was built JPEG-only (OpenCV with only the
JPEG codec). Plex/PMS decodes via full OpenCV `cv::imdecode`, which handles PNG
and WebP. Jellyfin artwork from some providers is PNG/WebP → we skip it and can
end up returning 404 where Plex would produce colors.

### P3 — Endpoint delivery instead of inline metadata

Plex embeds colors in the item DTO; a Jellyfin plugin cannot extend core DTOs,
so we serve a separate `GET /UltraBlur/Colors/{itemId}`. One extra request per
item (cached both sides). **No action** — architectural constraint; mitigated
by warm-up + client `sessionStorage`.

### P3 — Failure returns 422 instead of last-known colors

Plex, on a failed recompute, serves the stale-but-present cached values. We
return 422. Minor; a transient CLI timeout yields no gradient instead of a
slightly-stale one.

### P3 — Repeated compute of shared source images

N episodes inheriting one show backdrop = N identical CLI runs. Plex also
persists per metadata item, so this matches Plex's *storage* model, but we could
dedup by source-image hash to save compute. **Optimization only.**

---

## Implementation plan

### Phase 1 — Faithful image resolution (fixes both P1s)

File: [`UltraBlur.Plugin/Services/UltraBlurService.cs`](UltraBlur.Plugin/Services/UltraBlurService.cs)

Rewrite `BuildFallbackChain` to walk parents for the backdrop, then fall to the
item's own poster/still. Use `BaseItem.FindParent<T>()` (Episode exposes
`.Series` but not `.Season`, so use `FindParent<Season>()`).

```csharp
using MediaBrowser.Controller.Entities.Audio; // Audio, MusicAlbum, MusicArtist
using MediaBrowser.Controller.Entities.TV;    // Episode, Season, Series

private static IEnumerable<(BaseItem Owner, ImageType Type)> BuildFallbackChain(BaseItem item)
{
    switch (item)
    {
        case Episode ep:
            yield return (ep, ImageType.Backdrop);                 // own art (rare)
            var season = ep.FindParent<Season>();
            if (season != null) yield return (season, ImageType.Backdrop);
            if (ep.Series != null) yield return (ep.Series, ImageType.Backdrop);
            yield return (ep, ImageType.Primary);                  // episode still
            if (ep.Series != null) yield return (ep.Series, ImageType.Primary); // extra coverage (beyond Plex)
            break;

        case Season se:
            yield return (se, ImageType.Backdrop);
            if (se.Series != null) yield return (se.Series, ImageType.Backdrop);
            yield return (se, ImageType.Primary);                  // season poster
            if (se.Series != null) yield return (se.Series, ImageType.Primary); // extra coverage (beyond Plex)
            break;

        case Audio au:                                             // track
            var artist = au.FindParent<MusicArtist>();
            var album = au.FindParent<MusicAlbum>();
            if (artist != null) yield return (artist, ImageType.Backdrop);
            if (album != null)  yield return (album, ImageType.Primary);
            yield return (au, ImageType.Primary);
            break;

        case MusicAlbum al:
            var albArtist = al.FindParent<MusicArtist>();
            if (albArtist != null) yield return (albArtist, ImageType.Backdrop);
            yield return (al, ImageType.Primary);                  // album cover
            break;

        default:                                                   // Movie, Series, MusicArtist, …
            yield return (item, ImageType.Backdrop);
            yield return (item, ImageType.Primary);
            break;
    }
}
```

Notes:
- Cache key stays `(ItemId, ImageType)`; the path+mtime tag disambiguates which
  concrete file (own vs inherited backdrop) was used, so invalidation still
  works when the resolved source changes.
- `ResolveImage` loop is unchanged — it already iterates the chain, checks file
  existence, and returns the first usable image.
- Explicit `?imageType=` query override still targets the item's own image only
  (intended behavior for callers that want a specific source).

### Phase 2 — Warm-up coverage (fixes P2 warm-up)

File: [`UltraBlur.Plugin/ScheduledTasks/UltraBlurWarmupTask.cs`](UltraBlur.Plugin/ScheduledTasks/UltraBlurWarmupTask.cs)

Add `Season` (and, behind the existing task, `MusicAlbum`) to
`IncludeItemTypes`:

```csharp
IncludeItemTypes = new[]
{
    BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Season,
    BaseItemKind.Episode, BaseItemKind.MusicAlbum,
},
```

`MusicArtist` optional (few users theme artist pages) — leave a config note
rather than warming every artist.

### Phase 3 — PNG/WebP support (fixes P2 format gate)

Two options; **recommend 3a** (keeps decode identical to Plex's OpenCV).

**3a — Enable codecs in the standalone CLI (faithful).**
File: `../pms/cpp-linux/dist/standalone/build.sh`. In the OpenCV static build,
turn on the PNG and WebP codecs (currently JPEG-only). Codecs are independent —
this does **not** change the JPEG path, so the 96/99 bit-exact result is
preserved. Then in the plugin, relax `IsJpeg()` to accept known-decodable magic
bytes (JPEG `FF D8`, PNG `89 50 4E 47`, WebP `RIFF…WEBP`). **Do not drop the
gate entirely**: the gate currently *skips* an undecodable candidate so the
chain falls through to the next image; with no gate, a PNG backdrop would stop
the chain and 422 instead of falling through to a JPEG Primary — a coverage
regression. The magic-byte set must exactly track what the CLI build decodes.
Rebuild + re-benchmark the CLI to confirm JPEG bit-exactness is unchanged
before shipping.

**3b — Transcode to JPEG in the plugin (no CLI rebuild).**
Inject Jellyfin's `IImageEncoder` into `UltraBlurService`; when the resolved
image isn't JPEG, transcode to a temp JPEG and feed that to the CLI. Downside:
the re-encode differs from Plex's direct decode, so those items are not
bit-exact (acceptable — Plex-exactness is only defined for the JPEG pipeline,
and such items are rare). Simpler to ship; no cross-repo change.

### Phase 4 — Stale-on-failure fallback (fixes P3 failure) — optional

File: `ColorStore` + `UltraBlurService.GetOrComputeAsync`. Add
`ColorStore.GetAnyTag(itemId, imageType)` returning the stored row regardless of
tag. In `GetOrComputeAsync`, when `RunCliAsync` fails, return the last-known row
(flag `stale: true` in the DTO) instead of the error, matching Plex. Low effort,
low priority.

### Phase 5 — Source-image dedup cache (P3 optimization) — optional

Add a second SQLite table `source_colors(source_tag PRIMARY KEY, tl,tr,br,bl)`.
Before spawning the CLI, look up by the source image's tag; on hit, copy into
the per-item row without running the CLI. Cuts warm-up cost dramatically for
series where every episode inherits one backdrop. Purely internal; output
identical.

---

## Verification

1. `dotnet build UltraBlur.Plugin -c Release` (both `-p:JellyfinTarget=jf10` and
   `jf12`), warnings-as-errors clean.
2. **Episode parity**: pick a series whose episodes have no own backdrop. Before
   fix, `GET /UltraBlur/Colors/{episodeId}` returns colors from the episode
   still; after, from the show backdrop. Confirm the after-colors equal
   `GET /UltraBlur/Colors/{seriesId}` (same source image → same colors).
3. **Season parity**: same check, season vs its series.
4. **Ground truth**: for a title present in `../dataset/ultrablur.ndjson`, run
   `../pms/cpp-linux/dist/standalone/ultrablur_cli` on the exact backdrop file
   and assert the endpoint returns the same hex — and that it matches the Plex
   ground-truth row (per-corner ΔRGB 0 on the 96/99 bit-exact set).
5. **PNG (Phase 3)**: point an item's backdrop at a PNG; confirm colors are
   produced (not 404) and, for 3a, that a JPEG control still matches ground
   truth after the CLI rebuild.
6. **Warm-up**: run the task; confirm `colors.db` now has rows for `Season` (and
   `MusicAlbum`) item IDs.

## Sequencing & delegation

- Phase 1 and Phase 2 are independent, both plugin-only → can run in parallel
  (Sonnet for Phase 2 mechanical edit; Opus for Phase 1 resolver logic).
- Phase 3a touches `../pms/cpp-linux` (CLI build) — isolate in a worktree; must
  re-run the CLI benchmark before the plugin gate is relaxed.
- Phases 4–5 optional; schedule after 1–3 land and parity is confirmed.
- Fable/Opus integrates and verifies each result (`dotnet build` + the parity
  checks above) before merging.
