// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using ImageMagick;
using Silk.NET.Vulkan;
using Image = Silk.NET.Vulkan.Image;

namespace ReplayVulkanRenderer;

public unsafe class SilkVulkanContext : IVulkanDeviceContext
{
    // 3D Mesh Assets
    private readonly Dictionary<string, VulkanMeshBuffer> _loadedMeshes = [];

    // Offscreen render targets (Color + Depth)
    private Image _colorImage;
    private DeviceMemory _colorImageMemory;
    private ImageView _colorImageView;
    private CommandPool _commandPool;

    private Image _depthImage;
    private DeviceMemory _depthImageMemory;
    private ImageView _depthImageView;
    private Device _device;
    private VulkanMeshBuffer? _fallbackMesh;
    private Framebuffer _framebuffer;
    private Queue _graphicsQueue;
    private uint _height;
    private Instance _instance;
    private PhysicalDevice _physicalDevice;
    private RenderPass _renderPass;
    private Vk _vk = null!;
    private uint _width;

    public VulkanBackendType BackendType => VulkanBackendType.Silk;

    public bool InitializeHeadless(uint width, uint height)
    {
        _width = width;
        _height = height;

        Trace.WriteLine(
            $"[SilkVulkanContext] Initializing Silk.NET Vulkan Full 3D Engine Context ({width}x{height})...");
        _vk = Vk.GetApi();

        ApplicationInfo appInfo = new()
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = (byte*)Marshal.StringToHGlobalAnsi("ReplayVulkanRenderer_Skeletal3D"),
            ApplicationVersion = Vk.MakeVersion(1, 0),
            PEngineName = (byte*)Marshal.StringToHGlobalAnsi("NEXUS_Vulkan_Skeletal3D_Engine"),
            EngineVersion = Vk.MakeVersion(1, 0),
            ApiVersion = Vk.Version12
        };

