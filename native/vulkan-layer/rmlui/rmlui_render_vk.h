// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ============================================================================
// File: rmlui_render_vk.h
// System: ProjectKongor - Native Vulkan Layer (VkLayer_hon_replay_capture)
// Subsystem: Modern In-Game HUD via RmlUi - Vulkan 1.3 Render Interface
// ============================================================================

#pragma once

#include <RmlUi/Core/RenderInterface.h>
#include <vulkan/vulkan.h>
#include <cstdint>
#include <memory>
#include <mutex>
#include <unordered_map>
#include <vector>
#include "../layer_manifest.h"
#include "rmlui_vk_suballocator.h"

struct VkTextureResource {
    VkImage image = VK_NULL_HANDLE;
    VkDeviceMemory memory = VK_NULL_HANDLE;
    VkSuballocation suballocation;
    VkImageView view = VK_NULL_HANDLE;
    VkSampler sampler = VK_NULL_HANDLE;
    VkDescriptorSet descriptorSet = VK_NULL_HANDLE;
    uint32_t width = 0;
    uint32_t height = 0;
};

struct VkCompiledGeometryResource {
    VkBuffer vertexBuffer = VK_NULL_HANDLE;
    VkDeviceMemory vertexMemory = VK_NULL_HANDLE;
    VkSuballocation vertexSuballocation;
    VkBuffer indexBuffer = VK_NULL_HANDLE;
    VkDeviceMemory indexMemory = VK_NULL_HANDLE;
    VkSuballocation indexSuballocation;
    int numIndices = 0;
    Rml::TextureHandle texture = 0;
};

struct UniformBlock {
    float transform[16]; // mat4 column-major
    float translate[2];  // vec2
    float padding[2];
};

class RmlUi_RenderInterface_VK : public Rml::RenderInterface {
public:
    static constexpr uint32_t kStreamingRingSlots = 3;
    static constexpr uint32_t kUniformSlotAlignment = 256;
    static constexpr uint32_t kMaxUniformDrawsPerSlot = 4096;
    static constexpr uint64_t kFenceRetireLag = 4;

    RmlUi_RenderInterface_VK();
    ~RmlUi_RenderInterface_VK() override;

    bool Initialize(
        VkDevice device,
        VkPhysicalDevice physicalDevice,
        VkQueue queue,
        uint32_t queueFamilyIndex,
        VkFormat format,
        uint32_t width,
        uint32_t height,
        const LayerDeviceDispatchTable& devDispatch,
        const LayerInstanceDispatchTable& instDispatch);

    void SetSwapchainImages(const std::vector<VkImage>& images);
    void OnResize(uint32_t width, uint32_t height);
    void ReleaseSwapchainResources();
    void Cleanup();

    bool BeginFrame(VkCommandBuffer cmdBuffer, uint32_t imageIndex, uint32_t frameSlot = 0);
    void EndFrame();
    void NotifyFrameFenceCompleted(uint32_t frameSlot);

    bool IsInitialized() const { return m_isInitialized; }
    uint32_t GetWidth() const { return m_width; }
    uint32_t GetHeight() const { return m_height; }

    bool UpdateQrTexture(const uint8_t* rgbaPixels, int width, int height);
    Rml::TextureHandle GetQrTextureHandle() const { return m_qrTextureHandle; }

    // -- Rml::RenderInterface implementation --
    void RenderGeometry(Rml::Vertex* vertices, int num_vertices, int* indices, int num_indices,
                        Rml::TextureHandle texture, const Rml::Vector2f& translation) override;

    Rml::CompiledGeometryHandle CompileGeometry(Rml::Vertex* vertices, int num_vertices,
                                                int* indices, int num_indices,
                                                Rml::TextureHandle texture) override;
    void RenderCompiledGeometry(Rml::CompiledGeometryHandle geometry, const Rml::Vector2f& translation) override;
    void ReleaseCompiledGeometry(Rml::CompiledGeometryHandle geometry) override;

    void EnableScissorRegion(bool enable) override;
    void SetScissorRegion(int x, int y, int width, int height) override;

    bool LoadTexture(Rml::TextureHandle& texture_handle, Rml::Vector2i& texture_dimensions, const Rml::String& source) override;
    bool GenerateTexture(Rml::TextureHandle& texture_handle, const Rml::byte* source, const Rml::Vector2i& source_dimensions) override;
    void ReleaseTexture(Rml::TextureHandle texture_handle) override;

    void SetTransform(const Rml::Matrix4f* transform) override;

private:
    struct RetiredTextureEntry {
        uint64_t retireAfterTick = 0;
        std::unique_ptr<VkTextureResource> resource;
    };

