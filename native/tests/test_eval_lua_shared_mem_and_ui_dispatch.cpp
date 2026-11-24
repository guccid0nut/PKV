// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ============================================================================
// Wave 5 Part A NativeGuard Seam Tests (MCP-246, MCP-231, MCP-234)
//
// Test Value Bar:
// 1. protects:
//    - [MCP-246] EvalLuaSync hex-encoded __kongor_eval_res round-trip (multi-line '\n', ';',
//      quotes, '%s' format specifiers, UTF-8) + GetGamePhase() / Host.GetGamePhase() Lua shim
//    - [MCP-231] Immediate fast-fail completion when KongorSharedMem is unbound (no 2.5s stall)
//    - [MCP-234] DispatchUiEvent JSON unescaping, K2 CConsole-safe string.char(...) encoding,
//      and onclicklua / GetCallback('onclick') / _button suffix actuation
// 2. fails_when:
//    - __kongor_eval_res drops or splits multi-line/semicolon output or mangles '%' format tokens
//    - EvalLuaSync omits GetGamePhase / Host.GetGamePhase shim or omits unbound KongorSharedMem fast-fail
//    - DispatchUiEvent truncates JSON on escaped quotes, emits raw quotes/semicolons/backslashes
//      into Script "...", or only calls b:DoEvent() ignoring onclicklua / GetCallback('onclick')
// 3. seam:
//    - Real compiled Release version.dll exports (Kongor_NativeGuard_DecodeEvalResArgs,
//      Kongor_NativeGuard_BuildEvalRunnerCmdUtf8, Kongor_NativeGuard_BuildDispatchUiEventCmdUtf8)
// ============================================================================

#include <cstdio>
#include <cstdlib>
#include <cstdint>
#include <cstring>
#include <string>
#include <vector>

#ifdef _WIN32
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#endif

static int g_failures = 0;

