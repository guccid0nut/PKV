// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ============================================================================
// Project Kongor - NativeGuard Lua Evaluation & UI Event Queue Helpers
// ============================================================================

#include "native_guard_internal.h"
#include "kongor_lua_shared_mem.h"
#include "kongor_tracy.h"
#include <windows.h>
#include <cstdint>
#include <string>
#include <vector>
#include <cstdio>
#include <cstring>
#include <cstdlib>
#include <cwchar>
#include <atomic>

static int HexNibbleToInt(char c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return 10 + (c - 'a');
    if (c >= 'A' && c <= 'F') return 10 + (c - 'A');
    return -1;
}

// Decodes optional "hex:<hex-bytes>" payload emitted by EvalLuaSync runner so multi-line outputs,
// semicolons ';', quotes, and '%' format tokens bypass K2 CConsole::Execute splitting/formatting.
static std::string DecodeEvalResPayloadUtf8(const std::string& rawPayloadUtf8) {
    if (rawPayloadUtf8.size() >= 4 && _strnicmp(rawPayloadUtf8.c_str(), "hex:", 4) == 0) {
        size_t endPos = rawPayloadUtf8.size();
        while (endPos > 4) {
            char tail = rawPayloadUtf8[endPos - 1];
            if (tail == ' ' || tail == '\t' || tail == '\r' || tail == '\n') {
                --endPos;
            } else {
                break;
            }
        }
        const size_t hexLen = endPos - 4;
        if ((hexLen % 2) == 0) {
            std::string decoded;
            decoded.reserve(hexLen / 2);
            bool validHex = true;
            for (size_t i = 4; i < endPos; i += 2) {
                int hi = HexNibbleToInt(rawPayloadUtf8[i]);
                int lo = HexNibbleToInt(rawPayloadUtf8[i + 1]);
                if (hi < 0 || lo < 0) {
                    validHex = false;
                    break;
                }
                decoded.push_back(static_cast<char>((hi << 4) | lo));
            }
            if (validHex) {
                return decoded;
            }
        }
    }
    return rawPayloadUtf8;
}

bool ParseEvalResCommandArgsW(
    const wchar_t* pCmdArgs,
    uint64_t* outEvalId,
    bool* outOk,
    std::string* outDecodedUtf8) {
    if (!pCmdArgs || !outEvalId || !outOk || !outDecodedUtf8) return false;
    const wchar_t* pId = pCmdArgs;
    while (*pId == L' ' || *pId == L'\t') ++pId;
    if (*pId == L'\0') return false;

    wchar_t* pAfterId = nullptr;
    uint64_t evalId = _wcstoui64(pId, &pAfterId, 10);
    if (!pAfterId || pAfterId == pId) return false;

    const wchar_t* pOk = pAfterId;
    while (*pOk == L' ' || *pOk == L'\t') ++pOk;
    if (*pOk != L'0' && *pOk != L'1') return false;
    bool ok = (*pOk == L'1');

    const wchar_t* pPayload = pOk;
    while (*pPayload && *pPayload != L' ' && *pPayload != L'\t') ++pPayload;
    if (*pPayload == L' ' || *pPayload == L'\t') ++pPayload;

    *outEvalId = evalId;
    *outOk = ok;
    *outDecodedUtf8 = DecodeEvalResPayloadUtf8(WideToUtf8(std::wstring(pPayload)));
    return true;
}

std::wstring Utf8ToWide(const std::string& str) {
    if (str.empty()) return std::wstring();
    int sizeNeeded = MultiByteToWideChar(CP_UTF8, 0, str.data(), (int)str.size(), NULL, 0);
    if (sizeNeeded <= 0) return std::wstring();
    std::wstring wstr(sizeNeeded, 0);
    MultiByteToWideChar(CP_UTF8, 0, str.data(), (int)str.size(), &wstr[0], sizeNeeded);
    return wstr;
}

