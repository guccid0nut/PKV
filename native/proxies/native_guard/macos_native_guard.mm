// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors

// ============================================================================
// File: macos_native_guard.mm
// System: Heroes of Newerth - macOS Native Client Monitor & Frame Pacing Guard
// Target: libkongor_native_guard.dylib (macOS x86_64 / Universal)
// ============================================================================

#if defined(__APPLE__)

#import <Cocoa/Cocoa.h>
#import <CoreGraphics/CoreGraphics.h>
#import <CoreFoundation/CoreFoundation.h>
#import <OpenGL/OpenGL.h>

#include <dlfcn.h>
#include <mach/mach_time.h>
#include <pthread.h>
#include <time.h>
#include <unistd.h>

#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cwchar>

extern "C" bool KongorMac_ApplyMonitorIndex(int monitorIndex, int explicitHz);

namespace {

struct MacMonitorEntry {
    int index;
    uint32_t displayId;
    char token[32];
    char friendlyName[96];
    bool isPrimary;
    int boundsX;
    int boundsY;
    int nativeWidth;
    int nativeHeight;
    int maxHz;
    char modesSpec[512];
};

static constexpr int kMaxMacMonitors = 16;
static pthread_mutex_t g_macMonMutex = PTHREAD_MUTEX_INITIALIZER;
static MacMonitorEntry g_macMonitors[kMaxMacMonitors] = {};
static int g_macMonitorCount = 0;

static std::atomic<int> g_macTargetMaxFps{ 0 };
static std::atomic<int> g_macManualFpsOverride{ 0 };
static std::atomic<uint64_t> g_macSuppressScreenMoveUntilNs{ 0 };
static std::atomic<int> g_macActiveMonitorIdx{ 1 };

struct LibcxxLongWString {
    uint64_t capWithLongBit;
    uint64_t size;
    const wchar_t* data;
};

static uint64_t NowMonotonicNs() {
    struct timespec ts{};
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return static_cast<uint64_t>(ts.tv_sec) * 1000000000ULL + static_cast<uint64_t>(ts.tv_nsec);
}

static void SanitizeMacMonitorName(const char* raw, char* outBuf, size_t outCap, const char* fallback) {
    if (!outBuf || outCap == 0) return;
    outBuf[0] = '\0';
    const char* src = (raw && raw[0] != '\0') ? raw : fallback;
    if (!src) return;

    size_t pos = 0;
    for (size_t i = 0; src[i] != '\0' && pos + 1 < outCap; ++i) {
        unsigned char c = static_cast<unsigned char>(src[i]);
        if (c == '|' || c == '/' || c == ';' || c == '"' || c == '\'' || c == '\\' || c == '\r' || c == '\n') {
            outBuf[pos++] = ' ';
        } else if (c >= 32 && c <= 126) {
            outBuf[pos++] = static_cast<char>(c);
        }
    }
    while (pos > 0 && outBuf[pos - 1] == ' ') --pos;
    outBuf[pos] = '\0';
    if (pos == 0 && fallback) {
        std::snprintf(outBuf, outCap, "%s", fallback);
    }
}

static size_t Utf8ToWcharMac(const char* str, wchar_t* outBuf, size_t outCap) {
    if (!str || !outBuf || outCap == 0) return 0;
    size_t outLen = 0;
    for (size_t i = 0; str[i] != '\0' && outLen + 1 < outCap; ++i) {
        unsigned char c = static_cast<unsigned char>(str[i]);
        outBuf[outLen++] = (c >= 32 && c <= 126) ? static_cast<wchar_t>(c) : L' ';
    }
    outBuf[outLen] = L'\0';
    return outLen;
}

static void ExecuteK2ConsoleCommandUtf8(const char* cmdUtf8) {
    if (!cmdUtf8 || cmdUtf8[0] == '\0') return;
    using FnExecute = void (*)(void*, const void*, unsigned int);

    static void** s_ppConsole = reinterpret_cast<void**>(
        dlsym(RTLD_DEFAULT, "_ZN8CConsole11s_pInstanceE"));
    static FnExecute s_execute = reinterpret_cast<FnExecute>(
        dlsym(RTLD_DEFAULT, "_ZN8CConsole7ExecuteERKNSt3__112basic_stringIwNS0_11char_traitsIwEE17K2StringAllocatorIwEEEj"));

    if (!s_ppConsole || !*s_ppConsole || !s_execute) return;
    void* consolePtr = *s_ppConsole;

    wchar_t wbuf[2048] = {};
    size_t wlen = Utf8ToWcharMac(cmdUtf8, wbuf, 2048);
    LibcxxLongWString fake{};
    fake.capWithLongBit = ((wlen + 1) << 1) | 1ULL;
    fake.size = wlen;
    fake.data = wbuf;
    s_execute(consolePtr, &fake, 0);
}

static int ParseMacDisplayIndexToken(const char* raw) {
    if (!raw || raw[0] == '\0') return 0;
    while (*raw == ' ' || *raw == '\t' || *raw == '\\' || *raw == '/' || *raw == '.' || *raw == '"') ++raw;
    if (strncasecmp(raw, "DISPLAY", 7) == 0) {
        return std::atoi(raw + 7);
    }
    return std::atoi(raw);
}

static void PollMacVidDisplayCvarChange() {
    using FnGetModCount = uint32_t (*)(const void*);
    using FnGetInt = int (*)(const void*);
    using FnGetStringSret = void (*)(void*, const void*);

    static FnGetModCount s_getModCount = reinterpret_cast<FnGetModCount>(
        dlsym(RTLD_DEFAULT, "_ZN5ICvar16GetModifiedCountERKNSt3__112basic_stringIwNS0_11char_traitsIwEE17K2StringAllocatorIwEEE"));
    static FnGetStringSret s_getString = reinterpret_cast<FnGetStringSret>(
        dlsym(RTLD_DEFAULT, "_ZN5ICvar9GetStringERKNSt3__112basic_stringIwNS0_11char_traitsIwEE17K2StringAllocatorIwEEE"));
    static FnGetInt s_getInt = reinterpret_cast<FnGetInt>(
        dlsym(RTLD_DEFAULT, "_ZN5ICvar10GetIntegerERKNSt3__112basic_stringIwNS0_11char_traitsIwEE17K2StringAllocatorIwEEE"));

    if (!s_getModCount || !s_getString) return;

    static const wchar_t kVidDisplayW[] = L"vid_display";
    static const LibcxxLongWString kVidDisplayRef = { ((11ULL + 1ULL) << 1) | 1ULL, 11ULL, kVidDisplayW };
    static const wchar_t kVidRefreshW[] = L"vid_refreshRate";
    static const LibcxxLongWString kVidRefreshRef = { ((15ULL + 1ULL) << 1) | 1ULL, 15ULL, kVidRefreshW };

    static uint32_t s_lastModCount = 0xFFFFFFFFu;
    uint32_t curMod = s_getModCount(&kVidDisplayRef);
    if (s_lastModCount == 0xFFFFFFFFu) {
        s_lastModCount = curMod;
        return;
    }
    if (curMod == s_lastModCount) return;
    s_lastModCount = curMod;

    alignas(16) uint8_t sretBuf[32] = {};
    s_getString(sretBuf, &kVidDisplayRef);

    const wchar_t* wptr = nullptr;
    size_t wlen = 0;
    if ((sretBuf[0] & 1u) != 0) {
        wlen = *reinterpret_cast<const uint64_t*>(sretBuf + 8);
        wptr = *reinterpret_cast<const wchar_t* const*>(sretBuf + 16);
    } else {
        wlen = static_cast<size_t>(sretBuf[0] >> 1);
        wptr = reinterpret_cast<const wchar_t*>(sretBuf + 4);
    }
    if (!wptr || wlen == 0 || wlen > 64) return;

    char asciiTok[68] = {};
    for (size_t i = 0; i < wlen && i < 64; ++i) {
        asciiTok[i] = static_cast<char>(wptr[i] & 0x7F);
    }
    int idx = ParseMacDisplayIndexToken(asciiTok);
    if (idx > 0 && idx != g_macActiveMonitorIdx.load(std::memory_order_relaxed)) {
        int hz = s_getInt ? s_getInt(&kVidRefreshRef) : 0;
        KongorMac_ApplyMonitorIndex(idx, hz);
        s_lastModCount = s_getModCount(&kVidDisplayRef);
    }
}

static int RefreshMacMonitorsLocked() {
    CGDirectDisplayID ids[kMaxMacMonitors] = {};
    uint32_t count = 0;
    if (CGGetOnlineDisplayList(kMaxMacMonitors, ids, &count) != kCGErrorSuccess || count == 0) {
        g_macMonitorCount = 0;
        return 0;
    }

    struct RawMacDisp {
        uint32_t id;
        bool isMain;
        int x;
        int y;
        int w;
        int h;
        int maxHz;
        char name[96];
        char modesSpec[512];
    };
    RawMacDisp raw[kMaxMacMonitors] = {};
    int rawCount = 0;

    @autoreleasepool {
        NSArray<NSScreen*>* screens = [NSScreen screens];
        for (uint32_t i = 0; i < count && rawCount < kMaxMacMonitors; ++i) {
            CGDirectDisplayID did = ids[i];
            bool isMain = CGDisplayIsMain(did) != 0;
            CGRect bounds = CGDisplayBounds(did);
            int nw = bounds.size.width > 0 ? static_cast<int>(std::lround(bounds.size.width)) : 1920;
            int nh = bounds.size.height > 0 ? static_cast<int>(std::lround(bounds.size.height)) : 1080;
            int curHz = 60;

            CGDisplayModeRef curMode = CGDisplayCopyDisplayMode(did);
            if (curMode != nullptr) {
                int pw = static_cast<int>(CGDisplayModeGetPixelWidth(curMode));
                int ph = static_cast<int>(CGDisplayModeGetPixelHeight(curMode));
                if (pw >= 640 && ph >= 480) {
                    nw = pw;
                    nh = ph;
                }
                double r = CGDisplayModeGetRefreshRate(curMode);
                if (r > 1.0) curHz = static_cast<int>(std::lround(r));
                CFRelease(curMode);
            }

            int maxHz = curHz;
            struct ModeBucket {
                int w;
                int h;
                int rates[16];
                int rateCount;
            };
            ModeBucket buckets[64] = {};
            int bucketCount = 0;

            auto addModeRate = [&](int mw, int mh, int hz) {
                if (mw < 640 || mh < 480) return;
                if (hz <= 1) hz = 60;
                int bIdx = -1;
                for (int b = 0; b < bucketCount; ++b) {
                    if (buckets[b].w == mw && buckets[b].h == mh) {
                        bIdx = b;
                        break;
                    }
                }
                if (bIdx < 0) {
                    if (bucketCount >= 64) return;
                    bIdx = bucketCount++;
                    buckets[bIdx].w = mw;
                    buckets[bIdx].h = mh;
                    buckets[bIdx].rateCount = 0;
                }
                for (int r = 0; r < buckets[bIdx].rateCount; ++r) {
                    if (buckets[bIdx].rates[r] == hz) return;
                }
                if (buckets[bIdx].rateCount < 16) {
                    buckets[bIdx].rates[buckets[bIdx].rateCount++] = hz;
                } else {
                    int minIdx = 0;
                    for (int r = 1; r < 16; ++r) {
                        if (buckets[bIdx].rates[r] < buckets[bIdx].rates[minIdx]) minIdx = r;
                    }
                    if (hz > buckets[bIdx].rates[minIdx]) {
                        buckets[bIdx].rates[minIdx] = hz;
                    }
                }
            };

            addModeRate(nw, nh, curHz);
            CFArrayRef modes = CGDisplayCopyAllDisplayModes(did, nullptr);
            if (modes != nullptr) {
                CFIndex mCount = CFArrayGetCount(modes);
                for (CFIndex m = 0; m < mCount; ++m) {
                    CGDisplayModeRef mRef = (CGDisplayModeRef)CFArrayGetValueAtIndex(modes, m);
                    if (!mRef) continue;
                    int mw = static_cast<int>(CGDisplayModeGetPixelWidth(mRef));
                    int mh = static_cast<int>(CGDisplayModeGetPixelHeight(mRef));
                    double mr = CGDisplayModeGetRefreshRate(mRef);
                    int mhz = mr > 1.0 ? static_cast<int>(std::lround(mr)) : 60;
                    addModeRate(mw, mh, mhz);
                    if (mw == nw && mh == nh && mhz > maxHz) {
                        maxHz = mhz;
                    }
                }
                CFRelease(modes);
            }

            // Sort non-native buckets by pixel area descending, and sort rates descending inside each bucket
            for (int a = 1; a < bucketCount; ++a) {
                for (int c = a + 1; c < bucketCount; ++c) {
                    long long areaA = static_cast<long long>(buckets[a].w) * static_cast<long long>(buckets[a].h);
                    long long areaC = static_cast<long long>(buckets[c].w) * static_cast<long long>(buckets[c].h);
                    if (areaC > areaA || (areaC == areaA && buckets[c].w > buckets[a].w)) {
                        ModeBucket tmp = buckets[a];
                        buckets[a] = buckets[c];
                        buckets[c] = tmp;
                    }
                }
            }
            for (int b = 0; b < bucketCount; ++b) {
                for (int a = 0; a < buckets[b].rateCount; ++a) {
                    for (int c = a + 1; c < buckets[b].rateCount; ++c) {
                        if (buckets[b].rates[c] > buckets[b].rates[a]) {
                            int tmp = buckets[b].rates[a];
                            buckets[b].rates[a] = buckets[b].rates[c];
                            buckets[b].rates[c] = tmp;
                        }
                    }
                }
            }

            char fallbackName[64];
            std::snprintf(fallbackName, sizeof(fallbackName), isMain ? "Main Display" : "Display %u", rawCount + 1);
            char cleanName[96];
            SanitizeMacMonitorName(nullptr, cleanName, sizeof(cleanName), fallbackName);

            for (NSScreen* scr in screens) {
                NSDictionary* desc = [scr deviceDescription];
                NSNumber* num = [desc objectForKey:@"NSScreenNumber"];
                if (num != nil && [num unsignedIntValue] == did) {
                    if ([scr respondsToSelector:@selector(localizedName)]) {
                        NSString* loc = [scr localizedName];
                        if (loc != nil && [loc length] > 0) {
                            SanitizeMacMonitorName([loc UTF8String], cleanName, sizeof(cleanName), fallbackName);
                        }
                    }
                    break;
                }
            }

            char modesBuf[220] = {};
            size_t mPos = 0;
            const int maxModesOut = bucketCount < 8 ? bucketCount : 8;
            for (int b = 0; b < maxModesOut && mPos + 24 < sizeof(modesBuf); ++b) {
                if (b > 0) modesBuf[mPos++] = '+';
                mPos += static_cast<size_t>(std::snprintf(modesBuf + mPos, sizeof(modesBuf) - mPos, "%dx%d@", buckets[b].w, buckets[b].h));
                const int maxRatesOut = buckets[b].rateCount < 6 ? buckets[b].rateCount : 6;
                for (int r = 0; r < maxRatesOut && mPos + 8 < sizeof(modesBuf); ++r) {
                    if (r > 0) modesBuf[mPos++] = ',';
                    mPos += static_cast<size_t>(std::snprintf(modesBuf + mPos, sizeof(modesBuf) - mPos, "%d", buckets[b].rates[r]));
                }
            }

            RawMacDisp& dst = raw[rawCount++];
            dst.id = did;
            dst.isMain = isMain;
            dst.x = static_cast<int>(std::lround(bounds.origin.x));
            dst.y = static_cast<int>(std::lround(bounds.origin.y));
            dst.w = nw;
            dst.h = nh;
            dst.maxHz = maxHz > 0 ? maxHz : 60;
            std::snprintf(dst.name, sizeof(dst.name), "%s", cleanName);
            std::snprintf(dst.modesSpec, sizeof(dst.modesSpec), "%s", modesBuf);
        }
    }

    // Order primary first, then X ascending, then Y ascending
    for (int i = 0; i < rawCount; ++i) {
        for (int j = i + 1; j < rawCount; ++j) {
            bool swapNeeded = false;
            if (!raw[i].isMain && raw[j].isMain) swapNeeded = true;
            else if (raw[i].isMain == raw[j].isMain) {
                if (raw[j].x < raw[i].x || (raw[j].x == raw[i].x && raw[j].y < raw[i].y)) {
                    swapNeeded = true;
                }
            }
            if (swapNeeded) {
                RawMacDisp tmp = raw[i];
                raw[i] = raw[j];
                raw[j] = tmp;
            }
        }
    }

    g_macMonitorCount = rawCount;
    for (int i = 0; i < rawCount; ++i) {
        MacMonitorEntry& e = g_macMonitors[i];
        e.index = i + 1;
        e.displayId = raw[i].id;
        std::snprintf(e.token, sizeof(e.token), "DISPLAY%d", i + 1);
        std::snprintf(e.friendlyName, sizeof(e.friendlyName), "%s", raw[i].name);
        e.isPrimary = raw[i].isMain;
        e.boundsX = raw[i].x;
        e.boundsY = raw[i].y;
        e.nativeWidth = raw[i].w;
        e.nativeHeight = raw[i].h;
        e.maxHz = raw[i].maxHz;
        std::snprintf(e.modesSpec, sizeof(e.modesSpec), "%s", raw[i].modesSpec);
    }

    return g_macMonitorCount;
}

} // namespace

