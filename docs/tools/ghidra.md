# 🐉 Ghidra Reverse-Engineering & Native MCP Server

> **Category:** `Developer Tool` | **Location:** `src/tools/ghidra` | **Identifier:** `ghidra`

---

## 📋 Overview

The **Ghidra MCP Server** provides instantaneous, AI-assisted reverse-engineering, native disassembly, xref tracing, and sub-second pseudo-C decompilation for the Heroes of Newerth x64 game engine.

By pairing Ghidra's decompiler engine with a persistent, pre-baked program database (`src/tools/ghidra/projects/ProjectKongor.rep`) and stdio protocol shielding, developers and AI pair-programmers can introspect internal K2 engine symbols, audit crash dumps, verify binary vtables, and analyze packet handlers with sub-second response times.

```
  ┌──────────────────────────────────────────────────────────────────────────┐
  │                 GHIDRA MCP REVERSE-ENGINEERING PIPELINE                  │
  │                                                                          │
  │   [ AI Assistant / WinDbg ] ──( JSON-RPC stdio )──► [ mcp_server.py ]   │
  │                                                               │          │
  │         • Stdio Protocol Shield (System.setOut -> System.err) │          │
  │         • Friendly Binary Aliasing (k2_x64.dll -> cached DB)  │          │
  │                                                               ▼          │
  │     [ Pre-Baked Repository: ProjectKongor.rep (7 Analyzed Binaries) ]     │
  │                                                                          │
  │         • hon_x64.exe           (0.2 MB) - Launcher & Entry Point        │
  │         • game_x64.dll          (1.1 MB) - Game Logic & Event Parser     │
  │         • vid_d3d9_x64.dll      (0.9 MB) - Direct3D 9.0c Graphics Driver │
  │         • vid_gl2_x64.dll       (0.9 MB) - OpenGL 2.x Graphics Driver    │
  │         • cgame_x64.dll         (2.8 MB) - Client Game UI & Entities     │
  │         • game_shared_x64.dll   (7.7 MB) - Shared Ability & Physics Core │
  │         • k2_x64.dll           (18.9 MB) - Core K2 Engine & Subsystems   │
  └──────────────────────────────────────────────────────────────────────────┘
```

---

## ⚙️ Architecture & Hardening Features

### 1. Stdio Transport Protocol Shielding
Standard input/output JSON-RPC MCP servers fail when third-party libraries write diagnostic text to `stdout` (e.g. `pyghidrecomp` writing `"Setting up Symbol Server for symbols..."`).
- **Java System Hook**: Redirects Java's `System.out` to `System.err` via `System.setOut(System.err)` inside a custom `pyghidra.start(False)` lifecycle wrapper.
- **Python Print Hook**: Redirects Python's `builtins.print()` to `sys.stderr`.
- **Guarantee**: `sys.stdout` contains strictly valid, single-line JSON-RPC 2.0 messages.

### 2. Friendly Binary Aliasing
Ghidra domain files append 6-character SHA-1 hex suffixes upon import (e.g. `/k2_x64.dll-64104e`).
- `PyGhidraContext._lookup_program_info` automatically normalizes names, strips regex hash patterns (`r"-[a-f0-9]{6,}$"`), and resolves case-insensitively.
- Developers and MCP clients can query clean, intuitive names:
  ```json
  {"binary_name": "k2_x64.dll", "name_or_address": "180a03810"}
  ```

### 3. Persistent Diagnostics Logging
- Rotates logs with a 10 MB limit to [`logs/ghidra_mcp.log`](../../logs/ghidra_mcp.log).
- Records all tool executions, background indexing stages, and address resolutions with millisecond precision.

---

## 📦 Pre-Baked Program Cache

All 7 core client binaries are fully pre-analyzed and indexed in `src/tools/ghidra/projects/ProjectKongor.rep`:

| Binary Name | Disk Size | Pre-Baked Status | Query Latency | Primary Responsibilities |
| :--- | :--- | :--- | :--- | :--- |
| **`hon_x64.exe`** | 0.2 MB | **ANALYZED** | `< 0.10s` | WinMain entry, security cookie, crash interceptor |
| **`game_x64.dll`** | 1.1 MB | **ANALYZED** | `< 0.10s` | Game server logic, packet event parser (`FUN_180035d40`) |
| **`vid_d3d9_x64.dll`** | 0.9 MB | **ANALYZED** | `< 0.10s` | Direct3D 9.0c HLSL shader compilation & device setup |
| **`vid_gl2_x64.dll`** | 0.9 MB | **ANALYZED** | `< 0.10s` | OpenGL 2.x fallback renderer |
| **`cgame_x64.dll`** | 2.8 MB | **ANALYZED** | `< 0.20s` | Client game vtable (`InitLibrary`), UI bindings |
| **`game_shared_x64.dll`**| 7.7 MB | **ANALYZED** | `< 0.15s` | Abilities (`IEntityAbility::LevelUp`), modifiers, affectors |
| **`k2_x64.dll`** | 18.9 MB | **ANALYZED** | `< 0.30s` | K2 Core, `CSystem`, VFS filesystem, console, memory |

