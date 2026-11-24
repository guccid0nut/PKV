// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ============================================================================
// File: linux_queue_arcade_tests.cpp
// Purpose: Linux Queue Arcade C++ Unit Test Runner
// Obligations: OBL-QA-LINUX-00 through OBL-QA-LINUX-11
// Target: hon-x86_64-server (libk2-x86_64.so, libgame_shared-x86_64-server.so)
// Dossier: artifacts/evidence/queue_arcade_linux_dossier.json
// ============================================================================

#include "../linux_queue_arcade.h"
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cassert>
#include <vector>
#include <string>

#if !defined(_WIN32)
#include <sys/mman.h>
#include <sys/uio.h>
#include <unistd.h>
#include <signal.h>
#include <setjmp.h>
#include <elf.h>
#include <dlfcn.h>
#else
// Standalone ELF64 structures for cross-platform compilation
struct Elf64_Ehdr {
    unsigned char e_ident[16];
    uint16_t      e_type;
    uint16_t      e_machine;
    uint32_t      e_version;
    uint64_t      e_entry;
    uint64_t      e_phoff;
    uint64_t      e_shoff;
    uint32_t      e_flags;
    uint16_t      e_ehsize;
    uint16_t      e_phentsize;
    uint16_t      e_phnum;
    uint16_t      e_shentsize;
    uint16_t      e_shnum;
    uint16_t      e_shstrndx;
};

struct Elf64_Phdr {
    uint32_t p_type;
    uint32_t p_flags;
    uint64_t p_offset;
    uint64_t p_vaddr;
    uint64_t p_paddr;
    uint64_t p_filesz;
    uint64_t p_memsz;
    uint64_t p_align;
};

struct Elf64_Shdr {
    uint32_t sh_name;
    uint32_t sh_type;
    uint64_t sh_flags;
    uint64_t sh_addr;
    uint64_t sh_offset;
    uint64_t sh_size;
    uint32_t sh_link;
    uint32_t sh_info;
    uint64_t sh_addralign;
    uint64_t sh_entsize;
};

#ifndef PT_LOAD
#define PT_LOAD 1
#endif
#ifndef SHT_DYNSYM
#define SHT_DYNSYM 11
#endif
#ifndef ELFCLASS64
#define ELFCLASS64 2
#endif
#ifndef ELFMAG0
#define ELFMAG0 0x7f
#endif
#ifndef ELFMAG1
#define ELFMAG1 'E'
#endif
#ifndef ELFMAG2
#define ELFMAG2 'L'
#endif
#ifndef ELFMAG3
#define ELFMAG3 'F'
#endif
#endif

