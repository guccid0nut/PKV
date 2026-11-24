// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#pragma once

#include "layer_manifest.h"
#include "page_bitmask_tracker.h"
#include <bit>
#include <queue>

// ============================================================================
// (#203) Headless RenderDoc Dynamic Capture Bridge (eRENDERDOC_API_Version_1_6_0)
// Inspired by shadPS4/src/video_core/renderer_vulkan/vk_instance.cpp:178-219 & renderdoc.cpp:25-171
// ============================================================================
static constexpr int eRENDERDOC_API_Version_1_6_0 = 10600;

enum class RenderDocCaptureState : uint8_t {
    Idle = 0,
    Triggered = 1,
    InProgress = 2
};

struct MinimalRenderDocApi160 {
    void* GetAPIVersion;
    void* SetCaptureOptionU32;
    void* SetCaptureOptionF32;
    void* GetCaptureOptionU32;
    void* GetCaptureOptionF32;
    void* SetFocusToggleKeys;
    void* SetCaptureKeys;
    void* GetOverlayBits;
    void* MaskOverlayBits;
    void (WINAPI* UnloadCrashHandler)();
    void (WINAPI* SetCaptureFilePathTemplate)(const char* pathtemplate);
    const char* (WINAPI* GetCaptureFilePathTemplate)();
    uint32_t (WINAPI* GetNumCaptures)();
    uint32_t (WINAPI* GetCapture)(uint32_t idx, char* filename, uint32_t* pathlength, uint64_t* timestamp);
    void (WINAPI* TriggerCapture)();
    uint32_t (WINAPI* IsTargetControlConnected)();
    uint32_t (WINAPI* LaunchReplayUI)(uint32_t connectTargetControl, const char* cmdline);
    void (WINAPI* SetActiveWindow)(void* device, void* wndHandle);
    void (WINAPI* StartFrameCapture)(void* device, void* wndHandle);
    uint32_t (WINAPI* IsFrameCapturing)();
    uint32_t (WINAPI* EndFrameCapture)(void* device, void* wndHandle);
};

using pRENDERDOC_GetAPI = int(WINAPI*)(int version, void** outAPIPointers);

class HeadlessRenderDocCaptureController {
public:
    static std::string DiscoverRenderDocDllFromRegistry();
    bool TryLoadAndInitialize(const char* captureOutputTemplate = "userdata/logs/captures/kongor_vk");
    void TriggerNextFrameCapture();
    void OnPresentBegin(void* device = nullptr, void* wndHandle = nullptr);
    void OnPresentEnd(void* device = nullptr, void* wndHandle = nullptr);

    RenderDocCaptureState GetState() const noexcept { return m_state; }
    bool WasUnloadCrashHandlerCalled() const noexcept { return m_unloadCrashHandlerCalled; }

private:
    RenderDocCaptureState m_state = RenderDocCaptureState::Idle;
    MinimalRenderDocApi160* m_rdocApi = nullptr;
    bool m_initialized = false;
    bool m_unloadCrashHandlerCalled = false;
};

// ============================================================================
// (#205) Three-Tier Staging Buffer Pool with Idle-Frame GC
// Inspired by shadPS4/src/video_core/buffer_cache/buffer_cache.cpp:542-932 & stream_buffer.cpp
// ============================================================================
class ThreeTierStagingBufferAllocator {
public:
    static constexpr uint64_t TIER1_RING_CAPACITY_BYTES = 4ULL * 1024ULL * 1024ULL;
    static constexpr uint64_t TIER1_MAX_SINGLE_ALLOC    = 256ULL * 1024ULL;
    static constexpr uint64_t MAX_IDLE_FRAMES           = 64ULL;

    struct PooledStagingEntry {
        uint64_t bufferId = 0;
        uint64_t capacityBytes = 0;
        uint64_t busyUntilTimelineTick = 0;
        uint64_t lastUsedEpoch = 0;
        bool inUse = false;
    };

    uint64_t AllocateStagingSlice(uint64_t sizeBytes, uint64_t submitTimelineTick, bool* outUsedTier1Ring = nullptr);
    size_t TickFrame(uint64_t completedTimelineTick);
    size_t GetTier2PoolCount() const noexcept { return m_tier2Pool.size(); }
    size_t GetTotalEvictedCount() const noexcept { return m_totalEvicted; }

private:
    uint64_t m_ringWriteOffset = 0;
    uint64_t m_currentEpoch = 0;
    uint64_t m_completedTick = 0;
    uint64_t m_nextBufferId = 1;
    size_t m_totalEvicted = 0;
    std::vector<PooledStagingEntry> m_tier2Pool;
};

