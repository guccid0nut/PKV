// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#ifndef KONGOR_ARENA_H
#define KONGOR_ARENA_H

/**
 * ============================================================================
 * Project Kongor - High-Performance Memory Arena Allocator
 * Architecture: Adapted from RAD Debugger (Ryan Fleury / Allen Webster)
 * ============================================================================
 * 
 * CORE RULES & PHILOSOPHY:
 * 1. Zero Heap Churn: Eliminates malloc/free, new/delete, and heap fragmentation
 *    in hot native hooks, DLL proxies, packet handlers, and native tools.
 * 2. Instant O(1) Allocation: Allocating memory is a single pointer bump.
 * 3. Instant O(1) Cleanup: Scopes use arena_temp_begin/end to restore the mark
 *    at the end of a tick or packet decode in 0 nanoseconds.
 * 
 * CRITICAL ABI BOUNDARY OWNERSHIP RULE:
 * ----------------------------------------------------------------------------
 * NEVER return or pass an arena-allocated pointer to a K2 Engine subsystem
 * (e.g. k2_x64.dll, cgame_x64.dll) that expects heap ownership (where K2 CRT
 * will later call delete or K2_Free).
 * 
 * Arena memory is strictly reserved for:
 *   - Internal proxy DLL hook state
 *   - Intermediate packet decode buffers
 *   - Temporary string/telemetry formatting
 *   - Standalone native dev tools (kongor-model-viewer, kongor-physics-sandbox)
 * 
 * If memory must be transferred to K2 engine ownership, allocate using K2's
 * native engine allocator (e.g. K2_Alloc).
 * ============================================================================
 */

#ifdef _WIN32
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#else
#include <sys/mman.h>
#include <unistd.h>
#endif

#include <stdint.h>
#include <stddef.h>
#include <stdbool.h>
#include <string.h>
#include <assert.h>

