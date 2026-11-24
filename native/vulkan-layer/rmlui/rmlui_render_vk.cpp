// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ============================================================================
// File: rmlui_render_vk.cpp
// System: ProjectKongor - Native Vulkan Layer (VkLayer_hon_replay_capture)
// Subsystem: Modern In-Game HUD via RmlUi - Vulkan 1.3 Render Interface
// ============================================================================

#include "rmlui_render_vk.h"
#include "tao_qr_matrix.h"
#include <RmlUi_Vulkan/ShadersCompiledSPV.h>
#include <algorithm>
#include <cmath>
#include <cstring>

RmlUi_RenderInterface_VK::RmlUi_RenderInterface_VK()
    : m_device(VK_NULL_HANDLE)
    , m_physicalDevice(VK_NULL_HANDLE)
    , m_queue(VK_NULL_HANDLE)
    , m_queueFamilyIndex(0)
    , m_format(VK_FORMAT_B8G8R8A8_UNORM)
    , m_width(0)
    , m_height(0)
    , m_isInitialized(false)
    , m_renderPass(VK_NULL_HANDLE)
    , m_vertShader(VK_NULL_HANDLE)
    , m_fragShaderColor(VK_NULL_HANDLE)
    , m_fragShaderTexture(VK_NULL_HANDLE)
    , m_descriptorSetLayoutTransform(VK_NULL_HANDLE)
    , m_descriptorSetLayoutTexture(VK_NULL_HANDLE)
    , m_descriptorSetTransform(VK_NULL_HANDLE)
    , m_descriptorPool(VK_NULL_HANDLE)
    , m_pipelineLayout(VK_NULL_HANDLE)
    , m_pipelineColor(VK_NULL_HANDLE)
    , m_pipelineTexture(VK_NULL_HANDLE)
    , m_vertexBuffer(VK_NULL_HANDLE)
    , m_vertexMemory(VK_NULL_HANDLE)
    , m_mappedVertices(nullptr)
    , m_vertexOffset(0)
    , m_verticesPerSlot(65536)
    , m_maxVertices(65536 * kStreamingRingSlots)
    , m_indexBuffer(VK_NULL_HANDLE)
    , m_indexMemory(VK_NULL_HANDLE)
    , m_mappedIndices(nullptr)
    , m_indexOffset(0)
    , m_indicesPerSlot(131072)
    , m_maxIndices(131072 * kStreamingRingSlots)
    , m_uniformBuffer(VK_NULL_HANDLE)
    , m_uniformMemory(VK_NULL_HANDLE)
    , m_mappedUniforms(nullptr)
    , m_activeRingSlot(0)
    , m_uniformDrawIndexInSlot(0)
    , m_whiteTextureHandle(0)
    , m_qrTexture(nullptr)
    , m_qrTextureHandle(0)
    , m_currentCmdBuffer(VK_NULL_HANDLE)
    , m_currentImageIndex(0)
    , m_inRenderPass(false)
    , m_hasTransform(false)
    , m_scissorEnabled(false)
    , m_completedFenceTick(0)
    , m_nextGeometryHandle(0)
    , m_uploadCommandPool(VK_NULL_HANDLE)
{
    memset(&m_devDispatch, 0, sizeof(m_devDispatch));
    memset(&m_instDispatch, 0, sizeof(m_instDispatch));
    m_currentTransform = Rml::Matrix4f::Identity();
    m_currentScissor = { {0, 0}, {0, 0} };
}

RmlUi_RenderInterface_VK::~RmlUi_RenderInterface_VK() {
    if (IsLayerProcessTerminating()) {
        m_device = VK_NULL_HANDLE;
        m_isInitialized = false;
        return;
    }
    Cleanup();
}

uint32_t RmlUi_RenderInterface_VK::FindMemoryType(uint32_t typeFilter, VkMemoryPropertyFlags properties) {
    if (!m_instDispatch.GetPhysicalDeviceMemoryProperties || m_physicalDevice == VK_NULL_HANDLE) {
        return UINT32_MAX;
    }
    VkPhysicalDeviceMemoryProperties memProperties = {};
    m_instDispatch.GetPhysicalDeviceMemoryProperties(m_physicalDevice, &memProperties);

    for (uint32_t i = 0; i < memProperties.memoryTypeCount; i++) {
        if ((typeFilter & (1u << i)) &&
            (memProperties.memoryTypes[i].propertyFlags & properties) == properties) {
            return i;
        }
    }
    return UINT32_MAX;
}

bool RmlUi_RenderInterface_VK::Initialize(
    VkDevice device,
    VkPhysicalDevice physicalDevice,
    VkQueue queue,
    uint32_t queueFamilyIndex,
    VkFormat format,
    uint32_t width,
    uint32_t height,
    const LayerDeviceDispatchTable& devDispatch,
    const LayerInstanceDispatchTable& instDispatch)
{
    std::lock_guard<std::mutex> lock(m_renderMutex);
    Cleanup();

    m_device = device;
    m_physicalDevice = physicalDevice;
    m_queue = queue;
    m_queueFamilyIndex = queueFamilyIndex;
    m_format = format;
    m_width = width;
    m_height = height;
    m_devDispatch = devDispatch;
    m_instDispatch = instDispatch;

    m_suballocator.Initialize(m_device, &m_devDispatch);

    VkCommandPoolCreateInfo uploadCpInfo = { VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO };
    uploadCpInfo.queueFamilyIndex = m_queueFamilyIndex;
    uploadCpInfo.flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT;
    if (m_devDispatch.CreateCommandPool(m_device, &uploadCpInfo, nullptr, &m_uploadCommandPool) != VK_SUCCESS) {
        HON_LOG_ERROR("[RmlUi_Render] Failed to create persistent upload command pool.");
        return false;
    }

    if (!CreateRenderPass()) {
        HON_LOG_ERROR("[RmlUi_Render] Failed to create render pass.");
        return false;
    }

    if (!CreatePipelines()) {
        HON_LOG_ERROR("[RmlUi_Render] Failed to create graphics pipelines.");
        return false;
    }

    if (!CreateDynamicBuffers()) {
        HON_LOG_ERROR("[RmlUi_Render] Failed to allocate dynamic geometry buffers.");
        return false;
    }

    if (!CreateWhiteTexture()) {
        HON_LOG_ERROR("[RmlUi_Render] Failed to create 1x1 white fallback texture.");
        return false;
    }

    m_isInitialized = true;
    HON_LOG_INFO("[RmlUi_Render] Vulkan 1.3 RenderInterface initialized for %ux%u format %d (3-slot streaming ring + fence-deferred retirement).",
                 width, height, format);
    return true;
}

bool RmlUi_RenderInterface_VK::CreateRenderPass() {
    VkAttachmentDescription colorAttachment = {};
    colorAttachment.format = m_format;
    colorAttachment.samples = VK_SAMPLE_COUNT_1_BIT;
    colorAttachment.loadOp = VK_ATTACHMENT_LOAD_OP_LOAD;   // Preserve underlying game frame
    colorAttachment.storeOp = VK_ATTACHMENT_STORE_OP_STORE;
    colorAttachment.stencilLoadOp = VK_ATTACHMENT_LOAD_OP_DONT_CARE;
    colorAttachment.stencilStoreOp = VK_ATTACHMENT_STORE_OP_DONT_CARE;
    colorAttachment.initialLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;
    colorAttachment.finalLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;

    VkAttachmentReference colorRef = {};
    colorRef.attachment = 0;
    colorRef.layout = VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL;

    VkSubpassDescription subpass = {};
    subpass.pipelineBindPoint = VK_PIPELINE_BIND_POINT_GRAPHICS;
    subpass.colorAttachmentCount = 1;
    subpass.pColorAttachments = &colorRef;

    VkSubpassDependency dependency = {};
    dependency.srcSubpass = VK_SUBPASS_EXTERNAL;
    dependency.dstSubpass = 0;
    dependency.srcStageMask = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT;
    dependency.dstStageMask = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT;
    dependency.srcAccessMask = VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT;
    dependency.dstAccessMask = VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT | VK_ACCESS_COLOR_ATTACHMENT_READ_BIT;

    VkRenderPassCreateInfo rpInfo = { VK_STRUCTURE_TYPE_RENDER_PASS_CREATE_INFO };
    rpInfo.attachmentCount = 1;
    rpInfo.pAttachments = &colorAttachment;
    rpInfo.subpassCount = 1;
    rpInfo.pSubpasses = &subpass;
    rpInfo.dependencyCount = 1;
    rpInfo.pDependencies = &dependency;

    return m_devDispatch.CreateRenderPass(m_device, &rpInfo, nullptr, &m_renderPass) == VK_SUCCESS;
}

