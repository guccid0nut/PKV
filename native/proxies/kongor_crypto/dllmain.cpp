// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ============================================================================
// File: dllmain.cpp
// System: Native C++ Modernization - Modern Cryptography & TLS 1.3 Proxy Shim
// Subsystem: kongor_crypto_x64.dll ABI Proxy Forwarder
// ============================================================================

#include "kongor_crypto.h"
#include <windows.h>
#include <bcrypt.h>
#include <cstdio>
#include <cstring>
#include <string>

HMODULE g_hOriginalCrypto = NULL;

extern "C" {
    FARPROC pfnBN_new = NULL;
    FARPROC pfnBN_hex2bn = NULL;
    FARPROC pfnBN_num_bits = NULL;
    FARPROC pfnBN_bn2bin = NULL;
    FARPROC pfnBN_free = NULL;
    FARPROC pfnBN_bin2bn = NULL;
    FARPROC pfnBN_bn2hex = NULL;
    FARPROC pfnBN_mul = NULL;
    FARPROC pfnBN_CTX_free = NULL;
    FARPROC pfnBN_sub = NULL;
    FARPROC pfnBN_CTX_new = NULL;
    FARPROC pfnBN_add = NULL;
    FARPROC pfnBN_div = NULL;
    FARPROC pfnBN_rand = NULL;
    FARPROC pfnBN_is_zero = NULL;
    FARPROC pfnBN_mod_exp = NULL;
    FARPROC pfnSHA1 = NULL;
    FARPROC pfnSHA1_Init = NULL;
    FARPROC pfnSHA1_Update = NULL;
    FARPROC pfnSHA1_Final = NULL;
    FARPROC pfnSHA224 = NULL;
    FARPROC pfnSHA224_Init = NULL;
    FARPROC pfnSHA224_Update = NULL;
    FARPROC pfnSHA224_Final = NULL;
    FARPROC pfnSHA256 = NULL;
    FARPROC pfnSHA256_Init = NULL;
    FARPROC pfnSHA256_Update = NULL;
    FARPROC pfnSHA256_Final = NULL;
    FARPROC pfnSHA384 = NULL;
    FARPROC pfnSHA384_Init = NULL;
    FARPROC pfnSHA384_Update = NULL;
    FARPROC pfnSHA384_Final = NULL;
    FARPROC pfnSHA512 = NULL;
    FARPROC pfnSHA512_Init = NULL;
    FARPROC pfnSHA512_Update = NULL;
    FARPROC pfnSHA512_Final = NULL;
    FARPROC pfnCRYPTO_free = NULL;
    FARPROC pfnRAND_seed = NULL;
}

static void InitializeCryptoProxies(HMODULE hMod) {
    if (!hMod) return;

#define BIND_CRYPTO_FN(name) pfn##name = GetProcAddress(hMod, #name)
    BIND_CRYPTO_FN(BN_new);
    BIND_CRYPTO_FN(BN_hex2bn);
    BIND_CRYPTO_FN(BN_num_bits);
    BIND_CRYPTO_FN(BN_bn2bin);
    BIND_CRYPTO_FN(BN_free);
    BIND_CRYPTO_FN(BN_bin2bn);
    BIND_CRYPTO_FN(BN_bn2hex);
    BIND_CRYPTO_FN(BN_mul);
    BIND_CRYPTO_FN(BN_CTX_free);
    BIND_CRYPTO_FN(BN_sub);
    BIND_CRYPTO_FN(BN_CTX_new);
    BIND_CRYPTO_FN(BN_add);
    BIND_CRYPTO_FN(BN_div);
    BIND_CRYPTO_FN(BN_rand);
    BIND_CRYPTO_FN(BN_is_zero);
    BIND_CRYPTO_FN(BN_mod_exp);
    BIND_CRYPTO_FN(SHA1);
    BIND_CRYPTO_FN(SHA1_Init);
    BIND_CRYPTO_FN(SHA1_Update);
    BIND_CRYPTO_FN(SHA1_Final);
    BIND_CRYPTO_FN(SHA224);
    BIND_CRYPTO_FN(SHA224_Init);
    BIND_CRYPTO_FN(SHA224_Update);
    BIND_CRYPTO_FN(SHA224_Final);
    BIND_CRYPTO_FN(SHA256);
    BIND_CRYPTO_FN(SHA256_Init);
    BIND_CRYPTO_FN(SHA256_Update);
    BIND_CRYPTO_FN(SHA256_Final);
    BIND_CRYPTO_FN(SHA384);
    BIND_CRYPTO_FN(SHA384_Init);
    BIND_CRYPTO_FN(SHA384_Update);
    BIND_CRYPTO_FN(SHA384_Final);
    BIND_CRYPTO_FN(SHA512);
    BIND_CRYPTO_FN(SHA512_Init);
    BIND_CRYPTO_FN(SHA512_Update);
    BIND_CRYPTO_FN(SHA512_Final);
    BIND_CRYPTO_FN(CRYPTO_free);
    BIND_CRYPTO_FN(RAND_seed);
#undef BIND_CRYPTO_FN
}

