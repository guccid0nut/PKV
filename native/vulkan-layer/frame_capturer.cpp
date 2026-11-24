// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#include "frame_capturer.h"

// ============================================================================
// (#203) Headless RenderDoc Dynamic Capture Bridge Implementation
// ============================================================================
std::string HeadlessRenderDocCaptureController::DiscoverRenderDocDllFromRegistry() {
    HKEY hKey = NULL;
    if (RegOpenKeyExA(
            HKEY_LOCAL_MACHINE,
            "SOFTWARE\\Classes\\RenderDoc.RDCCapture.1\\DefaultIcon",
            0,
            KEY_READ,
            &hKey) != ERROR_SUCCESS) {
        return "";
    }

    char iconBuf[MAX_PATH * 2] = {0};
    DWORD bufSize = sizeof(iconBuf) - 1;
    DWORD valType = 0;
    LSTATUS st = RegQueryValueExA(hKey, nullptr, nullptr, &valType, reinterpret_cast<LPBYTE>(iconBuf), &bufSize);
    RegCloseKey(hKey);
    if (st != ERROR_SUCCESS || iconBuf[0] == '\0') {
        return "";
    }

    std::string pathStr(iconBuf);
    size_t commaPos = pathStr.rfind(',');
    if (commaPos != std::string::npos) {
        pathStr.erase(commaPos);
    }
    while (!pathStr.empty() && (pathStr.front() == '"' || pathStr.front() == ' ')) {
        pathStr.erase(pathStr.begin());
    }
    while (!pathStr.empty() && (pathStr.back() == '"' || pathStr.back() == ' ')) {
        pathStr.pop_back();
    }

    size_t exePos = pathStr.find("qrenderdoc.exe");
    if (exePos != std::string::npos) {
        pathStr.replace(exePos, 14, "renderdoc.dll");
        return pathStr;
    }

    size_t slashPos = pathStr.find_last_of("\\/");
    if (slashPos != std::string::npos) {
        return pathStr.substr(0, slashPos + 1) + "renderdoc.dll";
    }
    return "";
}

static bool IsRenderDocEnvTruthy(const char* envName) {
    const char* val = std::getenv(envName);
    if (!val || !val[0]) {
        return false;
    }
    return (std::strcmp(val, "1") == 0 || _stricmp(val, "true") == 0 || _stricmp(val, "yes") == 0);
}

bool HeadlessRenderDocCaptureController::TryLoadAndInitialize(const char* captureOutputTemplate) {
    if (m_initialized && m_rdocApi != nullptr) {
        return true;
    }

    if (IsRenderDocEnvTruthy("DISABLE_VULKAN_RENDERDOC_CAPTURE")) {
        HON_LOG_INFO("[RenderDocBridge] Opt-in guard: DISABLE_VULKAN_RENDERDOC_CAPTURE is set; skipping renderdoc.dll attachment/load.");
        return false;
    }

    HMODULE hRdoc = GetModuleHandleA("renderdoc.dll");
    if (!hRdoc) {
        if (!IsRenderDocEnvTruthy("HON_RENDERDOC_ENABLE")) {
            HON_LOG_INFO("[RenderDocBridge] Opt-in guard active (HON_RENDERDOC_ENABLE!=1); skipping dynamic renderdoc.dll load.");
            return false;
        }
        std::string regPath = DiscoverRenderDocDllFromRegistry();
        if (!regPath.empty()) {
            hRdoc = LoadLibraryA(regPath.c_str());
            HON_LOG_INFO("[RenderDocBridge] HON_RENDERDOC_ENABLE=1: LoadLibraryA('%s') -> %s.",
                regPath.c_str(), hRdoc ? "OK" : "FAILED");
        }
    }
    if (!hRdoc) {
        return false;
    }

    auto getApiFn = reinterpret_cast<pRENDERDOC_GetAPI>(GetProcAddress(hRdoc, "RENDERDOC_GetAPI"));
    if (!getApiFn) {
        return false;
    }

    void* rawApi = nullptr;
    if (getApiFn(eRENDERDOC_API_Version_1_6_0, &rawApi) != 1 || !rawApi) {
        return false;
    }

    m_rdocApi = reinterpret_cast<MinimalRenderDocApi160*>(rawApi);
    // Immediately unload RenderDoc's global crash handler so it does not hijack native_guard/veh_crash_shield.cpp
    if (m_rdocApi->UnloadCrashHandler) {
        m_rdocApi->UnloadCrashHandler();
        m_unloadCrashHandlerCalled = true;
    }
    if (m_rdocApi->SetCaptureFilePathTemplate && captureOutputTemplate && captureOutputTemplate[0]) {
        m_rdocApi->SetCaptureFilePathTemplate(captureOutputTemplate);
    }
    m_initialized = true;
    HON_LOG_INFO("[RenderDocBridge] Initialized headless RenderDoc API (v%d) and called UnloadCrashHandler().", eRENDERDOC_API_Version_1_6_0);
    return true;
}

void HeadlessRenderDocCaptureController::TriggerNextFrameCapture() {
    if (!m_initialized && IsRenderDocEnvTruthy("HON_RENDERDOC_ENABLE")) {
        TryLoadAndInitialize();
    }
    if (m_state == RenderDocCaptureState::Idle) {
        m_state = RenderDocCaptureState::Triggered;
    }
}

void HeadlessRenderDocCaptureController::OnPresentBegin(void* device, void* wndHandle) {
    if (m_state == RenderDocCaptureState::Triggered) {
        if (m_rdocApi && m_rdocApi->StartFrameCapture) {
            m_rdocApi->StartFrameCapture(device, wndHandle);
        }
        m_state = RenderDocCaptureState::InProgress;
    }
}

void HeadlessRenderDocCaptureController::OnPresentEnd(void* device, void* wndHandle) {
    if (m_state == RenderDocCaptureState::InProgress) {
        if (m_rdocApi && m_rdocApi->EndFrameCapture) {
            m_rdocApi->EndFrameCapture(device, wndHandle);
        }
        m_state = RenderDocCaptureState::Idle;
    }
}

// ============================================================================
// (#205) Three-Tier Staging Buffer Pool with Idle-Frame GC Implementation
// ============================================================================
uint64_t ThreeTierStagingBufferAllocator::AllocateStagingSlice(
    uint64_t sizeBytes,
    uint64_t submitTimelineTick,
    bool* outUsedTier1Ring)
{
    uint64_t effectiveSize = sizeBytes > 0 ? sizeBytes : 1;
    if (effectiveSize <= TIER1_MAX_SINGLE_ALLOC && effectiveSize <= TIER1_RING_CAPACITY_BYTES) {
        uint64_t alignedOffset = (m_ringWriteOffset + 255ULL) & ~255ULL;
        if (alignedOffset + effectiveSize > TIER1_RING_CAPACITY_BYTES) {
            alignedOffset = 0;
        }
        m_ringWriteOffset = alignedOffset + effectiveSize;
        if (outUsedTier1Ring) *outUsedTier1Ring = true;
        return alignedOffset;
    }

    if (outUsedTier1Ring) *outUsedTier1Ring = false;
    uint64_t roundedCap = (effectiveSize + 4095ULL) & ~4095ULL;
    for (PooledStagingEntry& entry : m_tier2Pool) {
        if (!entry.inUse &&
            entry.busyUntilTimelineTick <= m_completedTick &&
            entry.capacityBytes >= roundedCap &&
            entry.capacityBytes <= roundedCap * 2ULL) {
            entry.inUse = true;
            entry.busyUntilTimelineTick = submitTimelineTick;
            entry.lastUsedEpoch = m_currentEpoch;
            return entry.bufferId;
        }
    }

    PooledStagingEntry created{};
    created.bufferId = ++m_nextBufferId;
    created.capacityBytes = roundedCap;
    created.busyUntilTimelineTick = submitTimelineTick;
    created.lastUsedEpoch = m_currentEpoch;
    created.inUse = true;
    m_tier2Pool.push_back(created);
    return created.bufferId;
}

size_t ThreeTierStagingBufferAllocator::TickFrame(uint64_t completedTimelineTick) {
    ++m_currentEpoch;
    if (completedTimelineTick > m_completedTick) {
        m_completedTick = completedTimelineTick;
    }

    size_t evicted = 0;
    for (size_t i = m_tier2Pool.size(); i > 0; --i) {
        size_t idx = i - 1;
        PooledStagingEntry& entry = m_tier2Pool[idx];
        if (entry.inUse && entry.busyUntilTimelineTick <= m_completedTick) {
            entry.inUse = false;
        }
        if (!entry.inUse &&
            entry.busyUntilTimelineTick <= m_completedTick &&
            m_currentEpoch >= entry.lastUsedEpoch &&
            (m_currentEpoch - entry.lastUsedEpoch) >= MAX_IDLE_FRAMES) {
            m_tier2Pool.erase(m_tier2Pool.begin() + static_cast<ptrdiff_t>(idx));
            ++evicted;
            ++m_totalEvicted;
        }
    }
    return evicted;
}