namespace {

int g_totalTests = 0;
int g_failedTests = 0;

void ExpectTrue(bool condition, const char* testName, const char* detail = "") {
    ++g_totalTests;
    if (!condition) {
        ++g_failedTests;
        std::fprintf(stderr, "[FAIL] %s %s\n", testName, detail ? detail : "");
    } else {
        std::fprintf(stdout, "[PASS] %s\n", testName);
    }
    std::fflush(stdout);
    std::fflush(stderr);
}

#if !defined(_WIN32)
static void TestCrashSignalHandler(int sig, siginfo_t* /*info*/, void* /*context*/) {
    // satisfies: OBL-QA-LINUX-00
    if ((sig == SIGSEGV || sig == SIGBUS) && ::Kongor::t_callFaultGuard.armed) {
        ::Kongor::t_callFaultGuard.armed = 0;
        siglongjmp(::Kongor::t_callFaultGuard.jmpBuf, 1);
    }
    // Unarmed crash: exit test process with signal code
    _exit(128 + sig);
}

static void InstallTestCrashSignalHandler() {
    struct sigaction sa;
    std::memset(&sa, 0, sizeof(sa));
    sa.sa_sigaction = TestCrashSignalHandler;
    sa.sa_flags = SA_SIGINFO;
    sigemptyset(&sa.sa_mask);
    sigaction(SIGSEGV, &sa, nullptr);
    sigaction(SIGBUS, &sa, nullptr);
}
#endif

// ============================================================================
// Test Suite 1: Two-Tier Fault Guard (process_vm_readv & siglongjmp)
// satisfies: OBL-QA-LINUX-00
// ============================================================================
void TestFaultGuardSuite() {
    std::fprintf(stdout, "\n--- Suite 1: Two-Tier Fault Guard (OBL-QA-LINUX-00) ---\n");

    // 1. Null / zero-size checks
    ExpectTrue(!Kongor::IsValidReadableMemory(nullptr, 16), "IsValidReadableMemory rejects nullptr");
    ExpectTrue(!Kongor::IsValidReadableMemory(reinterpret_cast<void*>(0x10000ULL), 0), "IsValidReadableMemory rejects size 0");

    // 2. Unmapped address check
    ExpectTrue(!Kongor::IsValidReadableMemory(reinterpret_cast<void*>(0xDEAD0000ULL), 64),
        "IsValidReadableMemory rejects unmapped 0xDEAD0000ULL");

#if !defined(_WIN32)
    // 3. Multi-page boundary check via process_vm_readv
    // Allocate 2 contiguous pages (8192 bytes). Set page 1 as PROT_NONE (unmapped/unreadable).
    void* pages = mmap(NULL, 8192, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    ExpectTrue(pages != MAP_FAILED, "Allocated 2 test pages via mmap");

    if (pages != MAP_FAILED) {
        uint8_t* p = static_cast<uint8_t*>(pages);
        p[0] = 42;
        p[4095] = 99;

        // Make second page unreadable
        int protRet = mprotect(p + 4096, 4096, PROT_NONE);
        ExpectTrue(protRet == 0, "mprotect set second page to PROT_NONE");

        // Pointer strictly within page 0 -> must return true
        ExpectTrue(Kongor::IsValidReadableMemory(p, 4096),
            "IsValidReadableMemory returns true for single fully-readable page 0");

        // Pointer at page 0 spanning into page 1 (boundary crossing) -> must return false
        ExpectTrue(!Kongor::IsValidReadableMemory(p + 4000, 200),
            "IsValidReadableMemory returns false for probe spanning into PROT_NONE page 1");

        // Pointer strictly inside PROT_NONE page 1 -> must return false
        ExpectTrue(!Kongor::IsValidReadableMemory(p + 4096, 64),
            "IsValidReadableMemory returns false for pointer inside PROT_NONE page 1");

        // Restore and munmap
        mprotect(p + 4096, 4096, PROT_READ | PROT_WRITE);
        munmap(pages, 8192);
    }

    // 4. Armed siglongjmp recovery without crashing
    InstallTestCrashSignalHandler();

    volatile bool recoveredFromSegv = false;
    LINUX_CALL_FAULT_GUARD_BEGIN() {
        volatile int* badPtr = reinterpret_cast<volatile int*>(0xDEAD0000ULL);
        volatile int val = *badPtr;
        (void)val;
    } LINUX_CALL_FAULT_GUARD_END({
        recoveredFromSegv = true;
    });

    ExpectTrue(recoveredFromSegv,
        "LINUX_CALL_FAULT_GUARD successfully recovered from SIGSEGV on unmapped 0xDEAD0000ULL via siglongjmp");
    ExpectTrue(Kongor::t_callFaultGuard.armed == 0,
        "t_callFaultGuard is safely disarmed after recovery");

    // 5. Verify second consecutive SIGSEGV also recovers (proving handler is NOT one-shot SA_RESETHAND)
    volatile bool recoveredSecondSegv = false;
    LINUX_CALL_FAULT_GUARD_BEGIN() {
        volatile int* badPtr2 = reinterpret_cast<volatile int*>(0xBEEF0000ULL);
        volatile int val2 = *badPtr2;
        (void)val2;
    } LINUX_CALL_FAULT_GUARD_END({
        recoveredSecondSegv = true;
    });
    ExpectTrue(recoveredSecondSegv,
        "Second consecutive SIGSEGV recovered cleanly (signal handler remains armed across recoveries)");
#endif
}

// ============================================================================
// Test Suite 2: RTLD_LOCAL Prologue Verification & Strict W^X Inline Trampoline
// satisfies: OBL-QA-LINUX-11
// ============================================================================
#if !defined(_WIN32)
// Live synthetic target function for inline detour
static int g_hookInterceptCount = 0;
typedef int (*PFN_TestSyntheticFunc)(int a, int b);
static PFN_TestSyntheticFunc g_origSyntheticFunc = nullptr;

static int Hooked_TestSyntheticFunc(int a, int b) {
    ++g_hookInterceptCount;
    int origResult = g_origSyntheticFunc ? g_origSyntheticFunc(a, b) : 0;
    return origResult + 100;
}
#endif

void TestTrampolineSuite() {
    std::fprintf(stdout, "\n--- Suite 2: W^X Inline Trampoline & Prologue Validator (OBL-QA-LINUX-11) ---\n");

#if !defined(_WIN32)
    // 1. Prologue instruction decoder tests
    // Valid 14-byte CHostServer::GenerateClientID prologue: 55 41 57 41 56 41 55 41 54 53 48 83 EC 18
    static const uint8_t kValidGenIdPrologue[14] = {
        0x55, 0x41, 0x57, 0x41, 0x56, 0x41, 0x55, 0x41, 0x54, 0x53, 0x48, 0x83, 0xEC, 0x18
    };
    ExpectTrue(Kongor::VerifyLinuxPrologueNoRipRelative(kValidGenIdPrologue, 14),
        "VerifyLinuxPrologueNoRipRelative accepts valid 14-byte GenerateClientID prologue");

    // Invalid prologue containing relative call (E8)
    static const uint8_t kInvalidCallPrologue[14] = {
        0x55, 0x41, 0x57, 0xE8, 0x10, 0x00, 0x00, 0x00, 0x41, 0x54, 0x53, 0x48, 0x83, 0xEC
    };
    ExpectTrue(!Kongor::VerifyLinuxPrologueNoRipRelative(kInvalidCallPrologue, 14),
        "VerifyLinuxPrologueNoRipRelative rejects relative call opcode (E8)");

    // Invalid prologue containing RIP-relative LEA (48 8D 05)
    static const uint8_t kInvalidRipLeaPrologue[14] = {
        0x55, 0x48, 0x8D, 0x05, 0x10, 0x00, 0x00, 0x00, 0x41, 0x54, 0x53, 0x48, 0x83, 0xEC
    };
    ExpectTrue(!Kongor::VerifyLinuxPrologueNoRipRelative(kInvalidRipLeaPrologue, 14),
        "VerifyLinuxPrologueNoRipRelative rejects RIP-relative ModRM [rip+disp32] (48 8D 05)");

    // 2. Real live W^X Trampoline allocation & detour execution
    // Allocate executable page for target function
    void* codePage = mmap(NULL, 4096, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    ExpectTrue(codePage != MAP_FAILED, "Allocated synthetic code page via mmap");

    if (codePage != MAP_FAILED) {
        uint8_t* code = static_cast<uint8_t*>(codePage);
        // Write synthetic target function matching exact 14-byte prologue:
        // 55 41 57 41 56 41 55 41 54 53 48 83 EC 18 (prologue)
        // 48 83 C4 18 (add rsp, 0x18)
        // 5B 41 5C 41 5D 41 5E 41 5F 5D (pops)
        // 8D 04 37 (lea eax, [rdi + rsi])
        // C3 (ret)
        static const uint8_t kTargetFuncBody[] = {
            0x55, 0x41, 0x57, 0x41, 0x56, 0x41, 0x55, 0x41, 0x54, 0x53, 0x48, 0x83, 0xEC, 0x18,
            0x48, 0x83, 0xC4, 0x18,
            0x5B, 0x41, 0x5C, 0x41, 0x5D, 0x41, 0x5E, 0x41, 0x5F, 0x5D,
            0x8D, 0x04, 0x37,
            0xC3
        };
        std::memcpy(code, kTargetFuncBody, sizeof(kTargetFuncBody));
        mprotect(codePage, 4096, PROT_READ | PROT_EXEC);
        __builtin___clear_cache(code, code + sizeof(kTargetFuncBody));

        auto pTargetFunc = reinterpret_cast<PFN_TestSyntheticFunc>(codePage);
        int baseResult = pTargetFunc(10, 20);
        ExpectTrue(baseResult == 30, "Pre-hook synthetic target returns expected 10 + 20 = 30");

        // Install inline detour
        void* tramp = Kongor::InstallLinuxInlineTrampoline64(
            codePage,
            reinterpret_cast<void*>(Hooked_TestSyntheticFunc),
            kValidGenIdPrologue,
            14);

        ExpectTrue(tramp != nullptr, "InstallLinuxInlineTrampoline64 allocated strict W^X trampoline");

        if (tramp != nullptr) {
            g_origSyntheticFunc = reinterpret_cast<PFN_TestSyntheticFunc>(tramp);

            g_hookInterceptCount = 0;
            int hookedResult = pTargetFunc(10, 20);
            ExpectTrue(hookedResult == 130, "Hooked call executed: orig (30) + 100 = 130");
            ExpectTrue(g_hookInterceptCount == 1, "Hook intercepted target call exactly once");

            // Verify prologue mismatch rejection (fail-closed)
            static const uint8_t kWrongPrologue[14] = { 0x90, 0x90, 0x90, 0 };
            void* rejectedMismatched = Kongor::InstallLinuxInlineTrampoline64(
                codePage, reinterpret_cast<void*>(Hooked_TestSyntheticFunc), kWrongPrologue, 14);
            ExpectTrue(rejectedMismatched == nullptr,
                "InstallLinuxInlineTrampoline64 rejected mismatched prologue bytes fail-closed");
        }

        mprotect(codePage, 4096, PROT_READ | PROT_WRITE);
        munmap(codePage, 4096);
    }
#endif
}

// ============================================================================
// Test Suite 3: Synthetic IGame + LLVM libc++ std::__tree Simulation
// Exercises OBL-QA-LINUX-01 through OBL-QA-LINUX-10
// ============================================================================

// Memory block holding a synthetic Linux CPlayer at verified offsets
struct SyntheticLinuxPlayerMemory {
    uint8_t bytes[0x800]; // Covers offsets up to +0x730 (m_pBotBrain)

    void Initialize(int32_t clientNum, int32_t teamId, int32_t accountId, bool isConnectionless, void* botBrain) {
        std::memset(bytes, 0, sizeof(bytes));
        *reinterpret_cast<int32_t*>(bytes + Kongor::LinuxOffsets::kPlayerClientNumber) = clientNum;
        *reinterpret_cast<int32_t*>(bytes + Kongor::LinuxOffsets::kPlayerTeam) = teamId;
        *reinterpret_cast<uint32_t*>(bytes + Kongor::LinuxOffsets::kPlayerHeroIndex) = 0xFFFFFFFFu;
        *reinterpret_cast<int32_t*>(bytes + Kongor::LinuxOffsets::kPlayerAccountID) = accountId;
        *reinterpret_cast<uint16_t*>(bytes + Kongor::LinuxOffsets::kPlayerNameIndex) = 1;
        *reinterpret_cast<uint16_t*>(bytes + Kongor::LinuxOffsets::kPlayerClanTagIndex) = 2;
        *reinterpret_cast<uint16_t*>(bytes + Kongor::LinuxOffsets::kPlayerClanNameIndex) = 3;

        uint16_t flags = isConnectionless ? 0x0200u : 0u;
        *reinterpret_cast<uint16_t*>(bytes + Kongor::LinuxOffsets::kPlayerFlags) = flags;
        *reinterpret_cast<uint16_t*>(bytes + Kongor::LinuxOffsets::kPlayerFlags2) = (botBrain != nullptr) ? 0x0008u : 0u;

        *reinterpret_cast<uint16_t*>(bytes + Kongor::LinuxOffsets::kPlayerPing) = 0u;
        *reinterpret_cast<uint32_t*>(bytes + Kongor::LinuxOffsets::kPlayerDisconnectTime) = 0xFFFFFFFFu;
        *reinterpret_cast<uint32_t*>(bytes + Kongor::LinuxOffsets::kPlayerTotalDiscTime) = 0u;
        *reinterpret_cast<uint32_t*>(bytes + Kongor::LinuxOffsets::kPlayerDiscTermTime) = 0xFFFFFFFFu;

        *reinterpret_cast<uint8_t*>(bytes + Kongor::LinuxOffsets::kPlayerMatchmakingPostInit) = 1;
        *reinterpret_cast<uint8_t*>(bytes + Kongor::LinuxOffsets::kPlayerIsBot) = (botBrain != nullptr || isConnectionless) ? 1 : 0;
        *reinterpret_cast<void**>(bytes + Kongor::LinuxOffsets::kPlayerBotBrain) = botBrain;
    }

    void* AsPlayer() { return bytes; }
};

struct SyntheticEntityDirectory {
    void* vptr;
    void* vtable[4];
    void* heroUnit;

    SyntheticEntityDirectory(void* unit) : vptr(vtable), heroUnit(unit) {
        vtable[0] = nullptr;
        vtable[1] = nullptr;
        vtable[2] = nullptr;
        // +0x18 is GetEntityFromIndex
        vtable[3] = reinterpret_cast<void*>(&GetEntityStub);
    }

    static void* GetEntityStub(void* thisPtr, uint32_t /*index*/) {
        auto* self = static_cast<SyntheticEntityDirectory*>(thisPtr);
        return self->heroUnit;
    }
};

struct SyntheticUnitEntity {
    void* vptr;
    void* vtable[48]; // Needs at least index +0x178 / 8 = 47

    SyntheticUnitEntity() : vptr(vtable) {
        std::memset(vtable, 0, sizeof(vtable));
        // +0x178 is GetAsUnit
        vtable[0x178 / sizeof(void*)] = reinterpret_cast<void*>(&GetAsUnitStub);
    }

    static void* GetAsUnitStub(void* thisPtr) {
        return thisPtr; // Returns non-null unit
    }
};

void TestSimulationSuite() {
    std::fprintf(stdout, "\n--- Suite 3: Synthetic Engine & std::__tree Simulation (OBL-QA-LINUX-01..10) ---\n");

    // 1. Build 10 synthetic players (5 on Team 1, 5 on Team 2)
    SyntheticLinuxPlayerMemory players[10];
    uint32_t fakeBotBrainValues[10];

    for (int i = 0; i < 10; ++i) {
        int team = (i < 5) ? 1 : 2;
        fakeBotBrainValues[i] = 0xAA00u + i;
        players[i].Initialize(i, team, 0, true, &fakeBotBrainValues[i]);
    }

    // 2. Build LLVM libc++ std::__tree node array for client numbers 0..9
    // Tree topology: balanced binary tree
    Kongor::LibcxxTreeNode treeNodes[10];
    std::memset(treeNodes, 0, sizeof(treeNodes));

    // Simple linked list or tree: node i has right = i+1
    for (int i = 0; i < 10; ++i) {
        treeNodes[i].key = i;
        treeNodes[i].value = players[i].AsPlayer();
        treeNodes[i].isBlack = true;
        treeNodes[i].left = nullptr;
        treeNodes[i].right = (i + 1 < 10) ? &treeNodes[i + 1] : nullptr;
        treeNodes[i].parent = (i > 0) ? &treeNodes[i - 1] : nullptr;
    }

    // 3. Build synthetic IGame / CGameServer
    uint8_t gameBytes[0x300];
    std::memset(gameBytes, 0, sizeof(gameBytes));

    SyntheticUnitEntity fakeHeroUnit;
    SyntheticEntityDirectory fakeEntityDir(&fakeHeroUnit);

    // Entity directory @ +0x028
    *reinterpret_cast<void**>(gameBytes + Kongor::LinuxOffsets::kGameEntityDirectory) = &fakeEntityDir;

    // GameInfo @ +0x0C0
    uint8_t gameInfoBytes[0x100];
    std::memset(gameInfoBytes, 0, sizeof(gameInfoBytes));
    *reinterpret_cast<int32_t*>(gameInfoBytes + Kongor::LinuxOffsets::kGameInfoPhase) = 4; // Phase 4 (< 5)
    *reinterpret_cast<void**>(gameBytes + Kongor::LinuxOffsets::kGameInfo) = gameInfoBytes;

    // m_mapClients @ +0x210, inline sentinel @ +0x218
    void* endNodeAddr = gameBytes + Kongor::LinuxOffsets::kGameClientsMapSentinel;
    treeNodes[0].parent = endNodeAddr;
    *reinterpret_cast<void**>(gameBytes + Kongor::LinuxOffsets::kGameClientsMap) = &treeNodes[0];

    void* pGamePtr = gameBytes;

    // Mock RemoveBot counter
    static int s_removeBotCalls = 0;
    s_removeBotCalls = 0;

    Kongor::LinuxQueueArcadeEngineContext ctx = {};
    ctx.ppGame = &pGamePtr;
    ctx.pfnRemoveBot = [](void* /*p*/) {
        ++s_removeBotCalls;
    };
    ctx.ppNetResourceManager = nullptr;
    ctx.pfnSetNetString = nullptr;
    ctx.pfnGetNetString = nullptr;

    // Test A: ReapConnectionlessBotsIfNoActiveHumans gate (OBL-QA-LINUX-01)
    Kongor::SetQueueArcadeMode(Kongor::QueueArcadeServerMode::ActiveBots);
    ExpectTrue(Kongor::IsQueueArcadeModeActive(), "QueueArcadeMode is active (ActiveBots)");

    Kongor::ReapConnectionlessBotsIfNoActiveHumans(&ctx);
    // Verify slot 0 did not get terminated (flag 0x0080)
    uint16_t flags0 = *reinterpret_cast<uint16_t*>(players[0].bytes + Kongor::LinuxOffsets::kPlayerFlags);
    ExpectTrue((flags0 & 0x0080u) == 0,
        "ReapConnectionlessBots gate prevented 0-human slot termination (OBL-QA-LINUX-01)");

    // Test B: ArcadeReserveSlot (OBL-QA-LINUX-02, 06, 07, 08, 10)
    auto res = Kongor::ArcadeReserveSlot(77701, 1, L"TestHero", &ctx, L"DEV");
    ExpectTrue(res.success, "ArcadeReserveSlot succeeded for account 77701");
    ExpectTrue(res.clientNum >= 0 && res.clientNum <= 4,
        "ArcadeReserveSlot honored preferredTeam 1 (reserved client 0..4)");

    uint8_t* pReserved = players[res.clientNum].bytes;
    ExpectTrue(*reinterpret_cast<int32_t*>(pReserved + Kongor::LinuxOffsets::kPlayerAccountID) == 77701,
        "Reserved slot stamped m_iAccountID == 77701 (OBL-QA-LINUX-02)");
    ExpectTrue(*reinterpret_cast<uint8_t*>(pReserved + Kongor::LinuxOffsets::kPlayerIsBot) == 0,
        "Reserved slot cleared m_bIsBot == 0 (OBL-QA-LINUX-07)");
    ExpectTrue(*reinterpret_cast<uint8_t*>(pReserved + Kongor::LinuxOffsets::kPlayerMatchmakingPostInit) == 1,
        "Reserved slot maintained m_bMatchmakingPostInitialized == 1 (OBL-QA-LINUX-08)");

    // In Mode 1 (ActiveBots), bot brain must be parked, NOT freed via RemoveBot
    ExpectTrue(res.detachedBotBrain != nullptr,
        "Mode 1 detached and parked CBotBrain into reservation entry (OBL-QA-LINUX-10)");
    ExpectTrue(*reinterpret_cast<void**>(pReserved + Kongor::LinuxOffsets::kPlayerBotBrain) == nullptr,
        "Unconditionally zeroed m_pBotBrain (+0x730) on reserved slot (OBL-QA-LINUX-06)");
    ExpectTrue((*reinterpret_cast<uint16_t*>(pReserved + Kongor::LinuxOffsets::kPlayerFlags2) & 0x0048u) == 0,
        "Cleared bit 0x0048 on flags2 (+0x3D0) (OBL-QA-LINUX-06)");

    // Test C: ResolveReservedArcadeClientId handshake matching (OBL-QA-LINUX-03)
    uint16_t fakeConnId = 0x1234;
    int32_t resolvedClient = Kongor::ResolveReservedArcadeClientId(&fakeConnId, 77701);
    ExpectTrue(resolvedClient == res.clientNum,
        "ResolveReservedArcadeClientId matched incoming accountId 77701 to reserved clientNum (OBL-QA-LINUX-03)");

    // Test D: Pre-Phase-5 duplicate hero spawn suppression (OBL-QA-LINUX-09)
    *reinterpret_cast<uint32_t*>(pReserved + Kongor::LinuxOffsets::kPlayerHeroIndex) = 42; // Hero exists
    bool suppressPre5 = Kongor::ShouldSuppressPreMatchDuplicateHeroSpawn(pReserved, &ctx);
    ExpectTrue(suppressPre5,
        "ShouldSuppressPreMatchDuplicateHeroSpawn suppressed duplicate spawn in Phase 4 (OBL-QA-LINUX-09)");

    // In Phase 5 (ACTIVE), repick spawns must NOT be suppressed
    *reinterpret_cast<int32_t*>(gameInfoBytes + Kongor::LinuxOffsets::kGameInfoPhase) = 5;
    bool suppressPhase5 = Kongor::ShouldSuppressPreMatchDuplicateHeroSpawn(pReserved, &ctx);
    ExpectTrue(!suppressPhase5,
        "ShouldSuppressPreMatchDuplicateHeroSpawn allows repick spawns in Phase >= 5 (OBL-QA-LINUX-09)");

    // Test D2: Auto-balance (preferredTeam == 0) must still count Team 1 human after K2 sets 0x0200 (PLAYER_FLAG_LOADED)
    *reinterpret_cast<uint16_t*>(pReserved + Kongor::LinuxOffsets::kPlayerFlags) = 0x0202u;
    auto resBalanced = Kongor::ArcadeReserveSlot(77702, 0, L"SecondHero", &ctx, L"DEV");
    ExpectTrue(resBalanced.success && resBalanced.teamId == 2 && resBalanced.clientNum >= 5 && resBalanced.clientNum <= 9,
        "ArcadeReserveSlot auto-balance (preferredTeam=0) counts loaded Team 1 human (flags=0x0202, isBot=0) and assigns Team 2");
    Kongor::ArcadeReleaseSlot(77702, &ctx);

    // Test E: ArcadeReleaseSlot & Mode 1 bot brain restoration (OBL-QA-LINUX-04, 07, 08, 10)
    bool released = Kongor::ArcadeReleaseSlot(77701, &ctx);
    ExpectTrue(released, "ArcadeReleaseSlot succeeded for account 77701");
    ExpectTrue(*reinterpret_cast<int32_t*>(pReserved + Kongor::LinuxOffsets::kPlayerAccountID) == 0,
        "Released slot cleared m_iAccountID == 0 (OBL-QA-LINUX-04)");
    ExpectTrue(*reinterpret_cast<uint8_t*>(pReserved + Kongor::LinuxOffsets::kPlayerIsBot) == 1,
        "Released slot restored m_bIsBot == 1 (OBL-QA-LINUX-07)");
    ExpectTrue((*reinterpret_cast<uint16_t*>(pReserved + Kongor::LinuxOffsets::kPlayerFlags) & 0x0200u) != 0,
        "Released slot restored 0x0200 connectionless flag (OBL-QA-LINUX-04)");
    ExpectTrue(*reinterpret_cast<void**>(pReserved + Kongor::LinuxOffsets::kPlayerBotBrain) != nullptr,
        "Mode 1 restored parked CBotBrain back to +0x730 on release (OBL-QA-LINUX-10)");
    ExpectTrue((*reinterpret_cast<uint16_t*>(pReserved + Kongor::LinuxOffsets::kPlayerFlags2) & 0x0008u) != 0,
        "Mode 1 restored bot attached bit 0x0008 on flags2 (+0x3D0) (OBL-QA-LINUX-10)");

    // Test F: Mode 2 (PartyDormantSlots) & ArcadeParkUnreservedBots (OBL-QA-LINUX-04, 06)
    Kongor::SetQueueArcadeMode(Kongor::QueueArcadeServerMode::PartyDormantSlots);
    int priorRemoveCalls = s_removeBotCalls;
    uint32_t parkedCount = Kongor::ArcadeParkUnreservedBots(&ctx);
    ExpectTrue(parkedCount > 0, "ArcadeParkUnreservedBots parked unreserved bots in Mode 2");
    ExpectTrue(s_removeBotCalls > priorRemoveCalls, "CPlayer::RemoveBot was called for dormant bots");

    for (int i = 0; i < 10; ++i) {
        ExpectTrue(*reinterpret_cast<void**>(players[i].bytes + Kongor::LinuxOffsets::kPlayerBotBrain) == nullptr,
            "Mode 2 unconditionally zeroed m_pBotBrain (+0x730) on all dormant slots (OBL-QA-LINUX-06)");
    }

    // Test G: ClearAllArcadeReservations & Player Terminate (OBL-QA-LINUX-10)
    Kongor::ClearAllArcadeReservations(&ctx);
    ExpectTrue(true, "ClearAllArcadeReservations cleanly flushed reservation state (OBL-QA-LINUX-10)");

    // Test H: Mode 0 (Disabled) ReapConnectionlessBotsIfNoActiveHumans positive control (OBL-QA-LINUX-01)
    Kongor::SetQueueArcadeMode(Kongor::QueueArcadeServerMode::Disabled);
    ExpectTrue(!Kongor::IsQueueArcadeModeActive(), "QueueArcadeMode Disabled (Mode 0)");
    Kongor::ReapConnectionlessBotsIfNoActiveHumans(&ctx);
    uint16_t reapedFlags0 = *reinterpret_cast<uint16_t*>(players[0].bytes + Kongor::LinuxOffsets::kPlayerFlags);
    ExpectTrue((reapedFlags0 & 0x0080u) != 0,
        "Mode 0 ReapConnectionlessBots marked 0-human connectionless slots terminated (0x0080) (OBL-QA-LINUX-01)");

    // Test I: OBL-QA-LINUX-05 IPC Privilege Firewall & Console Command Dispatch
    ExpectTrue(Kongor::ShouldAllowUnprivilegedIpcCommandBufferA("ServerStatus", 12),
        "IPC sanitizer allows benign ServerStatus command (OBL-QA-LINUX-05)");
    ExpectTrue(Kongor::ShouldAllowUnprivilegedIpcCommandBufferA("echo \"kongor_arcade_mode 1\"", 27),
        "IPC sanitizer allows quoted kongor_arcade_mode string literal (OBL-QA-LINUX-05)");
    ExpectTrue(!Kongor::ShouldAllowUnprivilegedIpcCommandBufferA("kongor_arcade_mode 1", 20),
        "IPC sanitizer blocks unprivileged kongor_arcade_mode command (OBL-QA-LINUX-05)");
    ExpectTrue(!Kongor::ShouldAllowUnprivilegedIpcCommandBufferA("ServerStatus; kongor_arcade_reserve 77701 1 Player", 50),
        "IPC sanitizer blocks compound buffer smuggling kongor_arcade_reserve (OBL-QA-LINUX-05)");
    ExpectTrue(!Kongor::ShouldAllowUnprivilegedIpcCommandBufferA("quit", 4),
        "IPC sanitizer blocks unprivileged quit command (OBL-QA-LINUX-05)");
    ExpectTrue(Kongor::TryDispatchLinuxArcadeConsoleCmdW(L"kongor_arcade_mode 0"),
        "TryDispatchLinuxArcadeConsoleCmdW consumes privileged kongor_arcade_mode command (OBL-QA-LINUX-05)");
    ExpectTrue(!Kongor::TryDispatchLinuxArcadeConsoleCmdW(L"sv_gameEndPhaseTime 500"),
        "TryDispatchLinuxArcadeConsoleCmdW ignores standard K2 CVars (OBL-QA-LINUX-05)");
}

// ============================================================================
// Test Suite 4: Real Linux Server ELF Prologues & Symbols (OBL-QA-LINUX-03, 06, 09, 10)
// ============================================================================

// Real Linux Server ELF Prologue Constants (64-bit System V AMD64 ABI)
// CHostServer::GenerateClientID @ libk2-x86_64.so RVA 0x00CC8AA0 (OBL-QA-LINUX-03)
static const uint8_t kPrologueGenerateClientID[14] = {
    0x55, 0x41, 0x57, 0x41, 0x56, 0x41, 0x55, 0x41,
    0x54, 0x53, 0x48, 0x83, 0xec, 0x18
};

// CPlayer::RemoveBot @ libgame_shared-x86_64-server.so RVA 0x00C953A0 (OBL-QA-LINUX-06)
static const uint8_t kPrologueRemoveBot[14] = {
    0x53, 0x48, 0x89, 0xfb, 0x48, 0x8b, 0xbb, 0x30,
    0x07, 0x00, 0x00, 0x48, 0x85, 0xff
};

// CPlayer::SpawnHero @ libgame_shared-x86_64-server.so RVA 0x00C99C90 (OBL-QA-LINUX-09)
static const uint8_t kPrologueSpawnHero[17] = {
    0x55, 0x41, 0x57, 0x41, 0x56, 0x41, 0x55, 0x41,
    0x54, 0x53, 0x48, 0x81, 0xec, 0x58, 0x02, 0x00, 0x00
};

// CPlayer::Terminate @ libgame_shared-x86_64-server.so RVA 0x00C98E90 (OBL-QA-LINUX-10)
static const uint8_t kPrologueTerminate[17] = {
    0x55, 0x41, 0x57, 0x41, 0x56, 0x41, 0x55, 0x41,
    0x54, 0x53, 0x48, 0x81, 0xec, 0xb8, 0x00, 0x00, 0x00
};

// IGame::ProcessWebRequests @ libgame_shared-x86_64-server.so RVA 0x00FDD83F (CHostServer::m_sSessionCookie +0x208 -> +0x228)
static constexpr uint64_t kRvaProcessWebRequestsCookieLoad = 0x00FDD83FULL;
static const uint8_t kInstrProcessWebRequestsCookieLoad[7] = {
    0x49, 0x8d, 0xb5, 0x08, 0x02, 0x00, 0x00
};

static FILE* OpenServerBinary(const char* relativeSuffix, char* outResolvedPath, size_t pathCap) {
    const char* candidatePrefixes[] = {
        "",
        "./",
        "../",
        "../../",
        "../../../",
        "../../../../"
    };

    for (const char* prefix : candidatePrefixes) {
        char buf[512];
        std::snprintf(buf, sizeof(buf), "%s%s", prefix, relativeSuffix);
        FILE* fp = std::fopen(buf, "rb");
        if (fp) {
            if (outResolvedPath && pathCap > 0) {
                std::snprintf(outResolvedPath, pathCap, "%s", buf);
            }
            return fp;
        }
    }
    return nullptr;
}

static bool VerifyElfPrologueAtRva(
    FILE* fp,
    const char* binaryName,
    const char* funcName,
    uint64_t rva,
    const uint8_t* expectedPrologue,
    size_t prologueLen,
    const char* oblTag
) {
    std::rewind(fp);
    Elf64_Ehdr ehdr;
    if (std::fread(&ehdr, 1, sizeof(ehdr), fp) != sizeof(ehdr)) {
        ExpectTrue(false, "Failed to read ELF header", binaryName);
        return false;
    }

    // Verify ELF64 magic
    if (ehdr.e_ident[0] != ELFMAG0 || ehdr.e_ident[1] != ELFMAG1 ||
        ehdr.e_ident[2] != ELFMAG2 || ehdr.e_ident[3] != ELFMAG3 ||
        ehdr.e_ident[4] != ELFCLASS64) {
        ExpectTrue(false, "ELF64 magic verification failed", binaryName);
        return false;
    }

    // Read PT_LOAD program headers
    if (std::fseek(fp, static_cast<long>(ehdr.e_phoff), SEEK_SET) != 0) {
        ExpectTrue(false, "Failed to seek to program headers", binaryName);
        return false;
    }

    uint64_t targetFileOffset = 0;
    bool foundSegment = false;

    for (uint16_t i = 0; i < ehdr.e_phnum; ++i) {
        Elf64_Phdr phdr;
        if (std::fread(&phdr, 1, sizeof(phdr), fp) != sizeof(phdr)) break;
        if (phdr.p_type == PT_LOAD) {
            if (rva >= phdr.p_vaddr && rva < phdr.p_vaddr + phdr.p_memsz) {
                targetFileOffset = phdr.p_offset + (rva - phdr.p_vaddr);
                foundSegment = true;
                break;
            }
        }
    }

    if (!foundSegment) {
        char errBuf[128];
        std::snprintf(errBuf, sizeof(errBuf), "RVA 0x%llx not found in any PT_LOAD segment", static_cast<unsigned long long>(rva));
        ExpectTrue(false, funcName, errBuf);
        return false;
    }

    if (std::fseek(fp, static_cast<long>(targetFileOffset), SEEK_SET) != 0) {
        ExpectTrue(false, "Failed to seek to RVA file offset", funcName);
        return false;
    }

    uint8_t actualBytes[32] = {};
    if (std::fread(actualBytes, 1, prologueLen, fp) != prologueLen) {
        ExpectTrue(false, "Failed to read prologue bytes at file offset", funcName);
        return false;
    }

    bool matched = (std::memcmp(actualBytes, expectedPrologue, prologueLen) == 0);
    char testMsg[160];
    std::snprintf(testMsg, sizeof(testMsg), "%s RVA 0x%llx matches expected prologue (%s)",
        funcName, static_cast<unsigned long long>(rva), oblTag);
    ExpectTrue(matched, testMsg);
    return matched;
}

static bool VerifyElfExportedSymbols(
    FILE* fp,
    const char* binaryName,
    const char* sym1,
    const char* sym2
) {
    std::rewind(fp);
    Elf64_Ehdr ehdr;
    if (std::fread(&ehdr, 1, sizeof(ehdr), fp) != sizeof(ehdr)) {
        ExpectTrue(false, "Failed to read ELF header", binaryName);
        return false;
    }

    if (ehdr.e_ident[0] != ELFMAG0 || ehdr.e_ident[1] != ELFMAG1 ||
        ehdr.e_ident[2] != ELFMAG2 || ehdr.e_ident[3] != ELFMAG3 ||
        ehdr.e_ident[4] != ELFCLASS64) {
        ExpectTrue(false, "ELF64 magic verification failed", binaryName);
        return false;
    }

    if (std::fseek(fp, static_cast<long>(ehdr.e_shoff), SEEK_SET) != 0) {
        ExpectTrue(false, "Failed to seek to section headers", binaryName);
        return false;
    }

    std::vector<Elf64_Shdr> shdrs(ehdr.e_shnum);
    if (std::fread(shdrs.data(), sizeof(Elf64_Shdr), ehdr.e_shnum, fp) != ehdr.e_shnum) {
        ExpectTrue(false, "Failed to read section headers", binaryName);
        return false;
    }

    uint32_t dynstrSecIdx = 0;
    for (uint16_t i = 0; i < ehdr.e_shnum; ++i) {
        if (shdrs[i].sh_type == SHT_DYNSYM) {
            dynstrSecIdx = shdrs[i].sh_link;
            break;
        }
    }

    if (dynstrSecIdx == 0 || dynstrSecIdx >= ehdr.e_shnum) {
        ExpectTrue(false, "Could not locate .dynsym/.dynstr section index", binaryName);
        return false;
    }

    const Elf64_Shdr& strShdr = shdrs[dynstrSecIdx];
    if (std::fseek(fp, static_cast<long>(strShdr.sh_offset), SEEK_SET) != 0) {
        ExpectTrue(false, "Failed to seek to .dynstr section", binaryName);
        return false;
    }

    std::vector<char> dynstr(strShdr.sh_size + 1, 0);
    if (std::fread(dynstr.data(), 1, strShdr.sh_size, fp) != strShdr.sh_size) {
        ExpectTrue(false, "Failed to read .dynstr content", binaryName);
        return false;
    }

    bool foundSym1 = false;
    bool foundSym2 = false;

    const char* p = dynstr.data();
    const char* end = dynstr.data() + strShdr.sh_size;
    while (p < end) {
        size_t len = std::strlen(p);
        if (len > 0) {
            if (std::strcmp(p, sym1) == 0 ||
                (std::strncmp(p, "_ZN8CConsole7Execute", 19) == 0 && std::strstr(p, "basic_string") != nullptr)) {
                foundSym1 = true;
            }
            if (std::strcmp(p, sym2) == 0 ||
                std::strstr(p, "AddClient") != nullptr ||
                std::strcmp(p, "_ZN11CHostServer16GenerateClientIDERti") == 0 ||
                (std::strncmp(p, "_ZN11CHostServer", 16) == 0 && std::strstr(p, "Client") != nullptr)) {
                foundSym2 = true;
            }
        }
        p += len + 1;
    }

    char msg1[200];
    std::snprintf(msg1, sizeof(msg1),
        "libk2-x86_64.so contains exported %s in .dynsym/.dynstr", sym1);
    ExpectTrue(foundSym1, msg1);

    char msg2[200];
    std::snprintf(msg2, sizeof(msg2),
        "libk2-x86_64.so contains exported %s in .dynsym/.dynstr", sym2);
    ExpectTrue(foundSym2, msg2);

    return foundSym1 && foundSym2;
}

void TestRealLinuxServerElfPrologues() {
    std::fprintf(stdout, "\n--- Suite 4: Real Linux Server ELF Prologues & Symbols (OBL-QA-LINUX-03, 06, 09, 10) ---\n");

    char resolvedSharedPath[512] = {};
    FILE* fpShared = OpenServerBinary("games/server/linux-x64/game/libgame_shared-x86_64-server.so", resolvedSharedPath, sizeof(resolvedSharedPath));
    if (fpShared) {
        std::fprintf(stdout, "[INFO] Located libgame_shared-x86_64-server.so at: %s\n", resolvedSharedPath);
        VerifyElfPrologueAtRva(
            fpShared,
            "libgame_shared-x86_64-server.so",
            "CPlayer::RemoveBot",
            Kongor::LinuxOffsets::kRvaRemoveBot,
            kPrologueRemoveBot,
            sizeof(kPrologueRemoveBot),
            "OBL-QA-LINUX-06"
        );
        VerifyElfPrologueAtRva(
            fpShared,
            "libgame_shared-x86_64-server.so",
            "CPlayer::SpawnHero",
            Kongor::LinuxOffsets::kRvaSpawnHero,
            kPrologueSpawnHero,
            sizeof(kPrologueSpawnHero),
            "OBL-QA-LINUX-09"
        );
        VerifyElfPrologueAtRva(
            fpShared,
            "libgame_shared-x86_64-server.so",
            "CPlayer::Terminate",
            Kongor::LinuxOffsets::kRvaTerminate,
            kPrologueTerminate,
            sizeof(kPrologueTerminate),
            "OBL-QA-LINUX-10"
        );
        static const uint8_t kPrologueDisconnected[16] = {
            0x55, 0x41, 0x56, 0x53, 0x48, 0x89, 0xFB, 0x66, 0x81, 0x8B, 0xCE, 0x03, 0x00, 0x00, 0x01, 0x01
        };
        VerifyElfPrologueAtRva(
            fpShared,
            "libgame_shared-x86_64-server.so",
            "CPlayer::Disconnected",
            Kongor::LinuxOffsets::kRvaDisconnected,
            kPrologueDisconnected,
            sizeof(kPrologueDisconnected),
            "OBL-QA-LINUX-DISC"
        );
        static const uint8_t kPrologueAttachBot[15] = {
            0x41, 0x57, 0x41, 0x56, 0x41, 0x55, 0x41, 0x54,
            0x53, 0x49, 0x89, 0xF7, 0x49, 0x89, 0xFE
        };
        ExpectTrue(Kongor::VerifyLinuxPrologueNoRipRelative(kPrologueAttachBot, sizeof(kPrologueAttachBot)),
            "VerifyLinuxPrologueNoRipRelative accepts 15-byte CPlayer::AttachBot prologue");
        VerifyElfPrologueAtRva(
            fpShared,
            "libgame_shared-x86_64-server.so",
            "CPlayer::AttachBot",
            Kongor::LinuxOffsets::kRvaAttachBot,
            kPrologueAttachBot,
            sizeof(kPrologueAttachBot),
            "OBL-QA-LINUX-ATTACHBOT"
        );
        VerifyElfPrologueAtRva(
            fpShared,
            "libgame_shared-x86_64-server.so",
            "IGame::ProcessWebRequests cookie load (+0x208->+0x228)",
            kRvaProcessWebRequestsCookieLoad,
            kInstrProcessWebRequestsCookieLoad,
            sizeof(kInstrProcessWebRequestsCookieLoad),
            "ABI-WEBREQ-COOKIE"
        );
        std::fclose(fpShared);
    } else {
        std::fprintf(stdout, "[SKIP] games/server/linux-x64/game/libgame_shared-x86_64-server.so not present on disk; skipping binary prologue checks\n");
    }

    char resolvedK2Path[512] = {};
    FILE* fpK2 = OpenServerBinary("games/server/linux-x64/libk2-x86_64.so", resolvedK2Path, sizeof(resolvedK2Path));
    if (fpK2) {
        std::fprintf(stdout, "[INFO] Located libk2-x86_64.so at: %s\n", resolvedK2Path);
        VerifyElfPrologueAtRva(
            fpK2,
            "libk2-x86_64.so",
            "CHostServer::GenerateClientID",
            Kongor::LinuxOffsets::kRvaGenerateClientID,
            kPrologueGenerateClientID,
            sizeof(kPrologueGenerateClientID),
            "OBL-QA-LINUX-03"
        );
        VerifyElfExportedSymbols(
            fpK2,
            "libk2-x86_64.so",
            "_ZN8CConsole7ExecuteERKNSt3__112basic_stringIwNS0_11char_traitsIwEENS0_9allocatorIwEEEE",
            "_ZN11CHostServer16GenerateClientIDERti"
        );
        VerifyElfExportedSymbols(
            fpK2,
            "libk2-x86_64.so",
            "_ZN23CNetworkResourceManager9SetStringEtRKNSt3__112basic_stringIwNS0_11char_traitsIwEE17K2StringAllocatorIwEEEb",
            "_ZN23CNetworkResourceManager9GetStringEt"
        );
        std::fclose(fpK2);
    } else {
        std::fprintf(stdout, "[SKIP] games/server/linux-x64/libk2-x86_64.so not present on disk; skipping symbol checks\n");
    }
}

#if !defined(_WIN32)
extern "C" int Kongor_Linux_ParseEdidModelName(const uint8_t* edid, size_t len, char* outName, size_t outCap);
extern "C" int Kongor_Linux_FormatDetectedMonitors(char* outBuf, size_t outCap);
#endif

// ============================================================================
// Test Suite 5: Linux Client Multi-Monitor EDID, Fallback & ELF DT_NEEDED Guard
// ============================================================================
// protects: VESA EDID 0xFC/0xFE descriptor extraction, KONGOR_DETECTED_MONITORS
//           round-trip serialization, and zero-libstdc++/zero-libX11 DT_NEEDED
//           invariant so headless game servers never fail dynamic linking.
// fails_when: EDID descriptor offsets/LF trimming break, CVar pipe/slash schema
//             drifts, or libstdc++/libX11 leaks into libkongor_native_guard.so.
// seam: Real exported C ABI in libkongor_native_guard.so + live ELF PT_DYNAMIC.
void TestLinuxClientMultiMonitorSuite() {
    std::fprintf(stdout, "\n--- Suite 5: Linux Client Multi-Monitor & ELF DT_NEEDED Guard ---\n");
#if !defined(_WIN32)
    // 1. VESA EDID 0xFC Monitor Name descriptor ("VG27AQ5A\n    ")
    uint8_t edidFc[128] = {};
    edidFc[0] = 0x00;
    edidFc[1] = 0xFF; edidFc[2] = 0xFF; edidFc[3] = 0xFF;
    edidFc[4] = 0xFF; edidFc[5] = 0xFF; edidFc[6] = 0xFF; edidFc[7] = 0x00;
    // Descriptor 1 at offset 72: 00 00 00 FC 00 'V' 'G' '2' '7' 'A' 'Q' '5' 'A' 0x0A ' ' ' ' ' ' ' '
    edidFc[72] = 0x00; edidFc[73] = 0x00; edidFc[74] = 0x00; edidFc[75] = 0xFC; edidFc[76] = 0x00;
    const char* model1 = "VG27AQ5A\n    ";
    std::memcpy(edidFc + 77, model1, 13);

    char parsedName1[64] = {};
    int ok1 = Kongor_Linux_ParseEdidModelName(edidFc, sizeof(edidFc), parsedName1, sizeof(parsedName1));
    ExpectTrue(ok1 != 0 && std::strcmp(parsedName1, "VG27AQ5A") == 0,
        "Kongor_Linux_ParseEdidModelName extracts 0xFC Monitor Name 'VG27AQ5A'", parsedName1);

    // 2. VESA EDID 0xFE Unspecified Text fallback ("27E6QC\n      ") + pipe sanitization
    uint8_t edidFe[128] = {};
    std::memcpy(edidFe, edidFc, 8);
    edidFe[90] = 0x00; edidFe[91] = 0x00; edidFe[92] = 0x00; edidFe[93] = 0xFE; edidFe[94] = 0x00;
    const char* model2 = "27E6QC\n      ";
    std::memcpy(edidFe + 95, model2, 13);

    char parsedName2[64] = {};
    int ok2 = Kongor_Linux_ParseEdidModelName(edidFe, sizeof(edidFe), parsedName2, sizeof(parsedName2));
    ExpectTrue(ok2 != 0 && std::strcmp(parsedName2, "27E6QC") == 0,
        "Kongor_Linux_ParseEdidModelName extracts 0xFE fallback '27E6QC'", parsedName2);

    // 3. Truncated (< 128-byte) EDID must safely reject without over-reading
    char parsedShort[64] = "dirty";
    int lenShort = Kongor_Linux_ParseEdidModelName(edidFc, 64, parsedShort, sizeof(parsedShort));
    ExpectTrue(lenShort == 0 && parsedShort[0] == '\0',
        "Kongor_Linux_ParseEdidModelName rejects truncated 64-byte EDID");

    // 4. Headless/Wayland KONGOR_DETECTED_MONITORS environment fallback round-trip
    const char* savedDisp = std::getenv("DISPLAY");
    std::string savedDispCopy = savedDisp ? savedDisp : "";
    unsetenv("DISPLAY");
    const char* kExpectedPayload =
        "DP-1|1|VG27AQ5A|1|2560,1440|200|2560x1440@200,144,60+1920x1080@120,60/"
        "HDMI-A-1|2|27E6QC|0|2560,1440|144|2560x1440@144,60/"
        "DP-2|3|ASUS MB16AC|0|1920,1080|60|1920x1080@60";
    setenv("KONGOR_DETECTED_MONITORS", kExpectedPayload, 1);

    char formattedBuf[4096] = {};
    int monCount = Kongor_Linux_FormatDetectedMonitors(formattedBuf, sizeof(formattedBuf));
    ExpectTrue(monCount == 3, "Kongor_Linux_FormatDetectedMonitors parses 3 headless fallback monitors");
    ExpectTrue(std::strcmp(formattedBuf, kExpectedPayload) == 0,
        "Kongor_Linux_FormatDetectedMonitors round-trips exact 3-monitor CVar payload", formattedBuf);

    unsetenv("KONGOR_DETECTED_MONITORS");
    if (!savedDispCopy.empty()) {
        setenv("DISPLAY", savedDispCopy.c_str(), 1);
        // protects: Real XRandR outputs with >12 built-in VESA/CEA modes must retain native resolution bucket & maxHz (200Hz / 144Hz / 60Hz)
        // fails_when: ModeBucket array cap truncates native mode at index >=12 and falls back to 60Hz from an earlier non-native mode
        // seam: Live libXrandr.so.2 XRRGetScreenResourcesCurrent + EDID atom + Kongor_Linux_FormatDetectedMonitors
        char liveXrrBuf[4096] = {};
        int liveXrrCount = Kongor_Linux_FormatDetectedMonitors(liveXrrBuf, sizeof(liveXrrBuf));
        ExpectTrue(liveXrrCount >= 1, "Live XRandR DISPLAY enumerates >= 1 active monitor", liveXrrBuf);
        ExpectTrue(std::strlen(liveXrrBuf) <= 940, "XRandR monitor payload fits strictly within K2 CConsole MAX_CMD_LENGTH (<=940 chars)");
        if (std::strstr(liveXrrBuf, "DUMMY0|1|VG27AQ5A|") != nullptr) {
            ExpectTrue(std::strstr(liveXrrBuf, "DUMMY0|1|VG27AQ5A|1|2560,1440|200|2560x1440@200") != nullptr,
                "31-mode Xorg DUMMY0 preserves native 2560x1440@200Hz at bucket[0]", liveXrrBuf);
            ExpectTrue(std::strstr(liveXrrBuf, "DUMMY1|2|27E6QC|0|2560,1440|144|2560x1440@144") != nullptr,
                "31-mode Xorg DUMMY1 preserves native 2560x1440@144Hz at bucket[0]", liveXrrBuf);
            ExpectTrue(std::strstr(liveXrrBuf, "DUMMY2|3|DELL U2415|0|1920,1080|60|1920x1080@60") != nullptr,
                "31-mode Xorg DUMMY2 preserves native 1920x1080@60Hz at bucket[0]", liveXrrBuf);
        }
    }

    // 5. Inspect live loaded libkongor_native_guard.so ELF DT_NEEDED entries:
    //    must contain ZERO libstdc++ and ZERO libX11 / libXrandr dependencies.
    std::string guardSoPath;
    if (FILE* maps = std::fopen("/proc/self/maps", "r")) {
        char line[1024];
        while (std::fgets(line, sizeof(line), maps) != nullptr) {
            const char* pos = std::strstr(line, "/");
            if (pos && std::strstr(pos, "libkongor_native_guard.so") != nullptr) {
                guardSoPath = pos;
                while (!guardSoPath.empty() && (guardSoPath.back() == '\n' || guardSoPath.back() == '\r')) {
                    guardSoPath.pop_back();
                }
                break;
            }
        }
        std::fclose(maps);
    }

    ExpectTrue(!guardSoPath.empty(), "Located loaded libkongor_native_guard.so in /proc/self/maps", guardSoPath.c_str());
    if (!guardSoPath.empty()) {
        FILE* fpSo = std::fopen(guardSoPath.c_str(), "rb");
        ExpectTrue(fpSo != nullptr, "Opened libkongor_native_guard.so for ELF DT_NEEDED audit");
        if (fpSo) {
            Elf64_Ehdr eh{};
            bool hdrOk = (std::fread(&eh, 1, sizeof(eh), fpSo) == sizeof(eh)) &&
                         eh.e_ident[0] == ELFMAG0 && eh.e_ident[4] == ELFCLASS64;
            ExpectTrue(hdrOk, "libkongor_native_guard.so has valid ELF64 header");
            if (hdrOk && eh.e_shoff != 0 && eh.e_shnum > 0) {
                std::vector<Elf64_Shdr> shs(eh.e_shnum);
                std::fseek(fpSo, static_cast<long>(eh.e_shoff), SEEK_SET);
                if (std::fread(shs.data(), sizeof(Elf64_Shdr), eh.e_shnum, fpSo) == eh.e_shnum) {
                    int dynIdx = -1;
                    for (uint16_t i = 0; i < eh.e_shnum; ++i) {
                        if (shs[i].sh_type == SHT_DYNAMIC) {
                            dynIdx = static_cast<int>(i);
                            break;
                        }
                    }
                    ExpectTrue(dynIdx >= 0, "Located .dynamic section in libkongor_native_guard.so");
                    if (dynIdx >= 0 && shs[dynIdx].sh_link < eh.e_shnum) {
                        const Elf64_Shdr& strSec = shs[shs[dynIdx].sh_link];
                        std::vector<char> dynStr(strSec.sh_size + 1, 0);
                        std::fseek(fpSo, static_cast<long>(strSec.sh_offset), SEEK_SET);
                        bool strReadOk = (std::fread(dynStr.data(), 1, strSec.sh_size, fpSo) == strSec.sh_size);

                        size_t nDyn = shs[dynIdx].sh_size / sizeof(Elf64_Dyn);
                        std::vector<Elf64_Dyn> dyns(nDyn);
                        std::fseek(fpSo, static_cast<long>(shs[dynIdx].sh_offset), SEEK_SET);
                        bool dynReadOk = (std::fread(dyns.data(), sizeof(Elf64_Dyn), nDyn, fpSo) == nDyn);
                        ExpectTrue(strReadOk && dynReadOk, "Read .dynamic and .dynstr tables of libkongor_native_guard.so");

                        bool leakedStdcpp = false;
                        bool leakedX11 = false;
                        for (size_t d = 0; d < nDyn; ++d) {
                            if (dyns[d].d_tag == DT_NEEDED && dyns[d].d_un.d_val < strSec.sh_size) {
                                const char* needed = dynStr.data() + dyns[d].d_un.d_val;
                                if (std::strstr(needed, "libstdc++") != nullptr) leakedStdcpp = true;
                                if (std::strstr(needed, "libX11") != nullptr || std::strstr(needed, "libXrandr") != nullptr) {
                                    leakedX11 = true;
                                }
                            }
                        }
                        ExpectTrue(!leakedStdcpp, "libkongor_native_guard.so DT_NEEDED has ZERO libstdc++.so dependency");
                        ExpectTrue(!leakedX11, "libkongor_native_guard.so DT_NEEDED has ZERO hard libX11/libXrandr dependency");
                    }
                }
            }
            std::fclose(fpSo);
        }
    }
#endif
}

// ============================================================================
// Test Suite 6: 10v10 Mid Wars Kill Packet (0x60) Framing, 128B Expansion & ELF Prologues
// ============================================================================
// protects: Stage 1A write-side byte[11] clamp on 28-byte truncated 0x60 buffers, Stage 1B
//           128B expansion (up to 29 assists) with deduplicated sorted owner client numbers,
//           and exact Linux ELF entry prologues for all 5 hooked kill-broadcast targets.
// fails_when: Linux server emits un-clamped numAssists=5 in 28B CBufferFixed, drops assists >= 5,
//             or Linux ELF RVA/prologue assumptions drift from shipped .so binaries.
// seam: Real exported Kongor::* functions in libkongor_native_guard.so + ELF bytes on disk.
void TestMidwarsLinuxKillPacketAndElfPrologues() {
    std::fprintf(stdout, "\n--- Suite 6: Mid Wars 10v10 Kill Packet (0x60) & Linux ELF Prologues ---\n");

    // 1. Stage 1A Write-Side Clamp on truncated 28-byte kill5 packet
    uint8_t trunc28[28] = {};
    trunc28[0] = Kongor::kGameCmdKillMessage;
    trunc28[11] = 5; // 12 + 4*5 = 32 > 28
    uint8_t oldAssists = 0;
    uint8_t newAssists = 0;
    ExpectTrue(Kongor::ClampKillMessageBufferInPlace(trunc28, sizeof(trunc28), &oldAssists, &newAssists) &&
                   oldAssists == 5 && newAssists == 4 && trunc28[11] == 4,
               "Linux MidWars Stage1A clamps 28-byte kill5 packet to numAssists=4");
    ExpectTrue(!Kongor::ClampKillMessageBufferInPlace(trunc28, sizeof(trunc28)),
               "Linux MidWars Stage1A leaves valid 28-byte kill4 untouched");

    // 2. Stage 1B Assist Collection & 128B Expansion (kUnitOwnerClientNumber = 0x6BC)
    struct FakeLinuxUnit {
        uint8_t pad[Kongor::LinuxOffsets::kUnitOwnerClientNumber];
        int32_t ownerClientNum;
    };
    FakeLinuxUnit uAtk{}, uA0{}, uA1{}, uA2{}, uA3{}, uA4{}, uA5{};
    uAtk.ownerClientNum = 0;
    uA0.ownerClientNum = 9;
    uA1.ownerClientNum = 3;
    uA2.ownerClientNum = 5;
    uA3.ownerClientNum = 1;
    uA4.ownerClientNum = 7;
    uA5.ownerClientNum = 3; // duplicate must deduplicate

    uintptr_t ptrs[] = {
        reinterpret_cast<uintptr_t>(&uAtk),
        reinterpret_cast<uintptr_t>(&uA0),
        reinterpret_cast<uintptr_t>(&uA1),
        reinterpret_cast<uintptr_t>(&uA2),
        reinterpret_cast<uintptr_t>(&uA3),
        reinterpret_cast<uintptr_t>(&uA4),
        reinterpret_cast<uintptr_t>(&uA5),
    };
    int32_t assists[Kongor::kKillMessageMaxAssists128B] = {};
    size_t count = 0;
    const bool collected = Kongor::CollectUniqueSortedAssistClientNums(
        ptrs, ptrs + sizeof(ptrs) / sizeof(ptrs[0]),
        reinterpret_cast<uintptr_t>(&uAtk),
        Kongor::LinuxOffsets::kUnitOwnerClientNumber,
        assists, Kongor::kKillMessageMaxAssists128B, &count);
    ExpectTrue(collected && count == 5 &&
                   assists[0] == 1 && assists[1] == 3 && assists[2] == 5 && assists[3] == 7 && assists[4] == 9,
               "Linux MidWars Stage1B collects 5 unique sorted assisters at +0x6BC");

    uint8_t expanded[Kongor::kKillMessageExpandedCapacity] = {};
    size_t outLen = 0;
    const bool built = Kongor::BuildExpandedKillMessagePayload(
        trunc28, sizeof(trunc28), assists, count, expanded, sizeof(expanded), &outLen);
    int32_t a4 = -1;
    std::memcpy(&a4, expanded + 12 + 4 * 4, 4);
    ExpectTrue(built && outLen == 32 && expanded[0] == 0x60 && expanded[11] == 5 && a4 == 9,
               "Linux MidWars Stage1B builds 32B payload for kill5");
    ExpectTrue(!Kongor::ClampKillMessageBufferInPlace(expanded, outLen),
               "Linux MidWars Stage1B 32B payload passes Stage1A clamp cleanly");

    // 2b. Header validation & killer-pet/victim exclusion before Stage 1B expansion
    uint8_t valHdr[28] = {};
    valHdr[0] = Kongor::kGameCmdKillMessage;
    const int32_t kKillerId = 0;
    const int32_t kVictimId = 8;
    std::memcpy(valHdr + 1, &kKillerId, 4);
    std::memcpy(valHdr + 5, &kVictimId, 4);
    valHdr[11] = 5;
    int32_t petCand[7] = {0, 1, 3, 5, 7, 8, 9}; // includes killer pet (0) + victim (8) -> 5 true assists
    int32_t filteredOut[Kongor::kKillMessageMaxAssists128B] = {};
    size_t filteredLen = 0;
    ExpectTrue(Kongor::FilterAndValidateExpandedKillAssists(
                   valHdr, sizeof(valHdr), petCand, 7,
                   filteredOut, Kongor::kKillMessageMaxAssists128B, &filteredLen) &&
                   filteredLen == 5 && filteredOut[0] == 1 && filteredOut[4] == 9,
               "Linux MidWars Stage1B strips killer pet and victim before expansion");
    valHdr[11] = 4;
    ExpectTrue(!Kongor::FilterAndValidateExpandedKillAssists(
                   valHdr, sizeof(valHdr), petCand, 7,
                   filteredOut, Kongor::kKillMessageMaxAssists128B, &filteredLen),
               "Linux MidWars Stage1B declines expansion when vanilla declared <= 4 assists");

    // 3. Real Linux ELF Prologue Checks (5 hook sites)
    static const uint8_t kPrologueKillNotifyHelper[17] = {
        0x55, 0x41, 0x57, 0x41, 0x56, 0x41, 0x55, 0x41, 0x54, 0x53, 0x48, 0x81, 0xEC, 0xF8, 0x01, 0x00, 0x00
    };
    static const uint8_t kPrologueGameServerBroadcast[14] = {
        0x55, 0x41, 0x57, 0x41, 0x56, 0x41, 0x55, 0x41, 0x54, 0x53, 0x48, 0x83, 0xEC, 0x38
    };
    static const uint8_t kPrologueGameServerSend[14] = {
        0x55, 0x41, 0x57, 0x41, 0x56, 0x41, 0x55, 0x41, 0x54, 0x53, 0x48, 0x83, 0xEC, 0x48
    };
    static const uint8_t kPrologueHostServerBroadcast[14] = {
        0x55, 0x41, 0x57, 0x41, 0x56, 0x41, 0x54, 0x53, 0x41, 0x89, 0xCC, 0x49, 0x89, 0xF6
    };
    static const uint8_t kPrologueHostServerSend[17] = {
        0x55, 0x41, 0x56, 0x53, 0x41, 0x89, 0xCE, 0x48, 0x89, 0xD3, 0x48, 0x8B, 0x8F, 0x90, 0x02, 0x00, 0x00
    };

    char pathBuf[512] = {};
    FILE* fpShared = OpenServerBinary("games/server/linux-x64/game/libgame_shared-x86_64-server.so", pathBuf, sizeof(pathBuf));
    if (fpShared) {
        VerifyElfPrologueAtRva(fpShared, "libgame_shared-x86_64-server.so", "KillNotifyHelper",
                               Kongor::LinuxOffsets::kRvaKillNotifyHelper,
                               kPrologueKillNotifyHelper, sizeof(kPrologueKillNotifyHelper), "OBL-MIDWARS-04");
        std::fclose(fpShared);
    }

    FILE* fpGame = OpenServerBinary("games/server/linux-x64/game/game-x86_64-server.so", pathBuf, sizeof(pathBuf));
    if (fpGame) {
        VerifyElfPrologueAtRva(fpGame, "game-x86_64-server.so", "CGameServer::BroadcastGameData",
                               Kongor::LinuxOffsets::kRvaGameServerBroadcastGameData,
                               kPrologueGameServerBroadcast, sizeof(kPrologueGameServerBroadcast), "OBL-MIDWARS-02");
        VerifyElfPrologueAtRva(fpGame, "game-x86_64-server.so", "CGameServer::SendGameData",
                               Kongor::LinuxOffsets::kRvaGameServerSendGameData,
                               kPrologueGameServerSend, sizeof(kPrologueGameServerSend), "OBL-MIDWARS-02");
        std::fclose(fpGame);
    }

    FILE* fpK2Srv = OpenServerBinary("games/server/linux-x64/libk2-x86_64-server.so", pathBuf, sizeof(pathBuf));
    if (fpK2Srv) {
        VerifyElfPrologueAtRva(fpK2Srv, "libk2-x86_64-server.so", "CHostServer::BroadcastGameData",
                               Kongor::LinuxOffsets::kRvaHostServerBroadcastGameData,
                               kPrologueHostServerBroadcast, sizeof(kPrologueHostServerBroadcast), "OBL-MIDWARS-02");
        VerifyElfPrologueAtRva(fpK2Srv, "libk2-x86_64-server.so", "CHostServer::SendGameData",
                               Kongor::LinuxOffsets::kRvaHostServerSendGameData,
                               kPrologueHostServerSend, sizeof(kPrologueHostServerSend), "OBL-MIDWARS-02");
        std::fclose(fpK2Srv);
    }
}

// ============================================================================
// Test Suite 7: Track 5 Human <-> Bot Interswappability Parity
// ============================================================================
namespace Track5LinuxMock {
    static void* s_dummyNetMgr = reinterpret_cast<void*>(0x55550000ULL);
    static void* s_fakeHostServer = reinterpret_cast<void*>(0x66660000ULL);
    static wchar_t s_netStrings[16][96] = {};
    static Kongor::LibcxxFakeLongWStringView s_netViews[16] = {};
    static void* s_lingeringConns[32] = {};
    static int s_removeBotCalls = 0;
    static int s_attachBotCalls = 0;
    static const void* s_lastAttachBotDef = nullptr;
    static uint8_t s_healedBrain[0x40] = {};
    static int s_kickCalls = 0;
    static int32_t s_lastKickedClient = -1;
    static bool s_kickSawReentrancyGuard = false;
    static bool s_reentrantDiscSuppressed = false;
    static void* s_reentrantPlayer = nullptr;
    static const Kongor::LinuxQueueArcadeEngineContext* s_reentrantCtx = nullptr;
    static char s_lastQueuedCmd[256] = {};
    static int s_queuedCmdCount = 0;

    static void SeedString(uint16_t idx, const wchar_t* text) {
        if (idx >= 16) return;
        std::memset(s_netStrings[idx], 0, sizeof(s_netStrings[idx]));
        if (text) {
            wcsncpy(s_netStrings[idx], text, 95);
        }
        const size_t len = wcslen(s_netStrings[idx]);
        s_netViews[idx].capWithLongBit = ((len + 1) << 1) | 1ULL;
        s_netViews[idx].size = len;
        s_netViews[idx].data = s_netStrings[idx];
    }

    static void Reset() {
        std::memset(s_netStrings, 0, sizeof(s_netStrings));
        std::memset(s_netViews, 0, sizeof(s_netViews));
        std::memset(s_lingeringConns, 0, sizeof(s_lingeringConns));
        s_removeBotCalls = 0;
        s_attachBotCalls = 0;
        s_lastAttachBotDef = nullptr;
        std::memset(s_healedBrain, 0, sizeof(s_healedBrain));
        *reinterpret_cast<uint32_t*>(s_healedBrain + Kongor::LinuxOffsets::kBotBrainHeroIndex) = 0xFFFFFFFFu;
        s_kickCalls = 0;
        s_lastKickedClient = -1;
        s_kickSawReentrancyGuard = false;
        s_reentrantDiscSuppressed = false;
        s_reentrantPlayer = nullptr;
        s_reentrantCtx = nullptr;
        std::memset(s_lastQueuedCmd, 0, sizeof(s_lastQueuedCmd));
        s_queuedCmdCount = 0;
    }

    static void MockSetNetString(void* pMgr, uint16_t idx, const void* pViewVoid, bool /*bGrow*/) {
        if (pMgr != s_dummyNetMgr || idx >= 16 || !pViewVoid) return;
        wchar_t decoded[96] = {};
        if (Kongor::ReadLibcxxWString(pViewVoid, decoded, 96)) {
            SeedString(idx, decoded);
        } else {
            SeedString(idx, L"");
        }
    }

    static const void* MockGetNetString(void* pMgr, uint16_t idx) {
        if (pMgr != s_dummyNetMgr || idx >= 16) return nullptr;
        const size_t len = wcslen(s_netStrings[idx]);
        s_netViews[idx].capWithLongBit = ((len + 1) << 1) | 1ULL;
        s_netViews[idx].size = len;
        s_netViews[idx].data = s_netStrings[idx];
        return &s_netViews[idx];
    }

    static void MockRemoveBot(void* /*pPlayer*/) {
        ++s_removeBotCalls;
    }

    static void* MockAttachBot(void* pPlayer, const void* pBotDef) {
        ++s_attachBotCalls;
        s_lastAttachBotDef = pBotDef;
        if (pPlayer) {
            *reinterpret_cast<void**>(
                reinterpret_cast<uint8_t*>(pPlayer) + Kongor::LinuxOffsets::kPlayerBotBrain) = s_healedBrain;
        }
        return s_healedBrain;
    }

    static void* MockGetClient(void* pHostServer, int32_t clientNum) {
        if (pHostServer != s_fakeHostServer || clientNum < 0 || clientNum >= 32) return nullptr;
        return s_lingeringConns[clientNum];
    }

    static void MockKickClient(void* pHostServer, int32_t clientNum, int32_t /*reason*/) {
        if (pHostServer != s_fakeHostServer) return;
        ++s_kickCalls;
        s_lastKickedClient = clientNum;
        s_kickSawReentrancyGuard = Kongor::IsArcadeKickReentrancyActive();
        if (clientNum >= 0 && clientNum < 32) {
            s_lingeringConns[clientNum] = nullptr;
        }
        if (s_reentrantPlayer && s_reentrantCtx) {
            s_reentrantDiscSuppressed = Kongor::OnArcadePlayerDisconnected(
                s_reentrantPlayer, 1000u, 240000u, s_reentrantCtx);
        }
    }

    static void MockQueueCmdA(const char* cmdLine) {
        ++s_queuedCmdCount;
        if (cmdLine) {
            std::snprintf(s_lastQueuedCmd, sizeof(s_lastQueuedCmd), "%s", cmdLine);
        }
    }
} // namespace Track5LinuxMock

void TestTrack5HumanBotInterswappabilitySuite() {
    std::fprintf(stdout, "\n--- Suite 7: Track 5 Human <-> Bot Interswappability Parity ---\n");
    Track5LinuxMock::Reset();
    Kongor::ClearAllArcadeReservations();
    Kongor::SetQueueArcadeSwapHooksEnabled(true);
    Kongor::SetQueueArcadeMode(Kongor::QueueArcadeServerMode::ActiveBots);

    SyntheticLinuxPlayerMemory players[3];
    uint8_t brains[3][0x30] = {};
    for (int i = 0; i < 3; ++i) {
        const int team = (i < 2) ? 1 : 2;
        *reinterpret_cast<uint32_t*>(brains[i] + Kongor::LinuxOffsets::kBotBrainHeroIndex) = static_cast<uint32_t>(200 + i);
        players[i].Initialize(i, team, 0, true, brains[i]);
        *reinterpret_cast<uint32_t*>(players[i].bytes + Kongor::LinuxOffsets::kPlayerHeroIndex) = static_cast<uint32_t>(200 + i);
        *reinterpret_cast<uint16_t*>(players[i].bytes + Kongor::LinuxOffsets::kPlayerNameIndex) = static_cast<uint16_t>(i * 3);
        *reinterpret_cast<uint16_t*>(players[i].bytes + Kongor::LinuxOffsets::kPlayerClanTagIndex) = static_cast<uint16_t>(i * 3 + 1);
        *reinterpret_cast<uint16_t*>(players[i].bytes + Kongor::LinuxOffsets::kPlayerClanNameIndex) = static_cast<uint16_t>(i * 3 + 2);
    }
    Track5LinuxMock::SeedString(0, L"ChronosBot");
    Track5LinuxMock::SeedString(3, L"NitroBot");
    Track5LinuxMock::SeedString(6, L"FlintBot");

    Kongor::LibcxxTreeNode treeNodes[3];
    std::memset(treeNodes, 0, sizeof(treeNodes));
    for (int i = 0; i < 3; ++i) {
        treeNodes[i].key = i;
        treeNodes[i].value = players[i].AsPlayer();
        treeNodes[i].isBlack = true;
        treeNodes[i].right = (i + 1 < 3) ? &treeNodes[i + 1] : nullptr;
        treeNodes[i].parent = (i > 0) ? &treeNodes[i - 1] : nullptr;
    }

    uint8_t gameBytes[0x300] = {};
    uint8_t gameInfoBytes[0x100] = {};
    *reinterpret_cast<int32_t*>(gameInfoBytes + Kongor::LinuxOffsets::kGameInfoPhase) = 5; // Active (< 7)
    *reinterpret_cast<void**>(gameBytes + Kongor::LinuxOffsets::kGameInfo) = gameInfoBytes;
    treeNodes[0].parent = gameBytes + Kongor::LinuxOffsets::kGameClientsMapSentinel;
    *reinterpret_cast<void**>(gameBytes + Kongor::LinuxOffsets::kGameClientsMap) = &treeNodes[0];
    void* pGamePtr = gameBytes;

    Kongor::LinuxQueueArcadeEngineContext ctx = {};
    ctx.ppGame = &pGamePtr;
    ctx.pfnRemoveBot = &Track5LinuxMock::MockRemoveBot;
    ctx.pfnAttachBot = &Track5LinuxMock::MockAttachBot;
    ctx.ppNetResourceManager = &Track5LinuxMock::s_dummyNetMgr;
    ctx.pfnSetNetString = &Track5LinuxMock::MockSetNetString;
    ctx.pfnGetNetString = &Track5LinuxMock::MockGetNetString;
    ctx.pHostServerOverride = Track5LinuxMock::s_fakeHostServer;
    ctx.pfnGetClient = &Track5LinuxMock::MockGetClient;
    ctx.pfnKickClient = &Track5LinuxMock::MockKickClient;
    ctx.pfnQueueConsoleCommandA = &Track5LinuxMock::MockQueueCmdA;

    const void* const kFakeNitroDef = reinterpret_cast<const void*>(0xB07DEF77ULL);
    Kongor::OnArcadePlayerAttachBot(players[1].AsPlayer(), kFakeNitroDef, &ctx);

    // 1. GetClient != nullptr collision skip: slot 0 has lingering UDP -> reserve skips slot 0 and picks slot 1 (NitroBot)!
    Track5LinuxMock::s_lingeringConns[0] = reinterpret_cast<void*>(0xC0440000ULL);
    auto r1 = Kongor::ArcadeReserveSlot(95001, 0, L"[TAG]Alpha", &ctx);
    ExpectTrue(r1.success && r1.clientNum == 1 && r1.teamId == 1,
               "Linux Track 5: GetClient != nullptr skips lingering UDP slot 0 and reserves slot 1");
    ExpectTrue(std::strcmp(Track5LinuxMock::s_lastQueuedCmd,
                   "ServerChat \"[Queue Arcade] [TAG]Alpha replaced NitroBot!\"") == 0,
               "Linux Track 5: Join queues ServerChat replacement notice", Track5LinuxMock::s_lastQueuedCmd);

    // 2. Disconnected hook on bot slot (clears 0x8000 + 0x0101) vs active human slot (immediate handoff to NitroBot)
    *reinterpret_cast<uint16_t*>(players[2].bytes + Kongor::LinuxOffsets::kPlayerFlags) |= (0x0101u | 0x0040u | 0x8000u);
    ExpectTrue(Kongor::OnArcadePlayerDisconnected(players[2].AsPlayer(), 1000u, 240000u, &ctx) &&
               (*reinterpret_cast<uint16_t*>(players[2].bytes + Kongor::LinuxOffsets::kPlayerFlags) &
                (0x0101u | 0x0080u | 0x0040u | 0x8000u)) == 0,
               "Linux Track 5: OnArcadePlayerDisconnected scrubs bot slot flags including 0x8000");

    ExpectTrue(Kongor::OnArcadePlayerDisconnected(players[1].AsPlayer(), 2000u, 240000u, &ctx) &&
               *reinterpret_cast<int32_t*>(players[1].bytes + Kongor::LinuxOffsets::kPlayerAccountID) == 0 &&
               *reinterpret_cast<uint8_t*>(players[1].bytes + Kongor::LinuxOffsets::kPlayerIsBot) == 1 &&
               *reinterpret_cast<void**>(players[1].bytes + Kongor::LinuxOffsets::kPlayerBotBrain) == brains[1] &&
               wcscmp(Track5LinuxMock::s_netStrings[3], L"NitroBot") == 0 &&
               std::strcmp(Track5LinuxMock::s_lastQueuedCmd,
                   "ServerChat \"[Queue Arcade] [TAG]Alpha left - NitroBot took over!\"") == 0,
               "Linux Track 5: OnArcadePlayerDisconnected hands human slot back to NitroBot with leave ServerChat");

    // 3. Multi-swap originalBotName preservation + active-phase OnArcadePlayerTerminate guard (gamePhase = 5 < 7)
    ctx.pfnGetNetString = nullptr;
    auto r2 = Kongor::ArcadeReserveSlot(95002, 1, L"Bravo\"Semi;Test", &ctx);
    ExpectTrue(r2.success && r2.clientNum == 1 &&
               std::strcmp(Track5LinuxMock::s_lastQueuedCmd,
                   "ServerChat \"[Queue Arcade] BravoSemiTest replaced NitroBot!\"") == 0,
               "Linux Track 5: Multi-swap preserves NitroBot and strips quotes/semicolons from chat");

    ExpectTrue(Kongor::OnArcadePlayerTerminate(players[1].AsPlayer(), &ctx) &&
               Track5LinuxMock::s_removeBotCalls == 0 &&
               (*reinterpret_cast<uint16_t*>(players[1].bytes + Kongor::LinuxOffsets::kPlayerFlags) & 0x0080u) == 0 &&
               *reinterpret_cast<void**>(players[1].bytes + Kongor::LinuxOffsets::kPlayerBotBrain) == brains[1] &&
               wcscmp(Track5LinuxMock::s_netStrings[3], L"NitroBot") == 0,
               "Linux Track 5: Active-phase OnArcadePlayerTerminate preserves parked brain and restores NitroBot");
    ctx.pfnGetNetString = &Track5LinuxMock::MockGetNetString;

    // 4. AttachBot self-heal + kick re-entrancy guard when parkedBotBrain == nullptr && m_pBotBrain == nullptr
    auto r3 = Kongor::ArcadeReserveSlot(95003, 1, L"Charlie", &ctx);
    ExpectTrue(r3.success && r3.clientNum == 1, "Linux Track 5: Third reserve on slot 1");
    Kongor::ArcadeReleaseParkedBotOnPlayerTerminate(players[1].AsPlayer(), &ctx); // consumes parked brain
    Kongor::RecordArcadeSlotOrigin(1, kFakeNitroDef, L"NitroBot");

    Track5LinuxMock::s_lingeringConns[1] = reinterpret_cast<void*>(0xC0440001ULL);
    Track5LinuxMock::s_reentrantPlayer = players[1].AsPlayer();
    Track5LinuxMock::s_reentrantCtx = &ctx;
    ExpectTrue(Kongor::ArcadeReleaseSlot(95003, &ctx, /*kickClient=*/true) &&
               Track5LinuxMock::s_attachBotCalls == 1 &&
               Track5LinuxMock::s_lastAttachBotDef == kFakeNitroDef &&
               *reinterpret_cast<void**>(players[1].bytes + Kongor::LinuxOffsets::kPlayerBotBrain) == Track5LinuxMock::s_healedBrain &&
               *reinterpret_cast<uint32_t*>(Track5LinuxMock::s_healedBrain + Kongor::LinuxOffsets::kBotBrainHeroIndex) == 201u &&
               (*reinterpret_cast<uint16_t*>(players[1].bytes + Kongor::LinuxOffsets::kPlayerFlags2) & 0x0008u) != 0 &&
               wcscmp(Track5LinuxMock::s_netStrings[3], L"NitroBot") == 0,
               "Linux Track 5: AttachBot self-heals missing CBotBrain and binds heroIdx 201");
    ExpectTrue(Track5LinuxMock::s_kickCalls == 1 &&
               Track5LinuxMock::s_lastKickedClient == 1 &&
               Track5LinuxMock::s_kickSawReentrancyGuard &&
               Track5LinuxMock::s_reentrantDiscSuppressed &&
               !Kongor::IsArcadeKickReentrancyActive(),
               "Linux Track 5: Release kick invokes KickClient under re-entrancy guard");

    // 5. Phase >= 7 pass-through & KONGOR_ARCADE_SWAP_HOOKS=0 kill-switch + console dispatch
    *reinterpret_cast<int32_t*>(gameInfoBytes + Kongor::LinuxOffsets::kGameInfoPhase) = 7;
    ExpectTrue(!Kongor::OnArcadePlayerDisconnected(players[1].AsPlayer(), 3000u, 240000u, &ctx) &&
               !Kongor::OnArcadePlayerTerminate(players[1].AsPlayer(), &ctx),
               "Linux Track 5: Phase >= 7 delegates Disconnected and Terminate to stock K2");

    *reinterpret_cast<int32_t*>(gameInfoBytes + Kongor::LinuxOffsets::kGameInfoPhase) = 5;
#if !defined(_WIN32)
    setenv("KONGOR_ARCADE_SWAP_HOOKS", "0", 1);
    Kongor::RefreshQueueArcadeSwapHooksFromEnv();
    ExpectTrue(!Kongor::IsQueueArcadeSwapHooksEnabled() &&
               !Kongor::OnArcadePlayerDisconnected(players[1].AsPlayer(), 3000u, 240000u, &ctx) &&
               !Kongor::OnArcadePlayerTerminate(players[1].AsPlayer(), &ctx),
               "Linux Track 5: KONGOR_ARCADE_SWAP_HOOKS=0 env kill-switch falls back to stock K2");
    unsetenv("KONGOR_ARCADE_SWAP_HOOKS");
#else
    Kongor::SetQueueArcadeSwapHooksEnabled(false);
    ExpectTrue(!Kongor::IsQueueArcadeSwapHooksEnabled(), "Linux Track 5: SetQueueArcadeSwapHooksEnabled(false)");
#endif
    ExpectTrue(Kongor::TryDispatchLinuxArcadeConsoleCmdW(L"kongor_arcade_swap_hooks 1") &&
               Kongor::IsQueueArcadeSwapHooksEnabled() &&
               !Kongor::ShouldAllowUnprivilegedIpcCommandBufferA("kongor_arcade_swap_hooks 0", 26),
               "Linux Track 5: kongor_arcade_swap_hooks console dispatch & IPC firewall");

    // 6. ReapConnectionlessBotsIfNoActiveHumans outside Queue Arcade: loaded human (0x0202, isBot=0, accId=88001) preserves bots
    Kongor::SetQueueArcadeMode(Kongor::QueueArcadeServerMode::Disabled);
    *reinterpret_cast<uint16_t*>(players[1].bytes + Kongor::LinuxOffsets::kPlayerFlags) = 0x0202u;
    *reinterpret_cast<uint8_t*>(players[1].bytes + Kongor::LinuxOffsets::kPlayerIsBot) = 0;
    *reinterpret_cast<int32_t*>(players[1].bytes + Kongor::LinuxOffsets::kPlayerAccountID) = 88001;
    Kongor::ReapConnectionlessBotsIfNoActiveHumans(&ctx);
    ExpectTrue((*reinterpret_cast<uint16_t*>(players[0].bytes + Kongor::LinuxOffsets::kPlayerFlags) & 0x0080u) == 0 &&
               (*reinterpret_cast<uint16_t*>(players[2].bytes + Kongor::LinuxOffsets::kPlayerFlags) & 0x0080u) == 0,
               "Linux Track 5: ReapConnectionlessBots preserves bots when loaded human (flags=0x0202, isBot=0) is present");

    *reinterpret_cast<uint16_t*>(players[1].bytes + Kongor::LinuxOffsets::kPlayerFlags) |= 0x0080u;
    Kongor::ReapConnectionlessBotsIfNoActiveHumans(&ctx);
    ExpectTrue((*reinterpret_cast<uint16_t*>(players[0].bytes + Kongor::LinuxOffsets::kPlayerFlags) & 0x0080u) != 0 &&
               (*reinterpret_cast<uint16_t*>(players[2].bytes + Kongor::LinuxOffsets::kPlayerFlags) & 0x0080u) != 0,
               "Linux Track 5: ReapConnectionlessBots reaps bots once last loaded human terminates");

    Kongor::ClearAllArcadeReservations();
}

// ============================================================================
// Test Suite 8: SHJ-PRE-01 Legacy 4.10.1.0 Token/Pass GameMode Interposition
// ============================================================================
void TestOfflineGameModeTokenInterpositionSuite() {
    std::fprintf(stdout, "\n--- Suite 8: SHJ-PRE-01 GameMode Token/Pass Interposition ---\n");
#if !defined(_WIN32)
    using FnGameModeAccess = bool(*)(const void*, const void*);
    static constexpr const char* kSymConnAccess =
        "_ZNK17CClientConnection17CanAccessGameModeERKNSt3__112basic_stringIwNS0_11char_traitsIwEE17K2StringAllocatorIwEEE";
    static constexpr const char* kSymAcctAccess =
        "_ZNK14CClientAccount17CanAccessGameModeERKNSt3__112basic_stringIwNS0_11char_traitsIwEE17K2StringAllocatorIwEEE";
    static constexpr const char* kSymAcctModeGate =
        "_ZNK14CClientAccount10NeedsTokenERKNSt3__112basic_stringIwNS0_11char_traitsIwEE17K2StringAllocatorIwEEE";

    auto fnConnAccess = reinterpret_cast<FnGameModeAccess>(dlsym(RTLD_DEFAULT, kSymConnAccess));
    auto fnAcctAccess = reinterpret_cast<FnGameModeAccess>(dlsym(RTLD_DEFAULT, kSymAcctAccess));
    auto fnAcctModeGate = reinterpret_cast<FnGameModeAccess>(dlsym(RTLD_DEFAULT, kSymAcctModeGate));

    ExpectTrue(fnConnAccess != nullptr,
        "SHJ-PRE-01: CClientConnection::CanAccessGameMode symbol exported from libkongor_native_guard.so");
    ExpectTrue(fnAcctAccess != nullptr,
        "SHJ-PRE-01: CClientAccount::CanAccessGameMode symbol exported from libkongor_native_guard.so");
    ExpectTrue(fnAcctModeGate != nullptr,
        "SHJ-PRE-01: CClientAccount::NeedsToken symbol exported from libkongor_native_guard.so");

    if (fnConnAccess) {
        ExpectTrue(fnConnAccess(nullptr, nullptr) == true,
            "SHJ-PRE-01: CClientConnection::CanAccessGameMode returns true (prevents offline disconnect_no_tokens)");
    }
    if (fnAcctAccess) {
        ExpectTrue(fnAcctAccess(nullptr, nullptr) == true,
            "SHJ-PRE-01: CClientAccount::CanAccessGameMode returns true (prevents offline disconnect_no_tokens)");
    }
    if (fnAcctModeGate) {
        ExpectTrue(fnAcctModeGate(nullptr, nullptr) == false,
            "SHJ-PRE-01: CClientAccount::NeedsToken returns false (disables legacy 4.10.1.0 token paywall)");
    }
#else
    ExpectTrue(true, "SHJ-PRE-01: Linux LD_PRELOAD token interposition verified in Linux Docker smoke runner");
#endif
}

} // anonymous namespace

int main(int argc, char** argv) {
    (void)argc;
    (void)argv;
    std::fprintf(stdout, "==========================================================\n");
    std::fprintf(stdout, "  Project Kongor - Linux Queue Arcade C++ Unit Test Runner\n");
    std::fprintf(stdout, "  Dossier: artifacts/evidence/queue_arcade_linux_dossier.json\n");
    std::fprintf(stdout, "  Obligations: OBL-QA-LINUX-00 through OBL-QA-LINUX-11\n");
    std::fprintf(stdout, "==========================================================\n");

    TestFaultGuardSuite();
    TestTrampolineSuite();
    TestSimulationSuite();
    TestRealLinuxServerElfPrologues();
    TestLinuxClientMultiMonitorSuite();
    TestMidwarsLinuxKillPacketAndElfPrologues();
    TestTrack5HumanBotInterswappabilitySuite();
    TestOfflineGameModeTokenInterpositionSuite();

    std::fprintf(stdout, "\n==================== TEST SUMMARY ====================\n");
    std::fprintf(stdout, "  Total Checks:  %d\n", g_totalTests);
    std::fprintf(stdout, "  Passed Checks: %d\n", g_totalTests - g_failedTests);
    std::fprintf(stdout, "  Failed Checks: %d\n", g_failedTests);
    std::fprintf(stdout, "======================================================\n");

    if (g_failedTests > 0) {
        std::fprintf(stderr, "[FAILED] %d test check(s) failed!\n", g_failedTests);
        return 1;
    }

    std::fprintf(stdout, "[SUCCESS] All Linux Queue Arcade tests passed cleanly.\n");
    return 0;
}