    struct RetiredGeometryEntry {
        uint64_t retireAfterTick = 0;
        VkCompiledGeometryResource resource;
    };

    struct PendingStagingUpload {
        VkFence fence = VK_NULL_HANDLE;
        VkCommandBuffer cmdBuffer = VK_NULL_HANDLE;
        VkBuffer stagingBuffer = VK_NULL_HANDLE;
        VkSuballocation stagingSuballoc;
    };

    uint32_t FindMemoryType(uint32_t typeFilter, VkMemoryPropertyFlags properties);
    bool CreateRenderPass();
    bool CreatePipelines();
    bool CreateFramebuffers();
    bool CreateDynamicBuffers();
    bool CreateWhiteTexture();
    bool GenerateTextureLocked(Rml::TextureHandle& texture_handle, const Rml::byte* source, const Rml::Vector2i& source_dimensions);

    bool WriteDrawUniformAndGetOffset(const Rml::Vector2f& translation, uint32_t& outDynamicOffset);
    VkTextureResource* ResolveTextureResource(Rml::TextureHandle texture);
    void DestroyTextureResourceImmediate(VkTextureResource& tex);
    void DestroyCompiledGeometryImmediate(VkCompiledGeometryResource& geom);
    void DrainRetiredResources(bool forceAll);
    void RetireStagingUploads(bool forceAll);

private:
    VkDevice m_device;
    VkPhysicalDevice m_physicalDevice;
    VkQueue m_queue;
    uint32_t m_queueFamilyIndex;
    VkFormat m_format;
    uint32_t m_width;
    uint32_t m_height;

    LayerDeviceDispatchTable m_devDispatch;
    LayerInstanceDispatchTable m_instDispatch;

    bool m_isInitialized;
    std::mutex m_renderMutex;

    // Render Targets & Framebuffers
    VkRenderPass m_renderPass;
    std::vector<VkImage> m_swapchainImages;
    std::vector<VkImageView> m_swapchainImageViews;
    std::vector<VkFramebuffer> m_framebuffers;

    // Shaders & Pipeline
    VkShaderModule m_vertShader;
    VkShaderModule m_fragShaderColor;
    VkShaderModule m_fragShaderTexture;
    VkDescriptorSetLayout m_descriptorSetLayoutTransform;
    VkDescriptorSetLayout m_descriptorSetLayoutTexture;
    VkDescriptorSet m_descriptorSetTransform;
    VkDescriptorPool m_descriptorPool;
    VkPipelineLayout m_pipelineLayout;
    VkPipeline m_pipelineColor;
    VkPipeline m_pipelineTexture;

    // Dynamic Geometry Ring Buffers (Host visible, partitioned per frameSlot)
    VkBuffer m_vertexBuffer;
    VkDeviceMemory m_vertexMemory;
    void* m_mappedVertices;
    uint32_t m_vertexOffset;
    uint32_t m_verticesPerSlot;
    uint32_t m_maxVertices;

    VkBuffer m_indexBuffer;
    VkDeviceMemory m_indexMemory;
    void* m_mappedIndices;
    uint32_t m_indexOffset;
    uint32_t m_indicesPerSlot;
    uint32_t m_maxIndices;

    VkBuffer m_uniformBuffer;
    VkDeviceMemory m_uniformMemory;
    void* m_mappedUniforms;
    uint32_t m_activeRingSlot;
    uint32_t m_uniformDrawIndexInSlot;

    // 1x1 White Texture fallback
    VkTextureResource m_whiteTexture;
    Rml::TextureHandle m_whiteTextureHandle;

    // High-Density QR Texture
    VkTextureResource* m_qrTexture;
    Rml::TextureHandle m_qrTextureHandle;

    // Active frame recording state
    VkCommandBuffer m_currentCmdBuffer;
    uint32_t m_currentImageIndex;
    bool m_inRenderPass;
    Rml::Matrix4f m_currentTransform;
    bool m_hasTransform;
    bool m_scissorEnabled;
    VkRect2D m_currentScissor;

    // Live & fence-retired GPU resources
    uint64_t m_completedFenceTick;
    uintptr_t m_nextGeometryHandle;
    std::vector<std::unique_ptr<VkTextureResource>> m_allocatedTextures;
    std::unordered_map<Rml::CompiledGeometryHandle, VkCompiledGeometryResource> m_compiledGeometries;
    std::vector<RetiredTextureEntry> m_retiredTextures;
    std::vector<RetiredGeometryEntry> m_retiredGeometries;

    // Persistent staging upload pipeline & 16 MiB slab suballocator
    VkCommandPool m_uploadCommandPool;
    std::vector<PendingStagingUpload> m_pendingStagingUploads;
    RmlUi_VkSlabSuballocator m_suballocator;
};