#define EXPECT_TRUE(cond, msg)                                                                  \
    do {                                                                                        \
        if (!(cond)) {                                                                          \
            std::fprintf(stderr, "[FAIL] %s:%d: %s (%s)\n"                                      \
                                 "  -> Fix/Diagnose: Run scripts/Invoke-KongorTestLock.ps1 "    \
                                 "-Owner test -- ctest --test-dir native/build -C Release -R "  \
                                 "test_eval_lua_shared_mem_and_ui_dispatch --output-on-failure\n",              \
                         __FILE__, __LINE__, (msg), #cond);                                     \
            ++g_failures;                                                                       \
        }                                                                                       \
    } while (0)

static std::string ToHexWithPrefix(const std::string& s) {
    static const char kHex[] = "0123456789abcdef";
    std::string out = "hex:";
    out.reserve(4 + s.size() * 2);
    for (unsigned char b : s) {
        out.push_back(kHex[(b >> 4) & 0x0F]);
        out.push_back(kHex[b & 0x0F]);
    }
    return out;
}

static void AssertK2ConsoleSafeScriptLiteral(const std::string& cmd, const char* contextLabel) {
    EXPECT_TRUE(cmd.size() > 9 && cmd.compare(0, 8, "Script \"") == 0 && cmd.back() == '"',
                "Command must be wrapped as Script \"...\"");
    if (cmd.size() <= 9) return;
    const std::string inner = cmd.substr(8, cmd.size() - 9);
    EXPECT_TRUE(inner.find('\n') == std::string::npos, contextLabel);
    EXPECT_TRUE(inner.find('\r') == std::string::npos, contextLabel);
    EXPECT_TRUE(inner.find(';') == std::string::npos, contextLabel);
    EXPECT_TRUE(inner.find('%') == std::string::npos, contextLabel);
    EXPECT_TRUE(inner.find('\\') == std::string::npos, contextLabel);
    EXPECT_TRUE(inner.find('"') == std::string::npos, contextLabel);
}

#ifdef _WIN32
static std::string GetExecutableDirectory() {
    char path[MAX_PATH] = {};
    DWORD n = GetModuleFileNameA(nullptr, path, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) return ".";
    std::string s(path, n);
    size_t pos = s.find_last_of("\\/");
    return (pos == std::string::npos) ? "." : s.substr(0, pos);
}

static HMODULE LoadSiblingBuildDll(const char* relativeSubdir, const char* dllName) {
    std::string exeDir = GetExecutableDirectory();
    std::vector<std::string> candidates = {
        exeDir + "\\..\\..\\" + relativeSubdir + "\\Release\\" + dllName,
        exeDir + "\\..\\" + relativeSubdir + "\\" + dllName,
        exeDir + "\\" + dllName
    };
    for (const std::string& cand : candidates) {
        HMODULE h = LoadLibraryExA(cand.c_str(), nullptr, 0);
        if (h != nullptr) {
            return h;
        }
    }
    return nullptr;
}

static void TestWave5ANativeGuardExports() {
    SetEnvironmentVariableA("KONGOR_NATIVE_GUARD_TEST_MODE", "1");

    HMODULE hGuard = LoadSiblingBuildDll("native_guard_build", "version.dll");
    EXPECT_TRUE(hGuard != nullptr, "Failed to load built version.dll from native/build/native_guard_build/Release");
    if (!hGuard) return;

    using PFN_DecodeEvalRes = int(__cdecl*)(const char*, uint64_t*, int*, char*, uint32_t);
    using PFN_BuildEvalRunner = int(__cdecl*)(uint64_t, uint32_t, char*, uint32_t);
    using PFN_BuildDispatchUi = int(__cdecl*)(const char*, char*, uint32_t, char*, uint32_t);

    auto pfnDecodeEvalRes = reinterpret_cast<PFN_DecodeEvalRes>(
        GetProcAddress(hGuard, "Kongor_NativeGuard_DecodeEvalResArgs"));
    auto pfnBuildEvalRunner = reinterpret_cast<PFN_BuildEvalRunner>(
        GetProcAddress(hGuard, "Kongor_NativeGuard_BuildEvalRunnerCmdUtf8"));
    auto pfnBuildDispatchUi = reinterpret_cast<PFN_BuildDispatchUi>(
        GetProcAddress(hGuard, "Kongor_NativeGuard_BuildDispatchUiEventCmdUtf8"));

    EXPECT_TRUE(pfnDecodeEvalRes != nullptr, "Export Kongor_NativeGuard_DecodeEvalResArgs must exist");
    EXPECT_TRUE(pfnBuildEvalRunner != nullptr, "Export Kongor_NativeGuard_BuildEvalRunnerCmdUtf8 must exist");
    EXPECT_TRUE(pfnBuildDispatchUi != nullptr, "Export Kongor_NativeGuard_BuildDispatchUiEventCmdUtf8 must exist");

    if (pfnDecodeEvalRes) {
        std::printf("[TEST] [MCP-246] DecodeEvalResArgs preserves multi-line, ';', quotes, and '%%' tokens...\n");
        const std::string adversarial =
            "line1: ok\nline2; Quit; StartGame\r\nfmt=%s %d %% %!s(MISSING) \"double\" 'single' \\path";
        const std::string encodedCmdArgs = "9876543210123 1 " + ToHexWithPrefix(adversarial) + "\r\n";

        uint64_t evalId = 0;
        int ok = 0;
        char decodedBuf[4096] = {};
        int n = pfnDecodeEvalRes(encodedCmdArgs.c_str(), &evalId, &ok, decodedBuf, sizeof(decodedBuf));
        EXPECT_TRUE(n == static_cast<int>(adversarial.size()), "Decoded byte length must match adversarial input exactly");
        EXPECT_TRUE(evalId == 9876543210123ULL, "Decoded 64-bit evalId must match");
        EXPECT_TRUE(ok == 1, "Decoded ok flag must be 1");
        EXPECT_TRUE(std::string(decodedBuf, static_cast<size_t>(n)) == adversarial,
                    "Hex-decoded payload must preserve newlines, semicolons, quotes, backslashes, and % specifiers byte-for-byte");

        // Error flag (0) + legacy unencoded fallback
        uint64_t errId = 0;
        int errOk = 1;
        char legacyBuf[256] = {};
        int nLegacy = pfnDecodeEvalRes("77 0 legacy error text", &errId, &errOk, legacyBuf, sizeof(legacyBuf));
        EXPECT_TRUE(nLegacy > 0 && errId == 77ULL && errOk == 0 && std::strcmp(legacyBuf, "legacy error text") == 0,
                    "Legacy plain-text __kongor_eval_res fallback must remain functional");

        // Malformed args must fail closed (return 0)
        EXPECT_TRUE(pfnDecodeEvalRes("", &errId, &errOk, legacyBuf, sizeof(legacyBuf)) == 0,
                    "Empty __kongor_eval_res args must fail closed");
        EXPECT_TRUE(pfnDecodeEvalRes("notanid 1 hex:30", &errId, &errOk, legacyBuf, sizeof(legacyBuf)) == 0,
                    "Non-numeric evalId must fail closed");
        EXPECT_TRUE(pfnDecodeEvalRes("55 X hex:30", &errId, &errOk, legacyBuf, sizeof(legacyBuf)) == 0,
                    "Invalid ok flag must fail closed");
    }

    if (pfnBuildEvalRunner) {
        std::printf("[TEST] [MCP-246 / MCP-231] BuildEvalRunnerCmdUtf8 safety, GetGamePhase shim, and fast-fail...\n");
        char runnerBuf[8192] = {};
        int len = pfnBuildEvalRunner(424242ULL, 5U, runnerBuf, sizeof(runnerBuf));
        EXPECT_TRUE(len > 0, "BuildEvalRunnerCmdUtf8 must return positive length");
        const std::string runner(runnerBuf, static_cast<size_t>(len > 0 ? len : 0));
        std::printf("  -> EvalRunner byte length = %zu\n", runner.size());

        AssertK2ConsoleSafeScriptLiteral(runner, "EvalRunner Script must contain zero CConsole splitters/escapes");
        EXPECT_TRUE(runner.size() < 1900,
                    "EvalRunner Script must stay strictly under K2 CConsole's 2048-byte command limit");
        EXPECT_TRUE(runner.find("SetEvalResult") != std::string::npos,
                    "EvalRunner must write arbitrary-length results via KongorSharedMem.SetEvalResult");
        EXPECT_TRUE(runner.find("hex:") != std::string::npos, "EvalRunner must hex-encode unbound fallback outputs with 'hex:' prefix");
        EXPECT_TRUE(runner.find("GetGamePhase") != std::string::npos, "EvalRunner must install GetGamePhase shim");
        EXPECT_TRUE(runner.find("Host.GetGamePhase") != std::string::npos, "EvalRunner must install Host.GetGamePhase shim");
        EXPECT_TRUE(runner.find("'__kongor_native_game_phase',5") != std::string::npos ||
                    runner.find("'__kongor_native_game_phase', 5") != std::string::npos,
                    "EvalRunner must propagate live native GamePhase (5) into Lua shim");
        EXPECT_TRUE(runner.find("__kongor_eval_res 424242 0 ") != std::string::npos,
                    "EvalRunner must fast-fail with explicit evalId when KongorSharedMem is unbound");
    }

    if (pfnBuildDispatchUi) {
        std::printf("[TEST] [MCP-234] BuildDispatchUiEventCmdUtf8 JSON unescaping, console-safe encoding & onclicklua...\n");
        const char* trickyJson =
            "{\"interfaceName\":\"main\",\"widgetName\":\"btn_\\\"quote\\\"_\\\\slash;semi%pct\","
            "\"eventType\":\"onclicklua\",\"onclicklua\":\"Echo('clicked;100%'); PlaySound('/ui/click.wav')\"}";
        char cmdBuf[8192] = {};
        char widgetBuf[512] = {};
        int cmdLen = pfnBuildDispatchUi(trickyJson, cmdBuf, sizeof(cmdBuf), widgetBuf, sizeof(widgetBuf));
        EXPECT_TRUE(cmdLen > 0, "BuildDispatchUiEventCmdUtf8 must succeed on adversarial JSON");
        EXPECT_TRUE(std::strcmp(widgetBuf, "btn_\"quote\"_\\slash;semi%pct") == 0,
                    "ExtractJsonUiField must accurately unescape quotes and backslashes without early truncation");

        const std::string uiCmd(cmdBuf, static_cast<size_t>(cmdLen > 0 ? cmdLen : 0));
        std::printf("  -> DispatchUiEvent byte length = %zu\n", uiCmd.size());
        AssertK2ConsoleSafeScriptLiteral(uiCmd, "DispatchUiEvent Script must contain zero raw quotes/semicolons/backslashes/%");
        EXPECT_TRUE(uiCmd.size() < 1900,
                    "DispatchUiEvent Script must stay strictly under K2 CConsole's 2048-byte command limit");
        EXPECT_TRUE(uiCmd.find("string.char(") != std::string::npos,
                    "Unsafe widget/onclicklua strings must be encoded via string.char(...)");
        EXPECT_TRUE(uiCmd.find("GetCallback") != std::string::npos,
                    "DispatchUiEvent must invoke GetCallback for Lua/XML onclicklua handlers");
        EXPECT_TRUE(uiCmd.find("onclicklua") != std::string::npos,
                    "DispatchUiEvent must support onclicklua eventType and property actuation");
        EXPECT_TRUE(uiCmd.find("_button") != std::string::npos,
                    "DispatchUiEvent must probe _button/_btn/_catcher child template widgets");
        EXPECT_TRUE(uiCmd.find("DoEvent") != std::string::npos,
                    "DispatchUiEvent must preserve DoEvent/DoEventN fallback");
    }

    using PFN_IsQuitCmd = int(__cdecl*)(const wchar_t*);
    using PFN_ResolveQuitDeadline = uint32_t(__cdecl*)(const char*);
    auto pfnIsQuitCmd = reinterpret_cast<PFN_IsQuitCmd>(
        GetProcAddress(hGuard, "Kongor_NativeGuard_IsQuitConsoleCommandW"));
    auto pfnResolveDeadline = reinterpret_cast<PFN_ResolveQuitDeadline>(
        GetProcAddress(hGuard, "Kongor_NativeGuard_ResolveGracefulQuitDeadlineMs"));
    EXPECT_TRUE(pfnIsQuitCmd != nullptr, "Missing export Kongor_NativeGuard_IsQuitConsoleCommandW");
    EXPECT_TRUE(pfnResolveDeadline != nullptr, "Missing export Kongor_NativeGuard_ResolveGracefulQuitDeadlineMs");

    if (pfnIsQuitCmd && pfnResolveDeadline) {
        std::printf("[TEST] [Wave3A] Graceful early-load IPC quit command matcher & deadline clamping...\n");
        EXPECT_TRUE(pfnIsQuitCmd(L"quit") == 1, "'quit' must be detected as a quit command");
        EXPECT_TRUE(pfnIsQuitCmd(L"  QUIT  ") == 1, "Case-insensitive padded 'QUIT' must be detected");
        EXPECT_TRUE(pfnIsQuitCmd(L"exit") == 1, "'exit' must be detected as a quit command");
        EXPECT_TRUE(pfnIsQuitCmd(L"quit 0") == 1, "'quit 0' must be detected as a quit command");
        EXPECT_TRUE(pfnIsQuitCmd(L"quit;") == 1, "'quit;' must be detected as a quit command");
        EXPECT_TRUE(pfnIsQuitCmd(L"quitgame") == 0, "'quitgame' must NOT falsely trigger quit watchdog");
        EXPECT_TRUE(pfnIsQuitCmd(L"exit_menu") == 0, "'exit_menu' must NOT falsely trigger quit watchdog");
        EXPECT_TRUE(pfnIsQuitCmd(L"echo quit") == 0, "'echo quit' must NOT falsely trigger quit watchdog");
        EXPECT_TRUE(pfnIsQuitCmd(L"") == 0, "Empty string must NOT trigger quit watchdog");
        EXPECT_TRUE(pfnIsQuitCmd(nullptr) == 0, "nullptr must NOT crash or trigger quit watchdog");

        EXPECT_TRUE(pfnResolveDeadline(nullptr) == 1250, "Default graceful quit deadline must be 1250ms (< 2s SLO)");
        EXPECT_TRUE(pfnResolveDeadline("") == 1250, "Empty override must use 1250ms default");
        EXPECT_TRUE(pfnResolveDeadline("0") == 1250, "Zero override must fall back to 1250ms default");
        EXPECT_TRUE(pfnResolveDeadline("50") == 150, "Sub-minimum deadline must clamp to 150ms so IPC reply completes");
        EXPECT_TRUE(pfnResolveDeadline("900") == 900, "Valid in-range override must be honored");
        EXPECT_TRUE(pfnResolveDeadline(" 10000  ") == 10000, "Padded max deadline must be honored");
        EXPECT_TRUE(pfnResolveDeadline("99999") == 10000, "Excessive override must clamp to 10000ms ceiling");
        EXPECT_TRUE(pfnResolveDeadline("9999999999999999999999999") == 10000, "Huge digit string must clamp safely without uint64 overflow");
        EXPECT_TRUE(pfnResolveDeadline("invalid") == 1250, "Malformed override must fall back to 1250ms default");
        EXPECT_TRUE(pfnResolveDeadline("123abc") == 1250, "In-range digits with trailing chars must fall back to 1250ms default");
        EXPECT_TRUE(pfnResolveDeadline("10000ms") == 1250, "Max-value digits with trailing suffix 'ms' must fall back to 1250ms default");
        EXPECT_TRUE(pfnResolveDeadline("99999xyz") == 1250, "Clamped digits with trailing junk 'xyz' must fall back to 1250ms default");
        EXPECT_TRUE(pfnResolveDeadline("9999999999999999999999999x") == 1250, "Huge digits with trailing char must fall back to 1250ms default");
    }

    FreeLibrary(hGuard);
}
#endif

int main() {
    std::printf("=== Wave 5 Part A NativeGuard Seam Tests ===\n");
#ifdef _WIN32
    TestWave5ANativeGuardExports();
#endif
    if (g_failures > 0) {
        std::fprintf(stderr, "=== FAILED (%d failure(s)) ===\n", g_failures);
        return 1;
    }
    std::printf("=== ALL WAVE 5A NATIVE GUARD TESTS PASSED ===\n");
    return 0;
}
