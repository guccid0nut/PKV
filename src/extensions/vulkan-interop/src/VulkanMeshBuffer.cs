// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Silk.NET.Vulkan;
using Buffer = System.Buffer;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace ReplayVulkanRenderer;

public unsafe class VulkanMeshBuffer(Vk vk, Device device, PhysicalDevice physicalDevice) : IDisposable
{
    private readonly Device _device = device;
    private readonly PhysicalDevice _physicalDevice = physicalDevice;
    private readonly Vk _vk = vk;

    public VkBuffer VertexBuffer { get; private set; }
    public DeviceMemory VertexBufferMemory { get; private set; }
    public VkBuffer IndexBuffer { get; private set; }
    public DeviceMemory IndexBufferMemory { get; private set; }
    public uint IndexCount { get; private set; }
    public GlbMeshData? MeshData { get; private set; }

    public void Dispose()
    {
        if (VertexBuffer.Handle != 0)
        {
            _vk.DestroyBuffer(_device, VertexBuffer, null);
            _vk.FreeMemory(_device, VertexBufferMemory, null);
            VertexBuffer = default;
            VertexBufferMemory = default;
        }

        if (IndexBuffer.Handle != 0)
        {
            _vk.DestroyBuffer(_device, IndexBuffer, null);
            _vk.FreeMemory(_device, IndexBufferMemory, null);
            IndexBuffer = default;
            IndexBufferMemory = default;
        }

        GC.SuppressFinalize(this);
    }

    public bool UploadMesh(GlbMeshData meshData)
    {
        MeshData = meshData;
        IndexCount = (uint)meshData.Indices.Length;
        Console.WriteLine(
            $"[VulkanMeshBuffer] Uploading mesh '{meshData.Name}' to GPU ({meshData.Vertices.Length} vertices, {IndexCount} indices)...");

        // 1. Create & Alloc Vertex Buffer
        ulong vertexBufferSize = (ulong)(sizeof(Vertex3D) * meshData.Vertices.Length);
        if (!CreateBuffer(vertexBufferSize, BufferUsageFlags.VertexBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, out VkBuffer vBuf,
                out DeviceMemory vMem))
        {
            Console.WriteLine("[VulkanMeshBuffer] ERROR creating GPU vertex buffer.");
            return false;
        }

        VertexBuffer = vBuf;
        VertexBufferMemory = vMem;

        void* vData;
        _vk.MapMemory(_device, VertexBufferMemory, 0, vertexBufferSize, 0, &vData);
        fixed (Vertex3D* ptr = meshData.Vertices)
        {
            Buffer.MemoryCopy(ptr, vData, vertexBufferSize, vertexBufferSize);
        }

        _vk.UnmapMemory(_device, VertexBufferMemory);

        // 2. Create & Alloc Index Buffer
        ulong indexBufferSize = (ulong)(sizeof(uint) * meshData.Indices.Length);
        if (!CreateBuffer(indexBufferSize, BufferUsageFlags.IndexBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, out VkBuffer iBuf,
                out DeviceMemory iMem))
        {
            Console.WriteLine("[VulkanMeshBuffer] ERROR creating GPU index buffer.");
            return false;
        }

        IndexBuffer = iBuf;
        IndexBufferMemory = iMem;

        void* iData;
        _vk.MapMemory(_device, IndexBufferMemory, 0, indexBufferSize, 0, &iData);
        fixed (uint* ptr = meshData.Indices)
        {
            Buffer.MemoryCopy(ptr, iData, indexBufferSize, indexBufferSize);
        }

        _vk.UnmapMemory(_device, IndexBufferMemory);

        Console.WriteLine($"[VulkanMeshBuffer] Mesh '{meshData.Name}' uploaded to Vulkan GPU memory successfully.");
        return true;
    }

    private bool CreateBuffer(ulong size, BufferUsageFlags usage, MemoryPropertyFlags properties, out VkBuffer buffer,
        out DeviceMemory memory)
    {
        BufferCreateInfo bufferInfo = new()
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive
        };

        fixed (VkBuffer* pBuf = &buffer)
        {
            if (_vk.CreateBuffer(_device, &bufferInfo, null, pBuf) != Result.Success)
            {
                memory = default;
                return false;
            }
        }

        MemoryRequirements memReqs;
        _vk.GetBufferMemoryRequirements(_device, buffer, out memReqs);

        MemoryAllocateInfo allocInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReqs.Size,
            MemoryTypeIndex = FindMemoryType(memReqs.MemoryTypeBits, properties)
        };

        fixed (DeviceMemory* pMem = &memory)
        {
            if (_vk.AllocateMemory(_device, &allocInfo, null, pMem) != Result.Success) return false;
        }

        _vk.BindBufferMemory(_device, buffer, memory, 0);
        return true;
    }

    private uint FindMemoryType(uint typeFilter, MemoryPropertyFlags properties)
    {
        PhysicalDeviceMemoryProperties memProperties;
        _vk.GetPhysicalDeviceMemoryProperties(_physicalDevice, out memProperties);

        for (int i = 0; i < memProperties.MemoryTypeCount; i++)
            if ((typeFilter & (1 << i)) != 0 && (memProperties.MemoryTypes[i].PropertyFlags & properties) == properties)
                return (uint)i;

        return 0;
    }
}