// ============================================================================
// (#206, [L2-P0-06]) Two-Level 4KB Page Bitmask Memory Tracker (shared header)
// ============================================================================
using TwoLevelPageBitmaskTracker = hon_capture::TwoLevelPageBitmaskTracker;

// ============================================================================
// (#207) 8-Slot Download Buffer Ring & GPU Fault-Buffer Bitmask Scanner
// Inspired by shadPS4/src/video_core/buffer_cache/buffer_cache.cpp:250-540
// ============================================================================
class GpuFaultBufferRing {
public:
    static constexpr size_t DOWNLOAD_RING_SLOTS = 8;
    static constexpr uint64_t FAULT_PAGE_SIZE   = 4096ULL;

    struct RegisteredBufferSpan {
        uint64_t bufferId = 0;
        uint64_t baseVa = 0;
        uint64_t sizeBytes = 0;
        char debugName[64] = {0};
    };

    void RegisterBuffer(uint64_t bufferId, uint64_t baseVa, uint64_t sizeBytes, const char* debugName);
    bool TryAcquireDownloadSlot(uint64_t completedTick, uint64_t submitTick, size_t* outSlotIndex);
    size_t ScanFaultWordsAndLog(const uint64_t* faultWords, size_t wordCount, uint64_t baseVa = 0x10000000ULL) const;

private:
    uint64_t m_slotBusyUntilTick[DOWNLOAD_RING_SLOTS] = {0};
    size_t m_nextCursor = 0;
    std::vector<RegisteredBufferSpan> m_buffers;
};

// ============================================================================
// (#210) OBS AVX 32-Byte Aligned 3-Stage GPU Readback to 2-Slot Shared-Memory Ping-Pong
// Inspired by obs-studio/plugins/win-capture/graphics-hook/d3d11-capture.cpp & vk-capture.cpp
// ============================================================================
static constexpr size_t NUM_STAGE_BUFFERS  = 3;
static constexpr size_t NUM_SHMEM_TEXTURES = 2;

static constexpr size_t Align32(size_t size) noexcept {
    return (size + 31ULL) & ~31ULL;
}

class ObsAvxThreeStageToTwoSlotShmemPipeline {
public:
    ObsAvxThreeStageToTwoSlotShmemPipeline() noexcept;
    ~ObsAvxThreeStageToTwoSlotShmemPipeline();

    bool StartPipeline(uint32_t width, uint32_t height, void* externalShmemBase = nullptr, size_t externalShmemBytes = 0);
    void StopPipeline();
    bool EnqueueCompletedStage(
        uint32_t stageIndex,
        const uint8_t* stageMappedPtr,
        size_t rowPitchBytes,
        uint64_t frameIndex,
        uint8_t* externalRingSlotBasePtr = nullptr,
        const void* externalRingHeader64 = nullptr,
        size_t externalRingPixelBytes = 0,
        HANDLE externalReadyEvent = NULL,
        std::atomic<bool>* inFlightStageGuard = nullptr);
    bool ReadLatestShmemSlot(std::vector<uint8_t>& outPixels, int32_t* outSlotIndex = nullptr, uint64_t* outFrameIndex = nullptr);

    int32_t GetLastTexIndex() const noexcept { return last_tex.load(std::memory_order_acquire); }
    size_t GetAlignedRowPitch() const noexcept { return m_alignedRowPitch; }
    uint64_t GetCompletedCopiesCount() const noexcept { return m_completedCopies.load(std::memory_order_relaxed); }

private:
    void CopyThreadMain();

    uint32_t m_width = 0;
    uint32_t m_height = 0;
    size_t m_alignedRowPitch = 0;
    size_t m_slotPayloadBytes = 0;

