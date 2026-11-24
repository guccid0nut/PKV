// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#include "vulkan_3d.h"
#include "kongor_tracy.h"
#include <cstdio>
#include <cstring>
#include <algorithm>

// External logger hook from vid_vulkan_spy.cpp
void VulkanLog(const char* format, ...);

#define VK3D_LOG(fmt, ...) VulkanLog("[VULKAN_3D] " fmt, ##__VA_ARGS__)

Vulkan3D& Vulkan3D::Get() {
    static Vulkan3D s_instance;
    return s_instance;
}

Vulkan3D::Vulkan3D() = default;

Vulkan3D::~Vulkan3D() {
    Shutdown();
}

bool Vulkan3D::Initialize(VkDevice device,
                          VkPhysicalDevice physicalDevice,
                          VkQueue graphicsQueue,
                          uint32_t graphicsQueueFamily) {
    if (m_bInitialized) return true;

    m_device = device;
    m_physicalDevice = physicalDevice;
    m_graphicsQueue = graphicsQueue;
    m_graphicsQueueFamily = graphicsQueueFamily;

    if (!m_device || !m_physicalDevice) {
        VK3D_LOG("ERROR: Cannot initialize Vulkan3D with null device or physical device!");
        return false;
    }

    m_bInitialized = true;
    VK3D_LOG("Vulkan3D Draw-Call & Model/Shader Telemetry Subsystem initialized (3D Vulkan translation handled by DXVK v3.0.2).");
    return true;
}

void Vulkan3D::Shutdown() {
    if (!m_bInitialized && !m_device) return;
    m_bInitialized = false;

    VK3D_LOG("Shutting down Vulkan3D subsystem...");
    m_device = VK_NULL_HANDLE;

    {
        std::lock_guard<std::mutex> lock(m_registryMutex);
        m_activeModels.clear();
        m_vertexShaders.clear();
        m_pixelShaders.clear();
        m_shaderPairs.clear();
    }

    VK3D_LOG("Vulkan3D subsystem shutdown complete.");
}

void Vulkan3D::BeginFrame(uint64_t frameIndex) {
    m_currentFrameIndex = frameIndex;
    m_currentFrameStats = {};
    m_hasLastDraw = false;
    memset(&m_lastDrawRecord, 0, sizeof(m_lastDrawRecord));

    {
        std::lock_guard<std::mutex> regLock(m_registryMutex);
        m_currentFrameStats.activeModels = m_activeModels.size();
        m_currentFrameStats.activeShaderPairs = m_shaderPairs.size();
    }
}

void Vulkan3D::EndFrame(uint64_t frameIndex) {
    KONGOR_TRACY_ZONE_SCOPED_N("Vulkan3D::EndFrame");
    m_lastFrameStats = m_currentFrameStats;

    if (frameIndex <= 5) {
        VK3D_LOG("[VULKAN_3D_BATCH] Frame #%llu | Draws: %llu (Indexed: %llu, Prim: %llu, UP: %llu) | Primitives: %llu tris | Vertices: %llu | Consolidated: %llu | Active Models: %llu | Shaders: %llu",
                 frameIndex,
                 m_currentFrameStats.totalDrawCalls,
                 m_currentFrameStats.drawCallsIndexed,
                 m_currentFrameStats.drawCallsPrimitive,
                 m_currentFrameStats.drawCallsIndexedUP + m_currentFrameStats.drawCallsPrimitiveUP,
                 m_currentFrameStats.totalPrimitives,
                 m_currentFrameStats.totalVertices,
                 m_currentFrameStats.consolidatedBatches,
                 m_currentFrameStats.activeModels,
                 m_currentFrameStats.activeShaderPairs);
    }
}

