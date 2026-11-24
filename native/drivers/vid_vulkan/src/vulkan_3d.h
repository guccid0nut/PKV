// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#pragma once
#ifndef VK_USE_PLATFORM_WIN32_KHR
#define VK_USE_PLATFORM_WIN32_KHR
#endif
#include <windows.h>
#include <vulkan/vulkan.h>
#include <cstdint>
#include <vector>
#include <unordered_map>
#include <mutex>
#include <atomic>
#include <string>

// =================================================================================================
// 3D Draw-Call, Batch Consolidation & Model/Shader Telemetry Subsystem (#257)
// Note: Actual D3D9 -> Vulkan 1.3 3D geometry staging and command submission is handled in-process
// by games/client/d3d9.dll (DXVK v3.0.2). Vulkan3D tracks draw-call batching, model lifecycles,
// and shader pair permutations with zero redundant GPU buffer allocations or UP memcpys.
// =================================================================================================

// Draw Call Types
enum EVulkan3DDrawType {
    VULKAN_3D_DRAW_INDEXED = 0,
    VULKAN_3D_DRAW_PRIMITIVE = 1,
    VULKAN_3D_DRAW_INDEXED_UP = 2,
    VULKAN_3D_DRAW_PRIMITIVE_UP = 3
};

// 3D Telemetry and Frame Statistics
struct Vulkan3DFrameStats {
    uint64_t drawCallsIndexed = 0;
    uint64_t drawCallsPrimitive = 0;
    uint64_t drawCallsIndexedUP = 0;
    uint64_t drawCallsPrimitiveUP = 0;
    uint64_t totalDrawCalls = 0;
    uint64_t totalVertices = 0;
    uint64_t totalPrimitives = 0;
    uint64_t consolidatedBatches = 0;
    uint64_t activeModels = 0;
    uint64_t activeShaderPairs = 0;
};

// Registered Model Record
struct VulkanStagedModel {
    int modelId = -1;
    void* pModel = nullptr;
    uint64_t registrationTimestamp = 0;
};

// 3D Draw Call Record (for batch analysis and consolidation)
struct Vulkan3DDrawRecord {
    EVulkan3DDrawType drawType;
    uint32_t primitiveType;
    int32_t baseVertexIndex;
    uint32_t minVertexIndex;
    uint32_t numVertices;
    uint32_t startIndex;
    uint32_t primitiveCount;
    void* streamSource;
    uint32_t streamOffset;
    uint32_t streamStride;
    void* indexBuffer;
};

// =================================================================================================
// Vulkan3D Subsystem Manager
// =================================================================================================
class Vulkan3D {
public:
    static Vulkan3D& Get();

    bool Initialize(VkDevice device,
                    VkPhysicalDevice physicalDevice,
                    VkQueue graphicsQueue,
                    uint32_t graphicsQueueFamily);
    void Shutdown();

    // Frame Lifecycle
    void BeginFrame(uint64_t frameIndex);
    void EndFrame(uint64_t frameIndex);

    // 3D Draw Call Batching & Interception
    void RecordDrawIndexed(uint32_t primType, int32_t baseVertexIndex, uint32_t minVertexIndex,
                           uint32_t numVertices, uint32_t startIndex, uint32_t primCount,
                           void* streamSource, uint32_t streamOffset, uint32_t streamStride,
                           void* indexBuffer);

    void RecordDrawPrimitive(uint32_t primType, uint32_t startVertex, uint32_t primCount,
                             void* streamSource, uint32_t streamOffset, uint32_t streamStride);

    void RecordDrawIndexedUP(uint32_t primType, uint32_t minVertexIndex, uint32_t numVertices,
                             uint32_t primCount, const void* pIndexData, uint32_t indexFormat,
                             const void* pVertexData, uint32_t vertexStride);

    void RecordDrawPrimitiveUP(uint32_t primType, uint32_t primCount,
                               const void* pVertexData, uint32_t vertexStride);

    // Model & Shader Registration Hooks
    int OnRegisterModel(void* pModel, int assignedHandle);
    void OnUnregisterModel(void* pModel);

    int OnRegisterVertexShader(void* pShader, int assignedHandle);
    void OnUnregisterVertexShader(void* pShader);
    int OnRegisterPixelShader(void* pShader, int assignedHandle);
    void OnUnregisterPixelShader(void* pShader);
    void OnRegisterShaderPair(void* pVertexShader, void* pPixelShader);

    // Diagnostics & Inspection
    bool IsInitialized() const { return m_bInitialized; }
    const Vulkan3DFrameStats& GetLastFrameStats() const { return m_lastFrameStats; }
    const Vulkan3DFrameStats& GetCurrentFrameStats() const { return m_currentFrameStats; }
    size_t GetActiveModelCount() const;
    size_t GetActiveShaderPairCount() const;

private:
    Vulkan3D();
    ~Vulkan3D();

    Vulkan3D(const Vulkan3D&) = delete;
    Vulkan3D& operator=(const Vulkan3D&) = delete;

    VkDevice m_device = VK_NULL_HANDLE;
    VkPhysicalDevice m_physicalDevice = VK_NULL_HANDLE;
    VkQueue m_graphicsQueue = VK_NULL_HANDLE;
    uint32_t m_graphicsQueueFamily = 0;
    bool m_bInitialized = false;

    // Batch Consolidation State
    Vulkan3DDrawRecord m_lastDrawRecord = {};
    bool m_hasLastDraw = false;

    // Frame Statistics
    Vulkan3DFrameStats m_currentFrameStats;
    Vulkan3DFrameStats m_lastFrameStats;
    uint64_t m_currentFrameIndex = 0;

    // Model and Shader Permutation Caches
    mutable std::mutex m_registryMutex;
    std::unordered_map<void*, VulkanStagedModel> m_activeModels;
    std::unordered_map<void*, int> m_vertexShaders;
    std::unordered_map<void*, int> m_pixelShaders;
    std::vector<std::pair<void*, void*>> m_shaderPairs;
};
