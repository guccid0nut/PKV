// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

'use strict';

// ============================================================================
// Project Kongor: Frida High-Hz Main-Thread & Frame-Stutter Attribution Profiler
// Promoted from High-Hz investigation into scripts/qa/ for opt-in -Attribute
// runs and automatic failure attribution inside scripts/qa/kongor_shj.ps1.
// NOTE: NEVER attach during primary scored perf-canary windows (zero observer
//       bias policy). Attach ONLY on -Attribute or post-failure escalation.
// ============================================================================

const kernel32 = Process.getModuleByName('kernel32.dll');
const pQpc = kernel32.getExportByName('QueryPerformanceCounter');
const pQpf = kernel32.getExportByName('QueryPerformanceFrequency');
const pOpenThread = kernel32.getExportByName('OpenThread');
const pCloseHandle = kernel32.getExportByName('CloseHandle');
const pGetThreadTimes = kernel32.getExportByName('GetThreadTimes');
const pGetEnvA = kernel32.getExportByName('GetEnvironmentVariableA');
const pCreateFileW = kernel32.getExportByName('CreateFileW');
const pSleep = kernel32.getExportByName('Sleep');

const Qpc = new NativeFunction(pQpc, 'int', ['pointer']);
const Qpf = new NativeFunction(pQpf, 'int', ['pointer']);
const OpenThread = new NativeFunction(pOpenThread, 'pointer', ['uint32', 'int', 'uint32']);
const CloseHandle = new NativeFunction(pCloseHandle, 'int', ['pointer']);
const GetThreadTimes = new NativeFunction(pGetThreadTimes, 'int', ['pointer', 'pointer', 'pointer', 'pointer', 'pointer']);
const GetEnvA = new NativeFunction(pGetEnvA, 'uint32', ['pointer', 'pointer', 'uint32']);

const qpcBuf = Memory.alloc(8);
const rpcQpcBuf = Memory.alloc(8);
const qpfBuf = Memory.alloc(8);
Qpf(qpfBuf);
const QPF = qpfBuf.readS64().toNumber();
const TICKS_TO_MS = QPF > 0 ? (1000.0 / QPF) : 0.0001;

function nowTicks() {
    Qpc(qpcBuf);
    return qpcBuf.readS64().toNumber();
}

function nowTicksRpc() {
    Qpc(rpcQpcBuf);
    return rpcQpcBuf.readS64().toNumber();
}

function readEnv(name) {
    const nBuf = Memory.allocUtf8String(name);
    const vBuf = Memory.alloc(1024);
    const len = GetEnvA(nBuf, vBuf, 1024);
    if (len === 0 || len >= 1024) return null;
    return vBuf.readUtf8String(len);
}

const USER_PROFILE_DIR = (readEnv('USERPROFILE') || '').replace(/[\\/]+$/, '');
const USER_PROFILE_FWD = USER_PROFILE_DIR.replace(/\\/g, '/');

function sanitizePath(p) {
    if (!p || typeof p !== 'string' || !USER_PROFILE_DIR) return p;
    const escBack = USER_PROFILE_DIR.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    const escFwd = USER_PROFILE_FWD.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    return p
        .replace(new RegExp(escBack, 'gi'), '[USER_DIR]')
        .replace(new RegExp(escFwd, 'gi'), '[USER_DIR]');
}

function readThreadTimesMs(tid) {
    if (!tid) return { userMs: 0, kernelMs: 0 };
    const THREAD_QUERY_LIMITED_INFORMATION = 0x0800;
    const h = OpenThread(THREAD_QUERY_LIMITED_INFORMATION, 0, tid);
    if (h.isNull()) return { userMs: 0, kernelMs: 0 };
    const ftCreate = Memory.alloc(8);
    const ftExit = Memory.alloc(8);
    const ftKernel = Memory.alloc(8);
    const ftUser = Memory.alloc(8);
    let userMs = 0, kernelMs = 0;
    try {
        if (GetThreadTimes(h, ftCreate, ftExit, ftKernel, ftUser) !== 0) {
            kernelMs = ftKernel.readU64().toNumber() / 10000.0;
            userMs = ftUser.readU64().toNumber() / 10000.0;
        }
    } finally {
        CloseHandle(h);
    }
    return { userMs, kernelMs };
}

