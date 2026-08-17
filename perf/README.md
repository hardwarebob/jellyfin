# Performance-regression harness

Turns "I improved X by Y%" into a mechanical diff between two JSON files, instead of a claim
you have to take on faith. See the full design rationale in the plan this branch implements
(performance-regression-test infrastructure).

Not referenced by `Jellyfin.sln` and not under `tests/` — CI's only test invocation
(`.github/workflows/ci-tests.yml`: `dotnet test Jellyfin.sln`) cannot discover anything here,
so it never runs as part of the default PR gate.

## Why not just measure wall-clock time?

There's no dedicated benchmark hardware or historical baseline for this project — CI runs on
shared, variable GitHub-hosted runners. Absolute millisecond thresholds are either too loose to
catch a real regression or too tight to survive normal runner noise. Every gate here is
host-independent by construction instead:

- **`queryplan`** — asserts on SQLite's `EXPLAIN QUERY PLAN` output (does it use an index, or
  scan the table). SQLite's planner picks the same plan for the same schema/data regardless of
  machine speed.
- **`operationcount`** — asserts on a count of logical operations (SQL round trips, rows read),
  not a duration.
- **`ratio`** — a same-process, same-host comparison between two code paths (e.g. an
  optimization on vs off), so absolute host speed cancels out.
- **`timing`** — raw wall-clock, always `gating: false`. Logged for human trend-reading only,
  never fails a comparison.

## Layers

- **Layer 1 — query-plan gates** (`Layer1.QueryPlan/`, built). Fast, in-process, no host boot.
  Uses real EF Core migrations (`Migrate()`, not `EnsureCreated()`) against an in-memory SQLite
  database — unlike every other test in this repo, which uses `EnsureCreated()` and so silently
  skips migration-only raw SQL (FTS5 tables/triggers, partial/covering indexes). See
  `Core/PerfDbContextFactory.cs` for why that distinction matters.
- **Layer 2 — integration** (`Layer2.Integration/`, built). Boots a real, fully-migrated host by
  reusing `JellyfinApplicationFactory`/`AuthHelper` from `tests/Jellyfin.Server.Integration.Tests`
  directly, seeds deterministic data, and drives real HTTP endpoints. Run via `dotnet test` from
  `Layer2.Integration/` specifically (not `dotnet run`, and not from elsewhere — see
  `Core/SeededHostFixture.cs`'s comment on `WebApplicationFactory<Startup>`'s content-root
  auto-detection, which only resolves correctly launched that way). Currently both scenarios'
  `Gating` is `false` — see "Known follow-up work".
- **Layer 3 — e2e** (not yet built). Playwright against real running containers (baseline vs
  candidate, same host), formalizing the mechanics already proven in
  `/applications/deploy/jellyfin-perf/run.js`.

## Usage

```sh
# Run Layer 1 against the current working tree
perf/run-perf.sh run --out perf/results/mine.json

# Run against a specific ref (builds from a disposable git worktree, doesn't touch your working tree)
perf/run-perf.sh run --ref upstream/master --out perf/results/baseline.json

# Run baseline vs candidate and get a diff table + diff.json
perf/run-perf.sh diff --baseline upstream/master --candidate ux-performance

# Same, but exit non-zero if any gating result regressed (for CI)
perf/run-perf.sh diff --baseline upstream/master --candidate HEAD --fail-on-regression
```

No local `dotnet` SDK? Set `PERF_DOTNET_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0` (and optionally
`PERF_CONTAINER_RUNTIME=docker`, default `podman`) and the script runs `dotnet` inside a
container instead.

## Adding a gate

1. Add a class under `Layer1.QueryPlan/Core/Gates/` with a static `Run(PerfDbContextFactory
   factory) -> GateResult` method. See any existing gate for the pattern.
2. Register it in `Layer1.QueryPlan/Runner/Program.cs`'s gate list.
3. Optionally add a thin `[Fact]` wrapper in `Layer1.QueryPlan/GateFacts.cs` for ad-hoc
   `dotnet test` / IDE use.
4. If the gate checks something that only exists after a specific migration, return
   `GateStatus.NotApplicable` when it's absent (see `FullTextSearchIndexGate.cs`) rather than
   throwing — a baseline ref not having the feature yet isn't a failure.

## Known follow-up work

**Layer 1:**
- `recent-episodes-ordering-is-index-backed` is `gating: false`. Against the real full schema
  (many more competing indexes than a small synthetic test table), SQLite's planner picks a
  different index for this exact filter shape than the one the migration added, and needs a
  temp sort. An isolated 2-index reproduction confirms the new index *does* correctly avoid the
  temp sort in principle, so this isn't a believed-real regression — but the full-schema
  planner-preference behavior isn't reliably reproducible in a small synthetic dataset yet.
- True `sqlite3_stmt_status` VDBE-step counting (a finer-grained operation-count signal than
  round-trip/row counting) needs P/Invoke plumbing through `Microsoft.Data.Sqlite` internals —
  deferred, not blocking.
- Gates 2 (`resume-in-progress-lookup-uses-index`) and 4
  (`recent-episodes-ordering-is-index-backed`) assert against representative queries built
  directly, not captured live from the full `BaseItemRepository.TranslateQuery`/`NextUpService`
  call graph — building a full valid domain `User` object for that path was out of scope for
  this first slice.

**Layer 2:**
- Both scenarios (`search-endpoint-multilanguage`, `resume-endpoint-returns-in-progress-only`)
  are `gating: false`, with a confirmed (not speculative) diagnosis: `SeededHostFixture.SeedAsync`
  writes `BaseItemEntity`/`UserData` rows directly with no real `CollectionFolder`/library
  registration. Those items are fully queryable and correctly deserialized by direct id —
  `SearchScenario`'s own `byIdsTotalRecordCount` detail confirms `GET /Items?ids={id}` finds them
  — but `Recursive=true` search and `/Items/Resume` both traverse from registered library/user-view
  roots, so an item with no real folder ancestry is invisible to them by design. Needs a minimal
  seeded `CollectionFolder` + library registration to test actual search/resume *matching* logic
  instead of accidentally testing library-root traversal. Tracked, not mysterious.
- `WebApplicationFactory<Startup>`'s content-root auto-detection failure (worked around via
  `WithWebHostBuilder(builder => builder.UseContentRoot(...))` in `SeededHostFixture.cs`) was not
  root-caused to a specific mechanism — several plausible fixes (matching
  `Jellyfin.Server.Integration.Tests.csproj`'s exact reference shape, `Environment.CurrentDirectory`)
  had no effect at all. The `WithWebHostBuilder` workaround is confirmed working (migrations do
  run — 7s host boot, not a fast no-op) but is a workaround, not an understood root cause.
