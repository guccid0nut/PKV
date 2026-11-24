// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

/**
 * @file kongor_sha256.h
 * @brief Cross-Platform SHA-256 (FIPS 180-4 / RFC 6234) with Zero-Heap Win32 CNG Fast Path
 *
 * Provides:
 * 1. KongorPortableSha256: Pure C++17, zero-dependency, endian-safe FIPS 180-4 SHA-256
 *    for Linux, macOS, and cross-verification unit tests.
 * 2. KongorSha256: Uses cached Win32 BCrypt (SHA-NI / AVX2) with stack hash-object buffer
 *    and 64-bit >4GB chunked safety on Windows, and KongorPortableSha256 on POSIX.
 */

#ifndef KONGOR_SHA256_H
#define KONGOR_SHA256_H

#include <cstdint>
#include <cstddef>
#include <cstring>
#include <cstdio>
#include <string>

#ifdef _WIN32
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <bcrypt.h>
#endif

class KongorPortableSha256 {
private:
    uint32_t m_state[8];
    uint64_t m_totalBytes;
    uint8_t  m_buffer[64];
    size_t   m_bufferLen;
    bool     m_finalized;

    static inline uint32_t Rotr32(uint32_t x, uint32_t n) {
        return (x >> n) | (x << (32u - n));
    }

    static inline uint32_t LoadBe32(const uint8_t* p) {
        return ((uint32_t)p[0] << 24) |
               ((uint32_t)p[1] << 16) |
               ((uint32_t)p[2] << 8)  |
               ((uint32_t)p[3]);
    }

    static inline void StoreBe32(uint8_t* p, uint32_t v) {
        p[0] = (uint8_t)(v >> 24);
        p[1] = (uint8_t)(v >> 16);
        p[2] = (uint8_t)(v >> 8);
        p[3] = (uint8_t)(v);
    }

    void TransformBlock(const uint8_t block[64]) {
        static constexpr uint32_t K[64] = {
            0x428a2f98u, 0x71374491u, 0xb5c0fbcfu, 0xe9b5dba5u,
            0x3956c25bu, 0x59f111f1u, 0x923f82a4u, 0xab1c5ed5u,
            0xd807aa98u, 0x12835b01u, 0x243185beu, 0x550c7dc3u,
            0x72be5d74u, 0x80deb1feu, 0x9bdc06a7u, 0xc19bf174u,
            0xe49b69c1u, 0xefbe4786u, 0x0fc19dc6u, 0x240ca1ccu,
            0x2de92c6fu, 0x4a7484aau, 0x5cb0a9dcu, 0x76f988dau,
            0x983e5152u, 0xa831c66du, 0xb00327c8u, 0xbf597fc7u,
            0xc6e00bf3u, 0xd5a79147u, 0x06ca6351u, 0x14292967u,
            0x27b70a85u, 0x2e1b2138u, 0x4d2c6dfcu, 0x53380d13u,
            0x650a7354u, 0x766a0abbu, 0x81c2c92eu, 0x92722c85u,
            0xa2bfe8a1u, 0xa81a664bu, 0xc24b8b70u, 0xc76c51a3u,
            0xd192e819u, 0xd6990624u, 0xf40e3585u, 0x106aa070u,
            0x19a4c116u, 0x1e376c08u, 0x2748774cu, 0x34b0bcb5u,
            0x391c0cb3u, 0x4ed8aa4au, 0x5b9cca4fu, 0x682e6ff3u,
            0x748f82eeu, 0x78a5636fu, 0x84c87814u, 0x8cc70208u,
            0x90befffau, 0xa4506cebu, 0xbef9a3f7u, 0xc67178f2u
        };

        uint32_t w[64];
        for (int i = 0; i < 16; ++i) {
            w[i] = LoadBe32(block + (i * 4));
        }
        for (int i = 16; i < 64; ++i) {
            uint32_t s0 = Rotr32(w[i - 15], 7) ^ Rotr32(w[i - 15], 18) ^ (w[i - 15] >> 3);
            uint32_t s1 = Rotr32(w[i - 2], 17) ^ Rotr32(w[i - 2], 19) ^ (w[i - 2] >> 10);
            w[i] = w[i - 16] + s0 + w[i - 7] + s1;
        }

        uint32_t a = m_state[0];
        uint32_t b = m_state[1];
        uint32_t c = m_state[2];
        uint32_t d = m_state[3];
        uint32_t e = m_state[4];
        uint32_t f = m_state[5];
        uint32_t g = m_state[6];
        uint32_t h = m_state[7];

        for (int i = 0; i < 64; ++i) {
            uint32_t S1 = Rotr32(e, 6) ^ Rotr32(e, 11) ^ Rotr32(e, 25);
            uint32_t ch = (e & f) ^ ((~e) & g);
            uint32_t temp1 = h + S1 + ch + K[i] + w[i];
            uint32_t S0 = Rotr32(a, 2) ^ Rotr32(a, 13) ^ Rotr32(a, 22);
            uint32_t maj = (a & b) ^ (a & c) ^ (b & c);
            uint32_t temp2 = S0 + maj;

            h = g;
            g = f;
            f = e;
            e = d + temp1;
            d = c;
            c = b;
            b = a;
            a = temp1 + temp2;
        }

        m_state[0] += a;
        m_state[1] += b;
        m_state[2] += c;
        m_state[3] += d;
        m_state[4] += e;
        m_state[5] += f;
        m_state[6] += g;
        m_state[7] += h;
    }

public:
    KongorPortableSha256() {
        Reset();
    }