const MAX_SAMPLES = 30000;
const state = {
    mainTid: 0,
    windowStartTicks: nowTicks(),
    startThreadTimes: { userMs: 0, kernelMs: 0 },
    lastFrameEndTicks: 0,
    frameBeginTicks: 0,
    frameDeltas: new Float64Array(MAX_SAMPLES),
    frameCount: 0,
    frameOverflow: 0,
    sumFrameMs: 0,
    maxFrameMs: 0,
    gt8ms: 0,
    gt10ms: 0,
    gt16ms: 0,
    gt33ms: 0,
    gt50ms: 0,
    renderSamples: 0,
    sumRenderMs: 0,
    maxRenderMs: 0,
    presentSamples: 0,
    sumPresentMs: 0,
    maxPresentMs: 0,
    engineSamples: 0,
    sumEngineMs: 0,
    maxEngineMs: 0,
    uiSamples: 0,
    sumUiMs: 0,
    maxUiMs: 0,
    sceneSamples: 0,
    sumSceneMs: 0,
    maxSceneMs: 0,
    resRegCount: 0,
    resRegTotalMs: 0,
    resRegMaxMs: 0,
    resRegSlowEvents: [],
    createFileCount: 0,
    createFileTotalMs: 0,
    createFileMaxMs: 0,
    createFileSlowSamples: [],
    sleepCount: 0,
    sleepTotalMs: 0,
    sleepMaxMs: 0,
    hookedSymbols: {}
};

function resetWindow() {
    state.windowStartTicks = nowTicksRpc();
    state.startThreadTimes = readThreadTimesMs(state.mainTid);
    state.lastFrameEndTicks = 0;
    state.frameBeginTicks = 0;
    state.frameCount = 0;
    state.frameOverflow = 0;
    state.sumFrameMs = 0;
    state.maxFrameMs = 0;
    state.gt8ms = 0;
    state.gt10ms = 0;
    state.gt16ms = 0;
    state.gt33ms = 0;
    state.gt50ms = 0;
    state.renderSamples = 0;
    state.sumRenderMs = 0;
    state.maxRenderMs = 0;
    state.presentSamples = 0;
    state.sumPresentMs = 0;
    state.maxPresentMs = 0;
    state.engineSamples = 0;
    state.sumEngineMs = 0;
    state.maxEngineMs = 0;
    state.uiSamples = 0;
    state.sumUiMs = 0;
    state.maxUiMs = 0;
    state.sceneSamples = 0;
    state.sumSceneMs = 0;
    state.maxSceneMs = 0;
    state.resRegCount = 0;
    state.resRegTotalMs = 0;
    state.resRegMaxMs = 0;
    state.resRegSlowEvents = [];
    state.createFileCount = 0;
    state.createFileTotalMs = 0;
    state.createFileMaxMs = 0;
    state.createFileSlowSamples = [];
    state.sleepCount = 0;
    state.sleepTotalMs = 0;
    state.sleepMaxMs = 0;
    return { reset: true, mainTid: state.mainTid };
}

