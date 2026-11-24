// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ============================================================================
// File: dllmain.cpp
// System: Native C++ Modernization - Modern Cryptography & TLS 1.3 Proxy Shim
// Subsystem: kongor_ssl_x64.dll ABI Proxy Forwarder
// ============================================================================

#include "kongor_ssl.h"
#include <windows.h>
#include <cstdio>
#include <cstring>
#include <string>

HMODULE g_hOriginalSsl = NULL;

extern "C" {
    FARPROC pfnTLS_client_method = NULL;
    FARPROC pfnTLS_server_method = NULL;
    FARPROC pfnSSL_CTX_new = NULL;
    FARPROC pfnSSL_CTX_free = NULL;
    FARPROC pfnSSL_CTX_ctrl = NULL;
    FARPROC pfnSSL_CTX_set_options = NULL;
    FARPROC pfnSSL_CTX_set_verify = NULL;
    FARPROC pfnSSL_CTX_set_cipher_list = NULL;
    FARPROC pfnSSL_CTX_set_ciphersuites = NULL;
    FARPROC pfnSSL_CTX_use_certificate = NULL;
    FARPROC pfnSSL_CTX_use_certificate_file = NULL;
    FARPROC pfnSSL_CTX_use_certificate_chain_file = NULL;
    FARPROC pfnSSL_CTX_use_PrivateKey = NULL;
    FARPROC pfnSSL_CTX_use_PrivateKey_file = NULL;
    FARPROC pfnSSL_CTX_check_private_key = NULL;
    FARPROC pfnSSL_CTX_set_default_passwd_cb = NULL;
    FARPROC pfnSSL_CTX_set_default_passwd_cb_userdata = NULL;
    FARPROC pfnSSL_CTX_load_verify_locations = NULL;
    FARPROC pfnSSL_CTX_add_client_CA = NULL;
    FARPROC pfnSSL_CTX_get_cert_store = NULL;
    FARPROC pfnSSL_CTX_set_alpn_protos = NULL;
    FARPROC pfnSSL_CTX_set_next_proto_select_cb = NULL;
    FARPROC pfnSSL_CTX_sess_set_new_cb = NULL;
    FARPROC pfnSSL_CTX_set_msg_callback = NULL;
    FARPROC pfnSSL_CTX_set_post_handshake_auth = NULL;
    FARPROC pfnSSL_CTX_set_keylog_callback = NULL;
    FARPROC pfnSSL_new = NULL;
    FARPROC pfnSSL_free = NULL;
    FARPROC pfnSSL_connect = NULL;
    FARPROC pfnSSL_accept = NULL;
    FARPROC pfnSSL_read = NULL;
    FARPROC pfnSSL_write = NULL;
    FARPROC pfnSSL_shutdown = NULL;
    FARPROC pfnSSL_ctrl = NULL;
    FARPROC pfnSSL_get_error = NULL;
    FARPROC pfnSSL_version = NULL;
    FARPROC pfnSSL_pending = NULL;
    FARPROC pfnSSL_set_bio = NULL;
    FARPROC pfnSSL_set_fd = NULL;
    FARPROC pfnSSL_set_connect_state = NULL;
    FARPROC pfnSSL_set_session = NULL;
    FARPROC pfnSSL_SESSION_free = NULL;
    FARPROC pfnSSL_get_current_cipher = NULL;
    FARPROC pfnSSL_CIPHER_get_name = NULL;
    FARPROC pfnSSL_get_certificate = NULL;
    FARPROC pfnSSL_get_privatekey = NULL;
    FARPROC pfnSSL_get_peer_certificate = NULL;
    FARPROC pfnSSL_get_peer_cert_chain = NULL;
    FARPROC pfnSSL_get_verify_result = NULL;
    FARPROC pfnSSL_get_shutdown = NULL;
    FARPROC pfnSSL_get0_alpn_selected = NULL;
    FARPROC pfnSSL_alert_desc_string_long = NULL;
    FARPROC pfnSSL_get_ex_data = NULL;
    FARPROC pfnSSL_set_ex_data = NULL;
    FARPROC pfnBIO_f_ssl = NULL;
}

