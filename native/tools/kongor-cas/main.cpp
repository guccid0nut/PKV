// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ============================================================================
// native/tools/kongor-cas/main.cpp
// Project Kongor Cross-Platform Content-Addressed Storage & Merkle Engine CLI
//
// Implements the Lore CAS & Delta Patching architecture across Windows/Linux/macOS:
// 1. Hybrid 1-to-1 (<1MB) vs 1MB Fixed Slicing (>=1MB) with bounded ~2MB streaming RSS.
// 2. Dual hashing: KongorSha256 (Win32 CNG SHA-NI / Portable FIPS 180-4) + libdeflate SIMD CRC32.
// 3. RFC 1950 Zlib compression via libdeflate_zlib_compress (level 6) for 100% interop
//    with tools/assetdb/cas.py (zlib.decompress) and patch_engine.py (.kpatch).
// 4. Durable fsync/_commit + atomic MoveFileExW / POSIX rename() commits to .cas/objects/xx/yy...
// 5. Canonical Merkle tree root calculation matching tools/assetdb/cas.py byte-for-byte.
// 6. Cross-platform CAS store integrity verifier ('kongor-cas verify').
// ============================================================================

#include "kongor_sha256.h"
#include "kongor_arena.h"
#include "kongor_string.h"

#include <libdeflate.h>

#ifdef _WIN32
#include <io.h>
#include <process.h>
#else
#include <unistd.h>
#endif

#include <cstdio>
#include <cstdlib>
#include <cstdint>
#include <cstring>
#include <atomic>
#include <chrono>
#include <string>
#include <vector>
#include <algorithm>
#include <filesystem>
#include <fstream>
#include <system_error>

namespace fs = std::filesystem;

static constexpr size_t CAS_CHUNK_SIZE = 1048576; // 1 MB (1,048,576 bytes)

// ============================================================================
// PORTABLE FILE & STRING HELPERS
// ============================================================================
static FILE* OpenBinaryFileRead(const fs::path& p) {
#ifdef _WIN32
    return _wfopen(p.c_str(), L"rb");
#else
    return std::fopen(p.c_str(), "rb");
#endif
}

static FILE* OpenBinaryFileWrite(const fs::path& p) {
#ifdef _WIN32
    return _wfopen(p.c_str(), L"wb");
#else
    return std::fopen(p.c_str(), "wb");
#endif
}

static bool FlushAndSyncFile(FILE* fp) {
    if (!fp) return false;
    if (std::fflush(fp) != 0) return false;
#ifdef _WIN32
    int fd = _fileno(fp);
    if (fd >= 0 && _commit(fd) != 0) return false;
#else
    int fd = fileno(fp);
    if (fd >= 0 && fsync(fd) != 0) return false;
#endif
    return true;
}

static uint64_t GetCurrentProcessIdPortable() {
#ifdef _WIN32
    return static_cast<uint64_t>(_getpid());
#else
    return static_cast<uint64_t>(getpid());
#endif
}

static std::string PathGenericUtf8(const fs::path& p) {
    auto u8 = p.generic_u8string();
    return std::string(reinterpret_cast<const char*>(u8.data()), u8.size());
}

static std::string PathDisplayUtf8(const fs::path& p) {
    auto u8 = p.u8string();
    return std::string(reinterpret_cast<const char*>(u8.data()), u8.size());
}

static bool IsPrintableAsciiVpath(const std::string& s) {
    if (s.empty()) return false;
    for (size_t i = 0; i < s.size(); ++i) {
        unsigned char c = static_cast<unsigned char>(s[i]);
        if (c < 0x20u || c > 0x7Eu) {
            return false;
        }
    }
    return true;
}

static std::string AsciiLower(const std::string& s) {
    std::string out = s;
    for (size_t i = 0; i < out.size(); ++i) {
        unsigned char c = static_cast<unsigned char>(out[i]);
        if (c >= 'A' && c <= 'Z') {
            out[i] = static_cast<char>(c + ('a' - 'A'));
        }
    }
    return out;
}

static bool IsValidHex64(const std::string& s) {
    if (s.size() != 64) return false;
    for (char ch : s) {
        unsigned char c = static_cast<unsigned char>(ch);
        bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        if (!ok) return false;
    }
    return true;
}

static std::string NormalizeMerkleVpath(const std::string& vpath) {
    std::string norm = vpath;
    for (char& ch : norm) {
        if (ch == '\\') ch = '/';
    }
    size_t start = 0;
    while (start < norm.size() && norm[start] == '/') {
        ++start;
    }
    size_t end = norm.size();
    while (end > start && norm[end - 1] == '/') {
        --end;
    }
    return AsciiLower(norm.substr(start, end - start));
}

static std::string EscapeJsonString(const std::string& s) {
    std::string out;
    out.reserve(s.size() + 8);
    for (size_t i = 0; i < s.size(); ++i) {
        unsigned char c = static_cast<unsigned char>(s[i]);
        switch (c) {
            case '"':  out += "\\\""; break;
            case '\\': out += "\\\\"; break;
            case '\b': out += "\\b"; break;
            case '\f': out += "\\f"; break;
            case '\n': out += "\\n"; break;
            case '\r': out += "\\r"; break;
            case '\t': out += "\\t"; break;
            default:
                if (c < 0x20u) {
                    char buf[7];
                    std::snprintf(buf, sizeof(buf), "\\u%04x", static_cast<unsigned int>(c));
                    out += buf;
                } else {
                    out.push_back(static_cast<char>(c));
                }
                break;
        }
    }
    return out;
}