// (#206, [L2-P0-06]) TwoLevelPageBitmaskTracker is defined inline in page_bitmask_tracker.h.

// ============================================================================
// (#207) 8-Slot Download Buffer Ring & GPU Fault-Buffer Bitmask Scanner
// ============================================================================
void GpuFaultBufferRing::RegisterBuffer(uint64_t bufferId, uint64_t baseVa, uint64_t sizeBytes, const char* debugName) {
    if (sizeBytes == 0) return;
    RegisteredBufferSpan entry{};
    entry.bufferId = bufferId;
    entry.baseVa = baseVa;
    entry.sizeBytes = sizeBytes;
    if (debugName && debugName[0]) {
        strncpy_s(entry.debugName, sizeof(entry.debugName), debugName, _TRUNCATE);
    } else {
        strncpy_s(entry.debugName, sizeof(entry.debugName), "UnnamedBuffer", _TRUNCATE);
    }
    m_buffers.push_back(entry);
}

bool GpuFaultBufferRing::TryAcquireDownloadSlot(uint64_t completedTick, uint64_t submitTick, size_t* outSlotIndex) {
    for (size_t attempt = 0; attempt < DOWNLOAD_RING_SLOTS; ++attempt) {
        size_t idx = (m_nextCursor + attempt) % DOWNLOAD_RING_SLOTS;
        if (m_slotBusyUntilTick[idx] <= completedTick) {
            m_slotBusyUntilTick[idx] = submitTick;
            m_nextCursor = (idx + 1) % DOWNLOAD_RING_SLOTS;
            if (outSlotIndex) *outSlotIndex = idx;
            return true;
        }
    }
    return false;
}

size_t GpuFaultBufferRing::ScanFaultWordsAndLog(const uint64_t* faultWords, size_t wordCount, uint64_t baseVa) const {
    if (!faultWords || wordCount == 0) return 0;
    size_t detectedFaults = 0;
    for (size_t w = 0; w < wordCount; ++w) {
        uint64_t word = faultWords[w];
        while (word != 0ULL) {
            int bit = std::countr_zero(word);
            uint64_t pageIndex = (static_cast<uint64_t>(w) * 64ULL) + static_cast<uint64_t>(bit);
            uint64_t faultVa = baseVa + (pageIndex * FAULT_PAGE_SIZE);
            const char* matchedName = "UnmappedGpuPage";
            uint64_t matchedId = 0;
            for (const RegisteredBufferSpan& span : m_buffers) {
                if (faultVa >= span.baseVa && faultVa < (span.baseVa + span.sizeBytes)) {
                    matchedName = span.debugName;
                    matchedId = span.bufferId;
                    break;
                }
            }
            HON_LOG_ERROR("[GPU-FAULT-RING] Faulting 4KB page #%llu @ VA=0x%llX -> Buffer='%s' (id=%llu)",
                (unsigned long long)pageIndex, (unsigned long long)faultVa, matchedName, (unsigned long long)matchedId);
            ++detectedFaults;
            word &= (word - 1ULL);
        }
    }
    return detectedFaults;
}

// ============================================================================
// (#210) OBS AVX 32-Byte Aligned 3-Stage GPU Readback to 2-Slot Shared-Memory Ping-Pong
// ============================================================================
ObsAvxThreeStageToTwoSlotShmemPipeline::ObsAvxThreeStageToTwoSlotShmemPipeline() noexcept {
    last_tex.store(-1, std::memory_order_relaxed);
}

ObsAvxThreeStageToTwoSlotShmemPipeline::~ObsAvxThreeStageToTwoSlotShmemPipeline() {
    StopPipeline();
}

bool ObsAvxThreeStageToTwoSlotShmemPipeline::StartPipeline(
    uint32_t width,
    uint32_t height,
    void* externalShmemBase,
    size_t externalShmemBytes)
{
    StopPipeline();
    if (width == 0 || height == 0) {
        return false;
    }

    m_width = width;
    m_height = height;
    m_alignedRowPitch = Align32(static_cast<size_t>(width) * 4ULL);
    m_slotPayloadBytes = m_alignedRowPitch * static_cast<size_t>(height);

    for (size_t i = 0; i < NUM_STAGE_BUFFERS; ++i) {
        m_stageBuffers[i].assign(m_slotPayloadBytes, 0);
        m_stageSourcePtr[i] = nullptr;
        m_stageSourceRowPitch[i] = 0;
        m_stageFrameIndex[i] = 0;
        m_stageExtRingSlotPtr[i] = nullptr;
        std::memset(m_stageExtHeader64[i], 0, sizeof(m_stageExtHeader64[i]));
        m_stageExtPixelBytes[i] = 0;
        m_stageExtReadyEvent[i] = NULL;
        m_stageInFlightGuard[i] = nullptr;
        m_stageReady[i] = false;
    }

    if (externalShmemBase != nullptr && externalShmemBytes >= m_slotPayloadBytes * NUM_SHMEM_TEXTURES) {
        uint8_t* basePtr = static_cast<uint8_t*>(externalShmemBase);
        shmem_textures[0] = basePtr;
        shmem_textures[1] = basePtr + m_slotPayloadBytes;
        m_ownedShmemStorage.clear();
    } else {
        m_ownedShmemStorage.assign(m_slotPayloadBytes * NUM_SHMEM_TEXTURES, 0);
        shmem_textures[0] = m_ownedShmemStorage.data();
        shmem_textures[1] = m_ownedShmemStorage.data() + m_slotPayloadBytes;
    }

    for (size_t s = 0; s < NUM_SHMEM_TEXTURES; ++s) {
        m_shmemFrameIndex[s] = 0;
        m_shmemSeq[s].store(0ULL, std::memory_order_relaxed);
    }
    last_tex.store(-1, std::memory_order_release);

    copy_event = CreateEventA(nullptr, FALSE, FALSE, nullptr);
    stop_event = CreateEventA(nullptr, TRUE, FALSE, nullptr);
    if (!copy_event || !stop_event) {
        StopPipeline();
        return false;
    }

    m_running.store(true, std::memory_order_release);
    copy_thread = std::thread(&ObsAvxThreeStageToTwoSlotShmemPipeline::CopyThreadMain, this);
    HON_LOG_INFO("[ObsShmemPingPong] Started 3-stage -> 2-slot shmem copy_thread (%ux%u, alignedRowPitch=%zu).",
        m_width, m_height, m_alignedRowPitch);
    return true;
}

void ObsAvxThreeStageToTwoSlotShmemPipeline::StopPipeline() {
    m_running.store(false, std::memory_order_release);
    if (IsLayerProcessTerminating()) {
        // [L2-P1-02] Under LdrShutdownProcess, copy_thread has already been terminated by NtTerminateProcess(NULL, 0).
        // Detach so ~std::thread() never calls std::terminate(), and skip blocking join/handle teardown.
        if (copy_thread.joinable()) {
            copy_thread.detach();
        }
        return;
    }
    if (stop_event != NULL) {
        SetEvent(stop_event);
    }
    if (copy_thread.joinable()) {
        copy_thread.join();
    }
    if (copy_event != NULL) {
        CloseHandle(copy_event);
        copy_event = NULL;
    }
    if (stop_event != NULL) {
        CloseHandle(stop_event);
        stop_event = NULL;
    }
    for (size_t i = 0; i < NUM_STAGE_BUFFERS; ++i) {
        if (m_stageInFlightGuard[i] != nullptr) {
            m_stageInFlightGuard[i]->store(false, std::memory_order_release);
            m_stageInFlightGuard[i] = nullptr;
        }
        m_stageSourcePtr[i] = nullptr;
        m_stageSourceRowPitch[i] = 0;
        m_stageExtRingSlotPtr[i] = nullptr;
        m_stageExtPixelBytes[i] = 0;
        m_stageExtReadyEvent[i] = NULL;
        m_stageReady[i] = false;
    }
    shmem_textures[0] = nullptr;
    shmem_textures[1] = nullptr;
    last_tex.store(-1, std::memory_order_release);
}

