---
name: idapython
description: Headless IDA Pro 9.0 (idalib) and IDAPython 9.0 module router for Project Kongor dual-oracle reverse engineering (Ghidra + Hex-Rays). Use when decompiling K2 engine functions with Hex-Rays, comparing Ghidra vs. IDA output (Option V Dual-Oracle Disagreement Protocol), or writing headless IDAPython scripts against k2_x64.dll, cgame_x64.dll, and game_x64.dll.
capability_contract:
  allowed_worker_roles: ["kongor-offline-worker", "kongor-game-worker"]
  allowed_tools: ["call_mcp_tool", "run_command", "view_file"]
  requires_worker_lock: false
  max_probes: 10
---

# IDAPython 9.0 & Headless `idalib` Skill (Project Kongor Dual-Oracle)

## Overview
Project Kongor uses a **Dual-Oracle Reverse-Engineering Pipeline** (**Option V**):
1. **Ghidra (`pyghidra-mcp`)**: Primary pre-baked repository (`ProjectKongor.rep`) across all 7 K2 binaries.
2. **IDA Pro 9.0 (`idalib` / Hex-Rays)**: Secondary headless oracle (`C:\Program Files\IDA Professional 9.0\idalib\python`) used to cross-check complex MSVC x64 SSE/FPU routines, virtual inheritance (`CModelPanel`, `IWidget`), and decompiler artifacts (`CONCAT44`, `extraout_RAX`).

## IDAPython 9.0 Module Router
Always import the modern `ida_*` modules (never legacy `idc` wrappers):
- **`ida_hexrays`**: `decompile(ea)` $\rightarrow$ `cfuncptr_t` (`str(cfunc)` for Hex-Rays pseudo-C, `cfunc.lvars` for local variables).
- **`ida_funcs`**: `get_func(ea)`, `get_func_name(ea)`, `func_t.start_ea`, `func_t.end_ea`.
- **`ida_bytes`**: `get_bytes(ea, size)`, `get_dword(ea)`, `get_qword(ea)`.
- **`ida_xref`**: `xrefblk_t()`, `first_to(ea, XREF_ALL)`, `first_from(ea, XREF_ALL)`.
- **`ida_typeinf`**: `idc_parse_types(decl, 0)`, `apply_cdecl(til, ea, decl)` for syncing [`k2_structs.generated.h`](../../../src/engine/headers/k2_structs.generated.h).
- **`ida_nalt`**: `get_imagebase()` (`0x180000000` on x64 K2 DLLs; compute `ea = ida_nalt.get_imagebase() + rva`).

## Dual-Oracle Disagreement Protocol (`Option V`)
When both `decompiled_c` (Ghidra) and `hexrays_c` (IDA Hex-Rays) are present on an Evidence Dossier v2:
- Run `audit_dual_oracle_disagreement(dossier)` in [`evidence_dossier.py`](../../../src/tools/ghidra/evidence_dossier.py).
- Any mismatch in extracted struct offsets (`+0xOffset`) or control-flow branch counts automatically generates a `pending` obligation (`OBL-ORACLE-DIFF-XX`) and queues a live Frida verification probe in `artifacts/evidence/live_probe_queue.json`.
- **Never guess** which decompiler is right when they disagree—resolve the contradiction dynamically via Frida ([`triangulate_rva.js`](../../../scripts/re/triangulate_rva.js)) or WinDbg.
