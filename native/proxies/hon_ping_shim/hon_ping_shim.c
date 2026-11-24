// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#define _GNU_SOURCE
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <dlfcn.h>
#include <sys/types.h>
#include <sys/socket.h>
#include <netinet/in.h>
#include <arpa/inet.h>
#include <errno.h>
#include <stdarg.h>
#include <time.h>
#include <sys/mman.h>
#include <stdint.h>
#include "hon_ping_protocol.h"

static ssize_t (*real_recvfrom)(int, void *, size_t, int, struct sockaddr *, socklen_t *) = NULL;

static char g_server_name[128] = "Kongor Server #K1";
static char g_server_location[32] = "USE";
static char g_map_name[64] = "caldavar";
static char g_match_type[64] = "normal";
static int g_team_size = 5;
static char g_version[32] = "5.0.0.0";
static int g_configured = 0;
static int g_svr_port = 0;
static int g_is_game_active = 0;
static char g_match_description[128] = "";

static int check_game_active(void) {
    if (g_svr_port > 0) {
        char path[64];
        snprintf(path, sizeof(path), "/tmp/hon_game_idle_%d", g_svr_port);
        if (access(path, F_OK) == 0) return 0;
        snprintf(path, sizeof(path), "/tmp/hon_game_active_%d", g_svr_port);
        if (access(path, F_OK) == 0) return 1;
    }
    return g_is_game_active;
}

static void log_msg(const char *fmt, ...) {
    char buf[512];
    va_list args;
    va_start(args, fmt);
    vsnprintf(buf, sizeof(buf), fmt, args);
    va_end(args);

    fprintf(stderr, "[HON_PING_SHIM] %s\n", buf);
}

static void init_config(void) {
    if (g_configured) return;
    g_configured = 1;

    const char *env_name = getenv("HON_SERVER_NAME");
    if (!env_name) env_name = getenv("COMPEL_SERVER_NAME_PREFIX");
    if (env_name && strlen(env_name) > 0) {
        strncpy(g_server_name, env_name, sizeof(g_server_name) - 1);
        g_server_name[sizeof(g_server_name) - 1] = '\0';
    }

    const char *env_loc = getenv("COMPEL_LOCATION");
    if (!env_loc) env_loc = getenv("HON_SERVER_LOCATION");
    if (env_loc && strlen(env_loc) > 0) {
        strncpy(g_server_location, env_loc, sizeof(g_server_location) - 1);
        g_server_location[sizeof(g_server_location) - 1] = '\0';
    }

    FILE *f = fopen("/proc/self/cmdline", "rb");
    if (f) {
        char cmdline[8192];
        size_t n = fread(cmdline, 1, sizeof(cmdline) - 1, f);
        fclose(f);
        if (n > 0) {
            cmdline[n] = '\0';
            size_t i = 0;
            while (i < n) {
                const char *arg = &cmdline[i];
                if (strstr(arg, "svr_name") != NULL) {
                    if (i + strlen(arg) + 1 < n) {
                        const char *val = &cmdline[i + strlen(arg) + 1];
                        if (val && strlen(val) > 0 && val[0] != '+' && val[0] != '-') {
                            strncpy(g_server_name, val, sizeof(g_server_name) - 1);
                            g_server_name[sizeof(g_server_name) - 1] = '\0';
                        }
                    }
                }
                if (strstr(arg, "svr_location") != NULL) {
                    if (i + strlen(arg) + 1 < n) {
                        const char *val = &cmdline[i + strlen(arg) + 1];
                        if (val && strlen(val) > 0 && val[0] != '+' && val[0] != '-') {
                            strncpy(g_server_location, val, sizeof(g_server_location) - 1);
                            g_server_location[sizeof(g_server_location) - 1] = '\0';
                        }
                    }
                }
                if (strstr(arg, "svr_port") != NULL || strcmp(arg, "-port") == 0) {
                    if (i + strlen(arg) + 1 < n) {
                        const char *val = &cmdline[i + strlen(arg) + 1];
                        if (val && strlen(val) > 0 && val[0] != '+' && val[0] != '-') {
                            g_svr_port = atoi(val);
                        }
                    }
                }
                i += strlen(arg) + 1;
            }
        }
    }

    // Strip leading and trailing quotes from server name and location
    size_t s_len = strlen(g_server_name);
    while (s_len > 0 && (g_server_name[0] == '"' || g_server_name[0] == '\'')) {
        memmove(g_server_name, g_server_name + 1, s_len);
        s_len--;
    }
    while (s_len > 0 && (g_server_name[s_len - 1] == '"' || g_server_name[s_len - 1] == '\'')) {
        g_server_name[s_len - 1] = '\0';
        s_len--;
    }

    size_t l_len = strlen(g_server_location);
    while (l_len > 0 && (g_server_location[0] == '"' || g_server_location[0] == '\'')) {
        memmove(g_server_location, g_server_location + 1, l_len);
        l_len--;
    }
    while (l_len > 0 && (g_server_location[l_len - 1] == '"' || g_server_location[l_len - 1] == '\'')) {
        g_server_location[l_len - 1] = '\0';
        l_len--;
    }

    const char *env_ver = getenv("HON_SERVER_VERSION");
    if (!env_ver) env_ver = getenv("COMPEL_SERVER_VERSION");
    if (env_ver && strlen(env_ver) > 0) {
        strncpy(g_version, env_ver, sizeof(g_version) - 1);
        g_version[sizeof(g_version) - 1] = '\0';
    }

    log_msg("Initialized configuration: server_name='%s', location='%s', version='%s'",
            g_server_name, g_server_location, g_version);
}