bool ObsAvxThreeStageToTwoSlotShmemPipeline::EnqueueCompletedStage(
    uint32_t stageIndex,
    const uint8_t* stageMappedPtr,
    size_t rowPitchBytes,
    uint64_t frameIndex,
    uint8_t* externalRingSlotBasePtr,
    const void* externalRingHeader64,
    size_t externalRingPixelBytes,
    HANDLE externalReadyEvent,
    std::atomic<bool>* inFlightStageGuard)
{
    if (!m_running.load(std::memory_order_acquire) || !stageMappedPtr || m_slotPayloadBytes == 0) {
        return false;
    }

    size_t normalizedStage = stageIndex % NUM_STAGE_BUFFERS;
    {
        std::lock_guard<std::mutex> lock(m_queueMutex);
        if (m_stageReady[normalizedStage] && m_stageInFlightGuard[normalizedStage] != nullptr &&
            m_stageInFlightGuard[normalizedStage] != inFlightStageGuard) {
            m_stageInFlightGuard[normalizedStage]->store(false, std::memory_order_release);
        }
        if (inFlightStageGuard != nullptr) {
            inFlightStageGuard->store(true, std::memory_order_release);
        }
        m_stageSourcePtr[normalizedStage] = stageMappedPtr;
        m_stageSourceRowPitch[normalizedStage] = rowPitchBytes;
        m_stageFrameIndex[normalizedStage] = frameIndex;
        m_stageExtRingSlotPtr[normalizedStage] = externalRingSlotBasePtr;
        if (externalRingHeader64 != nullptr) {
            std::memcpy(m_stageExtHeader64[normalizedStage], externalRingHeader64, 64);
        } else {
            std::memset(m_stageExtHeader64[normalizedStage], 0, 64);
        }
        m_stageExtPixelBytes[normalizedStage] = externalRingPixelBytes;
        m_stageExtReadyEvent[normalizedStage] = externalReadyEvent;
        m_stageInFlightGuard[normalizedStage] = inFlightStageGuard;
        m_stageReady[normalizedStage] = true;
    }

    if (copy_event != NULL) {
        SetEvent(copy_event);
    }
    return true;
}

void ObsAvxThreeStageToTwoSlotShmemPipeline::CopyThreadMain() {
    HANDLE waitHandles[2] = { copy_event, stop_event };
    while (m_running.load(std::memory_order_acquire)) {
        DWORD waitRes = WaitForMultipleObjects(2, waitHandles, FALSE, 100);
        if (waitRes == WAIT_OBJECT_0 + 1 || !m_running.load(std::memory_order_acquire)) {
            break;
        }
        if (waitRes != WAIT_OBJECT_0) {
            continue;
        }

        for (size_t stageIdx = 0; stageIdx < NUM_STAGE_BUFFERS; ++stageIdx) {
            uint64_t frameIdx = 0;
            const uint8_t* srcPtr = nullptr;
            size_t srcPitch = 0;
            uint8_t* extRingSlotPtr = nullptr;
            uint8_t extHeader64[64] = {};
            size_t extPixelBytes = 0;
            HANDLE extReadyEvent = NULL;
            std::atomic<bool>* inFlightGuard = nullptr;
            bool hasStage = false;
            {
                // [L2-P0-06] Snapshot stage pointers in O(1) under m_queueMutex; NEVER hold m_queueMutex during memcpy!
                std::lock_guard<std::mutex> qLock(m_queueMutex);
                if (m_stageReady[stageIdx]) {
                    hasStage = true;
                    frameIdx = m_stageFrameIndex[stageIdx];
                    srcPtr = m_stageSourcePtr[stageIdx];
                    srcPitch = m_stageSourceRowPitch[stageIdx];
                    extRingSlotPtr = m_stageExtRingSlotPtr[stageIdx];
                    std::memcpy(extHeader64, m_stageExtHeader64[stageIdx], 64);
                    extPixelBytes = m_stageExtPixelBytes[stageIdx];
                    extReadyEvent = m_stageExtReadyEvent[stageIdx];
                    inFlightGuard = m_stageInFlightGuard[stageIdx];
                    m_stageReady[stageIdx] = false;
                    m_stageInFlightGuard[stageIdx] = nullptr;
                }
            }
            if (!hasStage) {
                continue;
            }

            if (srcPtr != nullptr && srcPitch > 0) {
                // 1. Async Tier 1 HonReplayFrameBuffer ring slot copy (zero memcpy on QueuePresentKHR thread)
                if (extRingSlotPtr != nullptr && extPixelBytes > 0) {
                    std::memcpy(extRingSlotPtr + sizeof(HONCAP_FRAME_HEADER), srcPtr, extPixelBytes);
                    std::memcpy(extRingSlotPtr, extHeader64, sizeof(HONCAP_FRAME_HEADER));
                    if (extReadyEvent != NULL) {
                        SetEvent(extReadyEvent);
                    }
                }

                // 2. Stage into 32-byte aligned internal staging slice
                uint8_t* dstBase = m_stageBuffers[stageIdx].data();
                const size_t copyRowBytes = (std::min)(srcPitch, m_alignedRowPitch);
                for (uint32_t y = 0; y < m_height; ++y) {
                    std::memcpy(
                        dstBase + (static_cast<size_t>(y) * m_alignedRowPitch),
                        srcPtr + (static_cast<size_t>(y) * srcPitch),
                        copyRowBytes);
                }
            }

            if (inFlightGuard != nullptr) {
                inFlightGuard->store(false, std::memory_order_release);
            }

            int32_t curLast = last_tex.load(std::memory_order_acquire);
            int32_t nextTex = (curLast == 0) ? 1 : 0;
            int32_t chosenTex = -1;
            if (shmem_mutexes[nextTex].try_lock()) {
                chosenTex = nextTex;
            } else if (shmem_mutexes[1 - nextTex].try_lock()) {
                chosenTex = 1 - nextTex;
            }
            if (chosenTex < 0) {
                continue;
            }

            m_shmemSeq[chosenTex].fetch_add(1ULL, std::memory_order_release); // odd = write in progress
            if (shmem_textures[chosenTex] != nullptr) {
                // Guarded strictly by shmem_mutexes[chosenTex]; never acquire m_queueMutex here!
                std::memcpy(shmem_textures[chosenTex], m_stageBuffers[stageIdx].data(), m_slotPayloadBytes);
            }
            m_shmemFrameIndex[chosenTex] = frameIdx;
            m_shmemSeq[chosenTex].fetch_add(1ULL, std::memory_order_release); // even = write complete
            last_tex.store(chosenTex, std::memory_order_release);
            m_completedCopies.fetch_add(1ULL, std::memory_order_relaxed);
            shmem_mutexes[chosenTex].unlock();
        }
    }
}

bool ObsAvxThreeStageToTwoSlotShmemPipeline::ReadLatestShmemSlot(
    std::vector<uint8_t>& outPixels,
    int32_t* outSlotIndex,
    uint64_t* outFrameIndex)
{
    int32_t slot = last_tex.load(std::memory_order_acquire);
    if (slot < 0 || slot >= static_cast<int32_t>(NUM_SHMEM_TEXTURES) || !shmem_textures[slot]) {
        return false;
    }

    std::lock_guard<std::mutex> slotLock(shmem_mutexes[slot]);
    uint64_t seqBefore = m_shmemSeq[slot].load(std::memory_order_acquire);
    if ((seqBefore & 1ULL) != 0ULL) {
        return false;
    }

    outPixels.resize(m_slotPayloadBytes);
    std::memcpy(outPixels.data(), shmem_textures[slot], m_slotPayloadBytes);
    uint64_t seqAfter = m_shmemSeq[slot].load(std::memory_order_acquire);
    if (seqBefore != seqAfter) {
        return false;
    }

    if (outSlotIndex) *outSlotIndex = slot;
    if (outFrameIndex) *outFrameIndex = m_shmemFrameIndex[slot];
    return true;
}

FrameCapturer::FrameCapturer()

    : m_device(VK_NULL_HANDLE)
    , m_physicalDevice(VK_NULL_HANDLE)
    , m_queue(VK_NULL_HANDLE)
    , m_queueFamilyIndex(0)
    , m_swapchain(VK_NULL_HANDLE)
    , m_width(0)
    , m_height(0)
    , m_format(VK_FORMAT_B8G8R8A8_UNORM)
    , m_imageCount(0)
    , m_commandPool(VK_NULL_HANDLE)
    , m_ringSlotCount(HON_CAPTURE_DEFAULT_RING_SLOTS)
    , m_currentSlotIndex(0)
    , m_totalFramesCaptured(0)
    , m_hSharedMemoryMap(NULL)
    , m_pSharedMemoryView(nullptr)
    , m_frameSizeBytes(0)
    , m_slotStrideBytes(0)
    , m_totalSharedMemBytes(0)
    , m_activeTier(CAPTURE_TIER_DISABLED)
    , m_importedBuffer(VK_NULL_HANDLE)
    , m_importedMemory(VK_NULL_HANDLE)
    , m_isHostCoherent(false)
    , m_processId(0)
    , m_isInitialized(false)
{
    memset(&m_deviceDispatch, 0, sizeof(m_deviceDispatch));
    memset(&m_instanceDispatch, 0, sizeof(m_instanceDispatch));
    QueryPerformanceFrequency(&m_qpcFrequency);
}

FrameCapturer::~FrameCapturer() {
    if (IsLayerProcessTerminating()) {
        m_obsShmemPingPong.StopPipeline();
        m_device = VK_NULL_HANDLE;
        m_isInitialized = false;
        return;
    }
    Cleanup();
}

uint64_t FrameCapturer::GetCurrentTimestampNs() {
    LARGE_INTEGER qpc;
    QueryPerformanceCounter(&qpc);
    if (m_qpcFrequency.QuadPart == 0) return 0;
    return static_cast<uint64_t>((qpc.QuadPart * 1000000000ULL) / m_qpcFrequency.QuadPart);
}