std::string WideToUtf8(const std::wstring& wstr) {
    if (wstr.empty()) return std::string();
    int sizeNeeded = WideCharToMultiByte(CP_UTF8, 0, wstr.data(), (int)wstr.size(), NULL, 0, NULL, NULL);
    if (sizeNeeded <= 0) return std::string();
    std::string str(sizeNeeded, 0);
    WideCharToMultiByte(CP_UTF8, 0, wstr.data(), (int)wstr.size(), &str[0], sizeNeeded, NULL, NULL);
    return str;
}


std::string EscapeJsonString(const std::string& input) {
    std::string out;
    out.reserve(input.size() + 16);
    for (unsigned char c : input) {
        switch (c) {
            case '\"': out += "\\\""; break;
            case '\\': out += "\\\\"; break;
            case '\b': out += "\\b";  break;
            case '\f': out += "\\f";  break;
            case '\n': out += "\\n";  break;
            case '\r': out += "\\r";  break;
            case '\t': out += "\\t";  break;
            default:
                if (c < 0x20) {
                    char hex[8];
                    snprintf(hex, sizeof(hex), "\\u%04x", (unsigned int)c);
                    out += hex;
                } else {
                    out += (char)c;
                }
                break;
        }
    }
    return out;
}

uint32_t GetCurrentNativeGamePhaseForLuaShim() {
    if (g_pTelemetryHeader) {
        return g_pTelemetryHeader->GamePhase;
    }
    const int lastKnown = g_lastKnownGamePhase.load(std::memory_order_relaxed);
    return (lastKnown > 0) ? static_cast<uint32_t>(lastKnown) : 0u;
}

// Builds a K2 CConsole-safe single-line Script "..." command for EvalLuaSync (MCP-246, MCP-231):
// - Strictly < 1200 bytes so it NEVER hits K2 CConsole's hard 2048-byte command-line limit.
// - Zero '\n', '\r', ';', '%', or '\\' characters inside the Script argument.
// - Writes arbitrary-length multi-line/semicolon/'%' results directly via KongorSharedMem.SetEvalResult
//   (with pre-encoded 'hex:' Cmd('__kongor_eval_res ...') fast-fail fallback if KongorSharedMem is unbound).
// - Installs non-intrusive GetGamePhase() / Host.GetGamePhase() Lua shims when GetGamePhase == nil.
std::string BuildEvalRunnerCommandUtf8(uint64_t evalId, uint32_t nativeGamePhase) {
    KONGOR_TRACY_ZONE_SCOPED_N("NativeGuard_BuildEvalRunnerCmd");
    const std::string idStr = std::to_string(evalId);
    const std::string phaseStr = std::to_string(nativeGamePhase);

    std::string lua = "Script \"";
    lua += "rawset(_G,'__kongor_native_game_phase'," + phaseStr + ") ";
    lua += "if rawget(_G,'GetGamePhase')==nil then ";
    lua += "rawset(_G,'GetGamePhase',function() ";
    lua += "if KongorSharedMem and KongorSharedMem.GetGamePhase then ";
    lua += "local sp=KongorSharedMem.GetGamePhase() if sp and sp>0 then return sp end end ";
    lua += "local p=tonumber(rawget(_G,'__kongor_native_game_phase')) or 0 ";
    lua += "if p>0 then return p end ";
    lua += "local gt=(LuaTrigger and LuaTrigger.GetTrigger) or GetTrigger ";
    lua += "if gt then local ok,gp=pcall(gt,'GamePhase') ";
    lua += "if ok and gp and gp.gamePhase~=nil then return tonumber(gp.gamePhase) or 0 end end ";
    lua += "return p end) end ";
    lua += "if type(rawget(_G,'Host'))=='table' then ";
    lua += "if Host.GetGamePhase==nil then Host.GetGamePhase=rawget(_G,'GetGamePhase') end ";
    lua += "elseif rawget(_G,'Host')==nil then ";
    lua += "local _h=loadstring('return '..string.char(123,125))() ";
    lua += "_h.GetGamePhase=rawget(_G,'GetGamePhase') rawset(_G,'Host',_h) end ";
    lua += "local _k=KongorSharedMem ";
    lua += "if not (_k and _k.GetPendingEval and _k.SetEvalResult) then ";
    // 'KongorSharedMem_unbound' hex-encoded so DecodeEvalResArgs decodes cleanly with zero runtime loop
    lua += "Cmd('__kongor_eval_res " + idStr + " 0 hex:4b6f6e676f725368617265644d656d5f756e626f756e64') ";
    lua += "else ";
    lua += "local _c,_id=_k.GetPendingEval() ";
    lua += "if not (_c and _id) then _k.SetEvalResult('" + idStr + "',false,'KongorSharedMem.GetPendingEval returned empty slot') ";
    lua += "else ";
    lua += "local _fn,_e=loadstring('return '.._c) ";
    lua += "if not _fn then _fn,_e=loadstring(_c) end ";
    lua += "if not _fn then _k.SetEvalResult(_id,false,tostring(_e or 'syntax error')) ";
    lua += "else ";
    lua += "local _eb='' local _oe=Echo ";
    lua += "Echo=function(m) _eb=(_eb=='' and tostring(m)) or (_eb..string.char(10)..tostring(m)) if _oe then _oe(m) end end ";
    lua += "local _ok,_r=pcall(_fn) Echo=_oe ";
    lua += "local _o='nil' ";
    lua += "if _r~=nil then _o=(_eb~='' and (_eb..string.char(10)..tostring(_r))) or tostring(_r) ";
    lua += "elseif _eb~='' then _o=_eb end ";
    lua += "if #_o>65536 then _o=string.sub(_o,1,65536) end ";
    lua += "_k.SetEvalResult(_id,_ok,tostring(_o)) ";
    lua += "end end end\"";
    return lua;
}

