// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#pragma once
// ============================================================================
// Kongor In-Process Symbolicated Native Stack Walker (kongor_stacktrace.h)
// Captures instant local PDB + module RVA call stacks (k2_x64.dll+0x...,
// cgame_x64.dll+0x..., game_x64.dll+0x...) at crash time or on demand without
// spawning external cdb.exe or stalling on remote symbol servers.
// ============================================================================

#include <windows.h>
#include <dbghelp.h>
#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <sstream>
#include <iomanip>
#include <string>
#include <vector>

namespace Kongor {
namespace StackTrace {

struct RvaSymbolEntry {
    uint32_t startRva = 0;
    uint32_t endRva = 0;
    std::string name;
};

struct ModuleSymbolTable {
    std::string moduleName;
    uint32_t expectedTimeDateStamp = 0;
    uint32_t expectedSizeOfImage = 0;
    std::string expectedSha256;
    std::vector<RvaSymbolEntry> entries;
};

enum class RvaLookupResult {
    NotFound = 0,
    Resolved = 1,
    StaleHeader = 2
};

inline SRWLOCK g_symLock = SRWLOCK_INIT;
inline bool g_symInitialized = false;
inline std::vector<ModuleSymbolTable> g_rvaSymbolTables;

inline bool LoadSymbolTableFromStream(std::istream& in, std::vector<ModuleSymbolTable>& outTables) {
    outTables.clear();
    ModuleSymbolTable* currentMod = nullptr;
    std::string line;

    while (std::getline(in, line)) {
        while (!line.empty() && (line.back() == '\r' || line.back() == '\n' || line.back() == ' ' || line.back() == '\t')) {
            line.pop_back();
        }
        size_t firstNonSpace = line.find_first_not_of(" \t");
        if (firstNonSpace == std::string::npos) continue;
        if (line[firstNonSpace] == '#') continue;

        std::istringstream iss(line.substr(firstNonSpace));
        std::string token;
        if (!(iss >> token)) continue;

        if (token == "@MODULE") {
            std::string modName, tsStr, sizeStr, shaStr;
            if (iss >> modName >> tsStr >> sizeStr) {
                iss >> shaStr;
                ModuleSymbolTable table;
                table.moduleName = modName;
                table.expectedTimeDateStamp = static_cast<uint32_t>(std::strtoul(tsStr.c_str(), nullptr, 0));
                table.expectedSizeOfImage = static_cast<uint32_t>(std::strtoul(sizeStr.c_str(), nullptr, 0));
                table.expectedSha256 = shaStr;
                outTables.push_back(std::move(table));
                currentMod = &outTables.back();
            }
        } else if (currentMod != nullptr) {
            std::string endStr, symName;
            if (iss >> endStr >> symName) {
                RvaSymbolEntry entry;
                entry.startRva = static_cast<uint32_t>(std::strtoul(token.c_str(), nullptr, 0));
                entry.endRva = static_cast<uint32_t>(std::strtoul(endStr.c_str(), nullptr, 0));
                if (entry.endRva <= entry.startRva) {
                    entry.endRva = entry.startRva + 0x80;
                }
                entry.name = symName;
                currentMod->entries.push_back(std::move(entry));
            }
        }
    }

    for (auto& mod : outTables) {
        std::sort(mod.entries.begin(), mod.entries.end(), [](const RvaSymbolEntry& a, const RvaSymbolEntry& b) {
            return a.startRva < b.startRva;
        });
    }
    return !outTables.empty();
}

inline bool LoadSymbolTableFromFile(const char* filePath, std::vector<ModuleSymbolTable>& outTables) {
    if (!filePath || !*filePath) return false;
    std::ifstream ifs(filePath);
    if (!ifs.is_open()) return false;
    return LoadSymbolTableFromStream(ifs, outTables);
}

inline bool ReadModulePeIdentity(HMODULE hMod, uint32_t& outTimeDateStamp, uint32_t& outSizeOfImage) {
    outTimeDateStamp = 0;
    outSizeOfImage = 0;
    if (!hMod) return false;
    __try {
        const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(hMod);
        if (dos->e_magic != IMAGE_DOS_SIGNATURE) return false;
        const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS*>(
            reinterpret_cast<const uint8_t*>(hMod) + dos->e_lfanew);
        if (nt->Signature != IMAGE_NT_SIGNATURE) return false;
        outTimeDateStamp = nt->FileHeader.TimeDateStamp;
        outSizeOfImage = nt->OptionalHeader.SizeOfImage;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

inline RvaLookupResult ResolveRvaSymbol(
    const std::vector<ModuleSymbolTable>& tables,
    const char* modBaseName,
    uint32_t actualTimeDateStamp,
    uint32_t actualSizeOfImage,
    uint32_t rva,
    std::string& outFormatted)
{
    outFormatted.clear();
    if (!modBaseName || !*modBaseName) return RvaLookupResult::NotFound;

    const ModuleSymbolTable* matchedTable = nullptr;
    for (const auto& t : tables) {
        if (_stricmp(t.moduleName.c_str(), modBaseName) == 0) {
            matchedTable = &t;
            break;
        }
    }
    if (!matchedTable) return RvaLookupResult::NotFound;

    if (matchedTable->expectedTimeDateStamp != actualTimeDateStamp ||
        matchedTable->expectedSizeOfImage != actualSizeOfImage) {
        std::ostringstream stale;
        stale << "[SYMBOLS][STALE: " << modBaseName
              << " header mismatch (expected ts=0x" << std::hex << std::uppercase << matchedTable->expectedTimeDateStamp
              << " size=0x" << matchedTable->expectedSizeOfImage
              << ", actual ts=0x" << actualTimeDateStamp
              << " size=0x" << actualSizeOfImage
              << ") - falling back to raw RVA]";
        outFormatted = stale.str();
        return RvaLookupResult::StaleHeader;
    }

    // Binary search sorted entries for startRva <= rva < endRva
    auto it = std::upper_bound(
        matchedTable->entries.begin(),
        matchedTable->entries.end(),
        rva,
        [](uint32_t val, const RvaSymbolEntry& e) { return val < e.startRva; });

    if (it != matchedTable->entries.begin()) {
        --it;
        if (rva >= it->startRva && rva < it->endRva) {
            std::ostringstream sym;
            sym << "(" << it->name << "+0x" << std::hex << (rva - it->startRva) << ")";
            outFormatted = sym.str();
            return RvaLookupResult::Resolved;
        }
    }

    return RvaLookupResult::NotFound;
}

inline void EnsureLocalSymbolsInitialized(HANDLE hProcess) {
    if (g_symInitialized) return;
    SymSetOptions(SYMOPT_UNDNAME | SYMOPT_DEFERRED_LOADS | SYMOPT_LOAD_LINES | SYMOPT_FAIL_CRITICAL_ERRORS | SYMOPT_NO_PROMPTS);
    // Restrict symbol search to local executable directory only (never block on internet symbol servers)
    char exeDir[MAX_PATH] = {0};
    if (GetModuleFileNameA(NULL, exeDir, MAX_PATH) > 0) {
        char* lastSlash = strrchr(exeDir, '\\');
        if (lastSlash) *lastSlash = '\0';
        SymInitialize(hProcess, exeDir, TRUE);

        std::string exeSymPath = std::string(exeDir) + "\\kongor_symbols.rva";
        if (!LoadSymbolTableFromFile(exeSymPath.c_str(), g_rvaSymbolTables)) {
            LoadSymbolTableFromFile("games/client/kongor_symbols.rva", g_rvaSymbolTables);
        }
    } else {
        SymInitialize(hProcess, NULL, TRUE);
        LoadSymbolTableFromFile("games/client/kongor_symbols.rva", g_rvaSymbolTables);
    }
    g_symInitialized = true;
}

inline void FormatAddressModuleAndSymbol(HANDLE hProcess, uintptr_t address, std::ostream& out, int frameIndex) {
    HMODULE hMod = NULL;
    char modPath[MAX_PATH] = "UnknownModule";
    const char* modBaseName = "UnknownModule";
    uintptr_t rva = address;

    if (GetModuleHandleExA(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCSTR>(address),
            &hMod) && hMod != NULL) {
        rva = address - reinterpret_cast<uintptr_t>(hMod);
        if (GetModuleFileNameA(hMod, modPath, MAX_PATH) > 0) {
            const char* slash = strrchr(modPath, '\\');
            modBaseName = slash ? (slash + 1) : modPath;
        }
    }

    out << "  #" << std::dec << std::setw(2) << std::setfill('0') << frameIndex
        << " 0x" << std::hex << std::uppercase << std::setw(16) << std::setfill('0') << address
        << " " << modBaseName << "+0x" << std::hex << rva;

    // 1. Check hash-pinned RVA symbol table first (covers stripped retail K2 DLLs with stale-header guard)
    bool resolvedByRvaTable = false;
    if (hMod != NULL && !g_rvaSymbolTables.empty()) {
        uint32_t actualTs = 0, actualSize = 0;
        if (ReadModulePeIdentity(hMod, actualTs, actualSize)) {
            std::string rvaFormatted;
            RvaLookupResult rvaRes = ResolveRvaSymbol(
                g_rvaSymbolTables,
                modBaseName,
                actualTs,
                actualSize,
                static_cast<uint32_t>(rva),
                rvaFormatted);
            if (rvaRes == RvaLookupResult::Resolved || rvaRes == RvaLookupResult::StaleHeader) {
                out << " " << rvaFormatted;
                resolvedByRvaTable = (rvaRes == RvaLookupResult::Resolved);
            }
        }
    }

    // 2. Fallback to DbgHelp local PDB / export table
    if (!resolvedByRvaTable) {
        uint8_t symBuffer[sizeof(SYMBOL_INFO) + MAX_SYM_NAME] = {0};
        PSYMBOL_INFO pSymbol = reinterpret_cast<PSYMBOL_INFO>(symBuffer);
        pSymbol->SizeOfStruct = sizeof(SYMBOL_INFO);
        pSymbol->MaxNameLen = MAX_SYM_NAME;

        DWORD64 displacement = 0;
        if (SymFromAddr(hProcess, static_cast<DWORD64>(address), &displacement, pSymbol)) {
            out << " (" << pSymbol->Name << "+0x" << std::hex << displacement << ")";
        }
    }

    IMAGEHLP_LINE64 lineInfo = {};
    lineInfo.SizeOfStruct = sizeof(IMAGEHLP_LINE64);
    DWORD lineDisp = 0;
    if (SymGetLineFromAddr64(hProcess, static_cast<DWORD64>(address), &lineDisp, &lineInfo) && lineInfo.FileName) {
        const char* fileSlash = strrchr(lineInfo.FileName, '\\');
        const char* shortFile = fileSlash ? (fileSlash + 1) : lineInfo.FileName;
        out << " [" << shortFile << ":" << std::dec << lineInfo.LineNumber << "]";
    }

    out << "\n";
}

inline std::string CaptureExceptionStackTrace(const CONTEXT* pContextRecord, uint32_t maxFrames = 32) {
    if (!pContextRecord) return "  <no context record available>\n";

    std::ostringstream out;
    AcquireSRWLockExclusive(&g_symLock);

    HANDLE hProcess = GetCurrentProcess();
    HANDLE hThread = GetCurrentThread();
    EnsureLocalSymbolsInitialized(hProcess);

    CONTEXT ctxCopy = *pContextRecord;
    STACKFRAME64 frame = {};
#if defined(_M_X64) || defined(__x86_64__)
    DWORD machineType = IMAGE_FILE_MACHINE_AMD64;
    frame.AddrPC.Offset = ctxCopy.Rip;
    frame.AddrPC.Mode = AddrModeFlat;
    frame.AddrFrame.Offset = ctxCopy.Rbp;
    frame.AddrFrame.Mode = AddrModeFlat;
    frame.AddrStack.Offset = ctxCopy.Rsp;
    frame.AddrStack.Mode = AddrModeFlat;
#else
    DWORD machineType = IMAGE_FILE_MACHINE_I386;
    frame.AddrPC.Offset = ctxCopy.Eip;
    frame.AddrPC.Mode = AddrModeFlat;
    frame.AddrFrame.Offset = ctxCopy.Ebp;
    frame.AddrFrame.Mode = AddrModeFlat;
    frame.AddrStack.Offset = ctxCopy.Esp;
    frame.AddrStack.Mode = AddrModeFlat;
#endif

    for (uint32_t i = 0; i < maxFrames; ++i) {
        BOOL walked = StackWalk64(
            machineType,
            hProcess,
            hThread,
            &frame,
            &ctxCopy,
            NULL,
            SymFunctionTableAccess64,
            SymGetModuleBase64,
            NULL);

        if (!walked || frame.AddrPC.Offset == 0) {
            break;
        }

        FormatAddressModuleAndSymbol(hProcess, static_cast<uintptr_t>(frame.AddrPC.Offset), out, static_cast<int>(i));
    }

    ReleaseSRWLockExclusive(&g_symLock);
    return out.str();
}

inline std::string CaptureCurrentStackTrace(uint32_t skipFrames = 1, uint32_t maxFrames = 32) {
    void* backtrace[64] = {0};
    uint32_t framesToCapture = (maxFrames > 60) ? 60 : maxFrames;
    USHORT captured = RtlCaptureStackBackTrace(skipFrames, framesToCapture, backtrace, NULL);

    std::ostringstream out;
    AcquireSRWLockExclusive(&g_symLock);

    HANDLE hProcess = GetCurrentProcess();
    EnsureLocalSymbolsInitialized(hProcess);

    for (USHORT i = 0; i < captured; ++i) {
        FormatAddressModuleAndSymbol(hProcess, reinterpret_cast<uintptr_t>(backtrace[i]), out, static_cast<int>(i));
    }

    ReleaseSRWLockExclusive(&g_symLock);
    return out.str();
}

} // namespace StackTrace
} // namespace Kongor
