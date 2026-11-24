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
#include <vector>
#include <cstdint>
#include <mutex>

// =================================================================================================
// 2D Vertex & Memory Data Structures
// =================================================================================================
#pragma pack(push, 1)
struct SVulkan2DVertex {
    float x, y;        // Screen-space pixel coordinates (0.0 .. width, 0.0 .. height)
    float u, v;        // Normalized texture coordinates (0.0 .. 1.0)
    uint32_t color;    // 32-bit packed color (RGBA8 / BGRA8)
};
#pragma pack(pop)
static_assert(sizeof(SVulkan2DVertex) == 20, "SVulkan2DVertex must be exactly 20 bytes");

struct Vulkan2DPushConstants {
    float scale[2];     // (2.0f / viewportWidth, 2.0f / viewportHeight)
    float translate[2]; // (-1.0f, -1.0f)
};

struct Vulkan2DDrawBatch {
    uint32_t firstIndex;
    uint32_t indexCount;
    int32_t vertexOffset;
    VkDescriptorSet descriptorSet;
    VkRect2D scissor;
};

// =================================================================================================
// Vulkan2D Subsystem Manager
// =================================================================================================
class Vulkan2D {
public:
    static constexpr uint32_t MAX_FRAMES_IN_FLIGHT = 3;

    static Vulkan2D& Get();

    bool Initialize(VkDevice device,
                    VkPhysicalDevice physicalDevice,
                    VkQueue graphicsQueue,
                    uint32_t graphicsQueueFamily,
                    VkFormat colorFormat,
                    uint32_t width,
                    uint32_t height);
    void Shutdown();
    void Resize(uint32_t width, uint32_t height);

    // Frame lifecycle
    void ResetBatch();

    // 2D Primitive Aggregation (direct host-coherent ring buffer emission)
    void AddRect(float x, float y, float w, float h,
                 float s0, float t0, float s1, float t1,
                 uint32_t color1, uint32_t color2,
                 VkDescriptorSet descSet = VK_NULL_HANDLE);

    void AddQuad(const float* v, const float* tc, uint32_t color, VkDescriptorSet descSet = VK_NULL_HANDLE);
    void AddTri(const float* v, const float* tc, uint32_t color, VkDescriptorSet descSet = VK_NULL_HANDLE);
    void AddLine(float x1, float y1, float x2, float y2, uint32_t color);

    void SetScissor(int32_t x, int32_t y, uint32_t w, uint32_t h);

    // GPU Submission
    void RecordDrawCommands(VkCommandBuffer cmd, uint32_t viewportWidth, uint32_t viewportHeight);

    // Diagnostics & Query
    bool IsInitialized() const { return m_bInitialized; }
    uint32_t GetCurrentVertexCount() const { return m_vertexCount; }
    uint32_t GetCurrentIndexCount() const { return m_indexCount; }
    size_t GetBatchCount() const { return m_batches.size(); }
    VkDescriptorSet GetWhiteTextureDescriptorSet() const { return m_whiteTextureDescSet; }

    VkRenderPass GetRenderPass() const { return m_renderPass; }
    VkPipeline GetPipeline() const { return m_pipeline; }
    VkPipelineLayout GetPipelineLayout() const { return m_pipelineLayout; }

private:
    struct FrameRingSlot {
        VkBuffer vtxBuffer = VK_NULL_HANDLE;
        VkDeviceMemory vtxMemory = VK_NULL_HANDLE;
        SVulkan2DVertex* pMappedVertices = nullptr;

        VkBuffer idxBuffer = VK_NULL_HANDLE;
        VkDeviceMemory idxMemory = VK_NULL_HANDLE;
        uint32_t* pMappedIndices = nullptr;
    };

    Vulkan2D();
    ~Vulkan2D();

    bool CreateRenderPass(VkFormat colorFormat);
    bool CreatePipeline();
    bool CreateBuffers();
    bool CreateWhiteFallbackTexture();

    uint32_t FindMemoryType(uint32_t typeFilter, VkMemoryPropertyFlags properties);

    VkDevice m_device = VK_NULL_HANDLE;
    VkPhysicalDevice m_physicalDevice = VK_NULL_HANDLE;
    VkQueue m_graphicsQueue = VK_NULL_HANDLE;
    uint32_t m_graphicsQueueFamily = 0;
    VkFormat m_colorFormat = VK_FORMAT_UNDEFINED;
    uint32_t m_width = 0;
    uint32_t m_height = 0;

    // Vulkan Pipeline Objects
    VkRenderPass m_renderPass = VK_NULL_HANDLE;
    VkDescriptorSetLayout m_descriptorSetLayout = VK_NULL_HANDLE;
    VkPipelineLayout m_pipelineLayout = VK_NULL_HANDLE;
    VkPipeline m_pipeline = VK_NULL_HANDLE;
    VkDescriptorPool m_descriptorPool = VK_NULL_HANDLE;

    // Dynamic Host-Coherent Per-Frame Ring Buffers (MAX_FRAMES_IN_FLIGHT = 3 slots * 4 MB)
    static constexpr VkDeviceSize VTX_BUFFER_SIZE = 2 * 1024 * 1024; // 2 MB (~104,857 vertices per frame slot)
    static constexpr VkDeviceSize IDX_BUFFER_SIZE = 2 * 1024 * 1024; // 2 MB (~524,288 indices per frame slot)

    FrameRingSlot m_frameRings[MAX_FRAMES_IN_FLIGHT] = {};
    uint32_t m_activeFrameSlot = 0;
    bool m_hasStartedAnyFrame = false;

    // Fallback 4x4 White Texture for flat/untextured primitives
    VkImage m_whiteImage = VK_NULL_HANDLE;
    VkDeviceMemory m_whiteImageMemory = VK_NULL_HANDLE;
    VkImageView m_whiteImageView = VK_NULL_HANDLE;
    VkSampler m_whiteSampler = VK_NULL_HANDLE;
    VkDescriptorSet m_whiteTextureDescSet = VK_NULL_HANDLE;

    // Active Frame State
    std::mutex m_batchMutex;
    uint32_t m_vertexCount = 0;
    uint32_t m_indexCount = 0;
    VkRect2D m_currentScissor = {{0, 0}, {0, 0}};
    VkDescriptorSet m_currentDescriptorSet = VK_NULL_HANDLE;
    std::vector<Vulkan2DDrawBatch> m_batches;

    bool m_bInitialized = false;
};