    std::vector<uint8_t> m_stageBuffers[NUM_STAGE_BUFFERS];
    const uint8_t* m_stageSourcePtr[NUM_STAGE_BUFFERS] = {nullptr, nullptr, nullptr};
    size_t m_stageSourceRowPitch[NUM_STAGE_BUFFERS] = {0, 0, 0};
    uint64_t m_stageFrameIndex[NUM_STAGE_BUFFERS] = {0};
    uint8_t* m_stageExtRingSlotPtr[NUM_STAGE_BUFFERS] = {nullptr, nullptr, nullptr};
    uint8_t m_stageExtHeader64[NUM_STAGE_BUFFERS][64] = {};
    size_t m_stageExtPixelBytes[NUM_STAGE_BUFFERS] = {0, 0, 0};
    HANDLE m_stageExtReadyEvent[NUM_STAGE_BUFFERS] = {NULL, NULL, NULL};
    std::atomic<bool>* m_stageInFlightGuard[NUM_STAGE_BUFFERS] = {nullptr, nullptr, nullptr};
    bool m_stageReady[NUM_STAGE_BUFFERS] = {false};

    std::vector<uint8_t> m_ownedShmemStorage;
    uint8_t* shmem_textures[NUM_SHMEM_TEXTURES] = {nullptr, nullptr};
    uint64_t m_shmemFrameIndex[NUM_SHMEM_TEXTURES] = {0};
    std::atomic<uint64_t> m_shmemSeq[NUM_SHMEM_TEXTURES] = {0, 0};
    std::atomic<int32_t> last_tex{-1};
    std::mutex shmem_mutexes[NUM_SHMEM_TEXTURES];

    std::mutex m_queueMutex;
    HANDLE copy_event = NULL;
    HANDLE stop_event = NULL;
    std::thread copy_thread;
    std::atomic<bool> m_running{false};
    std::atomic<uint64_t> m_completedCopies{0};
};

// ============================================================================
// (#213) OBS Nanosecond QPC Frame-Pacing Drop Throttle (frame_ready)
// Inspired by obs-studio/plugins/win-capture/graphics-hook/graphics-hook.h:frame_ready
// ============================================================================
class ObsNanosecondFramePacer {
public:
    static uint64_t QueryCurrentQpcNs() noexcept {
        LARGE_INTEGER freq = {};
        LARGE_INTEGER counter = {};
        if (!QueryPerformanceFrequency(&freq) || freq.QuadPart <= 0 || !QueryPerformanceCounter(&counter)) {
            return GetTickCount64() * 1000000ULL;
        }
        const uint64_t seconds = static_cast<uint64_t>(counter.QuadPart) / static_cast<uint64_t>(freq.QuadPart);
        const uint64_t remainder = static_cast<uint64_t>(counter.QuadPart) % static_cast<uint64_t>(freq.QuadPart);
        return (seconds * 1000000000ULL) + ((remainder * 1000000000ULL) / static_cast<uint64_t>(freq.QuadPart));
    }

    bool frame_ready(uint64_t interval_ns) noexcept {
        return frame_ready(QueryCurrentQpcNs(), interval_ns);
    }

    bool frame_ready(uint64_t current_time_ns, uint64_t interval_ns) noexcept {
        if (interval_ns == 0ULL) {
            ++m_acceptedFrames;
            return true;
        }
        if (m_lastFrameTimeNs == 0ULL) {
            m_lastFrameTimeNs = current_time_ns;
            ++m_acceptedFrames;
            return true;
        }
        if (current_time_ns < m_lastFrameTimeNs) {
            ++m_droppedFrames;
            return false;
        }
        const uint64_t elapsed = current_time_ns - m_lastFrameTimeNs;
        if (elapsed < interval_ns) {
            ++m_droppedFrames;
            return false;
        }
        m_lastFrameTimeNs = (elapsed >= interval_ns * 2ULL)
            ? current_time_ns
            : (m_lastFrameTimeNs + interval_ns);
        ++m_acceptedFrames;
        return true;
    }

    void Reset() noexcept {
        m_lastFrameTimeNs = 0ULL;
        m_acceptedFrames = 0ULL;
        m_droppedFrames = 0ULL;
    }

    uint64_t GetLastFrameTimeNs() const noexcept { return m_lastFrameTimeNs; }
    uint64_t GetAcceptedFrames() const noexcept { return m_acceptedFrames; }
    uint64_t GetDroppedFrames() const noexcept { return m_droppedFrames; }

private:
    uint64_t m_lastFrameTimeNs = 0ULL;
    uint64_t m_acceptedFrames = 0ULL;
    uint64_t m_droppedFrames = 0ULL;
};