// ============================================================================
// ATOMIC CAS OBJECT STORAGE (.cas/objects/xx/yyyy...)
// ============================================================================
static fs::path GetCasObjectPath(const fs::path& casDir, const std::string& chunkHashLower) {
    return casDir / "objects" / chunkHashLower.substr(0, 2) / chunkHashLower.substr(2);
}

static bool SaveChunkBytesToCas(const fs::path& casDir, const std::string& chunkHash, const uint8_t* payload, size_t payloadSize) {
    std::string hashNorm = AsciiLower(chunkHash);
    if (!IsValidHex64(hashNorm)) {
        std::fprintf(stderr,
            "[ERROR] Invalid 64-char SHA-256 chunk hash '%s' during CAS write | Diagnose/Fix: Run 'kongor-cas verify \"%s\"'\n",
            chunkHash.c_str(), casDir.string().c_str());
        return false;
    }

    fs::path targetPath = GetCasObjectPath(casDir, hashNorm);
    std::error_code ec;
    if (fs::exists(targetPath, ec) && !ec) {
        return true; // Content-addressed immutability: already persisted
    }

    fs::path parentDir = targetPath.parent_path();
    fs::create_directories(parentDir, ec);
    if (ec && !fs::is_directory(parentDir)) {
        std::fprintf(stderr,
            "[ERROR] Failed to create CAS bucket directory '%s': %s | Diagnose/Fix: Check directory permissions on '%s'\n",
            parentDir.string().c_str(), ec.message().c_str(), casDir.string().c_str());
        return false;
    }

    static std::atomic<uint64_t> s_tmpCounter{0};
    uint64_t seq = s_tmpCounter.fetch_add(1, std::memory_order_relaxed);
    uint64_t ticks = static_cast<uint64_t>(
        std::chrono::steady_clock::now().time_since_epoch().count());
    std::string tmpSuffix = ".tmp." + std::to_string(GetCurrentProcessIdPortable()) +
                            "_" + std::to_string(ticks) + "_" + std::to_string(seq);
    fs::path tmpPath = targetPath;
    tmpPath += tmpSuffix;

    FILE* fp = OpenBinaryFileWrite(tmpPath);
    if (!fp) {
        std::fprintf(stderr,
            "[ERROR] Failed to open temporary CAS file '%s' for write | Diagnose/Fix: Verify disk space and write access on '%s'\n",
            tmpPath.string().c_str(), casDir.string().c_str());
        return false;
    }

    size_t written = (payloadSize > 0) ? std::fwrite(payload, 1, payloadSize, fp) : 0;
    bool synced = (written == payloadSize) && FlushAndSyncFile(fp);
    int closeRc = std::fclose(fp);

    if (!synced || closeRc != 0) {
        fs::remove(tmpPath, ec);
        std::fprintf(stderr,
            "[ERROR] Short write or fsync failure on temporary CAS chunk '%s' (%zu/%zu bytes) | Diagnose/Fix: Check available disk space and run 'kongor-cas verify \"%s\"'\n",
            tmpPath.string().c_str(), written, payloadSize, casDir.string().c_str());
        return false;
    }

#ifdef _WIN32
    if (!MoveFileExW(tmpPath.c_str(), targetPath.c_str(), MOVEFILE_REPLACE_EXISTING)) {
        DWORD winErr = GetLastError();
        // If another concurrent worker atomically committed the exact same immutable hash first,
        // accept existing target and clean up our temporary file.
        if (fs::exists(targetPath, ec) && !ec) {
            fs::remove(tmpPath, ec);
            return true;
        }
        fs::remove(tmpPath, ec);
        std::fprintf(stderr,
            "[ERROR] MoveFileExW failed committing CAS chunk '%s' (Win32 error %lu) | Diagnose/Fix: Close processes locking '%s' and retry\n",
            targetPath.string().c_str(), static_cast<unsigned long>(winErr), casDir.string().c_str());
        return false;
    }
#else
    if (std::rename(tmpPath.c_str(), targetPath.c_str()) != 0) {
        if (fs::exists(targetPath, ec) && !ec) {
            fs::remove(tmpPath, ec);
            return true;
        }
        fs::remove(tmpPath, ec);
        std::fprintf(stderr,
            "[ERROR] POSIX rename() failed committing CAS chunk '%s' | Diagnose/Fix: Ensure '%s' resides on a writable local filesystem\n",
            targetPath.string().c_str(), casDir.string().c_str());
        return false;
    }
#endif

    return true;
}

// ============================================================================
// STREAMING HYBRID DUAL-HASHING & ZLIB (RFC 1950) INGEST ENGINE
// ============================================================================
struct CasIngestSummary {
    std::string blob_hash;
    uint64_t total_size = 0;
    uint32_t crc32 = 0;
    uint32_t chunk_count = 0;
};