std::expected<uint32_t, VkResult> FrameCapturer::FindMemoryType(uint32_t typeFilter, VkMemoryPropertyFlags properties) {
    VkPhysicalDeviceMemoryProperties memProperties;
    m_instanceDispatch.GetPhysicalDeviceMemoryProperties(m_physicalDevice, &memProperties);

    for (uint32_t i = 0; i < memProperties.memoryTypeCount; i++) {
        if ((typeFilter & (1 << i)) &&
            (memProperties.memoryTypes[i].propertyFlags & properties) == properties) {
            return i;
        }
    }

    HON_LOG_WARN("Exact memory property match not found, falling back to basic HOST_VISIBLE type.");
    for (uint32_t i = 0; i < memProperties.memoryTypeCount; i++) {
        if ((typeFilter & (1 << i)) &&
            (memProperties.memoryTypes[i].propertyFlags & VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT)) {
            return i;
        }
    }

    HON_LOG_ERROR("Failed to find compatible host-visible memory type!");
    return std::unexpected(VK_ERROR_FEATURE_NOT_PRESENT);
}

std::expected<void, VkResult> FrameCapturer::SetupSharedMemoryIpc() {
    m_processId = GetCurrentProcessId();

    m_frameSizeBytes = static_cast<size_t>(m_width) * m_height * 4;
    // [L2-P0-06] 4KB page-aligned slot stride so adjacent ring slots never share a 4KB bitmask page
    // (also satisfies 32-byte AVX alignment since 4096 is a multiple of 32).
    m_slotStrideBytes = hon_capture::AlignCaptureSlotStride4KB(sizeof(HONCAP_FRAME_HEADER) + m_frameSizeBytes);
    // Align total shared memory size to Windows 64 KB (0x10000) allocation granularity
    m_totalSharedMemBytes = (m_slotStrideBytes * m_ringSlotCount + 0xFFFF) & ~0xFFFF;

    char mapName[256];
    sprintf_s(mapName, "Local\\HonReplayFrameBuffer_%lu", m_processId);

    HON_LOG_INFO("Creating Named FileMapping: %s (Size: %zu bytes / %.2f MB, 64KB aligned)",
                 mapName, m_totalSharedMemBytes, static_cast<double>(m_totalSharedMemBytes) / (1024.0 * 1024.0));

    m_hSharedMemoryMap = CreateFileMappingA(
        INVALID_HANDLE_VALUE,
        nullptr,
        PAGE_READWRITE,
        0,
        static_cast<DWORD>(m_totalSharedMemBytes),
        mapName);

    if (!m_hSharedMemoryMap) {
        HON_LOG_ERROR("CreateFileMappingA failed! Error: %lu", GetLastError());
        return std::unexpected(VK_ERROR_INITIALIZATION_FAILED);
    }

    m_pSharedMemoryView = MapViewOfFile(
        m_hSharedMemoryMap,
        FILE_MAP_ALL_ACCESS,
        0,
        0,
        m_totalSharedMemBytes);

    if (!m_pSharedMemoryView) {
        HON_LOG_ERROR("MapViewOfFile failed! Error: %lu", GetLastError());
        CloseHandle(m_hSharedMemoryMap);
        m_hSharedMemoryMap = NULL;
        return std::unexpected(VK_ERROR_INITIALIZATION_FAILED);
    }

    auto shmSpan = GetSharedMemorySpan();
    if (!shmSpan.empty()) {
        std::ranges::fill(shmSpan, uint8_t{0});
    }

    // Write discovery metadata so external streamers and tools discover active session immediately
    FILE* fMeta = fopen("artifacts/captures/honcap_active.json", "w");
    if (fMeta) {
        fprintf(fMeta, "{\n"
                       "  \"pid\": %lu,\n"
                       "  \"map_name\": \"%s\",\n"
                       "  \"width\": %u,\n"
                       "  \"height\": %u,\n"
                       "  \"format\": %u,\n"
                       "  \"slots\": %u,\n"
                       "  \"slot_stride\": %zu,\n"
                       "  \"total_bytes\": %zu\n"
                       "}\n",
                       m_processId, mapName, m_width, m_height, static_cast<uint32_t>(m_format),
                       m_ringSlotCount, m_slotStrideBytes, m_totalSharedMemBytes);
        fclose(fMeta);
        HON_LOG_INFO("Wrote active capture discovery session to artifacts/captures/honcap_active.json");
    }

    return {};
}

std::expected<void, VkResult> FrameCapturer::CreateCommandResourcesAndSyncEvents() {
    // Dedicated Command Pool for Transfer operations
    VkCommandPoolCreateInfo poolInfo = { VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO };
    poolInfo.flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT;
    poolInfo.queueFamilyIndex = m_queueFamilyIndex;

    VkResult res = m_deviceDispatch.CreateCommandPool(m_device, &poolInfo, nullptr, &m_commandPool);
    if (res != VK_SUCCESS) {
        HON_LOG_ERROR("vkCreateCommandPool failed with code %d", res);
        return std::unexpected(res);
    }

    m_slots.resize(m_ringSlotCount);

    for (uint32_t i = 0; i < m_ringSlotCount; i++) {
        StagingSlot& slot = m_slots[i];
        slot = StagingSlot{};

        // Allocate Primary Command Buffer
        VkCommandBufferAllocateInfo cmdAlloc = { VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO };
        cmdAlloc.commandPool = m_commandPool;
        cmdAlloc.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
        cmdAlloc.commandBufferCount = 1;

        res = m_deviceDispatch.AllocateCommandBuffers(m_device, &cmdAlloc, &slot.cmdBuffer);
        if (res != VK_SUCCESS) {
            HON_LOG_ERROR("vkAllocateCommandBuffers failed for slot %u with code %d", i, res);
            return std::unexpected(res);
        }

        // Create Fence in SIGNALED state
        VkFenceCreateInfo fenceInfo = { VK_STRUCTURE_TYPE_FENCE_CREATE_INFO };
        fenceInfo.flags = VK_FENCE_CREATE_SIGNALED_BIT;

        res = m_deviceDispatch.CreateFence(m_device, &fenceInfo, nullptr, &slot.fence);
        if (res != VK_SUCCESS) {
            HON_LOG_ERROR("vkCreateFence failed for slot %u with code %d", i, res);
            return std::unexpected(res);
        }

        // Create Copy Complete Semaphore
        slot.copyCompleteSemaphore = VK_NULL_HANDLE;
        if (m_deviceDispatch.CreateSemaphore) {
            VkSemaphoreCreateInfo semInfo = { VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO };
            res = m_deviceDispatch.CreateSemaphore(m_device, &semInfo, nullptr, &slot.copyCompleteSemaphore);
            if (res != VK_SUCCESS) {
                HON_LOG_ERROR("vkCreateSemaphore failed for slot %u with code %d", i, res);
                return std::unexpected(res);
            }
        }

        // Create Win32 Named IPC Events
        char readyName[256];
        char doneName[256];
        sprintf_s(readyName, "Local\\HonFrameReady_%lu_%u", m_processId, i);
        sprintf_s(doneName, "Local\\HonFrameDone_%lu_%u", m_processId, i);

        slot.hReadyEvent = CreateEventA(nullptr, FALSE, FALSE, readyName);
        slot.hDoneEvent = CreateEventA(nullptr, FALSE, TRUE, doneName); // Initially signaled
        slot.isPending = false;
        slot.state = SLOT_STATE_FREE;
        slot.frameIndex = 0;

        HON_LOG_DEBUG("Slot %u initialized. ReadyEvent: %s, DoneEvent: %s", i, readyName, doneName);
    }

    return {};
}

