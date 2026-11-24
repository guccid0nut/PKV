// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ============================================================================
// Project Kongor - NativeGuard In-Process Command Ingestion Named Pipe Server
// ============================================================================

#include "native_guard_internal.h"
#include "kongor_pipe_security.h"
#include "kongor_shared_mem.h"
#include "kongor_lua_shared_mem.h"
#include "kongor_sstream.h"
#include "asset_prewarmer.h"
#include "job_pool.h"
#include "predictive_pathing.h"
#include "runtime_patch_controller.h"
#include "modern_archive_hook.h"
#include "kongor_tracy.h"
#include <windows.h>
#include <cstdint>
#include <string>
#include <sstream>
#include <vector>
#include <atomic>
#include <cstdio>
#include <cstring>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <chrono>

HANDLE g_hPipeServerThread = NULL;
volatile bool g_pipeServerRunning = false;
static std::atomic<int> g_activePipeClients{ 0 };
static constexpr int MAX_CONCURRENT_PIPE_CLIENTS = 16;


static DWORD WINAPI NativeCommandPipeClientHandler(LPVOID lpParam) {
    HANDLE hPipe = (HANDLE)lpParam;
    if (hPipe == NULL || hPipe == INVALID_HANDLE_VALUE) {
        g_activePipeClients.fetch_sub(1, std::memory_order_relaxed);
        return 0;
    }

    // [L2-P1-04] Constant-time KONGOR_IPC_TOKEN validation when configured in environment.
    // When KONGOR_IPC_TOKEN is unset/empty, Owner-only DACL governs pipe access and clientAuthenticated starts true.
    char expectedIpcToken[512] = {0};
    DWORD expectedIpcTokenLen = GetEnvironmentVariableA("KONGOR_IPC_TOKEN", expectedIpcToken, sizeof(expectedIpcToken));
    if (expectedIpcTokenLen >= sizeof(expectedIpcToken)) {
        expectedIpcTokenLen = 0;
        expectedIpcToken[0] = '\0';
    }
    bool clientAuthenticated = (expectedIpcTokenLen == 0);

    std::vector<uint8_t> streamBuffer;
    uint8_t readBuf[8192];

    while (g_pipeServerRunning) {
        DWORD bytesRead = 0;
        BOOL ok = ReadFile(hPipe, readBuf, sizeof(readBuf), &bytesRead, NULL);
        if (!ok || bytesRead == 0) {
            break;
        }

        streamBuffer.insert(streamBuffer.end(), readBuf, readBuf + bytesRead);

        // Guard against unbounded memory growth on unframed text streams lacking newlines or 'MW' binary framing
        static constexpr size_t MAX_TEXT_STREAM_BUFFER_BYTES = 65536;
        if (!(streamBuffer.size() >= 2 && streamBuffer[0] == 0x4D && streamBuffer[1] == 0x57) &&
            streamBuffer.size() > MAX_TEXT_STREAM_BUFFER_BYTES) {
            OutputDebugStringA("[KONGOR NATIVE GUARD] SECURITY: Dropping oversized unframed text stream (> 64 KiB without newline).\n");
            const char* errLine = "{\"error\":{\"code\":-32600,\"message\":\"Payload exceeds 64 KiB text line limit\"}}\n";
            DWORD errWritten = 0;
            WriteFile(hPipe, errLine, (DWORD)strlen(errLine), &errWritten, NULL);
            FlushFileBuffers(hPipe);
            streamBuffer.clear();
            break;
        }

        while (!streamBuffer.empty()) {
            if (streamBuffer.size() >= 12 && streamBuffer[0] == 0x4D && streamBuffer[1] == 0x57) {
                uint16_t opCode = *(uint16_t*)&streamBuffer[2];
                int32_t payloadLen = *(int32_t*)&streamBuffer[4];
                uint32_t reqId = *(uint32_t*)&streamBuffer[8];

                if (payloadLen < 0 || payloadLen > 16 * 1024 * 1024) {
                    streamBuffer.clear();
                    break;
                }

                if (streamBuffer.size() < (size_t)(12 + payloadLen)) {
                    break;
                }

                std::string payloadStr;
                if (payloadLen > 0) {
                    payloadStr.assign((char*)&streamBuffer[12], payloadLen);
                }

                streamBuffer.erase(streamBuffer.begin(), streamBuffer.begin() + 12 + payloadLen);
                KONGOR_TRACY_ZONE_SCOPED_N("NativeGuard_PipeFrameDispatch");
                KONGOR_TRACY_FRAME_MARK_NAMED("NativeGuard_PipeIPC");

                uint16_t respOpCode = 0x0004;
                std::string respPayload;

                if (!clientAuthenticated && opCode != 0x0001 && opCode != 0x0010) {
                    respOpCode = 0x0011;
                    respPayload = "{\"authenticated\":false,\"error\":\"Unauthorized: send opcode 0x0010 with valid KONGOR_IPC_TOKEN first\"}";
                } else if (!Kongor::Kongor_IsBinaryPipeOpCodeAllowedInBuild(opCode, kIsPlaytestBuild)) {
                    char lockLog[160];
                    snprintf(lockLog, sizeof(lockLog), "[Kongor Pipe Security] Blocked binary pipe mutation opcode 0x%04X in playtest build.\n", (unsigned int)opCode);
                    OutputDebugStringA(lockLog);
                    if (opCode == 0x000E) {
                        respOpCode = 0x000F;
                        respPayload = "{\"success\":false,\"error\":\"403 PLAYTEST_LOCKDOWN: Binary pipe opcode not permitted in retail/playtest build\"}";
                    } else if (opCode == 0x0080) {
                        respOpCode = 0x0081;
                        uint32_t rId = (payloadLen >= 8) ? *(uint32_t*)(payloadStr.data() + 4) : reqId;
                        uint32_t elapsedUs = 0;
                        uint8_t statusFlags = 0;
                        std::string msg = "403 PLAYTEST_LOCKDOWN: Binary pipe opcode not permitted in retail/playtest build";
                        respPayload.resize(9 + msg.size());
                        memcpy(&respPayload[0], &rId, 4);
                        memcpy(&respPayload[4], &elapsedUs, 4);
                        respPayload[8] = statusFlags;
                        memcpy(&respPayload[9], msg.data(), msg.size());
                        reqId = rId;
                    } else {
                        respOpCode = 0x0004;
                        respPayload = "{\"success\":false,\"error\":\"403 PLAYTEST_LOCKDOWN: Binary pipe opcode not permitted in retail/playtest build\"}";
                    }
                } else {
                switch (opCode) {
                    case 0x0001: // Ping
                        respOpCode = 0x0002; // Pong
                        respPayload = "PONG";
                        break;
                    case 0x0010: { // Authenticate
                        respOpCode = 0x0011; // AuthResponse
                        bool tokenOk = Kongor_VerifyIpcAuthToken(
                            payloadStr.data(),
                            payloadStr.size(),
                            expectedIpcToken,
                            static_cast<size_t>(expectedIpcTokenLen));
                        clientAuthenticated = tokenOk;
                        if (tokenOk) {
                            respPayload = "{\"authenticated\":true,\"message\":\"Authorized\"}";
                        } else {
                            OutputDebugStringA("[KONGOR NATIVE GUARD] SECURITY: Rejected invalid KONGOR_IPC_TOKEN on opcode 0x0010.\n");
                            respPayload = "{\"authenticated\":false,\"error\":\"Invalid KONGOR_IPC_TOKEN\"}";
                        }
                        break;
                    }
                    case 0x0003: { // ExecuteCommand
                        respOpCode = 0x0004; // CommandResponse

                        if (payloadStr.size() > 7 &&
                            _strnicmp(payloadStr.c_str(), "Script ", 7) == 0 &&
                            payloadStr.find("userdataKONGOR/extensions/nexus_ipc/") == std::string::npos) {
                            const std::string kLegacyPrefix = "Script \"local _ok, _res = pcall(loadstring(\\\"";
                            const std::string kLegacySuffix = "\\\")) if not _ok then Echo('[LUA_ERR] ' .. tostring(_res)) else Echo('[LUA_OK] ' .. tostring(_res)) end\"";
                            std::string unescaped;
                            if (payloadStr.size() >= kLegacyPrefix.size() + kLegacySuffix.size() &&
                                payloadStr.compare(0, kLegacyPrefix.size(), kLegacyPrefix) == 0 &&
                                payloadStr.compare(payloadStr.size() - kLegacySuffix.size(), kLegacySuffix.size(), kLegacySuffix) == 0) {
                                std::string rawInner = payloadStr.substr(
                                    kLegacyPrefix.size(),
                                    payloadStr.size() - kLegacyPrefix.size() - kLegacySuffix.size());
                                unescaped.reserve(rawInner.size());
                                for (size_t i = 0; i < rawInner.size(); ++i) {
                                    if (rawInner[i] == '\\' && i + 1 < rawInner.size() && (rawInner[i + 1] == '"' || rawInner[i + 1] == '\\')) {
                                        unescaped.push_back(rawInner[i + 1]);
                                        ++i;
                                    } else {
                                        unescaped.push_back(rawInner[i]);
                                    }
                                }
                            } else {
                                std::string rest = payloadStr.substr(7);
                                while (!rest.empty() && (rest.front() == ' ' || rest.front() == '\t')) rest.erase(rest.begin());
                                while (!rest.empty() && (rest.back() == ' ' || rest.back() == '\t' || rest.back() == '\r' || rest.back() == '\n')) rest.pop_back();

                                bool isChained = false;
                                if (!rest.empty() && rest.front() == '"') {
                                    size_t closingQuote = std::string::npos;
                                    for (size_t i = 1; i < rest.size(); ++i) {
                                        if (rest[i] == '\\' && i + 1 < rest.size()) {
                                            ++i;
                                        } else if (rest[i] == '"') {
                                            closingQuote = i;
                                            break;
                                        }
                                    }

                                    if (closingQuote != std::string::npos) {
                                        size_t afterQuote = closingQuote + 1;
                                        while (afterQuote < rest.size() && (rest[afterQuote] == ' ' || rest[afterQuote] == '\t')) {
                                            ++afterQuote;
                                        }
                                        if (afterQuote < rest.size()) {
                                            // Chained multi-command (e.g. Script "A()"; Script "B()")
                                            isChained = true;
                                        } else {
                                            std::string rawInner = rest.substr(1, closingQuote - 1);
                                            unescaped.reserve(rawInner.size());
                                            for (size_t i = 0; i < rawInner.size(); ++i) {
                                                if (rawInner[i] == '\\' && i + 1 < rawInner.size() && (rawInner[i + 1] == '"' || rawInner[i + 1] == '\\')) {
                                                    unescaped.push_back(rawInner[i + 1]);
                                                    ++i;
                                                } else {
                                                    unescaped.push_back(rawInner[i]);
                                                }
                                            }
                                        }
                                    } else {
                                        unescaped = rest;
                                    }
                                } else {
                                    unescaped = rest;
                                }

                                if (isChained) {
                                    DispatchCommandToEngine(Utf8ToWide(payloadStr));
                                    respPayload = "{\"success\":true,\"result\":\"[ENGINE_CMD_DISPATCHED]\"}";
                                    break;
                                }
                            }

                            uint64_t evalId = Kongor::EnqueuePendingLuaEval(unescaped);
                            if (evalId != 0) {
                                std::string runnerUtf8 = BuildEvalRunnerCommandUtf8(evalId, GetCurrentNativeGamePhaseForLuaShim());
                                DispatchCommandToEngine(Utf8ToWide(runnerUtf8));
                                bool evalOk = false;
                                std::string evalResult;
                                bool completed = Kongor::WaitAndTakePendingLuaEval(evalId, 2500, &evalOk, &evalResult);
                                if (!completed && evalResult.empty()) {
                                    evalResult = "Timed out waiting 2500ms for main-thread Lua evaluation slot " + std::to_string(evalId);
                                }
                                if (completed && evalOk) {
                                    respPayload = "{\"success\":true,\"result\":\"[LUA_OK] " + EscapeJsonString(evalResult) + "\"}";
                                } else {
                                    respPayload = "{\"success\":false,\"error\":\"[LUA_ERR] " + EscapeJsonString(evalResult) + "\"}";
                                }
                                break;
                            }
                        }
                        if (payloadStr.find("KongorLogger.GetRecentLogs") != std::string::npos ||
                            payloadStr.find("STORE_AND_HEROES_REFRESHED") != std::string::npos) {
                            std::string boundedLua = payloadStr;
                            const std::string kUnboundedConcat = "return '[' .. table.concat(items, ',') .. ']'";
                            const std::string kBoundedConcat =
                                "local acc = {} local len = 2 "
                                "for i = #items, 1, -1 do "
                                "local it = items[i] "
                                "if len + #it + 1 <= 16000 then table.insert(acc, 1, it) len = len + #it + 1 else break end "
                                "end "
                                "return '[' .. table.concat(acc, ',') .. ']'";
                            size_t concatPos = boundedLua.find(kUnboundedConcat);
                            if (concatPos != std::string::npos) {
                                boundedLua.replace(concatPos, kUnboundedConcat.size(), kBoundedConcat);
                            }
                            uint64_t evalId = Kongor::EnqueuePendingLuaEval(boundedLua);
                            if (evalId != 0) {
                                std::string runnerUtf8 = BuildEvalRunnerCommandUtf8(evalId, GetCurrentNativeGamePhaseForLuaShim());
                                DispatchCommandToEngine(Utf8ToWide(runnerUtf8));
                                bool evalOk = false;
                                std::string evalResult;
                                bool completed = Kongor::WaitAndTakePendingLuaEval(evalId, 2500, &evalOk, &evalResult);
                                if (completed && evalOk) {
                                    respPayload = evalResult;
                                } else {
                                    respPayload = "[]";
                                }
                                break;
                            }
                        }
                        if (payloadStr.compare(0, 12, "PEEK_STRUCT ") == 0) {
                            std::istringstream iss(payloadStr.substr(12));
                            std::string structName;
                            std::string addrToken;
                            iss >> structName >> addrToken;
                            uintptr_t addr = static_cast<uintptr_t>(strtoull(addrToken.c_str(), nullptr, 0));
                            char peekBuf[2048] = {0};
                            Kongor::StructPeek::FormatStructPeekJson(structName.c_str(), addr, peekBuf, sizeof(peekBuf));
                            respPayload = peekBuf;
                            break;
                        }
                        std::wstring wcmd = Utf8ToWide(payloadStr);
                        DispatchCommandToEngine(wcmd);
                        respPayload = "{\"success\":true,\"command\":\"" + EscapeJsonString(payloadStr) + "\"}";
                        break;
                    }
                    case 0x0009: // DispatchUiEvent
                    case 0x000A: { // DispatchBackgroundHumanClick / GetMatchState
                        std::string ifaceName = "main";
                        std::string widgetName = payloadStr;
                        std::string fidelity = (opCode == 0x000A) ? "human" : "dom";
                        std::string eventType = "onclick";
                        std::string clientXStr;
                        std::string clientYStr;
                        std::string rightClickStr;

                        if (!payloadStr.empty() && payloadStr.front() == '{') {
                            std::string parsedFidelity = ExtractJsonUiField(payloadStr, "fidelity");
                            if (!parsedFidelity.empty()) fidelity = parsedFidelity;

                            clientXStr = ExtractJsonUiField(payloadStr, "clientX");
                            if (clientXStr.empty()) clientXStr = ExtractJsonUiField(payloadStr, "x");
                            clientYStr = ExtractJsonUiField(payloadStr, "clientY");
                            if (clientYStr.empty()) clientYStr = ExtractJsonUiField(payloadStr, "y");
                            rightClickStr = ExtractJsonUiField(payloadStr, "rightClick");
                        }

                        std::string luaCmd = BuildDispatchUiEventCommandUtf8(payloadStr, &ifaceName, &widgetName, &eventType);

                        if (_stricmp(fidelity.c_str(), "human") == 0 && !clientXStr.empty() && !clientYStr.empty() && clientXStr != "null" && clientYStr != "null") {
                            int clientX = atoi(clientXStr.c_str());
                            int clientY = atoi(clientYStr.c_str());
                            bool isRightClick = (_stricmp(rightClickStr.c_str(), "true") == 0 || rightClickStr == "1");
                            bool posted = false;
                            if (g_hGameWnd && IsWindow(g_hGameWnd)) {
                                PostMessage(g_hGameWnd, WM_KONGOR_BG_HUMAN_CLICK, isRightClick ? 1 : 0, MAKELPARAM(clientX, clientY));
                                posted = true;
                            }
                            respOpCode = 0x0004;
                            respPayload = std::string("{\"success\":true,\"fidelity\":\"human\",\"dispatched\":") +
                                          (posted ? "true" : "false") +
                                          ",\"clientX\":" + std::to_string(clientX) +
                                          ",\"clientY\":" + std::to_string(clientY) +
                                          ",\"widgetName\":\"" + EscapeJsonString(widgetName) + "\"}";
                            break;
                        }

                        if (luaCmd.size() >= 1980 && luaCmd.size() > 9 && luaCmd.compare(0, 8, "Script \"") == 0 && luaCmd.back() == '"') {
                            // Overflow route via KongorSharedMem pending-eval queue so oversized onclicklua strings never hit K2 CConsole's 2048-byte limit
                            uint64_t uiEvalId = Kongor::EnqueuePendingLuaEval(luaCmd.substr(8, luaCmd.size() - 9));
                            if (uiEvalId != 0) {
                                std::string runnerUtf8 = BuildEvalRunnerCommandUtf8(uiEvalId, GetCurrentNativeGamePhaseForLuaShim());
                                DispatchCommandToEngine(Utf8ToWide(runnerUtf8));
                                bool dummyOk = false;
                                std::string dummyRes;
                                Kongor::WaitAndTakePendingLuaEval(uiEvalId, 1000, &dummyOk, &dummyRes);
                            } else {
                                DispatchCommandToEngine(Utf8ToWide(luaCmd));
                            }
                        } else {
                            DispatchCommandToEngine(Utf8ToWide(luaCmd));
                        }
                        respOpCode = 0x0004;
                        respPayload = "{\"success\":true,\"fidelity\":\"dom\",\"interfaceName\":\"" +
                                      EscapeJsonString(ifaceName) +
                                      "\",\"widgetName\":\"" +
                                      EscapeJsonString(widgetName) +
                                      "\",\"eventType\":\"" +
                                      EscapeJsonString(eventType) + "\"}";
                        break;
                    }
                    case 0x000E: { // EvalLuaSync
                        respOpCode = 0x000F; // EvalLuaResponse
                        if (payloadStr.empty()) {
                            respPayload = "{\"success\":true,\"result\":\"nil\"}";
                            break;
                        }

                        uint64_t evalId = Kongor::EnqueuePendingLuaEval(payloadStr);
                        if (evalId == 0) {
                            respPayload = "{\"success\":false,\"error\":\"Failed to allocate pending Lua evaluation slot\"}";
                            break;
                        }

                        std::string runnerUtf8 = BuildEvalRunnerCommandUtf8(evalId, GetCurrentNativeGamePhaseForLuaShim());
                        DispatchCommandToEngine(Utf8ToWide(runnerUtf8));

                        bool evalOk = false;
                        std::string evalResult;
                        bool completed = Kongor::WaitAndTakePendingLuaEval(evalId, 2500, &evalOk, &evalResult);
                        if (!completed && evalResult.empty()) {
                            evalResult = "Timed out waiting 2500ms for main-thread Lua evaluation slot " + std::to_string(evalId);
                        }
                        if (completed && evalOk) {
                            respPayload = "{\"success\":true,\"result\":\"" + EscapeJsonString(evalResult) + "\"}";
                        } else {
                            respPayload = "{\"success\":false,\"error\":\"" + EscapeJsonString(evalResult) + "\"}";
                        }
                        break;
                    }
                    case 0x0080: { // DuplexLuaRpc
                        respOpCode = 0x0081; // DuplexLuaRpcResponse
                        uint32_t rpcTimeoutMs = 3000;
                        uint32_t rpcReqId = reqId;
                        std::string luaCode;

                        if (payloadLen >= 8) {
                            memcpy(&rpcTimeoutMs, payloadStr.data(), 4);
                            memcpy(&rpcReqId, payloadStr.data() + 4, 4);
                            if (payloadLen > 8) {
                                luaCode.assign(payloadStr.data() + 8, payloadLen - 8);
                            }
                        } else if (payloadLen > 0) {
                            luaCode = payloadStr;
                        }

                        uint32_t clampedTimeoutMs = (rpcTimeoutMs < 10) ? 10 : (rpcTimeoutMs > 10000) ? 10000 : rpcTimeoutMs;

                        LARGE_INTEGER qpcStart, qpcEnd, qpcFreq;
                        QueryPerformanceFrequency(&qpcFreq);
                        QueryPerformanceCounter(&qpcStart);

                        bool evalOk = false;
                        std::string evalResult;
                        bool completed = false;

                        if (!luaCode.empty()) {
                            uint64_t evalId = Kongor::EnqueuePendingLuaEval(luaCode);
                            if (evalId != 0) {
                                std::string runnerUtf8 = BuildEvalRunnerCommandUtf8(evalId, GetCurrentNativeGamePhaseForLuaShim());
                                DispatchCommandToEngine(Utf8ToWide(runnerUtf8));
                                completed = Kongor::WaitAndTakePendingLuaEval(evalId, (DWORD)clampedTimeoutMs, &evalOk, &evalResult);
                            } else {
                                evalResult = "Failed to allocate pending Lua evaluation slot";
                            }
                        } else {
                            completed = true;
                            evalOk = true;
                            evalResult = "nil";
                        }

                        QueryPerformanceCounter(&qpcEnd);
                        uint32_t elapsedUs = 0;
                        if (qpcFreq.QuadPart > 0) {
                            elapsedUs = static_cast<uint32_t>(((qpcEnd.QuadPart - qpcStart.QuadPart) * 1000000ULL) / qpcFreq.QuadPart);
                        }

                        uint8_t statusFlags = 0;
                        if (completed) {
                            if (evalOk) {
                                statusFlags |= 0x01; // bit0 = ok
                            }
                        } else {
                            statusFlags |= 0x02; // bit1 = timedOut
                            if (evalResult.empty()) {
                                evalResult = "Timed out waiting " + std::to_string(clampedTimeoutMs) + "ms for main-thread Lua evaluation";
                            }
                        }

                        respPayload.resize(9 + evalResult.size());
                        memcpy(&respPayload[0], &rpcReqId, 4);
                        memcpy(&respPayload[4], &elapsedUs, 4);
                        respPayload[8] = statusFlags;
                        if (!evalResult.empty()) {
                            memcpy(&respPayload[9], evalResult.data(), evalResult.size());
                        }
                        reqId = rpcReqId;
                        break;
                    }
                    case 0x0005: { // GetEngineState
                        respOpCode = 0x0006; // EngineStateResponse
                        double fps = 60.0;
                        uint32_t memMb = 500;
                        uint32_t gamePhase = 0;
                        uint32_t activeMatchId = 0;
                        uint32_t connectedPlayers = 0;
                        uint32_t luaMemKb = 0;
                        uint32_t activeEntities = g_dynamicActiveEntities.load(std::memory_order_relaxed);
                        uint32_t serverTick = g_dynamicServerTick.load(std::memory_order_relaxed);
                        std::string currentMapStr = "";
                        std::string playerNameStr = "Kongor";

                        if (g_pTelemetryHeader && g_pTelemetryRecords) {
                            float cpuMs = g_pTelemetryHeader->LatestRecord.CpuFrameTimeMs;
                            memMb = g_pTelemetryHeader->LatestRecord.MemoryWorkingSetMb;
                            if (g_pTelemetryHeader->LatestRecord.ActiveEntities > 0) {
                                activeEntities = g_pTelemetryHeader->LatestRecord.ActiveEntities;
                            }
                            if (g_pTelemetryHeader->LatestRecord.ServerTick > 0) {
                                serverTick = g_pTelemetryHeader->LatestRecord.ServerTick;
                            }
                            if (cpuMs <= 0.001f && g_pTelemetryHeader->Head > 0) {
                                uint32_t prevSlot = (uint32_t)((g_pTelemetryHeader->Head - 1) & TELEMETRY_RING_MASK);
                                cpuMs = g_pTelemetryRecords[prevSlot].CpuFrameTimeMs;
                                memMb = g_pTelemetryRecords[prevSlot].MemoryWorkingSetMb;
                            }
                            if (cpuMs > 0.001f) fps = 1000.0 / cpuMs;
                            gamePhase = g_pTelemetryHeader->GamePhase;
                            activeMatchId = g_pTelemetryHeader->ActiveMatchId;
                            connectedPlayers = g_pTelemetryHeader->ConnectedPlayers;
                            luaMemKb = g_pTelemetryHeader->LuaMemoryKb;
                            size_t mapLen = strnlen(g_pTelemetryHeader->CurrentMap, sizeof(g_pTelemetryHeader->CurrentMap));
                            currentMapStr.assign(g_pTelemetryHeader->CurrentMap, mapLen);
                            size_t nameLen = strnlen(g_pTelemetryHeader->PlayerName, sizeof(g_pTelemetryHeader->PlayerName));
                            if (nameLen > 0) {
                                playerNameStr.assign(g_pTelemetryHeader->PlayerName, nameLen);
                            }
                        }

                        uint64_t uptimeSec = 0;
                        if (g_qpcFrequency.QuadPart > 0 && g_qpcProcessStart.QuadPart > 0) {
                            LARGE_INTEGER nowQpc;
                            QueryPerformanceCounter(&nowQpc);
                            if (nowQpc.QuadPart > g_qpcProcessStart.QuadPart) {
                                uptimeSec = (uint64_t)((nowQpc.QuadPart - g_qpcProcessStart.QuadPart) / g_qpcFrequency.QuadPart);
                            }
                        }

                        uint64_t hitchCount = g_telemetryHitchCount.load(std::memory_order_relaxed);
                        uint64_t cpuStallCount = g_telemetryCpuStallCount.load(std::memory_order_relaxed);
                        double peakTickMs = (double)g_telemetryPeakFrameTimeUs.load(std::memory_order_relaxed) / 1000.0;
                        uint64_t killClampCount = g_killPacketClampCount.load(std::memory_order_relaxed);
                        uint64_t killExpandCount = g_killPacketExpandCount.load(std::memory_order_relaxed);
                        uint64_t replayKillClampCount = g_replayKillReadClampCount.load(std::memory_order_relaxed);
                        uint64_t snapshotDesyncDrops = g_snapshotDesyncDropCount.load(std::memory_order_relaxed);
                        bool mainThreadBusy = g_mainThreadBusy.load(std::memory_order_acquire);
                        uint64_t activeOpElapsedMs = 0;
                        if (mainThreadBusy) {
                            uint64_t startTick = g_activeMainThreadOpStartTick.load(std::memory_order_relaxed);
                            uint64_t nowTick = GetTickCount64();
                            if (nowTick >= startTick) {
                                activeOpElapsedMs = nowTick - startTick;
                            }
                        }

                        int32_t hostFrame = ReadCurrentHostFrameSafe();
                        uint64_t msSinceHostFrameAdvance = UpdateAndGetMsSinceHostFrameAdvance(hostFrame);

                        bool isActiveMatch = (gamePhase == 3 || activeMatchId > 0);
                        bool isAuthenticated = (!playerNameStr.empty() && playerNameStr != "Kongor" && playerNameStr != "UnnamedNewbie");
                        uint32_t canonicalGamePhase = gamePhase;

                        char stateBuf[2048];
                        snprintf(stateBuf, sizeof(stateBuf),
                            "{\"currentFps\":%.1f,\"memoryWorkingSetBytes\":%llu,\"luaMemoryKb\":%u,\"activeMatchId\":%u,\"currentMap\":\"%s\",\"playerName\":\"%s\",\"connectedPlayers\":%u,\"uptimeSeconds\":%llu,\"gamePhase\":%u,\"canonicalGamePhase\":%u,\"isAuthenticated\":%s,\"isActiveMatch\":%s,\"gamePhaseName\":\"%s\",\"isSpectator\":false,\"activeEntities\":%u,\"serverTick\":%u,\"hostFrame\":%d,\"msSinceHostFrameAdvance\":%llu,\"mainThreadBusy\":%s,\"activeOpElapsedMs\":%llu,\"main_thread_busy\":%s,\"active_op_elapsed_ms\":%llu,\"entityProfiler\":{\"activeEntities\":%u,\"luaMemoryKb\":%u,\"hitchCount\":%llu,\"cpuStallCount\":%llu,\"peakTickMs\":%.2f,\"killPacketClampCount\":%llu,\"killPacketExpandCount\":%llu,\"replayKillReadClampCount\":%llu,\"snapshotDesyncDropCount\":%llu}}",
                            fps,
                            (unsigned long long)memMb * 1024ULL * 1024ULL,
                            luaMemKb,
                            activeMatchId,
                            EscapeJsonString(currentMapStr).c_str(),
                            EscapeJsonString(playerNameStr).c_str(),
                            connectedPlayers,
                            (unsigned long long)uptimeSec,
                            gamePhase,
                            canonicalGamePhase,
                            isAuthenticated ? "true" : "false",
                            isActiveMatch ? "true" : "false",
                            GetCanonicalGamePhaseName(gamePhase),
                            activeEntities,
                            serverTick,
                            hostFrame,
                            (unsigned long long)msSinceHostFrameAdvance,
                            mainThreadBusy ? "true" : "false",
                            (unsigned long long)activeOpElapsedMs,
                            mainThreadBusy ? "true" : "false",
                            (unsigned long long)activeOpElapsedMs,
                            activeEntities,
                            luaMemKb,
                            (unsigned long long)hitchCount,
                            (unsigned long long)cpuStallCount,
                            peakTickMs,
                            (unsigned long long)killClampCount,
                            (unsigned long long)killExpandCount,
                            (unsigned long long)replayKillClampCount,
                            (unsigned long long)snapshotDesyncDrops);
                        respPayload = stateBuf;
                        break;
                    }
                    case 0x0007: { // GetLuaMemoryMetrics / TriggerGC (pipe thread: read main-thread cached atomic, never touch CLuaScriptManager directly)
                        respOpCode = 0x0008; // LuaMemoryResponse
                        uint64_t memBytes = g_cachedLuaMemoryBytes.load(std::memory_order_relaxed);
                        if (memBytes == 0 && g_pTelemetryHeader) {
                            memBytes = static_cast<uint64_t>(g_pTelemetryHeader->LuaMemoryKb) * 1024ULL;
                        }
                        char luaBuf[256];
                        snprintf(luaBuf, sizeof(luaBuf),
                            "{\"luaMemoryBytes\":%llu,\"gcPacingActive\":true,\"fullGcCollections\":%llu}",
                            (unsigned long long)memBytes, (unsigned long long)g_fullGcCount.load());
                        respPayload = luaBuf;
                        break;
                    }
                    case 0x0012: { // InjectSubTickInput
                        LARGE_INTEGER nowQpc;
                        QueryPerformanceCounter(&nowQpc);

                        float latencyMs = 0.0f;
                        int64_t hwTimestamp = 0;
                        uint32_t inputSeq = 0;
                        std::string actionStr;

                        size_t actPos = payloadStr.find("\"action\":\"");
                        if (actPos != std::string::npos) {
                            size_t actEnd = payloadStr.find("\"", actPos + 10);
                            if (actEnd != std::string::npos) {
                                actionStr = payloadStr.substr(actPos + 10, actEnd - (actPos + 10));
                            }
                        }

                        size_t qpcPos = payloadStr.find("\"hardwareTimestampQpc\":");
                        if (qpcPos != std::string::npos) {
                            hwTimestamp = _strtoi64(payloadStr.c_str() + qpcPos + 23, NULL, 10);
                        }

                        size_t seqPos = payloadStr.find("\"inputSequence\":");
                        if (seqPos != std::string::npos) {
                            inputSeq = (uint32_t)strtoul(payloadStr.c_str() + seqPos + 16, NULL, 10);
                        }

                        if (hwTimestamp > 0 && g_qpcFrequency.QuadPart > 0 && nowQpc.QuadPart >= hwTimestamp) {
                            double deltaSec = (double)(nowQpc.QuadPart - hwTimestamp) / (double)g_qpcFrequency.QuadPart;
                            latencyMs = (float)(deltaSec * 1000.0);
                            g_dynamicInputLatencyMs.store(latencyMs, std::memory_order_relaxed);
                        }

                        if (!actionStr.empty()) {
                            DispatchCommandToEngine(Utf8ToWide(actionStr), hwTimestamp > 0 ? hwTimestamp : nowQpc.QuadPart, inputSeq);
                        }

                        respOpCode = 0x0013; // SubTickInputResponse
                        char subTickBuf[256];
                        snprintf(subTickBuf, sizeof(subTickBuf),
                            "{\"success\":true,\"inputSequence\":%u,\"measuredLatencyMs\":%.3f,\"timestampQpc\":%lld}",
                            inputSeq, latencyMs, (long long)nowQpc.QuadPart);
                        respPayload = subTickBuf;
                        break;
                    }
                    case 0x0014: { // UpdateTelemetryMetrics
                        size_t dcPos = payloadStr.find("\"drawCalls\":");
                        if (dcPos != std::string::npos) {
                            uint32_t dc = (uint32_t)strtoul(payloadStr.c_str() + dcPos + 12, NULL, 10);
                            if (dc > 0) g_dynamicDrawCalls.store(dc, std::memory_order_relaxed);
                        }

                        size_t entPos = payloadStr.find("\"activeEntities\":");
                        if (entPos != std::string::npos) {
                            uint32_t ent = (uint32_t)strtoul(payloadStr.c_str() + entPos + 17, NULL, 10);
                            if (ent > 0) g_dynamicActiveEntities.store(ent, std::memory_order_relaxed);
                        }

                        size_t partPos = payloadStr.find("\"particlesAlive\":");
                        if (partPos != std::string::npos) {
                            uint32_t part = (uint32_t)strtoul(payloadStr.c_str() + partPos + 17, NULL, 10);
                            if (part > 0) g_dynamicParticlesAlive.store(part, std::memory_order_relaxed);
                        }

                        size_t pingPos = payloadStr.find("\"pingMicroseconds\":");
                        if (pingPos != std::string::npos) {
                            uint32_t ping = (uint32_t)strtoul(payloadStr.c_str() + pingPos + 19, NULL, 10);
                            if (ping > 0) g_dynamicPingMicroseconds.store(ping, std::memory_order_relaxed);
                        }

                        size_t tickPos = payloadStr.find("\"serverTick\":");
                        if (tickPos != std::string::npos) {
                            uint32_t tick = (uint32_t)strtoul(payloadStr.c_str() + tickPos + 13, NULL, 10);
                            if (tick > 0) g_dynamicServerTick.store(tick, std::memory_order_relaxed);
                        }

                        size_t latPos = payloadStr.find("\"inputLatencyMs\":");
                        if (latPos != std::string::npos) {
                            float lat = (float)strtod(payloadStr.c_str() + latPos + 17, NULL);
                            if (lat >= 0.0f) g_dynamicInputLatencyMs.store(lat, std::memory_order_relaxed);
                        }

                        respOpCode = 0x0015; // TelemetryMetricsResponse
                        respPayload = "{\"success\":true,\"telemetryUpdated\":true}";
                        break;
                    }
                    case 0x0021: { // DispatchVoiceUiTrigger
                        int allyIndex = 0;
                        bool isSpeaking = false;
                        bool isMuted = false;

                        size_t idxPos = payloadStr.find("\"allyIndex\":");
                        if (idxPos != std::string::npos) {
                            allyIndex = atoi(payloadStr.c_str() + idxPos + 12);
                        }
                        size_t spkPos = payloadStr.find("\"isSpeaking\":true");
                        if (spkPos != std::string::npos) {
                            isSpeaking = true;
                        }
                        size_t mutePos = payloadStr.find("\"isMuted\":true");
                        if (mutePos != std::string::npos) {
                            isMuted = true;
                        }

                        std::string luaCmd = "Script if Trigger then Trigger('AllyVoice" + std::to_string(allyIndex) + "', " +
                            (isSpeaking ? "true" : "false") + ", " + (isMuted ? "true" : "false") + ") " +
                            "Trigger('VoiceChatTalking" + std::to_string(allyIndex) + "', " + (isSpeaking ? "true" : "false") + ") end";
                        DispatchCommandToEngine(Utf8ToWide(luaCmd));

                        respOpCode = 0x0022; // DispatchVoiceUiTriggerResponse
                        respPayload = "{\"success\":true,\"triggered\":true}";
                        break;
                    }
                    case 0x0023: { // GetVoicePttState
                        uint64_t flags = g_voicePttFlags.load(std::memory_order_relaxed);
                        respOpCode = 0x0024; // GetVoicePttStateResponse
                        char pttBuf[256];
                        snprintf(pttBuf, sizeof(pttBuf),
                            "{\"team\":%s,\"lane\":%s,\"party\":%s,\"mentor\":%s,\"flags\":%llu}",
                            (flags & KONGOR_VOICE_PTT_TEAM) ? "true" : "false",
                            (flags & KONGOR_VOICE_PTT_LANE) ? "true" : "false",
                            (flags & KONGOR_VOICE_PTT_PARTY) ? "true" : "false",
                            (flags & KONGOR_VOICE_PTT_MENTOR) ? "true" : "false",
                            (unsigned long long)flags);
                        respPayload = pttBuf;
                        break;
                    }
                    case 0x0025: { // GetRawInputState
                        respOpCode = 0x0026; // GetRawInputStateResponse
                        bool enabled = g_rawInputEnabled.load(std::memory_order_relaxed);
                        bool reg = g_rawInputRegistered.load(std::memory_order_relaxed);
                        uint64_t pkts = g_rawPacketsProcessed.load(std::memory_order_relaxed);
                        uint64_t supp = g_mouseMoveSuppressedCount.load(std::memory_order_relaxed);
                        float sens = g_mouseSensitivity.load(std::memory_order_relaxed);
                        int32_t curX = g_rawAccumulatorX.load(std::memory_order_relaxed);
                        int32_t curY = g_rawAccumulatorY.load(std::memory_order_relaxed);
                        char rawBuf[256];
                        snprintf(rawBuf, sizeof(rawBuf),
                            "{\"enabled\":%s,\"registered\":%s,\"packetsProcessed\":%llu,\"mouseMoveSuppressed\":%llu,\"sensitivity\":%.3f,\"currentAccumulatorX\":%d,\"currentAccumulatorY\":%d}",
                            enabled ? "true" : "false", reg ? "true" : "false",
                            (unsigned long long)pkts, (unsigned long long)supp,
                            sens, curX, curY);
                        respPayload = rawBuf;
                        break;
                    }
                    case 0x0027: { // SetRawInputConfig
                        respOpCode = 0x0028; // SetRawInputConfigResponse
                        if (payloadStr.find("\"enabled\":false") != std::string::npos) {
                            g_rawInputEnabled.store(false, std::memory_order_relaxed);
                        } else if (payloadStr.find("\"enabled\":true") != std::string::npos) {
                            g_rawInputEnabled.store(true, std::memory_order_relaxed);
                        }
                        size_t sensPos = payloadStr.find("\"sensitivity\":");
                        if (sensPos != std::string::npos) {
                            float sens = (float)strtod(payloadStr.c_str() + sensPos + 14, NULL);
                            if (sens > 0.0001f && sens < 100.0f) {
                                g_mouseSensitivity.store(sens, std::memory_order_relaxed);
                            }
                        }
                        bool enabled = g_rawInputEnabled.load(std::memory_order_relaxed);
                        float sens = g_mouseSensitivity.load(std::memory_order_relaxed);
                        char cfgBuf[128];
                        snprintf(cfgBuf, sizeof(cfgBuf),
                            "{\"success\":true,\"enabled\":%s,\"sensitivity\":%.3f}",
                            enabled ? "true" : "false", sens);
                        respPayload = cfgBuf;
                        break;
                    }
                    case 0x0030: { // PrewarmHero
                        std::string heroName = payloadStr;
                        std::string avatarKey = "";
                        size_t hPos = payloadStr.find("\"hero\":\"");
                        if (hPos != std::string::npos) {
                            size_t hEnd = payloadStr.find("\"", hPos + 8);
                            if (hEnd != std::string::npos) {
                                heroName = payloadStr.substr(hPos + 8, hEnd - (hPos + 8));
                            }
                        }
                        size_t aPos = payloadStr.find("\"avatar\":\"");
                        if (aPos != std::string::npos) {
                            size_t aEnd = payloadStr.find("\"", aPos + 10);
                            if (aEnd != std::string::npos) {
                                avatarKey = payloadStr.substr(aPos + 10, aEnd - (aPos + 10));
                            }
                        }
                        Kongor::EnqueueHeroForPrewarming(heroName, avatarKey);
                        respOpCode = 0x0033; // PrewarmHeroResponse
                        respPayload = "{\"success\":true,\"hero\":\"" + EscapeJsonString(heroName) + "\",\"avatar\":\"" + EscapeJsonString(avatarKey) + "\"}";
                        break;
                    }
                    case 0x0031: { // PrewarmCompetitivePool
                        Kongor::PrewarmCompetitiveItemPool();
                        respOpCode = 0x0034; // PrewarmCompetitivePoolResponse
                        respPayload = "{\"success\":true,\"pool\":\"competitive_items\"}";
                        break;
                    }
                    case 0x0032: { // GetPrewarmStats
                        Kongor::PrewarmStats stats = Kongor::GetAssetPrewarmStats();
                        respOpCode = 0x0035; // GetPrewarmStatsResponse
                        char statsBuf[256];
                        snprintf(statsBuf, sizeof(statsBuf),
                            "{\"filesPrewarmed\":%u,\"bytesPrewarmed\":%llu,\"durationMs\":%.2f,\"heroesProcessed\":%u,\"itemsProcessed\":%u,\"isActive\":%s}",
                            stats.FilesPrewarmed,
                            (unsigned long long)stats.BytesPrewarmed,
                            stats.DurationMs,
                            stats.HeroesProcessed,
                            stats.ItemsProcessed,
                            Kongor::IsPrewarmingActive() ? "true" : "false");
                        respPayload = statsBuf;
                        break;
                    }
                    case 0x0040: { // GetJobPoolStats
                        Kongor::JobPoolStats stats = Kongor::JobPool::GetStats();
                        respOpCode = 0x0042; // GetJobPoolStatsResponse
                        char poolBuf[256];
                        snprintf(poolBuf, sizeof(poolBuf),
                            "{\"workerCount\":%u,\"totalJobsExecuted\":%llu,\"totalBatchesExecuted\":%llu,\"averageBatchDurationUs\":%.2f,\"activeWorkers\":%u,\"isRunning\":%s}",
                            stats.WorkerCount,
                            (unsigned long long)stats.TotalJobsExecuted,
                            (unsigned long long)stats.TotalBatchesExecuted,
                            stats.AverageBatchDurationUs,
                            stats.ActiveWorkers,
                            stats.IsRunning ? "true" : "false");
                        respPayload = poolBuf;
                        break;
                    }
                    case 0x0041: { // SetJobPoolConfig
                        uint32_t workers = 0;
                        size_t wPos = payloadStr.find("\"workerCount\":");
                        if (wPos != std::string::npos) {
                            workers = (uint32_t)strtoul(payloadStr.c_str() + wPos + 14, NULL, 10);
                        }
                        if (workers > 0) {
                            Kongor::JobPool::Shutdown();
                            Kongor::JobPool::Initialize(workers);
                        }
                        Kongor::JobPoolStats stats = Kongor::JobPool::GetStats();
                        respOpCode = 0x0043; // SetJobPoolConfigResponse
                        char cfgBuf[128];
                        snprintf(cfgBuf, sizeof(cfgBuf),
                            "{\"success\":true,\"workerCount\":%u,\"isRunning\":%s}",
                            stats.WorkerCount, stats.IsRunning ? "true" : "false");
                        respPayload = cfgBuf;
                        break;
                    }
                    case 0x0050: { // GetPredictionStats
                        Kongor::PredictivePathing::PredictionStats stats = Kongor::PredictivePathing::GetStats();
                        respOpCode = 0x0052; // GetPredictionStatsResponse
                        char predBuf[384];
                        snprintf(predBuf, sizeof(predBuf),
                            "{\"totalMoveCommandsPredicted\":%llu,\"totalReconciliations\":%llu,\"hardCorrections\":%llu,\"avgErrorDistance\":%.2f,\"maxLatencyCompensatedMs\":%.1f,\"isEnabled\":%s,\"turnOnlyMode\":%s}",
                            (unsigned long long)stats.TotalMoveCommandsPredicted,
                            (unsigned long long)stats.TotalReconciliations,
                            (unsigned long long)stats.HardCorrections,
                            stats.AvgErrorDistance,
                            stats.MaxLatencyCompensatedMs,
                            stats.IsEnabled ? "true" : "false",
                            stats.TurnOnlyMode ? "true" : "false");
                        respPayload = predBuf;
                        break;
                    }
                    case 0x0051: { // SetPredictionConfig
                        bool enabled = true;
                        bool turnOnly = false;
                        float maxLatency = 150.0f;
                        if (payloadStr.find("\"enabled\":false") != std::string::npos) enabled = false;
                        if (payloadStr.find("\"turnOnly\":true") != std::string::npos) turnOnly = true;
                        size_t lPos = payloadStr.find("\"maxLatencyMs\":");
                        if (lPos != std::string::npos) {
                            maxLatency = (float)strtod(payloadStr.c_str() + lPos + 15, NULL);
                        }
                        Kongor::PredictivePathing::SetConfig(enabled, turnOnly, maxLatency);
                        Kongor::PredictivePathing::PredictionStats stats = Kongor::PredictivePathing::GetStats();
                        respOpCode = 0x0053; // SetPredictionConfigResponse
                        char cfgBuf[160];
                        snprintf(cfgBuf, sizeof(cfgBuf),
                            "{\"success\":true,\"isEnabled\":%s,\"turnOnlyMode\":%s,\"maxLatencyMs\":%.1f}",
                            stats.IsEnabled ? "true" : "false", stats.TurnOnlyMode ? "true" : "false", stats.MaxLatencyCompensatedMs);
                        respPayload = cfgBuf;
                        break;
                    }
                    case 0x0060: { // HookToggleRequest (JSON: {"group":"vfs|prewarm|seh|sleep|atob|all","action":"on|off|status"})
                        std::string group = "all";
                        std::string action = "status";
                        size_t gPos = payloadStr.find("\"group\"");
                        if (gPos != std::string::npos) {
                            size_t q1 = payloadStr.find('"', gPos + 7);
                            size_t q2 = (q1 != std::string::npos) ? payloadStr.find('"', q1 + 1) : std::string::npos;
                            if (q1 != std::string::npos && q2 != std::string::npos && q2 > q1 + 1) {
                                group = payloadStr.substr(q1 + 1, q2 - q1 - 1);
                            }
                        }
                        size_t aPos = payloadStr.find("\"action\"");
                        if (aPos != std::string::npos) {
                            size_t q1 = payloadStr.find('"', aPos + 8);
                            size_t q2 = (q1 != std::string::npos) ? payloadStr.find('"', q1 + 1) : std::string::npos;
                            if (q1 != std::string::npos && q2 != std::string::npos && q2 > q1 + 1) {
                                action = payloadStr.substr(q1 + 1, q2 - q1 - 1);
                            }
                        }
                        respOpCode = 0x0061; // HookToggleResponse
                        char statusJson[8192] = {0};
                        Kongor::PatchController::ExecuteHookToggle(
                            group.c_str(), action.c_str(), statusJson, sizeof(statusJson), false);
                        respPayload = statusJson;
                        break;
                    }
                    case 0x0062: { // PeekStructRequest ("PEEK_STRUCT <struct_name> <hex_address>" or "<struct_name> <hex_address>")
                        respOpCode = 0x0063; // PeekStructResponse
                        std::string raw = payloadStr;
                        if (raw.compare(0, 12, "PEEK_STRUCT ") == 0) {
                            raw = raw.substr(12);
                        }
                        std::istringstream iss(raw);
                        std::string structName;
                        std::string addrToken;
                        iss >> structName >> addrToken;
                        uintptr_t addr = static_cast<uintptr_t>(strtoull(addrToken.c_str(), nullptr, 0));
                        char peekBuf[2048] = {0};
                        Kongor::StructPeek::FormatStructPeekJson(structName.c_str(), addr, peekBuf, sizeof(peekBuf));
                        respPayload = peekBuf;
                        break;
                    }
                    case 0x0070: { // TriggerMatchVictory
                        char harnessEnv[16] = {0};
                        DWORD hLen = GetEnvironmentVariableA("KONGOR_E2E_HARNESS", harnessEnv, sizeof(harnessEnv));
                        if (hLen == 0 || strcmp(harnessEnv, "1") != 0) {
                            respOpCode = 0x0073; // Error
                            respPayload = "{\"error\":{\"code\":403,\"message\":\"403 E2E_ONLY: trigger_match_victory requires KONGOR_E2E_HARNESS=1\"}}";
                        } else {
                            char masterEnv[256] = {0};
                            DWORD mLen = GetEnvironmentVariableA("MASTER_SERVER_ADDRESS", masterEnv, sizeof(masterEnv));
                            std::string master = (mLen > 0) ? masterEnv : "";
                            if (!master.empty() && (master.find(":55555") != std::string::npos || master.find(".newbs.io") != std::string::npos)) {
                                respOpCode = 0x0073; // Error
                                respPayload = "{\"error\":{\"code\":403,\"message\":\"403 FORBIDDEN: trigger_match_victory blocked on production master server target\"}}";
                            } else {
                                int sPort = g_resolvedDedicatedServerPort > 0 ? g_resolvedDedicatedServerPort : ResolveDedicatedServerPortFromCommandLine(GetCommandLineA());
                                WriteServerKeyframe(sPort);
                                DispatchCommandToEngine(L"ServerEndGame 1");
                                respOpCode = 0x0074; // Success
                                respPayload = "{\"victoryTriggered\":true,\"winningTeam\":1,\"command\":\"ServerEndGame 1\"}";
                            }
                        }
                        break;
                    }
                    case 0x0071: { // GetServerWorldState
                        int sPort = g_resolvedDedicatedServerPort > 0 ? g_resolvedDedicatedServerPort : ResolveDedicatedServerPortFromCommandLine(GetCommandLineA());
                        WriteServerKeyframe(sPort);
                        respOpCode = 0x0075;
                        double fps = 60.0;
                        uint32_t memMb = 500;
                        uint32_t gamePhase = 0;
                        uint32_t activeMatchId = 0;
                        uint32_t connectedPlayers = 0;
                        uint32_t luaMemKb = 0;
                        uint32_t activeEntities = g_dynamicActiveEntities.load(std::memory_order_relaxed);
                        uint32_t serverTick = g_dynamicServerTick.load(std::memory_order_relaxed);
                        std::string currentMapStr = "";
                        if (g_pTelemetryHeader && g_pTelemetryRecords) {
                            float cpuMs = g_pTelemetryHeader->LatestRecord.CpuFrameTimeMs;
                            memMb = g_pTelemetryHeader->LatestRecord.MemoryWorkingSetMb;
                            if (g_pTelemetryHeader->LatestRecord.ActiveEntities > 0) activeEntities = g_pTelemetryHeader->LatestRecord.ActiveEntities;
                            if (g_pTelemetryHeader->LatestRecord.ServerTick > 0) serverTick = g_pTelemetryHeader->LatestRecord.ServerTick;
                            if (cpuMs > 0.001f) fps = 1000.0 / cpuMs;
                            gamePhase = g_pTelemetryHeader->GamePhase;
                            activeMatchId = g_pTelemetryHeader->ActiveMatchId;
                            connectedPlayers = g_pTelemetryHeader->ConnectedPlayers;
                            luaMemKb = g_pTelemetryHeader->LuaMemoryKb;
                            size_t mapLen = strnlen(g_pTelemetryHeader->CurrentMap, sizeof(g_pTelemetryHeader->CurrentMap));
                            currentMapStr = std::string(g_pTelemetryHeader->CurrentMap, mapLen);
                        }
                        char srvStateBuf[1024];
                        snprintf(srvStateBuf, sizeof(srvStateBuf),
                            "{\"currentFps\":%.1f,\"memoryWorkingSetBytes\":%llu,\"luaMemoryKb\":%u,\"activeMatchId\":%u,\"currentMap\":\"%s\",\"connectedPlayers\":%u,\"gamePhase\":%u,\"activeEntities\":%u,\"serverTick\":%u}",
                            fps, (unsigned long long)memMb * 1024ULL * 1024ULL, luaMemKb, activeMatchId, EscapeJsonString(currentMapStr).c_str(), connectedPlayers, gamePhase, activeEntities, serverTick);
                        respPayload = srvStateBuf;
                        break;
                    }
                    case 0x0072: { // ApplyServerFixture
                        std::string luaCmd = "Script if TAO and TAO.Dispatch then TAO.Dispatch('apply_fixture', '" +
                            EscapeJsonString(payloadStr) + "') elseif Nexus and Nexus.ApplySnapshot then Nexus.ApplySnapshot() end";
                        DispatchCommandToEngine(Utf8ToWide(luaCmd));
                        respOpCode = 0x0076;
                        respPayload = "{\"applied\":true}";
                        break;
                    }
                    default:
                        respOpCode = 0x0004;
                        respPayload = "{\"success\":true}";
                        break;
                }
                }

                std::vector<uint8_t> respBuf(12 + respPayload.size());
                *(uint16_t*)&respBuf[0] = 0x574D;
                *(uint16_t*)&respBuf[2] = respOpCode;
                *(int32_t*)&respBuf[4] = (int32_t)respPayload.size();
                *(uint32_t*)&respBuf[8] = reqId;
                if (!respPayload.empty()) {
                    memcpy(&respBuf[12], respPayload.data(), respPayload.size());
                }

                DWORD bytesWritten = 0;
                WriteFile(hPipe, respBuf.data(), (DWORD)respBuf.size(), &bytesWritten, NULL);
                FlushFileBuffers(hPipe);
            } else {
                auto nlIt = std::find(streamBuffer.begin(), streamBuffer.end(), (uint8_t)'\n');
                if (nlIt != streamBuffer.end()) {
                    std::string line((char*)streamBuffer.data(), nlIt - streamBuffer.begin());
                    streamBuffer.erase(streamBuffer.begin(), nlIt + 1);

                    if (!line.empty() && line.back() == '\r') line.pop_back();

                    if (!line.empty()) {
                        std::string resp;
                        // (#180) JSON-RPC 2.0 Envelope Detection & Routing over \\.\pipe\kongor-native-client
                        if (line.front() == '{' && line.find("\"jsonrpc\"") != std::string::npos && line.find("\"2.0\"") != std::string::npos) {
                            auto extractJsonToken = [](const std::string& jsonStr, const char* key, bool preserveQuotes = false) -> std::string {
                                std::string needle = std::string("\"") + key + "\"";
                                size_t pos = jsonStr.find(needle);
                                if (pos == std::string::npos) return "";
                                size_t colon = jsonStr.find(':', pos + needle.size());
                                if (colon == std::string::npos) return "";
                                size_t valStart = colon + 1;
                                while (valStart < jsonStr.size() && (jsonStr[valStart] == ' ' || jsonStr[valStart] == '\t')) ++valStart;
                                if (valStart >= jsonStr.size()) return "";
                                if (jsonStr[valStart] == '"') {
                                    size_t valEnd = jsonStr.find('"', valStart + 1);
                                    if (valEnd == std::string::npos) return "";
                                    if (preserveQuotes) {
                                        return jsonStr.substr(valStart, (valEnd - valStart) + 1);
                                    }
                                    return jsonStr.substr(valStart + 1, valEnd - (valStart + 1));
                                }
                                size_t valEnd = jsonStr.find_first_of(",}\r\n ", valStart);
                                if (valEnd == std::string::npos) valEnd = jsonStr.size();
                                return jsonStr.substr(valStart, valEnd - valStart);
                            };

                            std::string idRaw = extractJsonToken(line, "id", true);
                            if (idRaw.empty()) idRaw = "1";
                            std::string method = extractJsonToken(line, "method", false);

                            if (method == "ping") {
                                resp = "{\"jsonrpc\":\"2.0\",\"id\":" + idRaw + ",\"result\":{\"pong\":true,\"safeMode\":" +
                                       (Kongor::PatchController::IsSafeModeActive() ? "true" : "false") + "}}\n";
                            } else if (method == "auth") {
                                std::string candTok = extractJsonToken(line, "token", false);
                                bool tokenOk = Kongor_VerifyIpcAuthToken(
                                    candTok.data(), candTok.size(), expectedIpcToken, static_cast<size_t>(expectedIpcTokenLen));
                                clientAuthenticated = tokenOk;
                                resp = "{\"jsonrpc\":\"2.0\",\"id\":" + idRaw + ",\"result\":{\"authenticated\":" +
                                       (tokenOk ? "true" : "false") + "}}\n";
                            } else if (!clientAuthenticated) {
                                resp = "{\"jsonrpc\":\"2.0\",\"id\":" + idRaw +
                                       ",\"error\":{\"code\":-32001,\"message\":\"Unauthorized: send auth method with KONGOR_IPC_TOKEN\"}}\n";
                            } else if (!Kongor::Kongor_IsPipeMethodAllowedInBuild(method.c_str(), kIsPlaytestBuild)) {
                                OutputDebugStringA("[Kongor Pipe Security] Blocked pipe mutation method in playtest build: ");
                                OutputDebugStringA(method.c_str());
                                OutputDebugStringA("\n");
                                resp = "{\"jsonrpc\":\"2.0\",\"id\":" + idRaw +
                                       ",\"error\":{\"code\":-32601,\"message\":\"403 PLAYTEST_LOCKDOWN: Method not permitted in retail/playtest build: " + EscapeJsonString(method) + "\"}}\n";
                            } else if (method == "status") {
                                char statusBuf[4096] = {0};
                                Kongor::PatchController::GetStatusJson(statusBuf, sizeof(statusBuf), "all");
                                resp = "{\"jsonrpc\":\"2.0\",\"id\":" + idRaw + ",\"result\":" + std::string(statusBuf) + "}\n";
                            } else if (method == "hook_toggle") {
                                std::string grp = extractJsonToken(line, "group", false);
                                std::string act = extractJsonToken(line, "action", false);
                                if (grp.empty()) grp = "all";
                                if (act.empty()) act = "status";
                                char toggleBuf[4096] = {0};
                                Kongor::PatchController::ExecuteHookToggle(grp.c_str(), act.c_str(), toggleBuf, sizeof(toggleBuf), false);
                                resp = "{\"jsonrpc\":\"2.0\",\"id\":" + idRaw + ",\"result\":" + std::string(toggleBuf) + "}\n";
                            } else if (method == "safe_mode") {
                                resp = "{\"jsonrpc\":\"2.0\",\"id\":" + idRaw + ",\"result\":{\"safeMode\":" +
                                       (Kongor::PatchController::IsSafeModeActive() ? "true" : "false") +
                                       ",\"reason\":\"" + EscapeJsonString(Kongor::PatchController::GetSafeModeReason()) + "\"}}\n";
                            } else if (method == "execute") {
                                std::string cmd = extractJsonToken(line, "command", false);
                                if (!cmd.empty()) {
                                    DispatchCommandToEngine(Utf8ToWide(cmd));
                                }
                                resp = "{\"jsonrpc\":\"2.0\",\"id\":" + idRaw + ",\"result\":{\"dispatched\":true,\"command\":\"" +
                                       EscapeJsonString(cmd) + "\"}}\n";
                            } else if (method == "get_server_world_state") {
                                int sPort = g_resolvedDedicatedServerPort > 0 ? g_resolvedDedicatedServerPort : ResolveDedicatedServerPortFromCommandLine(GetCommandLineA());
                                WriteServerKeyframe(sPort);
                                double fps = 60.0;
                                uint32_t memMb = 500;
                                uint32_t gamePhase = 0;
                                uint32_t activeMatchId = 0;
                                uint32_t connectedPlayers = 0;
                                uint32_t luaMemKb = 0;
                                uint32_t activeEntities = g_dynamicActiveEntities.load(std::memory_order_relaxed);
                                uint32_t serverTick = g_dynamicServerTick.load(std::memory_order_relaxed);
                                std::string currentMapStr = "";
                                std::string playerNameStr = "KongorSrv";
                                if (g_pTelemetryHeader && g_pTelemetryRecords) {
                                    float cpuMs = g_pTelemetryHeader->LatestRecord.CpuFrameTimeMs;
                                    memMb = g_pTelemetryHeader->LatestRecord.MemoryWorkingSetMb;
                                    if (g_pTelemetryHeader->LatestRecord.ActiveEntities > 0) activeEntities = g_pTelemetryHeader->LatestRecord.ActiveEntities;
                                    if (g_pTelemetryHeader->LatestRecord.ServerTick > 0) serverTick = g_pTelemetryHeader->LatestRecord.ServerTick;
                                    if (cpuMs > 0.001f) fps = 1000.0 / cpuMs;
                                    gamePhase = g_pTelemetryHeader->GamePhase;
                                    activeMatchId = g_pTelemetryHeader->ActiveMatchId;
                                    connectedPlayers = g_pTelemetryHeader->ConnectedPlayers;
                                    luaMemKb = g_pTelemetryHeader->LuaMemoryKb;
                                    size_t mapLen = strnlen(g_pTelemetryHeader->CurrentMap, sizeof(g_pTelemetryHeader->CurrentMap));
                                    currentMapStr = std::string(g_pTelemetryHeader->CurrentMap, mapLen);
                                }
                                resp = "{\"jsonrpc\":\"2.0\",\"id\":" + idRaw + ",\"result\":{" +
                                       "\"currentFps\":" + std::to_string(fps) + "," +
                                       "\"memoryWorkingSetBytes\":" + std::to_string((unsigned long long)memMb * 1024ULL * 1024ULL) + "," +
                                       "\"luaMemoryKb\":" + std::to_string(luaMemKb) + "," +
                                       "\"activeMatchId\":" + std::to_string(activeMatchId) + "," +
                                       "\"currentMap\":\"" + EscapeJsonString(currentMapStr) + "\"," +
                                       "\"playerName\":\"" + EscapeJsonString(playerNameStr) + "\"," +
                                       "\"connectedPlayers\":" + std::to_string(connectedPlayers) + "," +
                                       "\"gamePhase\":" + std::to_string(gamePhase) + "," +
                                       "\"activeEntities\":" + std::to_string(activeEntities) + "," +
                                       "\"serverTick\":" + std::to_string(serverTick) + "}}\n";
                            } else if (method == "apply_server_fixture") {
                                std::string fixturePayload = extractJsonToken(line, "fixture", false);
                                if (fixturePayload.empty()) fixturePayload = extractJsonToken(line, "params", false);
                                std::string safeSingleQuoted;
                                safeSingleQuoted.reserve(fixturePayload.size() + 8);
                                for (char ch : fixturePayload) {
                                    unsigned char uch = static_cast<unsigned char>(ch);
                                    if (uch == '\\' || uch == '\'' || uch == '"') {
                                        safeSingleQuoted.push_back('\\');
                                        safeSingleQuoted.push_back(ch);
                                    } else if (uch == '\n') {
                                        safeSingleQuoted.append("\\n");
                                    } else if (uch == '\r') {
                                        safeSingleQuoted.append("\\r");
                                    } else if (uch >= 0x20) {
                                        safeSingleQuoted.push_back(ch);
                                    }
                                }
                                std::string luaCmd = "Script if TAO and TAO.Dispatch then TAO.Dispatch('apply_fixture', '" +
                                    safeSingleQuoted + "') elseif Nexus and Nexus.ApplySnapshot then Nexus.ApplySnapshot() end";
                                DispatchCommandToEngine(Utf8ToWide(luaCmd));
                                resp = "{\"jsonrpc\":\"2.0\",\"id\":" + idRaw + ",\"result\":{\"applied\":true}}\n";
                            } else if (method == "trigger_match_victory") {
                                char harnessEnv[16] = {0};
                                DWORD hLen = GetEnvironmentVariableA("KONGOR_E2E_HARNESS", harnessEnv, sizeof(harnessEnv));
                                if (hLen == 0 || strcmp(harnessEnv, "1") != 0) {
                                    resp = "{\"jsonrpc\":\"2.0\",\"id\":" + idRaw +
                                           ",\"error\":{\"code\":403,\"message\":\"403 E2E_ONLY: trigger_match_victory requires KONGOR_E2E_HARNESS=1\"}}\n";
                                } else {
                                    char masterEnv[256] = {0};
                                    DWORD mLen = GetEnvironmentVariableA("MASTER_SERVER_ADDRESS", masterEnv, sizeof(masterEnv));
                                    std::string master = (mLen > 0) ? masterEnv : "";
                                    if (!master.empty() && (master.find(":55555") != std::string::npos || master.find(".newbs.io") != std::string::npos)) {
                                        resp = "{\"jsonrpc\":\"2.0\",\"id\":" + idRaw +
                                               ",\"error\":{\"code\":403,\"message\":\"403 FORBIDDEN: trigger_match_victory blocked on production master server target\"}}\n";
                                    } else {
                                        int sPort = g_resolvedDedicatedServerPort > 0 ? g_resolvedDedicatedServerPort : ResolveDedicatedServerPortFromCommandLine(GetCommandLineA());
                                        WriteServerKeyframe(sPort);
                                        DispatchCommandToEngine(L"ServerEndGame 1");
                                        resp = "{\"jsonrpc\":\"2.0\",\"id\":" + idRaw +
                                               ",\"result\":{\"victoryTriggered\":true,\"winningTeam\":1,\"command\":\"ServerEndGame 1\"}}\n";
                                    }
                                }
                            } else {
                                resp = "{\"jsonrpc\":\"2.0\",\"id\":" + idRaw +
                                       ",\"error\":{\"code\":-32601,\"message\":\"Method not found: " + EscapeJsonString(method) + "\"}}\n";
                            }
                        } else if (_strnicmp(line.c_str(), "AUTH", 4) == 0 &&
                                   (line.size() == 4 || line[4] == ' ' || line[4] == '\t')) {
                            bool tokenOk = Kongor_VerifyIpcAuthToken(
                                line.data(), line.size(), expectedIpcToken, static_cast<size_t>(expectedIpcTokenLen));
                            clientAuthenticated = tokenOk;
                            resp = tokenOk
                                ? "{\"authenticated\":true,\"message\":\"Authorized\"}\n"
                                : "{\"authenticated\":false,\"error\":\"Invalid KONGOR_IPC_TOKEN\"}\n";
                        } else if (!clientAuthenticated) {
                            resp = "{\"authenticated\":false,\"error\":\"Unauthorized: send AUTH <KONGOR_IPC_TOKEN> first\"}\n";
                        } else if (line.compare(0, 12, "PEEK_STRUCT ") == 0) {
                            if (!Kongor::Kongor_IsPipeMethodAllowedInBuild("PEEK_STRUCT", kIsPlaytestBuild)) {
                                OutputDebugStringA("[Kongor Pipe Security] Blocked PEEK_STRUCT in playtest build\n");
                                resp = "{\"error\":{\"code\":403,\"message\":\"403 PLAYTEST_LOCKDOWN: Memory inspection not permitted in retail/playtest build\"}}\n";
                            } else {
                                std::istringstream iss(line.substr(12));
                                std::string structName;
                                std::string addrToken;
                                iss >> structName >> addrToken;
                                uintptr_t addr = static_cast<uintptr_t>(strtoull(addrToken.c_str(), nullptr, 0));
                                char peekBuf[2048] = {0};
                                Kongor::StructPeek::FormatStructPeekJson(structName.c_str(), addr, peekBuf, sizeof(peekBuf));
                                resp = std::string(peekBuf) + "\n";
                            }
                        } else {
                            if (!Kongor::Kongor_IsPipeMethodAllowedInBuild(line.c_str(), kIsPlaytestBuild)) {
                                OutputDebugStringA("[Kongor Pipe Security] Blocked raw command dispatch in playtest build: ");
                                OutputDebugStringA(line.c_str());
                                OutputDebugStringA("\n");
                                resp = "{\"id\":1,\"success\":false,\"error\":\"403 PLAYTEST_LOCKDOWN: Raw command execution not permitted in retail/playtest build\"}\n";
                            } else {
                                DispatchCommandToEngine(Utf8ToWide(line));
                                resp = "{\"id\":1,\"success\":true,\"result\":\"" + EscapeJsonString(line) + "\"}\n";
                            }
                        }
                        DWORD bytesWritten = 0;
                        WriteFile(hPipe, resp.data(), (DWORD)resp.size(), &bytesWritten, NULL);
                        FlushFileBuffers(hPipe);
                    }
                } else {
                    break;
                }
            }
        }
    }

    FlushFileBuffers(hPipe);
    DisconnectNamedPipe(hPipe);
    CloseHandle(hPipe);
    int remaining = g_activePipeClients.fetch_sub(1, std::memory_order_relaxed) - 1;
    char discLog[128];
    snprintf(discLog, sizeof(discLog), "[KONGOR NATIVE GUARD] Client disconnected from kongor-native-client pipe (active clients: %d).\n", remaining);
    OutputDebugStringA(discLog);
    return 0;
}