struct LibdeflateCompressorGuard {
    struct libdeflate_compressor* ptr = nullptr;
    explicit LibdeflateCompressorGuard(int level) : ptr(libdeflate_alloc_compressor(level)) {}
    ~LibdeflateCompressorGuard() {
        if (ptr) libdeflate_free_compressor(ptr);
    }
    LibdeflateCompressorGuard(const LibdeflateCompressorGuard&) = delete;
    LibdeflateCompressorGuard& operator=(const LibdeflateCompressorGuard&) = delete;
};

struct LibdeflateDecompressorGuard {
    struct libdeflate_decompressor* ptr = nullptr;
    LibdeflateDecompressorGuard() : ptr(libdeflate_alloc_decompressor()) {}
    ~LibdeflateDecompressorGuard() {
        if (ptr) libdeflate_free_decompressor(ptr);
    }
    LibdeflateDecompressorGuard(const LibdeflateDecompressorGuard&) = delete;
    LibdeflateDecompressorGuard& operator=(const LibdeflateDecompressorGuard&) = delete;
};

static bool CompressAndPersistSlice(
    const fs::path& casDir,
    const std::string& chunkHash,
    const uint8_t* rawSlice,
    size_t sliceLen,
    struct libdeflate_compressor* compressor,
    std::vector<uint8_t>& compBuf)
{
    if (compressor && sliceLen > 0) {
        size_t bound = libdeflate_zlib_compress_bound(compressor, sliceLen);
        if (compBuf.size() < bound) {
            compBuf.resize(bound);
        }
        // RFC 1950 zlib format: matches Python tools/assetdb/cas.py zlib.compress(data, level=6)
        size_t compBytes = libdeflate_zlib_compress(compressor, rawSlice, sliceLen, compBuf.data(), compBuf.size());
        if (compBytes > 0) {
            return SaveChunkBytesToCas(casDir, chunkHash, compBuf.data(), compBytes);
        }
    }
    // 0-byte empty file or explicit raw fallback
    return SaveChunkBytesToCas(casDir, chunkHash, rawSlice, sliceLen);
}

static bool IngestFileStreaming(
    const fs::path& filePath,
    const fs::path& casDir,
    struct libdeflate_compressor* compressor,
    std::vector<uint8_t>& readBuf,
    std::vector<uint8_t>& compBuf,
    CasIngestSummary& outSummary)
{
    FILE* fp = OpenBinaryFileRead(filePath);
    if (!fp) {
        std::fprintf(stderr,
            "[ERROR] Could not open input file '%s' for CAS ingest | Diagnose/Fix: Check file path and read permissions\n",
            filePath.string().c_str());
        return false;
    }

    if (readBuf.size() < CAS_CHUNK_SIZE) {
        readBuf.resize(CAS_CHUNK_SIZE);
    }

    size_t firstRead = std::fread(readBuf.data(), 1, CAS_CHUNK_SIZE, fp);
    if (std::ferror(fp)) {
        std::fclose(fp);
        std::fprintf(stderr,
            "[ERROR] I/O read failure while ingesting '%s' | Diagnose/Fix: Check disk health and file locks\n",
            filePath.string().c_str());
        return false;
    }

    if (firstRead < CAS_CHUNK_SIZE) {
        // Hybrid 1-to-1 fast path (< 1 MB, including 0-byte files)
        std::fclose(fp);
        outSummary.total_size = firstRead;
        outSummary.blob_hash = KongorSha256::HashBuffer(readBuf.data(), firstRead);
        outSummary.crc32 = (firstRead > 0) ? libdeflate_crc32(0, readBuf.data(), firstRead) : 0u;
        outSummary.chunk_count = 1;

        if (outSummary.blob_hash.empty()) {
            std::fprintf(stderr,
                "[ERROR] SHA-256 computation failed on '%s' | Diagnose/Fix: Run native CTest suite 'ctest --test-dir native/build --output-on-failure'\n",
                filePath.string().c_str());
            return false;
        }

        return CompressAndPersistSlice(
            casDir, outSummary.blob_hash, readBuf.data(), firstRead, compressor, compBuf);
    }

    // Fixed 1 MB streaming slicer (>= 1 MB: bounded ~2 MB RSS even on >1 GB archives)
    KongorSha256 fullHasher;
    uint32_t fullCrc = 0;
    uint64_t totalBytes = 0;
    uint32_t chunkCount = 0;

    size_t bytesInBuf = firstRead;
    while (bytesInBuf > 0) {
        if (!fullHasher.Update(readBuf.data(), bytesInBuf)) {
            std::fclose(fp);
            std::fprintf(stderr,
                "[ERROR] Streaming SHA-256 update failed on '%s' | Diagnose/Fix: Run 'ctest --test-dir native/build --output-on-failure'\n",
                filePath.string().c_str());
            return false;
        }
        fullCrc = libdeflate_crc32(fullCrc, readBuf.data(), bytesInBuf);
        totalBytes += bytesInBuf;

        std::string sliceHash = KongorSha256::HashBuffer(readBuf.data(), bytesInBuf);
        if (sliceHash.empty() || !CompressAndPersistSlice(casDir, sliceHash, readBuf.data(), bytesInBuf, compressor, compBuf)) {
            std::fclose(fp);
            return false;
        }
        ++chunkCount;

        bytesInBuf = std::fread(readBuf.data(), 1, CAS_CHUNK_SIZE, fp);
        if (std::ferror(fp)) {
            std::fclose(fp);
            std::fprintf(stderr,
                "[ERROR] I/O read failure at offset %llu in '%s' | Diagnose/Fix: Check disk health and file locks\n",
                static_cast<unsigned long long>(totalBytes), filePath.string().c_str());
            return false;
        }
    }

    std::fclose(fp);
    outSummary.blob_hash = fullHasher.FinalizeHex();
    outSummary.total_size = totalBytes;
    outSummary.crc32 = fullCrc;
    outSummary.chunk_count = chunkCount;
    return !outSummary.blob_hash.empty();
}

