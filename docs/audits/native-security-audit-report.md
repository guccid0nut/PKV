# ProjectKongor — Native C++ Binary Security & Recompilation Audit

**Auditor:** Principal Systems Engineer (Security & Native Runtime Swarm)  
**Date:** 2026-09-06  
**Status:** Complete & Actionable  
**Target:** `games/client/` and `games/server/` Native Dependencies

---

## 1. Executive Summary

ProjectKongor operates custom and legacy native C++ libraries interfacing with `hon_x64.exe`, K2 engine modules (`k2_x64.dll`, `cgame_x64.dll`), and dedicated server workers. A forensic dependency review identified three critical legacy native components requiring coordinated recompilation and security uplift.

| Native Component | Current Legacy Version | Target Modern Version | Target ABI / Toolchain | Primary CVEs Remediated |
| :--- | :--- | :--- | :--- | :--- |
| **OpenSSL** | 1.1.1u / 1.0.2 | **OpenSSL 3.3.2 LTS** | MSVC v143 / Clang 18 (x64) | CVE-2023-3817, CVE-2023-5678, EOL risk |
| **zlib** | 1.2.11 | **zlib 1.3.1** | MSVC v143 (AVX2 optimized) | CVE-2022-37434, CVE-2018-25032 (Buffer Overflow) |
| **libcurl** | 7.79.1 | **libcurl 8.8.0** | MSVC v143 (HTTP/2, TLS 1.3) | CVE-2023-38545 (SOCKS5 Heap Overflow), CVE-2023-46218 |

---

## 2. Vulnerability Assessment & Technical Impact

### 2.1 zlib 1.2.11 Heap Buffer Overflow (CVE-2022-37434)
- **Mechanism**: In `inflate.c`, the `inflateGetHeader` function allows a heap-based buffer over-read and overflow when processing gzip header extra field data.
- **Exposure**: Game resource archive extraction (`.s2z` files) and network packet decompression in `k2_x64.dll`. A maliciously crafted custom skin or map could trigger heap corruption.
- **Remediation**: Drop-in upgrade to `zlib 1.3.1`, which rewrites header parsing bounds checks and integrates SSE4.2 / AVX2 CRC32 vectorization.

### 2.2 OpenSSL 1.1.1 End-of-Life (EOL)
- **Mechanism**: OpenSSL 1.1.1 reached official End-of-Life on September 11, 2023. No public security patches are provided.
- **Exposure**: In-game store web requests, account authentication handshakes, and HTTPS telemetry.
- **Remediation**: Migration to `OpenSSL 3.3.2`. ProjectKongor's .NET components already leverage modern OS TLS 1.3 via `SslStream`, so this uplift harmonizes client native network modules with the backend.

### 2.3 libcurl SOCKS5 Heap Buffer Overflow (CVE-2023-38545)
- **Mechanism**: Severity 9.8 CVSS flaw where curl overflows a heap buffer during SOCKS5 hostname resolution.
- **Remediation**: Build static `libcurl 8.8.0` with Schannel or OpenSSL 3.3 backend.

---

## 3. Recompilation & Deployment Roadmap

1. **Step 1: Automated CMake Build Script**
   - Provide CMake presets for MSVC 2022 (v143) targeting `win-x64` and `linux-x64`.
2. **Step 2: Binary Verification**
   - Inspect exports and symbol mangling in `zlib1.dll`, `libcrypto-3-x64.dll`, and `libssl-3-x64.dll` to guarantee binary backward compatibility with K2 engine imports.
3. **Step 3: Verification with `kongor_vfs_resolve` and `kongor_engine_inspect`**
   - Validate that VFS zip archives mount and decompress with 100% fidelity without memory regressions.