    void Reset() {
        m_state[0] = 0x6a09e667u;
        m_state[1] = 0xbb67ae85u;
        m_state[2] = 0x3c6ef372u;
        m_state[3] = 0xa54ff53au;
        m_state[4] = 0x510e527fu;
        m_state[5] = 0x9b05688cu;
        m_state[6] = 0x1f83d9abu;
        m_state[7] = 0x5be0cd19u;
        m_totalBytes = 0;
        m_bufferLen = 0;
        m_finalized = false;
    }

    bool Update(const void* data, size_t size) {
        if (m_finalized) return false;
        if (size == 0 || data == nullptr) return true;

        const uint8_t* bytes = static_cast<const uint8_t*>(data);
        m_totalBytes += static_cast<uint64_t>(size);

        if (m_bufferLen > 0) {
            size_t needed = 64 - m_bufferLen;
            if (size < needed) {
                std::memcpy(m_buffer + m_bufferLen, bytes, size);
                m_bufferLen += size;
                return true;
            }
            std::memcpy(m_buffer + m_bufferLen, bytes, needed);
            TransformBlock(m_buffer);
            bytes += needed;
            size -= needed;
            m_bufferLen = 0;
        }

        while (size >= 64) {
            TransformBlock(bytes);
            bytes += 64;
            size -= 64;
        }

        if (size > 0) {
            std::memcpy(m_buffer, bytes, size);
            m_bufferLen = size;
        }
        return true;
    }

    bool FinalizeBytes(uint8_t outDigest[32]) {
        if (m_finalized) return false;
        m_finalized = true;

        uint64_t totalBits = m_totalBytes * 8ULL;
        m_buffer[m_bufferLen++] = 0x80u;

        if (m_bufferLen > 56) {
            std::memset(m_buffer + m_bufferLen, 0, 64 - m_bufferLen);
            TransformBlock(m_buffer);
            m_bufferLen = 0;
        }

        std::memset(m_buffer + m_bufferLen, 0, 56 - m_bufferLen);
        StoreBe32(m_buffer + 56, static_cast<uint32_t>(totalBits >> 32));
        StoreBe32(m_buffer + 60, static_cast<uint32_t>(totalBits & 0xFFFFFFFFULL));
        TransformBlock(m_buffer);

        for (int i = 0; i < 8; ++i) {
            StoreBe32(outDigest + (i * 4), m_state[i]);
        }
        return true;
    }

    std::string FinalizeHex() {
        uint8_t digest[32];
        if (!FinalizeBytes(digest)) return "";
        static constexpr char kHexDigits[] = "0123456789abcdef";
        char hex[65];
        for (int i = 0; i < 32; ++i) {
            hex[i * 2]     = kHexDigits[(digest[i] >> 4) & 0x0Fu];
            hex[i * 2 + 1] = kHexDigits[digest[i] & 0x0Fu];
        }
        hex[64] = '\0';
        return std::string(hex, 64);
    }

    static std::string HashBuffer(const void* data, size_t size) {
        KongorPortableSha256 sha;
        sha.Update(data, size);
        return sha.FinalizeHex();
    }
};