bool RmlUi_RenderInterface_VK::CreatePipelines() {
    // 1. Shaders from precompiled SPIR-V
    VkShaderModuleCreateInfo vertInfo = { VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO };
    vertInfo.codeSize = sizeof(shader_vert);
    vertInfo.pCode = reinterpret_cast<const uint32_t*>(shader_vert);
    if (m_devDispatch.CreateShaderModule(m_device, &vertInfo, nullptr, &m_vertShader) != VK_SUCCESS) return false;

    VkShaderModuleCreateInfo fragColorInfo = { VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO };
    fragColorInfo.codeSize = sizeof(shader_frag_color);
    fragColorInfo.pCode = reinterpret_cast<const uint32_t*>(shader_frag_color);
    if (m_devDispatch.CreateShaderModule(m_device, &fragColorInfo, nullptr, &m_fragShaderColor) != VK_SUCCESS) return false;

    VkShaderModuleCreateInfo fragTexInfo = { VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO };
    fragTexInfo.codeSize = sizeof(shader_frag_texture);
    fragTexInfo.pCode = reinterpret_cast<const uint32_t*>(shader_frag_texture);
    if (m_devDispatch.CreateShaderModule(m_device, &fragTexInfo, nullptr, &m_fragShaderTexture) != VK_SUCCESS) return false;

    // 2. Descriptor set layouts
    // Set 0: Per-draw Dynamic Vertex Transform Uniform Buffer (binding 1, VERTEX_BIT)
    VkDescriptorSetLayoutBinding transformBinding = {};
    transformBinding.binding = 1;
    transformBinding.descriptorType = VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER_DYNAMIC;
    transformBinding.descriptorCount = 1;
    transformBinding.stageFlags = VK_SHADER_STAGE_VERTEX_BIT;

    VkDescriptorSetLayoutCreateInfo transformDslInfo = { VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO };
    transformDslInfo.bindingCount = 1;
    transformDslInfo.pBindings = &transformBinding;
    if (m_devDispatch.CreateDescriptorSetLayout(m_device, &transformDslInfo, nullptr, &m_descriptorSetLayoutTransform) != VK_SUCCESS) return false;

    // Set 1: Texture Combined Image Sampler (binding 2, FRAGMENT_BIT)
    VkDescriptorSetLayoutBinding texBinding = {};
    texBinding.binding = 2;
    texBinding.descriptorType = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER;
    texBinding.descriptorCount = 1;
    texBinding.stageFlags = VK_SHADER_STAGE_FRAGMENT_BIT;

    VkDescriptorSetLayoutCreateInfo texDslInfo = { VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO };
    texDslInfo.bindingCount = 1;
    texDslInfo.pBindings = &texBinding;
    if (m_devDispatch.CreateDescriptorSetLayout(m_device, &texDslInfo, nullptr, &m_descriptorSetLayoutTexture) != VK_SUCCESS) return false;

    // 3. Descriptor Pool (FREE_DESCRIPTOR_SET_BIT enabled so ReleaseTexture recycles Set 1 descriptors)
    VkDescriptorPoolSize poolSizes[2] = {};
    poolSizes[0].type = VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER_DYNAMIC;
    poolSizes[0].descriptorCount = 16;
    poolSizes[1].type = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER;
    poolSizes[1].descriptorCount = 1024;

    VkDescriptorPoolCreateInfo poolInfo = { VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO };
    poolInfo.flags = VK_DESCRIPTOR_POOL_CREATE_FREE_DESCRIPTOR_SET_BIT;
    poolInfo.maxSets = 1040;
    poolInfo.poolSizeCount = 2;
    poolInfo.pPoolSizes = poolSizes;
    if (m_devDispatch.CreateDescriptorPool(m_device, &poolInfo, nullptr, &m_descriptorPool) != VK_SUCCESS) return false;

    // 4. Pipeline layout (Set 0 = Transform, Set 1 = Texture)
    VkDescriptorSetLayout setLayouts[2] = { m_descriptorSetLayoutTransform, m_descriptorSetLayoutTexture };
    VkPipelineLayoutCreateInfo plInfo = { VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO };
    plInfo.setLayoutCount = 2;
    plInfo.pSetLayouts = setLayouts;
    if (m_devDispatch.CreatePipelineLayout(m_device, &plInfo, nullptr, &m_pipelineLayout) != VK_SUCCESS) return false;

    // 5. Vertex input state (Rml::Vertex: position 8B, colour 4B, tex_coord 8B)
    VkVertexInputBindingDescription bindingDesc = {};
    bindingDesc.binding = 0;
    bindingDesc.stride = sizeof(Rml::Vertex);
    bindingDesc.inputRate = VK_VERTEX_INPUT_RATE_VERTEX;

    VkVertexInputAttributeDescription attrDescs[3] = {};
    attrDescs[0].location = 0; // inPosition
    attrDescs[0].binding = 0;
    attrDescs[0].format = VK_FORMAT_R32G32_SFLOAT;
    attrDescs[0].offset = offsetof(Rml::Vertex, position);

    attrDescs[1].location = 1; // inColor0
    attrDescs[1].binding = 0;
    attrDescs[1].format = VK_FORMAT_R8G8B8A8_UNORM;
    attrDescs[1].offset = offsetof(Rml::Vertex, colour);

    attrDescs[2].location = 2; // inTexCoord0
    attrDescs[2].binding = 0;
    attrDescs[2].format = VK_FORMAT_R32G32_SFLOAT;
    attrDescs[2].offset = offsetof(Rml::Vertex, tex_coord);

    VkPipelineVertexInputStateCreateInfo vertexInputInfo = { VK_STRUCTURE_TYPE_PIPELINE_VERTEX_INPUT_STATE_CREATE_INFO };
    vertexInputInfo.vertexBindingDescriptionCount = 1;
    vertexInputInfo.pVertexBindingDescriptions = &bindingDesc;
    vertexInputInfo.vertexAttributeDescriptionCount = 3;
    vertexInputInfo.pVertexAttributeDescriptions = attrDescs;

    VkPipelineInputAssemblyStateCreateInfo inputAssembly = { VK_STRUCTURE_TYPE_PIPELINE_INPUT_ASSEMBLY_STATE_CREATE_INFO };
    inputAssembly.topology = VK_PRIMITIVE_TOPOLOGY_TRIANGLE_LIST;

    VkPipelineViewportStateCreateInfo viewportState = { VK_STRUCTURE_TYPE_PIPELINE_VIEWPORT_STATE_CREATE_INFO };
    viewportState.viewportCount = 1;
    viewportState.scissorCount = 1;

    VkPipelineRasterizationStateCreateInfo rasterizer = { VK_STRUCTURE_TYPE_PIPELINE_RASTERIZATION_STATE_CREATE_INFO };
    rasterizer.polygonMode = VK_POLYGON_MODE_FILL;
    rasterizer.cullMode = VK_CULL_MODE_NONE;
    rasterizer.frontFace = VK_FRONT_FACE_COUNTER_CLOCKWISE;
    rasterizer.lineWidth = 1.0f;

    VkPipelineMultisampleStateCreateInfo multisampling = { VK_STRUCTURE_TYPE_PIPELINE_MULTISAMPLE_STATE_CREATE_INFO };
    multisampling.rasterizationSamples = VK_SAMPLE_COUNT_1_BIT;

    VkPipelineColorBlendAttachmentState colorBlendAttachment = {};
    colorBlendAttachment.colorWriteMask = VK_COLOR_COMPONENT_R_BIT | VK_COLOR_COMPONENT_G_BIT | VK_COLOR_COMPONENT_B_BIT | VK_COLOR_COMPONENT_A_BIT;
    colorBlendAttachment.blendEnable = VK_TRUE;
    colorBlendAttachment.srcColorBlendFactor = VK_BLEND_FACTOR_SRC_ALPHA;
    colorBlendAttachment.dstColorBlendFactor = VK_BLEND_FACTOR_ONE_MINUS_SRC_ALPHA;
    colorBlendAttachment.colorBlendOp = VK_BLEND_OP_ADD;
    colorBlendAttachment.srcAlphaBlendFactor = VK_BLEND_FACTOR_ONE;
    colorBlendAttachment.dstAlphaBlendFactor = VK_BLEND_FACTOR_ONE_MINUS_SRC_ALPHA;
    colorBlendAttachment.alphaBlendOp = VK_BLEND_OP_ADD;

    VkPipelineColorBlendStateCreateInfo colorBlending = { VK_STRUCTURE_TYPE_PIPELINE_COLOR_BLEND_STATE_CREATE_INFO };
    colorBlending.attachmentCount = 1;
    colorBlending.pAttachments = &colorBlendAttachment;

    VkDynamicState dynamicStates[] = { VK_DYNAMIC_STATE_VIEWPORT, VK_DYNAMIC_STATE_SCISSOR };
    VkPipelineDynamicStateCreateInfo dynamicState = { VK_STRUCTURE_TYPE_PIPELINE_DYNAMIC_STATE_CREATE_INFO };
    dynamicState.dynamicStateCount = 2;
    dynamicState.pDynamicStates = dynamicStates;

    VkPipelineShaderStageCreateInfo shaderStages[2] = {};
    shaderStages[0].sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO;
    shaderStages[0].stage = VK_SHADER_STAGE_VERTEX_BIT;
    shaderStages[0].module = m_vertShader;
    shaderStages[0].pName = "main";

    shaderStages[1].sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO;
    shaderStages[1].stage = VK_SHADER_STAGE_FRAGMENT_BIT;
    shaderStages[1].module = m_fragShaderTexture;
    shaderStages[1].pName = "main";

    VkGraphicsPipelineCreateInfo pipelineInfo = { VK_STRUCTURE_TYPE_GRAPHICS_PIPELINE_CREATE_INFO };
    pipelineInfo.stageCount = 2;
    pipelineInfo.pStages = shaderStages;
    pipelineInfo.pVertexInputState = &vertexInputInfo;
    pipelineInfo.pInputAssemblyState = &inputAssembly;
    pipelineInfo.pViewportState = &viewportState;
    pipelineInfo.pRasterizationState = &rasterizer;
    pipelineInfo.pMultisampleState = &multisampling;
    pipelineInfo.pColorBlendState = &colorBlending;
    pipelineInfo.pDynamicState = &dynamicState;
    pipelineInfo.layout = m_pipelineLayout;
    pipelineInfo.renderPass = m_renderPass;
    pipelineInfo.subpass = 0;

    if (m_devDispatch.CreateGraphicsPipelines(m_device, VK_NULL_HANDLE, 1, &pipelineInfo, nullptr, &m_pipelineTexture) != VK_SUCCESS) return false;

    shaderStages[1].module = m_fragShaderColor;
    if (m_devDispatch.CreateGraphicsPipelines(m_device, VK_NULL_HANDLE, 1, &pipelineInfo, nullptr, &m_pipelineColor) != VK_SUCCESS) return false;

    return true;
}

