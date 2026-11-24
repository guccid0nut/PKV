// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ============================================================================
// File: rmlui_vk_suballocator.h
// System: ProjectKongor - Native Vulkan Layer (VkLayer_hon_replay_capture)
// Subsystem: Vulkan 1.3 16 MiB Slab Suballocator with Persistent Mapping
// ============================================================================

#pragma once

#include <vulkan/vulkan.h>
#include <cstdint>
#include <vector>
#include <unordered_map>
#include <mutex>
#include <algorithm>
#include <cstring>
#include "../layer_manifest.h"

struct VkSuballocation {
    VkDeviceMemory memory = VK_NULL_HANDLE;
    VkDeviceSize offset = 0;
    VkDeviceSize size = 0;
    void* mappedData = nullptr; // Non-null if host-visible
    uint32_t memoryTypeIndex = 0;
    uint32_t slabId = 0;
    uint32_t bucketKey = 0;

    bool isValid() const { return memory != VK_NULL_HANDLE; }
};

class RmlUi_VkSlabSuballocator {
public:
    static constexpr VkDeviceSize kDefaultSlabSize = 16ULL * 1024ULL * 1024ULL; // 16 MiB

    struct SlabBlock {
        VkDeviceSize offset = 0;
        VkDeviceSize size = 0;
        bool isFree = true;
    };

    struct Slab {
        uint32_t slabId = 0;
        uint32_t memoryTypeIndex = 0;
        bool isHostVisible = false;
        bool isImage = false;
        VkDeviceSize totalSize = 0;
        VkDeviceMemory memory = VK_NULL_HANDLE;
        void* mappedBase = nullptr;
        std::vector<SlabBlock> blocks;
    };

    RmlUi_VkSlabSuballocator()
        : m_device(VK_NULL_HANDLE)
        , m_devDispatch(nullptr)
        , m_nextSlabId(1)
    {
    }

    ~RmlUi_VkSlabSuballocator() {
        Shutdown();
    }

    void Initialize(VkDevice device, const LayerDeviceDispatchTable* devDispatch) {
        std::lock_guard<std::mutex> lock(m_mutex);
        ShutdownInternal();
        m_device = device;
        m_devDispatch = devDispatch;
        HON_LOG_INFO("[VkSlabSuballocator] Initialized slab suballocator for device 0x%p (16 MiB base slabs).",
                     reinterpret_cast<void*>(device));
    }

    static uint32_t MakeBucketKey(uint32_t memoryTypeIndex, bool isHostVisible, bool isImage) {
        return (memoryTypeIndex & 0xFFFFu) | ((isHostVisible ? 1u : 0u) << 16) | ((isImage ? 1u : 0u) << 17);
    }

    bool Allocate(
        uint32_t memoryTypeIndex,
        bool isHostVisible,
        VkDeviceSize size,
        VkDeviceSize alignment,
        VkSuballocation& outSuballoc,
        bool isImage = false)
    {
        outSuballoc = {};
        if (size == 0) {
            return false;
        }

        std::lock_guard<std::mutex> lock(m_mutex);
        if (m_device == VK_NULL_HANDLE || !m_devDispatch || !m_devDispatch->AllocateMemory) {
            HON_LOG_ERROR("[VkSlabSuballocator] Allocate called with uninitialized device or null dispatch table.");
            return false;
        }

        if (alignment == 0) {
            alignment = 1;
        }

        const uint32_t bucketKey = MakeBucketKey(memoryTypeIndex, isHostVisible, isImage);
        auto& slabList = m_buckets[bucketKey];

        // 1. First-fit search across existing slabs in this bucket
        for (auto& slab : slabList) {
            if (TrySuballocateFromSlab(slab, size, alignment, bucketKey, outSuballoc)) {
                return true;
            }
        }

        // 2. No existing slab has space; create a new slab (at least 16 MiB, expanding smoothly if size > 16 MiB)
        const VkDeviceSize requiredWithAlign = size + alignment;
        const VkDeviceSize newSlabSize = (std::max)(kDefaultSlabSize, requiredWithAlign);

        Slab newSlab;
        newSlab.slabId = m_nextSlabId++;
        newSlab.memoryTypeIndex = memoryTypeIndex;
        newSlab.isHostVisible = isHostVisible;
        newSlab.isImage = isImage;
        newSlab.totalSize = newSlabSize;

        VkMemoryAllocateInfo allocInfo = { VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO };
        allocInfo.allocationSize = newSlabSize;
        allocInfo.memoryTypeIndex = memoryTypeIndex;

        VkResult res = m_devDispatch->AllocateMemory(m_device, &allocInfo, nullptr, &newSlab.memory);
        if (res != VK_SUCCESS || newSlab.memory == VK_NULL_HANDLE) {
            HON_LOG_ERROR("[VkSlabSuballocator] vkAllocateMemory failed (code=%d, size=%llu, memType=%u).",
                          static_cast<int>(res), static_cast<unsigned long long>(newSlabSize), memoryTypeIndex);
            return false;
        }

        if (isHostVisible && m_devDispatch->MapMemory) {
            res = m_devDispatch->MapMemory(m_device, newSlab.memory, 0, newSlabSize, 0, &newSlab.mappedBase);
            if (res != VK_SUCCESS || !newSlab.mappedBase) {
                HON_LOG_ERROR("[VkSlabSuballocator] vkMapMemory failed for persistent host-visible slab (code=%d).",
                              static_cast<int>(res));
                m_devDispatch->FreeMemory(m_device, newSlab.memory, nullptr);
                return false;
            }
        }

        newSlab.blocks.push_back({ 0, newSlabSize, true });
        HON_LOG_INFO("[VkSlabSuballocator] Created new slab #%u (bytes=%llu, memType=%u, hostVisible=%d, isImage=%d).",
                     newSlab.slabId, static_cast<unsigned long long>(newSlabSize), memoryTypeIndex,
                     isHostVisible ? 1 : 0, isImage ? 1 : 0);

        slabList.push_back(std::move(newSlab));
        return TrySuballocateFromSlab(slabList.back(), size, alignment, bucketKey, outSuballoc);
    }

