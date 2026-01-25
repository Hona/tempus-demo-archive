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

## Parse Controls
- `TEMPUS_PARSE_PARALLELISM=8` (default 5)
- `TEMPUS_PARSE_BATCH_SIZE=200` (default 200)
- `TEMPUS_PARSE_LOG_EVERY=50`
- `TEMPUS_PARSE_VERBOSE=1`

## Schema Highlights (Raw Event Storage)
- `StvUsers` includes `EntityId`, `SteamIdClean`, `SteamId64`, `SteamIdKind`, `IsBot`
- `StvChats` includes `ClientEntityId`, `FromUserId` (joinable to users)
- `StvSpawns`, `StvTeamChanges`, `StvDeaths`, `StvPauses` are raw timeline events

## Dedupe Guarantee
- Demos are deduped by `Demos.Id` (in-memory HashSet + DB PK).
- Re-running ingest/crawl is safe.
