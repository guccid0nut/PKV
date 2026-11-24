# 🛡️ Native Engine Interop Proxies (`native/proxies`)

> **Cleanroom C/C++/MASM proxy libraries (`MPL-2.0`) built via the unified root CMake tree (`native/CMakeLists.txt`).**

All proxy DLLs (`Windows x64 MSVC`) and `LD_PRELOAD` shared objects (`Linux x86_64`) are compiled from source via `./kongor.ps1 build native` (or `cmake -B native/build -S native && cmake --build native/build --config Release`).

---

## 📂 Proxy Subsystems

| Directory | Output Target | Platform | Purpose |
| :--- | :--- | :--- | :--- |
| `native_guard/` | `version.dll` / `libk2_native_guard.so` / `libkongor_native_guard.so` | Windows & Linux | MASM/ABI-safe API forwarder, SEH/VEH crash shield, main-thread `CConsole::AddCmdBuffer` IPC ring, VFS loose cache, Queue Arcade & predictive pathing hooks |
| `kongor_zlib/` | `zlibwapi_x64.dll` | Windows x64 | `libdeflate` SIMD-accelerated drop-in proxy for fast `.s2z` decompression |
| `kongor_audio/` | `fmodex64.dll` | Windows x64 | FMOD Ex C-API forwarder bridging into `NEXUS.SpatialAudio` (OpenAL HRTF 3D spatialization) |
| `kongor_audio_stub/` | `fmodex64_null.dll` | Windows x64 | Null audio shim for headless/automated client verification |
| `kongor_video/` | `avcodec-x64-58.dll`, `avdevice-x64-58.dll`, `avfilter-x64-7.dll` | Windows x64 | FFmpeg video shim proxies fixing K2 video playback orientation & compatibility |
| `hon_ping_shim/` | `libhon-ping-shim.so` | Linux ELF | `LD_PRELOAD` `sendto`/`recvfrom` raw ICMP/UDP ping helper for Wine/Proton/Linux dedicated hosts |
| `kongor_crypto/` | `libcrypto` shim | Windows x64 | Cleanroom OpenSSL `libcrypto` ABI proxy |
| `kongor_ssl/` | `libssl` shim | Windows x64 | Cleanroom OpenSSL `libssl` ABI proxy |
| `kongor_curl/` | `libcurl` shim | Windows x64 | Cleanroom `libcurl` MASM/C++ proxy |

---

## ⚡ Quick Start

```powershell
# Configure and build all native targets & run CTest
.\kongor.ps1 build native
ctest --test-dir native/build -C Release --output-on-failure
```

---

## 📖 Related Documentation

- [Native Layer Overview (`native/README.md`)](../README.md)
- [ADR-0009: Unified Cross-Platform CMake Native Build Matrix](../../docs/adr/0009-native-build-unification.md)
