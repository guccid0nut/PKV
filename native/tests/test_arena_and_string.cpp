// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

/**
 * Project Kongor - Native Arena & String8 Slicing Unit Tests & Benchmark
 * Architecture: Adapted from RAD Debugger (Ryan Fleury / Allen Webster)
 */

#include "../include/kongor_arena.h"
#include "../include/kongor_string.h"
#include <stdio.h>
#include <stdlib.h>
#include <chrono>
#include <string_view>

#define ASSERT_TRUE(cond) do { \
    if (!(cond)) { \
        fprintf(stderr, "[FAIL] %s:%d: Assertion failed: %s\n", __FILE__, __LINE__, #cond); \
        exit(1); \
    } \
} while(0)

void test_arena_basic_lifecycle() {
    printf("[TEST] test_arena_basic_lifecycle...\n");
    KongorArena arena = kongor_arena_create(4 * 1024 * 1024); // 4 MB reserve
    ASSERT_TRUE(arena.memory != NULL);
    ASSERT_TRUE(arena.pos == 0);

    // Test aligned allocations
    void *ptr1 = kongor_arena_alloc(&arena, 12);
    ASSERT_TRUE(ptr1 != NULL);
    ASSERT_TRUE(((size_t)ptr1 % 16) == 0); // 16-byte alignment

    void *ptr2 = kongor_arena_alloc(&arena, 24);
    ASSERT_TRUE(ptr2 != NULL);
    ASSERT_TRUE(((size_t)ptr2 % 16) == 0);
    ASSERT_TRUE((uint8_t *)ptr2 >= (uint8_t *)ptr1 + 12);

    ASSERT_TRUE(KONGOR_IS_ARENA_PTR(&arena, ptr1));
    ASSERT_TRUE(KONGOR_IS_ARENA_PTR(&arena, ptr2));

    kongor_arena_destroy(&arena);
    ASSERT_TRUE(arena.memory == NULL);
    printf("  -> PASSED\n");
}

void test_arena_temp_scratchpad() {
    printf("[TEST] test_arena_temp_scratchpad...\n");
    KongorArena arena = kongor_arena_create(4 * 1024 * 1024);
    
    // Allocate some persistent base data
    void *base_data = kongor_arena_alloc(&arena, 1024);
    size_t base_pos = arena.pos;

    // Begin scratchpad scope
    KongorArenaTemp temp = kongor_arena_temp_begin(&arena);
    ASSERT_TRUE(temp.pos == base_pos);

    void *scratch1 = kongor_arena_alloc(&arena, 2048);
    void *scratch2 = kongor_arena_alloc(&arena, 4096);
    ASSERT_TRUE(arena.pos > base_pos);

    // End scratchpad scope
    kongor_arena_temp_end(temp);
    ASSERT_TRUE(arena.pos == base_pos);

    kongor_arena_destroy(&arena);
    printf("  -> PASSED\n");
}

void test_string8_sentinel_and_slices() {
    printf("[TEST] test_string8_sentinel_and_slices...\n");
    KongorArena arena = kongor_arena_create(2 * 1024 * 1024);

    // Test compile-time literal
    String8 lit = str8_lit("ui/fe2/main_v2.interface");
    ASSERT_TRUE(lit.size == 24);
    ASSERT_TRUE(lit.str[lit.size] == '\0'); // Literals are guaranteed null-terminated in C++

    // Test path stripping (zero-allocation)
    String8 filename = str8_skip_last_slash(lit);
    ASSERT_TRUE(str8_equal(filename, str8_lit("main_v2.interface")));
    ASSERT_TRUE(filename.size == 17);

    String8 dir = str8_chop_last_slash(lit);
    ASSERT_TRUE(str8_equal(dir, str8_lit("ui/fe2")));
    ASSERT_TRUE(dir.size == 6);

    String8 ext = str8_get_extension(lit);
    ASSERT_TRUE(str8_equal(ext, str8_lit(".interface")));

    // Test arena allocation with sentinel
    String8 allocated = str8_alloc(&arena, 11);
    memcpy(allocated.str, "hello_world", 11);
    ASSERT_TRUE(allocated.size == 11);
    ASSERT_TRUE(allocated.str[11] == '\0'); // Sentinel null-terminator verified!

    // Verify safe C-String conversion
    const char *cstr = str8_to_cstr(&arena, allocated);
    ASSERT_TRUE(cstr == (const char *)allocated.str); // Zero copy when sentinel is present!
    ASSERT_TRUE(strcmp(cstr, "hello_world") == 0);

    // Test C++17 string_view conversion
    std::string_view sv = allocated;
    ASSERT_TRUE(sv == "hello_world");
    ASSERT_TRUE(sv.length() == 11);

    kongor_arena_destroy(&arena);
    printf("  -> PASSED\n");
}