static void InitializeSslProxies(HMODULE hMod) {
    if (!hMod) return;

#define BIND_SSL_FN(name) pfn##name = GetProcAddress(hMod, #name)
    BIND_SSL_FN(TLS_client_method);
    BIND_SSL_FN(TLS_server_method);
    BIND_SSL_FN(SSL_CTX_new);
    BIND_SSL_FN(SSL_CTX_free);
    BIND_SSL_FN(SSL_CTX_ctrl);
    BIND_SSL_FN(SSL_CTX_set_options);
    BIND_SSL_FN(SSL_CTX_set_verify);
    BIND_SSL_FN(SSL_CTX_set_cipher_list);
    BIND_SSL_FN(SSL_CTX_set_ciphersuites);
    BIND_SSL_FN(SSL_CTX_use_certificate);
    BIND_SSL_FN(SSL_CTX_use_certificate_file);
    BIND_SSL_FN(SSL_CTX_use_certificate_chain_file);
    BIND_SSL_FN(SSL_CTX_use_PrivateKey);
    BIND_SSL_FN(SSL_CTX_use_PrivateKey_file);
    BIND_SSL_FN(SSL_CTX_check_private_key);
    BIND_SSL_FN(SSL_CTX_set_default_passwd_cb);
    BIND_SSL_FN(SSL_CTX_set_default_passwd_cb_userdata);
    BIND_SSL_FN(SSL_CTX_load_verify_locations);
    BIND_SSL_FN(SSL_CTX_add_client_CA);
    BIND_SSL_FN(SSL_CTX_get_cert_store);
    BIND_SSL_FN(SSL_CTX_set_alpn_protos);
    BIND_SSL_FN(SSL_CTX_set_next_proto_select_cb);
    BIND_SSL_FN(SSL_CTX_sess_set_new_cb);
    BIND_SSL_FN(SSL_CTX_set_msg_callback);
    BIND_SSL_FN(SSL_CTX_set_post_handshake_auth);
    BIND_SSL_FN(SSL_CTX_set_keylog_callback);
    BIND_SSL_FN(SSL_new);
    BIND_SSL_FN(SSL_free);
    BIND_SSL_FN(SSL_connect);
    BIND_SSL_FN(SSL_accept);
    BIND_SSL_FN(SSL_read);
    BIND_SSL_FN(SSL_write);
    BIND_SSL_FN(SSL_shutdown);
    BIND_SSL_FN(SSL_ctrl);
    BIND_SSL_FN(SSL_get_error);
    BIND_SSL_FN(SSL_version);
    BIND_SSL_FN(SSL_pending);
    BIND_SSL_FN(SSL_set_bio);
    BIND_SSL_FN(SSL_set_fd);
    BIND_SSL_FN(SSL_set_connect_state);
    BIND_SSL_FN(SSL_set_session);
    BIND_SSL_FN(SSL_SESSION_free);
    BIND_SSL_FN(SSL_get_current_cipher);
    BIND_SSL_FN(SSL_CIPHER_get_name);
    BIND_SSL_FN(SSL_get_certificate);
    BIND_SSL_FN(SSL_get_privatekey);
    BIND_SSL_FN(SSL_get_peer_certificate);
    BIND_SSL_FN(SSL_get_peer_cert_chain);
    BIND_SSL_FN(SSL_get_verify_result);
    BIND_SSL_FN(SSL_get_shutdown);
    BIND_SSL_FN(SSL_get0_alpn_selected);
    BIND_SSL_FN(SSL_alert_desc_string_long);
    BIND_SSL_FN(SSL_get_ex_data);
    BIND_SSL_FN(SSL_set_ex_data);
    BIND_SSL_FN(BIO_f_ssl);
#undef BIND_SSL_FN
}