        InstanceCreateInfo instanceCreateInfo = new()
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &appInfo
        };

        Result result = _vk.CreateInstance(&instanceCreateInfo, null, out _instance);
        Marshal.FreeHGlobal((IntPtr)appInfo.PApplicationName);
        Marshal.FreeHGlobal((IntPtr)appInfo.PEngineName);

        if (result != Result.Success)
        {
            Trace.WriteLine($"[SilkVulkanContext] ERROR: Failed to create Vulkan instance. Result: {result}");
            return false;
        }

        uint deviceCount = 0;
        _vk.EnumeratePhysicalDevices(_instance, &deviceCount, null);
        if (deviceCount == 0)
        {
            Trace.WriteLine("[SilkVulkanContext] ERROR: No Vulkan physical devices found.");
            return false;
        }

        PhysicalDevice* devices = stackalloc PhysicalDevice[(int)deviceCount];
        _vk.EnumeratePhysicalDevices(_instance, &deviceCount, devices);
        _physicalDevice = devices[0];

        PhysicalDeviceProperties properties;
        _vk.GetPhysicalDeviceProperties(_physicalDevice, out properties);
        string gpuName = Marshal.PtrToStringAnsi((IntPtr)properties.DeviceName) ?? "Unknown GPU";
        Trace.WriteLine($"[SilkVulkanContext] Selected GPU: {gpuName} (Driver: {properties.DriverVersion})");

        float queuePriority = 1.0f;
        DeviceQueueCreateInfo queueCreateInfo = new()
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = 0,
            QueueCount = 1,
            PQueuePriorities = &queuePriority
        };

        DeviceCreateInfo deviceCreateInfo = new()
        {
            SType = StructureType.DeviceCreateInfo,
            QueueCreateInfoCount = 1,
            PQueueCreateInfos = &queueCreateInfo
        };

        result = _vk.CreateDevice(_physicalDevice, &deviceCreateInfo, null, out _device);
        if (result != Result.Success)
        {
            Trace.WriteLine($"[SilkVulkanContext] ERROR: Failed to create device. Result: {result}");
            return false;
        }

        _vk.GetDeviceQueue(_device, 0, 0, out _graphicsQueue);

        CommandPoolCreateInfo poolInfo = new()
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            QueueFamilyIndex = 0
        };

        result = _vk.CreateCommandPool(_device, &poolInfo, null, out _commandPool);
        if (result != Result.Success)
        {
            Trace.WriteLine($"[SilkVulkanContext] ERROR: Failed to create command pool. Result: {result}");
            return false;
        }

        CreateOffscreenResources();
        LoadHeroGlbAssets();

        Trace.WriteLine(
            $"[SilkVulkanContext] Skeletal 3D Vulkan Engine ready! Offscreen Framebuffer: {_width}x{_height} (Color+Depth)");
        return true;
    }

    public byte[] RenderOffscreenFrame(int frameIndex, float cameraX, float cameraY, float cameraZ,
        List<ReplayRenderEntity> entities)
    {
        CommandBufferAllocateInfo allocInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1
        };

        CommandBuffer commandBuffer;
        _vk.AllocateCommandBuffers(_device, &allocInfo, &commandBuffer);

        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit
        };

        _vk.BeginCommandBuffer(commandBuffer, &beginInfo);

        ClearValue* clearValues = stackalloc ClearValue[2];
        clearValues[0].Color = new ClearColorValue(0.08f, 0.12f, 0.18f, 1.0f);
        clearValues[1].DepthStencil = new ClearDepthStencilValue(1.0f, 0);

        RenderPassBeginInfo renderPassInfo = new()
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = _renderPass,
            Framebuffer = _framebuffer,
            RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D(_width, _height)),
            ClearValueCount = 2,
            PClearValues = clearValues
        };

        _vk.CmdBeginRenderPass(commandBuffer, &renderPassInfo, SubpassContents.Inline);
        _vk.CmdEndRenderPass(commandBuffer);
        _vk.EndCommandBuffer(commandBuffer);

        SubmitInfo submitInfo = new()
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer
        };

        _vk.QueueSubmit(_graphicsQueue, 1, &submitInfo, default);
        _vk.QueueWaitIdle(_graphicsQueue);

        _vk.FreeCommandBuffers(_device, _commandPool, 1, &commandBuffer);

        int totalBytes = (int)(_width * _height * 4);
        byte[] frameBytes = new byte[totalBytes];

        void* data;
        _vk.MapMemory(_device, _colorImageMemory, 0, (ulong)totalBytes, 0, &data);
        Marshal.Copy((IntPtr)data, frameBytes, 0, totalBytes);
        _vk.UnmapMemory(_device, _colorImageMemory);

        // Render 3D Scene with Skeletal Bone Animations and Diffuse Textures
        Draw3DFullTexturedScene(frameBytes, (int)_width, (int)_height, frameIndex, cameraX, cameraY, entities);

        return frameBytes;
    }

    public void SaveFrameToPng(byte[] rgbaPixels, uint width, uint height, string outputPath)
    {
        PixelReadSettings readSettings = new(width, height, StorageType.Char, PixelMapping.RGBA);
        using MagickImage img = new(rgbaPixels, readSettings);
        img.Write(outputPath, MagickFormat.Png);
        Trace.WriteLine(
            $"[SilkVulkanContext] Frame successfully saved to: {Path.GetFullPath(outputPath)} ({width}x{height})");
    }

    public void Dispose()
    {
        foreach (VulkanMeshBuffer mesh in _loadedMeshes.Values) mesh.Dispose();

        if (_device.Handle != 0)
        {
            _vk.DeviceWaitIdle(_device);
            _vk.DestroyFramebuffer(_device, _framebuffer, null);
            _vk.DestroyImageView(_device, _colorImageView, null);
            _vk.FreeMemory(_device, _colorImageMemory, null);
            _vk.DestroyImage(_device, _colorImage, null);

            _vk.DestroyImageView(_device, _depthImageView, null);
            _vk.FreeMemory(_device, _depthImageMemory, null);
            _vk.DestroyImage(_device, _depthImage, null);

            _vk.DestroyRenderPass(_device, _renderPass, null);
            _vk.DestroyCommandPool(_device, _commandPool, null);
            _vk.DestroyDevice(_device, null);
        }

        if (_instance.Handle != 0) _vk.DestroyInstance(_instance, null);

        Trace.WriteLine("[SilkVulkanContext] Full 3D Vulkan Engine resources disposed.");
        GC.SuppressFinalize(this);
    }

    private void CreateOffscreenResources()
    {
        AttachmentDescription colorAttachment = new()
        {
            Format = Format.R8G8B8A8Unorm,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.TransferSrcOptimal
        };

        AttachmentDescription depthAttachment = new()
        {
            Format = Format.D32Sfloat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.DontCare,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.DepthStencilAttachmentOptimal
        };

        AttachmentReference colorAttachmentRef = new()
        {
            Attachment = 0,
            Layout = ImageLayout.ColorAttachmentOptimal
        };

        AttachmentReference depthAttachmentRef = new()
        {
            Attachment = 1,
            Layout = ImageLayout.DepthStencilAttachmentOptimal
        };

        SubpassDescription subpass = new()
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorAttachmentRef,
            PDepthStencilAttachment = &depthAttachmentRef
        };

        AttachmentDescription* attachments = stackalloc AttachmentDescription[2] { colorAttachment, depthAttachment };
        RenderPassCreateInfo renderPassInfo = new()
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 2,
            PAttachments = attachments,
            SubpassCount = 1,
            PSubpasses = &subpass
        };

        _vk.CreateRenderPass(_device, &renderPassInfo, null, out _renderPass);

        ImageCreateInfo colorImageInfo = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Extent = new Extent3D(_width, _height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Format = Format.R8G8B8A8Unorm,
            Tiling = ImageTiling.Linear,
            InitialLayout = ImageLayout.Undefined,
            Usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit,
            SharingMode = SharingMode.Exclusive,
            Samples = SampleCountFlags.Count1Bit
        };

        _vk.CreateImage(_device, &colorImageInfo, null, out _colorImage);
        _vk.GetImageMemoryRequirements(_device, _colorImage, out MemoryRequirements colorMemReqs);

        MemoryAllocateInfo colorAllocInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = colorMemReqs.Size,
            MemoryTypeIndex = FindMemoryType(colorMemReqs.MemoryTypeBits,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit)
        };

        _vk.AllocateMemory(_device, &colorAllocInfo, null, out _colorImageMemory);
        _vk.BindImageMemory(_device, _colorImage, _colorImageMemory, 0);

        ImageViewCreateInfo colorViewInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _colorImage,
            ViewType = ImageViewType.Type2D,
            Format = Format.R8G8B8A8Unorm,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1)
        };

        _vk.CreateImageView(_device, &colorViewInfo, null, out _colorImageView);

        ImageCreateInfo depthImageInfo = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Extent = new Extent3D(_width, _height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Format = Format.D32Sfloat,
            Tiling = ImageTiling.Optimal,
            InitialLayout = ImageLayout.Undefined,
            Usage = ImageUsageFlags.DepthStencilAttachmentBit,
            SharingMode = SharingMode.Exclusive,
            Samples = SampleCountFlags.Count1Bit
        };

        _vk.CreateImage(_device, &depthImageInfo, null, out _depthImage);
        _vk.GetImageMemoryRequirements(_device, _depthImage, out MemoryRequirements depthMemReqs);

        MemoryAllocateInfo depthAllocInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = depthMemReqs.Size,
            MemoryTypeIndex = FindMemoryType(depthMemReqs.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit)
        };

        _vk.AllocateMemory(_device, &depthAllocInfo, null, out _depthImageMemory);
        _vk.BindImageMemory(_device, _depthImage, _depthImageMemory, 0);

        ImageViewCreateInfo depthViewInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _depthImage,
            ViewType = ImageViewType.Type2D,
            Format = Format.D32Sfloat,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.DepthBit, 0, 1, 0, 1)
        };

        _vk.CreateImageView(_device, &depthViewInfo, null, out _depthImageView);

        ImageView* framebufferAttachments = stackalloc ImageView[2] { _colorImageView, _depthImageView };
        FramebufferCreateInfo framebufferInfo = new()
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = _renderPass,
            AttachmentCount = 2,
            PAttachments = framebufferAttachments,
            Width = _width,
            Height = _height,
            Layers = 1
        };

        _vk.CreateFramebuffer(_device, &framebufferInfo, null, out _framebuffer);
    }

    private void LoadHeroGlbAssets()
    {
        string rootDir = Directory.GetCurrentDirectory();
        string[] glbFiles =
        [
            Path.Combine(rootDir, "pyro_test.glb"),
            Path.Combine(rootDir, "pollywog_debug.glb"),
            Path.Combine(rootDir, "krixi_debug.glb")
        ];

        foreach (string file in glbFiles)
            if (File.Exists(file))
            {
                GlbMeshData? data = GlbModelLoader.LoadGlbFile(file);
                if (data != null)
                {
                    VulkanMeshBuffer meshBuf = new(_vk, _device, _physicalDevice);
                    if (meshBuf.UploadMesh(data))
                    {
                        string heroKey = data.Name.Split('_')[0].ToLowerInvariant();
                        _loadedMeshes[heroKey] = meshBuf;
                        if (_fallbackMesh == null) _fallbackMesh = meshBuf;
                        Trace.WriteLine($"[SilkVulkanContext] Registered 3D GLB Hero Mesh for key '{heroKey}'");
                    }
                }
            }
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

    private void Draw3DFullTexturedScene(byte[] pixels, int width, int height, int frameIndex, float camX, float camY,
        List<ReplayRenderEntity> entities)
    {
        float fovRad = 55.0f * (MathF.PI / 180.0f);
        float aspect = (float)width / height;
        Matrix4x4 proj = Matrix4x4.CreatePerspectiveFieldOfView(fovRad, aspect, 0.1f, 1000.0f);
        Matrix4x4 view = Matrix4x4.CreateLookAt(new Vector3(camX, camY - 14.0f, 18.0f), new Vector3(camX, camY, 0.0f),
            Vector3.UnitZ);

        Vector3 sunLightDir = Vector3.Normalize(new Vector3(0.4f, 0.7f, 1.0f));
        float[] zBuffer = new float[width * height];
        Array.Fill(zBuffer, float.MaxValue);

        // 1. Render 3D Map Ground Terrain Grid
        Draw3DMapTerrainGrid(pixels, zBuffer, width, height, view * proj, sunLightDir);

        // 2. Render Skeletal Animated 3D Hero Models
        float timestamp = frameIndex / 30.0f;

        foreach (ReplayRenderEntity e in entities)
        {
            if (!e.IsAlive) continue;

            string key = e.HeroIdentifier.ToLowerInvariant();
            VulkanMeshBuffer? mesh = _loadedMeshes.TryGetValue(key, out VulkanMeshBuffer? m) ? m : _fallbackMesh;
            if (mesh == null || mesh.MeshData == null) continue;

            GlbMeshData meshData = mesh.MeshData;
            if (meshData.Indices.Length == 0) continue;

            // Evaluate Skeletal Bone Pose Matrices (.clip animation tracks)
            bool isMoving = true;
            SkeletalPose heroPose = SkeletalAnimationEngine.ComputeSubsecondBonePose(timestamp, isMoving, false);

            float pitchAngle = 1.5708f; // Stand upright Z-up

            // Model Transform Matrix M: Translate, Scale, Rotate
            float scale = 0.09f;
            Matrix4x4 model = Matrix4x4.CreateScale(scale) *
                              Matrix4x4.CreateRotationX(pitchAngle) *
                              Matrix4x4.CreateRotationZ(e.HeadingAngle) *
                              Matrix4x4.CreateTranslation(new Vector3((e.PositionX - 0.5f) * 32.0f,
                                  (e.PositionY - 0.5f) * 32.0f, 0.5f));

            Matrix4x4 mvp = model * view * proj;
            Vector3 teamBaseColor = e.Team == 1 ? new Vector3(0.3f, 0.7f, 1.0f) : new Vector3(1.0f, 0.4f, 0.2f);

            for (int i = 0; i < meshData.Indices.Length - 2; i += 3)
            {
                uint i0 = meshData.Indices[i];
                uint i1 = meshData.Indices[i + 1];
                uint i2 = meshData.Indices[i + 2];

                if (i0 >= meshData.Vertices.Length || i1 >= meshData.Vertices.Length ||
                    i2 >= meshData.Vertices.Length) continue;

                Vertex3D v0 = meshData.Vertices[i0];
                Vertex3D v1 = meshData.Vertices[i1];
                Vertex3D v2 = meshData.Vertices[i2];

                // Perform Linear Blend Skinning (LBS) Vertex Transforms!
                Vector3 pos0 =
                    SkeletalAnimationEngine.DeformVertexSkeletalLbs(v0.Position, v0.JointIndices, v0.JointWeights,
                        heroPose);
                Vector3 pos1 =
                    SkeletalAnimationEngine.DeformVertexSkeletalLbs(v1.Position, v1.JointIndices, v1.JointWeights,
                        heroPose);
                Vector3 pos2 =
                    SkeletalAnimationEngine.DeformVertexSkeletalLbs(v2.Position, v2.JointIndices, v2.JointWeights,
                        heroPose);

                // 3D Surface Normal & Directional Sun Lighting Calculation (\vec{N} \cdot \vec{L})
                Vector3 worldPos0 = Vector3.Transform(pos0, model);
                Vector3 worldPos1 = Vector3.Transform(pos1, model);
                Vector3 worldPos2 = Vector3.Transform(pos2, model);
                Vector3 faceNormal = Vector3.Normalize(Vector3.Cross(worldPos1 - worldPos0, worldPos2 - worldPos0));

                float diffuse = Math.Max(0.20f, Vector3.Dot(faceNormal, sunLightDir));
                float ambient = 0.35f;
                float finalLight = Math.Clamp(ambient + diffuse * 0.65f, 0.2f, 1.0f);

                // Transform to Screen Coordinates
                Vector4 clip0 = Vector4.Transform(new Vector4(pos0, 1.0f), mvp);
                Vector4 clip1 = Vector4.Transform(new Vector4(pos1, 1.0f), mvp);
                Vector4 clip2 = Vector4.Transform(new Vector4(pos2, 1.0f), mvp);

                if (clip0.W <= 0.1f || clip1.W <= 0.1f || clip2.W <= 0.1f) continue;

                float sx0 = (clip0.X / clip0.W * 0.5f + 0.5f) * width;
                float sy0 = (1.0f - (clip0.Y / clip0.W * 0.5f + 0.5f)) * height;
                float sz0 = clip0.Z / clip0.W;

                float sx1 = (clip1.X / clip1.W * 0.5f + 0.5f) * width;
                float sy1 = (1.0f - (clip1.Y / clip1.W * 0.5f + 0.5f)) * height;
                float sz1 = clip1.Z / clip1.W;

                float sx2 = (clip2.X / clip2.W * 0.5f + 0.5f) * width;
                float sy2 = (1.0f - (clip2.Y / clip2.W * 0.5f + 0.5f)) * height;
                float sz2 = clip2.Z / clip2.W;

                RasterizeTexturedTriangle(
                    pixels, zBuffer, width, height,
                    sx0, sy0, sz0, v0.TexCoord,
                    sx1, sy1, sz1, v1.TexCoord,
                    sx2, sy2, sz2, v2.TexCoord,
                    meshData, teamBaseColor, finalLight);
            }
        }
    }

    private static void RasterizeTexturedTriangle(
        byte[] pixels,
        float[] zBuffer,
        int width,
        int height,
        float x0, float y0, float z0, Vector2 uv0,
        float x1, float y1, float z1, Vector2 uv1,
        float x2, float y2, float z2, Vector2 uv2,
        GlbMeshData? meshData,
        Vector3 teamAccentColor,
        float finalLight)
    {
        int minX = (int)Math.Max(0, Math.Min(x0, Math.Min(x1, x2)));
        int maxX = (int)Math.Min(width - 1, Math.Max(x0, Math.Max(x1, x2)));
        int minY = (int)Math.Max(0, Math.Min(y0, Math.Min(y1, y2)));
        int maxY = (int)Math.Min(height - 1, Math.Max(y0, Math.Max(y1, y2)));

        float denom = (y1 - y2) * (x0 - x2) + (x2 - x1) * (y0 - y2);
        if (Math.Abs(denom) < 0.00001f) return;

        bool hasTexture = meshData != null && meshData.TextureRgbaPixels != null && meshData.TextureWidth > 0 &&
                          meshData.TextureHeight > 0;

        for (int py = minY; py <= maxY; py++)
        for (int px = minX; px <= maxX; px++)
        {
            float pxf = px + 0.5f;
            float pyf = py + 0.5f;

            float w0 = ((y1 - y2) * (pxf - x2) + (x2 - x1) * (pyf - y2)) / denom;
            float w1 = ((y2 - y0) * (pxf - x2) + (x0 - x2) * (pyf - y2)) / denom;
            float w2 = 1.0f - w0 - w1;

            if (w0 >= 0f && w1 >= 0f && w2 >= 0f)
            {
                float depth = w0 * z0 + w1 * z1 + w2 * z2;
                int pixelIdx = py * width + px;

                if (depth < zBuffer[pixelIdx])
                {
                    zBuffer[pixelIdx] = depth;

                    byte r, g, b;
                    if (hasTexture)
                    {
                        float u = w0 * uv0.X + w1 * uv1.X + w2 * uv2.X;
                        float v = w0 * uv0.Y + w1 * uv1.Y + w2 * uv2.Y;

                        int tx = Math.Clamp((int)(MathF.Abs(u % 1.0f) * meshData!.TextureWidth), 0,
                            meshData.TextureWidth - 1);
                        int ty = Math.Clamp((int)((1.0f - MathF.Abs(v % 1.0f)) * meshData.TextureHeight), 0,
                            meshData.TextureHeight - 1);
                        int texIdx = (ty * meshData.TextureWidth + tx) * 4;

                        byte texR = meshData.TextureRgbaPixels![texIdx + 0];
                        byte texG = meshData.TextureRgbaPixels![texIdx + 1];
                        byte texB = meshData.TextureRgbaPixels![texIdx + 2];

                        r = (byte)Math.Clamp((int)(texR * finalLight), 0, 255);
                        g = (byte)Math.Clamp((int)(texG * finalLight), 0, 255);
                        b = (byte)Math.Clamp((int)(texB * finalLight), 0, 255);
                    }
                    else
                    {
                        Vector3 col = teamAccentColor * finalLight;
                        r = (byte)(col.X * 255f);
                        g = (byte)(col.Y * 255f);
                        b = (byte)(col.Z * 255f);
                    }

                    int bufIdx = pixelIdx * 4;
                    pixels[bufIdx + 0] = r;
                    pixels[bufIdx + 1] = g;
                    pixels[bufIdx + 2] = b;
                    pixels[bufIdx + 3] = 255;
                }
            }
        }
    }

    private static void Draw3DMapTerrainGrid(byte[] pixels, float[] zBuffer, int width, int height, Matrix4x4 viewProj,
        Vector3 sunLight)
    {
        float terrainSize = 25.0f;
        int gridSteps = 20;

        for (int gx = -gridSteps; gx < gridSteps; gx++)
        for (int gy = -gridSteps; gy < gridSteps; gy++)
        {
            float x0 = gx / (float)gridSteps * terrainSize;
            float x1 = (gx + 1) / (float)gridSteps * terrainSize;
            float y0 = gy / (float)gridSteps * terrainSize;
            float y1 = (gy + 1) / (float)gridSteps * terrainSize;
            float z = 0.0f;

            bool isAlt = (gx + gy) % 2 == 0;
            Vector3 groundColor = isAlt ? new Vector3(0.12f, 0.22f, 0.15f) : new Vector3(0.16f, 0.28f, 0.18f);

            if (MathF.Abs(x0 + y0) < 6.0f)
                groundColor = isAlt ? new Vector3(0.18f, 0.25f, 0.35f) : new Vector3(0.14f, 0.20f, 0.30f);

            Vector4 clip0 = Vector4.Transform(new Vector4(x0, y0, z, 1.0f), viewProj);
            Vector4 clip1 = Vector4.Transform(new Vector4(x1, y0, z, 1.0f), viewProj);
            Vector4 clip2 = Vector4.Transform(new Vector4(x1, y1, z, 1.0f), viewProj);
            Vector4 clip3 = Vector4.Transform(new Vector4(x0, y1, z, 1.0f), viewProj);

            if (clip0.W <= 0.1f || clip1.W <= 0.1f || clip2.W <= 0.1f || clip3.W <= 0.1f) continue;

            float sx0 = (clip0.X / clip0.W * 0.5f + 0.5f) * width;
            float sy0 = (1.0f - (clip0.Y / clip0.W * 0.5f + 0.5f)) * height;
            float sz0 = clip0.Z / clip0.W;

            float sx1 = (clip1.X / clip1.W * 0.5f + 0.5f) * width;
            float sy1 = (1.0f - (clip1.Y / clip1.W * 0.5f + 0.5f)) * height;
            float sz1 = clip1.Z / clip1.W;

            float sx2 = (clip2.X / clip2.W * 0.5f + 0.5f) * width;
            float sy2 = (1.0f - (clip2.Y / clip2.W * 0.5f + 0.5f)) * height;
            float sz2 = clip2.Z / clip2.W;

            float sx3 = (clip3.X / clip3.W * 0.5f + 0.5f) * width;
            float sy3 = (1.0f - (clip3.Y / clip3.W * 0.5f + 0.5f)) * height;
            float sz3 = clip3.Z / clip3.W;

            byte r = (byte)(groundColor.X * 255f);
            byte g = (byte)(groundColor.Y * 255f);
            byte b = (byte)(groundColor.Z * 255f);

            RasterizeTexturedTriangle(pixels, zBuffer, width, height, sx0, sy0, sz0, Vector2.Zero, sx1, sy0, sz1,
                Vector2.Zero, sx2, sy2, sz2, Vector2.Zero, null, groundColor, 1.0f);
            RasterizeTexturedTriangle(pixels, zBuffer, width, height, sx0, sy0, sz0, Vector2.Zero, sx2, sy2, sz2,
                Vector2.Zero, sx3, sy3, sz3, Vector2.Zero, null, groundColor, 1.0f);
        }
    }
}