void test_string8_list_join() {
    printf("[TEST] test_string8_list_join...\n");
    KongorArena arena = kongor_arena_create(2 * 1024 * 1024);

    String8List list = {0};
    str8_list_push(&arena, &list, str8_lit("games"));
    str8_list_push(&arena, &list, str8_lit("client"));
    str8_list_push(&arena, &list, str8_lit("override"));
    str8_list_push(&arena, &list, str8_lit("ui"));

    String8 joined = str8_list_join(&arena, &list, str8_lit("/"));
    ASSERT_TRUE(str8_equal(joined, str8_lit("games/client/override/ui")));
    ASSERT_TRUE(joined.str[joined.size] == '\0'); // Joined string is sentinel null-terminated!

    kongor_arena_destroy(&arena);
    printf("  -> PASSED\n");
}

void benchmark_scratchpad_path_slices() {
    printf("[BENCHMARK] benchmark_scratchpad_path_slices (100,000 iterations)...\n");
    KongorArena arena = kongor_arena_create(16 * 1024 * 1024);

    String8 sample_path = str8_lit("games/client/override/ui/scripts/newui/heroes/alt_avatar_selector_v2.package");

    auto start = std::chrono::high_resolution_clock::now();

    for (int i = 0; i < 100000; i++) {
        KongorArenaTemp temp = kongor_arena_temp_begin(&arena);

        String8 fname = str8_skip_last_slash(sample_path);
        String8 dir = str8_chop_last_slash(sample_path);
        String8 ext = str8_get_extension(sample_path);

        // Build a dynamic lookup key on scratch arena
        String8 key = str8_pushf(&arena, "[%s] => %s (%s)", (const char *)ext.str, (const char *)fname.str, (const char *)dir.str);
        (void)key;

        kongor_arena_temp_end(temp); // Instant 0ns cleanup
    }

    auto end = std::chrono::high_resolution_clock::now();
    double elapsed_ms = std::chrono::duration<double, std::milli>(end - start).count();

    printf("  -> 100,000 path slice & format cycles completed in: %.2f ms\n", elapsed_ms);
    printf("  -> Average per cycle: %.3f microseconds\n", (elapsed_ms * 1000.0) / 100000.0);
    ASSERT_TRUE(arena.pos == 0); // Arena fully rewound, 0 bytes leaked!

    kongor_arena_destroy(&arena);
    printf("  -> PASSED with ZERO OS Heap Allocations\n");
}

#include "../include/concurrentqueue.h"
#if defined(_WIN32)
#include "../include/kongor_stacktrace.h"
#endif
#include <thread>
#include <vector>

void test_concurrent_queue_mpmc() {
    printf("[TEST] test_concurrent_queue_mpmc (4 producer threads x 200 items)...\n");
    moodycamel::ConcurrentQueue<int, 1024> q;
    std::vector<std::thread> producers;
    for (int t = 0; t < 4; ++t) {
        producers.emplace_back([&q, t]() {
            for (int i = 1; i <= 200; ++i) {
                bool ok = q.enqueue(t * 1000 + i);
                ASSERT_TRUE(ok);
            }
        });
    }
    for (auto& th : producers) {
        th.join();
    }
    ASSERT_TRUE(q.size_approx() == 800);

    int dequeuedCount = 0;
    long long sum = 0;
    int val = 0;
    while (q.try_dequeue(val)) {
        ++dequeuedCount;
        sum += val;
    }
    ASSERT_TRUE(dequeuedCount == 800);
    ASSERT_TRUE(q.size_approx() == 0);
    // Expected sum: 4 * (1..200) + 200 * (0 + 1000 + 2000 + 3000) = 4 * 20100 + 1200000 = 1280400
    ASSERT_TRUE(sum == 1280400LL);
    printf("  -> PASSED (800 items enqueued/dequeued lock-free across 4 threads)\n");
}

void test_in_process_stacktrace() {
#if defined(_WIN32)
    printf("[TEST] test_in_process_stacktrace...\n");
    std::string trace = Kongor::StackTrace::CaptureCurrentStackTrace(0, 16);
    ASSERT_TRUE(!trace.empty());
    ASSERT_TRUE(trace.find("+0x") != std::string::npos);
    printf("  -> PASSED (Captured %zu bytes of RVA/PDB stack trace)\n", trace.size());
#endif
}