// ============================================================================
// DETERMINISTIC MERKLE TREE ROOT CALCULATION (100% Parity with cas.py)
// ============================================================================
struct ManifestEntry {
    std::string vpath;
    std::string vpath_norm;
    std::string blob_hash;
    std::string canonical_archive;
    uint32_t crc32 = 0;
    uint64_t size_bytes = 0;
};

static std::string ComputeMerkleRoot(std::vector<ManifestEntry>& entries) {
    for (auto& e : entries) {
        e.vpath_norm = NormalizeMerkleVpath(e.vpath);
        e.blob_hash = AsciiLower(e.blob_hash);
    }

    // Sort lexicographically by normalized forward-slash lowercase vpath (matching tools/assetdb/cas.py)
    std::sort(entries.begin(), entries.end(), [](const ManifestEntry& a, const ManifestEntry& b) {
        if (a.vpath_norm != b.vpath_norm) {
            return a.vpath_norm < b.vpath_norm;
        }
        return a.blob_hash < b.blob_hash;
    });

    KongorSha256 merkleHasher;
    for (const auto& entry : entries) {
        std::string line = entry.vpath_norm + ":" + entry.blob_hash + "\n";
        merkleHasher.Update(line.data(), line.size());
    }

    return merkleHasher.FinalizeHex();
}

// ============================================================================
// SUB-COMMAND: HASH BENCHMARK
// ============================================================================
static int CmdHash(int argc, char* argv[]) {
    if (argc != 3) {
        std::fprintf(stderr,
            "[ERROR] Invalid arguments for 'hash' | Diagnose/Fix: Run 'kongor-cas hash <file_path>'\n");
        return 1;
    }

    fs::path filePath = fs::u8path(argv[2]);
    auto tStart = std::chrono::high_resolution_clock::now();

    FILE* fp = OpenBinaryFileRead(filePath);
    if (!fp) {
        std::fprintf(stderr,
            "[ERROR] Could not open file '%s' | Diagnose/Fix: Verify path exists and run 'kongor-cas hash <file_path>'\n",
            argv[2]);
        return 1;
    }

    std::error_code ec;
    uint64_t fileSize = fs::file_size(filePath, ec);
    if (ec) {
        std::fclose(fp);
        std::fprintf(stderr,
            "[ERROR] Failed to stat file size for '%s': %s | Diagnose/Fix: Ensure target is a regular readable file\n",
            argv[2], ec.message().c_str());
        return 1;
    }

    static constexpr uint64_t kMaxInMemoryBenchBytes = 64ULL * 1024ULL * 1024ULL;
    std::string sha256;
    uint32_t crc32 = 0;
    double readMs = 0.0;
    double hashMs = 0.0;

    if (fileSize <= kMaxInMemoryBenchBytes) {
        std::vector<uint8_t> buffer(static_cast<size_t>(fileSize));
        size_t got = (fileSize > 0) ? std::fread(buffer.data(), 1, static_cast<size_t>(fileSize), fp) : 0;
        bool ioErr = std::ferror(fp) != 0;
        std::fclose(fp);

        if (ioErr || got != static_cast<size_t>(fileSize)) {
            std::fprintf(stderr,
                "[ERROR] Short read on '%s' (%zu/%llu bytes) | Diagnose/Fix: Check file locks and retry\n",
                argv[2], got, static_cast<unsigned long long>(fileSize));
            return 1;
        }

        auto tRead = std::chrono::high_resolution_clock::now();
        sha256 = KongorSha256::HashBuffer(buffer.data(), buffer.size());
        crc32 = buffer.empty() ? 0u : libdeflate_crc32(0, buffer.data(), buffer.size());
        auto tDone = std::chrono::high_resolution_clock::now();

        readMs = std::chrono::duration<double, std::milli>(tRead - tStart).count();
        hashMs = std::chrono::duration<double, std::milli>(tDone - tRead).count();
    } else {
        // Streaming benchmark for large archives (>64 MB) without multi-GB heap spike
        std::vector<uint8_t> chunkBuf(4 * 1024 * 1024);
        KongorSha256 streamHasher;
        while (true) {
            auto r0 = std::chrono::high_resolution_clock::now();
            size_t got = std::fread(chunkBuf.data(), 1, chunkBuf.size(), fp);
            auto r1 = std::chrono::high_resolution_clock::now();
            readMs += std::chrono::duration<double, std::milli>(r1 - r0).count();

            if (std::ferror(fp)) {
                std::fclose(fp);
                std::fprintf(stderr,
                    "[ERROR] Streaming read failed on '%s' | Diagnose/Fix: Check disk health\n",
                    argv[2]);
                return 1;
            }
            if (got == 0) break;

            auto h0 = std::chrono::high_resolution_clock::now();
            streamHasher.Update(chunkBuf.data(), got);
            crc32 = libdeflate_crc32(crc32, chunkBuf.data(), got);
            auto h1 = std::chrono::high_resolution_clock::now();
            hashMs += std::chrono::duration<double, std::milli>(h1 - h0).count();
        }
        std::fclose(fp);
        auto h0 = std::chrono::high_resolution_clock::now();
        sha256 = streamHasher.FinalizeHex();
        auto h1 = std::chrono::high_resolution_clock::now();
        hashMs += std::chrono::duration<double, std::milli>(h1 - h0).count();
    }

    auto tEnd = std::chrono::high_resolution_clock::now();
    double totalMs = std::chrono::duration<double, std::milli>(tEnd - tStart).count();
    double sizeMb = static_cast<double>(fileSize) / (1024.0 * 1024.0);
    double speedMBs = (hashMs > 0.0001) ? (sizeMb / (hashMs / 1000.0)) : 0.0;

    std::printf("=================================================================\n");
    std::printf(" [KONGOR CAS] DUAL-HASH BENCHMARK: %s\n", argv[2]);
    std::printf("=================================================================\n");
    std::printf(" Size       : %llu bytes (%.2f MB)\n", static_cast<unsigned long long>(fileSize), sizeMb);
    std::printf(" SHA-256    : %s\n", sha256.c_str());
    std::printf(" CRC32      : %08X\n", crc32);
    std::printf(" Hash Time  : %.3f ms (Throughput: %.2f MB/s)\n", hashMs, speedMBs);
    std::printf(" Total Time : %.3f ms (including disk read: %.3f ms)\n", totalMs, readMs);
    std::printf("=================================================================\n");

    return 0;
}