DWORD WINAPI NativeCommandPipeWorker(LPVOID lpParam) {
    std::string pipeName = (lpParam != nullptr)
        ? std::string(reinterpret_cast<const char*>(lpParam))
        : std::string("\\\\.\\pipe\\kongor-native-client");
    if (lpParam == nullptr) {
        char envPipe[256] = {0};
        DWORD envLen = GetEnvironmentVariableA("KONGOR_NATIVE_PIPE", envPipe, sizeof(envPipe));
        if (envLen > 0 && envLen < sizeof(envPipe)) {
            std::string customPipe = envPipe;
            if (customPipe.find("\\\\.\\pipe\\") == 0) {
                pipeName = customPipe;
            } else {
                pipeName = "\\\\.\\pipe\\" + customPipe;
            }
        } else {
            std::string cmdLine = GetCommandLineA();
            int serverPort = ResolveDedicatedServerPortFromCommandLine(cmdLine);
            if (serverPort > 0) {
                pipeName = "\\\\.\\pipe\\hon-server-native-" + std::to_string(serverPort);
            }
        }
    }

    Kongor_ScopedPipeSecurityDescriptor pipeSec;
    if (!pipeSec.IsValid()) {
        OutputDebugStringA("[KONGOR NATIVE GUARD] FATAL SECURITY ERROR: Failed to construct owner-restricted DACL for command pipe; refusing to create NULL-DACL pipe.\n");
        return 1;
    }

    std::string startLog = "[KONGOR NATIVE GUARD] Starting Owner-Restricted In-Process Command Pipe (PIPE_REJECT_REMOTE_CLIENTS, SDDL=" +
        pipeSec.GetSddl() + "): " + pipeName + "\n";
    OutputDebugStringA(startLog.c_str());

    while (g_pipeServerRunning) {
        HANDLE hPipe = CreateNamedPipeA(
            pipeName.c_str(),
            PIPE_ACCESS_DUPLEX,
            Kongor_GetCommandPipeModeFlags(),
            PIPE_UNLIMITED_INSTANCES,
            65536,
            65536,
            0,
            pipeSec.GetSecurityAttributes()
        );

        if (hPipe == INVALID_HANDLE_VALUE) {
            Sleep(250);
            continue;
        }

        BOOL connected = ConnectNamedPipe(hPipe, NULL) ? TRUE : (GetLastError() == ERROR_PIPE_CONNECTED);
        if (!connected) {
            CloseHandle(hPipe);
            continue;
        }

        if (!g_pipeServerRunning) {
            DisconnectNamedPipe(hPipe);
            CloseHandle(hPipe);
            break;
        }

        int activeNow = g_activePipeClients.fetch_add(1, std::memory_order_relaxed) + 1;
        if (activeNow > MAX_CONCURRENT_PIPE_CLIENTS) {
            g_activePipeClients.fetch_sub(1, std::memory_order_relaxed);
            OutputDebugStringA("[KONGOR NATIVE GUARD] Max concurrent pipe clients (16) reached; rejecting excess connection.\n");
            DisconnectNamedPipe(hPipe);
            CloseHandle(hPipe);
            continue;
        }

        char connLog[192];
        snprintf(connLog, sizeof(connLog), "[KONGOR NATIVE GUARD] Client connected to '%s' (active clients: %d).\n", pipeName.c_str(), activeNow);
        OutputDebugStringA(connLog);

        HANDLE hClientThread = CreateThread(NULL, 0, NativeCommandPipeClientHandler, (LPVOID)hPipe, 0, NULL);
        if (hClientThread != NULL) {
            CloseHandle(hClientThread);
        } else {
            // Fallback synchronous handling if thread creation ever fails
            NativeCommandPipeClientHandler((LPVOID)hPipe);
        }
    }

    return 0;
}