static int apply_engine_patches(void) {
    static int s_k2_patched = 0;
    static int s_game_patched = 0;
    static int s_game_shared_patched = 0;
    if (s_k2_patched && s_game_patched && s_game_shared_patched) return 1;

    FILE *fp = fopen("/proc/self/maps", "r");
    if (!fp) {
        log_msg("Failed to open /proc/self/maps for engine patches");
        return 0;
    }

    uintptr_t k2_base = 0;
    uintptr_t game_base = 0;
    uintptr_t game_shared_base = 0;
    char line[512];
    while (fgets(line, sizeof(line), fp)) {
        if (!k2_base && strstr(line, "libk2") && strstr(line, "r-xp")) {
            char *dash = strchr(line, '-');
            if (dash) {
                *dash = '\0';
                k2_base = (uintptr_t)strtoull(line, NULL, 16);
            }
        }
        if (!game_base && strstr(line, "game-x86_64-server.so") && !strstr(line, "libgame_shared") && strstr(line, "r-xp")) {
            char *dash = strchr(line, '-');
            if (dash) {
                *dash = '\0';
                game_base = (uintptr_t)strtoull(line, NULL, 16);
            }
        }
        if (!game_shared_base && strstr(line, "libgame_shared-x86_64-server.so") && strstr(line, "r-xp")) {
            char *dash = strchr(line, '-');
            if (dash) {
                *dash = '\0';
                game_shared_base = (uintptr_t)strtoull(line, NULL, 16);
            }
        }
    }
    fclose(fp);

    long page_size = sysconf(_SC_PAGESIZE);
    if (page_size <= 0) page_size = 4096;

    if (k2_base && !s_k2_patched) {
        // Patch 1: Host promotion at 0xCBEF91 (10 bytes)
        uintptr_t target = k2_base + 0xCBEF91;
        unsigned char expected_orig[10] = { 0x83, 0xf9, 0xff, 0x74, 0x05, 0x44, 0x39, 0xf9, 0x74, 0x20 };
        unsigned char expected_patch[10] = { 0x45, 0x85, 0xff, 0x74, 0x25, 0x90, 0x90, 0x90, 0x90, 0x90 };

        if (memcmp((const void *)target, expected_patch, 10) == 0) {
            log_msg("Host promotion patch already active at %p (base %p)", (void *)target, (void *)k2_base);
        } else if (memcmp((const void *)target, expected_orig, 10) == 0) {
            uintptr_t page_start = target & ~((uintptr_t)page_size - 1);
            size_t map_len = (size_t)page_size;
            if ((target + 10) > (page_start + (uintptr_t)page_size)) {
                map_len = (size_t)page_size * 2;
            }

            if (mprotect((void *)page_start, map_len, PROT_READ | PROT_WRITE | PROT_EXEC) == 0) {
                memcpy((void *)target, expected_patch, 10);
                mprotect((void *)page_start, map_len, PROT_READ | PROT_EXEC);
                log_msg("SUCCESS: Patched libk2 host promotion at %p (base %p): client 0 auto-promoted to host!",
                        (void *)target, (void *)k2_base);
            }
        } else {
            unsigned char current[10];
            memcpy(current, (const void *)target, 10);
            log_msg("Host promotion target bytes at %p mismatch: %02x %02x %02x %02x %02x %02x %02x %02x %02x %02x",
                    (void *)target,
                    current[0], current[1], current[2], current[3], current[4],
                    current[5], current[6], current[7], current[8], current[9]);
        }

        // Patch 2: Host ID bypass & auto-accept at 0xCCAF34 (26 bytes)
        uintptr_t target2 = k2_base + 0xCCAF34;
        unsigned char expected_orig2[26] = { 0x75, 0x18, 0x41, 0xbd, 0x07, 0x00, 0x00, 0x00, 0x48, 0x8b, 0x44, 0x24, 0x20, 0x80, 0xb8, 0xe1, 0x03, 0x00, 0x00, 0x00, 0x0f, 0x84, 0x73, 0x04, 0x00, 0x00 };
        unsigned char expected_patch2[26] = { 0x90, 0x90, 0x41, 0xbd, 0x07, 0x00, 0x00, 0x00, 0xe9, 0x80, 0x04, 0x00, 0x00, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 };

        if (memcmp((const void *)target2, expected_patch2, 26) == 0) {
            log_msg("Host ID bypass patch already active at %p (base %p)", (void *)target2, (void *)k2_base);
        } else if (memcmp((const void *)target2, expected_orig2, 26) == 0) {
            uintptr_t page_start2 = target2 & ~((uintptr_t)page_size - 1);
            size_t map_len2 = (size_t)page_size;
            if ((target2 + 26) > (page_start2 + (uintptr_t)page_size)) {
                map_len2 = (size_t)page_size * 2;
            }

            if (mprotect((void *)page_start2, map_len2, PROT_READ | PROT_WRITE | PROT_EXEC) == 0) {
                memcpy((void *)target2, expected_patch2, 26);
                mprotect((void *)page_start2, map_len2, PROT_READ | PROT_EXEC);
                log_msg("SUCCESS: Patched libk2 Host ID bypass & auto-accept at %p (base %p): client connection accepted as host!",
                        (void *)target2, (void *)k2_base);
            }
        } else {
            unsigned char current2[26];
            memcpy(current2, (const void *)target2, 26);
            log_msg("Host ID bypass target bytes at %p mismatch: %02x %02x %02x %02x %02x %02x %02x %02x",
                    (void *)target2,
                    current2[0], current2[1], current2[2], current2[3], current2[4],
                    current2[5], current2[6], current2[7]);
        }

        // Patch 3: Host disconnect bypass at 0xCB84E4 (6 bytes)
        // In CClientConnection::Frame(), if client is host (CLIENT_FLAG_HOST = 7),
        // it checks if byte at 0x1b0 is zero. If zero, it executes `je 0xCB910F` which
        // triggers Disconnect(11) (disconnect_invalid_connection).
        // NOP-ing this 6-byte jump allows player-hosted connections to stay connected.
        uintptr_t target3 = k2_base + 0xCB84E4;
        unsigned char expected_orig3[6] = { 0x0f, 0x84, 0x25, 0x0c, 0x00, 0x00 };
        unsigned char expected_patch3[6] = { 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 };

        if (memcmp((const void *)target3, expected_patch3, 6) == 0) {
            log_msg("Host disconnect bypass patch already active at %p (base %p)", (void *)target3, (void *)k2_base);
        } else if (memcmp((const void *)target3, expected_orig3, 6) == 0) {
            uintptr_t page_start3 = target3 & ~((uintptr_t)page_size - 1);
            size_t map_len3 = (size_t)page_size;
            if ((target3 + 6) > (page_start3 + (uintptr_t)page_size)) {
                map_len3 = (size_t)page_size * 2;
            }

            if (mprotect((void *)page_start3, map_len3, PROT_READ | PROT_WRITE | PROT_EXEC) == 0) {
                memcpy((void *)target3, expected_patch3, 6);
                mprotect((void *)page_start3, map_len3, PROT_READ | PROT_EXEC);
                log_msg("SUCCESS: Patched libk2 Host disconnect bypass at %p (base %p): client host kept connected without disconnect_invalid_connection!",
                        (void *)target3, (void *)k2_base);
            }
        } else {
            unsigned char current3[6];
            memcpy(current3, (const void *)target3, 6);
            log_msg("Host disconnect bypass target bytes at %p mismatch: %02x %02x %02x %02x %02x %02x",
                    (void *)target3,
                    current3[0], current3[1], current3[2], current3[3], current3[4], current3[5]);
        }

        // Patch 4: Client Game Data bypass in GS_SLEEP at 0xCC0CE7 (6 bytes)
        // In libk2-x86_64-server.so packet dispatcher for NETCMD_CLIENT_GAME_DATA (opcode 200 / 0xC8):
        // 0xcc0ce3: cmpb $0x0,0x10(%rax)      # is game running?
        // 0xcc0ce7: je cc1222                 # jumps to throw "Failed to process NETCMD_CLIENT_GAME_DATA"
        // 0xcc0cf1: mov 0xb0(%rax),%rax       # function pointer to ProcessGameData (0x17fcc0)
        // 0xcc0cfb: call *%rax                # calls ProcessGameData
        // While in GS_SLEEP (c_state=1), m_bGameRunning is 0.
        // NOP-ing this 6-byte jump allows incoming GAME_CMD_CREATE_GAME to invoke ProcessGameData!
        uintptr_t target4 = k2_base + 0xCC0CE7;
        unsigned char expected_orig4[6] = { 0x0f, 0x84, 0x35, 0x05, 0x00, 0x00 };
        unsigned char expected_patch4[6] = { 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 };

        if (memcmp((const void *)target4, expected_patch4, 6) == 0) {
            log_msg("Dormant game data bypass patch already active at %p (base %p)", (void *)target4, (void *)k2_base);
        } else if (memcmp((const void *)target4, expected_orig4, 6) == 0) {
            uintptr_t page_start4 = target4 & ~((uintptr_t)page_size - 1);
            size_t map_len4 = (size_t)page_size;
            if ((target4 + 6) > (page_start4 + (uintptr_t)page_size)) {
                map_len4 = (size_t)page_size * 2;
            }

            if (mprotect((void *)page_start4, map_len4, PROT_READ | PROT_WRITE | PROT_EXEC) == 0) {
                memcpy((void *)target4, expected_patch4, 6);
                mprotect((void *)page_start4, map_len4, PROT_READ | PROT_EXEC);
                log_msg("SUCCESS: Patched libk2 dormant game data bypass at %p (base %p): GAME_CMD_CREATE_GAME permitted while dormant!",
                        (void *)target4, (void *)k2_base);
            }
        } else {
            unsigned char current4[6];
            memcpy(current4, (const void *)target4, 6);
            log_msg("Dormant game data bypass target bytes at %p mismatch: %02x %02x %02x %02x %02x %02x",
                    (void *)target4,
                    current4[0], current4[1], current4[2], current4[3], current4[4], current4[5]);
        }

        // Patch 6: Fix CClientConnection packet size underflows in libk2
        // S2 Games K2 engine bug: `uiSpaceLeft = maxPacketSize - packetLen` underflows to 4.29 billion
        // when packet is full (packetLen >= maxPacketSize).
        // The check `cmp $0xa, %ebp` uses `jb` (unsigned jump if below), which evaluates to FALSE
        // for 4.29 billion. This causes CPacket::Write to attempt to write 4,294,967,257 bytes!
        // Changing `jb` (0x0F, 0x82) to `jle` (0x0F, 0x8E) ensures negative numbers (<= 10) jump to clean exit.
        static const struct { uintptr_t offset; unsigned char orig[6]; unsigned char patch[6]; const char *name; } s_underflow_patches[] = {
            { 0xCB789F, { 0x0f, 0x82, 0xca, 0x01, 0x00, 0x00 }, { 0x0f, 0x8e, 0xca, 0x01, 0x00, 0x00 }, "SendConnectionData 6A" },
            { 0xCB7D50, { 0x0f, 0x82, 0xcc, 0x03, 0x00, 0x00 }, { 0x0f, 0x8e, 0xcc, 0x03, 0x00, 0x00 }, "SendConnectionData 6B" },
            { 0xCB5E58, { 0x0f, 0x82, 0x8c, 0x08, 0x00, 0x00 }, { 0x0f, 0x8e, 0x8c, 0x08, 0x00, 0x00 }, "SendConnectionData 6C" },
            { 0xCB5FFA, { 0x0f, 0x82, 0xea, 0x06, 0x00, 0x00 }, { 0x0f, 0x8e, 0xea, 0x06, 0x00, 0x00 }, "SendConnectionData 6D" },
            { 0xCB6631, { 0x0f, 0x82, 0xb3, 0x00, 0x00, 0x00 }, { 0x0f, 0x8e, 0xb3, 0x00, 0x00, 0x00 }, "SendConnectionData 6E" },
            { 0xCB7126, { 0x0f, 0x82, 0xb6, 0x00, 0x00, 0x00 }, { 0x0f, 0x8e, 0xb6, 0x00, 0x00, 0x00 }, "SendConnectionData 6F" }
        };

        for (size_t pi = 0; pi < sizeof(s_underflow_patches) / sizeof(s_underflow_patches[0]); ++pi) {
            uintptr_t target_uf = k2_base + s_underflow_patches[pi].offset;
            if (memcmp((const void *)target_uf, s_underflow_patches[pi].patch, 6) == 0) {
                log_msg("Underflow patch %s already active at %p", s_underflow_patches[pi].name, (void *)target_uf);
            } else if (memcmp((const void *)target_uf, s_underflow_patches[pi].orig, 6) == 0) {
                uintptr_t page_start_uf = target_uf & ~((uintptr_t)page_size - 1);
                size_t map_len_uf = (size_t)page_size;
                if ((target_uf + 6) > (page_start_uf + (uintptr_t)page_size)) map_len_uf = (size_t)page_size * 2;
                if (mprotect((void *)page_start_uf, map_len_uf, PROT_READ | PROT_WRITE | PROT_EXEC) == 0) {
                    memcpy((void *)target_uf, s_underflow_patches[pi].patch, 6);
                    mprotect((void *)page_start_uf, map_len_uf, PROT_READ | PROT_EXEC);
                    log_msg("SUCCESS: Patched libk2 %s at %p: jb -> jle!", s_underflow_patches[pi].name, (void *)target_uf);
                }
            } else {
                unsigned char cur_uf[6];
                memcpy(cur_uf, (const void *)target_uf, 6);
                log_msg("Underflow patch %s target bytes at %p mismatch: %02x %02x %02x %02x %02x %02x",
                        s_underflow_patches[pi].name, (void *)target_uf,
                        cur_uf[0], cur_uf[1], cur_uf[2], cur_uf[3], cur_uf[4], cur_uf[5]);
            }
        }

        s_k2_patched = 1;
    }

    if (game_base && !s_game_patched) {
        // Patch 5A: Host check bypass for GAME_CMD_CREATE_GAME at 0x113A1E (6 bytes)
        // In game-x86_64-server.so:
        // 0x113a17: testb $0x1, 0xe8(%rbx)     # check if client is host
        // 0x113a1e: je 1145db                  # jumps to "Client is not a host"
        // NOP-ing this 6-byte jump allows any connected client to initiate GAME_CMD_CREATE_GAME.
        uintptr_t target5a = game_base + 0x113A1E;
        unsigned char expected_orig5a[6] = { 0x0f, 0x84, 0xb7, 0x0b, 0x00, 0x00 };
        unsigned char expected_patch5a[6] = { 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 };

        if (memcmp((const void *)target5a, expected_patch5a, 6) == 0) {
            log_msg("Host check bypass patch already active at %p (base %p)", (void *)target5a, (void *)game_base);
        } else if (memcmp((const void *)target5a, expected_orig5a, 6) == 0) {
            uintptr_t page_start5a = target5a & ~((uintptr_t)page_size - 1);
            size_t map_len5a = (size_t)page_size;
            if ((target5a + 6) > (page_start5a + (uintptr_t)page_size)) {
                map_len5a = (size_t)page_size * 2;
            }

            if (mprotect((void *)page_start5a, map_len5a, PROT_READ | PROT_WRITE | PROT_EXEC) == 0) {
                memcpy((void *)target5a, expected_patch5a, 6);
                mprotect((void *)page_start5a, map_len5a, PROT_READ | PROT_EXEC);
                log_msg("SUCCESS: Patched game-x86_64-server.so host check bypass at %p (base %p): GAME_CMD_CREATE_GAME host check bypassed!",
                        (void *)target5a, (void *)game_base);
            }
        } else {
            unsigned char current5a[6];
            memcpy(current5a, (const void *)target5a, 6);
            log_msg("Host check bypass target bytes at %p mismatch: %02x %02x %02x %02x %02x %02x",
                    (void *)target5a,
                    current5a[0], current5a[1], current5a[2], current5a[3], current5a[4], current5a[5]);
        }

        // Patch 5B: Account verified check bypass for GAME_CMD_CREATE_GAME at 0x113A41 (6 bytes)
        // In game-x86_64-server.so:
        // 0x113a3a: cmpl $0x1, 0xf0(%rbx)     # check account verified level
        // 0x113a41: jg 115856                 # jump to GAME_CMD_CREATE_GAME from client #
        // 0x113aad: lea 0x930b4(%rip),%rsi    # "Non-verified account attempted to create a game."
        // Changing `jg 115856` to unconditional `jmp 115856` (0xe9, 0x10, 0x1e, 0x00, 0x00, 0x90)
        // guarantees match creation succeeds for all player-hosted clients.
        uintptr_t target5 = game_base + 0x113A41;
        unsigned char expected_orig5[6] = { 0x0f, 0x8f, 0x0f, 0x1e, 0x00, 0x00 };
        unsigned char expected_patch5[6] = { 0xe9, 0x10, 0x1e, 0x00, 0x00, 0x90 };

        if (memcmp((const void *)target5, expected_patch5, 6) == 0) {
            log_msg("Account verified bypass patch already active at %p (base %p)", (void *)target5, (void *)game_base);
        } else if (memcmp((const void *)target5, expected_orig5, 6) == 0) {
            uintptr_t page_start5 = target5 & ~((uintptr_t)page_size - 1);
            size_t map_len5 = (size_t)page_size;
            if ((target5 + 6) > (page_start5 + (uintptr_t)page_size)) {
                map_len5 = (size_t)page_size * 2;
            }

            if (mprotect((void *)page_start5, map_len5, PROT_READ | PROT_WRITE | PROT_EXEC) == 0) {
                memcpy((void *)target5, expected_patch5, 6);
                mprotect((void *)page_start5, map_len5, PROT_READ | PROT_EXEC);
                log_msg("SUCCESS: Patched game-x86_64-server.so account verified bypass at %p (base %p): GAME_CMD_CREATE_GAME permitted for all accounts!",
                        (void *)target5, (void *)game_base);
            }
        } else {
            unsigned char current5[6];
            memcpy(current5, (const void *)target5, 6);
            log_msg("Account verified bypass target bytes at %p mismatch: %02x %02x %02x %02x %02x %02x",
                    (void *)target5,
                    current5[0], current5[1], current5[2], current5[3], current5[4], current5[5]);
        }

        s_game_patched = 1;
    }

    if (game_shared_base && !s_game_shared_patched) {
        // Patch 7: CPlayer::AssignAndUpdateUpgrades segfault bypass at 0xC94C24 (2 bytes)
        // In libgame_shared-x86_64-server.so:
        // 0xc94c21: test %rbx, %rbx
        // 0xc94c24: je 0xc94c70 (74 4a)
        // If client connection (%rbx) is non-null, it reads uninitialized upgrade map at 0x8560(%rbx),
        // causing CR2_T = 0x20 segfault at 0xCAA1E0 during client connection.
        // Changing `je c94c70` (74 4a) to `jmp c94c70` (eb 4a) unconditionally jumps to 0xc94c70,
        // which safely updates courier/ward slots and returns cleanly.
        uintptr_t target7 = game_shared_base + 0xC94C24;
        unsigned char expected_orig7[2] = { 0x74, 0x4a };
        unsigned char expected_patch7[2] = { 0xeb, 0x4a };

        if (memcmp((const void *)target7, expected_patch7, 2) == 0) {
            log_msg("AssignAndUpdateUpgrades bypass patch already active at %p (base %p)", (void *)target7, (void *)game_shared_base);
        } else if (memcmp((const void *)target7, expected_orig7, 2) == 0) {
            uintptr_t page_start7 = target7 & ~((uintptr_t)page_size - 1);
            size_t map_len7 = (size_t)page_size;
            if ((target7 + 2) > (page_start7 + (uintptr_t)page_size)) {
                map_len7 = (size_t)page_size * 2;
            }

            if (mprotect((void *)page_start7, map_len7, PROT_READ | PROT_WRITE | PROT_EXEC) == 0) {
                memcpy((void *)target7, expected_patch7, 2);
                mprotect((void *)page_start7, map_len7, PROT_READ | PROT_EXEC);
                log_msg("SUCCESS: Patched libgame_shared-x86_64-server.so AssignAndUpdateUpgrades at %p (base %p): je -> jmp 0xc94c70!",
                        (void *)target7, (void *)game_shared_base);
            }
        } else {
            unsigned char current7[2];
            memcpy(current7, (const void *)target7, 2);
            log_msg("AssignAndUpdateUpgrades bypass target bytes at %p mismatch: %02x %02x",
                    (void *)target7, current7[0], current7[1]);
        }

        // Patch 8: CPlayer::ServerFrameMovement cross-SO CHostServer ABI shift guard at 0xC957E3 (2 bytes)
        // In HoN 5.0.0.0 Linux server, libgame_shared-x86_64-server.so compiled CHostServer with a -0x50
        // field offset mismatch against libk2-x86_64-server.so. When a real human player (m_pClientConnection != NULL)
        // clicks to move, IUnitEntity::GetActivePath() returns ebx != -1 and 0xC957CD reads m_pHostServer+0x1a8
        // (shifted pointer) -> [r14+0x3d0] -> 0xC957F2 `add rbp, [rax]` SIGSEGV!
        // Changing `je 0xc957f5` (74 10) to `jmp 0xc957f5` (eb 10) leaves ebp = 0 (same branch taken when idle),
        // skipping only optional debug path-preview serialization while actual unit movement runs normally.
        uintptr_t target8 = game_shared_base + 0xC957E3;
        unsigned char expected_orig8[2] = { 0x74, 0x10 };
        unsigned char expected_patch8[2] = { 0xeb, 0x10 };

        if (memcmp((const void *)target8, expected_patch8, 2) == 0) {
            log_msg("ServerFrameMovement path-preview ABI guard already active at %p (base %p)", (void *)target8, (void *)game_shared_base);
        } else if (memcmp((const void *)target8, expected_orig8, 2) == 0) {
            uintptr_t page_start8 = target8 & ~((uintptr_t)page_size - 1);
            size_t map_len8 = ((target8 + 2) > (page_start8 + (uintptr_t)page_size)) ? (size_t)page_size * 2 : (size_t)page_size;

            if (mprotect((void *)page_start8, map_len8, PROT_READ | PROT_WRITE | PROT_EXEC) == 0) {
                memcpy((void *)target8, expected_patch8, 2);
                mprotect((void *)page_start8, map_len8, PROT_READ | PROT_EXEC);
                log_msg("SUCCESS: Patched libgame_shared-x86_64-server.so CPlayer::ServerFrameMovement ABI guard at %p (base %p): je -> jmp 0xc957f5!",
                        (void *)target8, (void *)game_shared_base);
            }
        } else {
            unsigned char current8[2];
            memcpy(current8, (const void *)target8, 2);
            log_msg("ServerFrameMovement ABI guard target bytes at %p mismatch: %02x %02x",
                    (void *)target8, current8[0], current8[1]);
        }

        // Patch 9A & 9B: CPlayer::CanAccessAltAvatar unlock at 0xCA2D50 and 0xCA2B90 (6 bytes each)
        // Because Patch 7 bypasses CPlayer::AssignAndUpdateUpgrades (to prevent CClientConnection+0x8560 crash),
        // m_setUpgrades remains empty on Linux dedicated servers, causing CPlayer::SetAltAvatar to reject all
        // Alt Avatar selections on Hero Select. Patching both CanAccessAltAvatar overloads to `mov eax, 1; ret`
        // unlocks all Alt Avatars on Linux dedicated servers matching Windows/Playtest entitlement behavior.
        uintptr_t target9a = game_shared_base + 0xCA2D50;
        unsigned char expected_orig9a[6] = { 0x41, 0x56, 0x53, 0x48, 0x83, 0xec };
        unsigned char expected_patch9[6] = { 0xb8, 0x01, 0x00, 0x00, 0x00, 0xc3 };

        if (memcmp((const void *)target9a, expected_patch9, 6) == 0) {
            log_msg("CanAccessAltAvatar(1-arg) unlock already active at %p (base %p)", (void *)target9a, (void *)game_shared_base);
        } else if (memcmp((const void *)target9a, expected_orig9a, 6) == 0) {
            uintptr_t page_start9a = target9a & ~((uintptr_t)page_size - 1);
            size_t map_len9a = ((target9a + 6) > (page_start9a + (uintptr_t)page_size)) ? (size_t)page_size * 2 : (size_t)page_size;

            if (mprotect((void *)page_start9a, map_len9a, PROT_READ | PROT_WRITE | PROT_EXEC) == 0) {
                memcpy((void *)target9a, expected_patch9, 6);
                mprotect((void *)page_start9a, map_len9a, PROT_READ | PROT_EXEC);
                log_msg("SUCCESS: Patched libgame_shared-x86_64-server.so CanAccessAltAvatar(1-arg) at %p (base %p) -> true!",
                        (void *)target9a, (void *)game_shared_base);
            }
        } else {
            unsigned char current9a[6];
            memcpy(current9a, (const void *)target9a, 6);
            log_msg("CanAccessAltAvatar(1-arg) target bytes at %p mismatch: %02x %02x %02x %02x %02x %02x",
                    (void *)target9a, current9a[0], current9a[1], current9a[2], current9a[3], current9a[4], current9a[5]);
        }

        uintptr_t target9b = game_shared_base + 0xCA2B90;
        unsigned char expected_orig9b[6] = { 0x41, 0x57, 0x41, 0x56, 0x41, 0x54 };

        if (memcmp((const void *)target9b, expected_patch9, 6) == 0) {
            log_msg("CanAccessAltAvatar(2-arg) unlock already active at %p (base %p)", (void *)target9b, (void *)game_shared_base);
        } else if (memcmp((const void *)target9b, expected_orig9b, 6) == 0) {
            uintptr_t page_start9b = target9b & ~((uintptr_t)page_size - 1);
            size_t map_len9b = ((target9b + 6) > (page_start9b + (uintptr_t)page_size)) ? (size_t)page_size * 2 : (size_t)page_size;

            if (mprotect((void *)page_start9b, map_len9b, PROT_READ | PROT_WRITE | PROT_EXEC) == 0) {
                memcpy((void *)target9b, expected_patch9, 6);
                mprotect((void *)page_start9b, map_len9b, PROT_READ | PROT_EXEC);
                log_msg("SUCCESS: Patched libgame_shared-x86_64-server.so CanAccessAltAvatar(2-arg) at %p (base %p) -> true!",
                        (void *)target9b, (void *)game_shared_base);
            }
        } else {
            unsigned char current9b[6];
            memcpy(current9b, (const void *)target9b, 6);
            log_msg("CanAccessAltAvatar(2-arg) target bytes at %p mismatch: %02x %02x %02x %02x %02x %02x",
                    (void *)target9b, current9b[0], current9b[1], current9b[2], current9b[3], current9b[4], current9b[5]);
        }

        s_game_shared_patched = 1;
    }

    return (s_k2_patched && s_game_patched && s_game_shared_patched);
}