// ============================================================================
// SUB-COMMAND: INGEST (Single file or directory tree into CAS)
// ============================================================================
static int CmdIngest(int argc, char* argv[]) {
    if (argc < 3) {
        std::fprintf(stderr,
            "[ERROR] Missing input path for 'ingest' | Diagnose/Fix: Run 'kongor-cas ingest <path> [--cas-dir <path>]'\n");
        return 1;
    }

    fs::path inputPath = fs::u8path(argv[2]);
    fs::path casDir = fs::u8path(".cas");
    bool compress = true;

    for (int i = 3; i < argc; ++i) {
        if (std::strcmp(argv[i], "--cas-dir") == 0 && i + 1 < argc) {
            casDir = fs::u8path(argv[++i]);
        } else if (std::strcmp(argv[i], "--no-compress") == 0) {
            compress = false;
        } else {
            std::fprintf(stderr,
                "[ERROR] Unknown or incomplete CLI option '%s' for 'ingest' | Diagnose/Fix: Run 'kongor-cas ingest <path> [--cas-dir <path>] [--no-compress]'\n",
                argv[i]);
            return 1;
        }
    }

    std::error_code ec;
    fs::create_directories(casDir / "objects", ec);
    if (ec && !fs::is_directory(casDir / "objects")) {
        std::fprintf(stderr,
            "[ERROR] Failed to initialize CAS objects directory '%s': %s | Diagnose/Fix: Check filesystem permissions\n",
            (casDir / "objects").string().c_str(), ec.message().c_str());
        return 1;
    }

    LibdeflateCompressorGuard compressor(compress ? 6 : 0);
    if (compress && !compressor.ptr) {
        std::fprintf(stderr,
            "[ERROR] Failed to allocate libdeflate compressor | Diagnose/Fix: Check available system memory\n");
        return 1;
    }

    std::vector<uint8_t> readBuf(CAS_CHUNK_SIZE);
    std::vector<uint8_t> compBuf;
    size_t filesIngested = 0;
    uint64_t bytesIngested = 0;
    auto tStart = std::chrono::high_resolution_clock::now();

    auto ProcessOneFile = [&](const fs::path& p) -> bool {
        CasIngestSummary summary;
        if (!IngestFileStreaming(p, casDir, compress ? compressor.ptr : nullptr, readBuf, compBuf, summary)) {
            return false;
        }
        ++filesIngested;
        bytesIngested += summary.total_size;
        std::printf("  [CAS INGEST] %s -> Blob: %s (Chunks: %u, Size: %llu bytes, CRC: %08X)\n",
            p.string().c_str(),
            summary.blob_hash.c_str(),
            summary.chunk_count,
            static_cast<unsigned long long>(summary.total_size),
            summary.crc32);
        return true;
    };

    if (fs::is_directory(inputPath, ec)) {
        std::printf("[KONGOR CAS] Ingesting directory tree '%s' into '%s' (rfc1950_zlib=%s)...\n",
            inputPath.string().c_str(), casDir.string().c_str(), compress ? "true" : "false");
        for (const auto& entry : fs::recursive_directory_iterator(inputPath, ec)) {
            if (ec) {
                std::fprintf(stderr,
                    "[ERROR] Directory traversal error under '%s': %s | Diagnose/Fix: Check directory permissions\n",
                    inputPath.string().c_str(), ec.message().c_str());
                return 1;
            }
            if (entry.is_regular_file()) {
                if (!ProcessOneFile(entry.path())) return 1;
            }
        }
    } else if (fs::is_regular_file(inputPath, ec)) {
        if (!ProcessOneFile(inputPath)) return 1;
    } else {
        std::fprintf(stderr,
            "[ERROR] Input path does not exist: '%s' | Diagnose/Fix: Pass a valid file or directory to 'kongor-cas ingest'\n",
            inputPath.string().c_str());
        return 1;
    }

    auto tEnd = std::chrono::high_resolution_clock::now();
    double elapsedMs = std::chrono::duration<double, std::milli>(tEnd - tStart).count();
    std::printf("[KONGOR CAS] Ingest complete: %zu file(s), %llu bytes persisted in %.2f ms.\n",
        filesIngested, static_cast<unsigned long long>(bytesIngested), elapsedMs);
    return 0;
}