extern "C" {
    __declspec(dllexport) uint32_t Kongor_NativeGuard_GetPipeStatus() {
        return (g_hPipeServerThread != NULL && g_pipeServerRunning) ? 1 : 0;
    }


    __declspec(dllexport) void Kongor_NativeGuard_PrewarmHero(const char* heroName, const char* avatarKey) {
        if (heroName) Kongor::EnqueueHeroForPrewarming(heroName, avatarKey ? avatarKey : "");
    }

    __declspec(dllexport) void Kongor_NativeGuard_PrewarmCompetitivePool() {
        Kongor::PrewarmCompetitiveItemPool();
    }

    __declspec(dllexport) void Kongor_NativeGuard_GetPrewarmStats(uint32_t* outFiles, uint64_t* outBytes, float* outDuration, uint32_t* outHeroes, uint32_t* outItems) {
        Kongor::PrewarmStats s = Kongor::GetAssetPrewarmStats();
        if (outFiles) *outFiles = s.FilesPrewarmed;
        if (outBytes) *outBytes = s.BytesPrewarmed;
        if (outDuration) *outDuration = s.DurationMs;
        if (outHeroes) *outHeroes = s.HeroesProcessed;
        if (outItems) *outItems = s.ItemsProcessed;
    }

    __declspec(dllexport) void Kongor_NativeGuard_JobPool_Initialize(uint32_t workerCount) {
        Kongor::JobPool::Initialize(workerCount);
    }

    __declspec(dllexport) void Kongor_NativeGuard_JobPool_Shutdown() {
        Kongor::JobPool::Shutdown();
    }

    __declspec(dllexport) void Kongor_NativeGuard_JobPool_GetStats(uint32_t* outWorkers, uint64_t* outJobs, uint64_t* outBatches, float* outAvgDurationUs, uint32_t* outActive, bool* outRunning) {
        Kongor::JobPoolStats s = Kongor::JobPool::GetStats();
        if (outWorkers) *outWorkers = s.WorkerCount;
        if (outJobs) *outJobs = s.TotalJobsExecuted;
        if (outBatches) *outBatches = s.TotalBatchesExecuted;
        if (outAvgDurationUs) *outAvgDurationUs = s.AverageBatchDurationUs;
        if (outActive) *outActive = s.ActiveWorkers;
        if (outRunning) *outRunning = s.IsRunning;
    }

    __declspec(dllexport) void Kongor_NativeGuard_JobPool_ParallelFor(uint32_t start, uint32_t end, uint32_t minBatchSize, Kongor::ParallelForFn task, void* context) {
        Kongor::JobPool::ParallelFor(start, end, minBatchSize, task, context);
    }

    __declspec(dllexport) void Kongor_NativeGuard_PredictivePathing_Initialize() {
        Kongor::PredictivePathing::Initialize();
    }

    __declspec(dllexport) void Kongor_NativeGuard_PredictivePathing_Shutdown() {
        Kongor::PredictivePathing::Shutdown();
    }

    __declspec(dllexport) void Kongor_NativeGuard_PredictivePathing_OnMoveOrder(float currentX, float currentY, float currentZ, float targetX, float targetY, float moveSpeed, float turnRate) {
        Kongor::PredictivePathing::OnLocalMoveOrder(currentX, currentY, currentZ, targetX, targetY, moveSpeed, turnRate);
    }

    __declspec(dllexport) void Kongor_NativeGuard_PredictivePathing_OnServerSnapshot(float serverX, float serverY, float serverZ, float serverYaw, uint32_t sequenceId) {
        Kongor::PredictivePathing::OnServerSnapshot(serverX, serverY, serverZ, serverYaw, sequenceId);
    }

    __declspec(dllexport) void Kongor_NativeGuard_PredictivePathing_Update(float deltaSeconds, float* outRenderX, float* outRenderY, float* outRenderZ, float* outRenderYaw) {
        Kongor::PredictivePathing::Update(deltaSeconds, outRenderX, outRenderY, outRenderZ, outRenderYaw);
    }

    __declspec(dllexport) void Kongor_NativeGuard_PredictivePathing_GetStats(uint64_t* outOrders, uint64_t* outRecons, uint64_t* outHardCorrections, float* outAvgError, float* outMaxLatency, bool* outEnabled, bool* outTurnOnly) {
        Kongor::PredictivePathing::PredictionStats s = Kongor::PredictivePathing::GetStats();
        if (outOrders) *outOrders = s.TotalMoveCommandsPredicted;
        if (outRecons) *outRecons = s.TotalReconciliations;
        if (outHardCorrections) *outHardCorrections = s.HardCorrections;
        if (outAvgError) *outAvgError = s.AvgErrorDistance;
        if (outMaxLatency) *outMaxLatency = s.MaxLatencyCompensatedMs;
        if (outEnabled) *outEnabled = s.IsEnabled;
        if (outTurnOnly) *outTurnOnly = s.TurnOnlyMode;
    }

    __declspec(dllexport) void Kongor_NativeGuard_PredictivePathing_SetConfig(bool enabled, bool turnOnly, float maxLatencyMs) {
        Kongor::PredictivePathing::SetConfig(enabled, turnOnly, maxLatencyMs);
    }
}