bool RmlUi_RenderInterface_VK::CreateDynamicBuffers() {
    const VkMemoryPropertyFlags hostProps = VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT;

    // 1. Streaming Vertex Ring Buffer (3 slices of m_verticesPerSlot)
    VkBufferCreateInfo vbInfo = { VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO };
    vbInfo.size = static_cast<VkDeviceSize>(sizeof(Rml::Vertex)) * m_maxVertices;
    vbInfo.usage = VK_BUFFER_USAGE_VERTEX_BUFFER_BIT;
    if (m_devDispatch.CreateBuffer(m_device, &vbInfo, nullptr, &m_vertexBuffer) != VK_SUCCESS) return false;

    VkMemoryRequirements vbReqs = {};
    m_devDispatch.GetBufferMemoryRequirements(m_device, m_vertexBuffer, &vbReqs);

    uint32_t vbMemType = FindMemoryType(vbReqs.memoryTypeBits, hostProps);
    if (vbMemType == UINT32_MAX) {
        HON_LOG_ERROR("[RmlUi_Render] FindMemoryType failed for streaming vertex buffer (typeBits=0x%x).", vbReqs.memoryTypeBits);
        m_devDispatch.DestroyBuffer(m_device, m_vertexBuffer, nullptr);
        m_vertexBuffer = VK_NULL_HANDLE;
        return false;
    }

    VkMemoryAllocateInfo vbAlloc = { VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO };
    vbAlloc.allocationSize = vbReqs.size;
    vbAlloc.memoryTypeIndex = vbMemType;
    if (m_devDispatch.AllocateMemory(m_device, &vbAlloc, nullptr, &m_vertexMemory) != VK_SUCCESS) {
        m_devDispatch.DestroyBuffer(m_device, m_vertexBuffer, nullptr);
        m_vertexBuffer = VK_NULL_HANDLE;
        return false;
    }
    m_devDispatch.BindBufferMemory(m_device, m_vertexBuffer, m_vertexMemory, 0);
    if (m_devDispatch.MapMemory(m_device, m_vertexMemory, 0, vbAlloc.allocationSize, 0, &m_mappedVertices) != VK_SUCCESS) {
        return false;
    }

    // 2. Streaming Index Ring Buffer (3 slices of m_indicesPerSlot)
    VkBufferCreateInfo ibInfo = { VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO };
    ibInfo.size = static_cast<VkDeviceSize>(sizeof(int)) * m_maxIndices;
    ibInfo.usage = VK_BUFFER_USAGE_INDEX_BUFFER_BIT;
    if (m_devDispatch.CreateBuffer(m_device, &ibInfo, nullptr, &m_indexBuffer) != VK_SUCCESS) return false;

    VkMemoryRequirements ibReqs = {};
    m_devDispatch.GetBufferMemoryRequirements(m_device, m_indexBuffer, &ibReqs);

    uint32_t ibMemType = FindMemoryType(ibReqs.memoryTypeBits, hostProps);
    if (ibMemType == UINT32_MAX) {
        HON_LOG_ERROR("[RmlUi_Render] FindMemoryType failed for streaming index buffer (typeBits=0x%x).", ibReqs.memoryTypeBits);
        m_devDispatch.DestroyBuffer(m_device, m_indexBuffer, nullptr);
        m_indexBuffer = VK_NULL_HANDLE;
        return false;
    }

    VkMemoryAllocateInfo ibAlloc = { VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO };
    ibAlloc.allocationSize = ibReqs.size;
    ibAlloc.memoryTypeIndex = ibMemType;
    if (m_devDispatch.AllocateMemory(m_device, &ibAlloc, nullptr, &m_indexMemory) != VK_SUCCESS) {
        m_devDispatch.DestroyBuffer(m_device, m_indexBuffer, nullptr);
        m_indexBuffer = VK_NULL_HANDLE;
        return false;
    }
    m_devDispatch.BindBufferMemory(m_device, m_indexBuffer, m_indexMemory, 0);
    if (m_devDispatch.MapMemory(m_device, m_indexMemory, 0, ibAlloc.allocationSize, 0, &m_mappedIndices) != VK_SUCCESS) {
        return false;
    }

    // 3. Dynamic Uniform Ring Buffer (kStreamingRingSlots * kMaxUniformDrawsPerSlot * 256B)
    const VkDeviceSize totalUniformBytes =
        static_cast<VkDeviceSize>(kStreamingRingSlots) * kMaxUniformDrawsPerSlot * kUniformSlotAlignment;
    VkBufferCreateInfo ubInfo = { VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO };
    ubInfo.size = totalUniformBytes;
    ubInfo.usage = VK_BUFFER_USAGE_UNIFORM_BUFFER_BIT;
    if (m_devDispatch.CreateBuffer(m_device, &ubInfo, nullptr, &m_uniformBuffer) != VK_SUCCESS) return false;

    VkMemoryRequirements ubReqs = {};
    m_devDispatch.GetBufferMemoryRequirements(m_device, m_uniformBuffer, &ubReqs);

    uint32_t ubMemType = FindMemoryType(ubReqs.memoryTypeBits, hostProps);
    if (ubMemType == UINT32_MAX) {
        HON_LOG_ERROR("[RmlUi_Render] FindMemoryType failed for uniform ring buffer (typeBits=0x%x).", ubReqs.memoryTypeBits);
        m_devDispatch.DestroyBuffer(m_device, m_uniformBuffer, nullptr);
        m_uniformBuffer = VK_NULL_HANDLE;
        return false;
    }

    VkMemoryAllocateInfo ubAlloc = { VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO };
    ubAlloc.allocationSize = ubReqs.size;
    ubAlloc.memoryTypeIndex = ubMemType;
    if (m_devDispatch.AllocateMemory(m_device, &ubAlloc, nullptr, &m_uniformMemory) != VK_SUCCESS) {
        m_devDispatch.DestroyBuffer(m_device, m_uniformBuffer, nullptr);
        m_uniformBuffer = VK_NULL_HANDLE;
        return false;
    }
    m_devDispatch.BindBufferMemory(m_device, m_uniformBuffer, m_uniformMemory, 0);
    if (m_devDispatch.MapMemory(m_device, m_uniformMemory, 0, ubAlloc.allocationSize, 0, &m_mappedUniforms) != VK_SUCCESS) {
        return false;
    }

    // Allocate and update Set 0 Dynamic Uniform Descriptor Set for Transform
    VkDescriptorSetAllocateInfo dsAlloc = { VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO };
    dsAlloc.descriptorPool = m_descriptorPool;
    dsAlloc.descriptorSetCount = 1;
    dsAlloc.pSetLayouts = &m_descriptorSetLayoutTransform;
    if (m_devDispatch.AllocateDescriptorSets(m_device, &dsAlloc, &m_descriptorSetTransform) != VK_SUCCESS) {
        HON_LOG_ERROR("[RmlUi_Render] Failed to allocate Set 0 Transform descriptor set.");
        return false;
    }

    VkDescriptorBufferInfo descBuf = {};
    descBuf.buffer = m_uniformBuffer;
    descBuf.offset = 0;
    descBuf.range = sizeof(UniformBlock);

    VkWriteDescriptorSet write = { VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET };
    write.dstSet = m_descriptorSetTransform;
    write.dstBinding = 1; // layout(set=0, binding=1)
    write.descriptorType = VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER_DYNAMIC;
    write.descriptorCount = 1;
    write.pBufferInfo = &descBuf;

    m_devDispatch.UpdateDescriptorSets(m_device, 1, &write, 0, nullptr);
    return true;
}

bool RmlUi_RenderInterface_VK::CreateWhiteTexture() {
    uint8_t whitePixel[4] = { 255, 255, 255, 255 };
    Rml::TextureHandle handle = 0;
    if (!GenerateTextureLocked(handle, whitePixel, Rml::Vector2i(1, 1))) {
        return false;
    }
    m_whiteTextureHandle = handle;
    m_whiteTexture = *reinterpret_cast<VkTextureResource*>(handle);
    return true;
}

void RmlUi_RenderInterface_VK::SetSwapchainImages(const std::vector<VkImage>& images) {
    std::lock_guard<std::mutex> lock(m_renderMutex);
    m_swapchainImages = images;
    CreateFramebuffers();
}