#pragma pack(push, 1)
struct HONCAP_FRAME_HEADER {
    uint32_t magic;               // 0x484F4E43 ("HONC" in Little Endian)
    uint32_t version;             // Protocol Version (1)
    uint32_t width;               // Frame Width (e.g. 1920 or 3840)
    uint32_t height;              // Frame Height (e.g. 1080 or 2160)
    uint32_t format;              // VK_FORMAT_B8G8R8A8_UNORM = 44
    uint32_t rowPitch;            // Row stride in bytes (width * 4)
    uint64_t frameIndex;          // Monotonically increasing sequence number
    uint64_t timestampNs;         // High-resolution timestamp in nanoseconds
    uint32_t dataSizeBytes;       // Payload size of raw pixel data (width * height * 4)
    uint32_t bufferSlotIndex;     // Ring buffer slot index (0, 1, 2)
    uint32_t flags;               // Bit 0: Keyframe / Scene cut, Bit 1: Last frame
    uint8_t  reserved[12];        // Reserved for future extensions (64-byte alignment)
    // C++23 Hardware-accelerated Endian Swapping
    static constexpr uint32_t SwapBytes32(uint32_t val) noexcept {
        return std::byteswap(val);
    }
    static constexpr uint64_t SwapBytes64(uint64_t val) noexcept {
        return std::byteswap(val);
    }
};
#pragma pack(pop)

static_assert(sizeof(HONCAP_FRAME_HEADER) == 64, "HONCAP_FRAME_HEADER must be exactly 64 bytes");

enum SlotState : uint32_t {
    SLOT_STATE_FREE = 0,
    SLOT_STATE_GPU_COPYING = 1,
    SLOT_STATE_HOST_READY = 2,
    SLOT_STATE_CONSUMING = 3
};

struct StagingSlot {
    VkBuffer buffer;
    VkDeviceMemory memory;
    void* mappedHostPtr;
    VkCommandBuffer cmdBuffer;
    VkFence fence;
    VkSemaphore copyCompleteSemaphore;
    HANDLE hReadyEvent;
    HANDLE hDoneEvent;
    bool isPending;
    uint64_t frameIndex;
    SlotState state;
    HANDLE externalNtHandle;
    std::atomic<bool> asyncCopyInFlight{false};

    StagingSlot() noexcept
        : buffer(VK_NULL_HANDLE)
        , memory(VK_NULL_HANDLE)
        , mappedHostPtr(nullptr)
        , cmdBuffer(VK_NULL_HANDLE)
        , fence(VK_NULL_HANDLE)
        , copyCompleteSemaphore(VK_NULL_HANDLE)
        , hReadyEvent(NULL)
        , hDoneEvent(NULL)
        , isPending(false)
        , frameIndex(0)
        , state(SLOT_STATE_FREE)
        , externalNtHandle(NULL)
        , asyncCopyInFlight(false)
    {}

    StagingSlot(const StagingSlot& other) noexcept
        : buffer(other.buffer)
        , memory(other.memory)
        , mappedHostPtr(other.mappedHostPtr)
        , cmdBuffer(other.cmdBuffer)
        , fence(other.fence)
        , copyCompleteSemaphore(other.copyCompleteSemaphore)
        , hReadyEvent(other.hReadyEvent)
        , hDoneEvent(other.hDoneEvent)
        , isPending(other.isPending)
        , frameIndex(other.frameIndex)
        , state(other.state)
        , externalNtHandle(other.externalNtHandle)
        , asyncCopyInFlight(other.asyncCopyInFlight.load(std::memory_order_relaxed))
    {}

    StagingSlot& operator=(const StagingSlot& other) noexcept {
        if (this != &other) {
            buffer = other.buffer;
            memory = other.memory;
            mappedHostPtr = other.mappedHostPtr;
            cmdBuffer = other.cmdBuffer;
            fence = other.fence;
            copyCompleteSemaphore = other.copyCompleteSemaphore;
            hReadyEvent = other.hReadyEvent;
            hDoneEvent = other.hDoneEvent;
            isPending = other.isPending;
            frameIndex = other.frameIndex;
            state = other.state;
            externalNtHandle = other.externalNtHandle;
            asyncCopyInFlight.store(other.asyncCopyInFlight.load(std::memory_order_relaxed), std::memory_order_relaxed);
        }
        return *this;
    }

