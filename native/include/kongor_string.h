// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#ifndef KONGOR_STRING_H
#define KONGOR_STRING_H

/**
 * ============================================================================
 * Project Kongor - High-Performance Length-Delimited String Slice Library
 * Architecture: Adapted from RAD Debugger (Ryan Fleury / Allen Webster)
 * ============================================================================
 * 
 * CORE RULES & PHILOSOPHY:
 * 1. Zero-Allocation Slices: Substrings, path stripping, and tokenizing take
 *    zero allocations by referencing existing buffers via (pointer, size).
 * 2. Sentinel Null-Termination: Every String8 allocated on a KongorArena
 *    allocates size + 1 bytes and guarantees str[size] == '\0'. This maintains
 *    100% compatibility with native C-APIs (VFS::Open, CreateFileA) without
 *    breaking length-delimited guarantees.
 * 3. String8List Joins: Concatenation builds a linked list on a scratch arena
 *    and joins once into a single contiguous memory block.
 * ============================================================================
 */

#include "kongor_arena.h"
#include <stdint.h>
#include <stddef.h>
#include <stdbool.h>
#include <string.h>
#include <stdio.h>
#include <stdarg.h>
#include <assert.h>

#ifdef __cplusplus
#include <string_view>
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct String8 {
    uint8_t *str;
    size_t size;

#ifdef __cplusplus
    // Seamless C++17 string_view interoperability
    operator std::string_view() const {
        return std::string_view((const char *)str, size);
    }
#endif
} String8;

typedef struct String8Node {
    struct String8Node *next;
    String8 string;
} String8Node;

typedef struct String8List {
    String8Node *first;
    String8Node *last;
    size_t node_count;
    size_t total_size;
} String8List;

/**
 * Creates a compile-time String8 literal with zero runtime strlen() overhead.
 */
#ifdef __cplusplus
#define str8_lit(s) String8{ (uint8_t *)(s), sizeof(s) - 1 }
#else
#define str8_lit(s) (String8){ (uint8_t *)(s), sizeof(s) - 1 }
#endif

static inline String8 str8(uint8_t *str, size_t size) {
    String8 result;
    result.str = str;
    result.size = size;
    return result;
}

static inline String8 str8_zero(void) {
    String8 result = { NULL, 0 };
    return result;
}

static inline String8 str8_from_cstr(const char *cstr) {
    if (!cstr) return str8_zero();
    return str8((uint8_t *)cstr, strlen(cstr));
}

/**
 * Allocates a String8 on a KongorArena with a guaranteed sentinel null-terminator.
 */
static inline String8 str8_alloc(KongorArena *arena, size_t size) {
    String8 result;
    result.size = size;
    // Allocate size + 1 for hidden sentinel null-terminator
    result.str = (uint8_t *)kongor_arena_alloc(arena, size + 1);
    result.str[size] = '\0';
    return result;
}

static inline String8 str8_push_copy(KongorArena *arena, String8 s) {
    if (s.size == 0 || s.str == NULL) {
        return str8_zero();
    }
    String8 result = str8_alloc(arena, s.size);
    memcpy(result.str, s.str, s.size);
    return result;
}

/**
 * Safe C-String extraction:
 * If the string already has a sentinel '\0' at str[size], returns it directly (zero-copy).
 * Otherwise, copies the slice to the arena and appends '\0'.
 */
static inline const char *str8_to_cstr(KongorArena *arena, String8 s) {
    if (s.str == NULL || s.size == 0) {
        return "";
    }
    // Check if memory has a safe null terminator at s.str[s.size]
    // (safe if allocated via str8_alloc)
    if (s.str[s.size] == '\0') {
        return (const char *)s.str;
    }
    // Sub-slice without trailing null -> duplicate onto arena
    String8 copy = str8_push_copy(arena, s);
    return (const char *)copy.str;
}

/**
 * Formats a string directly onto the arena with sentinel null-termination.
 */
static inline String8 str8_pushf(KongorArena *arena, const char *fmt, ...) {
    va_list args;
    va_start(args, fmt);
    va_list args_copy;
    va_copy(args_copy, args);

    int needed = vsnprintf(NULL, 0, fmt, args_copy);
    va_end(args_copy);

    if (needed < 0) {
        va_end(args);
        return str8_zero();
    }

    String8 result = str8_alloc(arena, (size_t)needed);
    vsnprintf((char *)result.str, result.size + 1, fmt, args);
    va_end(args);
    return result;
}

/* ============================================================================
 * Zero-Allocation Substring & Path Operations
 * ============================================================================ */