    void Free(const VkSuballocation& suballoc) {
        if (!suballoc.isValid()) {
            return;
        }

        std::lock_guard<std::mutex> lock(m_mutex);
        auto itBucket = m_buckets.find(suballoc.bucketKey);
        if (itBucket == m_buckets.end()) {
            HON_LOG_WARN("[VkSlabSuballocator] Free failed: bucketKey 0x%x not found.", suballoc.bucketKey);
            return;
        }

        for (auto& slab : itBucket->second) {
            if (slab.slabId == suballoc.slabId) {
                for (size_t i = 0; i < slab.blocks.size(); ++i) {
                    if (slab.blocks[i].offset == suballoc.offset) {
                        slab.blocks[i].isFree = true;

                        // Coalesce forward with next block if free
                        if (i + 1 < slab.blocks.size() && slab.blocks[i + 1].isFree) {
                            slab.blocks[i].size += slab.blocks[i + 1].size;
                            slab.blocks.erase(slab.blocks.begin() + static_cast<std::ptrdiff_t>(i + 1));
                        }

                        // Coalesce backward with previous block if free
                        if (i > 0 && slab.blocks[i - 1].isFree) {
                            slab.blocks[i - 1].size += slab.blocks[i].size;
                            slab.blocks.erase(slab.blocks.begin() + static_cast<std::ptrdiff_t>(i));
                        }
                        return;
                    }
                }
                HON_LOG_WARN("[VkSlabSuballocator] Free: block offset %llu not found in slab #%u.",
                             static_cast<unsigned long long>(suballoc.offset), slab.slabId);
                return;
            }
        }
        HON_LOG_WARN("[VkSlabSuballocator] Free: slab #%u not found in bucket 0x%x.",
                     suballoc.slabId, suballoc.bucketKey);
    }

    void Shutdown() {
        std::lock_guard<std::mutex> lock(m_mutex);
        ShutdownInternal();
    }

private:
    bool TrySuballocateFromSlab(
        Slab& slab,
        VkDeviceSize size,
        VkDeviceSize alignment,
        uint32_t bucketKey,
        VkSuballocation& outSuballoc)
    {
        for (size_t i = 0; i < slab.blocks.size(); ++i) {
            auto& block = slab.blocks[i];
            if (!block.isFree) {
                continue;
            }

            const VkDeviceSize alignedOffset = (block.offset + alignment - 1) & ~(alignment - 1);
            if (alignedOffset + size <= block.offset + block.size) {
                const VkDeviceSize prePadding = alignedOffset - block.offset;
                const VkDeviceSize postRemainder = (block.offset + block.size) - (alignedOffset + size);

                if (prePadding > 0) {
                    SlabBlock padBlock;
                    padBlock.offset = block.offset;
                    padBlock.size = prePadding;
                    padBlock.isFree = true;

                    block.offset = alignedOffset;
                    block.size -= prePadding;

                    slab.blocks.insert(slab.blocks.begin() + static_cast<std::ptrdiff_t>(i), padBlock);
                    ++i; // Point back to the newly shifted target block
                }

                auto& targetBlock = slab.blocks[i];
                if (postRemainder > 0) {
                    SlabBlock remBlock;
                    remBlock.offset = alignedOffset + size;
                    remBlock.size = postRemainder;
                    remBlock.isFree = true;

                    targetBlock.size = size;
                    targetBlock.isFree = false;

                    slab.blocks.insert(slab.blocks.begin() + static_cast<std::ptrdiff_t>(i + 1), remBlock);
                } else {
                    targetBlock.isFree = false;
                }

                outSuballoc.memory = slab.memory;
                outSuballoc.offset = alignedOffset;
                outSuballoc.size = size;
                outSuballoc.memoryTypeIndex = slab.memoryTypeIndex;
                outSuballoc.slabId = slab.slabId;
                outSuballoc.bucketKey = bucketKey;
                if (slab.mappedBase) {
                    outSuballoc.mappedData = static_cast<uint8_t*>(slab.mappedBase) + alignedOffset;
                } else {
                    outSuballoc.mappedData = nullptr;
                }
                return true;
            }
        }
        return false;
    }

    void ShutdownInternal() {
        if (m_device != VK_NULL_HANDLE && m_devDispatch) {
            for (auto& [bucketKey, slabList] : m_buckets) {
                for (auto& slab : slabList) {
                    if (slab.memory != VK_NULL_HANDLE) {
                        if (slab.mappedBase && m_devDispatch->UnmapMemory) {
                            m_devDispatch->UnmapMemory(m_device, slab.memory);
                            slab.mappedBase = nullptr;
                        }
                        if (m_devDispatch->FreeMemory) {
                            m_devDispatch->FreeMemory(m_device, slab.memory, nullptr);
                        }
                        slab.memory = VK_NULL_HANDLE;
                    }
                }
            }
        }
        m_buckets.clear();
        m_device = VK_NULL_HANDLE;
        m_devDispatch = nullptr;
    }

private:
    VkDevice m_device;
    const LayerDeviceDispatchTable* m_devDispatch;
    uint32_t m_nextSlabId;
    std::mutex m_mutex;
    std::unordered_map<uint32_t, std::vector<Slab>> m_buckets;
};
