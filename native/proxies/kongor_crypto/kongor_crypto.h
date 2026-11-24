// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#pragma once
#include <windows.h>

extern "C" {
    // BigNum operations
    extern FARPROC pfnBN_new;
    extern FARPROC pfnBN_hex2bn;
    extern FARPROC pfnBN_num_bits;
    extern FARPROC pfnBN_bn2bin;
    extern FARPROC pfnBN_free;
    extern FARPROC pfnBN_bin2bn;
    extern FARPROC pfnBN_bn2hex;
    extern FARPROC pfnBN_mul;
    extern FARPROC pfnBN_CTX_free;
    extern FARPROC pfnBN_sub;
    extern FARPROC pfnBN_CTX_new;
    extern FARPROC pfnBN_add;
    extern FARPROC pfnBN_div;
    extern FARPROC pfnBN_rand;
    extern FARPROC pfnBN_is_zero;
    extern FARPROC pfnBN_mod_exp;

    // Hashes
    extern FARPROC pfnSHA1;
    extern FARPROC pfnSHA1_Init;
    extern FARPROC pfnSHA1_Update;
    extern FARPROC pfnSHA1_Final;
    extern FARPROC pfnSHA224;
    extern FARPROC pfnSHA224_Init;
    extern FARPROC pfnSHA224_Update;
    extern FARPROC pfnSHA224_Final;
    extern FARPROC pfnSHA256;
    extern FARPROC pfnSHA256_Init;
    extern FARPROC pfnSHA256_Update;
    extern FARPROC pfnSHA256_Final;
    extern FARPROC pfnSHA384;
    extern FARPROC pfnSHA384_Init;
    extern FARPROC pfnSHA384_Update;
    extern FARPROC pfnSHA384_Final;
    extern FARPROC pfnSHA512;
    extern FARPROC pfnSHA512_Init;
    extern FARPROC pfnSHA512_Update;
    extern FARPROC pfnSHA512_Final;

    // General
    extern FARPROC pfnCRYPTO_free;
    extern FARPROC pfnRAND_seed;
}