function getAuditInfo() {
    const mods = Process.enumerateModules();
    const findMod = (needle) => mods.find(m => m.name.toLowerCase() === needle.toLowerCase()) || null;
    const replayLayer = findMod('VkLayer_hon_replay_capture.dll');
    const vidVulkan = findMod('vid_vulkan_x64.dll');
    const d3d9 = findMod('d3d9.dll');
    const versionDll = findMod('version.dll');
    const k2 = findMod('k2_x64.dll');

    return {
        pid: Process.id,
        mainTid: state.mainTid,
        modules: {
            vkLayerReplayCaptureLoaded: replayLayer !== null,
            vkLayerPath: replayLayer ? sanitizePath(replayLayer.path) : null,
            vidVulkanLoaded: vidVulkan !== null,
            vidVulkanPath: vidVulkan ? sanitizePath(vidVulkan.path) : null,
            d3d9Loaded: d3d9 !== null,
            d3d9Size: d3d9 ? d3d9.size : 0,
            d3d9Path: d3d9 ? sanitizePath(d3d9.path) : null,
            versionDllPath: versionDll ? sanitizePath(versionDll.path) : null,
            k2Base: k2 ? k2.base.toString() : null
        },
        env: {
            DXVK_CONFIG_FILE: sanitizePath(readEnv('DXVK_CONFIG_FILE')),
            VK_INSTANCE_LAYERS: readEnv('VK_INSTANCE_LAYERS'),
            VK_LOADER_LAYERS_DISABLE: readEnv('VK_LOADER_LAYERS_DISABLE'),
            HON_MODERN_HUD: readEnv('HON_MODERN_HUD'),
            HON_CAPTURE_ENABLE: readEnv('HON_CAPTURE_ENABLE'),
            HON_CAPTURE_DISABLE: readEnv('HON_CAPTURE_DISABLE'),
            HON_FSR_ENABLE: readEnv('HON_FSR_ENABLE'),
            KONGOR_HEADLESS: readEnv('KONGOR_HEADLESS')
        },
        hookedSymbols: state.hookedSymbols
    };
}

