# CLAUDE.md

Guidance for Claude Code when working in this repository.

## Project

Jellyfin plugin serving Plex-identical UltraBlur 4-corner ambient colors. C#/.NET. Runs a managed, in-process port of PMS UltraBlur (`UltraBlur.Plugin/Algorithm/`, transliterated from the bit-exact C++ port at `../pms/cpp-linux`), caching results in SQLite. No native binary.

- `UltraBlur.Plugin/Algorithm/` — the port. `ImageLoader` (host SkiaSharp decode, EXIF, OpenCV 4.5.0 resize), `UltraBlurCore` (quadrants → histogram → median-cut → rank → render), `MuslMath` (musl libm float functions), `LibCxx` (libc++ sort/heap tie order). Line-matched to the C sources; the `.editorconfig` there relaxes StyleCop layout rules on purpose.
- `tests/UltraBlur.Bench/` — bit-exactness harness (README "Bit-exactness"). Run it after ANY change under `Algorithm/`; the bar is 199/199 + 39/39 vs CLI and 96/99 vs GT.
- `UltraBlur.Plugin/` — plugin source. Key pieces: `UltraBlurService.GetOrComputeAsync` (cache → image resolve → in-process extract), `ScriptInjectionStartupFilter` (injects `ultrablur.js` into jellyfin-web at request time), REST controller (`GET /UltraBlur/Colors/{itemId}`, batch `POST /UltraBlur/Colors`, max 100).
- `build.json` / `manifest.json` — Jellyfin plugin packaging metadata.
- `package.sh` — builds and zips the plugin for a manual repo install.
- `out/` — build artifacts.

Build: `dotnet build UltraBlur.Plugin` (see `package.sh` for the release flow).

Gotchas:
- Targets **Jellyfin 10.11**; the 10.11 GA release renamed several plugin API surfaces vs older templates — don't copy code from pre-10.11 plugin examples without checking. The official `repo.jellyfin.org` template feed is dead.
- Bit-exactness lives in the numerics: never swap `MuslMath` for `MathF` (last-ULP differences flip cluster picks), never use `Array.Sort`/`PriorityQueue` in place of `LibCxx` (tie order shifts ordinals → rank), keep float vs double operand types exactly as in the C++ source, no FMA.
- SkiaSharp is compile-only (`ExcludeAssets=runtime;native`); the host's copy loads. Reference = the version the lowest supported host ships (10.11.0 → 3.116.1, 12.0.0-rc1 → 3.119.4); a higher reference would fail to bind on older hosts.
- C# 14 (net10/jf12) resolves `MemoryMarshal.Cast(byte[])` to the ReadOnlySpan overload — pass `.AsSpan()` when the result must be writable.
- Cache rows key on `(ItemId, ImageType)` with a tag from image path + mtime; artwork changes invalidate naturally.
- Image resolution order: parent-inherited backdrop-first chains; episodes/seasons walk season -> series backdrops before own poster/still, and music uses artist backdrop -> album cover. JPEG/PNG/WebP (the formats verified decode-identical between Skia and Plex's OpenCV).

## Orchestration: Fable as conductor

When the session model is **Fable**, act as an orchestrator, not a solo implementer. Decompose non-trivial work and delegate to subagents, keeping architecture decisions, plan ownership, integration, and final review at the Fable level.

**Delegation targets:**

| Worker | How to invoke | Use for |
|--------|---------------|---------|
| Codex | `Skill` tool → `codex:rescue`, or `Agent` tool → `subagent_type: "codex:codex-rescue"` | Second-opinion diagnosis, stuck bugs, independent implementation passes, deep root-cause hunts |
| Opus | `Agent` tool → `model: "opus"` | Complex implementation: service/middleware changes, async/caching logic, API surface migrations |
| Sonnet | `Agent` tool → `model: "sonnet"` | Well-scoped mechanical tasks: single-file edits, test writing, searches, packaging/manifest updates |
| Fable | `Agent` tool → `model: "fable"` | Escalation only: task failed Opus review twice, or needs deepest reasoning (algorithm-faithful ports, subtle async/caching bugs). Not a default implementation tier |

**Rules:**
1. Fable plans and reviews; subagents execute. Write each subagent prompt to be self-contained — file paths, acceptance criteria, the 10.11 API gotcha above — since subagents start cold.
2. Route by difficulty: Sonnet for mechanical/scoped work, Opus for hard implementation, Codex for independent second passes and diagnosis.
3. Independent tasks → dispatch subagents in parallel (single message, multiple Agent calls). Dependent tasks → sequential, feeding each result into the next prompt.
4. Use `isolation: "worktree"` when two subagents would touch overlapping files.
5. Fable verifies every subagent result before integrating: `dotnet build`, read the diff. A subagent's "done" claim is not evidence.
6. Relay subagent findings to the user in your own summary — their final messages are not shown to the user.