void Vulkan3D::RecordDrawIndexed(uint32_t primType, int32_t baseVertexIndex, uint32_t minVertexIndex,
                                 uint32_t numVertices, uint32_t startIndex, uint32_t primCount,
                                 void* streamSource, uint32_t streamOffset, uint32_t streamStride,
                                 void* indexBuffer) {
    m_currentFrameStats.drawCallsIndexed++;
    m_currentFrameStats.totalDrawCalls++;
    m_currentFrameStats.totalVertices += numVertices;
    m_currentFrameStats.totalPrimitives += primCount;

    // Check for batch consolidation opportunity
    if (m_hasLastDraw &&
        m_lastDrawRecord.drawType == VULKAN_3D_DRAW_INDEXED &&
        m_lastDrawRecord.streamSource == streamSource &&
        m_lastDrawRecord.streamOffset == streamOffset &&
        m_lastDrawRecord.streamStride == streamStride &&
        m_lastDrawRecord.indexBuffer == indexBuffer &&
        m_lastDrawRecord.primitiveType == primType &&
        (m_lastDrawRecord.startIndex + m_lastDrawRecord.primitiveCount * 3) == startIndex) {
        m_currentFrameStats.consolidatedBatches++;
    }

    m_lastDrawRecord.drawType = VULKAN_3D_DRAW_INDEXED;
    m_lastDrawRecord.primitiveType = primType;
    m_lastDrawRecord.baseVertexIndex = baseVertexIndex;
    m_lastDrawRecord.minVertexIndex = minVertexIndex;
    m_lastDrawRecord.numVertices = numVertices;
    m_lastDrawRecord.startIndex = startIndex;
    m_lastDrawRecord.primitiveCount = primCount;
    m_lastDrawRecord.streamSource = streamSource;
    m_lastDrawRecord.streamOffset = streamOffset;
    m_lastDrawRecord.streamStride = streamStride;
    m_lastDrawRecord.indexBuffer = indexBuffer;
    m_hasLastDraw = true;
}

void Vulkan3D::RecordDrawPrimitive(uint32_t primType, uint32_t startVertex, uint32_t primCount,
                                  void* streamSource, uint32_t streamOffset, uint32_t streamStride) {
    m_currentFrameStats.drawCallsPrimitive++;
    m_currentFrameStats.totalDrawCalls++;
    m_currentFrameStats.totalPrimitives += primCount;
    m_currentFrameStats.totalVertices += (primCount * 3); // Conservative estimate for triangle lists

    m_lastDrawRecord.drawType = VULKAN_3D_DRAW_PRIMITIVE;
    m_lastDrawRecord.primitiveType = primType;
    m_lastDrawRecord.startIndex = startVertex;
    m_lastDrawRecord.primitiveCount = primCount;
    m_lastDrawRecord.streamSource = streamSource;
    m_lastDrawRecord.streamOffset = streamOffset;
    m_lastDrawRecord.streamStride = streamStride;
    m_lastDrawRecord.indexBuffer = nullptr;
    m_hasLastDraw = true;
}

void Vulkan3D::RecordDrawIndexedUP(uint32_t primType, uint32_t minVertexIndex, uint32_t numVertices,
                                   uint32_t primCount, const void* pIndexData, uint32_t indexFormat,
                                   const void* pVertexData, uint32_t vertexStride) {
    (void)primType;
    (void)minVertexIndex;
    (void)pIndexData;
    (void)indexFormat;
    (void)pVertexData;
    (void)vertexStride;
    m_currentFrameStats.drawCallsIndexedUP++;
    m_currentFrameStats.totalDrawCalls++;
    m_currentFrameStats.totalVertices += numVertices;
    m_currentFrameStats.totalPrimitives += primCount;
    m_hasLastDraw = false;
}

void Vulkan3D::RecordDrawPrimitiveUP(uint32_t primType, uint32_t primCount,
                                    const void* pVertexData, uint32_t vertexStride) {
    (void)primType;
    (void)pVertexData;
    (void)vertexStride;
    m_currentFrameStats.drawCallsPrimitiveUP++;
    m_currentFrameStats.totalDrawCalls++;
    m_currentFrameStats.totalPrimitives += primCount;
    m_currentFrameStats.totalVertices += (primCount * 3);
    m_hasLastDraw = false;
}

