// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ReplayVulkanRenderer;

public enum VulkanBackendType
{
    Silk,
    Vortice,
    CVulkan
}

public interface IVulkanDeviceContext : IDisposable
{
    VulkanBackendType BackendType { get; }
    bool InitializeHeadless(uint width, uint height);

    byte[] RenderOffscreenFrame(int frameIndex, float cameraX, float cameraY, float cameraZ,
        List<ReplayRenderEntity> entities);

    void SaveFrameToPng(byte[] rgbaPixels, uint width, uint height, string outputPath);
}

public sealed class ReplayRenderEntity
{
    public required string HeroIdentifier { get; set; }
    public required float PositionX { get; set; }
    public required float PositionY { get; set; }
    public required float PositionZ { get; set; }
    public required float HeadingAngle { get; set; }
    public required float HealthPercent { get; set; }
    public required bool IsAlive { get; set; }
    public required int Team { get; set; }
}