// ============================================================================
// SUB-COMMAND: MANIFEST (Create immutable Merkle Manifest)
// ============================================================================
static int CmdManifest(int argc, char* argv[]) {
    if (argc < 4) {
        std::fprintf(stderr,
            "[ERROR] Missing arguments for 'manifest' | Diagnose/Fix: Run 'kongor-cas manifest <version_tag> <input_dir> [--output <file.json>] [--cas-dir <path>]'\n");
        return 1;
    }

    std::string versionTag = argv[2];
    fs::path inputDir = fs::u8path(argv[3]);
    fs::path outputFile = fs::u8path("manifest_" + versionTag + ".json");
    fs::path casDir = fs::u8path(".cas");

    for (int i = 4; i < argc; ++i) {
        if (std::strcmp(argv[i], "--output") == 0 && i + 1 < argc) {
            outputFile = fs::u8path(argv[++i]);
        } else if (std::strcmp(argv[i], "--cas-dir") == 0 && i + 1 < argc) {
            casDir = fs::u8path(argv[++i]);
        } else {
            std::fprintf(stderr,
                "[ERROR] Unknown or incomplete CLI option '%s' for 'manifest' | Diagnose/Fix: Run 'kongor-cas manifest <version_tag> <input_dir> [--output <file.json>] [--cas-dir <path>]'\n",
                argv[i]);
            return 1;
        }
    }

    std::error_code ec;
    if (!fs::is_directory(inputDir, ec)) {
        std::fprintf(stderr,
            "[ERROR] Input directory not found: '%s' | Diagnose/Fix: Verify <input_dir> exists before running 'kongor-cas manifest'\n",
            inputDir.string().c_str());
        return 1;
    }

    fs::create_directories(casDir / "objects", ec);
    if (ec && !fs::is_directory(casDir / "objects")) {
        std::fprintf(stderr,
            "[ERROR] Failed to initialize CAS directory '%s': %s | Diagnose/Fix: Check directory write permissions\n",
            casDir.string().c_str(), ec.message().c_str());
        return 1;
    }

    std::printf("[KONGOR CAS] Building Merkle Manifest for '%s' from '%s' (CAS: '%s')...\n",
        versionTag.c_str(), inputDir.string().c_str(), casDir.string().c_str());

    LibdeflateCompressorGuard compressor(6);
    if (!compressor.ptr) {
        std::fprintf(stderr,
            "[ERROR] Failed to allocate libdeflate compressor | Diagnose/Fix: Check available memory\n");
        return 1;
    }

    std::vector<uint8_t> readBuf(CAS_CHUNK_SIZE);
    std::vector<uint8_t> compBuf;
    std::vector<ManifestEntry> entries;
    uint64_t totalBytes = 0;
    auto tStart = std::chrono::high_resolution_clock::now();

    for (const auto& entry : fs::recursive_directory_iterator(inputDir, ec)) {
        if (ec) {
            std::fprintf(stderr,
                "[ERROR] Directory scan failed in '%s': %s | Diagnose/Fix: Check read permissions\n",
                inputDir.string().c_str(), ec.message().c_str());
            return 1;
        }
        if (!entry.is_regular_file()) continue;

        fs::path rel = fs::relative(entry.path(), inputDir, ec);
        if (ec) {
            std::fprintf(stderr,
                "[ERROR] Failed computing relative vpath for '%s': %s | Diagnose/Fix: Ensure symlinks do not escape input root\n",
                PathDisplayUtf8(entry.path()).c_str(), ec.message().c_str());
            return 1;
        }
        std::string vpath = PathGenericUtf8(rel);
        if (!IsPrintableAsciiVpath(vpath)) {
            std::fprintf(stderr,
                "[ERROR] Non-ASCII or control characters in asset vpath '%s' | Diagnose/Fix: Rename asset under '%s' to printable ASCII (0x20..0x7E) to preserve K2 .s2z and cross-OS Merkle parity\n",
                vpath.c_str(), PathDisplayUtf8(inputDir).c_str());
            return 1;
        }

        CasIngestSummary summary;
        if (!IngestFileStreaming(entry.path(), casDir, compressor.ptr, readBuf, compBuf, summary)) {
            return 1;
        }

        ManifestEntry me;
        me.vpath = vpath;
        me.blob_hash = summary.blob_hash;
        me.canonical_archive = "resources00.s2z"; // Default; packager partitions later
        me.crc32 = summary.crc32;
        me.size_bytes = summary.total_size;

        entries.push_back(std::move(me));
        totalBytes += summary.total_size;
    }

    std::string merkleRoot = ComputeMerkleRoot(entries);
    auto tEnd = std::chrono::high_resolution_clock::now();
    double totalMs = std::chrono::duration<double, std::milli>(tEnd - tStart).count();

    if (outputFile.has_parent_path()) {
        fs::create_directories(outputFile.parent_path(), ec);
    }

    std::ofstream out(outputFile, std::ios::binary | std::ios::trunc);
    if (!out.is_open()) {
        std::fprintf(stderr,
            "[ERROR] Could not open manifest output file '%s' for writing | Diagnose/Fix: Check path and permissions\n",
            outputFile.string().c_str());
        return 1;
    }

    out << "{\n";
    out << "  \"version_tag\": \"" << EscapeJsonString(versionTag) << "\",\n";
    out << "  \"merkle_root\": \"" << EscapeJsonString(merkleRoot) << "\",\n";
    out << "  \"total_files\": " << entries.size() << ",\n";
    out << "  \"total_bytes\": " << totalBytes << ",\n";
    out << "  \"entries\": [\n";
    for (size_t i = 0; i < entries.size(); ++i) {
        const auto& e = entries[i];
        out << "    {\"vpath\": \"" << EscapeJsonString(e.vpath)
            << "\", \"vpath_norm\": \"" << EscapeJsonString(e.vpath_norm)
            << "\", \"blob_hash\": \"" << EscapeJsonString(e.blob_hash)
            << "\", \"crc32\": " << e.crc32
            << ", \"size\": " << e.size_bytes << "}"
            << (i + 1 < entries.size() ? "," : "") << "\n";
    }
    out << "  ]\n";
    out << "}\n";
    out.flush();

    if (!out.good()) {
        std::fprintf(stderr,
            "[ERROR] Failed flushing manifest JSON to '%s' | Diagnose/Fix: Check available disk space\n",
            outputFile.string().c_str());
        return 1;
    }

    std::printf("=================================================================\n");
    std::printf(" [KONGOR CAS] MERKLE MANIFEST GENERATED: %s\n", outputFile.string().c_str());
    std::printf("=================================================================\n");
    std::printf(" Version Tag : %s\n", versionTag.c_str());
    std::printf(" Merkle Root : %s\n", merkleRoot.c_str());
    std::printf(" Total Files : %zu\n", entries.size());
    std::printf(" Total Bytes : %llu bytes (%.2f MB)\n",
        static_cast<unsigned long long>(totalBytes), static_cast<double>(totalBytes) / (1024.0 * 1024.0));
    std::printf(" Build Time  : %.2f ms\n", totalMs);
    std::printf("=================================================================\n");

    return 0;
}

