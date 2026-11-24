---
name: performance-optimization
description: Optimizes game and service performance across K2 client engine loops, Lua UI frame budgets (144 FPS / <= 1.0 ms), .NET services, and PostgreSQL queries. Use when frame rate drops, tick latency spikes, GC hitches occur, N+1 query patterns appear, or database profiling reveals bottlenecks.
capability_contract:
  allowed_worker_roles: ["kongor-offline-worker", "kongor-game-worker"]
  allowed_tools: ["view_file", "run_command", "replace_file_content"]
  requires_worker_lock: false
  max_probes: 10
---

# Performance Optimization (K2 Game Loop & Backend Services)

## Overview

Measure before optimizing. Performance work without empirical profiling is guessing — and guessing leads to speculative abstractions that add maintenance debt without improving what matters. Profile first, isolate the single bottleneck (CPU sim vs. CPU render-submit vs. GPU vs. database I/O), apply the targeted fix, and measure again under identical conditions.

## When to Use

- Frame rate is low or uneven (< 144 FPS target), or client stutters/hitches during team fights or store navigation.
- Hot Lua UI tick callbacks or trigger handlers exceed the $\le 1.0\text{ ms}$ budget.
- Memory growth, GC stalls, or Lua allocator exhaustion occurs.
- Backend API endpoints (MasterServer, COMPEL) show high p95/p99 response latency.
- PostgreSQL queries trigger sequential scans (`Seq Scan`) or N+1 query waterfalls.
- High connection pool wait times or Redis cache stampedes.

**When NOT to use:** Do not optimize without empirical profiling telemetry. Premature micro-optimizations that bypass error trapping or increase code complexity are strictly prohibited.

---

## Performance Targets & Budgets

| Target System | Metric | Target Budget | Hard Ceiling |
|---|---|---|---|
| **K2 Client Engine** | Total Frame Time | 144 FPS ($\le 6.94\text{ ms}$) | $\le 16.67\text{ ms}$ (60 FPS) |
| **K2 Client Subsystems** | Gameplay/Sim Thread | $\le 2.5\text{ ms}$ | $\le 4.0\text{ ms}$ |
| **K2 Client Subsystems** | Render Submission (D3D9/DXVK) | $\le 2.5\text{ ms}$ | $\le 4.0\text{ ms}$ |
| **K2 Client Subsystems** | GPU Scene Render | $\le 2.0\text{ ms}$ | $\le 4.0\text{ ms}$ |
| **K2 Lua UI Handlers** | Single Callback / Frame Tick | $\le 1.0\text{ ms}$ | Warning on $> 5.0\text{ ms}$ |
| **MasterServer Endpoints** | Authenticated Requests | p50 $\le 15\text{ ms}$, p95 $\le 30\text{ ms}$ | $\le 100\text{ ms}$ |
| **PostgreSQL Queries** | Core Entity Lookups | $\le 5\text{ ms}$ | $\le 20\text{ ms}$ |

---

## The Optimization Workflow

```
1. MEASURE  → Establish baseline with real telemetry (Release/optimized build)
2. IDENTIFY → Isolate CPU sim vs. Render submit vs. GPU vs. DB/Network bottleneck
3. FIX      → Address the single verified root bottleneck (algorithmic > micro)
4. VERIFY   → Re-measure under identical conditions; keep only if verified win
5. GUARD    → Add regression gate checks, budgets, or automated assertions
```

---

## Track A: K2 Client & Game Engine Loop

### 1. Identify the Bottleneck: CPU vs. GPU
- Inspect `stat unit` or DXVK telemetry (`games/client/hon_x64_d3d9.log`).
- **If GPU time $\gg$ CPU time:** Attack draw calls, material/shader state changes, overdraw from overlapping transparent UI panels or particles, or texture resolution.
- **If CPU Render-Submit $\gg$ GPU:** The CPU is stalling submitting draw calls. Enable GPU instancing/batching, atlas UI textures, and reduce individual mesh draw calls.
- **If CPU Sim/Game $\gg$ GPU:** Game logic, bot choreography, or Lua script callbacks are over budget. Focus on spatial partitioning, reducing entity queries, and eliminating hot-loop allocations.

