# ADR-0010: Evaluating `aardappel/goose` (`v2026.4`) for Standalone Native CLI Tools

- **Status:** Proposed (Hold Further Migration at Spike 1 Gate; Keep Opt-In `-DKONGOR_ENABLE_GOOSE=OFF`)
- **Date:** 2026-11-10
- **Deciders:** Project Owner (Pending Final Sign-Off)

## Context

Project Kongor maintains standalone native C/C++ build and asset tools under `native/tools/` (such as `kongor-metagen` and `kongor-cas`) alongside `.NET 11` binary serialization components (`HeroFlatBufferGenerator` / `HeroFlatBufferLoader` in `src/libs/protocol-core/`).

We evaluated Wouter van Oortmerssen's **Goose** systems programming language (`https://github.com/aardappel/goose`, `v2026.4`, runtime `dce889723a23`, `Apache-2.0`), which transpiles to C99 and replaces heap allocations, manual borrow-checking annotations, and garbage collection with compiler-assigned virtual-memory data stacks, compact relative links (`&<u32>?`), and bounds-checked slices.

To evaluate Goose rigorously without risking production stability, we established a two-track, 4-gate empirical protocol:

1. **Track A (Pure C# Zero-Copy Fix — Decoupled from Goose):** Fix `KNFB` binary serialization allocation overhead directly in `.NET 11` (`ReadOnlySpan<byte>`, `BinaryPrimitives`, `ArrayPool<byte>`, `XxHash3`) without coupling `.NET` hosts (`WILLOWMAKER`, `TAO`) to Goose internal memory layouts.
2. **Track B (Opt-In Standalone Goose CLI Spikes Behind `-DKONGOR_ENABLE_GOOSE=ON`, Default `OFF`):**
   - **Hard Boundary:** Shared libraries (`.dll`/`.so`) inside `hon_x64.exe` (`native_guard`) and `.NET` host processes were strictly prohibited.
   - **Mandatory 4-Gate Go/No-Go Bar After Every Spike:**
     - **Gate 1:** `100%` deterministic output parity + portable cross-platform C build readiness.
     - **Gate 2:** `>=` parity throughput **and** lower OS Peak Working Set (RSS) than the C++ baseline (`scripts/qa/benchmark_goose_ab.ps1`, 20 runs after 3 warmups).
     - **Gate 3:** Verified `.goose` `#line` source-line breakpoint/stack-frame mapping in WinDbg (PDB) / LLDB (DWARF), plus non-crashing actionable CLI errors on hostile inputs.
     - **Gate 4:** `<= 3` AI-agent `goose check --diagnostics json` compile-fix retries per feature.
   - **Kill Rule:** Failing **any** gate halts further spikes (`kongor-cas-goose`, `kongor-replay-inspect-goose`) immediately and preserves C++ as the default toolchain.

---

## Empirical Results

### 1. Track A — Pure C# `KNFB` Zero-Copy Refactor (Shipped to Production)

Refactoring [HeroFlatBufferGenerator.cs](../../src/libs/protocol-core/Prebaking/Generators/HeroFlatBufferGenerator.cs) and [HeroFlatBufferLoader.cs](../../src/libs/protocol-core/Prebaking/Serialization/HeroFlatBufferLoader.cs) in pure C# eliminated intermediate `MemoryStream`/`BinaryReader`/`List.FindAll` allocations, fixed a latent upper-32-bit Unix timestamp clobber bug at header offset `16..23`, and added full `XxHash3` payload integrity verification (`145`-hero benchmark, `.NET 11` `Release`):

| Operation (`145` Heroes, `273,544 B` Payload) | Before (Median) | After (Median) | Latency Delta | Before GC Alloc | After GC Alloc | GC Alloc Delta |
|---|---:|---:|---:|---:|---:|---:|
| `HeroFlatBufferGenerator.Generate` | `0.625 ms` | `0.417 ms` | **`-33.3%`** | `1,867,743 B` | `273,934 B` | **`-85.3%`** |
| `HeroFlatBufferLoader.Load` | `0.456 ms` *(no checksum)* | `0.550 ms` *(full `XxHash3` check)* | `+0.094 ms` *(integrity added)* | `1,775,562 B` | `855,216 B` | **`-51.8%`** |

### 2. Track B, Spike 1 — `kongor-metagen-goose` vs. C++ `kongor-metagen`

We implemented [native/cmake/KongorGoose.cmake](../../native/cmake/KongorGoose.cmake), [native/goose/pkg/kongor_log/kongor_log.goose](../../native/goose/pkg/kongor_log/kongor_log.goose), [scripts/qa/benchmark_goose_ab.ps1](../../scripts/qa/benchmark_goose_ab.ps1), and [native/tools/kongor-metagen/goose/main.goose](../../native/tools/kongor-metagen/goose/main.goose) (`924` LOC vs. `949` LOC in [native/tools/kongor-metagen/main.cpp](../../native/tools/kongor-metagen/main.cpp)).

#### Head-to-Head A/B Benchmark (`MSVC 19.44` `x64` `Release`, `3` Warmups + `20` Measured Runs)

| Workload | Metric | C++ `kongor-metagen` | Goose `kongor-metagen-goose` | Delta / Result |
|---|---|---:|---:|---|
| **1x Production Schema** (`data/schemas/protocol.json`, `11.7 KB`) | Median / P95 Wall Time | `14.580 ms` / `19.265 ms` | `14.245 ms` / `17.113 ms` | **`1.024x` faster** (`-2.3%` median, `-11.2%` P95) |
| **1x Production Schema** | Peak Working Set (RSS) | `3,160.0 KB` | `3,168.0 KB` | `+0.25%` (`+8.0 KB` at OS process floor) |
| **50x Scaled Schema** (`1.85 MB`, `1,000` consts, `250` structs) | Median / P95 Wall Time | `29.431 ms` / `33.570 ms` | `26.251 ms` / `32.564 ms` | **`1.121x` faster** (`-10.8%` median) |
| **50x Scaled Schema** | Peak Working Set (RSS) | `15,380.0 KB` | `11,860.0 KB` | **`-22.89%` lower Peak RSS** (`-3,520 KB`) |
| **Compiled Binary Size** | Standalone `.exe` (`Release`) | `56.0 KB` | `118.0 KB` | `+62.0 KB` (`2.11x`, includes C99 runtime + float tables) |
| **Deterministic Output Parity** | SHA-256 (`kongor_protocol.h`, `.g.cs`, `.lua`) | Reference | `0` byte diff (`1x` & `50x`) | **`100%` Match** |

> **Bonus Defect Uncovered by A/B Testing:** Goose's standard `json::parse` library properly decoded RFC 8259 `\uXXXX` string escapes (`\u0027` -> `'`), exposing that the hand-rolled JSON parser in `native/tools/kongor-metagen/main.cpp` strips `\` without decoding hex escapes (`\u0027` -> `u0027`).

#### 4-Gate Scorecard for Spike 1

| Gate | Requirement | Result | Empirical Evidence |
|---|---|:---:|---|
| **Gate 1** | `100%` Output Parity & Cross-Platform Build Matrix | **PARTIAL** | `0`-byte SHA-256 diff across C++, C#, and Lua outputs (`1x` and `50x`); `0` C compiler warnings via `goose --emit-runtime`; post-transpile CMake sanitizer strips canonical local-package paths (`gs_file0[]`). **However, only Windows `x64` (`MSVC 19.44`) was compiled and verified locally** (MSVC `ARM64` and WSL/Linux/macOS toolchains were unavailable on the test host). |
| **Gate 2** | `>=` Throughput & Lower Peak RSS vs. C++ | **FAIL (1x Production)** / **PASS (50x Synthetic)** | On the real `11.7 KB` production schema (`1x`), `14.245 ms` vs. `14.580 ms` is within process-startup noise, **Peak RSS was `+0.25%` (`+8 KB`) higher** (`3,168 KB` vs. `3,160 KB`), and the binary is **`2.11x` larger** (`118 KB` vs. `56 KB`). Goose only achieved lower Peak RSS (`-22.89%`, `11,860 KB` vs. `15,380 KB`) on a synthetic `50x` (`1.85 MB`) schema that Project Kongor does not have in production. |
| **Gate 3** | `.goose` `#line` WinDbg/LLDB Debugging & Hostile Input Safety | **FAIL (3a)** / **PASS (3b)** | **3b PASS:** `13/13` hostile/malformed/cyclic JSON inputs exited cleanly (`code 1`) with actionable `Diagnose/Fix:` guidance and `0` crashes.<br/>**3a FAIL (Blocker):** (1) Upstream `goose v2026.4` emits **no `#line` directives** (`docs/design/debugging.md` §1: *"Nothing here is on master... a `.goose` file cannot take a breakpoint"*); `bp \`main.goose:464\`` fails in WinDbg CDB and `lsa .` lands in `14,026` lines of generated C (`kongor-metagen-goose.c:2171`). (2) On Windows, Goose reserves `2 GB` per stack via uncommitted `VirtualAlloc(..., MEM_RESERVE)` and installs a first-position `AddVectoredExceptionHandler(1, gs_fault_filter)` (`goose_runtime.c:1636,1660`) that relies on hardware first-chance `0xC0000005` (`EXCEPTION_ACCESS_VIOLATION`) faults to commit memory chunks—halting WinDbg/VS debuggers before `main_g` unless `sxi av` is set (which masks real C FFI access violations). |
| **Gate 4** | AI-Agent Authoring Velocity (`<= 3` Retries) | **PASS** | `2` `goose check --diagnostics json` retries (`<= 3` limit), both caused by `§9.2` scope-depth slice re-binding (`var` slice rebound from static literal to local stack), documented in [native/goose/README.md](../../native/goose/README.md). |

---

## Decision

Per the mandatory 4-gate kill rule (**Spike 1 failed Gate 2 at production scale and Gate 3a on source-line debugger mapping / Windows `0xC0000005` VEH demand-paging friction, with Gate 1 only partially verified on Windows `x64`**):

1. **Ship Track A Permanently (Separate Commit):** Land the pure C# zero-copy `HeroFlatBufferGenerator` / `HeroFlatBufferLoader` optimizations, 64-bit timestamp fix, and `XxHash3` tamper verification in `src/libs/protocol-core/` independently of any Goose files.
2. **Halt Track B Production Migration After Spike 1:** Do **not** waive Gate 3a and do **not** proceed to Spike 2 (`kongor-cas-goose`) or Spike 3 (`kongor-replay-inspect-goose`). C++17 (`native/CMakeLists.txt`) remains the authoritative native language (`KONGOR_ENABLE_GOOSE=OFF`).
3. **Execute Pure C++ Upgrades Directly in C++ (Completed — Track C):**
   - Patched RFC 8259 `\uXXXX` + UTF-16 surrogate pair unescaping, self-referential struct cycle (`A -> A`) detection, strict CLI/JSON error handling, and RAII `KongorArena` cleanup in [native/tools/kongor-metagen/main.cpp](../../native/tools/kongor-metagen/main.cpp).
   - Removed `<windows.h>`/`bcrypt.lib` lock-in in [native/tools/kongor-cas/main.cpp](../../native/tools/kongor-cas/main.cpp) and [native/tools/kongor-cas/CMakeLists.txt](../../native/tools/kongor-cas/CMakeLists.txt) (**verified locally on Windows `x64`, verified on Linux `x64` via `ubuntu-latest` CI, and portable by design for macOS/ARM64**) via [native/include/kongor_sha256.h](../../native/include/kongor_sha256.h) (zero-heap cached Win32 CNG SHA-NI fast path + portable FIPS 180-4 SHA-256), fixed raw deflate (`RFC 1951`) -> `RFC 1950` zlib framing (`libdeflate_zlib_compress`), UTF-8 path extraction (`generic_u8string()`), and fail-closed ASCII lowercase `vpath` Merkle root normalization for lossless `.kpatch` interop with [tools/assetdb/cas.py](../../tools/assetdb/cas.py) and [tools/assetdb/patch_engine.py](../../tools/assetdb/patch_engine.py), bounded streaming ingest RSS to `~2 MB`, and added `kongor-cas verify` + CTest seam coverage ([native/tests/test_cas_and_metagen.cpp](../../native/tests/test_cas_and_metagen.cpp)).
4. **Preserve Opt-In Experimental Harness With a Staleness Sunset Rule:** Keep `KongorGoose.cmake`, `kongor_log`, `benchmark_goose_ab.ps1`, and `kongor-metagen-goose` behind `-DKONGOR_ENABLE_GOOSE=OFF` for one-command future re-testing. **Sunset Rule:** If a future build or toolchain change breaks the dormant Goose spike before upstream Goose resolves Gate 3a, delete the spike rather than spending maintenance time repairing it.
5. **Maintain Permanent In-Process Ban:** Never load Goose-compiled shared libraries inside `hon_x64.exe` (`native_guard`) or `.NET` hosts (`WILLOWMAKER`, `TAO`). Beyond foreign thread-pool and abort boundaries, Goose's `AddVectoredExceptionHandler(1, gs_fault_filter)` registers at the head of the Windows VEH chain to intercept `0xC0000005` page faults, which poses a strong architectural risk of interfering with K2 crash sentinels and CoreCLR `NullReferenceException` handlers.

### Re-Entry Criteria for Future Evaluation

Re-open Spike 2 (`kongor-cas-goose`) and Spike 3 (`kongor-replay-inspect-goose`) only if:
1. Upstream `aardappel/goose` ships the `#line` PDB/DWARF emission described in `docs/design/debugging.md` so WinDbg and LLDB resolve `.goose` breakpoints and stack frames directly; **and**
2. Windows data stacks pre-commit an initial page (or provide a non-AV commit mode under `--debug`) so WinDbg/Visual Studio debugging does not require globally suppressing first-chance `0xC0000005` exceptions (`sxi av`).

---

## Alternatives Considered

1. **Proceed Immediately with Full `native/tools/` Migration to Goose `v2026.4`:** Rejected. While Goose demonstrated impressive MB-scale memory efficiency (`-22.89%` Peak RSS on `50x` schemas), `1.121x` faster execution, and fast AI-agent authoring (`2` retries), losing `.goose` source-line debugging in WinDbg/LLDB and masking first-chance `0xC0000005` exceptions on Windows violates Project Kongor's debuggability invariant.
2. **Load Goose Shared DLLs via P/Invoke in `.NET` (`HeroFlatBufferLoader`):** Rejected. Pure C# `ReadOnlySpan<byte>` + `ArrayPool<byte>` delivered an `85.3%` allocation reduction and `33.3%` generation speedup in `< 1` day with zero native marshalling or VEH conflicts.
3. **Delete All Spike 1 Infrastructure:** Rejected. Keeping `KongorGoose.cmake`, `benchmark_goose_ab.ps1`, and `kongor-metagen-goose` behind `option(KONGOR_ENABLE_GOOSE OFF)` costs zero build time by default while preserving a repeatable benchmark harness for future upstream Goose releases.

## Consequences

- **Positive:** Immediate production C# `KNFB` performance and integrity gains; cross-platform C++ `kongor-cas` & `.kpatch` delta-patching compatibility (`RFC 1950` zlib + fail-closed ASCII canonical Merkle roots, verified on Windows `x64` + Linux `ubuntu-latest` CI and portable by design for macOS); RFC 8259 `\uXXXX` compliance in C++ `kongor-metagen`; zero impact on cleanroom C++ builds (`KONGOR_ENABLE_GOOSE=OFF`); empirical proof preventing a premature multi-tool migration and uncovering a critical Windows VEH interaction before any `hon_x64.exe`/`.NET` integration could occur.
- **Negative:** Standalone C++ tools (`kongor-cas`, `kongor-metagen`) continue to rely on explicit C++ memory discipline (`KongorArena` + bounded streaming buffers) rather than compiler-enforced data stacks unless upstream Goose lands `#line` debug support and non-AV stack commits.
- **Neutral:** Developers wishing to experiment with Goose locally can opt in via `.\scripts\Install-OptionalAgentTools.ps1 -Tool goose` and `cmake -S native -B native/build_goose -DKONGOR_ENABLE_GOOSE=ON`.