// ============================================================================
// SUB-COMMAND: VERIFY (Audit CAS store chunk decompression & SHA-256 integrity)
// ============================================================================
static int CmdVerify(int argc, char* argv[]) {
    fs::path casDir = fs::u8path(".cas");
    if (argc >= 3) {
        casDir = fs::u8path(argv[2]);
    }
    if (argc > 3) {
        std::fprintf(stderr,
            "[ERROR] Too many arguments for 'verify' | Diagnose/Fix: Run 'kongor-cas verify [<cas_dir>]'\n");
        return 1;
    }

    fs::path objectsDir = casDir / "objects";
    std::error_code ec;
    if (!fs::is_directory(objectsDir, ec)) {
        std::fprintf(stderr,
            "[ERROR] CAS objects directory not found: '%s' | Diagnose/Fix: Run 'kongor-cas ingest <path> --cas-dir \"%s\"' first\n",
            objectsDir.string().c_str(), casDir.string().c_str());
        return 1;
    }

    LibdeflateDecompressorGuard decomp;
    if (!decomp.ptr) {
        std::fprintf(stderr,
            "[ERROR] Failed to allocate libdeflate decompressor | Diagnose/Fix: Check available memory\n");
        return 1;
    }

    std::vector<uint8_t> compData;
    std::vector<uint8_t> rawBuf(CAS_CHUNK_SIZE);
    size_t verifiedChunks = 0;
    size_t corruptedChunks = 0;
    uint64_t totalUncompressedBytes = 0;
    auto tStart = std::chrono::high_resolution_clock::now();

    for (const auto& entry : fs::recursive_directory_iterator(objectsDir, ec)) {
        if (ec) {
            std::fprintf(stderr,
                "[ERROR] Failed iterating CAS objects under '%s': %s | Diagnose/Fix: Check filesystem permissions\n",
                objectsDir.string().c_str(), ec.message().c_str());
            return 1;
        }
        if (!entry.is_regular_file()) continue;

        std::string fileName = PathDisplayUtf8(entry.path().filename());
        if (fileName.find(".tmp.") != std::string::npos) continue;

        std::string bucketName = PathDisplayUtf8(entry.path().parent_path().filename());
        std::string expectedHash = AsciiLower(bucketName + fileName);
        if (!IsValidHex64(expectedHash)) {
            std::fprintf(stderr,
                "[CORRUPT CAS ENTRY] Unexpected non-SHA256 filename '%s' | Diagnose/Fix: Remove stray file from '%s'\n",
                PathDisplayUtf8(entry.path()).c_str(), PathDisplayUtf8(objectsDir).c_str());
            ++corruptedChunks;
            continue;
        }

        uint64_t fsz = fs::file_size(entry.path(), ec);
        FILE* fp = OpenBinaryFileRead(entry.path());
        if (ec || !fp) {
            if (fp) std::fclose(fp);
            std::fprintf(stderr,
                "[CORRUPT CAS ENTRY] Cannot read '%s' | Diagnose/Fix: Re-ingest missing/unreadable chunk %s\n",
                entry.path().string().c_str(), expectedHash.c_str());
            ++corruptedChunks;
            continue;
        }

        compData.resize(static_cast<size_t>(fsz));
        size_t got = (fsz > 0) ? std::fread(compData.data(), 1, static_cast<size_t>(fsz), fp) : 0;
        bool ioErr = std::ferror(fp) != 0;
        std::fclose(fp);

        if (ioErr || got != static_cast<size_t>(fsz)) {
            std::fprintf(stderr,
                "[CORRUPT CAS ENTRY] Short read on '%s' | Diagnose/Fix: Re-ingest chunk %s\n",
                entry.path().string().c_str(), expectedHash.c_str());
            ++corruptedChunks;
            continue;
        }

        std::string actualHash;
        size_t uncompressedLen = 0;

        if (compData.empty()) {
            actualHash = KongorSha256::HashBuffer(nullptr, 0);
            uncompressedLen = 0;
        } else {
            size_t actualOut = 0;
            enum libdeflate_result dres = libdeflate_zlib_decompress(
                decomp.ptr, compData.data(), compData.size(), rawBuf.data(), rawBuf.size(), &actualOut);
            while (dres == LIBDEFLATE_INSUFFICIENT_SPACE && rawBuf.size() < (64ULL * 1024ULL * 1024ULL)) {
                rawBuf.resize(rawBuf.size() * 2);
                dres = libdeflate_zlib_decompress(
                    decomp.ptr, compData.data(), compData.size(), rawBuf.data(), rawBuf.size(), &actualOut);
            }

            if (dres == LIBDEFLATE_SUCCESS) {
                actualHash = KongorSha256::HashBuffer(rawBuf.data(), actualOut);
                uncompressedLen = actualOut;
            } else {
                // Fallback: verify whether chunk was stored uncompressed (--no-compress)
                actualHash = KongorSha256::HashBuffer(compData.data(), compData.size());
                uncompressedLen = compData.size();
            }
        }

        if (actualHash != expectedHash) {
            std::fprintf(stderr,
                "[CORRUPT CAS CHUNK] SHA-256 mismatch at '%s': expected %s, got %s | Diagnose/Fix: Delete corrupted file and re-run 'kongor-cas ingest'\n",
                entry.path().string().c_str(), expectedHash.c_str(), actualHash.c_str());
            ++corruptedChunks;
        } else {
            ++verifiedChunks;
            totalUncompressedBytes += uncompressedLen;
        }
    }

    auto tEnd = std::chrono::high_resolution_clock::now();
    double elapsedMs = std::chrono::duration<double, std::milli>(tEnd - tStart).count();

    if (corruptedChunks > 0) {
        std::fprintf(stderr,
            "[CAS VERIFY FAILED] %zu corrupted chunk(s) detected (%zu valid) in %.2f ms | Diagnose/Fix: Re-ingest source assets into '%s'\n",
            corruptedChunks, verifiedChunks, elapsedMs, casDir.string().c_str());
        return 1;
    }

    std::printf("[KONGOR CAS] VERIFY PASS: %zu chunk(s) (%llu uncompressed bytes) verified in %.2f ms.\n",
        verifiedChunks, static_cast<unsigned long long>(totalUncompressedBytes), elapsedMs);
    return 0;
}