---

## 🛠️ MCP Tools Catalog

The Ghidra MCP server exposes 19 specialized tools:

### Program & Project Management
- **`list_project_binaries`**: Returns all programs loaded in the ProjectKongor project along with analysis and indexing status.
- **`list_project_binary_metadata`**: Returns architecture (PE x64), compiler, function count, and image base address.
- **`save`**: Flushes and commits project modifications to disk.
- **`import_binary`**: Asynchronously imports and queues a new binary without blocking active queries.

### Decompilation & Assembly Inspection
- **`decompile_function`**: Returns clean, decompiled pseudo-C code, signatures, xrefs, and referenced strings for a function name or address.
- **`disassemble`**: Returns disassembled assembly instructions with opcodes and operands.
- **`read_bytes`**: Reads raw binary bytes from memory.

### Symbols, Strings & Cross References
- **`search_symbols_by_name`**: Finds exported, imported, or analyzed function and variable symbols matching a substring.
- **`list_exports`**: Enumerates all PE export table entries and ordinals.
- **`list_imports`**: Enumerates imported DLLs and imported functions.
- **`list_xrefs`**: Lists all code and data cross-references to or from a given function or memory address.
- **`search_strings`**: Performs semantic or literal string lookups across indexed binary memory.
- **`gen_callgraph`**: Generates caller/callee trees for a function.

### Reverse-Engineering Evidence & Provenance
- **`export_evidence_dossier`**: Aggregates decompiled pseudo-C, IDA-style SigScan mask, calling conventions, VTable indices, parameters, and dependencies into an immutable JSON dossier saved to `artifacts/evidence/{category}/{dossier_id}.json`. Returns both file paths and the full dossier object (Dual Payload Return).

---

## 🔬 Evidence Dossier Workflow & Offline CI Validation

To ensure clean-room reimplementation and sub-second CI validation without requiring a heavy 2 GB Java runtime in GitHub Actions:

1. **Schema Definition**: [`docs/specs/evidence-dossier-schema.json`](../specs/evidence-dossier-schema.json)
2. **Export Tool**: Call `export_evidence_dossier` with `binary_name="game_x64.dll"`, `name_or_address="0x180035d40"`, `output_category="packets"`.
3. **Offline Fast Test (< 0.05s)**:
   ```powershell
   python tests/unit/tools/test_evidence_dossiers.py
   ```


## 🧪 How to Use When Testing in the Future

### 1. Fast Automated Unit Testing
To verify project cache health, aliasing logic, and tool script syntax without launching heavy JVM instances:

```powershell
# Run the sub-second Ghidra MCP unit test
python -m unittest tests/unit/tools/test_ghidra_mcp.py
```
*Guaranteed execution time:* **< 0.01 seconds** (strictly compliant with the repo's < 2.0s manual gating rule).

### 2. Triangulating Native Crashes (WinDbg + Ghidra Loop)
When a native crash or access violation occurs during client testing:
1. Capture the faulting module and offset from WinDbg or engine diagnostics:
   ```
   FAULTING_IP: k2_x64.dll+0x3A20
   ```
2. Retrieve the module base address via `list_project_binary_metadata(binary_name="k2_x64.dll")` (Base: `0x180000000`).
3. Compute the target virtual address (`0x180000000 + 0x3A20 = 0x180003A20`).
4. Invoke `decompile_function`:
   ```json
   {
     "binary_name": "k2_x64.dll",
     "name_or_address": "180003A20"
   }
   ```
5. Inspect parameter registers (`RCX`, `RDX`, `R8`), calling conventions, and potential null-pointer dereferences in the returned pseudo-C.

### 3. Auditing Native Entity & Ability Mechanics
When balancing heroes or debugging custom gameplay logic in `game_shared_x64.dll`:
```json
// Find all functions related to Cooldowns or Leveling
{
  "binary_name": "game_shared_x64.dll",
  "query": "GetCooldown"
}
```
Then decompile the function to verify the exact timing equations and attribute modifiers.

### 4. Reverse-Engineering Packet Formats & Event Enums
When verifying network communication between dedicated servers and clients:
1. Search strings in `game_x64.dll`:
   ```json
   {"binary_name": "game_x64.dll", "query": "HERO_DEATH"}
   ```
2. Retrieve cross-references via `list_xrefs`:
   ```json
   {"binary_name": "game_x64.dll", "name_or_address": "1800cc110"}
   ```
3. Decompile the referencing dispatcher (`FUN_180035d40`) to inspect every opcode and event type parsed by the network loop.

### 5. Inspecting Project Status & Ingesting Updates
If game client binaries are updated or patched:
```powershell
# Inspect binary status
python src/tools/ghidra/ingest_binaries.py --status

# Batch-ingest newly patched binaries in ascending size order
python src/tools/ghidra/ingest_binaries.py --all
```
*(Ensure the MCP server is idle or stopped before running standalone CLI batch ingestion to avoid `.lock` contention).*