function computeSnapshot() {
    const wallMs = (nowTicksRpc() - state.windowStartTicks) * TICKS_TO_MS;
    const endTimes = readThreadTimesMs(state.mainTid);
    const userDeltaMs = Math.max(0, endTimes.userMs - state.startThreadTimes.userMs);
    const kernelDeltaMs = Math.max(0, endTimes.kernelMs - state.startThreadTimes.kernelMs);
    const totalCpuMs = userDeltaMs + kernelDeltaMs;
    const cpuPct = wallMs > 1 ? (totalCpuMs / wallMs) * 100.0 : 0;

    const n = Math.min(state.frameCount, MAX_SAMPLES);
    let p50Ms = 0, p95Ms = 0, p99Ms = 0, p999Ms = 0, p1LowFps = 0, p01LowFps = 0, avgFrameMs = 0, avgFps = 0;

    if (n > 0) {
        const sorted = Array.from(state.frameDeltas.subarray(0, n)).sort((a, b) => a - b);
        const pct = (p) => sorted[Math.min(n - 1, Math.floor(p * n))];
        p50Ms = pct(0.50);
        p95Ms = pct(0.95);
        p99Ms = pct(0.99);
        p999Ms = pct(0.999);

        const worst1Count = Math.max(1, Math.ceil(n * 0.01));
        let sumWorst1 = 0;
        for (let i = n - worst1Count; i < n; i++) sumWorst1 += sorted[i];
        const avgWorst1Ms = sumWorst1 / worst1Count;
        p1LowFps = avgWorst1Ms > 0.001 ? 1000.0 / avgWorst1Ms : 0;

        const worst01Count = Math.max(1, Math.ceil(n * 0.001));
        let sumWorst01 = 0;
        for (let i = n - worst01Count; i < n; i++) sumWorst01 += sorted[i];
        const avgWorst01Ms = sumWorst01 / worst01Count;
        p01LowFps = avgWorst01Ms > 0.001 ? 1000.0 / avgWorst01Ms : 0;

        avgFrameMs = state.sumFrameMs / state.frameCount;
        avgFps = avgFrameMs > 0.001 ? 1000.0 / avgFrameMs : 0;
    }

    const r2 = (v) => Math.round(v * 100) / 100;
    return {
        wallMs: r2(wallMs),
        mainTid: state.mainTid,
        mainThreadCpu: {
            userMs: r2(userDeltaMs),
            kernelMs: r2(kernelDeltaMs),
            totalCpuMs: r2(totalCpuMs),
            cpuUtilizationPct: r2(cpuPct)
        },
        frames: {
            count: state.frameCount,
            overflow: state.frameOverflow,
            avgFps: r2(avgFps),
            medianFps: p50Ms > 0.001 ? r2(1000.0 / p50Ms) : 0,
            p1LowFps: r2(p1LowFps),
            p01LowFps: r2(p01LowFps),
            avgMs: r2(avgFrameMs),
            p50Ms: r2(p50Ms),
            p95Ms: r2(p95Ms),
            p99Ms: r2(p99Ms),
            p999Ms: r2(p999Ms),
            maxMs: r2(state.maxFrameMs),
            hitches: {
                gt8ms: state.gt8ms,
                gt10ms: state.gt10ms,
                gt16ms: state.gt16ms,
                gt33ms: state.gt33ms,
                gt50ms: state.gt50ms
            }
        },
        breakdownMs: {
            avgRenderBeginToEndMs: state.renderSamples > 0 ? r2(state.sumRenderMs / state.renderSamples) : 0,
            maxRenderBeginToEndMs: r2(state.maxRenderMs),
            avgEndFramePresentMs: state.presentSamples > 0 ? r2(state.sumPresentMs / state.presentSamples) : 0,
            maxEndFramePresentMs: r2(state.maxPresentMs),
            avgEngineBetweenFramesMs: state.engineSamples > 0 ? r2(state.sumEngineMs / state.engineSamples) : 0,
            maxEngineBetweenFramesMs: r2(state.maxEngineMs),
            avgUiFrameMs: state.uiSamples > 0 ? r2(state.sumUiMs / state.uiSamples) : 0,
            maxUiFrameMs: r2(state.maxUiMs),
            uiFrameSamples: state.uiSamples,
            avgSceneRenderMs: state.sceneSamples > 0 ? r2(state.sumSceneMs / state.sceneSamples) : 0,
            maxSceneRenderMs: r2(state.maxSceneMs),
            sceneRenderSamples: state.sceneSamples
        },
        mainThreadStalls: {
            resourceRegister: {
                count: state.resRegCount,
                totalMs: r2(state.resRegTotalMs),
                maxMs: r2(state.resRegMaxMs),
                slowEvents: state.resRegSlowEvents.slice(0, 10)
            },
            createFileW: {
                count: state.createFileCount,
                totalMs: r2(state.createFileTotalMs),
                maxMs: r2(state.createFileMaxMs),
                slowSamples: state.createFileSlowSamples.slice(0, 10)
            },
            sleep: {
                count: state.sleepCount,
                totalMs: r2(state.sleepTotalMs),
                maxMs: r2(state.sleepMaxMs)
            }
        },
        audit: getAuditInfo()
    };
}

const k2Mod = Process.getModuleByName('k2_x64.dll');
const k2Exports = k2Mod.enumerateExports();

function findExport(patterns) {
    for (const pat of patterns) {
        const hit = k2Exports.find(e => e.type === 'function' && pat.test(e.name));
        if (hit) return hit;
    }
    return null;
}

const expBeginFrame = findExport([/\?BeginFrame@CVid@@/, /BeginFrame@CVid/]);
const expEndFrame = findExport([/\?EndFrame@CVid@@/, /EndFrame@CVid/]);
const expHostExecute = findExport([/\?Execute@CHost@@/, /Execute@CHost/]);
const expUiFrame = findExport([/\?Frame@CUIManager@@/, /Frame@CUIManager/]);
const expResReg = findExport([/\?Register@CResourceManager@@QEAAIPEAVIResource@@I@Z/, /\?Register@CResourceManager@@/]);
const expSceneRender = findExport([/\?Render@CSceneManager@@/, /Render@CSceneManager/]);