#ifdef __cplusplus
extern "C" {
#endif

#define KONGOR_ARENA_DEFAULT_RESERVE (64 * 1024 * 1024) // 64 MB virtual address space
#define KONGOR_ARENA_COMMIT_CHUNK    (64 * 1024)        // 64 KB commit chunk
#define KONGOR_ARENA_DEFAULT_ALIGN   16                 // 16-byte alignment (SIMD/x64 friendly)

typedef struct KongorArena {
    uint8_t *memory;
    size_t capacity;
    size_t pos;
    size_t commit_pos;
} KongorArena;

typedef struct KongorArenaTemp {
    KongorArena *arena;
    size_t pos;
} KongorArenaTemp;

/**
 * Creates a virtual memory arena with reserved address space.
 * Only commits pages into physical memory on-demand.
 */
static inline KongorArena kongor_arena_create(size_t reserve_size) {
    KongorArena arena;
    memset(&arena, 0, sizeof(arena));

    if (reserve_size == 0) {
        reserve_size = KONGOR_ARENA_DEFAULT_RESERVE;
    }

#ifdef _WIN32
    arena.memory = (uint8_t *)VirtualAlloc(NULL, reserve_size, MEM_RESERVE, PAGE_NOACCESS);
#else
    arena.memory = (uint8_t *)mmap(NULL, reserve_size, PROT_NONE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
#endif

    assert(arena.memory != NULL && "VirtualAlloc failed to reserve address space for KongorArena!");
    arena.capacity = reserve_size;
    arena.pos = 0;
    arena.commit_pos = 0;
    return arena;
}

/**
 * Destroys the arena, releasing all virtual address space back to the OS.
 */
static inline void kongor_arena_destroy(KongorArena *arena) {
    if (arena && arena->memory) {
#ifdef _WIN32
        VirtualFree(arena->memory, 0, MEM_RELEASE);
#else
        munmap(arena->memory, arena->capacity);
#endif
        arena->memory = NULL;
        arena->capacity = 0;
        arena->pos = 0;
        arena->commit_pos = 0;
    }
}

/**
 * Allocates contiguous aligned memory from the arena.
 */
static inline void *kongor_arena_alloc_align(KongorArena *arena, size_t size, size_t alignment) {
    assert(arena != NULL && arena->memory != NULL);
    assert((alignment & (alignment - 1)) == 0 && "Alignment must be a power of 2!");

    size_t current_addr = (size_t)(arena->memory + arena->pos);
    size_t aligned_addr = (current_addr + (alignment - 1)) & ~(alignment - 1);
    size_t align_offset = aligned_addr - current_addr;
    size_t total_size = align_offset + size;

    assert(arena->pos + total_size <= arena->capacity && "KongorArena out of reserved capacity!");

    size_t new_pos = arena->pos + total_size;

    // Commit new physical pages if necessary
    if (new_pos > arena->commit_pos) {
        size_t commit_target = (new_pos + KONGOR_ARENA_COMMIT_CHUNK - 1) & ~(KONGOR_ARENA_COMMIT_CHUNK - 1);
        if (commit_target > arena->capacity) {
            commit_target = arena->capacity;
        }
        size_t bytes_to_commit = commit_target - arena->commit_pos;

#ifdef _WIN32
        void *committed = VirtualAlloc(arena->memory + arena->commit_pos, bytes_to_commit, MEM_COMMIT, PAGE_READWRITE);
        assert(committed != NULL && "VirtualAlloc failed to commit physical memory for KongorArena!");
#else
        int res = mprotect(arena->memory + arena->commit_pos, bytes_to_commit, PROT_READ | PROT_WRITE);
        assert(res == 0 && "mprotect failed to commit physical memory for KongorArena!");
#endif
        arena->commit_pos = commit_target;
    }

    void *ptr = (void *)aligned_addr;
    arena->pos = new_pos;
    return ptr;
}

static inline void *kongor_arena_alloc(KongorArena *arena, size_t size) {
    return kongor_arena_alloc_align(arena, size, KONGOR_ARENA_DEFAULT_ALIGN);
}

static inline void *kongor_arena_push_zero(KongorArena *arena, size_t size) {
    void *ptr = kongor_arena_alloc(arena, size);
    memset(ptr, 0, size);
    return ptr;
}

#ifdef __cplusplus
#define kongor_alignof(type) alignof(type)
#else
#define kongor_alignof(type) _Alignof(type)
#endif

#define kongor_arena_push_struct(arena, type) \
    ((type *)kongor_arena_push_zero((arena), sizeof(type)))

#define kongor_arena_push_array(arena, type, count) \
    ((type *)kongor_arena_alloc_align((arena), sizeof(type) * (count), kongor_alignof(type) > KONGOR_ARENA_DEFAULT_ALIGN ? kongor_alignof(type) : KONGOR_ARENA_DEFAULT_ALIGN))

#define kongor_arena_push_array_zero(arena, type, count) \
    ((type *)memset(kongor_arena_push_array((arena), type, (count)), 0, sizeof(type) * (count)))

/**
 * Begins a temporary scratchpad scope.
 */
static inline KongorArenaTemp kongor_arena_temp_begin(KongorArena *arena) {
    KongorArenaTemp temp;
    temp.arena = arena;
    temp.pos = arena->pos;
    return temp;
}

/**
 * Ends a temporary scratchpad scope, rewinding the bump pointer back to the marked position.
 * In debug builds, rewound memory is poisoned with 0xDD to immediately crash on Use-After-Rewind.
 */
static inline void kongor_arena_temp_end(KongorArenaTemp temp) {
    assert(temp.arena != NULL);
    assert(temp.pos <= temp.arena->pos);

#if defined(_DEBUG) || !defined(NDEBUG)
    // Poison released memory with 0xDD to catch Use-After-Rewind bugs
    if (temp.arena->memory != NULL && temp.arena->pos > temp.pos) {
        memset(temp.arena->memory + temp.pos, 0xDD, temp.arena->pos - temp.pos);
    }
#endif

    temp.arena->pos = temp.pos;
}

/**
 * Checks whether a given pointer falls within the arena's memory bounds.
 * Useful for debugging assertions at K2 engine ABI boundary crossings.
 */
#define KONGOR_IS_ARENA_PTR(arena, ptr) \
    ((arena) != NULL && (arena)->memory != NULL && \
     (uint8_t *)(ptr) >= (arena)->memory && \
     (uint8_t *)(ptr) < ((arena)->memory + (arena)->capacity))

/**
 * Resets the entire arena back to offset zero (retaining committed pages).
 */
static inline void kongor_arena_clear(KongorArena *arena) {
    if (arena) {
        arena->pos = 0;
    }
}

/**
 * Thread-Local Dual-Arena Architecture:
 * Provides standard Persistent and Scratch arenas without manual tracking.
 */
#ifdef _WIN32
static inline KongorArena *kongor_get_scratch_arena(void) {
    static __declspec(thread) KongorArena scratch_arena = {0};
    static __declspec(thread) bool initialized = false;
    if (!initialized) {
        scratch_arena = kongor_arena_create(32 * 1024 * 1024); // 32 MB scratch
        initialized = true;
    }
    return &scratch_arena;
}

static inline KongorArena *kongor_get_persistent_arena(void) {
    static KongorArena persistent_arena = {0};
    static bool initialized = false;
    if (!initialized) {
        persistent_arena = kongor_arena_create(64 * 1024 * 1024); // 64 MB persistent
        initialized = true;
    }
    return &persistent_arena;
}
#endif

#ifdef __cplusplus
}
#endif

#endif // KONGOR_ARENA_H