std::expected<void, VkResult> FrameCapturer::TryInitializeTier0() {
    if (!m_pSharedMemoryView || m_totalSharedMemBytes == 0) {
        return std::unexpected(VK_ERROR_INITIALIZATION_FAILED);
    }

    if (!m_deviceDispatch.GetMemoryHostPointerPropertiesEXT) {
        HON_LOG_INFO("Tier 0: GetMemoryHostPointerPropertiesEXT not available in dispatch table.");
        return std::unexpected(VK_ERROR_EXTENSION_NOT_PRESENT);
    }

    VkMemoryHostPointerPropertiesEXT hostPtrProps = { VK_STRUCTURE_TYPE_MEMORY_HOST_POINTER_PROPERTIES_EXT };
    VkResult res = m_deviceDispatch.GetMemoryHostPointerPropertiesEXT(
        m_device,
        VK_EXTERNAL_MEMORY_HANDLE_TYPE_HOST_ALLOCATION_BIT_EXT,
        m_pSharedMemoryView,
        &hostPtrProps);

    if (res != VK_SUCCESS || hostPtrProps.memoryTypeBits == 0) {
        HON_LOG_WARN("Tier 0: GetMemoryHostPointerPropertiesEXT returned res=%d, memTypeBits=0x%08X", res, hostPtrProps.memoryTypeBits);
        return std::unexpected(res != VK_SUCCESS ? res : VK_ERROR_FEATURE_NOT_PRESENT);
    }

    VkPhysicalDeviceMemoryProperties memProperties = {};
    if (m_instanceDispatch.GetPhysicalDeviceMemoryProperties && m_physicalDevice != VK_NULL_HANDLE) {
        m_instanceDispatch.GetPhysicalDeviceMemoryProperties(m_physicalDevice, &memProperties);
    } else {
        HON_LOG_WARN("Tier 0: GetPhysicalDeviceMemoryProperties not available.");
        return std::unexpected(VK_ERROR_INITIALIZATION_FAILED);
    }

    uint32_t chosenTypeIndex = UINT32_MAX;
    bool isCoherent = false;

    // Prioritize HostVisible | HostCoherent
    for (uint32_t i = 0; i < memProperties.memoryTypeCount; i++) {
        if ((hostPtrProps.memoryTypeBits & (1 << i)) &&
            (memProperties.memoryTypes[i].propertyFlags & VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT) &&
            (memProperties.memoryTypes[i].propertyFlags & VK_MEMORY_PROPERTY_HOST_COHERENT_BIT)) {
            chosenTypeIndex = i;
            isCoherent = true;
            break;
        }
    }

    if (chosenTypeIndex == UINT32_MAX) {
        for (uint32_t i = 0; i < memProperties.memoryTypeCount; i++) {
            if ((hostPtrProps.memoryTypeBits & (1 << i)) &&
                (memProperties.memoryTypes[i].propertyFlags & VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT)) {
                chosenTypeIndex = i;
                isCoherent = false;
                break;
            }
        }
    }

    if (chosenTypeIndex == UINT32_MAX) {
        HON_LOG_WARN("Tier 0: No compatible host-visible memory type found for imported host pointer.");
        return std::unexpected(VK_ERROR_FEATURE_NOT_PRESENT);
    }

    // Create external memory buffer spanning entire shared memory
    VkExternalMemoryBufferCreateInfo extBufInfo = { VK_STRUCTURE_TYPE_EXTERNAL_MEMORY_BUFFER_CREATE_INFO };
    extBufInfo.handleTypes = VK_EXTERNAL_MEMORY_HANDLE_TYPE_HOST_ALLOCATION_BIT_EXT;

    VkBufferCreateInfo bufInfo = { VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO };
    bufInfo.pNext = &extBufInfo;
    bufInfo.size = m_totalSharedMemBytes;
    bufInfo.usage = VK_BUFFER_USAGE_TRANSFER_DST_BIT;
    bufInfo.sharingMode = VK_SHARING_MODE_EXCLUSIVE;

    res = m_deviceDispatch.CreateBuffer(m_device, &bufInfo, nullptr, &m_importedBuffer);
    if (res != VK_SUCCESS) {
        HON_LOG_WARN("Tier 0: vkCreateBuffer for imported buffer failed with code %d", res);
        return std::unexpected(res);
    }

    // Import Host Pointer Memory
    VkImportMemoryHostPointerInfoEXT importInfo = { VK_STRUCTURE_TYPE_IMPORT_MEMORY_HOST_POINTER_INFO_EXT };
    importInfo.handleType = VK_EXTERNAL_MEMORY_HANDLE_TYPE_HOST_ALLOCATION_BIT_EXT;
    importInfo.pHostPointer = m_pSharedMemoryView;

    VkMemoryAllocateInfo allocInfo = { VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO };
    allocInfo.pNext = &importInfo;
    allocInfo.allocationSize = m_totalSharedMemBytes;
    allocInfo.memoryTypeIndex = chosenTypeIndex;

    res = m_deviceDispatch.AllocateMemory(m_device, &allocInfo, nullptr, &m_importedMemory);
    if (res != VK_SUCCESS) {
        HON_LOG_WARN("Tier 0: vkAllocateMemory for imported host pointer failed with code %d", res);
        m_deviceDispatch.DestroyBuffer(m_device, m_importedBuffer, nullptr);
        m_importedBuffer = VK_NULL_HANDLE;
        return std::unexpected(res);
    }

    res = m_deviceDispatch.BindBufferMemory(m_device, m_importedBuffer, m_importedMemory, 0);
    if (res != VK_SUCCESS) {
        HON_LOG_WARN("Tier 0: vkBindBufferMemory for imported buffer failed with code %d", res);
        m_deviceDispatch.DestroyBuffer(m_device, m_importedBuffer, nullptr);
        m_deviceDispatch.FreeMemory(m_device, m_importedMemory, nullptr);
        m_importedBuffer = VK_NULL_HANDLE;
        m_importedMemory = VK_NULL_HANDLE;
        return std::unexpected(res);
    }

    m_isHostCoherent = isCoherent;
    m_activeTier = CAPTURE_TIER_0_GPU_DMA;
    return {};
}

std::expected<void, VkResult> FrameCapturer::InitializeTier1StagingFallback() {
    for (uint32_t i = 0; i < m_ringSlotCount; i++) {
        StagingSlot& slot = m_slots[i];

        // Create staging VkBuffer
        VkBufferCreateInfo bufInfo = { VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO };
        bufInfo.size = m_frameSizeBytes;
        bufInfo.usage = VK_BUFFER_USAGE_TRANSFER_DST_BIT;
        bufInfo.sharingMode = VK_SHARING_MODE_EXCLUSIVE;

        VkResult res = m_deviceDispatch.CreateBuffer(m_device, &bufInfo, nullptr, &slot.buffer);
        if (res != VK_SUCCESS) {
            HON_LOG_ERROR("vkCreateBuffer failed for slot %u with code %d", i, res);
            return std::unexpected(res);
        }

        // Memory Requirements & Allocation
        VkMemoryRequirements memReq = {};
        m_deviceDispatch.GetBufferMemoryRequirements(m_device, slot.buffer, &memReq);

        auto memType = FindMemoryType(
            memReq.memoryTypeBits,
            VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT);
        if (!memType) {
            HON_LOG_ERROR("FindMemoryType failed for slot %u with code %d", i, memType.error());
            return std::unexpected(memType.error());
        }

        VkMemoryAllocateInfo allocInfo = { VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO };
        allocInfo.allocationSize = memReq.size;
        allocInfo.memoryTypeIndex = *memType;

        res = m_deviceDispatch.AllocateMemory(m_device, &allocInfo, nullptr, &slot.memory);
        if (res != VK_SUCCESS) {
            HON_LOG_ERROR("vkAllocateMemory failed for slot %u with code %d", i, res);
            return std::unexpected(res);
        }

        res = m_deviceDispatch.BindBufferMemory(m_device, slot.buffer, slot.memory, 0);
        if (res != VK_SUCCESS) {
            HON_LOG_ERROR("vkBindBufferMemory failed for slot %u with code %d", i, res);
            return std::unexpected(res);
        }

        // Persistently map host-visible memory
        res = m_deviceDispatch.MapMemory(m_device, slot.memory, 0, m_frameSizeBytes, 0, &slot.mappedHostPtr);
        if (res != VK_SUCCESS) {
            HON_LOG_ERROR("vkMapMemory failed for slot %u with code %d", i, res);
            return std::unexpected(res);
        }
    }

    m_activeTier = CAPTURE_TIER_1_STAGING_FALLBACK;
    return {};
}

std::expected<void, VkResult> FrameCapturer::CreateStagingBuffers() {
    return InitializeTier1StagingFallback();
}

