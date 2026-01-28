# Tempus Demo Archive

Ingest Tempus demo URLs, parse STV data, and persist raw events in a local SQLite DB for downstream analysis.

## Architecture
- `TempusDemoArchive.Persistence` – EF Core models + SQLite schema.
- `TempusDemoArchive.Jobs` – CLI jobs (ingest, crawl, parse, reparse, reporting).
- Native parser – `tf_demo_parser` via P/Invoke; binaries live in `runtimes/<rid>/native/`.

## Data Flow
1. **IngestArchivedDemosJob** – loads `archived_demos.txt` into `Demos`.
2. **CrawlRecordDemosJob** – crawls Tempus records API and adds new demos to `Demos`.
3. **ParseDemosJob** – downloads zips, extracts `.dem`, parses, writes `Stv*` tables, marks `StvProcessed`.
4. **ReparseDemosJob** (optional) – reprocesses previously parsed demos using the latest parser/schema.

## Quick Start
Prereqs:
- .NET 10 SDK
- Native parser binaries in `runtimes/<rid>/native/`

Run interactive jobs:
```
dotnet run --project TempusDemoArchive.Jobs
```

List jobs or run by id:
```
dotnet run --project TempusDemoArchive.Jobs -- --list
dotnet run --project TempusDemoArchive.Jobs -- --job crawl-record-demos
```

Data root:
- `~/.config/TempusDemoArchive/`
- DB: `tempus-demo-archive.db`

## Resumable Crawl
State file:
- `~/.config/TempusDemoArchive/crawl-record-demos-state.json`

Reset crawl:
```
TEMPUS_CRAWL_RESET=1
```

Throttle (ms between requests):
```
TEMPUS_CRAWL_MIN_INTERVAL_MS=200
```

## Reparse Controls
- `TEMPUS_REPARSE_DEMO_IDS=1,2,3` (explicit list)
- `TEMPUS_REPARSE_LIMIT=10` (oldest N processed)
- `TF_DEMO_PARSER_VERSION=...` (stamp parsed rows)
- `TEMPUS_REPARSE_LOG_EVERY=50`
- `TEMPUS_REPARSE_VERBOSE=1`
- State file: `~/.config/TempusDemoArchive/reparse-demos-state.json`

## Parse Controls
- `TEMPUS_PARSE_PARALLELISM=8` (default 5)
- `TEMPUS_PARSE_BATCH_SIZE=200` (default 200)
- `TEMPUS_PARSE_LOG_EVERY=50`
- `TEMPUS_PARSE_VERBOSE=1`
- `TEMPUS_PARSE_INCLUDE_FAILED=1` (retry demos marked failed)

## Sentiment Controls
- `TEMPUS_SENTIMENT_BUCKET=month` (month/year; default month)

## WR History Export

Jobs:
- `wr-history` – WR history for a single map + class.
- `wr-history-all` – full export (one CSV per map + class).

Output:
- `~/.config/TempusDemoArchive/temp/wr-history-all/`

Rules (no env toggles):
- Emits a deterministic, monotonic improvement timeline per `(map, class, segment)`.
- Only `evidence=record` rows get a `demo_id` (record-setting demo). All other evidence has no reliable demo link.
- Non-record evidence for the same `(segment, record_time)` is suppressed when a real record exists.

CSV schema (per-row):
- `segment`: `Map`, `Bonus N`, `Course N`, `C# - Name` (course segments)
- `evidence`: `record` | `announcement` | `command` | `observed`
- `evidence_source`: `map_record` | `first_record` | `zone_record` | `zone_first` | `irc` | `irc_set` | `wr_command` | `rank_command` | `wr_split`

## Map Run History Controls
- `TEMPUS_MAPRUN_INCLUDE_WR=1` (include WR map runs; default PR-only)

## Analysis Jobs
- `wr-history` – WR-only history for a map/class (CSV in `~/.config/TempusDemoArchive/temp/`).
- `player-maprun` – PR/WR map-run history for a player + map + class (CSV in temp).
- `sentiment-user-timeline` – per-user sentiment trend CSV + SVG.
- `tempus-wrapped` – year-in-review raw dataset (chat + stats) for a user.
- `playtime-map` – per-map soldier/demo playtime for a user.
- `spectator-map` – per-map spectator time for a user (requires team-change data).
- `spectator-peers` – who spectated you / who you spectated (overlap proxy).

## Schema Highlights (Raw Event Storage)
- `StvUsers` includes `EntityId`, `SteamIdClean`, `SteamId64`, `SteamIdKind`, `IsBot`
- `StvChats` includes `ClientEntityId`, `FromUserId` (joinable to users)
- `StvSpawns`, `StvTeamChanges`, `StvDeaths`, `StvPauses` are raw timeline events

## Notes
- Some chat rows have `ClientEntityId` but no `FromUserId` because entity ids can be reused; mapping is skipped when ambiguous.
- SQLite stores ids as `INTEGER` with a ulong↔long converter to keep queries translatable.
- `Demos.StvFailed` + `Demos.StvFailureReason` track corrupt/invalid demos so they can be skipped by default.
- `TEMPUS_SKIP_MIGRATIONS=1` skips auto-migrations at startup for read-only analysis runs.

## Dedupe Guarantee
- Demos are deduped by `Demos.Id` (in-memory HashSet + DB PK).
- Re-running ingest/crawl is safe.