    // C++23 Safe Memory View
    std::span<uint8_t> GetMappedSpan(size_t size) const noexcept {
        if (!mappedHostPtr || size == 0) return {};
        return std::span<uint8_t>(static_cast<uint8_t*>(mappedHostPtr), size);
    }
};

enum CaptureTier : uint32_t {
    CAPTURE_TIER_DISABLED = 0,
    CAPTURE_TIER_0_GPU_DMA = 1,
    CAPTURE_TIER_1_STAGING_FALLBACK = 2
};

class FrameCapturer {
public:
    FrameCapturer();
    ~FrameCapturer();

    bool Initialize(
        VkDevice device,
        VkPhysicalDevice physicalDevice,
        VkQueue queue,
        uint32_t queueFamilyIndex,
        const VkSwapchainCreateInfoKHR* pCreateInfo,
        VkSwapchainKHR swapchain,
        const LayerDeviceDispatchTable& deviceDispatch,
        const LayerInstanceDispatchTable& instanceDispatch);

    void SetSwapchainImages(const std::vector<VkImage>& images);
    bool CapturePresentFrame(
        VkQueue queue,
        uint32_t imageIndex,
        uint32_t waitSemaphoreCount = 0,
        const VkSemaphore* pWaitSemaphores = nullptr,
        VkSemaphore* pOutPresentWaitSemaphore = nullptr);
    void Cleanup();

    bool IsInitialized() const { return m_isInitialized; }
    CaptureTier GetActiveTier() const { return m_activeTier; }
    VkSwapchainKHR GetSwapchain() const { return m_swapchain; }
    uint32_t GetWidth() const { return m_width; }
    uint32_t GetHeight() const { return m_height; }
    VkFormat GetFormat() const { return m_format; }
    uint64_t GetTotalFramesCaptured() const { return m_totalFramesCaptured.load(); }
    VkImage GetSwapchainImage(uint32_t index) const { return index < m_swapchainImages.size() ? m_swapchainImages[index] : VK_NULL_HANDLE; }

    HeadlessRenderDocCaptureController& GetRenderDocController() noexcept { return m_renderDocController; }
    ThreeTierStagingBufferAllocator& GetStagingPoolAllocator() noexcept { return m_stagingAllocator; }
    TwoLevelPageBitmaskTracker& GetPageTracker() noexcept { return m_pageTracker; }
    GpuFaultBufferRing& GetFaultRing() noexcept { return m_faultRing; }
    ObsAvxThreeStageToTwoSlotShmemPipeline& GetObsShmemPingPong() noexcept { return m_obsShmemPingPong; }
    ObsNanosecondFramePacer& GetFramePacer() noexcept { return m_framePacer; }

    // (#212) OBS 5-second Keepalive Mutex Watchdog (Local\KongorCaptureKeepAlive_<PID>)
    static constexpr uint64_t KEEPALIVE_CHECK_INTERVAL_NS = 5000000000ULL;
    bool CheckHostKeepAliveMutex(uint64_t currentTimestampNs);
    bool CheckHostKeepAliveMutex() { return CheckHostKeepAliveMutex(GetCurrentTimestampNs()); }
    bool IsGpuStagingDisarmedByWatchdog() const noexcept { return m_gpuStagingDisarmedByWatchdog.load(std::memory_order_acquire); }

    // (#213) Nanosecond Frame-Pacing Interval
    void SetTargetCaptureIntervalNs(uint64_t intervalNs) noexcept { m_targetCaptureIntervalNs = intervalNs; }
    uint64_t GetTargetCaptureIntervalNs() const noexcept { return m_targetCaptureIntervalNs; }
    bool ShouldCaptureFrameNow() noexcept { return m_framePacer.frame_ready(GetCurrentTimestampNs(), m_targetCaptureIntervalNs); }

    // (#214) Pre-Resize / Pre-Recreation Backbuffer & Shared-Memory Notifier
    void OnBeforeSwapchainResizeOrRecreate(VkSwapchainKHR oldSwapchain, uint32_t newWidth = 0, uint32_t newHeight = 0);
    uint64_t GetSwapchainResizePreReleaseCount() const noexcept { return m_resizePreReleaseCount.load(std::memory_order_relaxed); }
    VkFence GetLastSubmittedFence() const noexcept { return m_lastSubmittedFence; }