bool FrameCapturer::Initialize(
    VkDevice device,
    VkPhysicalDevice physicalDevice,
    VkQueue queue,
    uint32_t queueFamilyIndex,
    const VkSwapchainCreateInfoKHR* pCreateInfo,
    VkSwapchainKHR swapchain,
    const LayerDeviceDispatchTable& deviceDispatch,
    const LayerInstanceDispatchTable& instanceDispatch)
{
    std::lock_guard<std::mutex> lock(m_capturerMutex);

    if (m_isInitialized) {
        HON_LOG_INFO("Re-initializing FrameCapturer for new swapchain configuration.");
        CleanupLocked();
    }

    m_device = device;
    m_physicalDevice = physicalDevice;
    m_queue = queue;
    m_queueFamilyIndex = queueFamilyIndex;
    m_swapchain = swapchain;
    m_deviceDispatch = deviceDispatch;
    m_instanceDispatch = instanceDispatch;

    m_width = pCreateInfo->imageExtent.width;
    m_height = pCreateInfo->imageExtent.height;
    m_format = pCreateInfo->imageFormat;
    m_imageCount = pCreateInfo->minImageCount;

    // Check custom slot depth override
    const char* slotEnv = getenv("HON_CAPTURE_SLOTS");
    if (slotEnv) {
        int slots = atoi(slotEnv);
        if (slots >= 2 && slots <= 8) {
            m_ringSlotCount = static_cast<uint32_t>(slots);
        }
    }

    // Tier 2: Check if frame capture is disabled
    const char* envCap = getenv("HON_CAPTURE_ENABLE");
    bool captureEnabled = (envCap && (strcmp(envCap, "1") == 0 || _stricmp(envCap, "true") == 0));
    if (!captureEnabled) {
        m_activeTier = CAPTURE_TIER_DISABLED;
        m_isInitialized = true;
        HON_LOG_INFO("FrameCapturer: Disabled by default (HON_CAPTURE_ENABLE!=1). Zero GPU/CPU overhead.");
        return true;
    }

    HON_LOG_INFO("Initializing FrameCapturer: Resolution %ux%u, Format %d, RingSlots %u",
                 m_width, m_height, m_format, m_ringSlotCount);

    auto shmRes = SetupSharedMemoryIpc();
    if (!shmRes) {
        return false;
    }

    auto cmdRes = CreateCommandResourcesAndSyncEvents();
    if (!cmdRes) {
        return false;
    }

    auto tier0Res = TryInitializeTier0();
    if (tier0Res) {
        HON_LOG_INFO("FrameCapturer: Tier 0 Active (VK_EXT_external_memory_host Direct GPU DMA Zero-Copy Frame Capture Enabled).");
    } else {
        HON_LOG_WARN("FrameCapturer: Tier 0 unavailable (Code %d). Falling back to Tier 1 Host Staging Buffers.", tier0Res.error());
        auto tier1Res = InitializeTier1StagingFallback();
        if (!tier1Res) {
            HON_LOG_ERROR("FrameCapturer: Failed to initialize Tier 1 Staging Fallback (Code %d)!", tier1Res.error());
            return false;
        }
    }

    m_currentSlotIndex = 0;
    m_totalFramesCaptured.store(0);
    // [L2-P0-06] Track the entire multi-slot shared-memory ring so slot >= 1 offsets are tracked accurately.
    m_pageTracker.Initialize(m_totalSharedMemBytes);
    m_faultRing.RegisterBuffer(1, 0x10000000ULL, m_totalSharedMemBytes, "HonSharedMemoryCaptureRing");
    m_renderDocController.TryLoadAndInitialize();

    // #210 (Gem 3A): Initialize 3-stage-to-2-slot AVX-aligned shared-memory ping-pong copy thread
    // Use dedicated ping-pong storage (nullptr) so HONCAP_FRAME_HEADER at offset 0 of m_pSharedMemoryView slots is never overwritten.
    if (m_pSharedMemoryView && m_ringSlotCount >= NUM_SHMEM_TEXTURES) {
        m_obsShmemPingPong.StartPipeline(
            m_width,
            m_height,
            nullptr,
            0);
    }

    // #212 (Gem 3K): Check if host keep-alive mutex monitoring is requested
    const char* envKeepAlive = getenv("HON_CAPTURE_REQUIRE_KEEPALIVE");
    m_hostKeepAliveArmed = (envKeepAlive && (strcmp(envKeepAlive, "1") == 0 || _stricmp(envKeepAlive, "true") == 0));
    m_gpuStagingDisarmedByWatchdog.store(false, std::memory_order_release);
    m_lastKeepAliveCheckNs = GetCurrentTimestampNs();

    // #213 (Gem 3M): Optional nanosecond frame pacing interval override
    const char* envIntervalNs = getenv("HON_CAPTURE_INTERVAL_NS");
    if (envIntervalNs) {
        uint64_t intervalNs = static_cast<uint64_t>(_strtoui64(envIntervalNs, nullptr, 10));
        SetTargetCaptureIntervalNs(intervalNs);
    }

    m_isInitialized = true;

    HON_LOG_INFO("FrameCapturer initialization complete for PID %lu (ActiveTier=%u, SlotStride=%zu).",
                 m_processId, static_cast<uint32_t>(m_activeTier), m_slotStrideBytes);
    return true;
}

bool FrameCapturer::CheckHostKeepAliveMutex(uint64_t currentTimestampNs) {
    if (currentTimestampNs - m_lastKeepAliveCheckNs < KEEPALIVE_CHECK_INTERVAL_NS) {
        return !m_gpuStagingDisarmedByWatchdog.load(std::memory_order_acquire);
    }
    m_lastKeepAliveCheckNs = currentTimestampNs;

    wchar_t mutexName[128];
    swprintf_s(mutexName, L"Local\\KongorCaptureKeepAlive_%lu", m_processId);
    HANDLE hKeepAlive = OpenMutexW(SYNCHRONIZE, FALSE, mutexName);
    if (hKeepAlive != NULL) {
        CloseHandle(hKeepAlive);
        m_hostKeepAliveArmed = true;
        if (m_gpuStagingDisarmedByWatchdog.exchange(false, std::memory_order_acq_rel)) {
            HON_LOG_INFO("FrameCapturer: Host keep-alive mutex %ls re-acquired; re-arming GPU staging.", mutexName);
        }
        return true;
    }

    if (m_hostKeepAliveArmed) {
        if (!m_gpuStagingDisarmedByWatchdog.exchange(true, std::memory_order_acq_rel)) {
            HON_LOG_WARN("FrameCapturer: Host keep-alive mutex %ls disappeared; disarming GPU staging to prevent orphan bandwidth leak.", mutexName);
        }
        return false;
    }

    return true;
}

void FrameCapturer::OnBeforeSwapchainResizeOrRecreate(VkSwapchainKHR oldSwapchain, uint32_t newWidth, uint32_t newHeight) {
    std::lock_guard<std::mutex> lock(m_capturerMutex);
    m_resizePreReleaseCount.fetch_add(1, std::memory_order_relaxed);
    HON_LOG_INFO("FrameCapturer: OnBeforeSwapchainResizeOrRecreate invoked (oldSwapchain=%p, old=%ux%u, new=%ux%u, count=%llu). Releasing backbuffer & shmem references prior to resize.",
                 static_cast<void*>(oldSwapchain),
                 m_width,
                 m_height,
                 newWidth,
                 newHeight,
                 static_cast<unsigned long long>(m_resizePreReleaseCount.load(std::memory_order_relaxed)));
    if (m_isInitialized) {
        CleanupLocked();
    }
}

void FrameCapturer::SetSwapchainImages(const std::vector<VkImage>& images) {
    std::lock_guard<std::mutex> lock(m_capturerMutex);
    m_swapchainImages = images;
    HON_LOG_INFO("Cached %zu swapchain VkImage handles.", m_swapchainImages.size());
}

void FrameCapturer::RecordCopyCommandBuffer(uint32_t slotIndex, VkImage swapchainImage) {
    if (slotIndex >= m_slots.size()) {
        HON_LOG_ERROR("RecordCopyCommandBuffer: slotIndex %u exceeds slot count %zu", slotIndex, m_slots.size());
        return;
    }

    StagingSlot& slot = m_slots[slotIndex];
    if (slot.cmdBuffer == VK_NULL_HANDLE || !m_deviceDispatch.BeginCommandBuffer) {
        HON_LOG_ERROR("RecordCopyCommandBuffer: Invalid command buffer or null BeginCommandBuffer pointer!");
        return;
    }

    VkCommandBufferBeginInfo beginInfo = { VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO };
    beginInfo.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
    m_deviceDispatch.BeginCommandBuffer(slot.cmdBuffer, &beginInfo);

    // 1. Pipeline Barrier: Transition swapchain image from PRESENT_SRC_KHR to TRANSFER_SRC_OPTIMAL
    VkImageMemoryBarrier barrierToSrc = { VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER };
    barrierToSrc.srcAccessMask = VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT | VK_ACCESS_MEMORY_READ_BIT;
    barrierToSrc.dstAccessMask = VK_ACCESS_TRANSFER_READ_BIT;
    barrierToSrc.oldLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;
    barrierToSrc.newLayout = VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL;
    barrierToSrc.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    barrierToSrc.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    barrierToSrc.image = swapchainImage;
    barrierToSrc.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    barrierToSrc.subresourceRange.baseMipLevel = 0;
    barrierToSrc.subresourceRange.levelCount = 1;
    barrierToSrc.subresourceRange.baseArrayLayer = 0;
    barrierToSrc.subresourceRange.layerCount = 1;

    m_deviceDispatch.CmdPipelineBarrier(
        slot.cmdBuffer,
        VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT | VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT,
        VK_PIPELINE_STAGE_TRANSFER_BIT,
        0,
        0, nullptr,
        0, nullptr,
        1, &barrierToSrc);

    // 2. Perform DMA Copy from Image to Target Buffer (Tier 0: Direct GPU DMA to Shared Memory; Tier 1: Host Staging Buffer)
    VkBuffer targetBuffer = (m_activeTier == CAPTURE_TIER_0_GPU_DMA) ? m_importedBuffer : slot.buffer;
    VkDeviceSize targetOffset = (m_activeTier == CAPTURE_TIER_0_GPU_DMA)
        ? (static_cast<VkDeviceSize>(slotIndex) * m_slotStrideBytes + sizeof(HONCAP_FRAME_HEADER))
        : 0;

    VkBufferImageCopy copyRegion = {};
    copyRegion.bufferOffset = targetOffset;
    copyRegion.bufferRowLength = m_width;
    copyRegion.bufferImageHeight = m_height;
    copyRegion.imageSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    copyRegion.imageSubresource.mipLevel = 0;
    copyRegion.imageSubresource.baseArrayLayer = 0;
    copyRegion.imageSubresource.layerCount = 1;
    copyRegion.imageOffset = { 0, 0, 0 };
    copyRegion.imageExtent = { m_width, m_height, 1 };

    m_deviceDispatch.CmdCopyImageToBuffer(
        slot.cmdBuffer,
        swapchainImage,
        VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
        targetBuffer,
        1,
        &copyRegion);

    // 3. Pipeline Barrier: Transition swapchain image back to PRESENT_SRC_KHR
    VkImageMemoryBarrier barrierToPresent = barrierToSrc;
    barrierToPresent.srcAccessMask = VK_ACCESS_TRANSFER_READ_BIT;
    barrierToPresent.dstAccessMask = VK_ACCESS_MEMORY_READ_BIT;
    barrierToPresent.oldLayout = VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL;
    barrierToPresent.newLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;

    m_deviceDispatch.CmdPipelineBarrier(
        slot.cmdBuffer,
        VK_PIPELINE_STAGE_TRANSFER_BIT,
        VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT,
        0,
        0, nullptr,
        0, nullptr,
        1, &barrierToPresent);

    m_deviceDispatch.EndCommandBuffer(slot.cmdBuffer);
}

