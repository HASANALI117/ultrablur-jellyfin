# CLAUDE.md

Guidance for Claude Code when working in this repository.

## Project

Jellyfin plugin serving Plex-identical UltraBlur 4-corner ambient colors. C#/.NET. Wraps the bit-exact C++ port at `../pms/cpp-linux` by shelling out to the statically linked `ultrablur_cli` (musl, x86_64 Linux), caching results in SQLite.

- `UltraBlur.Plugin/` — plugin source. Key pieces: `UltraBlurService.GetOrComputeAsync` (cache → image resolve → subprocess → parse), `ScriptInjectionStartupFilter` (injects `ultrablur.js` into jellyfin-web at request time), REST controller (`GET /UltraBlur/Colors/{itemId}`, batch `POST /UltraBlur/Colors`, max 100).
- `build.json` / `manifest.json` — Jellyfin plugin packaging metadata.
- `package.sh` — builds and zips the plugin for a manual repo install.
- `out/` — build artifacts.

Build: `dotnet build UltraBlur.Plugin` (see `package.sh` for the release flow).

Gotchas:
- Targets **Jellyfin 10.11**; the 10.11 GA release renamed several plugin API surfaces vs older templates — don't copy code from pre-10.11 plugin examples without checking. The official `repo.jellyfin.org` template feed is dead.
- Subprocess over P/Invoke is deliberate (crash isolation, zero bundled deps). The `extern "C" ultrablur_extract_jpeg` ABI exists in `pms/cpp-linux` if in-process is ever wanted.
- Cache rows key on `(ItemId, ImageType)` with a tag from image path + mtime; artwork changes invalidate naturally.
- Image resolution order: parent-inherited backdrop-first chains; episodes/seasons walk season -> series backdrops before own poster/still, and music uses artist backdrop -> album cover. JPEG/PNG/WebP (must track the CLI build's decoders).

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