bool RmlUi_RenderInterface_VK::CreateFramebuffers() {
    for (auto fb : m_framebuffers) {
        if (fb != VK_NULL_HANDLE && m_devDispatch.DestroyFramebuffer) {
            m_devDispatch.DestroyFramebuffer(m_device, fb, nullptr);
        }
    }
    m_framebuffers.clear();

    for (auto iv : m_swapchainImageViews) {
        if (iv != VK_NULL_HANDLE && m_devDispatch.DestroyImageView) {
            m_devDispatch.DestroyImageView(m_device, iv, nullptr);
        }
    }
    m_swapchainImageViews.clear();

    if (m_swapchainImages.empty() || m_renderPass == VK_NULL_HANDLE || m_width == 0 || m_height == 0) {
        return false;
    }

    for (size_t i = 0; i < m_swapchainImages.size(); ++i) {
        VkImageViewCreateInfo ivInfo = { VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO };
        ivInfo.image = m_swapchainImages[i];
        ivInfo.viewType = VK_IMAGE_VIEW_TYPE_2D;
        ivInfo.format = m_format;
        ivInfo.components.r = VK_COMPONENT_SWIZZLE_IDENTITY;
        ivInfo.components.g = VK_COMPONENT_SWIZZLE_IDENTITY;
        ivInfo.components.b = VK_COMPONENT_SWIZZLE_IDENTITY;
        ivInfo.components.a = VK_COMPONENT_SWIZZLE_IDENTITY;
        ivInfo.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
        ivInfo.subresourceRange.baseMipLevel = 0;
        ivInfo.subresourceRange.levelCount = 1;
        ivInfo.subresourceRange.baseArrayLayer = 0;
        ivInfo.subresourceRange.layerCount = 1;

        VkImageView imageView = VK_NULL_HANDLE;
        if (m_devDispatch.CreateImageView(m_device, &ivInfo, nullptr, &imageView) != VK_SUCCESS) {
            return false;
        }
        m_swapchainImageViews.push_back(imageView);

        VkFramebufferCreateInfo fbInfo = { VK_STRUCTURE_TYPE_FRAMEBUFFER_CREATE_INFO };
        fbInfo.renderPass = m_renderPass;
        fbInfo.attachmentCount = 1;
        fbInfo.pAttachments = &m_swapchainImageViews.back();
        fbInfo.width = m_width;
        fbInfo.height = m_height;
        fbInfo.layers = 1;

        VkFramebuffer fb = VK_NULL_HANDLE;
        if (m_devDispatch.CreateFramebuffer(m_device, &fbInfo, nullptr, &fb) != VK_SUCCESS) {
            return false;
        }
        m_framebuffers.push_back(fb);
    }

    HON_LOG_INFO("[RmlUi_Render] Created %zu framebuffers for swapchain.", m_framebuffers.size());
    return true;
}

void RmlUi_RenderInterface_VK::ReleaseSwapchainResources() {
    std::lock_guard<std::mutex> lock(m_renderMutex);
    if (m_device == VK_NULL_HANDLE) return;

    for (auto fb : m_framebuffers) {
        if (fb != VK_NULL_HANDLE && m_devDispatch.DestroyFramebuffer) {
            m_devDispatch.DestroyFramebuffer(m_device, fb, nullptr);
        }
    }
    m_framebuffers.clear();

    for (auto iv : m_swapchainImageViews) {
        if (iv != VK_NULL_HANDLE && m_devDispatch.DestroyImageView) {
            m_devDispatch.DestroyImageView(m_device, iv, nullptr);
        }
    }
    m_swapchainImageViews.clear();
    m_swapchainImages.clear();
    HON_LOG_INFO("[RmlUi_Render] Released all swapchain framebuffers and image views.");
}

void RmlUi_RenderInterface_VK::OnResize(uint32_t width, uint32_t height) {
    std::lock_guard<std::mutex> lock(m_renderMutex);
    m_width = width;
    m_height = height;
    if (!m_swapchainImages.empty() && width > 0 && height > 0) {
        CreateFramebuffers();
    }
}

void RmlUi_RenderInterface_VK::RetireStagingUploads(bool forceAll) {
    if (m_device == VK_NULL_HANDLE || m_uploadCommandPool == VK_NULL_HANDLE) {
        return;
    }

    for (auto it = m_pendingStagingUploads.begin(); it != m_pendingStagingUploads.end();) {
        bool isDone = false;
        if (forceAll) {
            if (it->fence != VK_NULL_HANDLE && m_devDispatch.WaitForFences) {
                VkResult waitRes = m_devDispatch.WaitForFences(m_device, 1, &it->fence, VK_TRUE, 250000000ULL);
                if (waitRes != VK_SUCCESS) {
                    HON_LOG_WARN("[RmlUi_Render] Staging upload fence wait returned %d during teardown.", waitRes);
                }
            }
            isDone = true;
        } else if (it->fence != VK_NULL_HANDLE && m_devDispatch.GetFenceStatus) {
            VkResult res = m_devDispatch.GetFenceStatus(m_device, it->fence);
            if (res == VK_SUCCESS) {
                isDone = true;
            }
        }

        if (isDone) {
            if (it->fence != VK_NULL_HANDLE && m_devDispatch.DestroyFence) {
                m_devDispatch.DestroyFence(m_device, it->fence, nullptr);
            }
            if (it->cmdBuffer != VK_NULL_HANDLE && m_devDispatch.FreeCommandBuffers) {
                m_devDispatch.FreeCommandBuffers(m_device, m_uploadCommandPool, 1, &it->cmdBuffer);
            }
            if (it->stagingBuffer != VK_NULL_HANDLE && m_devDispatch.DestroyBuffer) {
                m_devDispatch.DestroyBuffer(m_device, it->stagingBuffer, nullptr);
            }
            if (it->stagingSuballoc.isValid()) {
                m_suballocator.Free(it->stagingSuballoc);
            }
            it = m_pendingStagingUploads.erase(it);
        } else {
            ++it;
        }
    }
}

void RmlUi_RenderInterface_VK::NotifyFrameFenceCompleted(uint32_t /*frameSlot*/) {
    std::lock_guard<std::mutex> lock(m_renderMutex);
    ++m_completedFenceTick;
    DrainRetiredResources(false);
    RetireStagingUploads(false);
}

bool RmlUi_RenderInterface_VK::BeginFrame(VkCommandBuffer cmdBuffer, uint32_t imageIndex, uint32_t frameSlot) {
    if (!m_isInitialized || imageIndex >= m_framebuffers.size() || m_width == 0 || m_height == 0) {
        return false;
    }

    {
        std::lock_guard<std::mutex> lock(m_renderMutex);
        RetireStagingUploads(false);
    }

    m_currentCmdBuffer = cmdBuffer;
    m_currentImageIndex = imageIndex;
    m_activeRingSlot = frameSlot % kStreamingRingSlots;
    m_vertexOffset = 0;
    m_indexOffset = 0;
    m_uniformDrawIndexInSlot = 0;
    m_scissorEnabled = false;
    m_hasTransform = false;
    m_currentTransform = Rml::Matrix4f::Identity();

    VkRenderPassBeginInfo rpBegin = { VK_STRUCTURE_TYPE_RENDER_PASS_BEGIN_INFO };
    rpBegin.renderPass = m_renderPass;
    rpBegin.framebuffer = m_framebuffers[imageIndex];
    rpBegin.renderArea.offset = { 0, 0 };
    rpBegin.renderArea.extent = { m_width, m_height };
    rpBegin.clearValueCount = 0;
    rpBegin.pClearValues = nullptr;

    m_devDispatch.CmdBeginRenderPass(m_currentCmdBuffer, &rpBegin, VK_SUBPASS_CONTENTS_INLINE);
    m_inRenderPass = true;

    VkViewport viewport = { 0.0f, 0.0f, static_cast<float>(m_width), static_cast<float>(m_height), 0.0f, 1.0f };
    m_devDispatch.CmdSetViewport(m_currentCmdBuffer, 0, 1, &viewport);

    VkRect2D defaultScissor = { { 0, 0 }, { m_width, m_height } };
    m_devDispatch.CmdSetScissor(m_currentCmdBuffer, 0, 1, &defaultScissor);

    static bool s_hasLoggedBf = false;
    if (!s_hasLoggedBf) {
        s_hasLoggedBf = true;
        HON_LOG_INFO("[RmlUi_Render] First frame rendered: img=%u, ringSlot=%u, fbs=%zu, dims=%ux%u",
                     imageIndex, m_activeRingSlot, m_framebuffers.size(), m_width, m_height);
    }

    return true;
}

void RmlUi_RenderInterface_VK::EndFrame() {
    if (m_inRenderPass && m_currentCmdBuffer != VK_NULL_HANDLE) {
        m_devDispatch.CmdEndRenderPass(m_currentCmdBuffer);
        m_inRenderPass = false;
        m_currentCmdBuffer = VK_NULL_HANDLE;
    }
}

bool RmlUi_RenderInterface_VK::WriteDrawUniformAndGetOffset(const Rml::Vector2f& translation, uint32_t& outDynamicOffset) {
    if (!m_mappedUniforms || m_width == 0 || m_height == 0) return false;
    if (m_uniformDrawIndexInSlot >= kMaxUniformDrawsPerSlot) {
        HON_LOG_WARN("[RmlUi_Render] Dynamic uniform draw slot cap (%u) reached in frame slot %u.",
                     kMaxUniformDrawsPerSlot, m_activeRingSlot);
        return false;
    }

    const uint32_t slotIndex = m_activeRingSlot * kMaxUniformDrawsPerSlot + m_uniformDrawIndexInSlot;
    ++m_uniformDrawIndexInSlot;
    outDynamicOffset = slotIndex * kUniformSlotAlignment;

    UniformBlock ub = {};
    const float w = static_cast<float>(m_width);
    const float h = static_cast<float>(m_height);

    const float proj[16] = {
        2.0f / w,  0.0f,      0.0f, 0.0f,
        0.0f,      2.0f / h,  0.0f, 0.0f,
        0.0f,      0.0f,      1.0f, 0.0f,
       -1.0f,     -1.0f,      0.0f, 1.0f
    };

    if (m_hasTransform) {
        const float* t = m_currentTransform.data();
        for (int c = 0; c < 4; ++c) {
            for (int r = 0; r < 4; ++r) {
                ub.transform[c * 4 + r] =
                    proj[0 * 4 + r] * t[c * 4 + 0] +
                    proj[1 * 4 + r] * t[c * 4 + 1] +
                    proj[2 * 4 + r] * t[c * 4 + 2] +
                    proj[3 * 4 + r] * t[c * 4 + 3];
            }
        }
    } else {
        std::memcpy(ub.transform, proj, sizeof(proj));
    }

    ub.translate[0] = translation.x;
    ub.translate[1] = translation.y;

    uint8_t* dst = static_cast<uint8_t*>(m_mappedUniforms) + outDynamicOffset;
    std::memcpy(dst, &ub, sizeof(ub));
    return true;
}