### 2. Kill Per-Frame Allocations (Lua 5.1 & C#)
- **Zero Allocations in Hot Ticks:** Repeated allocations inside `_process`, `Frame()`, or event hooks thrash memory pools and trigger GC latency spikes.
- **Lua String Concatenation Hazard:** Lua 5.1's `luaV_concat` can exhaust internal buffers under rapid un-yielded allocation loops. Cache strings, pre-allocate tables, and avoid building transient tables inside tick loops.
- **No Per-Frame File I/O:** Never invoke `loadfile` per frame in Lua. Use CVar change-signals (`tao_req`) over Named Pipes to trigger evaluation only on actual demand.

### 3. Object Pooling
- For transient game objects (projectiles, floating combat text, visual status indicators), pre-warm fixed pools during load time.
- Acquire by setting visible/active; release by resetting state and returning to pool. Never `instantiate` / `free` in hot tick loops.

### 4. Draw Call & Texture Precedence Optimization
- Merge UI elements into shared atlas sheets to minimize state changes.
- **Avoid `textures.s2z` Shadowing (`FN-003`):** Legacy entries in `games/client/game/textures.s2z` take precedence in `CResourceManager::RegisterTexture`. When overriding textures, use versioned basenames (e.g. `tab38_*.dds`) to prevent silent shadowing.
- Limit multi-layer alpha blending and overlapping fullscreen widgets to prevent fill-rate penalties.

---

## Track B: .NET Services & PostgreSQL Backend

### 1. Eliminate N+1 Database Queries
- **The Signature:** An endpoint executes 1 query to fetch parent records, followed by $N$ individual queries inside a loop to fetch child relations.
- **The Fix:** Eagerly load required relations via `.Include(...)` in EF Core, or project directly into flat DTOs via `.Select(...)`. See [references/optimization-patterns.md](references/optimization-patterns.md#n1-queries-backend).

### 2. Bounded Pagination
- Every list or search endpoint must enforce `Take(limit)` and stable ordering (`OrderBy(...)`).
- Never execute `.ToListAsync()` or `findMany()` across unbounded production tables. See [references/optimization-patterns.md](references/optimization-patterns.md#unbounded-data-fetching).

### 3. Query Plan Verification (`EXPLAIN ANALYZE`)
- Never guess on indexes. Run `EXPLAIN (ANALYZE, BUFFERS)` in PostgreSQL.
- If a query triggers a `Seq Scan` on a large table, add a targeted composite index matching query predicates (equality columns first, then range/sort columns).
- Use GIN trigram indexes (`gin_trgm_ops`) for partial text searches (`DiscordUsername`, account search).

### 4. Connection Pool Sizing
- Sizing rule: `instances × max_pool_size` must remain strictly below PostgreSQL's `max_connections`.
- If endpoints experience connection timeouts while CPU is low, inspect connection leaks and long-running queries holding connections open.

### 5. Multi-Layer Caching & Cache Stampede Defense
- Cache immutable or high-read / low-write data (server configurations, store catalogs, item definitions).
- Include all scoping dimensions in cache keys (tenant, role, language).
- **Never Cache Volatile Balances:** Currency balances, tickets, and active transaction states must always read authoritative database state.
- Guard against cache stampedes using single-flight / mutex coalescing for expensive cache misses.

---

## Verification & Guard Checklist

After any performance-related change:

- [ ] Baseline and post-optimization measurements recorded with identical hardware and test conditions.
- [ ] Frame time verified under K2 engine target: total $\le 6.94\text{ ms}$ (144 FPS), Lua UI handler $\le 1.0\text{ ms}$.
- [ ] No per-frame allocations introduced in hot Lua ticks or C# update loops.
- [ ] No N+1 queries in new backend endpoints; verified via SQL query logging or integration test assertions.
- [ ] Database queries verified via `EXPLAIN ANALYZE` (no accidental full-table sequential scans).
- [ ] All unit and integration tests remain 100% green (correctness never sacrificed for speed).
- [ ] Neutral or within-noise results reverted rather than preserved as speculative complexity.

## References & Patterns

- For concrete code implementations of N+1 fixes, pagination, indexing, and caching patterns, read [references/optimization-patterns.md](references/optimization-patterns.md).