// Extracts and unescapes a JSON string or scalar field cleanly (MCP-234).
std::string ExtractJsonUiField(const std::string& jsonStr, const char* key) {
    if (!key || !*key || jsonStr.empty()) return "";
    const std::string needle = std::string("\"") + key + "\"";
    size_t searchFrom = 0;
    while (searchFrom < jsonStr.size()) {
        size_t pos = jsonStr.find(needle, searchFrom);
        if (pos == std::string::npos) return "";
        bool validBoundary = (pos == 0);
        if (!validBoundary) {
            size_t prev = pos;
            while (prev > 0 && (jsonStr[prev - 1] == ' ' || jsonStr[prev - 1] == '\t' || jsonStr[prev - 1] == '\r' || jsonStr[prev - 1] == '\n')) {
                --prev;
            }
            if (prev > 0 && (jsonStr[prev - 1] == '{' || jsonStr[prev - 1] == ',')) {
                validBoundary = true;
            }
        }
        size_t afterKey = pos + needle.size();
        while (afterKey < jsonStr.size() && (jsonStr[afterKey] == ' ' || jsonStr[afterKey] == '\t' || jsonStr[afterKey] == '\r' || jsonStr[afterKey] == '\n')) {
            ++afterKey;
        }
        if (!validBoundary || afterKey >= jsonStr.size() || jsonStr[afterKey] != ':') {
            searchFrom = pos + needle.size();
            continue;
        }
        size_t valStart = afterKey + 1;
        while (valStart < jsonStr.size() && (jsonStr[valStart] == ' ' || jsonStr[valStart] == '\t' || jsonStr[valStart] == '\r' || jsonStr[valStart] == '\n')) {
            ++valStart;
        }
        if (valStart >= jsonStr.size()) return "";
        if (jsonStr[valStart] == '"') {
            std::string out;
            for (size_t i = valStart + 1; i < jsonStr.size(); ++i) {
                char c = jsonStr[i];
                if (c == '\\') {
                    if (i + 1 >= jsonStr.size()) break;
                    char esc = jsonStr[++i];
                    switch (esc) {
                        case '"':  out.push_back('"'); break;
                        case '\\': out.push_back('\\'); break;
                        case '/':  out.push_back('/'); break;
                        case 'b':  out.push_back('\b'); break;
                        case 'f':  out.push_back('\f'); break;
                        case 'n':  out.push_back('\n'); break;
                        case 'r':  out.push_back('\r'); break;
                        case 't':  out.push_back('\t'); break;
                        case 'u': {
                            if (i + 4 < jsonStr.size()) {
                                int d0 = HexNibbleToInt(jsonStr[i + 1]);
                                int d1 = HexNibbleToInt(jsonStr[i + 2]);
                                int d2 = HexNibbleToInt(jsonStr[i + 3]);
                                int d3 = HexNibbleToInt(jsonStr[i + 4]);
                                if (d0 >= 0 && d1 >= 0 && d2 >= 0 && d3 >= 0) {
                                    uint32_t cp = static_cast<uint32_t>((d0 << 12) | (d1 << 8) | (d2 << 4) | d3);
                                    if (cp <= 0x7Fu) {
                                        out.push_back(static_cast<char>(cp));
                                    } else if (cp <= 0x7FFu) {
                                        out.push_back(static_cast<char>(0xC0u | (cp >> 6)));
                                        out.push_back(static_cast<char>(0x80u | (cp & 0x3Fu)));
                                    } else {
                                        out.push_back(static_cast<char>(0xE0u | (cp >> 12)));
                                        out.push_back(static_cast<char>(0x80u | ((cp >> 6) & 0x3Fu)));
                                        out.push_back(static_cast<char>(0x80u | (cp & 0x3Fu)));
                                    }
                                    i += 4;
                                }
                            }
                            break;
                        }
                        default:
                            out.push_back(esc);
                            break;
                    }
                } else if (c == '"') {
                    return out;
                } else {
                    out.push_back(c);
                }
            }
            return out;
        }
        size_t valEnd = jsonStr.find_first_of(",}\r\n \t", valStart);
        if (valEnd == std::string::npos) valEnd = jsonStr.size();
        std::string scalar = jsonStr.substr(valStart, valEnd - valStart);
        return (scalar == "null") ? "" : scalar;
    }
    return "";
}