extern "C" {
    void* Proxy_BN_new() { return pfnBN_new ? reinterpret_cast<void*(*)()>(pfnBN_new)() : nullptr; }
    int   Proxy_BN_hex2bn(void** a, const char* str) { return pfnBN_hex2bn ? reinterpret_cast<int(*)(void**,const char*)>(pfnBN_hex2bn)(a, str) : 0; }
    int   Proxy_BN_num_bits(const void* a) { return pfnBN_num_bits ? reinterpret_cast<int(*)(const void*)>(pfnBN_num_bits)(a) : 0; }
    int   Proxy_BN_bn2bin(const void* a, unsigned char* to) { return pfnBN_bn2bin ? reinterpret_cast<int(*)(const void*,unsigned char*)>(pfnBN_bn2bin)(a, to) : 0; }
    void  Proxy_BN_free(void* a) { if (pfnBN_free) reinterpret_cast<void(*)(void*)>(pfnBN_free)(a); }
    void* Proxy_BN_bin2bn(const unsigned char* s, int len, void* ret) { return pfnBN_bin2bn ? reinterpret_cast<void*(*)(const unsigned char*,int,void*)>(pfnBN_bin2bn)(s, len, ret) : nullptr; }
    char* Proxy_BN_bn2hex(const void* a) { return pfnBN_bn2hex ? reinterpret_cast<char*(*)(const void*)>(pfnBN_bn2hex)(a) : nullptr; }
    int   Proxy_BN_mul(void* r, const void* a, const void* b, void* ctx) { return pfnBN_mul ? reinterpret_cast<int(*)(void*,const void*,const void*,void*)>(pfnBN_mul)(r, a, b, ctx) : 0; }
    void  Proxy_BN_CTX_free(void* c) { if (pfnBN_CTX_free) reinterpret_cast<void(*)(void*)>(pfnBN_CTX_free)(c); }
    int   Proxy_BN_sub(void* r, const void* a, const void* b) { return pfnBN_sub ? reinterpret_cast<int(*)(void*,const void*,const void*)>(pfnBN_sub)(r, a, b) : 0; }
    void* Proxy_BN_CTX_new() { return pfnBN_CTX_new ? reinterpret_cast<void*(*)()>(pfnBN_CTX_new)() : nullptr; }
    int   Proxy_BN_add(void* r, const void* a, const void* b) { return pfnBN_add ? reinterpret_cast<int(*)(void*,const void*,const void*)>(pfnBN_add)(r, a, b) : 0; }
    int   Proxy_BN_div(void* dv, void* rem, const void* m, const void* d, void* ctx) { return pfnBN_div ? reinterpret_cast<int(*)(void*,void*,const void*,const void*,void*)>(pfnBN_div)(dv, rem, m, d, ctx) : 0; }
    int   Proxy_BN_rand(void* rnd, int bits, int top, int bottom) { return pfnBN_rand ? reinterpret_cast<int(*)(void*,int,int,int)>(pfnBN_rand)(rnd, bits, top, bottom) : 0; }
    int   Proxy_BN_is_zero(const void* a) { return pfnBN_is_zero ? reinterpret_cast<int(*)(const void*)>(pfnBN_is_zero)(a) : 1; }
    int   Proxy_BN_mod_exp(void* r, const void* a, const void* p, const void* m, void* ctx) { return pfnBN_mod_exp ? reinterpret_cast<int(*)(void*,const void*,const void*,const void*,void*)>(pfnBN_mod_exp)(r, a, p, m, ctx) : 0; }

    // [L2-P1-01] Fail-closed SHA digest forwarders: when real libcrypto is missing, zero output buffer and return nullptr / 0.
    unsigned char* Proxy_SHA1(const unsigned char* d, size_t n, unsigned char* md) {
        if (pfnSHA1) return reinterpret_cast<unsigned char*(*)(const unsigned char*,size_t,unsigned char*)>(pfnSHA1)(d, n, md);
        if (md) SecureZeroMemory(md, 20);
        OutputDebugStringA("[KONGOR CRYPTO GUARD] FAIL-CLOSED: SHA1 invoked without libcrypto backend!\n");
        return nullptr;
    }
    int Proxy_SHA1_Init(void* c) { return (c && pfnSHA1_Init) ? reinterpret_cast<int(*)(void*)>(pfnSHA1_Init)(c) : 0; }
    int Proxy_SHA1_Update(void* c, const void* data, size_t len) { return (c && pfnSHA1_Update) ? reinterpret_cast<int(*)(void*,const void*,size_t)>(pfnSHA1_Update)(c, data, len) : 0; }
    int Proxy_SHA1_Final(unsigned char* md, void* c) {
        if (c && pfnSHA1_Final) return reinterpret_cast<int(*)(unsigned char*,void*)>(pfnSHA1_Final)(md, c);
        if (md) SecureZeroMemory(md, 20);
        return 0;
    }

    unsigned char* Proxy_SHA224(const unsigned char* d, size_t n, unsigned char* md) {
        if (pfnSHA224) return reinterpret_cast<unsigned char*(*)(const unsigned char*,size_t,unsigned char*)>(pfnSHA224)(d, n, md);
        if (md) SecureZeroMemory(md, 28);
        OutputDebugStringA("[KONGOR CRYPTO GUARD] FAIL-CLOSED: SHA224 invoked without libcrypto backend!\n");
        return nullptr;
    }
    int Proxy_SHA224_Init(void* c) { return (c && pfnSHA224_Init) ? reinterpret_cast<int(*)(void*)>(pfnSHA224_Init)(c) : 0; }
    int Proxy_SHA224_Update(void* c, const void* data, size_t len) { return (c && pfnSHA224_Update) ? reinterpret_cast<int(*)(void*,const void*,size_t)>(pfnSHA224_Update)(c, data, len) : 0; }
    int Proxy_SHA224_Final(unsigned char* md, void* c) {
        if (c && pfnSHA224_Final) return reinterpret_cast<int(*)(unsigned char*,void*)>(pfnSHA224_Final)(md, c);
        if (md) SecureZeroMemory(md, 28);
        return 0;
    }

    unsigned char* Proxy_SHA256(const unsigned char* d, size_t n, unsigned char* md) {
        if (pfnSHA256) return reinterpret_cast<unsigned char*(*)(const unsigned char*,size_t,unsigned char*)>(pfnSHA256)(d, n, md);
        if (md) SecureZeroMemory(md, 32);
        OutputDebugStringA("[KONGOR CRYPTO GUARD] FAIL-CLOSED: SHA256 invoked without libcrypto backend!\n");
        return nullptr;
    }
    int Proxy_SHA256_Init(void* c) { return (c && pfnSHA256_Init) ? reinterpret_cast<int(*)(void*)>(pfnSHA256_Init)(c) : 0; }
    int Proxy_SHA256_Update(void* c, const void* data, size_t len) { return (c && pfnSHA256_Update) ? reinterpret_cast<int(*)(void*,const void*,size_t)>(pfnSHA256_Update)(c, data, len) : 0; }
    int Proxy_SHA256_Final(unsigned char* md, void* c) {
        if (c && pfnSHA256_Final) return reinterpret_cast<int(*)(unsigned char*,void*)>(pfnSHA256_Final)(md, c);
        if (md) SecureZeroMemory(md, 32);
        return 0;
    }

    unsigned char* Proxy_SHA384(const unsigned char* d, size_t n, unsigned char* md) {
        if (pfnSHA384) return reinterpret_cast<unsigned char*(*)(const unsigned char*,size_t,unsigned char*)>(pfnSHA384)(d, n, md);
        if (md) SecureZeroMemory(md, 48);
        OutputDebugStringA("[KONGOR CRYPTO GUARD] FAIL-CLOSED: SHA384 invoked without libcrypto backend!\n");
        return nullptr;
    }
    int Proxy_SHA384_Init(void* c) { return (c && pfnSHA384_Init) ? reinterpret_cast<int(*)(void*)>(pfnSHA384_Init)(c) : 0; }
    int Proxy_SHA384_Update(void* c, const void* data, size_t len) { return (c && pfnSHA384_Update) ? reinterpret_cast<int(*)(void*,const void*,size_t)>(pfnSHA384_Update)(c, data, len) : 0; }
    int Proxy_SHA384_Final(unsigned char* md, void* c) {
        if (c && pfnSHA384_Final) return reinterpret_cast<int(*)(unsigned char*,void*)>(pfnSHA384_Final)(md, c);
        if (md) SecureZeroMemory(md, 48);
        return 0;
    }

    unsigned char* Proxy_SHA512(const unsigned char* d, size_t n, unsigned char* md) {
        if (pfnSHA512) return reinterpret_cast<unsigned char*(*)(const unsigned char*,size_t,unsigned char*)>(pfnSHA512)(d, n, md);
        if (md) SecureZeroMemory(md, 64);
        OutputDebugStringA("[KONGOR CRYPTO GUARD] FAIL-CLOSED: SHA512 invoked without libcrypto backend!\n");
        return nullptr;
    }
    int Proxy_SHA512_Init(void* c) { return (c && pfnSHA512_Init) ? reinterpret_cast<int(*)(void*)>(pfnSHA512_Init)(c) : 0; }
    int Proxy_SHA512_Update(void* c, const void* data, size_t len) { return (c && pfnSHA512_Update) ? reinterpret_cast<int(*)(void*,const void*,size_t)>(pfnSHA512_Update)(c, data, len) : 0; }
    int Proxy_SHA512_Final(unsigned char* md, void* c) {
        if (c && pfnSHA512_Final) return reinterpret_cast<int(*)(unsigned char*,void*)>(pfnSHA512_Final)(md, c);
        if (md) SecureZeroMemory(md, 64);
        return 0;
    }

    void Proxy_CRYPTO_free(void* ptr, const char* file, int line) { if (pfnCRYPTO_free) reinterpret_cast<void(*)(void*,const char*,int)>(pfnCRYPTO_free)(ptr, file, line); else free(ptr); }
    void Proxy_RAND_seed(const void* buf, int num) { if (pfnRAND_seed) reinterpret_cast<void(*)(const void*,int)>(pfnRAND_seed)(buf, num); }
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID lpReserved) {
    if (ul_reason_for_call == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(hModule);

        char forceNoBackend[8] = {0};
        if (GetEnvironmentVariableA("KONGOR_PROXY_FORCE_NO_BACKEND", forceNoBackend, sizeof(forceNoBackend)) > 0 &&
            forceNoBackend[0] == '1') {
            OutputDebugStringA("[KONGOR CRYPTO GUARD] KONGOR_PROXY_FORCE_NO_BACKEND=1 active; running in fail-closed mode.\n");
            return TRUE;
        }

        char modDir[MAX_PATH] = {0};
        DWORD n = GetModuleFileNameA(hModule, modDir, MAX_PATH);
        if (n > 0 && n < MAX_PATH) {
            char* slash = strrchr(modDir, '\\');
            if (slash) *(slash + 1) = '\0';
            std::string localCrypto = std::string(modDir) + "libcrypto-1_1-x64.dll";
            g_hOriginalCrypto = LoadLibraryExA(localCrypto.c_str(), NULL, LOAD_WITH_ALTERED_SEARCH_PATH);
            if (!g_hOriginalCrypto) {
                std::string subCrypto = std::string(modDir) + "windows-x64\\libcrypto-1_1-x64.dll";
                g_hOriginalCrypto = LoadLibraryExA(subCrypto.c_str(), NULL, LOAD_WITH_ALTERED_SEARCH_PATH);
            }
        }
        if (!g_hOriginalCrypto) {
            g_hOriginalCrypto = LoadLibraryA("libcrypto-1_1-x64.dll");
        }
        if (!g_hOriginalCrypto) {
            g_hOriginalCrypto = LoadLibraryA("windows-x64\\libcrypto-1_1-x64.dll");
        }
        if (g_hOriginalCrypto) {
            InitializeCryptoProxies(g_hOriginalCrypto);
        } else {
            OutputDebugStringA("[KONGOR CRYPTO GUARD] WARNING: libcrypto-1_1-x64.dll not found; all SHA digest calls will fail closed.\n");
        }
    }
    else if (ul_reason_for_call == DLL_PROCESS_DETACH) {
        // [L2-P1-02] Never call FreeLibrary during process termination under OS loader lock
        if (lpReserved != nullptr) {
            return TRUE;
        }
        if (g_hOriginalCrypto) {
            FreeLibrary(g_hOriginalCrypto);
            g_hOriginalCrypto = NULL;
        }
    }
    return TRUE;
}