__attribute__((constructor))
static void shim_init(void) {
    real_recvfrom = (ssize_t (*)(int, void *, size_t, int, struct sockaddr *, socklen_t *))
                    dlsym(RTLD_NEXT, "recvfrom");
    init_config();
    log_msg("libhon-ping-shim loaded into PID %d (real_recvfrom = %p)", getpid(), (void *)real_recvfrom);
    apply_engine_patches();
}

ssize_t recvfrom(int sockfd, void *buf, size_t len, int flags,
                 struct sockaddr *src_addr, socklen_t *addrlen)
{
    if (!real_recvfrom) {
        real_recvfrom = (ssize_t (*)(int, void *, size_t, int, struct sockaddr *, socklen_t *))
                        dlsym(RTLD_NEXT, "recvfrom");
    }
    apply_engine_patches();

    ssize_t n = real_recvfrom(sockfd, buf, len, flags, src_addr, addrlen);
    if (n <= 0) return n;

    unsigned char *p = (unsigned char *)buf;
    unsigned char challenge[2] = {0, 0};
    int is_ping = 0;

    // Standard watermarked HoN ping probe (strictly 46 bytes)
    // Bytes 0..39: watermark
    // Byte 40..41: sequence (0x00, 0x00)
    // Byte 42: 0x01 (UnreliableFlag)
    // Byte 43: 0xCA (PingMarker)
    // Bytes 44..45: challenge
    if (n == 46 && p[40] == 0x00 && p[41] == 0x00 && p[42] == 0x01 && p[43] == 0xCA) {
        challenge[0] = p[44];
        challenge[1] = p[45];
        is_ping = 1;
    }
    // [L2-P0-03] Raw unwatermarked ping probe (strictly 6 bytes: 00 00 01 CA <chal0> <chal1>)
    // Never match n > 6 gameplay UDP packets that happen to carry 0x01 0xCA at [2..3].
    else if (hon_ping_is_exact_6byte_probe(p, n)) {
        challenge[0] = p[4];
        challenge[1] = p[5];
        is_ping = 1;
    }