static bool IsK2ConsoleSafeSingleQuoteChar(unsigned char ch) {
    if (ch < 0x20 || ch >= 0x7F) return false;
    return ch != '"' && ch != '\'' && ch != '\\' && ch != ';' && ch != '%';
}

// Encodes an arbitrary UTF-8 string into a compact Lua 5.1 expression containing zero double-quotes,
// backslashes, semicolons, percent signs, or newlines (MCP-234).
std::string EncodeK2ConsoleSafeLuaStringExpr(const std::string& s) {
    if (s.empty()) return "''";
    std::string expr;
    size_t i = 0;
    while (i < s.size()) {
        if (!expr.empty()) expr += "..";
        if (IsK2ConsoleSafeSingleQuoteChar(static_cast<unsigned char>(s[i]))) {
            expr += "'";
            while (i < s.size() && IsK2ConsoleSafeSingleQuoteChar(static_cast<unsigned char>(s[i]))) {
                expr.push_back(s[i]);
                ++i;
            }
            expr += "'";
        } else {
            expr += "string.char(";
            size_t count = 0;
            while (i < s.size() && !IsK2ConsoleSafeSingleQuoteChar(static_cast<unsigned char>(s[i])) && count < 64) {
                if (count > 0) expr += ",";
                expr += std::to_string(static_cast<unsigned int>(static_cast<unsigned char>(s[i])));
                ++i;
                ++count;
            }
            expr += ")";
        }
    }
    return expr;
}