function recordMainTid(tid) {
    if (state.mainTid === 0) {
        state.mainTid = tid;
        state.windowStartTicks = nowTicks();
        state.startThreadTimes = readThreadTimesMs(tid);
    }
}

if (expBeginFrame) {
    state.hookedSymbols.BeginFrame = expBeginFrame.name;
    Interceptor.attach(expBeginFrame.address, {
        onEnter() {
            recordMainTid(this.threadId);
            const t = nowTicks();
            state.frameBeginTicks = t;
            if (state.lastFrameEndTicks > 0) {
                const engMs = (t - state.lastFrameEndTicks) * TICKS_TO_MS;
                if (engMs >= 0 && engMs < 5000) {
                    state.engineSamples++;
                    state.sumEngineMs += engMs;
                    if (engMs > state.maxEngineMs) state.maxEngineMs = engMs;
                }
            }
        }
    });
}

if (expEndFrame) {
    state.hookedSymbols.EndFrame = expEndFrame.name;
    Interceptor.attach(expEndFrame.address, {
        onEnter() {
            recordMainTid(this.threadId);
            this._enterTicks = nowTicks();
        },
        onLeave() {
            const t = nowTicks();
            const presentMs = (t - this._enterTicks) * TICKS_TO_MS;
            if (presentMs >= 0 && presentMs < 5000) {
                state.presentSamples++;
                state.sumPresentMs += presentMs;
                if (presentMs > state.maxPresentMs) state.maxPresentMs = presentMs;
            }
            if (state.frameBeginTicks > 0) {
                const renderMs = (t - state.frameBeginTicks) * TICKS_TO_MS;
                if (renderMs >= 0 && renderMs < 5000) {
                    state.renderSamples++;
                    state.sumRenderMs += renderMs;
                    if (renderMs > state.maxRenderMs) state.maxRenderMs = renderMs;
                }
            }
            if (state.lastFrameEndTicks > 0) {
                const deltaMs = (t - state.lastFrameEndTicks) * TICKS_TO_MS;
                if (deltaMs > 0.05 && deltaMs < 5000) {
                    if (state.frameCount < MAX_SAMPLES) {
                        state.frameDeltas[state.frameCount] = deltaMs;
                    } else {
                        state.frameOverflow++;
                    }
                    state.frameCount++;
                    state.sumFrameMs += deltaMs;
                    if (deltaMs > state.maxFrameMs) state.maxFrameMs = deltaMs;
                    if (deltaMs > 8.0) state.gt8ms++;
                    if (deltaMs > 10.0) state.gt10ms++;
                    if (deltaMs > 16.67) state.gt16ms++;
                    if (deltaMs > 33.33) state.gt33ms++;
                    if (deltaMs > 50.0) state.gt50ms++;
                }
            }
            state.lastFrameEndTicks = t;
        }
    });
} else if (expHostExecute) {
    state.hookedSymbols.HostExecute = expHostExecute.name;
    Interceptor.attach(expHostExecute.address, {
        onEnter() {
            recordMainTid(this.threadId);
            const t = nowTicks();
            if (state.lastFrameEndTicks > 0) {
                const deltaMs = (t - state.lastFrameEndTicks) * TICKS_TO_MS;
                if (deltaMs > 0.05 && deltaMs < 5000) {
                    if (state.frameCount < MAX_SAMPLES) {
                        state.frameDeltas[state.frameCount] = deltaMs;
                    } else {
                        state.frameOverflow++;
                    }
                    state.frameCount++;
                    state.sumFrameMs += deltaMs;
                    if (deltaMs > state.maxFrameMs) state.maxFrameMs = deltaMs;
                    if (deltaMs > 8.0) state.gt8ms++;
                    if (deltaMs > 10.0) state.gt10ms++;
                    if (deltaMs > 16.67) state.gt16ms++;
                    if (deltaMs > 33.33) state.gt33ms++;
                    if (deltaMs > 50.0) state.gt50ms++;
                }
            }
            state.lastFrameEndTicks = t;
        }
    });
}