    int is_loopback_sender = 0;
    if (src_addr && addrlen && *addrlen > 0) {
        if (src_addr->sa_family == AF_INET && (size_t)(*addrlen) >= sizeof(struct sockaddr_in)) {
            const struct sockaddr_in *sin_chk = (const struct sockaddr_in *)src_addr;
            is_loopback_sender = hon_ping_is_loopback_ipv4_host(ntohl(sin_chk->sin_addr.s_addr));
        } else if (src_addr->sa_family == AF_INET6 && (size_t)(*addrlen) >= sizeof(struct sockaddr_in6)) {
            const struct sockaddr_in6 *sin6_chk = (const struct sockaddr_in6 *)src_addr;
            if (IN6_IS_ADDR_LOOPBACK(&sin6_chk->sin6_addr)) {
                is_loopback_sender = 1;
            } else if (IN6_IS_ADDR_V4MAPPED(&sin6_chk->sin6_addr)) {
                uint32_t mapped_be = 0;
                memcpy(&mapped_be, &sin6_chk->sin6_addr.s6_addr[12], sizeof(uint32_t));
                is_loopback_sender = hon_ping_is_loopback_ipv4_host(ntohl(mapped_be));
            }
        }
    }

    if (!is_ping && n >= 5) {
        // [L2-P0-03] Restrict "map:" match state updates strictly to 127.0.0.1 loopback
        const char *map_ptr = (const char *)memmem(buf, (size_t)n, "map:", 4);
        if (map_ptr != NULL && !is_loopback_sender) {
            static unsigned int s_rejected_map_spoofs = 0;
            if (++s_rejected_map_spoofs <= 10 || (s_rejected_map_spoofs % 100) == 0) {
                log_msg("SECURITY: Rejected non-loopback 'map:' state update attempt (count=%u, len=%zd)",
                        s_rejected_map_spoofs, n);
            }
            map_ptr = NULL;
        }
        if (map_ptr != NULL) {
            g_is_game_active = 1;
            const char *buf_start = (const char *)buf;
            const char *buf_end = buf_start + n;

            // Extract optional null-terminated gameName immediately preceding "map:"
            if (map_ptr >= buf_start + 2 && map_ptr[-1] == '\0') {
                const char *gn_end = map_ptr - 1;
                const char *gn = gn_end;
                while (gn > buf_start && (unsigned char)gn[-1] >= 0x20 && (unsigned char)gn[-1] < 0x7F && (gn_end - gn) < 63) {
                    gn--;
                }
                size_t gn_len = (size_t)(gn_end - gn);
                if (gn_len > 0) {
                    if (gn_len >= sizeof(g_match_description)) gn_len = sizeof(g_match_description) - 1;
                    memcpy(g_match_description, gn, gn_len);
                    g_match_description[gn_len] = '\0';
                }
            }
            if (strlen(g_match_description) == 0) {
                snprintf(g_match_description, sizeof(g_match_description), "%s Match", g_server_name);
            }

            // Copy bounded options slice starting at "map:" for safe token parsing
            char opts[512];
            size_t rem = (size_t)(buf_end - map_ptr);
            if (rem >= sizeof(opts)) rem = sizeof(opts) - 1;
            size_t k_opt = 0;
            while (k_opt < rem && map_ptr[k_opt] != '\0') {
                opts[k_opt] = map_ptr[k_opt];
                k_opt++;
            }
            opts[k_opt] = '\0';

            // Parse map:<name>
            const char *m_val = opts + 4;
            size_t m_len = 0;
            while (m_val[m_len] != '\0' && m_val[m_len] != ' ' && m_val[m_len] < 0x7F && m_len < sizeof(g_map_name) - 1) {
                m_len++;
            }
            if (m_len > 0) {
                memcpy(g_map_name, m_val, m_len);
                g_map_name[m_len] = '\0';
            }

            // Parse mode:<mode>
            const char *mode_tok = strstr(opts, "mode:");
            if (mode_tok != NULL) {
                const char *md_val = mode_tok + 5;
                size_t md_len = 0;
                while (md_val[md_len] != '\0' && md_val[md_len] != ' ' && md_val[md_len] < 0x7F && md_len < sizeof(g_match_type) - 1) {
                    md_len++;
                }
                if (md_len > 0) {
                    memcpy(g_match_type, md_val, md_len);
                    g_match_type[md_len] = '\0';
                }
            }

            // Parse teamsize:<1..5>
            const char *ts_tok = strstr(opts, "teamsize:");
            if (ts_tok != NULL) {
                int ts = atoi(ts_tok + 9);
                if (ts >= 1 && ts <= 5) {
                    g_team_size = ts;
                }
            }

            log_msg("MATCH CREATION DETECTED on port %d: gameName='%s', map='%s', mode='%s', teamSize=%d",
                    g_svr_port, g_match_description, g_map_name, g_match_type, g_team_size);
        }
    }

