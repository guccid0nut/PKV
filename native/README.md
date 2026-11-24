# Project KONGOR V — Native Engine Modernization Layer (`native/`)

SPDX-License-Identifier: MPL-2.0

## Licensing Scope

All first-party C and C++ source files, headers, and CMake build definitions authored for **Project KONGOR V (`PKV`)** inside this `native/` directory tree are licensed under the **Mozilla Public License Version 2.0 (`MPL-2.0`)** — see [`LICENSE`](LICENSE).

Per `MPL-2.0` Section 3.3, `MPL-2.0` is Secondary-License compatible with the root repository's `AGPL-3.0-or-later` license ([`../LICENSE`](../LICENSE)).

### Third-Party & Dual-Licensed Subdirectories Excluded from `MPL-2.0` Coverage

The following vendored dependencies and submodules carry their own upstream permissive or kernel-compatible licenses (documented in [`../THIRD_PARTY_NOTICES.md`](../THIRD_PARTY_NOTICES.md)):

- `native/deps/rmlui/` — `MIT License` (`The RmlUi Team`)
- `native/deps/libdeflate/` — `MIT License` (`Eric Biggers`)
- `native/deps/mimalloc/` — `MIT License` (`Microsoft Corporation, Daan Leijen` — see [`deps/mimalloc/LICENSE`](deps/mimalloc/LICENSE))
- `native/deps/miniz/` — `MIT License` (`Rich Geldreich / RAD Game Tools / Valve Software`)
- `native/deps/stb_truetype.h` — `MIT OR Unlicense` (`Sean Barrett`)
- `native/vulkan-layer/deps/qrcodegen.*` — `MIT License` (`Project Nayuki`)
- `native/vulkan-layer/include/vulkan/` — `Apache-2.0 OR MIT` (`Khronos Group Vulkan-Headers`)
- `native/tools/luac51/lua-5.1.5/` — `MIT License` (`Lua.org, PUC-Rio`)
- `native/ebpf/xdp_filter/` — `Dual BSD/GPL` (required for Linux kernel eBPF verifier helper compatibility)
- Opt-in `aardappel/goose` (`-DKONGOR_ENABLE_GOOSE=ON` standalone CLI toolchain) — `Apache-2.0` (`Wouter van Oortmerssen`)