VkTextureResource* RmlUi_RenderInterface_VK::ResolveTextureResource(Rml::TextureHandle texture) {
    if (texture != 0) {
        for (const auto& tex : m_allocatedTextures) {
            if (tex.get() == reinterpret_cast<VkTextureResource*>(texture)) {
                if (tex->descriptorSet != VK_NULL_HANDLE) {
                    return tex.get();
                }
                break;
            }
        }
    }
    return &m_whiteTexture;
}

void RmlUi_RenderInterface_VK::RenderGeometry(
    Rml::Vertex* vertices,
    int num_vertices,
    int* indices,
    int num_indices,
    Rml::TextureHandle texture,
    const Rml::Vector2f& translation)
{
    if (!m_inRenderPass || !m_currentCmdBuffer || !vertices || !indices || num_vertices <= 0 || num_indices <= 0) {
        return;
    }

    if (m_vertexOffset + static_cast<uint32_t>(num_vertices) > m_verticesPerSlot ||
        m_indexOffset + static_cast<uint32_t>(num_indices) > m_indicesPerSlot) {
        HON_LOG_WARN("[RmlUi_Render] Streaming geometry slice %u full, skipping draw (%d verts, %d inds).",
                     m_activeRingSlot, num_vertices, num_indices);
        return;
    }

    uint32_t dynamicUniformOffset = 0;
    if (!WriteDrawUniformAndGetOffset(translation, dynamicUniformOffset)) {
        return;
    }

    const uint32_t baseVertexInRing = m_activeRingSlot * m_verticesPerSlot + m_vertexOffset;
    const uint32_t baseIndexInRing = m_activeRingSlot * m_indicesPerSlot + m_indexOffset;

    std::memcpy(static_cast<Rml::Vertex*>(m_mappedVertices) + baseVertexInRing,
                vertices,
                static_cast<size_t>(num_vertices) * sizeof(Rml::Vertex));
    std::memcpy(static_cast<int*>(m_mappedIndices) + baseIndexInRing,
                indices,
                static_cast<size_t>(num_indices) * sizeof(int));

    VkTextureResource* texRes = ResolveTextureResource(texture);
    VkPipeline pipeline = (texture != 0 && texRes != &m_whiteTexture) ? m_pipelineTexture : m_pipelineColor;
    m_devDispatch.CmdBindPipeline(m_currentCmdBuffer, VK_PIPELINE_BIND_POINT_GRAPHICS, pipeline);

    m_devDispatch.CmdBindDescriptorSets(
        m_currentCmdBuffer,
        VK_PIPELINE_BIND_POINT_GRAPHICS,
        m_pipelineLayout,
        0,
        1,
        &m_descriptorSetTransform,
        1,
        &dynamicUniformOffset);

    if (texRes && texRes->descriptorSet != VK_NULL_HANDLE) {
        m_devDispatch.CmdBindDescriptorSets(
            m_currentCmdBuffer,
            VK_PIPELINE_BIND_POINT_GRAPHICS,
            m_pipelineLayout,
            1,
            1,
            &texRes->descriptorSet,
            0,
            nullptr);
    }

    VkDeviceSize vertexOffsetBytes = static_cast<VkDeviceSize>(baseVertexInRing) * sizeof(Rml::Vertex);
    m_devDispatch.CmdBindVertexBuffers(m_currentCmdBuffer, 0, 1, &m_vertexBuffer, &vertexOffsetBytes);

    VkDeviceSize indexOffsetBytes = static_cast<VkDeviceSize>(baseIndexInRing) * sizeof(int);
    m_devDispatch.CmdBindIndexBuffer(m_currentCmdBuffer, m_indexBuffer, indexOffsetBytes, VK_INDEX_TYPE_UINT32);

    m_devDispatch.CmdDrawIndexed(m_currentCmdBuffer, static_cast<uint32_t>(num_indices), 1, 0, 0, 0);

    m_vertexOffset += static_cast<uint32_t>(num_vertices);
    m_indexOffset += static_cast<uint32_t>(num_indices);
}

Rml::CompiledGeometryHandle RmlUi_RenderInterface_VK::CompileGeometry(
    Rml::Vertex* vertices,
    int num_vertices,
    int* indices,
    int num_indices,
    Rml::TextureHandle texture)
{
    std::lock_guard<std::mutex> lock(m_renderMutex);
    if (!m_isInitialized || m_device == VK_NULL_HANDLE || !vertices || !indices || num_vertices <= 0 || num_indices <= 0) {
        return 0;
    }

    const VkMemoryPropertyFlags hostProps = VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT;
    VkCompiledGeometryResource geom = {};
    geom.numIndices = num_indices;
    geom.texture = texture;

    // 1. Vertex Buffer
    const VkDeviceSize vbBytes = static_cast<VkDeviceSize>(num_vertices) * sizeof(Rml::Vertex);
    VkBufferCreateInfo vbInfo = { VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO };
    vbInfo.size = vbBytes;
    vbInfo.usage = VK_BUFFER_USAGE_VERTEX_BUFFER_BIT;
    if (m_devDispatch.CreateBuffer(m_device, &vbInfo, nullptr, &geom.vertexBuffer) != VK_SUCCESS) {
        return 0;
    }

    VkMemoryRequirements vbReqs = {};
    m_devDispatch.GetBufferMemoryRequirements(m_device, geom.vertexBuffer, &vbReqs);
    uint32_t vbMemType = FindMemoryType(vbReqs.memoryTypeBits, hostProps);
    if (vbMemType == UINT32_MAX) {
        DestroyCompiledGeometryImmediate(geom);
        return 0;
    }

    if (!m_suballocator.Allocate(vbMemType, true, vbReqs.size, vbReqs.alignment, geom.vertexSuballocation, false)) {
        DestroyCompiledGeometryImmediate(geom);
        return 0;
    }
    geom.vertexMemory = geom.vertexSuballocation.memory;
    m_devDispatch.BindBufferMemory(m_device, geom.vertexBuffer, geom.vertexSuballocation.memory, geom.vertexSuballocation.offset);

    if (!geom.vertexSuballocation.mappedData) {
        DestroyCompiledGeometryImmediate(geom);
        return 0;
    }
    std::memcpy(geom.vertexSuballocation.mappedData, vertices, static_cast<size_t>(vbBytes));

    // 2. Index Buffer
    const VkDeviceSize ibBytes = static_cast<VkDeviceSize>(num_indices) * sizeof(int);
    VkBufferCreateInfo ibInfo = { VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO };
    ibInfo.size = ibBytes;
    ibInfo.usage = VK_BUFFER_USAGE_INDEX_BUFFER_BIT;
    if (m_devDispatch.CreateBuffer(m_device, &ibInfo, nullptr, &geom.indexBuffer) != VK_SUCCESS) {
        DestroyCompiledGeometryImmediate(geom);
        return 0;
    }

    VkMemoryRequirements ibReqs = {};
    m_devDispatch.GetBufferMemoryRequirements(m_device, geom.indexBuffer, &ibReqs);
    uint32_t ibMemType = FindMemoryType(ibReqs.memoryTypeBits, hostProps);
    if (ibMemType == UINT32_MAX) {
        DestroyCompiledGeometryImmediate(geom);
        return 0;
    }

    if (!m_suballocator.Allocate(ibMemType, true, ibReqs.size, ibReqs.alignment, geom.indexSuballocation, false)) {
        DestroyCompiledGeometryImmediate(geom);
        return 0;
    }
    geom.indexMemory = geom.indexSuballocation.memory;
    m_devDispatch.BindBufferMemory(m_device, geom.indexBuffer, geom.indexSuballocation.memory, geom.indexSuballocation.offset);

    if (!geom.indexSuballocation.mappedData) {
        DestroyCompiledGeometryImmediate(geom);
        return 0;
    }
    std::memcpy(geom.indexSuballocation.mappedData, indices, static_cast<size_t>(ibBytes));

    const Rml::CompiledGeometryHandle handle = static_cast<Rml::CompiledGeometryHandle>(++m_nextGeometryHandle);
    m_compiledGeometries.emplace(handle, geom);
    return handle;
}

void RmlUi_RenderInterface_VK::RenderCompiledGeometry(
    Rml::CompiledGeometryHandle geometry,
    const Rml::Vector2f& translation)
{
    if (!m_inRenderPass || !m_currentCmdBuffer || geometry == 0) return;

    VkCompiledGeometryResource geom = {};
    VkTextureResource* texRes = nullptr;
    {
        std::lock_guard<std::mutex> lock(m_renderMutex);
        auto it = m_compiledGeometries.find(geometry);
        if (it == m_compiledGeometries.end()) return;
        geom = it->second;
        texRes = ResolveTextureResource(geom.texture);
    }

    if (geom.vertexBuffer == VK_NULL_HANDLE || geom.indexBuffer == VK_NULL_HANDLE || geom.numIndices <= 0) {
        return;
    }

    uint32_t dynamicUniformOffset = 0;
    if (!WriteDrawUniformAndGetOffset(translation, dynamicUniformOffset)) {
        return;
    }

    VkPipeline pipeline = (geom.texture != 0 && texRes != &m_whiteTexture) ? m_pipelineTexture : m_pipelineColor;
    m_devDispatch.CmdBindPipeline(m_currentCmdBuffer, VK_PIPELINE_BIND_POINT_GRAPHICS, pipeline);

    m_devDispatch.CmdBindDescriptorSets(
        m_currentCmdBuffer,
        VK_PIPELINE_BIND_POINT_GRAPHICS,
        m_pipelineLayout,
        0,
        1,
        &m_descriptorSetTransform,
        1,
        &dynamicUniformOffset);

    if (texRes && texRes->descriptorSet != VK_NULL_HANDLE) {
        m_devDispatch.CmdBindDescriptorSets(
            m_currentCmdBuffer,
            VK_PIPELINE_BIND_POINT_GRAPHICS,
            m_pipelineLayout,
            1,
            1,
            &texRes->descriptorSet,
            0,
            nullptr);
    }

    VkDeviceSize zeroOffset = 0;
    m_devDispatch.CmdBindVertexBuffers(m_currentCmdBuffer, 0, 1, &geom.vertexBuffer, &zeroOffset);
    m_devDispatch.CmdBindIndexBuffer(m_currentCmdBuffer, geom.indexBuffer, 0, VK_INDEX_TYPE_UINT32);
    m_devDispatch.CmdDrawIndexed(m_currentCmdBuffer, static_cast<uint32_t>(geom.numIndices), 1, 0, 0, 0);
}