static inline String8 str8_substr(String8 s, size_t start, size_t end) {
    if (start > s.size) start = s.size;
    if (end > s.size)   end = s.size;
    if (start > end)    start = end;
    return str8(s.str + start, end - start);
}

static inline String8 str8_skip(String8 s, size_t count) {
    return str8_substr(s, count, s.size);
}

static inline String8 str8_chop(String8 s, size_t count) {
    if (count > s.size) count = s.size;
    return str8_substr(s, 0, s.size - count);
}

static inline bool str8_equal(String8 a, String8 b) {
    if (a.size != b.size) return false;
    if (a.size == 0) return true;
    return memcmp(a.str, b.str, a.size) == 0;
}

static inline bool str8_equal_case_insensitive(String8 a, String8 b) {
    if (a.size != b.size) return false;
    for (size_t i = 0; i < a.size; i++) {
        uint8_t ca = a.str[i];
        uint8_t cb = b.str[i];
        if (ca >= 'A' && ca <= 'Z') ca += ('a' - 'A');
        if (cb >= 'A' && cb <= 'Z') cb += ('a' - 'A');
        if (ca != cb) return false;
    }
    return true;
}

/**
 * Returns the path stripped of its directory (e.g. "ui/fe2/main_v2.interface" -> "main_v2.interface").
 */
static inline String8 str8_skip_last_slash(String8 path) {
    for (size_t i = path.size; i > 0; i--) {
        uint8_t c = path.str[i - 1];
        if (c == '/' || c == '\\') {
            return str8_substr(path, i, path.size);
        }
    }
    return path;
}

/**
 * Returns the directory portion of a path (e.g. "ui/fe2/main_v2.interface" -> "ui/fe2").
 */
static inline String8 str8_chop_last_slash(String8 path) {
    for (size_t i = path.size; i > 0; i--) {
        uint8_t c = path.str[i - 1];
        if (c == '/' || c == '\\') {
            return str8_substr(path, 0, i - 1);
        }
    }
    return str8_zero();
}

/**
 * Returns file extension including dot (e.g. "main_v2.interface" -> ".interface").
 */
static inline String8 str8_get_extension(String8 path) {
    for (size_t i = path.size; i > 0; i--) {
        uint8_t c = path.str[i - 1];
        if (c == '.') {
            return str8_substr(path, i - 1, path.size);
        }
        if (c == '/' || c == '\\') break;
    }
    return str8_zero();
}

/* ============================================================================
 * String8List Construction & Joining
 * ============================================================================ */

static inline void str8_list_push_node(String8List *list, String8Node *node) {
    if (list->first == NULL) {
        list->first = node;
        list->last = node;
    } else {
        list->last->next = node;
        list->last = node;
    }
    list->node_count++;
    list->total_size += node->string.size;
}

static inline void str8_list_push(KongorArena *arena, String8List *list, String8 s) {
    String8Node *node = kongor_arena_push_struct(arena, String8Node);
    node->string = s;
    node->next = NULL;
    str8_list_push_node(list, node);
}

static inline void str8_list_pushf(KongorArena *arena, String8List *list, const char *fmt, ...) {
    va_list args;
    va_start(args, fmt);
    va_list args_copy;
    va_copy(args_copy, args);
    int needed = vsnprintf(NULL, 0, fmt, args_copy);
    va_end(args_copy);
    if (needed > 0) {
        String8 s = str8_alloc(arena, (size_t)needed);
        vsnprintf((char *)s.str, s.size + 1, fmt, args);
        str8_list_push(arena, list, s);
    }
    va_end(args);
}

static inline String8 str8_list_join(KongorArena *arena, String8List *list, String8 separator) {
    if (list->node_count == 0) return str8_zero();

    size_t sep_size = (list->node_count > 1) ? (separator.size * (list->node_count - 1)) : 0;
    size_t total_size = list->total_size + sep_size;

    String8 result = str8_alloc(arena, total_size);
    uint8_t *cursor = result.str;

    for (String8Node *node = list->first; node != NULL; node = node->next) {
        if (node->string.size > 0 && node->string.str != NULL) {
            memcpy(cursor, node->string.str, node->string.size);
            cursor += node->string.size;
        }
        if (node->next != NULL && separator.size > 0 && separator.str != NULL) {
            memcpy(cursor, separator.str, separator.size);
            cursor += separator.size;
        }
    }

    assert((size_t)(cursor - result.str) == total_size);
    return result;
}

#ifdef __cplusplus
}
#endif

#endif // KONGOR_STRING_H