bool FrameCapturer::CapturePresentFrame(
    VkQueue queue,
    uint32_t imageIndex,
    uint32_t waitSemaphoreCount,
    const VkSemaphore* pWaitSemaphores,
    VkSemaphore* pOutPresentWaitSemaphore)
{
    std::lock_guard<std::mutex> lock(m_capturerMutex);

    if (pOutPresentWaitSemaphore) {
        *pOutPresentWaitSemaphore = VK_NULL_HANDLE;
    }

    if (!m_isInitialized) {
        return false;
    }

    // Tier 2: Idle Invariant - zero overhead when disabled
    if (m_activeTier == CAPTURE_TIER_DISABLED) {
        return true;
    }

    // #212 (Gem 3K): Periodic 5s host keep-alive mutex watchdog check
    if (!CheckHostKeepAliveMutex()) {
        return true;
    }

    if ((m_swapchainImages.empty() || imageIndex >= m_swapchainImages.size()) && m_swapchain != VK_NULL_HANDLE && m_deviceDispatch.GetSwapchainImagesKHR) {
        uint32_t count = 0;
        if (m_deviceDispatch.GetSwapchainImagesKHR(m_device, m_swapchain, &count, nullptr) == VK_SUCCESS && count > 0) {
            m_swapchainImages.resize(count);
            if (m_deviceDispatch.GetSwapchainImagesKHR(m_device, m_swapchain, &count, m_swapchainImages.data()) == VK_SUCCESS) {
                HON_LOG_INFO("CapturePresentFrame: Lazy-retrieved %u swapchain images.", count);
            } else {
                m_swapchainImages.clear();
            }
        }
    }

    if (imageIndex >= m_swapchainImages.size()) {
        return false;
    }

    if (m_slots.empty() || m_ringSlotCount == 0) {
        return false;
    }

    uint32_t currentSlot = m_currentSlotIndex % m_ringSlotCount;
    uint32_t prevSlot = (currentSlot + m_ringSlotCount - 1) % m_ringSlotCount;
    VkImage swapImage = m_swapchainImages[imageIndex];

    // 1. Backpressure Check: Ensure consumer has completed reading current slot (non-blocking 0ms check)
    if (m_slots[currentSlot].hDoneEvent != NULL) {
        DWORD waitResult = WaitForSingleObject(m_slots[currentSlot].hDoneEvent, 0); // Non-blocking check
        if (waitResult == WAIT_TIMEOUT) {
            // Overwriting unread slot to maintain full engine framerate (1000 FPS)
        }
    }

    // 1.5 [L2-P0-06] Non-blocking check that background copy_thread has finished reading currentSlot's mapped buffer
    if (m_slots[currentSlot].asyncCopyInFlight.load(std::memory_order_acquire)) {
        HON_LOG_DEBUG("CapturePresentFrame: Slot %u async background copy still in flight; skipping readback without stalling.", currentSlot);
        return false;
    }

    // 2. #211 (Gem 3B): Non-blocking GPU fence check on current slot before resetting fence and re-recording
    if (m_slots[currentSlot].fence != VK_NULL_HANDLE) {
        if (m_deviceDispatch.GetFenceStatus) {
            VkResult fenceRes = m_deviceDispatch.GetFenceStatus(m_device, m_slots[currentSlot].fence);
            if (fenceRes == VK_NOT_READY) {
                HON_LOG_DEBUG("CapturePresentFrame: Slot %u fence VK_NOT_READY; skipping readback to avoid stalling QueuePresentKHR.", currentSlot);
                return false;
            }
        } else if (m_deviceDispatch.WaitForFences) {
            if (m_deviceDispatch.WaitForFences(m_device, 1, &m_slots[currentSlot].fence, VK_TRUE, 0) == VK_TIMEOUT) {
                return false;
            }
        }
    }
    if (m_deviceDispatch.ResetFences && m_slots[currentSlot].fence != VK_NULL_HANDLE) {
        m_deviceDispatch.ResetFences(m_device, 1, &m_slots[currentSlot].fence);
    }
    RecordCopyCommandBuffer(currentSlot, swapImage);

    // 3. Submit Copy to GPU Transfer Queue with proper semaphore synchronization
    VkSubmitInfo submitInfo = { VK_STRUCTURE_TYPE_SUBMIT_INFO };
    submitInfo.commandBufferCount = 1;
    submitInfo.pCommandBuffers = &m_slots[currentSlot].cmdBuffer;

    std::vector<VkPipelineStageFlags> waitDstStageMask;
    if (waitSemaphoreCount > 0 && pWaitSemaphores != nullptr) {
        waitDstStageMask.assign(waitSemaphoreCount, VK_PIPELINE_STAGE_TRANSFER_BIT);
        submitInfo.waitSemaphoreCount = waitSemaphoreCount;
        submitInfo.pWaitSemaphores = pWaitSemaphores;
        submitInfo.pWaitDstStageMask = waitDstStageMask.data();
    }

    if (m_slots[currentSlot].copyCompleteSemaphore != VK_NULL_HANDLE) {
        submitInfo.signalSemaphoreCount = 1;
        submitInfo.pSignalSemaphores = &m_slots[currentSlot].copyCompleteSemaphore;
    }

    if (m_deviceDispatch.QueueSubmit) {
        VkResult submitRes = m_deviceDispatch.QueueSubmit(queue, 1, &submitInfo, m_slots[currentSlot].fence);
        if (submitRes != VK_SUCCESS) {
            HON_LOG_ERROR("QueueSubmit failed with code %d", submitRes);
            return false;
        }
        m_lastSubmittedFence = m_slots[currentSlot].fence;
    }

    if (pOutPresentWaitSemaphore && m_slots[currentSlot].copyCompleteSemaphore != VK_NULL_HANDLE) {
        *pOutPresentWaitSemaphore = m_slots[currentSlot].copyCompleteSemaphore;
    }

    m_slots[currentSlot].isPending = true;
    m_slots[currentSlot].frameIndex = m_totalFramesCaptured.load();
    m_slots[currentSlot].state = SLOT_STATE_GPU_COPYING;
    size_t currentSlotPixelOffset = currentSlot * m_slotStrideBytes + sizeof(HONCAP_FRAME_HEADER);
    m_pageTracker.MarkRegionGpuModified(currentSlotPixelOffset, m_frameSizeBytes);

    // 4. Synchronize Previous Frame (N-1) with Zero-Stall Non-Blocking Fence Polling (#211)
    if (m_slots[prevSlot].isPending) {
        bool prevFenceReady = true;
        if (m_slots[prevSlot].fence != VK_NULL_HANDLE) {
            if (m_deviceDispatch.GetFenceStatus) {
                prevFenceReady = (m_deviceDispatch.GetFenceStatus(m_device, m_slots[prevSlot].fence) == VK_SUCCESS);
            } else if (m_deviceDispatch.WaitForFences) {
                prevFenceReady = (m_deviceDispatch.WaitForFences(m_device, 1, &m_slots[prevSlot].fence, VK_TRUE, 0) == VK_SUCCESS);
            }
        }

        if (prevFenceReady) {
            m_slots[prevSlot].isPending = false;
            m_slots[prevSlot].state = SLOT_STATE_HOST_READY;

            // Populate Binary Frame Header
            HONCAP_FRAME_HEADER header = {};
            header.magic = HON_CAPTURE_MAGIC;
            header.version = HON_LAYER_VERSION;
            header.width = m_width;
            header.height = m_height;
            header.format = static_cast<uint32_t>(m_format);
            header.rowPitch = m_width * 4;
            header.frameIndex = m_slots[prevSlot].frameIndex;
            header.timestampNs = GetCurrentTimestampNs();
            header.dataSizeBytes = static_cast<uint32_t>(m_frameSizeBytes);
            header.bufferSlotIndex = prevSlot;
            header.flags = (m_activeTier == CAPTURE_TIER_0_GPU_DMA) ? 0x1 : 0x0;

            // Mirror Frame to Shared Memory IPC Map Slot
            if (m_pSharedMemoryView) {
                size_t slotOffset = prevSlot * m_slotStrideBytes;
                size_t prevSlotPixelOffset = slotOffset + sizeof(HONCAP_FRAME_HEADER);
                // [L2-P0-06] Atomically count & clear dirty GPU pages ONLY for prevSlot's byte range
                // so currentSlot's newly marked dirty bits are never wiped!
                size_t slotDirtyPages = m_pageTracker.CountDirtyGpuPagesInRangeAndClear(prevSlotPixelOffset, m_frameSizeBytes, true);
                bool isSlotGpuModified = (slotDirtyPages > 0);
                auto slotSpan = GetSlotSpan(prevSlot);
                if (!slotSpan.empty()) {
                    if (m_activeTier == CAPTURE_TIER_0_GPU_DMA) {
                        // Tier 0: Direct GPU DMA has already copied pixel data directly into shared memory.
                        std::memcpy(slotSpan.data(), &header, sizeof(HONCAP_FRAME_HEADER));
                        if (isSlotGpuModified && !m_isHostCoherent && m_deviceDispatch.InvalidateMappedMemoryRanges && m_importedMemory != VK_NULL_HANDLE) {
                            VkMappedMemoryRange range = { VK_STRUCTURE_TYPE_MAPPED_MEMORY_RANGE };
                            range.memory = m_importedMemory;
                            range.offset = prevSlotPixelOffset;
                            range.size = m_frameSizeBytes;
                            m_deviceDispatch.InvalidateMappedMemoryRanges(m_device, 1, &range);
                        }
                        if (m_slots[prevSlot].hReadyEvent != NULL) {
                            SetEvent(m_slots[prevSlot].hReadyEvent);
                        }
                        m_slots[prevSlot].state = SLOT_STATE_CONSUMING;
                    } else if (isSlotGpuModified) {
                        // [L2-P0-06] Tier 1: Offload multi-MB staging->shmem copy + ready event signaling to background copy_thread
                        // so QueuePresentKHR performs zero full-frame memcpy!
                        auto srcSpan = m_slots[prevSlot].GetMappedSpan(m_frameSizeBytes);
                        if (!srcSpan.empty() && slotSpan.size() >= sizeof(HONCAP_FRAME_HEADER) + m_frameSizeBytes) {
                            bool enqueued = m_obsShmemPingPong.EnqueueCompletedStage(
                                static_cast<uint32_t>(prevSlot % NUM_STAGE_BUFFERS),
                                srcSpan.data(),
                                static_cast<size_t>(m_width) * 4ULL,
                                header.frameIndex,
                                slotSpan.data(),
                                &header,
                                m_frameSizeBytes,
                                m_slots[prevSlot].hReadyEvent,
                                &m_slots[prevSlot].asyncCopyInFlight);
                            if (enqueued) {
                                m_slots[prevSlot].state = SLOT_STATE_CONSUMING;
                            }
                        }
                    }
                }
            }
        }
    }

    uint64_t capturedCount = m_totalFramesCaptured.fetch_add(1) + 1;
    m_stagingAllocator.TickFrame(capturedCount);
    if (capturedCount == 1 ||
        (m_width != m_lastLoggedWidth || m_height != m_lastLoggedHeight || static_cast<uint32_t>(m_activeTier) != m_lastLoggedTier) ||
        (capturedCount % 36000 == 0)) {
        HON_LOG_INFO("FrameCapturer: Captured %llu frames (CurrentSlot=%u, Resolution=%ux%u, Tier=%u).",
                     capturedCount, currentSlot, m_width, m_height, static_cast<uint32_t>(m_activeTier));
        m_lastLoggedWidth = m_width;
        m_lastLoggedHeight = m_height;
        m_lastLoggedTier = static_cast<uint32_t>(m_activeTier);
    }

    m_currentSlotIndex = (currentSlot + 1) % m_ringSlotCount;

    return true;
}