void RmlUi_RenderInterface_VK::ReleaseCompiledGeometry(Rml::CompiledGeometryHandle geometry) {
    if (!geometry) return;
    std::lock_guard<std::mutex> lock(m_renderMutex);
    auto it = m_compiledGeometries.find(geometry);
    if (it == m_compiledGeometries.end()) return;

    RetiredGeometryEntry retired = {};
    retired.retireAfterTick = m_completedFenceTick + kFenceRetireLag;
    retired.resource = it->second;
    m_retiredGeometries.push_back(retired);
    m_compiledGeometries.erase(it);
}

void RmlUi_RenderInterface_VK::EnableScissorRegion(bool enable) {
    m_scissorEnabled = enable;
    if (!m_inRenderPass || !m_currentCmdBuffer) return;

    if (!enable) {
        VkRect2D fullScissor = { { 0, 0 }, { m_width, m_height } };
        m_devDispatch.CmdSetScissor(m_currentCmdBuffer, 0, 1, &fullScissor);
    } else {
        m_devDispatch.CmdSetScissor(m_currentCmdBuffer, 0, 1, &m_currentScissor);
    }
}

void RmlUi_RenderInterface_VK::SetScissorRegion(int x, int y, int width, int height) {
    int clampedX = (std::max)(0, x);
    int clampedY = (std::max)(0, y);
    uint32_t clampedW = static_cast<uint32_t>((std::max)(0, (std::min)(width, static_cast<int>(m_width) - clampedX)));
    uint32_t clampedH = static_cast<uint32_t>((std::max)(0, (std::min)(height, static_cast<int>(m_height) - clampedY)));

    m_currentScissor.offset = { clampedX, clampedY };
    m_currentScissor.extent = { clampedW, clampedH };

    if (m_inRenderPass && m_currentCmdBuffer && m_scissorEnabled) {
        m_devDispatch.CmdSetScissor(m_currentCmdBuffer, 0, 1, &m_currentScissor);
    }
}

bool RmlUi_RenderInterface_VK::LoadTexture(Rml::TextureHandle& texture_handle, Rml::Vector2i& texture_dimensions, const Rml::String& source) {
    texture_handle = 0;
    texture_dimensions = Rml::Vector2i(0, 0);

    if (source == "?tao_qr" || source.find("tao_qr") != std::string::npos) {
        if (!m_qrTexture) {
            std::vector<uint32_t> initialPixels;
            TaoQrMatrix::GenerateQrRgba("{\"tao\":\"1.0\",\"status\":\"ready\"}", 600, 600, initialPixels);
            UpdateQrTexture(reinterpret_cast<const uint8_t*>(initialPixels.data()), 600, 600);
        }
        if (m_qrTexture) {
            texture_handle = m_qrTextureHandle;
            texture_dimensions = Rml::Vector2i(m_qrTexture->width, m_qrTexture->height);
            HON_LOG_INFO("[RmlUi_Render] Bound high-density QR texture (%dx%d) for source: %s", m_qrTexture->width, m_qrTexture->height, source.c_str());
            return true;
        }
    }

    HON_LOG_DEBUG("[RmlUi_Render] LoadTexture requested: %s", source.c_str());
    return false;
}

bool RmlUi_RenderInterface_VK::GenerateTexture(
    Rml::TextureHandle& texture_handle,
    const Rml::byte* source,
    const Rml::Vector2i& source_dimensions)
{
    std::lock_guard<std::mutex> lock(m_renderMutex);
    return GenerateTextureLocked(texture_handle, source, source_dimensions);
}

