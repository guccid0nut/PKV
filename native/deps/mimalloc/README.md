# Microsoft mimalloc v2.3.2 Official Release Binaries

Provenance:
- Official GitHub Release: `https://github.com/microsoft/mimalloc/releases/tag/v3.3.2`
- Archive: `mimalloc-v2.3.2-windows-x64.tar.gz`
- Architecture: x64 Windows (MSVC)
- Files:
  - `mimalloc.dll` (Core lock-free allocator runtime, exports 160 C-ABI symbols including `mi_malloc`, `mi_free`, `mi_version`)
  - `mimalloc-redirect.dll` (Dynamic C Runtime `ucrtbase.dll` IAT redirector)
  - `mimalloc-override.dll` (Binary alias of `mimalloc.dll` for backward-compatible whitelist loaders)
  - `mimalloc.h` & `mimalloc-override.h` (C/C++ headers)
