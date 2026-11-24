# ADR-0009: Unified Cross-Platform CMake Native Build Matrix

- **Status:** Accepted
- **Date:** 2026-09-29
- **Deciders:** Core Engineering Team

## Context

Project Kongor maintains custom native C and C++ components that interface with the Heroes of Newerth game client, Vulkan graphics pipeline, and dedicated server network stack:
1. `native/tools/luac51`: Standalone Lua 5.1 bytecode compiler used by asset prebakers and K2 script validators.
2. `native/proxies/native_guard`: Windows PE DLL proxy (`version.dll`) utilizing MASM x64 assembly and Win32 APIs for memory protection, TLS redirection, and crash sentinel reporting.
3. `native/vulkan-layer`: Vulkan 1.3 replay capture and RmlUi modern HUD overlay layer (`VkLayer_hon_replay_capture.dll`).
4. `src/tools/hon-ping-shim`: Linux `LD_PRELOAD` shared object (`libhon-ping-shim.so`) intercepting UDP `recvfrom()` calls to serve server browser pings for headless dedicated servers.

Historically, these native targets relied on disparate build mechanisms (isolated CMake files, local Visual Studio `.sln` projects, GCC command lines, and batch scripts). Consequently, while C# microservices were rigorously tested in CI, native C/C++ targets were not compiled continuously from source in GitHub Actions, creating risk of compiler drift, missing dependencies, or syntax breakages.

## Decision

1. **Unified Root CMake Specification:** Establish a single root `native/CMakeLists.txt` that unifies all native components with platform detection:
   - On Windows: Compiles `luac51.exe`, `version.dll` (via MASM + MSVC), and `VkLayer_hon_replay_capture.dll`.
   - On Linux: Compiles `luac51` and `libhon-ping-shim.so` (via GCC/Clang).
2. **Standardized Submodule Mapping:** Maintain a root `.gitmodules` file mapping all vendored native dependencies (e.g. `RmlUi`) to enable clean recursive clones in CI and developer environments.
3. **CLI Ergonomics:** Integrate native compilation into `kongor.ps1 build native` and `scripts/build.ps1 -Target native`, providing a one-command clean rebuild experience (`-Clean`).
4. **CI Matrix Stage:** Add Stage 0 (`native-build-matrix`) to `.github/workflows/build-and-test.yml` running in parallel across Windows (`[self-hosted, windows]`) and Linux (`ubuntu-latest`) runners to verify clean compilation on every commit and upload binary build artifacts.

## Alternatives Considered

1. **Keep Visual Studio Solutions & Batch Files:** Rejected. Fragmented build mechanisms preclude automated Linux testing and create high developer onboarding friction.
2. **Vendor Precompiled Binaries Only:** Rejected. Precompiled binaries prevent automated validation of C/C++ modifications, static analysis, and cross-platform verification.

## Consequences

- **Positive:** Guarantees 100% build integrity across both C# and C++ layers in CI. Eliminates developer environment discrepancies and ensures all native shims can be compiled from clean source in seconds.
- **Negative:** Requires CMake 3.20+ and C/C++ compilers (MSVC/MASM on Windows, GCC/Clang on Linux) in build environments.
- **Neutral:** Game client and server runtime behaviors remain identical; output binaries are deployed to their respective target directories via CMake post-build rules.