    if (is_ping && src_addr && addrlen && *addrlen > 0) {
        char src_ip[INET6_ADDRSTRLEN] = {0};
        int src_port = 0;
        if (src_addr->sa_family == AF_INET) {
            struct sockaddr_in *sin = (struct sockaddr_in *)src_addr;
            inet_ntop(AF_INET, &sin->sin_addr, src_ip, sizeof(src_ip));
            src_port = ntohs(sin->sin_port);
        }

        log_msg("PING received from %s:%d (%zd bytes, challenge 0x%02X%02X)",
                src_ip, src_port, n, challenge[0], challenge[1]);

        unsigned char pong[512];
        int pos = 0;

        // 3-byte K2 header
        pong[pos++] = 0x00;
        pong[pos++] = 0x00;
        pong[pos++] = 0x01; // UnreliableFlag

        // Pong opcode
        pong[pos++] = 0x66;

        // Echo challenge
        pong[pos++] = challenge[0];
        pong[pos++] = challenge[1];

        // 1. Server name (null-terminated UTF-8)
        size_t name_len = strlen(g_server_name);
        memcpy(&pong[pos], g_server_name, name_len + 1);
        pos += (int)(name_len + 1);

        int active = check_game_active();

        // 2. num_players
        pong[pos++] = (unsigned char)(active ? 0x01 : 0x00);

        // 3. max_players (2 * teamSize when active, 10 when idle)
        pong[pos++] = (unsigned char)(active ? (g_team_size * 2) : 0x0A);

        // 4. mapName (K2 FUN_1808cdc20 local_510 -> GameListAdd args[19])
        // Empty "\0" for idle servers routes PONG to GetServerList; non-empty mapName routes PONG to GetGameList!
        if (active) {
            size_t map_len = strlen(g_map_name);
            memcpy(&pong[pos], g_map_name, map_len + 1);
            pos += (int)(map_len + 1);
        } else {
            pong[pos++] = 0x00;
        }

        // 5. version (null-terminated UTF-8 "5.0.0.0")
        size_t ver_len = strlen(g_version);
        memcpy(&pong[pos], g_version, ver_len + 1);
        pos += (int)(ver_len + 1);

        // 6..9: minTier=0, official=1, noStats=0, privateGame=0
        pong[pos++] = 0x00;
        pong[pos++] = 0x01;
        pong[pos++] = 0x00;
        pong[pos++] = 0x00;

        // 10. location (null-terminated UTF-8 "USE\0")
        size_t loc_len = strlen(g_server_location);
        memcpy(&pong[pos], g_server_location, loc_len + 1);
        pos += (int)(loc_len + 1);

        // 11. gameName (K2 FUN_1808cdc20 local_560 -> GameListAdd args[2])
        if (active) {
            const char *desc = (strlen(g_match_description) > 0) ? g_match_description : "Hosted Game";
            size_t desc_len = strlen(desc);
            memcpy(&pong[pos], desc, desc_len + 1);
            pos += (int)(desc_len + 1);
        } else {
            pong[pos++] = 0x00;
        }

        // 12. gameMode (K2 FUN_1808cdc20 local_4d8 -> GameListAdd args[5], e.g. "normal" or "botmatch")
        size_t mode_len = strlen(g_match_type);
        memcpy(&pong[pos], g_match_type, mode_len + 1);
        pos += (int)(mode_len + 1);

        // 13. teamSize (K2 FUN_1808cdc20 bVar17 -> GameListAdd args[4] formatted as "%dv%d")
        pong[pos++] = (unsigned char)(active ? g_team_size : 0x00);

        // 14..18: 10 trailing zero bytes (gameOptions int32 [4B], serverLoad [1B], minPSR [2B], maxPSR [2B], extraByte [1B])
        for (int k = 0; k < 10; k++) {
            pong[pos++] = 0x00;
        }

        ssize_t sent = sendto(sockfd, pong, pos, 0, src_addr, *addrlen);
        log_msg("PONG sent to %s:%d (%d bytes, sendto result: %zd)",
                src_ip, src_port, pos, sent);

        // Swallow ping packet so native engine does not see unauthenticated probe
        errno = EAGAIN;
        return -1;
    }

    return n;
}

static void (*real_cpacket_write)(void *this_ptr, const void *buf, unsigned int len) = NULL;

void _ZN7CPacket5WriteEPKcj(void *this_ptr, const void *buf, unsigned int len) {
    if (!real_cpacket_write) {
        real_cpacket_write = (void (*)(void *, const void *, unsigned int))dlsym(RTLD_NEXT, "_ZN7CPacket5WriteEPKcj");
        if (!real_cpacket_write) {
            log_msg("FATAL: Failed to resolve real _ZN7CPacket5WriteEPKcj");
            return;
        }
    }

    if (len > 4096) {
        void *caller = __builtin_return_address(0);
        log_msg("INTERCEPTED INVALID CPacket::Write: this=%p, buf=%p, len=%u, caller=%p! Suppressing write to prevent buffer overflow.",
                this_ptr, buf, len, caller);
        return;
    }

    real_cpacket_write(this_ptr, buf, len);
}