// Builds a K2 CConsole-safe Script "..." command for DispatchUiEvent (MCP-234):
// - Unescapes JSON fields safely and encodes widget/interface/event/onclicklua via EncodeK2ConsoleSafeLuaStringExpr.
// - Supports GetCallback('onclick') (which also backs K2 XML onclicklua="..."), child button templates
//   (_button/_btn/_catcher + depth-3 child hierarchy), explicit onclicklua script strings, and DoEvent()/DoEventN(0).
std::string BuildDispatchUiEventCommandUtf8(
    const std::string& payloadStr,
    std::string* outIfaceName,
    std::string* outWidgetName,
    std::string* outEventType) {
    std::string ifaceName = "main";
    std::string widgetName = payloadStr;
    std::string eventType = "onclick";
    std::string inlineOnClickLua;

    if (!payloadStr.empty() && payloadStr.front() == '{') {
        std::string parsedIface = ExtractJsonUiField(payloadStr, "interfaceName");
        if (parsedIface.empty()) parsedIface = ExtractJsonUiField(payloadStr, "interface");
        if (!parsedIface.empty()) ifaceName = parsedIface;

        std::string parsedWidget = ExtractJsonUiField(payloadStr, "widgetName");
        if (parsedWidget.empty()) parsedWidget = ExtractJsonUiField(payloadStr, "widget");
        if (parsedWidget.empty()) parsedWidget = ExtractJsonUiField(payloadStr, "target");
        widgetName = parsedWidget;

        std::string rightClickStr = ExtractJsonUiField(payloadStr, "rightClick");
        std::string parsedEvent = ExtractJsonUiField(payloadStr, "eventType");
        if (parsedEvent.empty()) parsedEvent = ExtractJsonUiField(payloadStr, "event");
        if (!parsedEvent.empty()) {
            eventType = parsedEvent;
        } else if (_stricmp(rightClickStr.c_str(), "true") == 0 || rightClickStr == "1") {
            eventType = "onrightclick";
        }

        inlineOnClickLua = ExtractJsonUiField(payloadStr, "onclicklua");
        if (inlineOnClickLua.empty()) inlineOnClickLua = ExtractJsonUiField(payloadStr, "lua");
    }

    if (outIfaceName) *outIfaceName = ifaceName;
    if (outWidgetName) *outWidgetName = widgetName;
    if (outEventType) *outEventType = eventType;

    const std::string ifExpr = EncodeK2ConsoleSafeLuaStringExpr(ifaceName);
    const std::string wnExpr = EncodeK2ConsoleSafeLuaStringExpr(widgetName);
    const std::string evExpr = EncodeK2ConsoleSafeLuaStringExpr(eventType);
    const std::string oclExpr = EncodeK2ConsoleSafeLuaStringExpr(inlineOnClickLua);

    std::string cmd = "Script \"";
    cmd += "if UIManager then ";
    cmd += "local _ifn=" + ifExpr + " _wn=" + wnExpr + " _ev=" + evExpr + " _inl=" + oclExpr + " ";
    cmd += "local _cbk=(_ev=='onclicklua' and 'onclick') or (_ev=='onrightclicklua' and 'onrightclick') or _ev ";
    cmd += "local _if=UIManager.GetInterface and (UIManager.GetInterface(_ifn) or (UIManager.GetActiveInterface and UIManager.GetActiveInterface())) ";
    cmd += "if _if and #_wn>0 then ";
    cmd += "local function _fc(w,d) ";
    cmd += "if not w or d>3 then return nil,nil end ";
    cmd += "local c=w.GetCallback and w:GetCallback(_cbk) if type(c)=='function' then return c,w end ";
    cmd += "local ch=w.GetChildren and w:GetChildren() ";
    cmd += "if type(ch)=='table' then for _,k in ipairs(ch) do local f,t=_fc(k,d+1) if f then return f,t end end end end ";
    cmd += "local _b,_cb,_tw=nil,nil,nil ";
    cmd += "for _si=1,4 do local _sx=(_si==1 and '') or (_si==2 and '_button') or (_si==3 and '_btn') or '_catcher' ";
    cmd += "local _sw=_if:GetWidget(_wn.._sx) if _sw then _b=_b or _sw _cb,_tw=_fc(_sw,1) if _cb then break end end end ";
    cmd += "if _b then local _done=false ";
    cmd += "if type(_cb)=='function' then _done=pcall(_cb,_tw or _b) end ";
    cmd += "local _ocl=(#_inl>0 and _inl) or nil ";
    cmd += "if not _ocl then local _okO,_v=pcall(function() return (_tw or _b).onclicklua end) if _okO and _v then _ocl=_v end end ";
    cmd += "if _ocl then local _t=_tw or _b ";
    cmd += "if type(_ocl)=='function' then _done=pcall(_ocl,_t) or _done ";
    cmd += "elseif type(_ocl)=='string' and #_ocl>0 then ";
    cmd += "if _t.UICmd then _done=pcall(function() _t:UICmd(_ocl) end) or _done ";
    cmd += "else local _f=loadstring(_ocl) if _f then _done=pcall(_f) or _done end end end end ";
    cmd += "if _b.DoEvent then local _okD=pcall(function() _b:DoEvent() end) _done=_okD or _done end ";
    cmd += "if not _done and _b.DoEventN then pcall(function() _b:DoEventN(0) end) end ";
    cmd += "end end end\"";
    return cmd;
}