// ============================================================================
// MAIN CLI DISPATCHER
// ============================================================================
static void PrintUsage() {
    std::printf("Project Kongor Cross-Platform Content-Addressed Storage Engine (kongor-cas)\n");
    std::printf("Usage:\n");
    std::printf("  kongor-cas hash <file_path>\n");
    std::printf("  kongor-cas ingest <path> [--cas-dir <path>] [--no-compress]\n");
    std::printf("  kongor-cas manifest <version_tag> <input_dir> [--output <file.json>] [--cas-dir <path>]\n");
    std::printf("  kongor-cas verify [<cas_dir>]\n");
}

int main(int argc, char* argv[]) {
    if (argc < 2) {
        PrintUsage();
        return 1;
    }

    const char* subCmd = argv[1];
    if (std::strcmp(subCmd, "--help") == 0 || std::strcmp(subCmd, "-h") == 0) {
        PrintUsage();
        return 0;
    } else if (std::strcmp(subCmd, "hash") == 0) {
        return CmdHash(argc, argv);
    } else if (std::strcmp(subCmd, "ingest") == 0) {
        return CmdIngest(argc, argv);
    } else if (std::strcmp(subCmd, "manifest") == 0) {
        return CmdManifest(argc, argv);
    } else if (std::strcmp(subCmd, "verify") == 0) {
        return CmdVerify(argc, argv);
    } else {
        std::fprintf(stderr,
            "[ERROR] Unknown subcommand '%s' | Diagnose/Fix: Run 'kongor-cas --help' for valid subcommands (hash, ingest, manifest, verify)\n",
            subCmd);
        return 1;
    }
}