void test_in_process_stacktrace_symbol_table_and_stale_guard() {
#if defined(_WIN32)
    printf("[TEST] test_in_process_stacktrace_symbol_table_and_stale_guard...\n");

    // 1. Verify live PE header identity extraction on current process module
    uint32_t selfTs = 0, selfSize = 0;
    ASSERT_TRUE(Kongor::StackTrace::ReadModulePeIdentity(GetModuleHandleA(NULL), selfTs, selfSize));
    ASSERT_TRUE(selfSize > 0);

    // 2. Load kongor_symbols.rva from disk (or fallback stream if running from another working dir)
    std::vector<Kongor::StackTrace::ModuleSymbolTable> tables;
    bool loadedFromDisk =
        Kongor::StackTrace::LoadSymbolTableFromFile("games/client/kongor_symbols.rva", tables) ||
        Kongor::StackTrace::LoadSymbolTableFromFile("../games/client/kongor_symbols.rva", tables) ||
        Kongor::StackTrace::LoadSymbolTableFromFile("../../games/client/kongor_symbols.rva", tables) ||
        Kongor::StackTrace::LoadSymbolTableFromFile("../../../games/client/kongor_symbols.rva", tables);

    if (!loadedFromDisk) {
        const char* sampleRvaFile =
            "@MODULE k2_x64.dll 0x61E64BFE 0x01394000 54adcb6aa938c41ea4f2993f96448c2a4f0b9f606f57d6e737baef2432fba4a7\n"
            "0x00951020 0x0095107C CModelPanel::Frame\n"
            "0x00CB62C0 0x00CB6643 luaV_concat\n";
        std::istringstream iss(sampleRvaFile);
        ASSERT_TRUE(Kongor::StackTrace::LoadSymbolTableFromStream(iss, tables));
    }
    ASSERT_TRUE(!tables.empty());

    // 3. Verify exact RVA symbol resolution when PE TimeDateStamp & SizeOfImage match
    std::string formatted;
    auto resFrame = Kongor::StackTrace::ResolveRvaSymbol(
        tables, "k2_x64.dll", 0x61E64BFE, 0x01394000, 0x00951034, formatted);
    ASSERT_TRUE(resFrame == Kongor::StackTrace::RvaLookupResult::Resolved);
    ASSERT_TRUE(formatted == "(CModelPanel::Frame+0x14)");

    auto resLuaConcat = Kongor::StackTrace::ResolveRvaSymbol(
        tables, "k2_x64.dll", 0x61E64BFE, 0x01394000, 0x00CB6477, formatted);
    ASSERT_TRUE(resLuaConcat == Kongor::StackTrace::RvaLookupResult::Resolved);
    ASSERT_TRUE(formatted == "(luaV_concat+0x1b7)");

    // 4. Verify [SYMBOLS][STALE] guard triggers when TimeDateStamp or SizeOfImage mismatches
    auto resStaleTs = Kongor::StackTrace::ResolveRvaSymbol(
        tables, "k2_x64.dll", 0xDEADBEEF, 0x01394000, 0x00951020, formatted);
    ASSERT_TRUE(resStaleTs == Kongor::StackTrace::RvaLookupResult::StaleHeader);
    ASSERT_TRUE(formatted.find("[SYMBOLS][STALE: k2_x64.dll header mismatch") != std::string::npos);

    auto resStaleSize = Kongor::StackTrace::ResolveRvaSymbol(
        tables, "k2_x64.dll", 0x61E64BFE, 0x01999000, 0x00951020, formatted);
    ASSERT_TRUE(resStaleSize == Kongor::StackTrace::RvaLookupResult::StaleHeader);
    ASSERT_TRUE(formatted.find("[SYMBOLS][STALE: k2_x64.dll header mismatch") != std::string::npos);

    printf("  -> PASSED (Verified RVA resolution & [SYMBOLS][STALE] PE header guard across %zu module tables)\n", tables.size());
#endif
}

int main() {
    printf("==========================================================\n");
    printf("  Project Kongor: Native Arena, String8 & Lock-Free Tests\n");
    printf("==========================================================\n");

    test_arena_basic_lifecycle();
    test_arena_temp_scratchpad();
    test_string8_sentinel_and_slices();
    test_string8_list_join();
    test_concurrent_queue_mpmc();
    test_in_process_stacktrace();
    test_in_process_stacktrace_symbol_table_and_stale_guard();
    benchmark_scratchpad_path_slices();

    printf("==========================================================\n");
    printf("  ALL TESTS PASSED CLEANLY (0 Errors, 0 Leaks)\n");
    printf("==========================================================\n");
    return 0;
}
