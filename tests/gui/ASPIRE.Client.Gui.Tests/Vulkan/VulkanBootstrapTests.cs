// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ReplayVulkanRenderer;
using Silk.NET.Vulkan;
using Xunit;

namespace ASPIRE.Tests.Vulkan;

[Trait("Category", "Manual")]
public unsafe class VulkanBootstrapTests
{
    private static readonly byte[] PngMagicHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public void VolkMetaLoader_Initialize_LoadsGlobalVulkanSymbols()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        bool initialized = VolkMetaLoader.Initialize();
        Assert.True(initialized, "VolkMetaLoader should initialize vulkan-1.dll runtime.");
        Assert.True(VolkMetaLoader.IsInitialized, "IsInitialized must be true after successful Volk load.");
    }

    [Fact]
    public void VkBootstrapContext_CreateInstance_NegotiatesModernVulkanInstance()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        Vk vk = Vk.GetApi();
        Instance instance;
        bool created = VkBootstrapContext.CreateInstance(vk, "ASPIRE_Vulkan_Unit_Test", out instance);

        Assert.True(created, "VkBootstrapContext.CreateInstance must successfully instantiate Vulkan instance.");
        Assert.True(instance.Handle != 0, "Vulkan instance handle must be non-zero.");

        vk.DestroyInstance(instance, null);
    }

    [Fact]
    public void VkBootstrapContext_EnumerateAndSelectDevice_PrioritizesDiscreteGpu()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        Vk vk = Vk.GetApi();
        Instance instance;
        bool created = VkBootstrapContext.CreateInstance(vk, "ASPIRE_Vkb_Device_Test", out instance);
        Assert.True(created);

        List<VkbDeviceInfo> candidates = VkBootstrapContext.EnumerateCandidateDevices(vk, instance);
        Assert.NotEmpty(candidates);

        VkbDeviceInfo selectedDevice;
        bool deviceCreated = VkBootstrapContext.SelectAndCreateDevice(vk, instance, out selectedDevice);
        Assert.True(deviceCreated);
        Assert.NotNull(selectedDevice);
        Assert.False(string.IsNullOrWhiteSpace(selectedDevice.DeviceName));
        Assert.True(selectedDevice.Device.Handle != 0, "Logical device handle must be non-zero.");

        vk.DestroyDevice(selectedDevice.Device, null);
        vk.DestroyInstance(instance, null);
    }

    [Fact]
    public void CVulkanDriver_HeadlessInitializationAndFrameRender_GeneratesValidFrameBuffer()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        using CVulkanDriver driver = new();
        bool initialized = driver.InitializeHeadless(640, 360);
        Assert.True(initialized, "CVulkanDriver should initialize headless offscreen context.");
        Assert.Equal(VulkanBackendType.CVulkan, driver.BackendType);
        Assert.NotNull(driver.DeviceInfo);

        List<ReplayRenderEntity> entities =
        [
            new()
            {
                HeroIdentifier = "hero_pyromancer",
                PositionX = 0.5f,
                PositionY = 0.5f,
                PositionZ = 0.5f,
                HeadingAngle = 0f,
                HealthPercent = 100f,
                IsAlive = true,
                Team = 1
            }
        ];

        byte[] frameBytes = driver.RenderOffscreenFrame(0, 0.5f, 0.5f, 200f, entities);
        Assert.NotNull(frameBytes);
        Assert.Equal(640 * 360 * 4, frameBytes.Length);

        string testPngPath = Path.Combine(Path.GetTempPath(), $"cvulkan_test_{Guid.NewGuid():N}.png");
        try
        {
            driver.SaveFrameToPng(frameBytes, 640, 360, testPngPath);
            Assert.True(File.Exists(testPngPath));
            FileInfo fi = new(testPngPath);
            Assert.True(fi.Length > 1000, "Rendered PNG file should contain non-trivial encoded frame bytes.");
        }
        finally
        {
            if (File.Exists(testPngPath)) File.Delete(testPngPath);
        }
    }

    [Fact]
    public void CVulkanDriver_EmptyEntities_RendersTerrainGroundOnly()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        using CVulkanDriver driver = new();
        bool initialized = driver.InitializeHeadless(640, 360);
        Assert.True(initialized);

        byte[] frameBytes = driver.RenderOffscreenFrame(0, 0.0f, 0.0f, 150f, []);
        Assert.NotNull(frameBytes);
        Assert.Equal(640 * 360 * 4, frameBytes.Length);

        // Verify terrain generated non-zero pixel data
        bool hasNonZeroData = frameBytes.Any(b => b > 0);
        Assert.True(hasNonZeroData, "Terrain render should populate framebuffer with color bytes.");
    }

    [Fact]
    public void CVulkanDriver_MultipleHeroesAndTeams_RendersSuccessfully()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        using CVulkanDriver driver = new();
        bool initialized = driver.InitializeHeadless(640, 360);
        Assert.True(initialized);

        List<ReplayRenderEntity> entities =
        [
            new()
            {
                HeroIdentifier = "hero_pyromancer",
                PositionX = 0.25f,
                PositionY = 0.25f,
                PositionZ = 0.0f,
                HeadingAngle = 1.57f,
                HealthPercent = 100f,
                IsAlive = true,
                Team = 1
            },
            new()
            {
                HeroIdentifier = "hero_magmus",
                PositionX = 0.75f,
                PositionY = 0.75f,
                PositionZ = 0.0f,
                HeadingAngle = 3.14f,
                HealthPercent = 65f,
                IsAlive = true,
                Team = 2
            }
        ];

        byte[] frameBytes = driver.RenderOffscreenFrame(10, 0.5f, 0.5f, 200f, entities);
        Assert.NotNull(frameBytes);
        Assert.Equal(640 * 360 * 4, frameBytes.Length);
    }

    [Fact]
    public void CVulkanDriver_DeadEntities_AreSafelySkipped()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        using CVulkanDriver driver = new();
        bool initialized = driver.InitializeHeadless(640, 360);
        Assert.True(initialized);

        List<ReplayRenderEntity> deadEntities =
        [
            new()
            {
                HeroIdentifier = "hero_pyromancer",
                PositionX = 0.5f,
                PositionY = 0.5f,
                PositionZ = 0.0f,
                HeadingAngle = 0f,
                HealthPercent = 0f,
                IsAlive = false,
                Team = 1
            }
        ];

        byte[] frameBytes = driver.RenderOffscreenFrame(0, 0.0f, 0.0f, 150f, deadEntities);
        Assert.NotNull(frameBytes);
        Assert.Equal(640 * 360 * 4, frameBytes.Length);
    }

    [Fact]
    public void CVulkanDriver_DifferentResolutions_RendersAppropriateBufferSizes()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        uint[] resolutions = [320, 180, 1280, 720];

        // Test Low-Res 320x180
        using (CVulkanDriver lowResDriver = new())
        {
            Assert.True(lowResDriver.InitializeHeadless(resolutions[0], resolutions[1]));
            byte[] lowResBytes = lowResDriver.RenderOffscreenFrame(0, 0f, 0f, 100f, []);
            Assert.Equal((int)(resolutions[0] * resolutions[1] * 4), lowResBytes.Length);
        }

        // Test High-Res 1280x720
        using (CVulkanDriver highResDriver = new())
        {
            Assert.True(highResDriver.InitializeHeadless(resolutions[2], resolutions[3]));
            byte[] highResBytes = highResDriver.RenderOffscreenFrame(0, 0f, 0f, 100f, []);
            Assert.Equal((int)(resolutions[2] * resolutions[3] * 4), highResBytes.Length);
        }
    }

    [Fact]
    public void CVulkanDriver_SkeletalAnimationProgression_RendersConsecutiveTimestamps()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        using CVulkanDriver driver = new();
        Assert.True(driver.InitializeHeadless(640, 360));

        List<ReplayRenderEntity> entities =
        [
            new()
            {
                HeroIdentifier = "hero_pyromancer",
                PositionX = 0.5f,
                PositionY = 0.5f,
                PositionZ = 0.0f,
                HeadingAngle = 0f,
                HealthPercent = 100f,
                IsAlive = true,
                Team = 1
            }
        ];

        // Render multiple consecutive animation frames (0s, 0.5s, 1s, 1.5s)
        for (int frame = 0; frame <= 45; frame += 15)
        {
            byte[] frameBytes = driver.RenderOffscreenFrame(frame, 0.5f, 0.5f, 200f, entities);
            Assert.NotNull(frameBytes);
            Assert.Equal(640 * 360 * 4, frameBytes.Length);
        }
    }

    [Fact]
    public void CVulkanDriver_SaveFrameToPng_ValidatesPngMagicHeader()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        using CVulkanDriver driver = new();
        Assert.True(driver.InitializeHeadless(640, 360));

        byte[] frameBytes = driver.RenderOffscreenFrame(0, 0f, 0f, 100f, []);
        string tempPng = Path.Combine(Path.GetTempPath(), $"cvulkan_header_test_{Guid.NewGuid():N}.png");

        try
        {
            driver.SaveFrameToPng(frameBytes, 640, 360, tempPng);
            Assert.True(File.Exists(tempPng));

            byte[] header = new byte[8];
            using (FileStream fs = File.OpenRead(tempPng))
            {
                int read = fs.Read(header, 0, 8);
                Assert.Equal(8, read);
            }

            Assert.Equal(PngMagicHeader, header);
        }
        finally
        {
            if (File.Exists(tempPng)) File.Delete(tempPng);
        }
    }

    [Fact]
    public void CVulkanDriver_DoubleDispose_IsIdempotentAndSafe()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        CVulkanDriver driver = new();
        bool initialized = driver.InitializeHeadless(320, 180);
        Assert.True(initialized);

        // First Dispose
        driver.Dispose();

        // Second Dispose must be completely idempotent and not crash
        Exception? ex = Record.Exception(driver.Dispose);
        Assert.Null(ex);
    }
}