    // C++23 Safe Memory Buffer Views
    std::span<uint8_t> GetSharedMemorySpan() const noexcept {
        if (!m_pSharedMemoryView || m_totalSharedMemBytes == 0) return {};
        return std::span<uint8_t>(static_cast<uint8_t*>(m_pSharedMemoryView), m_totalSharedMemBytes);
    }

    std::span<uint8_t> GetSlotSpan(uint32_t slotIndex) const noexcept {
        if (!m_pSharedMemoryView || slotIndex >= m_ringSlotCount || m_slotStrideBytes == 0) return {};
        size_t offset = slotIndex * m_slotStrideBytes;
        if (offset + m_slotStrideBytes > m_totalSharedMemBytes) return {};
        return std::span<uint8_t>(static_cast<uint8_t*>(m_pSharedMemoryView) + offset, m_slotStrideBytes);
    }

    std::span<uint8_t> GetSlotPixelSpan(uint32_t slotIndex) const noexcept {
        auto slotSpan = GetSlotSpan(slotIndex);
        if (slotSpan.size() < sizeof(HONCAP_FRAME_HEADER) + m_frameSizeBytes) return {};
        return slotSpan.subspan(sizeof(HONCAP_FRAME_HEADER), m_frameSizeBytes);
    }

private:
    void CleanupLocked();
    std::expected<uint32_t, VkResult> FindMemoryType(uint32_t typeFilter, VkMemoryPropertyFlags properties);
    std::expected<void, VkResult> CreateCommandResourcesAndSyncEvents();
    std::expected<void, VkResult> TryInitializeTier0();
    std::expected<void, VkResult> InitializeTier1StagingFallback();
    std::expected<void, VkResult> CreateStagingBuffers();
    std::expected<void, VkResult> SetupSharedMemoryIpc();
    void RecordCopyCommandBuffer(uint32_t slotIndex, VkImage swapchainImage);
    uint64_t GetCurrentTimestampNs();

private:
    VkDevice m_device;
    VkPhysicalDevice m_physicalDevice;
    VkQueue m_queue;
    uint32_t m_queueFamilyIndex;

    LayerDeviceDispatchTable m_deviceDispatch;
    LayerInstanceDispatchTable m_instanceDispatch;

    VkSwapchainKHR m_swapchain;
    uint32_t m_width;
    uint32_t m_height;
    VkFormat m_format;
    uint32_t m_imageCount;
    std::vector<VkImage> m_swapchainImages;

    VkCommandPool m_commandPool;
    std::vector<StagingSlot> m_slots;
    uint32_t m_ringSlotCount;
    uint32_t m_currentSlotIndex;
    std::atomic<uint64_t> m_totalFramesCaptured;

    HANDLE m_hSharedMemoryMap;
    void* m_pSharedMemoryView;
    size_t m_frameSizeBytes;
    size_t m_slotStrideBytes;
    size_t m_totalSharedMemBytes;

    CaptureTier m_activeTier;
    VkBuffer m_importedBuffer;
    VkDeviceMemory m_importedMemory;
    bool m_isHostCoherent;

    DWORD m_processId;
    bool m_isInitialized;
    mutable std::mutex m_capturerMutex;
    LARGE_INTEGER m_qpcFrequency;

    HeadlessRenderDocCaptureController m_renderDocController;
    ThreeTierStagingBufferAllocator m_stagingAllocator;
    TwoLevelPageBitmaskTracker m_pageTracker;
    GpuFaultBufferRing m_faultRing;
    ObsAvxThreeStageToTwoSlotShmemPipeline m_obsShmemPingPong;
    ObsNanosecondFramePacer m_framePacer;
    uint64_t m_targetCaptureIntervalNs = 0ULL;
    uint64_t m_lastKeepAliveCheckNs = 0ULL;
    bool m_hostKeepAliveArmed = false;
    std::atomic<bool> m_gpuStagingDisarmedByWatchdog{false};
    std::atomic<uint64_t> m_resizePreReleaseCount{0};
    VkFence m_lastSubmittedFence = VK_NULL_HANDLE;
    uint32_t m_lastLoggedWidth = 0;
    uint32_t m_lastLoggedHeight = 0;
    uint32_t m_lastLoggedTier = 0xFFFFFFFF;
};