extern "C" __declspec(dllexport) int __cdecl Kongor_NativeGuard_DecodeEvalResArgs(
    const char* rawArgsUtf8,
    uint64_t* outEvalId,
    int* outOk,
    char* outBuf,
    uint32_t outBufCap) {
    if (!rawArgsUtf8 || !outEvalId || !outOk || !outBuf || outBufCap == 0) return 0;
    KONGOR_TRACY_ZONE_SCOPED_N("NativeGuard_DecodeEvalResArgs");
    std::wstring wArgs = Utf8ToWide(std::string(rawArgsUtf8));
    uint64_t evalId = 0;
    bool ok = false;
    std::string decoded;
    if (!ParseEvalResCommandArgsW(wArgs.c_str(), &evalId, &ok, &decoded)) {
        outBuf[0] = '\0';
        return 0;
    }
    *outEvalId = evalId;
    *outOk = ok ? 1 : 0;
    const size_t copyLen = (decoded.size() < static_cast<size_t>(outBufCap - 1))
        ? decoded.size()
        : static_cast<size_t>(outBufCap - 1);
    if (copyLen > 0) {
        std::memcpy(outBuf, decoded.data(), copyLen);
    }
    outBuf[copyLen] = '\0';
    return static_cast<int>(copyLen);
}

extern "C" __declspec(dllexport) int __cdecl Kongor_NativeGuard_BuildEvalRunnerCmdUtf8(
    uint64_t evalId,
    uint32_t nativeGamePhase,
    char* outBuf,
    uint32_t outBufCap) {
    if (!outBuf || outBufCap == 0) return 0;
    std::string cmd = BuildEvalRunnerCommandUtf8(evalId, nativeGamePhase);
    if (cmd.size() + 1 > static_cast<size_t>(outBufCap)) {
        outBuf[0] = '\0';
        return -1;
    }
    std::memcpy(outBuf, cmd.data(), cmd.size());
    outBuf[cmd.size()] = '\0';
    return static_cast<int>(cmd.size());
}

extern "C" __declspec(dllexport) int __cdecl Kongor_NativeGuard_BuildDispatchUiEventCmdUtf8(
    const char* reqPayloadUtf8,
    char* outCmdBuf,
    uint32_t outCmdCap,
    char* outWidgetBuf,
    uint32_t outWidgetCap) {
    if (!reqPayloadUtf8 || !outCmdBuf || outCmdCap == 0) return 0;
    std::string ifaceName;
    std::string widgetName;
    std::string eventType;
    std::string cmd = BuildDispatchUiEventCommandUtf8(std::string(reqPayloadUtf8), &ifaceName, &widgetName, &eventType);
    if (cmd.size() + 1 > static_cast<size_t>(outCmdCap)) {
        outCmdBuf[0] = '\0';
        return -1;
    }
    std::memcpy(outCmdBuf, cmd.data(), cmd.size());
    outCmdBuf[cmd.size()] = '\0';
    if (outWidgetBuf && outWidgetCap > 0) {
        const size_t wLen = (widgetName.size() < static_cast<size_t>(outWidgetCap - 1))
            ? widgetName.size()
            : static_cast<size_t>(outWidgetCap - 1);
        if (wLen > 0) std::memcpy(outWidgetBuf, widgetName.data(), wLen);
        outWidgetBuf[wLen] = '\0';
    }
    return static_cast<int>(cmd.size());
}