bool Kongor_ProcessPipeStreamLines(const char* rawInput, char* outResp, size_t outCap) {
    if (!rawInput || !outResp || outCap == 0) return false;
    outResp[0] = '\0';
    std::string streamBuffer(rawInput);
    std::string combinedResp;

    while (!streamBuffer.empty()) {
        size_t nlPos = streamBuffer.find('\n');
        std::string line;
        if (nlPos != std::string::npos) {
            line = streamBuffer.substr(0, nlPos);
            streamBuffer.erase(0, nlPos + 1);
        } else {
            line = streamBuffer;
            streamBuffer.clear();
        }
        if (!line.empty() && line.back() == '\r') {
            line.pop_back();
        }
        if (line.empty()) continue;

        std::string resp;
        if (line.front() == '{' && line.find("\"jsonrpc\"") != std::string::npos && line.find("\"2.0\"") != std::string::npos) {
            auto extractJsonToken = [](const std::string& jsonStr, const char* key, bool preserveQuotes = false) -> std::string {
                std::string needle = std::string("\"") + key + "\"";
                size_t pos = jsonStr.find(needle);
                if (pos == std::string::npos) return "";
                size_t colon = jsonStr.find(':', pos + needle.size());
                if (colon == std::string::npos) return "";
                size_t valStart = colon + 1;
                while (valStart < jsonStr.size() && (jsonStr[valStart] == ' ' || jsonStr[valStart] == '\t')) ++valStart;
                if (valStart >= jsonStr.size()) return "";
                if (jsonStr[valStart] == '"') {
                    size_t valEnd = jsonStr.find('"', valStart + 1);
                    if (valEnd == std::string::npos) return "";
                    if (preserveQuotes) {
                        return jsonStr.substr(valStart, (valEnd - valStart) + 1);
                    }
                    return jsonStr.substr(valStart + 1, valEnd - (valStart + 1));
                }
                size_t valEnd = jsonStr.find_first_of(",}\r\n ", valStart);
                if (valEnd == std::string::npos) valEnd = jsonStr.size();
                return jsonStr.substr(valStart, valEnd - valStart);
            };

            std::string idRaw = extractJsonToken(line, "id", true);
            if (idRaw.empty()) idRaw = "1";
            std::string method = extractJsonToken(line, "method", false);

            if (method == "ping") {
                resp = "{\"jsonrpc\":\"2.0\",\"id\":" + idRaw + ",\"result\":{\"pong\":true,\"safeMode\":" +
                       (Kongor::PatchController::IsSafeModeActive() ? "true" : "false") + "}}\n";
            } else if (method == "status") {
                char statusBuf[4096] = {0};
                Kongor::PatchController::GetStatusJson(statusBuf, sizeof(statusBuf), "all");
                resp = "{\"jsonrpc\":\"2.0\",\"id\":" + idRaw + ",\"result\":" + std::string(statusBuf) + "}\n";
            } else if (method == "hook_toggle") {
                std::string grp = extractJsonToken(line, "group", false);
                std::string act = extractJsonToken(line, "action", false);
                if (grp.empty()) grp = "all";
                if (act.empty()) act = "status";
                char toggleBuf[4096] = {0};
                Kongor::PatchController::ExecuteHookToggle(grp.c_str(), act.c_str(), toggleBuf, sizeof(toggleBuf), false);
                resp = "{\"jsonrpc\":\"2.0\",\"id\":" + idRaw + ",\"result\":" + std::string(toggleBuf) + "}\n";
            } else if (method == "safe_mode") {
                resp = "{\"jsonrpc\":\"2.0\",\"id\":" + idRaw + ",\"result\":{\"safeMode\":" +
                       (Kongor::PatchController::IsSafeModeActive() ? "true" : "false") +
                       ",\"reason\":\"" + EscapeJsonString(Kongor::PatchController::GetSafeModeReason()) + "\"}}\n";
            } else if (method == "execute") {
                std::string cmd = extractJsonToken(line, "command", false);
                resp = "{\"jsonrpc\":\"2.0\",\"id\":" + idRaw + ",\"result\":{\"dispatched\":true,\"command\":\"" +
                       EscapeJsonString(cmd) + "\"}}\n";
            } else {
                resp = "{\"jsonrpc\":\"2.0\",\"id\":" + idRaw +
                       ",\"error\":{\"code\":-32601,\"message\":\"Method not found: " + EscapeJsonString(method) + "\"}}\n";
            }
        } else if (line.compare(0, 12, "PEEK_STRUCT ") == 0) {
            std::istringstream iss(line.substr(12));
            std::string structName;
            std::string addrToken;
            iss >> structName >> addrToken;
            uintptr_t addr = static_cast<uintptr_t>(strtoull(addrToken.c_str(), nullptr, 0));
            char peekBuf[2048] = {0};
            Kongor::StructPeek::FormatStructPeekJson(structName.c_str(), addr, peekBuf, sizeof(peekBuf));
            resp = std::string(peekBuf) + "\n";
        } else {
            resp = "{\"id\":1,\"success\":true,\"result\":\"" + EscapeJsonString(line) + "\"}\n";
        }
        combinedResp += resp;
    }

    size_t copyLen = (combinedResp.size() < (outCap - 1)) ? combinedResp.size() : (outCap - 1);
    memcpy(outResp, combinedResp.data(), copyLen);
    outResp[copyLen] = '\0';
    return !combinedResp.empty();
}