bool RmlUi_RenderInterface_VK::GenerateTextureLocked(
    Rml::TextureHandle& texture_handle,
    const Rml::byte* source,
    const Rml::Vector2i& source_dimensions)
{
    if (!source || source_dimensions.x <= 0 || source_dimensions.y <= 0 || m_device == VK_NULL_HANDLE) {
        return false;
    }

    auto tex = std::make_unique<VkTextureResource>();
    tex->width = static_cast<uint32_t>(source_dimensions.x);
    tex->height = static_cast<uint32_t>(source_dimensions.y);
    const VkDeviceSize imgSize = static_cast<VkDeviceSize>(tex->width) * tex->height * 4;

    // 1. Staging Buffer
    VkBuffer stagingBuffer = VK_NULL_HANDLE;
    VkBufferCreateInfo stgInfo = { VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO };
    stgInfo.size = imgSize;
    stgInfo.usage = VK_BUFFER_USAGE_TRANSFER_SRC_BIT;
    if (m_devDispatch.CreateBuffer(m_device, &stgInfo, nullptr, &stagingBuffer) != VK_SUCCESS) return false;

    VkMemoryRequirements stgReqs = {};
    m_devDispatch.GetBufferMemoryRequirements(m_device, stagingBuffer, &stgReqs);
    uint32_t stgMemType = FindMemoryType(stgReqs.memoryTypeBits, VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT);
    if (stgMemType == UINT32_MAX) {
        HON_LOG_ERROR("[RmlUi_Render] FindMemoryType failed for texture staging buffer (typeBits=0x%x).", stgReqs.memoryTypeBits);
        m_devDispatch.DestroyBuffer(m_device, stagingBuffer, nullptr);
        return false;
    }

    VkSuballocation stagingSuballoc;
    if (!m_suballocator.Allocate(stgMemType, true, stgReqs.size, stgReqs.alignment, stagingSuballoc, false)) {
        HON_LOG_ERROR("[RmlUi_Render] Suballocator failed for texture staging buffer.");
        m_devDispatch.DestroyBuffer(m_device, stagingBuffer, nullptr);
        return false;
    }
    m_devDispatch.BindBufferMemory(m_device, stagingBuffer, stagingSuballoc.memory, stagingSuballoc.offset);

    if (!stagingSuballoc.mappedData) {
        HON_LOG_ERROR("[RmlUi_Render] Persistent mapping unavailable for staging suballocation.");
        m_devDispatch.DestroyBuffer(m_device, stagingBuffer, nullptr);
        m_suballocator.Free(stagingSuballoc);
        return false;
    }
    std::memcpy(stagingSuballoc.mappedData, source, static_cast<size_t>(imgSize));

    // 2. Device Image
    VkImageCreateInfo imgInfo = { VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO };
    imgInfo.imageType = VK_IMAGE_TYPE_2D;
    imgInfo.extent.width = tex->width;
    imgInfo.extent.height = tex->height;
    imgInfo.extent.depth = 1;
    imgInfo.mipLevels = 1;
    imgInfo.arrayLayers = 1;
    imgInfo.format = VK_FORMAT_R8G8B8A8_UNORM;
    imgInfo.tiling = VK_IMAGE_TILING_OPTIMAL;
    imgInfo.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
    imgInfo.usage = VK_IMAGE_USAGE_TRANSFER_DST_BIT | VK_IMAGE_USAGE_SAMPLED_BIT;
    imgInfo.samples = VK_SAMPLE_COUNT_1_BIT;
    imgInfo.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
    if (m_devDispatch.CreateImage(m_device, &imgInfo, nullptr, &tex->image) != VK_SUCCESS) {
        m_devDispatch.DestroyBuffer(m_device, stagingBuffer, nullptr);
        m_suballocator.Free(stagingSuballoc);
        return false;
    }

    VkMemoryRequirements imgReqs = {};
    m_devDispatch.GetImageMemoryRequirements(m_device, tex->image, &imgReqs);
    uint32_t imgMemType = FindMemoryType(imgReqs.memoryTypeBits, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT);
    if (imgMemType == UINT32_MAX) {
        imgMemType = FindMemoryType(imgReqs.memoryTypeBits, VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT);
    }
    if (imgMemType == UINT32_MAX) {
        imgMemType = FindMemoryType(imgReqs.memoryTypeBits, VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT);
    }
    if (imgMemType == UINT32_MAX) {
        HON_LOG_ERROR("[RmlUi_Render] FindMemoryType failed for texture image (typeBits=0x%x).", imgReqs.memoryTypeBits);
        m_devDispatch.DestroyImage(m_device, tex->image, nullptr);
        m_devDispatch.DestroyBuffer(m_device, stagingBuffer, nullptr);
        m_suballocator.Free(stagingSuballoc);
        return false;
    }

    const bool isHostVisibleImage = (imgMemType != FindMemoryType(imgReqs.memoryTypeBits, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT));
    if (!m_suballocator.Allocate(imgMemType, isHostVisibleImage, imgReqs.size, imgReqs.alignment, tex->suballocation, true)) {
        HON_LOG_ERROR("[RmlUi_Render] Suballocator failed for texture image (size=%llu).",
                      static_cast<unsigned long long>(imgReqs.size));
        m_devDispatch.DestroyImage(m_device, tex->image, nullptr);
        m_devDispatch.DestroyBuffer(m_device, stagingBuffer, nullptr);
        m_suballocator.Free(stagingSuballoc);
        return false;
    }
    tex->memory = tex->suballocation.memory;
    m_devDispatch.BindImageMemory(m_device, tex->image, tex->suballocation.memory, tex->suballocation.offset);

    // 3. Image View, Sampler & Descriptor Set BEFORE GPU submission (prevents GPU UAF on allocation failure)
    VkImageViewCreateInfo ivInfo = { VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO };
    ivInfo.image = tex->image;
    ivInfo.viewType = VK_IMAGE_VIEW_TYPE_2D;
    ivInfo.format = VK_FORMAT_R8G8B8A8_UNORM;
    ivInfo.subresourceRange = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1 };
    if (m_devDispatch.CreateImageView(m_device, &ivInfo, nullptr, &tex->view) != VK_SUCCESS) {
        DestroyTextureResourceImmediate(*tex);
        m_devDispatch.DestroyBuffer(m_device, stagingBuffer, nullptr);
        m_suballocator.Free(stagingSuballoc);
        return false;
    }

    VkSamplerCreateInfo smpInfo = { VK_STRUCTURE_TYPE_SAMPLER_CREATE_INFO };
    smpInfo.magFilter = VK_FILTER_LINEAR;
    smpInfo.minFilter = VK_FILTER_LINEAR;
    smpInfo.mipmapMode = VK_SAMPLER_MIPMAP_MODE_LINEAR;
    smpInfo.addressModeU = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
    smpInfo.addressModeV = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
    smpInfo.addressModeW = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
    if (m_devDispatch.CreateSampler(m_device, &smpInfo, nullptr, &tex->sampler) != VK_SUCCESS) {
        DestroyTextureResourceImmediate(*tex);
        m_devDispatch.DestroyBuffer(m_device, stagingBuffer, nullptr);
        m_suballocator.Free(stagingSuballoc);
        return false;
    }

    VkDescriptorSetAllocateInfo dsAlloc = { VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO };
    dsAlloc.descriptorPool = m_descriptorPool;
    dsAlloc.descriptorSetCount = 1;
    dsAlloc.pSetLayouts = &m_descriptorSetLayoutTexture;
    if (m_devDispatch.AllocateDescriptorSets(m_device, &dsAlloc, &tex->descriptorSet) != VK_SUCCESS) {
        HON_LOG_ERROR("[RmlUi_Render] Failed to allocate Set 1 texture descriptor set (live=%zu, retired=%zu).",
                      m_allocatedTextures.size(), m_retiredTextures.size());
        DestroyTextureResourceImmediate(*tex);
        m_devDispatch.DestroyBuffer(m_device, stagingBuffer, nullptr);
        m_suballocator.Free(stagingSuballoc);
        return false;
    }

    VkDescriptorImageInfo descImage = {};
    descImage.sampler = tex->sampler;
    descImage.imageView = tex->view;
    descImage.imageLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;

    VkWriteDescriptorSet write = { VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET };
    write.dstSet = tex->descriptorSet;
    write.dstBinding = 2; // layout(set=1, binding=2)
    write.descriptorType = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER;
    write.descriptorCount = 1;
    write.pImageInfo = &descImage;

    m_devDispatch.UpdateDescriptorSets(m_device, 1, &write, 0, nullptr);

    // 4. Command Buffer for copy from persistent pool & async fence submission
    VkCommandBuffer cmdBuf = VK_NULL_HANDLE;
    VkCommandBufferAllocateInfo cbAlloc = { VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO };
    cbAlloc.commandPool = m_uploadCommandPool;
    cbAlloc.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
    cbAlloc.commandBufferCount = 1;
    if (m_devDispatch.AllocateCommandBuffers(m_device, &cbAlloc, &cmdBuf) != VK_SUCCESS) {
        DestroyTextureResourceImmediate(*tex);
        m_devDispatch.DestroyBuffer(m_device, stagingBuffer, nullptr);
        m_suballocator.Free(stagingSuballoc);
        return false;
    }

    VkCommandBufferBeginInfo beginInfo = { VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO };
    beginInfo.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
    m_devDispatch.BeginCommandBuffer(cmdBuf, &beginInfo);

    VkImageMemoryBarrier barrier1 = { VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER };
    barrier1.oldLayout = VK_IMAGE_LAYOUT_UNDEFINED;
    barrier1.newLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
    barrier1.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    barrier1.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    barrier1.image = tex->image;
    barrier1.subresourceRange = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1 };
    barrier1.srcAccessMask = 0;
    barrier1.dstAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT;
    m_devDispatch.CmdPipelineBarrier(cmdBuf, VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT,
                                     0, 0, nullptr, 0, nullptr, 1, &barrier1);

    VkBufferImageCopy region = {};
    region.bufferOffset = 0;
    region.imageSubresource = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1 };
    region.imageExtent = { tex->width, tex->height, 1 };
    m_devDispatch.CmdCopyBufferToImage(cmdBuf, stagingBuffer, tex->image, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &region);

    VkImageMemoryBarrier barrier2 = { VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER };
    barrier2.oldLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
    barrier2.newLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
    barrier2.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    barrier2.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    barrier2.image = tex->image;
    barrier2.subresourceRange = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1 };
    barrier2.srcAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT;
    barrier2.dstAccessMask = VK_ACCESS_SHADER_READ_BIT;
    m_devDispatch.CmdPipelineBarrier(cmdBuf, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT,
                                     0, 0, nullptr, 0, nullptr, 1, &barrier2);

    m_devDispatch.EndCommandBuffer(cmdBuf);

    VkFence uploadFence = VK_NULL_HANDLE;
    VkFenceCreateInfo fenceInfo = { VK_STRUCTURE_TYPE_FENCE_CREATE_INFO };
    if (m_devDispatch.CreateFence(m_device, &fenceInfo, nullptr, &uploadFence) != VK_SUCCESS) {
        DestroyTextureResourceImmediate(*tex);
        m_devDispatch.DestroyBuffer(m_device, stagingBuffer, nullptr);
        m_suballocator.Free(stagingSuballoc);
        m_devDispatch.FreeCommandBuffers(m_device, m_uploadCommandPool, 1, &cmdBuf);
        return false;
    }

    VkSubmitInfo submit = { VK_STRUCTURE_TYPE_SUBMIT_INFO };
    submit.commandBufferCount = 1;
    submit.pCommandBuffers = &cmdBuf;
    if (m_devDispatch.QueueSubmit(m_queue, 1, &submit, uploadFence) != VK_SUCCESS) {
        HON_LOG_ERROR("[RmlUi_Render] vkQueueSubmit failed for async texture upload.");
        m_devDispatch.DestroyFence(m_device, uploadFence, nullptr);
        DestroyTextureResourceImmediate(*tex);
        m_devDispatch.DestroyBuffer(m_device, stagingBuffer, nullptr);
        m_suballocator.Free(stagingSuballoc);
        m_devDispatch.FreeCommandBuffers(m_device, m_uploadCommandPool, 1, &cmdBuf);
        return false;
    }

    PendingStagingUpload upload = {};
    upload.fence = uploadFence;
    upload.cmdBuffer = cmdBuf;
    upload.stagingBuffer = stagingBuffer;
    upload.stagingSuballoc = stagingSuballoc;
    m_pendingStagingUploads.push_back(upload);

    VkTextureResource* rawPtr = tex.get();
    m_allocatedTextures.push_back(std::move(tex));
    texture_handle = reinterpret_cast<Rml::TextureHandle>(rawPtr);
    return true;
}

bool RmlUi_RenderInterface_VK::UpdateQrTexture(const uint8_t* /*rgbaPixels*/, int /*width*/, int /*height*/) {
    // QR code updates disabled to eliminate periodic GPU QueueWaitIdle pipeline stalls
    return true;
}

void RmlUi_RenderInterface_VK::ReleaseTexture(Rml::TextureHandle texture_handle) {
    if (!texture_handle || texture_handle == m_whiteTextureHandle) return;
    std::lock_guard<std::mutex> lock(m_renderMutex);

    VkTextureResource* res = reinterpret_cast<VkTextureResource*>(texture_handle);
    if (res == m_qrTexture) {
        m_qrTexture = nullptr;
        m_qrTextureHandle = 0;
    }

    for (auto it = m_allocatedTextures.begin(); it != m_allocatedTextures.end(); ++it) {
        if (it->get() == res) {
            RetiredTextureEntry retired = {};
            retired.retireAfterTick = m_completedFenceTick + kFenceRetireLag;
            retired.resource = std::move(*it);
            m_retiredTextures.push_back(std::move(retired));
            m_allocatedTextures.erase(it);
            break;
        }
    }
}

void RmlUi_RenderInterface_VK::SetTransform(const Rml::Matrix4f* transform) {
    if (transform) {
        m_currentTransform = *transform;
        m_hasTransform = true;
    } else {
        m_currentTransform = Rml::Matrix4f::Identity();
        m_hasTransform = false;
    }
}