if (expUiFrame) {
    state.hookedSymbols.UiFrame = expUiFrame.name;
    Interceptor.attach(expUiFrame.address, {
        onEnter() {
            if (this.threadId !== state.mainTid && state.mainTid !== 0) return;
            this._t0 = nowTicks();
        },
        onLeave() {
            if (!this._t0) return;
            const ms = (nowTicks() - this._t0) * TICKS_TO_MS;
            if (ms >= 0 && ms < 5000) {
                state.uiSamples++;
                state.sumUiMs += ms;
                if (ms > state.maxUiMs) state.maxUiMs = ms;
            }
        }
    });
}

if (expSceneRender) {
    state.hookedSymbols.SceneRender = expSceneRender.name;
    Interceptor.attach(expSceneRender.address, {
        onEnter() {
            if (this.threadId !== state.mainTid && state.mainTid !== 0) return;
            this._t0 = nowTicks();
        },
        onLeave() {
            if (!this._t0) return;
            const ms = (nowTicks() - this._t0) * TICKS_TO_MS;
            if (ms >= 0 && ms < 5000) {
                state.sceneSamples++;
                state.sumSceneMs += ms;
                if (ms > state.maxSceneMs) state.maxSceneMs = ms;
            }
        }
    });
}

if (expResReg) {
    state.hookedSymbols.ResourceRegister = expResReg.name;
    Interceptor.attach(expResReg.address, {
        onEnter() {
            if (state.mainTid === 0 || this.threadId !== state.mainTid) return;
            this._t0 = nowTicks();
        },
        onLeave() {
            if (!this._t0) return;
            const ms = (nowTicks() - this._t0) * TICKS_TO_MS;
            state.resRegCount++;
            state.resRegTotalMs += ms;
            if (ms > state.resRegMaxMs) state.resRegMaxMs = ms;
            if (ms >= 2.0 && state.resRegSlowEvents.length < 10) {
                state.resRegSlowEvents.push({ ms: Math.round(ms * 100) / 100, frame: state.frameCount });
            }
        }
    });
}

Interceptor.attach(pCreateFileW, {
    onEnter(args) {
        if (state.mainTid === 0 || this.threadId !== state.mainTid) return;
        this._t0 = nowTicks();
        this._pathPtr = args[0];
    },
    onLeave() {
        if (!this._t0) return;
        const ms = (nowTicks() - this._t0) * TICKS_TO_MS;
        state.createFileCount++;
        state.createFileTotalMs += ms;
        if (ms > state.createFileMaxMs) state.createFileMaxMs = ms;
        if (ms >= 1.5 && state.createFileSlowSamples.length < 10) {
            let p = '';
            try { p = this._pathPtr.readUtf16String(); } catch (_) {}
            const safePath = sanitizePath(p) || '';
            state.createFileSlowSamples.push({ ms: Math.round(ms * 100) / 100, path: safePath.slice(-96) });
        }
    }
});

Interceptor.attach(pSleep, {
    onEnter(args) {
        if (state.mainTid === 0 || this.threadId !== state.mainTid) return;
        this._reqMs = args[0].toUInt32();
        this._t0 = nowTicks();
    },
    onLeave() {
        if (!this._t0) return;
        const ms = (nowTicks() - this._t0) * TICKS_TO_MS;
        state.sleepCount++;
        state.sleepTotalMs += ms;
        if (ms > state.sleepMaxMs) state.sleepMaxMs = ms;
    }
});

rpc.exports = {
    getAudit() {
        return getAuditInfo();
    },
    resetWindow() {
        return resetWindow();
    },
    getSnapshot() {
        return computeSnapshot();
    },
    detach() {
        Interceptor.detachAll();
        return { detached: true };
    }
};

send({ type: 'profiler_ready', audit: getAuditInfo() });