void FrameCapturer::Cleanup() {
    if (IsLayerProcessTerminating()) {
        m_obsShmemPingPong.StopPipeline();
        m_device = VK_NULL_HANDLE;
        m_isInitialized = false;
        return;
    }
    std::lock_guard<std::mutex> lock(m_capturerMutex);
    CleanupLocked();
}

void FrameCapturer::CleanupLocked() {
    if (!m_isInitialized) return;
    if (IsLayerProcessTerminating()) {
        m_obsShmemPingPong.StopPipeline();
        m_device = VK_NULL_HANDLE;
        m_isInitialized = false;
        return;
    }

    HON_LOG_INFO("Cleaning up FrameCapturer resources...");

    // #210 (Gem 3A): Stop background copy_thread before unmapping shared memory or staging buffers
    m_obsShmemPingPong.StopPipeline();
    m_lastSubmittedFence = VK_NULL_HANDLE;

    if (m_device != VK_NULL_HANDLE) {
        // Fence-only synchronization per staging slot (250ms cap) instead of vkDeviceWaitIdle
        // so teardown never races DXVK's background dxvk-submit thread on a shared VkDevice.
        for (uint32_t i = 0; i < m_slots.size(); i++) {
            StagingSlot& slot = m_slots[i];
            if (slot.fence != VK_NULL_HANDLE) {
                if (slot.isPending && m_deviceDispatch.WaitForFences) {
                    m_deviceDispatch.WaitForFences(m_device, 1, &slot.fence, VK_TRUE, 250000000ULL);
                    slot.isPending = false;
                }
                m_deviceDispatch.DestroyFence(m_device, slot.fence, nullptr);
            }
            if (slot.copyCompleteSemaphore != VK_NULL_HANDLE) {
                if (m_deviceDispatch.DestroySemaphore) {
                    m_deviceDispatch.DestroySemaphore(m_device, slot.copyCompleteSemaphore, nullptr);
                }
                slot.copyCompleteSemaphore = VK_NULL_HANDLE;
            }
            if (slot.cmdBuffer != VK_NULL_HANDLE && m_commandPool != VK_NULL_HANDLE) {
                m_deviceDispatch.FreeCommandBuffers(m_device, m_commandPool, 1, &slot.cmdBuffer);
            }
            if (slot.mappedHostPtr != nullptr) {
                m_deviceDispatch.UnmapMemory(m_device, slot.memory);
                slot.mappedHostPtr = nullptr;
            }
            if (slot.buffer != VK_NULL_HANDLE) {
                m_deviceDispatch.DestroyBuffer(m_device, slot.buffer, nullptr);
            }
            if (slot.memory != VK_NULL_HANDLE) {
                m_deviceDispatch.FreeMemory(m_device, slot.memory, nullptr);
            }
            if (slot.hReadyEvent != NULL) {
                CloseHandle(slot.hReadyEvent);
            }
            if (slot.hDoneEvent != NULL) {
                CloseHandle(slot.hDoneEvent);
            }
        }
        m_slots.clear();

        // Clean up Tier 0 imported buffer and memory
        if (m_importedBuffer != VK_NULL_HANDLE) {
            if (m_deviceDispatch.DestroyBuffer) {
                m_deviceDispatch.DestroyBuffer(m_device, m_importedBuffer, nullptr);
            }
            m_importedBuffer = VK_NULL_HANDLE;
        }
        if (m_importedMemory != VK_NULL_HANDLE) {
            if (m_deviceDispatch.FreeMemory) {
                m_deviceDispatch.FreeMemory(m_device, m_importedMemory, nullptr);
            }
            m_importedMemory = VK_NULL_HANDLE;
        }

        if (m_commandPool != VK_NULL_HANDLE) {
            m_deviceDispatch.DestroyCommandPool(m_device, m_commandPool, nullptr);
            m_commandPool = VK_NULL_HANDLE;
        }
    }

    if (m_pSharedMemoryView != nullptr) {
        UnmapViewOfFile(m_pSharedMemoryView);
        m_pSharedMemoryView = nullptr;
    }

    if (m_hSharedMemoryMap != NULL) {
        CloseHandle(m_hSharedMemoryMap);
        m_hSharedMemoryMap = NULL;
    }

    m_swapchainImages.clear();
    m_swapchain = VK_NULL_HANDLE;
    m_device = VK_NULL_HANDLE;
    m_activeTier = CAPTURE_TIER_DISABLED;
    m_isInitialized = false;
    m_lastLoggedWidth = 0;
    m_lastLoggedHeight = 0;
    m_lastLoggedTier = 0xFFFFFFFF;

    remove("artifacts/captures/honcap_active.json");
    HON_LOG_INFO("FrameCapturer destroyed successfully.");
}