#ifdef _WIN32
struct KongorBCryptSha256Provider {
    BCRYPT_ALG_HANDLE hAlg = NULL;
    DWORD cbHashObj = 0;

    KongorBCryptSha256Provider() {
        if (BCryptOpenAlgorithmProvider(&hAlg, BCRYPT_SHA256_ALGORITHM, NULL, 0) >= 0) {
            DWORD cbData = 0;
            if (BCryptGetProperty(hAlg, BCRYPT_OBJECT_LENGTH, reinterpret_cast<PUCHAR>(&cbHashObj), sizeof(DWORD), &cbData, 0) < 0) {
                BCryptCloseAlgorithmProvider(hAlg, 0);
                hAlg = NULL;
                cbHashObj = 0;
            }
        }
    }

    ~KongorBCryptSha256Provider() {
        if (hAlg) {
            BCryptCloseAlgorithmProvider(hAlg, 0);
            hAlg = NULL;
        }
    }

    static const KongorBCryptSha256Provider& Instance() {
        static KongorBCryptSha256Provider s_provider;
        return s_provider;
    }
};

class KongorSha256 {
private:
    BCRYPT_HASH_HANDLE m_hHash = NULL;
    alignas(16) uint8_t m_stackObj[512];
    uint8_t* m_heapObj = nullptr;
    KongorPortableSha256 m_fallback;
    bool m_useFallback = false;

public:
    KongorSha256() {
        Reset();
    }

    ~KongorSha256() {
        if (m_hHash) {
            BCryptDestroyHash(m_hHash);
            m_hHash = NULL;
        }
        delete[] m_heapObj;
    }

    KongorSha256(const KongorSha256&) = delete;
    KongorSha256& operator=(const KongorSha256&) = delete;

    void Reset() {
        if (m_hHash) {
            BCryptDestroyHash(m_hHash);
            m_hHash = NULL;
        }
        const auto& prov = KongorBCryptSha256Provider::Instance();
        if (prov.hAlg && prov.cbHashObj > 0) {
            uint8_t* objBuf = m_stackObj;
            if (prov.cbHashObj > sizeof(m_stackObj)) {
                if (!m_heapObj) {
                    m_heapObj = new uint8_t[prov.cbHashObj];
                }
                objBuf = m_heapObj;
            }
            if (BCryptCreateHash(prov.hAlg, &m_hHash, objBuf, prov.cbHashObj, NULL, 0, 0) >= 0) {
                m_useFallback = false;
                return;
            }
        }
        m_useFallback = true;
        m_fallback.Reset();
    }

    bool Update(const void* data, size_t size) {
        if (m_useFallback) {
            return m_fallback.Update(data, size);
        }
        if (!m_hHash) return false;
        if (size == 0 || data == nullptr) return true;

        // Chunk in <= 1 GB slices to prevent 32-bit ULONG truncation on buffers >= 4 GB
        const uint8_t* ptr = static_cast<const uint8_t*>(data);
        static constexpr size_t kMaxBCryptChunk = 1ULL << 30;
        while (size > 0) {
            ULONG chunk = static_cast<ULONG>(size > kMaxBCryptChunk ? kMaxBCryptChunk : size);
            if (BCryptHashData(m_hHash, const_cast<PUCHAR>(ptr), chunk, 0) < 0) {
                return false;
            }
            ptr += chunk;
            size -= chunk;
        }
        return true;
    }

    std::string FinalizeHex() {
        if (m_useFallback) {
            return m_fallback.FinalizeHex();
        }
        if (!m_hHash) return "";
        uint8_t digest[32];
        if (BCryptFinishHash(m_hHash, digest, sizeof(digest), 0) < 0) {
            return "";
        }
        static constexpr char kHexDigits[] = "0123456789abcdef";
        char hex[65];
        for (int i = 0; i < 32; ++i) {
            hex[i * 2]     = kHexDigits[(digest[i] >> 4) & 0x0Fu];
            hex[i * 2 + 1] = kHexDigits[digest[i] & 0x0Fu];
        }
        hex[64] = '\0';
        return std::string(hex, 64);
    }

    static std::string HashBuffer(const void* data, size_t size) {
        KongorSha256 sha;
        if (!sha.Update(data, size)) return "";
        return sha.FinalizeHex();
    }
};
#else
using KongorSha256 = KongorPortableSha256;
#endif

#endif // KONGOR_SHA256_H