int Vulkan3D::OnRegisterModel(void* pModel, int assignedHandle) {
    if (!pModel) return assignedHandle;
    std::lock_guard<std::mutex> lock(m_registryMutex);

    VulkanStagedModel modelInfo = {};
    modelInfo.modelId = assignedHandle;
    modelInfo.pModel = pModel;
    modelInfo.registrationTimestamp = m_currentFrameIndex;

    m_activeModels[pModel] = modelInfo;
    m_currentFrameStats.activeModels = m_activeModels.size();

    if (m_activeModels.size() <= 20 || (m_activeModels.size() % 25 == 0)) {
        VK3D_LOG("[MODEL_REGISTRY] RegisterModel(pModel=0x%p) -> Handle #%d (Active Models in Cache: %zu)",
                 pModel, assignedHandle, m_activeModels.size());
    }
    return assignedHandle;
}

void Vulkan3D::OnUnregisterModel(void* pModel) {
    if (!pModel) return;
    std::lock_guard<std::mutex> lock(m_registryMutex);
    auto it = m_activeModels.find(pModel);
    if (it != m_activeModels.end()) {
        int handle = it->second.modelId;
        m_activeModels.erase(it);
        m_currentFrameStats.activeModels = m_activeModels.size();
        if (m_activeModels.size() % 25 == 0) {
            VK3D_LOG("[MODEL_REGISTRY] UnregisterModel(pModel=0x%p, Handle #%d) (Remaining Active: %zu)",
                     pModel, handle, m_activeModels.size());
        }
    }
}

int Vulkan3D::OnRegisterVertexShader(void* pShader, int assignedHandle) {
    if (!pShader) return assignedHandle;
    std::lock_guard<std::mutex> lock(m_registryMutex);
    m_vertexShaders[pShader] = assignedHandle;
    if (m_vertexShaders.size() <= 20 || (m_vertexShaders.size() % 50 == 0)) {
        VK3D_LOG("[SHADER_REGISTRY] RegisterVertexShader(pShader=0x%p) -> Handle #%d (Total VS: %zu)",
                 pShader, assignedHandle, m_vertexShaders.size());
    }
    return assignedHandle;
}

void Vulkan3D::OnUnregisterVertexShader(void* pShader) {
    if (!pShader) return;
    std::lock_guard<std::mutex> lock(m_registryMutex);
    m_vertexShaders.erase(pShader);
}

int Vulkan3D::OnRegisterPixelShader(void* pShader, int assignedHandle) {
    if (!pShader) return assignedHandle;
    std::lock_guard<std::mutex> lock(m_registryMutex);
    m_pixelShaders[pShader] = assignedHandle;
    if (m_pixelShaders.size() <= 20 || (m_pixelShaders.size() % 50 == 0)) {
        VK3D_LOG("[SHADER_REGISTRY] RegisterPixelShader(pShader=0x%p) -> Handle #%d (Total PS: %zu)",
                 pShader, assignedHandle, m_pixelShaders.size());
    }
    return assignedHandle;
}

void Vulkan3D::OnUnregisterPixelShader(void* pShader) {
    if (!pShader) return;
    std::lock_guard<std::mutex> lock(m_registryMutex);
    m_pixelShaders.erase(pShader);
}

void Vulkan3D::OnRegisterShaderPair(void* pVertexShader, void* pPixelShader) {
    std::lock_guard<std::mutex> lock(m_registryMutex);
    for (const auto& pair : m_shaderPairs) {
        if (pair.first == pVertexShader && pair.second == pPixelShader) {
            return;
        }
    }
    m_shaderPairs.push_back({pVertexShader, pPixelShader});
    m_currentFrameStats.activeShaderPairs = m_shaderPairs.size();
    if (m_shaderPairs.size() <= 20 || (m_shaderPairs.size() % 50 == 0)) {
        VK3D_LOG("[SHADER_PAIR_PSO] RegisterShaderPair(VS=0x%p, PS=0x%p) (Total Pairs Pre-warmed: %zu)",
                 pVertexShader, pPixelShader, m_shaderPairs.size());
    }
}

size_t Vulkan3D::GetActiveModelCount() const {
    std::lock_guard<std::mutex> lock(m_registryMutex);
    return m_activeModels.size();
}

size_t Vulkan3D::GetActiveShaderPairCount() const {
    std::lock_guard<std::mutex> lock(m_registryMutex);
    return m_shaderPairs.size();
}