void RmlUi_RenderInterface_VK::DestroyTextureResourceImmediate(VkTextureResource& tex) {
    if (m_device == VK_NULL_HANDLE) return;
    if (tex.descriptorSet != VK_NULL_HANDLE && m_descriptorPool != VK_NULL_HANDLE && m_devDispatch.FreeDescriptorSets) {
        m_devDispatch.FreeDescriptorSets(m_device, m_descriptorPool, 1, &tex.descriptorSet);
        tex.descriptorSet = VK_NULL_HANDLE;
    }
    if (tex.sampler != VK_NULL_HANDLE && m_devDispatch.DestroySampler) {
        m_devDispatch.DestroySampler(m_device, tex.sampler, nullptr);
        tex.sampler = VK_NULL_HANDLE;
    }
    if (tex.view != VK_NULL_HANDLE && m_devDispatch.DestroyImageView) {
        m_devDispatch.DestroyImageView(m_device, tex.view, nullptr);
        tex.view = VK_NULL_HANDLE;
    }
    if (tex.image != VK_NULL_HANDLE && m_devDispatch.DestroyImage) {
        m_devDispatch.DestroyImage(m_device, tex.image, nullptr);
        tex.image = VK_NULL_HANDLE;
    }
    if (tex.suballocation.isValid()) {
        m_suballocator.Free(tex.suballocation);
        tex.suballocation = {};
        tex.memory = VK_NULL_HANDLE;
    } else if (tex.memory != VK_NULL_HANDLE && m_devDispatch.FreeMemory) {
        m_devDispatch.FreeMemory(m_device, tex.memory, nullptr);
        tex.memory = VK_NULL_HANDLE;
    }
}

void RmlUi_RenderInterface_VK::DestroyCompiledGeometryImmediate(VkCompiledGeometryResource& geom) {
    if (m_device == VK_NULL_HANDLE) return;
    if (geom.vertexBuffer != VK_NULL_HANDLE && m_devDispatch.DestroyBuffer) {
        m_devDispatch.DestroyBuffer(m_device, geom.vertexBuffer, nullptr);
        geom.vertexBuffer = VK_NULL_HANDLE;
    }
    if (geom.vertexSuballocation.isValid()) {
        m_suballocator.Free(geom.vertexSuballocation);
        geom.vertexSuballocation = {};
        geom.vertexMemory = VK_NULL_HANDLE;
    } else if (geom.vertexMemory != VK_NULL_HANDLE && m_devDispatch.FreeMemory) {
        m_devDispatch.FreeMemory(m_device, geom.vertexMemory, nullptr);
        geom.vertexMemory = VK_NULL_HANDLE;
    }
    if (geom.indexBuffer != VK_NULL_HANDLE && m_devDispatch.DestroyBuffer) {
        m_devDispatch.DestroyBuffer(m_device, geom.indexBuffer, nullptr);
        geom.indexBuffer = VK_NULL_HANDLE;
    }
    if (geom.indexSuballocation.isValid()) {
        m_suballocator.Free(geom.indexSuballocation);
        geom.indexSuballocation = {};
        geom.indexMemory = VK_NULL_HANDLE;
    } else if (geom.indexMemory != VK_NULL_HANDLE && m_devDispatch.FreeMemory) {
        m_devDispatch.FreeMemory(m_device, geom.indexMemory, nullptr);
        geom.indexMemory = VK_NULL_HANDLE;
    }
    geom.numIndices = 0;
    geom.texture = 0;
}

void RmlUi_RenderInterface_VK::DrainRetiredResources(bool forceAll) {
    if (m_device == VK_NULL_HANDLE) {
        m_retiredTextures.clear();
        m_retiredGeometries.clear();
        return;
    }

    for (auto it = m_retiredTextures.begin(); it != m_retiredTextures.end();) {
        if (forceAll || m_completedFenceTick >= it->retireAfterTick) {
            if (it->resource) {
                DestroyTextureResourceImmediate(*it->resource);
            }
            it = m_retiredTextures.erase(it);
        } else {
            ++it;
        }
    }

    for (auto it = m_retiredGeometries.begin(); it != m_retiredGeometries.end();) {
        if (forceAll || m_completedFenceTick >= it->retireAfterTick) {
            DestroyCompiledGeometryImmediate(it->resource);
            it = m_retiredGeometries.erase(it);
        } else {
            ++it;
        }
    }
}

void RmlUi_RenderInterface_VK::Cleanup() {
    if (m_device == VK_NULL_HANDLE) return;

    if (IsLayerProcessTerminating()) {
        // [L2-P1-02] Never invoke Vulkan ICD teardown under LdrShutdownProcess / OS loader lock.
        m_device = VK_NULL_HANDLE;
        m_physicalDevice = VK_NULL_HANDLE;
        m_queue = VK_NULL_HANDLE;
        m_isInitialized = false;
        return;
    }

    // Fence-only synchronization on shared DXVK VkDevice: never call vkDeviceWaitIdle(m_device)
    // while DXVK's background dxvk-submit thread may still be active on this device.
    // OverlayManager::Cleanup() has already waited on our per-frame fences; wait only on our own
    // pending staging upload fences (bounded to 250ms each) before destroying resources.
    RetireStagingUploads(true);

    for (auto fb : m_framebuffers) {
        if (fb) m_devDispatch.DestroyFramebuffer(m_device, fb, nullptr);
    }
    m_framebuffers.clear();

    for (auto iv : m_swapchainImageViews) {
        if (iv) m_devDispatch.DestroyImageView(m_device, iv, nullptr);
    }
    m_swapchainImageViews.clear();
    m_swapchainImages.clear();

    m_qrTexture = nullptr;
    m_qrTextureHandle = 0;
    m_whiteTexture = {};
    m_whiteTextureHandle = 0;

    DrainRetiredResources(true);

    for (auto& [handle, geom] : m_compiledGeometries) {
        (void)handle;
        DestroyCompiledGeometryImmediate(geom);
    }
    m_compiledGeometries.clear();

    for (auto& tex : m_allocatedTextures) {
        if (tex) {
            DestroyTextureResourceImmediate(*tex);
        }
    }
    m_allocatedTextures.clear();

    if (m_mappedVertices) { m_devDispatch.UnmapMemory(m_device, m_vertexMemory); m_mappedVertices = nullptr; }
    if (m_vertexBuffer) { m_devDispatch.DestroyBuffer(m_device, m_vertexBuffer, nullptr); m_vertexBuffer = VK_NULL_HANDLE; }
    if (m_vertexMemory) { m_devDispatch.FreeMemory(m_device, m_vertexMemory, nullptr); m_vertexMemory = VK_NULL_HANDLE; }

    if (m_mappedIndices) { m_devDispatch.UnmapMemory(m_device, m_indexMemory); m_mappedIndices = nullptr; }
    if (m_indexBuffer) { m_devDispatch.DestroyBuffer(m_device, m_indexBuffer, nullptr); m_indexBuffer = VK_NULL_HANDLE; }
    if (m_indexMemory) { m_devDispatch.FreeMemory(m_device, m_indexMemory, nullptr); m_indexMemory = VK_NULL_HANDLE; }

    if (m_mappedUniforms) { m_devDispatch.UnmapMemory(m_device, m_uniformMemory); m_mappedUniforms = nullptr; }
    if (m_uniformBuffer) { m_devDispatch.DestroyBuffer(m_device, m_uniformBuffer, nullptr); m_uniformBuffer = VK_NULL_HANDLE; }
    if (m_uniformMemory) { m_devDispatch.FreeMemory(m_device, m_uniformMemory, nullptr); m_uniformMemory = VK_NULL_HANDLE; }

    if (m_pipelineColor) { m_devDispatch.DestroyPipeline(m_device, m_pipelineColor, nullptr); m_pipelineColor = VK_NULL_HANDLE; }
    if (m_pipelineTexture) { m_devDispatch.DestroyPipeline(m_device, m_pipelineTexture, nullptr); m_pipelineTexture = VK_NULL_HANDLE; }
    if (m_pipelineLayout) { m_devDispatch.DestroyPipelineLayout(m_device, m_pipelineLayout, nullptr); m_pipelineLayout = VK_NULL_HANDLE; }

    if (m_descriptorPool) { m_devDispatch.DestroyDescriptorPool(m_device, m_descriptorPool, nullptr); m_descriptorPool = VK_NULL_HANDLE; }
    m_descriptorSetTransform = VK_NULL_HANDLE;
    if (m_descriptorSetLayoutTransform) { m_devDispatch.DestroyDescriptorSetLayout(m_device, m_descriptorSetLayoutTransform, nullptr); m_descriptorSetLayoutTransform = VK_NULL_HANDLE; }
    if (m_descriptorSetLayoutTexture) { m_devDispatch.DestroyDescriptorSetLayout(m_device, m_descriptorSetLayoutTexture, nullptr); m_descriptorSetLayoutTexture = VK_NULL_HANDLE; }

    if (m_vertShader) { m_devDispatch.DestroyShaderModule(m_device, m_vertShader, nullptr); m_vertShader = VK_NULL_HANDLE; }
    if (m_fragShaderColor) { m_devDispatch.DestroyShaderModule(m_device, m_fragShaderColor, nullptr); m_fragShaderColor = VK_NULL_HANDLE; }
    if (m_fragShaderTexture) { m_devDispatch.DestroyShaderModule(m_device, m_fragShaderTexture, nullptr); m_fragShaderTexture = VK_NULL_HANDLE; }

    if (m_renderPass) { m_devDispatch.DestroyRenderPass(m_device, m_renderPass, nullptr); m_renderPass = VK_NULL_HANDLE; }

    RetireStagingUploads(true);
    if (m_uploadCommandPool != VK_NULL_HANDLE && m_devDispatch.DestroyCommandPool) {
        m_devDispatch.DestroyCommandPool(m_device, m_uploadCommandPool, nullptr);
        m_uploadCommandPool = VK_NULL_HANDLE;
    }
    m_suballocator.Shutdown();

    m_device = VK_NULL_HANDLE;
    m_physicalDevice = VK_NULL_HANDLE;
    m_queue = VK_NULL_HANDLE;
    m_isInitialized = false;
}
