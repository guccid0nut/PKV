# Project Kongor — Goose (`v2026.4`) Agent Primer & Idiomatic Reference

> [!IMPORTANT]
> **Hard Architectural Boundary:** Goose in Project Kongor is strictly restricted to **standalone CLI processes** built behind opt-in `-DKONGOR_ENABLE_GOOSE=ON` (`native/cmake/KongorGoose.cmake`). Shared libraries (`.dll`/`.so`) inside `hon_x64.exe` (`native_guard`) or `.NET` hosts (`WILLOWMAKER`, `TAO`) are **strictly prohibited**.

## 1. Fast Pre-Flight Typecheck & Formatting (Always Run Before CMake)

```powershell
$goose = "src/tools/goose/bin/goose-v2026.4-windows-x64/goose.exe"
$env:GOOSE_HOME = (Resolve-Path "src/tools/goose/home").Path

# Structured JSON compiler diagnostics + data-stack report (< 100 ms):
& $goose check --diagnostics json --stacks native/tools/<tool>/goose/main.goose

# Canonical formatting check:
& $goose format --check native/tools/<tool>/goose/main.goose
```

## 2. Core Language & Memory Model Cheat Sheet (`v2026.4`)

### Imports & Packages
- **Global stdlib modules** (imported without namespace prefix): `import std;`, `import os;`, `import dictionary;`, `import slot_pool;`, `import slice_pool;`, `import vec;`, `import math;`.
- **Namespaced stdlib modules** (call via `ns::fn`): `import json;` (`json::parse`, `json::reader`), `import binary;` (`binary::reader`, `binary::read_u32le`), `import ffi;` (`ffi::cstr`, `ffi::bytes`), `import base64;`, `import csv;`, `import regex;`.
- **Local packages**: Declare **only in the root `.goose` file** using a relative path:
  ```goose
  import kongor_log from "../../../goose/pkg/kongor_log";
  ```
  Call via `kongor_log::init("kongor-metagen-goose");`, `kongor_log::info(str("Parsed ", n, " items"));`.

### Arrays, Slices & Strings (Zero Heap Allocations)
- Strings are UTF-8 byte arrays/slices:
  - Grow-only buffer / builder: `var buf: u8[>..] = [];` (`buf.push('a')`, `buf.append("text")`, `format(buf, "x=", 42)`).
  - Stack-like grow/shrink buffer: `var stack: Open[>..<] = [];` (`stack.push(...)`, `stack.pop()`, `stack.resize(0)`).
  - Fixed-capacity inline array: `var small: u8[..64] = [];`.
  - Read-only slice view (accepts literals, builders, and sub-slices): `const u8[:]` (e.g., `fn parse_key(s: const u8[:]) -> bool`).
  - Mutable slice view: `u8[:]`.
- **String formatting**:
  - `str("count=", count, ", path=", path)` returns a fresh `u8[>..]` directly into the caller's destination stack.
  - `format(out, a, b, c)` appends directly to `out: u8[>..]&`.
  - `format_uint(out, val_u64, 16, 8, '0')` writes zero-padded hex; `format_flt(out, val_f64, 2)` writes fixed decimals.

### References, Rebinding (`= ` vs `.=`) & Optional References
- `=` mutates the **value** referred to; `.=` rebinds **which element** a `var` reference points at:
  ```goose
  var cur .= pool[0];
  if next { cur .= next; }
  ```
- **Different-root slice selection (`§9.2`)**: Do not rebind a `var` slice from one local array to another local array declared later; bind once with `let s: const u8[:] = if cond { buf_a } else { buf_b };`.
- Self-relative compact links inside grow-only pools (`Node[>..]`): `next: Node&<u32>?`.
- Reuse pools (note: `reusable` is deprecated; use `slot_pool<T>` or `slice_pool<T>` when freeing/reusing slots).

### Casts & Control Flow
- Widening casts are implicit or `x as i64` (checked in `--debug`).
- Narrowing / sign-reinterpreting casts that intentionally truncate or reinterpret bits **must** use `as!` (e.g., `v as! u8`, `neg as! u64`).
- Early exit guard:
  ```goose
  guard read_file(path, text) else {
      kongor_log::error_actionable(str("Cannot read ", path), "Verify path exists");
      exit(1);
  }
  ```
- Multiple return values:
  ```goose
  fn read_pair(s: const u8[:]) -> i64, bool { return 42, true; }
  let val, ok = read_pair(input);
  ```
- Non-local return from nested helper function: `return err from outer_fn;`.

### Calling C (`extern fn`)
- Slices cross C as `sl_u8` (`typedef struct { uint8_t *data; int64_t len; } sl_u8;` — always pass/declare explicit slice or `(ptr, len)` helpers).
- Builders (`u8[>..]&`) cross C as `gs_rref` (`gs_bld_append(out, ptr, len)`).
- Wire external `.c` shims and static libraries (`libdeflate`) via `add_kongor_goose_cli(... C_SOURCES ... INCLUDE_HEADERS ... LINK_LIBRARIES ...)` in `CMakeLists.txt`.

## 3. Known `v2026.4` Toolchain & Debugger Notes
- **Debug Line Mapping (`docs/design/debugging.md`)**: Upstream `goose v2026.4` emits C function symbols (`*_g`, e.g., `main_g`, `generate_cpp_header_g`) and embeds exact `.goose` file + line numbers into `--debug` runtime traps (`gs_abort(..., gs_file0, line)`), but does **not** yet emit `#line` C preprocessor directives (`docs/design/debugging.md` §1). Source breakpoints (`bp \`main.goose:464\``) cannot resolve in WinDbg/LLDB; stepping lands in generated `<target>.c`.
- **Windows First-Chance `0xC0000005` VEH Demand Paging (`goose_runtime.c`)**: On Windows, Goose reserves `2 GB` per data stack via uncommitted `VirtualAlloc(..., MEM_RESERVE)` and installs `AddVectoredExceptionHandler(1, gs_fault_filter)` to commit `GS_COMMIT_CHUNK` pages on first-chance `EXCEPTION_ACCESS_VIOLATION` (`0xC0000005`). When stepping a standalone Goose CLI in WinDbg/CDB, run `sxi av` first so demand-paging faults pass through to `gs_fault_filter` without halting before `main_g`. (This also reinforces the hard architectural ban on loading Goose inside `hon_x64.exe` or `.NET` processes where foreign VEH chains are active.)
- **Cleanroom Binary Hygiene**: `goose` resolves relative local package imports (`import ... from "..."`) via canonical absolute paths in `gs_fileN[]`; `native/cmake/KongorGoose.cmake` runs an automated post-transpile CMake sanitizer stripping `${REPO_ROOT}/` from `<target>.c` prior to C compilation so binaries contain zero workstation paths.
