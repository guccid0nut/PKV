// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#pragma once
#include <windows.h>

extern "C" {
    // Core OpenSSL 1.1.1 SSL API function pointers
    extern FARPROC pfnTLS_client_method;
    extern FARPROC pfnTLS_server_method;
    extern FARPROC pfnSSL_CTX_new;
    extern FARPROC pfnSSL_CTX_free;
    extern FARPROC pfnSSL_CTX_ctrl;
    extern FARPROC pfnSSL_CTX_set_options;
    extern FARPROC pfnSSL_CTX_set_verify;
    extern FARPROC pfnSSL_CTX_set_cipher_list;
    extern FARPROC pfnSSL_CTX_set_ciphersuites;
    extern FARPROC pfnSSL_CTX_use_certificate;
    extern FARPROC pfnSSL_CTX_use_certificate_file;
    extern FARPROC pfnSSL_CTX_use_certificate_chain_file;
    extern FARPROC pfnSSL_CTX_use_PrivateKey;
    extern FARPROC pfnSSL_CTX_use_PrivateKey_file;
    extern FARPROC pfnSSL_CTX_check_private_key;
    extern FARPROC pfnSSL_CTX_set_default_passwd_cb;
    extern FARPROC pfnSSL_CTX_set_default_passwd_cb_userdata;
    extern FARPROC pfnSSL_CTX_load_verify_locations;
    extern FARPROC pfnSSL_CTX_add_client_CA;
    extern FARPROC pfnSSL_CTX_get_cert_store;
    extern FARPROC pfnSSL_CTX_set_alpn_protos;
    extern FARPROC pfnSSL_CTX_set_next_proto_select_cb;
    extern FARPROC pfnSSL_CTX_sess_set_new_cb;
    extern FARPROC pfnSSL_CTX_set_msg_callback;
    extern FARPROC pfnSSL_CTX_set_post_handshake_auth;
    extern FARPROC pfnSSL_CTX_set_keylog_callback;
    extern FARPROC pfnSSL_new;
    extern FARPROC pfnSSL_free;
    extern FARPROC pfnSSL_connect;
    extern FARPROC pfnSSL_accept;
    extern FARPROC pfnSSL_read;
    extern FARPROC pfnSSL_write;
    extern FARPROC pfnSSL_shutdown;
    extern FARPROC pfnSSL_ctrl;
    extern FARPROC pfnSSL_get_error;
    extern FARPROC pfnSSL_version;
    extern FARPROC pfnSSL_pending;
    extern FARPROC pfnSSL_set_bio;
    extern FARPROC pfnSSL_set_fd;
    extern FARPROC pfnSSL_set_connect_state;
    extern FARPROC pfnSSL_set_session;
    extern FARPROC pfnSSL_SESSION_free;
    extern FARPROC pfnSSL_get_current_cipher;
    extern FARPROC pfnSSL_CIPHER_get_name;
    extern FARPROC pfnSSL_get_certificate;
    extern FARPROC pfnSSL_get_privatekey;
    extern FARPROC pfnSSL_get_peer_certificate;
    extern FARPROC pfnSSL_get_peer_cert_chain;
    extern FARPROC pfnSSL_get_verify_result;
    extern FARPROC pfnSSL_get_shutdown;
    extern FARPROC pfnSSL_get0_alpn_selected;
    extern FARPROC pfnSSL_alert_desc_string_long;
    extern FARPROC pfnSSL_get_ex_data;
    extern FARPROC pfnSSL_set_ex_data;
    extern FARPROC pfnBIO_f_ssl;
}
