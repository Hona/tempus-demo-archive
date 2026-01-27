# Agents Guide

## Key Learnings (as of 2026-01-25)

- **Two sources of demos**:
  - `archived_demos.txt` (static export) via `IngestArchivedDemosJob`.
  - API crawl via `CrawlRecordDemosJob` (records list + demo_info URLs).
- **No API endpoint to list all demos**; best-effort crawl is via records API.

## Domain Context (Tempus / TF2 Jump)
- Tempus is a TF2 jump community; records are per **zone** and per **class** (soldier/demoman).
- API returns **record lists** (PB per user) rather than every run.
- Zones are grouped: map, course, bonus, trick, etc. (`fullOverview2` supplies zone IDs).
- Demo URLs may point to **archive** or **rolling** buckets; filenames are not unique keys.

## Repo Architecture
- `TempusDemoArchive.Persistence` – EF Core models and migrations (SQLite).
- `TempusDemoArchive.Jobs` – CLI jobs; job catalog supports `--list` and `--job <id>`.
- Source of truth is `Demos` table; `StvProcessed` gates parsing.
- Warnings-as-errors enabled on both Jobs and Persistence projects.

## Parser + Data Model
- Parser now emits **raw events** to support later analytics:
  - `spawns[]`, `teamChanges[]`, `deaths[]`, `pauses[]` in JSON.
  - `chat.client` (SayText2 entity slot) and `users.entityId` are emitted for joinable chat.
- DB stores **raw ingest** only; no derived playtime/spectator time is computed at ingest.
- Steam IDs are preserved raw in `SteamId`, with normalized sidecars: `SteamIdClean`, `SteamId64`, `SteamIdKind`, `IsBot`.
- Chat attribution is via `ClientEntityId`/`FromUserId`; `From` can be empty in raw messages.
- Entity ids can be **reused** in demos; when a `ClientEntityId` maps to multiple users, `FromUserId` is left null.
- Deterministic chat attribution would require storing userinfo events over time.

## Cross‑Platform Parsing
- `TempusDemoArchive.Jobs` uses **P/Invoke** to `tf_demo_parser` (no external exe).
- Native binaries live under `runtimes/<rid>/native/`.

## Resumability + Deduping
- Crawl state is saved after **every API page**: `~/.config/TempusDemoArchive/crawl-record-demos-state.json`.
- `TEMPUS_CRAWL_RESET=1` restarts from scratch.
- Dedupe is by **Demos.Id** (in‑memory HashSet + DB PK). Re‑runs are safe.
- Unique‑constraint collisions are handled by filtering existing IDs and retrying the batch.
- Duplicate **URLs** can exist with different IDs (rolling bucket collisions).

## Crawl Pagination Behavior
- `limit=0` returns **full leaderboard** for some zones (e.g. jump_beef with 10k runs).
- If `limit=0` returns >50 results, that zone is complete in one call.
- Otherwise, page with `start/limit` until results are exhausted.
- `start=1` is correct (API is effectively 1‑indexed).

## Tempus API Quirks
- `demo_info.start_tick` / `end_tick` can be **string** or **negative** in some records.
- TempusApi `DemoInfo` uses nullable `long` with `JsonNumberHandling.AllowReadingFromString` (v4.0.4+).

## Rate Limits
- 429s occur at 200ms intervals; use `TEMPUS_CRAWL_MIN_INTERVAL_MS` to tune.
- Crawl logs 429 count and prints Retry‑After for first few hits.

## Database Snapshots
- Use `sqlite3 .backup` for a consistent snapshot before reparse.
- Backup path convention: `tempus-demo-archive.db.bak-<timestamp>`.

## Reparse Controls
- `ReparseDemosJob` re‑downloads and re‑parses processed demos.
- Env controls:
  - `TEMPUS_REPARSE_DEMO_IDS` (explicit list)
  - `TEMPUS_REPARSE_LIMIT` (oldest N)
  - `TF_DEMO_PARSER_VERSION` (stamped into rows)
- State file: `~/.config/TempusDemoArchive/reparse-demos-state.json` (offset based).
- Parse logging controls: `TEMPUS_PARSE_LOG_EVERY`, `TEMPUS_PARSE_VERBOSE`.
- Reparse logging controls: `TEMPUS_REPARSE_LOG_EVERY`, `TEMPUS_REPARSE_VERBOSE`.

## SQLite / EF Core
- SQLite can’t ORDER BY `ulong` directly; global value converter maps `ulong` ↔ `long` for all entities.

## WR History Job
- Job id: `wr-history` (WR-only, per map/class). CSV emitted to `~/.config/TempusDemoArchive/temp/`.
- Deterministic formats handled: map record, first record, bonus/course record (including ~holiday tags), course segment records, compact WR lines, IRC broke/set WR, ranked-with-time (rank=1 only).
- PRs are ignored; unlabeled map-run splits are ignored.
- Flags:
  - `TEMPUS_WR_INCLUDE_INFERRED=1` include map-run inferred WR times (default 0).
  - `TEMPUS_WR_INCLUDE_ALL=1` include all WR announcements, not just improvements.
  - `TEMPUS_WR_INCLUDE_SUBRECORDS=1` include bonus/course/segment/ranked entries.

## Analysis Jobs
- `sentiment-user-timeline` writes CSV + SVG for a user.
- `tempus-wrapped` exports a year-in-review raw dataset (chat + stats) for a user.
- `playtime-map`, `spectator-map`, `spectator-peers` are analysis-only (no DB writes).
- `player-maprun` exports PR/WR map-run history for a player + map + class.

## Parser Upstream
- Canonical parser upstream: `https://codeberg.org/demostf/parser`.
- FFI exports (`analyze_demo`, `free_string`) are merged into the fork and used for P/Invoke.