extern "C" {

__attribute__((visibility("default")))
int KongorMac_FormatDetectedMonitors(char* outBuf, size_t outCap) {
    if (!outBuf || outCap == 0) return 0;
    outBuf[0] = '\0';

    pthread_mutex_lock(&g_macMonMutex);
    int count = RefreshMacMonitorsLocked();
    size_t pos = 0;
    for (int i = 0; i < count && pos + 64 < outCap; ++i) {
        const MacMonitorEntry& m = g_macMonitors[i];
        if (i > 0) outBuf[pos++] = '/';
        pos += static_cast<size_t>(std::snprintf(
            outBuf + pos,
            outCap - pos,
            "%s|%d|%s|%d|%d,%d|%d|%s",
            m.token,
            m.index,
            m.friendlyName,
            m.isPrimary ? 1 : 0,
            m.nativeWidth,
            m.nativeHeight,
            m.maxHz,
            m.modesSpec));
    }
    pthread_mutex_unlock(&g_macMonMutex);
    return count;
}

__attribute__((visibility("default")))
bool KongorMac_ApplyMonitorIndex(int monitorIndex, int explicitHz) {
    pthread_mutex_lock(&g_macMonMutex);
    if (g_macMonitorCount == 0) {
        RefreshMacMonitorsLocked();
    }
    if (monitorIndex < 1 || monitorIndex > g_macMonitorCount) {
        pthread_mutex_unlock(&g_macMonMutex);
        return false;
    }
    MacMonitorEntry target = g_macMonitors[monitorIndex - 1];
    pthread_mutex_unlock(&g_macMonMutex);

    int manualOvr = g_macManualFpsOverride.load(std::memory_order_relaxed);
    int effectiveFps = manualOvr > 0 ? manualOvr : (explicitHz > 0 ? explicitHz : target.maxHz);
    g_macTargetMaxFps.store(effectiveFps, std::memory_order_release);
    g_macActiveMonitorIdx.store(target.index, std::memory_order_release);
    g_macSuppressScreenMoveUntilNs.store(NowMonotonicNs() + 2000000000ULL, std::memory_order_release);

    dispatch_async(dispatch_get_main_queue(), ^{
        @autoreleasepool {
            NSWindow* win = [NSApp mainWindow];
            if (win == nil && [[NSApp windows] count] > 0) {
                win = [[NSApp windows] objectAtIndex:0];
            }
            if (win != nil) {
                for (NSScreen* scr in [NSScreen screens]) {
                    NSNumber* num = [[scr deviceDescription] objectForKey:@"NSScreenNumber"];
                    if (num != nil && [num unsignedIntValue] == target.displayId) {
                        [win setFrame:[scr frame] display:YES animate:NO];
                        break;
                    }
                }
            }
        }
    });

    char cmd[1024];
    std::snprintf(
        cmd,
        sizeof(cmd),
        "SetSave vid_display %s; SetSave options_display %s; SetSave vid_resolution %d,%d; SetSave options_resolution %d,%d; SetSave vid_refreshRate %d; SetSave options_refreshrate %d; SetSave host_maxFPS %d; SetSave host_maximumFPS %d; SetSave cl_maxFPS %d; SetSave vid_maxFPS %d",
        target.token,
        target.token,
        target.nativeWidth,
        target.nativeHeight,
        target.nativeWidth,
        target.nativeHeight,
        target.maxHz,
        target.maxHz,
        effectiveFps,
        effectiveFps,
        effectiveFps,
        effectiveFps);
    ExecuteK2ConsoleCommandUtf8(cmd);

    std::fprintf(
        stdout,
        "[NativeGuard:macOS] Applied monitor #%d (%s '%s' %dx%d @ %d Hz, effectiveMaxFPS=%d)\n",
        target.index,
        target.token,
        target.friendlyName,
        target.nativeWidth,
        target.nativeHeight,
        target.maxHz,
        effectiveFps);
    std::fflush(stdout);
    return true;
}

static CGLError Hook_CGLFlushDrawable(CGLContextObj ctx) {
    static std::atomic<bool> s_syncedOnce{ false };
    static uint64_t s_nextFrameDeadlineNs = 0;

    CGLError err = CGLFlushDrawable(ctx);

    if (!s_syncedOnce.exchange(true, std::memory_order_acq_rel)) {
        char payload[4096] = {};
        int count = KongorMac_FormatDetectedMonitors(payload, sizeof(payload));
        if (count > 0) {
            payload[940] = '\0';
            ExecuteK2ConsoleCommandUtf8("CreateString kongor_detected_monitors \"\"");
            char cmd[1020];
            std::snprintf(
                cmd,
                sizeof(cmd),
                "SetSave kongor_detected_monitors \"%s\"",
                payload);
            ExecuteK2ConsoleCommandUtf8(cmd);
            std::fprintf(stdout, "[NativeGuard:macOS] Synced %d monitor(s) -> %s\n", count, payload);
            std::fflush(stdout);

            const char* targetEnv = std::getenv("KONGOR_TARGET_MONITOR");
            int startIdx = ParseMacDisplayIndexToken(targetEnv);
            if (startIdx < 1 || startIdx > count) startIdx = 1;
            KongorMac_ApplyMonitorIndex(startIdx, 0);
        }
    }

    static uint64_t s_lastCvarPollNs = 0;
    const uint64_t pollNowNs = NowMonotonicNs();
    if (pollNowNs - s_lastCvarPollNs >= 250000000ULL) {
        s_lastCvarPollNs = pollNowNs;
        PollMacVidDisplayCvarChange();
    }

    int targetFps = g_macTargetMaxFps.load(std::memory_order_relaxed);
    if (targetFps >= 24 && targetFps < 1000) {
        const uint64_t intervalNs = 1000000000ULL / static_cast<uint64_t>(targetFps);
        uint64_t now = NowMonotonicNs();
        if (s_nextFrameDeadlineNs == 0 || now > s_nextFrameDeadlineNs + intervalNs * 4) {
            s_nextFrameDeadlineNs = now + intervalNs;
        } else {
            if (now < s_nextFrameDeadlineNs) {
                uint64_t rem = s_nextFrameDeadlineNs - now;
                if (rem > 500000ULL) {
                    struct timespec req{};
                    uint64_t sleepNs = rem - 400000ULL;
                    req.tv_sec = static_cast<time_t>(sleepNs / 1000000000ULL);
                    req.tv_nsec = static_cast<long>(sleepNs % 1000000000ULL);
                    nanosleep(&req, nullptr);
                }
                while (NowMonotonicNs() < s_nextFrameDeadlineNs) {
                    // Sub-0.4ms precision spin tail
                }
            }
            s_nextFrameDeadlineNs += intervalNs;
        }
    }

    return err;
}

struct DyldInterposeTuple {
    const void* replacement;
    const void* replacee;
};

__attribute__((used, section("__DATA,__interpose")))
static const DyldInterposeTuple kMacInterposers[] = {
    { reinterpret_cast<const void*>(&Hook_CGLFlushDrawable), reinterpret_cast<const void*>(&CGLFlushDrawable) }
};

__attribute__((constructor))
static void KongorMacNativeGuardInit() {
    const char* manEnv = std::getenv("KONGOR_MANUAL_FPS_OVERRIDE");
    if (manEnv && manEnv[0] != '\0') {
        int val = std::atoi(manEnv);
        if (val > 0) {
            g_macManualFpsOverride.store(val, std::memory_order_release);
            g_macTargetMaxFps.store(val, std::memory_order_release);
        }
    }

    char payload[4096] = {};
    int count = KongorMac_FormatDetectedMonitors(payload, sizeof(payload));
    std::fprintf(stdout, "[NativeGuard:macOS] Initialized CoreGraphics multi-monitor guard (%d online display(s))\n", count);
    std::fflush(stdout);

    // Parity note (artifacts/evidence/midwars_10v10_kill_packet_truncation.json, OBL-MIDWARS-03):
    // macOS x86_64/Universal client builds share K2's CBufferFixed<28> 0x60 (GAME_CMD_KILL_MESSAGE) wire format.
    // While Windows (version.dll) and Linux (libk2_native_guard.so) install runtime detours on CClientGame::ProcessGameData
    // and CGameServer::BroadcastGameData / KillNotifyHelper, macOS client parity requires an analogous cgame_osx read-bounds
    // guard checking CPacket unread bytes before looping numAssists ReadInt(-1) calls on legacy 28-byte kill5 replays.
    // Wait-free framing helper parity reference: clamp byte[11] = (unreadBytes - 12) / 4 whenever byte[0] == 0x60 && unreadBytes < 12 + 4 * byte[11].

    // Observe live macOS window moves across displays (Mission Control / drag across screens)
    dispatch_async(dispatch_get_main_queue(), ^{
        [[NSNotificationCenter defaultCenter]
            addObserverForName:NSWindowDidChangeScreenNotification
                        object:nil
                         queue:[NSOperationQueue mainQueue]
                    usingBlock:^(NSNotification* note) {
                        if (NowMonotonicNs() < g_macSuppressScreenMoveUntilNs.load(std::memory_order_acquire)) {
                            return;
                        }
                        NSWindow* w = (NSWindow*)[note object];
                        NSScreen* scr = w != nil ? [w screen] : nil;
                        if (scr == nil) return;
                        NSNumber* num = [[scr deviceDescription] objectForKey:@"NSScreenNumber"];
                        if (num == nil) return;
                        uint32_t did = [num unsignedIntValue];

                        pthread_mutex_lock(&g_macMonMutex);
                        RefreshMacMonitorsLocked();
                        int matchedIdx = -1;
                        for (int i = 0; i < g_macMonitorCount; ++i) {
                            if (g_macMonitors[i].displayId == did) {
                                matchedIdx = g_macMonitors[i].index;
                                break;
                            }
                        }
                        pthread_mutex_unlock(&g_macMonMutex);

                        if (matchedIdx > 0 && matchedIdx != g_macActiveMonitorIdx.load(std::memory_order_relaxed)) {
                            KongorMac_ApplyMonitorIndex(matchedIdx, 0);
                        }
                    }];
    });
}

} // extern "C"

#endif // __APPLE__
