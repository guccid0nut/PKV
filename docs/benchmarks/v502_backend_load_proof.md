# Project KONGOR V (`v5.0.2`) — Backend Concurrency & Valkey 8.1 Load Proof

> **Scope & Honesty Note:** All measurements below were captured on a single local developer host running `.NET 11.0.100` (`Release`, Server GC enabled) against an ephemeral local Docker `valkey/valkey:8.1-alpine` container (`127.0.0.1:6399`) with 4 independent `StackExchange.Redis.ConnectionMultiplexer` pods. These benchmarks prove single-flight stampede resistance, atomic Lua quorum/failover correctness, and single-host throughput; they do **not** claim multi-region WAN PostgreSQL or 5,000 live K2 game-client soak coverage.

- **Date:** `2026-11-21`
- **Target Suite:** [`tests/load/ASPIRE.Load.Tests/ValkeyMatchmakingAndPopulationLoadTests.cs`](../../tests/load/ASPIRE.Load.Tests/ValkeyMatchmakingAndPopulationLoadTests.cs)
- **Full Load Suite Result:** **21 / 21 PASSED** in `1.24 s` (`dotnet test tests/load/ASPIRE.Load.Tests/ASPIRE.Load.Tests.csproj -c Release`, full xUnit parallelization enabled)

---

## 1. Summary Table

| Scenario | Concurrency / Scale | Upstream / Atomic Proof | Wall Time | Throughput | `p50` | `p95` | `p99` | GC `(0/1/2)` |
| :--- | :---: | :--- | :---: | :---: | :---: | :---: | :---: | :---: |
| **`/api/telemetry/population` Cold Stampede** (`NexusHybridCache` + `GetPopulationBreakdownHandler`) | `5,000` simultaneous callers | **`2`** Redis `HashGetAsync` calls (`99.98%` reduction vs `10,000` uncached reads) | `81 ms` | `61,001.4 req/s` | `47.217 ms` | `52.647 ms` | `53.086 ms` | `1/1/1` *(combined)* |
| **`/api/telemetry/population` Warm L1 Burst** (`5s` TTL single-flight cache) | `5,000` simultaneous callers | **`0`** additional upstream Redis calls | `12 ms` | `410,913.9 req/s` | `0.002 ms` | **`0.008 ms`** | `0.012 ms` | `1/1/1` *(combined)* |
| **Live Valkey 8.1 Multi-Pod Lua Matchmaking Storm** (`4` live `ConnectionMultiplexer` pods) | `2,500` players (`250` 10-player matches, `4,250` ops) | `quorumWins = 250/250`, `transitions = 250/250`, `0` duplicate/CROSSSLOT errors | `111 ms` | `38,276.9 ops/s` | `18.768 ms` | `31.977 ms` | `32.803 ms` | — |
| **In-Process Multi-Pod Matchmaking Storm** (`ValkeyMatchmakingStateStore` fallback) | `2,500` players (`250` matches, `4,250` ops) | `quorumWins = 250/250`, `transitions = 250/250`, `0` collisions | `45 ms` | `92,926.2 ops/s` | `0.010 ms` | `1.150 ms` | `3.772 ms` | `0/0/0` |
| **ChatServer Pipeline Session Burst** (`ChatServerPipelineLoadTests`, per-thread allocs) | `50` sessions × `50` packets | `< 4,096 B/packet`, `0` starvation | `73 ms` | — | — | — | — | — |
| **MasterServer SRP-6a Concurrent Handshakes** (`MasterServerSrp6aLoadTests`) | Concurrent auth burst + tamper check | `100%` valid accepted, `100%` tampered rejected | `734 ms` | — | — | — | — | — |

---

## 2. Reproduction Commands

```powershell
# 1. Run full automated Release load suite (21 tests):
.\scripts\Invoke-KongorTestLock.ps1 -Owner "load-proof" -- dotnet test tests\load\ASPIRE.Load.Tests\ASPIRE.Load.Tests.csproj -c Release --nologo

# 2. Run the Live Valkey 8.1 4-pod 2,500-player Lua storm against local Valkey 8.1:
docker run -d --name kongor-valkey-proof -p 127.0.0.1:6399:6379 valkey/valkey:8.1-alpine
$env:KONGOR_RUN_MANUAL_TESTS = "1"
$env:KONGOR_VALKEY_LOAD_ENDPOINT = "127.0.0.1:6399"
.\scripts\Invoke-KongorTestLock.ps1 -Owner "valkey-proof" -- dotnet test tests\load\ASPIRE.Load.Tests\ASPIRE.Load.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~ValkeyMatchmakingAndPopulationLoadTests" --logger "console;verbosity=detailed"
docker rm -f kongor-valkey-proof
```