extern "C" {
    void* Proxy_TLS_client_method() { return pfnTLS_client_method ? reinterpret_cast<void*(*)()>(pfnTLS_client_method)() : nullptr; }
    void* Proxy_TLS_server_method() { return pfnTLS_server_method ? reinterpret_cast<void*(*)()>(pfnTLS_server_method)() : nullptr; }
    void* Proxy_SSL_CTX_new(void* meth) { return pfnSSL_CTX_new ? reinterpret_cast<void*(*)(void*)>(pfnSSL_CTX_new)(meth) : nullptr; }
    void  Proxy_SSL_CTX_free(void* ctx) { if (pfnSSL_CTX_free) reinterpret_cast<void(*)(void*)>(pfnSSL_CTX_free)(ctx); }
    long  Proxy_SSL_CTX_ctrl(void* ctx, int cmd, long larg, void* parg) { return pfnSSL_CTX_ctrl ? reinterpret_cast<long(*)(void*,int,long,void*)>(pfnSSL_CTX_ctrl)(ctx, cmd, larg, parg) : 0; }
    long  Proxy_SSL_CTX_set_options(void* ctx, long op) { return pfnSSL_CTX_set_options ? reinterpret_cast<long(*)(void*,long)>(pfnSSL_CTX_set_options)(ctx, op) : 0; }
    void  Proxy_SSL_CTX_set_verify(void* ctx, int mode, void* cb) { if (pfnSSL_CTX_set_verify) reinterpret_cast<void(*)(void*,int,void*)>(pfnSSL_CTX_set_verify)(ctx, mode, cb); }
    int   Proxy_SSL_CTX_set_cipher_list(void* ctx, const char* str) { return pfnSSL_CTX_set_cipher_list ? reinterpret_cast<int(*)(void*,const char*)>(pfnSSL_CTX_set_cipher_list)(ctx, str) : 0; }
    int   Proxy_SSL_CTX_set_ciphersuites(void* ctx, const char* str) { return pfnSSL_CTX_set_ciphersuites ? reinterpret_cast<int(*)(void*,const char*)>(pfnSSL_CTX_set_ciphersuites)(ctx, str) : 0; }
    int   Proxy_SSL_CTX_use_certificate(void* ctx, void* x) { return pfnSSL_CTX_use_certificate ? reinterpret_cast<int(*)(void*,void*)>(pfnSSL_CTX_use_certificate)(ctx, x) : 0; }
    int   Proxy_SSL_CTX_use_certificate_file(void* ctx, const char* file, int type) { return pfnSSL_CTX_use_certificate_file ? reinterpret_cast<int(*)(void*,const char*,int)>(pfnSSL_CTX_use_certificate_file)(ctx, file, type) : 0; }
    int   Proxy_SSL_CTX_use_certificate_chain_file(void* ctx, const char* file) { return pfnSSL_CTX_use_certificate_chain_file ? reinterpret_cast<int(*)(void*,const char*)>(pfnSSL_CTX_use_certificate_chain_file)(ctx, file) : 0; }
    int   Proxy_SSL_CTX_use_PrivateKey(void* ctx, void* pkey) { return pfnSSL_CTX_use_PrivateKey ? reinterpret_cast<int(*)(void*,void*)>(pfnSSL_CTX_use_PrivateKey)(ctx, pkey) : 0; }
    int   Proxy_SSL_CTX_use_PrivateKey_file(void* ctx, const char* file, int type) { return pfnSSL_CTX_use_PrivateKey_file ? reinterpret_cast<int(*)(void*,const char*,int)>(pfnSSL_CTX_use_PrivateKey_file)(ctx, file, type) : 0; }
    int   Proxy_SSL_CTX_check_private_key(void* ctx) { return pfnSSL_CTX_check_private_key ? reinterpret_cast<int(*)(void*)>(pfnSSL_CTX_check_private_key)(ctx) : 0; }
    void  Proxy_SSL_CTX_set_default_passwd_cb(void* ctx, void* cb) { if (pfnSSL_CTX_set_default_passwd_cb) reinterpret_cast<void(*)(void*,void*)>(pfnSSL_CTX_set_default_passwd_cb)(ctx, cb); }
    void  Proxy_SSL_CTX_set_default_passwd_cb_userdata(void* ctx, void* u) { if (pfnSSL_CTX_set_default_passwd_cb_userdata) reinterpret_cast<void(*)(void*,void*)>(pfnSSL_CTX_set_default_passwd_cb_userdata)(ctx, u); }
    int   Proxy_SSL_CTX_load_verify_locations(void* ctx, const char* CAfile, const char* CApath) { return pfnSSL_CTX_load_verify_locations ? reinterpret_cast<int(*)(void*,const char*,const char*)>(pfnSSL_CTX_load_verify_locations)(ctx, CAfile, CApath) : 0; }
    int   Proxy_SSL_CTX_add_client_CA(void* ctx, void* x) { return pfnSSL_CTX_add_client_CA ? reinterpret_cast<int(*)(void*,void*)>(pfnSSL_CTX_add_client_CA)(ctx, x) : 0; }
    void* Proxy_SSL_CTX_get_cert_store(void* ctx) { return pfnSSL_CTX_get_cert_store ? reinterpret_cast<void*(*)(void*)>(pfnSSL_CTX_get_cert_store)(ctx) : nullptr; }
    int   Proxy_SSL_CTX_set_alpn_protos(void* ctx, const unsigned char* protos, unsigned int len) { return pfnSSL_CTX_set_alpn_protos ? reinterpret_cast<int(*)(void*,const unsigned char*,unsigned int)>(pfnSSL_CTX_set_alpn_protos)(ctx, protos, len) : 0; }
    void  Proxy_SSL_CTX_set_next_proto_select_cb(void* ctx, void* cb, void* arg) { if (pfnSSL_CTX_set_next_proto_select_cb) reinterpret_cast<void(*)(void*,void*,void*)>(pfnSSL_CTX_set_next_proto_select_cb)(ctx, cb, arg); }
    void  Proxy_SSL_CTX_sess_set_new_cb(void* ctx, void* cb) { if (pfnSSL_CTX_sess_set_new_cb) reinterpret_cast<void(*)(void*,void*)>(pfnSSL_CTX_sess_set_new_cb)(ctx, cb); }
    void  Proxy_SSL_CTX_set_msg_callback(void* ctx, void* cb) { if (pfnSSL_CTX_set_msg_callback) reinterpret_cast<void(*)(void*,void*)>(pfnSSL_CTX_set_msg_callback)(ctx, cb); }
    int   Proxy_SSL_CTX_set_post_handshake_auth(void* ctx, int val) { return pfnSSL_CTX_set_post_handshake_auth ? reinterpret_cast<int(*)(void*,int)>(pfnSSL_CTX_set_post_handshake_auth)(ctx, val) : 0; }
    void  Proxy_SSL_CTX_set_keylog_callback(void* ctx, void* cb) { if (pfnSSL_CTX_set_keylog_callback) reinterpret_cast<void(*)(void*,void*)>(pfnSSL_CTX_set_keylog_callback)(ctx, cb); }
    void* Proxy_SSL_new(void* ctx) { return pfnSSL_new ? reinterpret_cast<void*(*)(void*)>(pfnSSL_new)(ctx) : nullptr; }
    void  Proxy_SSL_free(void* ssl) { if (pfnSSL_free) reinterpret_cast<void(*)(void*)>(pfnSSL_free)(ssl); }
    int   Proxy_SSL_connect(void* ssl) { return pfnSSL_connect ? reinterpret_cast<int(*)(void*)>(pfnSSL_connect)(ssl) : -1; }
    int   Proxy_SSL_accept(void* ssl) { return pfnSSL_accept ? reinterpret_cast<int(*)(void*)>(pfnSSL_accept)(ssl) : -1; }
    int   Proxy_SSL_read(void* ssl, void* buf, int num) { return pfnSSL_read ? reinterpret_cast<int(*)(void*,void*,int)>(pfnSSL_read)(ssl, buf, num) : -1; }
    int   Proxy_SSL_write(void* ssl, const void* buf, int num) { return pfnSSL_write ? reinterpret_cast<int(*)(void*,const void*,int)>(pfnSSL_write)(ssl, buf, num) : -1; }
    int   Proxy_SSL_shutdown(void* ssl) { return pfnSSL_shutdown ? reinterpret_cast<int(*)(void*)>(pfnSSL_shutdown)(ssl) : 0; }
    long  Proxy_SSL_ctrl(void* ssl, int cmd, long larg, void* parg) { return pfnSSL_ctrl ? reinterpret_cast<long(*)(void*,int,long,void*)>(pfnSSL_ctrl)(ssl, cmd, larg, parg) : 0; }
    int   Proxy_SSL_get_error(void* ssl, int ret) { return (ssl && pfnSSL_get_error) ? reinterpret_cast<int(*)(void*,int)>(pfnSSL_get_error)(ssl, ret) : 1; /* SSL_ERROR_SSL */ }
    int   Proxy_SSL_version(void* ssl) { return (ssl && pfnSSL_version) ? reinterpret_cast<int(*)(void*)>(pfnSSL_version)(ssl) : 0x0304; /* TLS1_3_VERSION constant */ }
    int   Proxy_SSL_pending(void* ssl) { return (ssl && pfnSSL_pending) ? reinterpret_cast<int(*)(void*)>(pfnSSL_pending)(ssl) : 0; }
    void  Proxy_SSL_set_bio(void* ssl, void* rbio, void* wbio) { if (ssl && pfnSSL_set_bio) reinterpret_cast<void(*)(void*,void*,void*)>(pfnSSL_set_bio)(ssl, rbio, wbio); }
    int   Proxy_SSL_set_fd(void* ssl, int fd) { return (ssl && pfnSSL_set_fd) ? reinterpret_cast<int(*)(void*,int)>(pfnSSL_set_fd)(ssl, fd) : 0; }
    void  Proxy_SSL_set_connect_state(void* ssl) { if (ssl && pfnSSL_set_connect_state) reinterpret_cast<void(*)(void*)>(pfnSSL_set_connect_state)(ssl); }
    int   Proxy_SSL_set_session(void* ssl, void* session) { return (ssl && pfnSSL_set_session) ? reinterpret_cast<int(*)(void*,void*)>(pfnSSL_set_session)(ssl, session) : 0; }
    void  Proxy_SSL_SESSION_free(void* ses) { if (ses && pfnSSL_SESSION_free) reinterpret_cast<void(*)(void*)>(pfnSSL_SESSION_free)(ses); }
    void* Proxy_SSL_get_current_cipher(void* ssl) { return (ssl && pfnSSL_get_current_cipher) ? reinterpret_cast<void*(*)(void*)>(pfnSSL_get_current_cipher)(ssl) : nullptr; }
    const char* Proxy_SSL_CIPHER_get_name(void* c) { return (c && pfnSSL_CIPHER_get_name) ? reinterpret_cast<const char*(*)(void*)>(pfnSSL_CIPHER_get_name)(c) : "NONE"; }
    void* Proxy_SSL_get_certificate(void* ssl) { return (ssl && pfnSSL_get_certificate) ? reinterpret_cast<void*(*)(void*)>(pfnSSL_get_certificate)(ssl) : nullptr; }
    void* Proxy_SSL_get_privatekey(void* ssl) { return (ssl && pfnSSL_get_privatekey) ? reinterpret_cast<void*(*)(void*)>(pfnSSL_get_privatekey)(ssl) : nullptr; }
    void* Proxy_SSL_get_peer_certificate(void* ssl) { return (ssl && pfnSSL_get_peer_certificate) ? reinterpret_cast<void*(*)(void*)>(pfnSSL_get_peer_certificate)(ssl) : nullptr; }
    void* Proxy_SSL_get_peer_cert_chain(void* ssl) { return (ssl && pfnSSL_get_peer_cert_chain) ? reinterpret_cast<void*(*)(void*)>(pfnSSL_get_peer_cert_chain)(ssl) : nullptr; }
    // [L2-P1-01] Fail-closed: NEVER return 0 (X509_V_OK) when ssl handle or real OpenSSL backend is missing!
    long  Proxy_SSL_get_verify_result(void* ssl) {
        if (!ssl || !pfnSSL_get_verify_result) {
            OutputDebugStringA("[KONGOR SSL GUARD] FAIL-CLOSED: SSL_get_verify_result rejected (missing ssl handle or libssl backend); returning X509_V_ERR_INVALID_CALL (69).\n");
            return 69L; // X509_V_ERR_INVALID_CALL (non-zero != X509_V_OK)
        }
        return reinterpret_cast<long(*)(void*)>(pfnSSL_get_verify_result)(ssl);
    }
    int   Proxy_SSL_get_shutdown(void* ssl) { return (ssl && pfnSSL_get_shutdown) ? reinterpret_cast<int(*)(void*)>(pfnSSL_get_shutdown)(ssl) : 0; }
    void  Proxy_SSL_get0_alpn_selected(void* ssl, const unsigned char** data, unsigned int* len) {
        if (ssl && pfnSSL_get0_alpn_selected) {
            reinterpret_cast<void(*)(void*,const unsigned char**,unsigned int*)>(pfnSSL_get0_alpn_selected)(ssl, data, len);
        } else {
            if (data) *data = nullptr;
            if (len) *len = 0;
        }
    }
    const char* Proxy_SSL_alert_desc_string_long(int val) { return pfnSSL_alert_desc_string_long ? reinterpret_cast<const char*(*)(int)>(pfnSSL_alert_desc_string_long)(val) : "handshake failure"; }
    void* Proxy_SSL_get_ex_data(void* ssl, int idx) { return (ssl && pfnSSL_get_ex_data) ? reinterpret_cast<void*(*)(void*,int)>(pfnSSL_get_ex_data)(ssl, idx) : nullptr; }
    int   Proxy_SSL_set_ex_data(void* ssl, int idx, void* data) { return (ssl && pfnSSL_set_ex_data) ? reinterpret_cast<int(*)(void*,int,void*)>(pfnSSL_set_ex_data)(ssl, idx, data) : 0; }
    void* Proxy_BIO_f_ssl() { return pfnBIO_f_ssl ? reinterpret_cast<void*(*)()>(pfnBIO_f_ssl)() : nullptr; }
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID lpReserved) {
    if (ul_reason_for_call == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(hModule);

        char forceNoBackend[8] = {0};
        if (GetEnvironmentVariableA("KONGOR_PROXY_FORCE_NO_BACKEND", forceNoBackend, sizeof(forceNoBackend)) > 0 &&
            forceNoBackend[0] == '1') {
            OutputDebugStringA("[KONGOR SSL GUARD] KONGOR_PROXY_FORCE_NO_BACKEND=1 active; running in fail-closed mode.\n");
            return TRUE;
        }

        // Resolve strictly relative to module directory first to avoid untrusted CWD DLL search
        char modDir[MAX_PATH] = {0};
        DWORD n = GetModuleFileNameA(hModule, modDir, MAX_PATH);
        if (n > 0 && n < MAX_PATH) {
            char* slash = strrchr(modDir, '\\');
            if (slash) *(slash + 1) = '\0';
            std::string localSsl = std::string(modDir) + "libssl-1_1-x64.dll";
            g_hOriginalSsl = LoadLibraryExA(localSsl.c_str(), NULL, LOAD_WITH_ALTERED_SEARCH_PATH);
            if (!g_hOriginalSsl) {
                std::string subSsl = std::string(modDir) + "windows-x64\\libssl-1_1-x64.dll";
                g_hOriginalSsl = LoadLibraryExA(subSsl.c_str(), NULL, LOAD_WITH_ALTERED_SEARCH_PATH);
            }
        }
        if (!g_hOriginalSsl) {
            g_hOriginalSsl = LoadLibraryA("libssl-1_1-x64.dll");
        }
        if (!g_hOriginalSsl) {
            g_hOriginalSsl = LoadLibraryA("windows-x64\\libssl-1_1-x64.dll");
        }
        if (g_hOriginalSsl) {
            InitializeSslProxies(g_hOriginalSsl);
        } else {
            OutputDebugStringA("[KONGOR SSL GUARD] WARNING: libssl-1_1-x64.dll not found; all TLS verification calls will fail closed.\n");
        }
    }
    else if (ul_reason_for_call == DLL_PROCESS_DETACH) {
        // [L2-P1-02] Never call FreeLibrary during process termination under OS loader lock
        if (lpReserved != nullptr) {
            return TRUE;
        }
        if (g_hOriginalSsl) {
            FreeLibrary(g_hOriginalSsl);
            g_hOriginalSsl = NULL;
        }
    }
    return TRUE;
}
