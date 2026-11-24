// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Moq;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class AssetCacheAndDiagnosticsTests
{
    [Fact]
    public void McpToolRegistry_WhenInvoked_RegistersAssetCacheAndDiagnosticsToolsAndAliases()
    {
        McpToolRegistry registry = new(registerDefaults: true);

        string[] expectedTools =
        [
            "kongor_asset_inspect",
            "kongor_asset_inspector",
            "kongor_engine_cache_purge",
            "kongor_cache_purge",
            "kongor_cluster_diagnostics",
            "kongor_diagnostics"
        ];

        foreach (string toolName in expectedTools)
        {
            registry.HasTool(toolName).Should().BeTrue($"Registry must contain tool '{toolName}'");
            ToolDefinition? def = registry.GetTool(toolName);
            def.Should().NotBeNull();
            def!.Description.Should().NotBeNullOrWhiteSpace();
            def.InputSchema.Should().NotBeNull();
            def.InputSchema.Type.Should().Be("object");
        }
    }

    [Fact]
    public void AssetInspectService_TgaInspection_ValidatesHeaderAndOrigin()
    {
        AssetInspectService service = new();
        string samplePath = File.Exists("games/client/override/ui/fe2/mainmenu/logo_fb.tga")
            ? "games/client/override/ui/fe2/mainmenu/logo_fb.tga"
            : "games/shared/00000000/ui/fe2/mainmenu/logo_fb.tga";

        if (!File.Exists(samplePath))
        {
            // Skip file path check if not running from repo root, but test stream parser directly
            byte[] header = CreateTgaBytes(512, 128, 32, descriptor: 0x08);
            using MemoryStream ms = new(header);
            AssetInspectionResult res = service.InspectTga(ms, "logo_fb.tga");
            res.Success.Should().BeTrue();
            res.Dimensions.Should().NotBeNull();
            res.Dimensions!.Width.Should().Be(512);
            res.Dimensions.Height.Should().Be(128);
            res.BitsPerPixel.Should().Be(32);
            res.AlphaDepth.Should().Be(8);
            res.Origin.Should().Be("Bottom-Left");
            res.OrientationWarning.Should().BeNull();
            return;
        }

        using FileStream fs = File.OpenRead(samplePath);
        AssetInspectionResult result = service.InspectTga(fs, samplePath);

        result.Success.Should().BeTrue();
        result.Format.Should().Be("TGA");
        result.Dimensions.Should().NotBeNull();
        result.Dimensions!.Width.Should().Be(512);
        result.Dimensions.Height.Should().Be(128);
        result.BitsPerPixel.Should().Be(32);
        result.AlphaDepth.Should().Be(8);
        result.HasAlpha.Should().BeTrue();
        result.Origin.Should().Be("Bottom-Left");
        result.OrientationWarning.Should().BeNull();
        result.DescriptorByte.Should().Be("0x08");
        result.SolidColor.IsSolid.Should().BeFalse();
    }

    [Fact]
    public void AssetInspectService_TgaOriginWarning_FlagsBit5TopLeftOrigin()
    {
        AssetInspectService service = new();

        // 0x28: bit 5 (0x20) top-left origin + bit 3 (0x08) 8 alpha bits
        byte[] tgaBytes = CreateTgaBytes(256, 256, 32, descriptor: 0x28);
        using MemoryStream ms = new(tgaBytes);

        AssetInspectionResult result = service.InspectTga(ms, "inverted_texture.tga");

        result.Success.Should().BeTrue();
        result.Origin.Should().Be("Top-Left");
        result.OrientationWarning.Should().NotBeNullOrWhiteSpace();
        result.OrientationWarning.Should().Contain("Top-Left origin");
        result.OrientationWarning.Should().Contain("inverted");
    }

    [Fact]
    public void AssetInspectService_SolidColorDetection_DetectsWhiteBlackInvis()
    {
        AssetInspectService service = new();

        // 1. Solid Black
        byte[] blackTga = CreateSolidTga(16, 16, 32, 0, 0, 0, 255);
        using (MemoryStream ms = new(blackTga))
        {
            AssetInspectionResult res = service.InspectTga(ms, "black.tga");
            res.SolidColor.IsSolid.Should().BeTrue();
            res.SolidColor.Type.Should().Be("$black");
        }

        // 2. Solid White
        byte[] whiteTga = CreateSolidTga(16, 16, 32, 255, 255, 255, 255);
        using (MemoryStream ms = new(whiteTga))
        {
            AssetInspectionResult res = service.InspectTga(ms, "white.tga");
            res.SolidColor.IsSolid.Should().BeTrue();
            res.SolidColor.Type.Should().Be("$white");
        }

        // 3. Solid Transparent ($invis)
        byte[] invisTga = CreateSolidTga(16, 16, 32, 0, 0, 0, 0);
        using (MemoryStream ms = new(invisTga))
        {
            AssetInspectionResult res = service.InspectTga(ms, "invis.tga");
            res.SolidColor.IsSolid.Should().BeTrue();
            res.SolidColor.Type.Should().Be("$invis");
        }
    }

    [Fact]
    public void AssetInspectService_DdsInspection_ValidatesHeaderDimensionsAndFourCC()
    {
        AssetInspectService service = new();
        string samplePath = "games/client/editor/00000000/core/brushes/standard/brush1.dds";

        if (!File.Exists(samplePath))
        {
            byte[] ddsBytes = CreateDdsBytes(32, 32, "DXT1", 6);
            using MemoryStream ms = new(ddsBytes);
            AssetInspectionResult res = service.InspectDds(ms, "brush1.dds");
            res.Success.Should().BeTrue();
            res.Dimensions!.Width.Should().Be(32);
            res.Dimensions.Height.Should().Be(32);
            res.FourCC.Should().Be("DXT1");
            res.MipMapCount.Should().Be(6);
            return;
        }

        using FileStream fs = File.OpenRead(samplePath);
        AssetInspectionResult result = service.InspectDds(fs, samplePath);

        result.Success.Should().BeTrue();
        result.Format.Should().Be("DDS");
        result.Dimensions.Should().NotBeNull();
        result.Dimensions!.Width.Should().Be(32);
        result.Dimensions.Height.Should().Be(32);
        result.FourCC.Should().Be("DXT1");
        result.MipMapCount.Should().Be(6);
        result.HasAlpha.Should().BeTrue();
    }

    [Fact]
    public async Task AssetInspectHandler_WhenInvoked_DispatchesViaRegistry()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;

        McpToolRegistry registry = new(registerDefaults: true);

        string inspectPath = File.Exists("games/client/override/ui/fe2/mainmenu/logo_fb.tga")
            ? "games/client/override/ui/fe2/mainmenu/logo_fb.tga"
            : "games/shared/00000000/ui/fe2/mainmenu/logo_fb.tga";
        using JsonDocument argsDoc = JsonDocument.Parse($@"{{ ""path"": ""{inspectPath}"" }}");
        ToolCallResult result = await registry.ExecuteToolAsync("kongor_asset_inspect", argsDoc.RootElement);

        result.IsError.Should().BeFalse();
        result.Content.Should().NotBeEmpty();

        using JsonDocument resDoc = JsonDocument.Parse(((TextContentItem)result.Content[0]).Text);
        string jsonOutput = resDoc.RootElement.ToString();
        resDoc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue(jsonOutput);
        resDoc.RootElement.GetProperty("format").GetString().Should().Be("TGA");
        resDoc.RootElement.GetProperty("dimensions").GetProperty("width").GetInt32().Should().Be(512);
        resDoc.RootElement.GetProperty("dimensions").GetProperty("height").GetInt32().Should().Be(128);
    }

    [Fact]
    public async Task EngineCacheService_Purge_DeletesTargetCachesAndCleansBoms()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "Kongor_CacheTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            // Setup mock cache directories
            string shaderDir = Path.Combine(tempRoot, "games", "client", "game", "shadercache");
            string textureDir = Path.Combine(tempRoot, "games", "client", "userdataKONGOR", "extensions", "texturecache");
            string clientDir = Path.Combine(tempRoot, "games", "client");

            Directory.CreateDirectory(shaderDir);
            Directory.CreateDirectory(textureDir);

            // Write dummy cache files
            File.WriteAllBytes(Path.Combine(shaderDir, "test.hlsl.bin"), [0x01, 0x02, 0x03, 0x04]);
            File.WriteAllBytes(Path.Combine(textureDir, "test.tex.bin"), [0x05, 0x06, 0x07, 0x08]);
            File.WriteAllBytes(Path.Combine(clientDir, "hon_x64.dxvk-cache"), [0x09, 0x0A, 0x0B, 0x0C]);

            // Write an XML file with UTF-8 BOM
            string userdataDir = Path.Combine(tempRoot, "games", "client", "userdataKONGOR");
            Directory.CreateDirectory(userdataDir);
            string bomXmlPath = Path.Combine(userdataDir, "cache_index.xml");
            byte[] bomBytes = [0xEF, 0xBB, 0xBF, (byte)'<', (byte)'r', (byte)'o', (byte)'o', (byte)'t', (byte)'/', (byte)'>'];
            File.WriteAllBytes(bomXmlPath, bomBytes);

            EngineCacheService service = new();
            CachePurgeResult result = await service.PurgeCacheAsync(
                ["all"],
                hotReload: false,
                cleanBoms: true,
                repoRoot: tempRoot);

            result.Success.Should().BeTrue();
            result.FilesDeleted.Should().BeGreaterThanOrEqualTo(3);
            result.BytesReclaimed.Should().BeGreaterThanOrEqualTo(12);
            result.BomsCleaned.Should().Be(1);

            // Verify BOM was stripped
            byte[] cleanedXml = File.ReadAllBytes(bomXmlPath);
            cleanedXml[0].Should().Be((byte)'<');
            cleanedXml.Length.Should().Be(bomBytes.Length - 3);

            // Verify cache files are deleted
            File.Exists(Path.Combine(shaderDir, "test.hlsl.bin")).Should().BeFalse();
            File.Exists(Path.Combine(textureDir, "test.tex.bin")).Should().BeFalse();
            File.Exists(Path.Combine(clientDir, "hon_x64.dxvk-cache")).Should().BeFalse();
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task EngineCachePurgeHandler_WhenInvoked_DispatchesViaRegistry()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        McpToolRegistry registry = new(registerDefaults: true);

        using JsonDocument argsDoc = JsonDocument.Parse(@"{ ""targets"": [""shaders""], ""hotReload"": false }");
        ToolCallResult result = await registry.ExecuteToolAsync("kongor_engine_cache_purge", argsDoc.RootElement);

        result.IsError.Should().BeFalse();
        using JsonDocument resDoc = JsonDocument.Parse(((TextContentItem)result.Content[0]).Text);
        resDoc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        resDoc.RootElement.GetProperty("purgedTargets").EnumerateArray().Any(t => t.GetString() == "shadercache").Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Manual")]
    public async Task ClusterDiagnosticsService_RedisProbe_ChecksConnectionAndPing()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        ClusterDiagnosticsService service = new();

        // Probe local Redis on 28298
        RedisProbeResult result = await service.ProbeRedisAsync("127.0.0.1", 28298);

        result.Host.Should().Be("127.0.0.1");
        result.Port.Should().Be(28298);

        if (result.Connected)
        {
            result.LatencyMs.Should().BeGreaterThan(0);
            result.AuthState.Should().BeOneOf("Authenticated", "NoAuthRequired", "PasswordRequired");
        }
        else
        {
            result.Error.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    [Trait("Category", "Manual")]
    public async Task ClusterDiagnosticsService_DatabaseProbe_ParsesSupabaseConnectionString()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        string cs = "postgresql://postgres.xyzcompany:password123@aws-0-us-east-2.pooler.supabase.com:6543/postgres";
        ClusterDiagnosticsService service = new();

        // Testing socket connectivity to Supabase pooler
        DatabaseProbeResult res = await service.ProbeDatabaseAsync(cs);
        res.Host.Should().Be("aws-0-us-east-2.pooler.supabase.com");
        res.Port.Should().Be(6543);
        res.Database.Should().Be("postgres");
        res.IsSupabase.Should().BeTrue();
        if (res.Connected)
        {
            res.LatencyMs.Should().BeGreaterThan(0);
        }
        else
        {
            res.Error.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void ClusterDiagnosticsService_AggregateRecentErrors_FindsErrorsWithoutHanging()
    {
        ClusterDiagnosticsService service = new();
        var errors = service.AggregateRecentErrors(3);
        errors.Should().NotBeNull();
    }

    [Fact]
    [Trait("Category", "Manual")]
    public async Task ClusterDiagnosticsService_RunDiagnostics_ExecutesCleanly()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        ClusterDiagnosticsService service = new();
        var report = await service.RunDiagnosticsAsync();
        report.Should().NotBeNull();
    }

    [Fact]
    [Trait("Category", "Manual")]
    public async Task ClusterDiagnosticsService_EndpointsProbe_ChecksParallelMicroservices()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        ClusterDiagnosticsService service = new();
        IReadOnlyList<EndpointProbeResult> results = await service.ProbeEndpointsAsync();

        results.Should().HaveCount(5);
        results.Select(e => e.Name).Should().Contain(["MasterServer", "ChatServer", "Web API", "Web UI", "COMPEL"]);

        foreach (EndpointProbeResult ep in results)
        {
            ep.Port.Should().BeGreaterThan(0);
            ep.Url.Should().StartWith("http");
        }
    }

    [Fact]
    [Trait("Category", "Manual")]
    public async Task ClusterDiagnosticsHandler_DirectCall_ExecutesCleanly()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        ClusterDiagnosticsHandler handler = new();
        using JsonDocument argsDoc = JsonDocument.Parse(@"{ ""maxLogErrors"": 3 }");
        ToolCallResult result = await handler.HandleAsync(argsDoc.RootElement);
        result.IsError.Should().BeFalse();

        using JsonDocument resDoc = JsonDocument.Parse(((TextContentItem)result.Content[0]).Text);
        string status = resDoc.RootElement.GetProperty("overallStatus").GetString()!;
        status.Should().BeOneOf("healthy", "degraded", "unhealthy");
        resDoc.RootElement.GetProperty("redis").Should().NotBeNull();
        resDoc.RootElement.GetProperty("database").Should().NotBeNull();
        resDoc.RootElement.GetProperty("endpoints").GetArrayLength().Should().Be(5);
    }

    [Fact]
    public async Task ClusterDiagnosticsHandler_WhenInvoked_DispatchesViaRegistry()
    {
        var mockService = new Mock<IClusterDiagnosticsService>();
        mockService.Setup(s => s.RunDiagnosticsAsync(
                It.IsAny<string?>(),
                It.IsAny<int?>(),
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClusterDiagnosticsReport
            {
                OverallStatus = "healthy",
                Timestamp = TimeProvider.System.GetUtcNow().ToString("o"),
                TotalProbeDurationMs = 12.5,
                Redis = new RedisProbeResult { Connected = true, Host = "127.0.0.1", Port = 28298, LatencyMs = 1.2 },
                Database = new DatabaseProbeResult { Connected = true, Host = "localhost", Port = 5432, Database = "postgres", LatencyMs = 2.4 },
                Endpoints = [new EndpointProbeResult { Name = "MasterServer", Url = "http://localhost:55555", Port = 55555, Connected = true }],
                RecentErrors = []
            });

        ClusterDiagnosticsHandler handler = new(mockService.Object);
        McpToolRegistry registry = new(clusterDiagnosticsHandler: handler, registerDefaults: true);

        using JsonDocument argsDoc = JsonDocument.Parse(@"{ ""maxLogErrors"": 3 }");
        ToolCallResult result = await registry.ExecuteToolAsync("kongor_cluster_diagnostics", argsDoc.RootElement);

        result.IsError.Should().BeFalse();
        using JsonDocument resDoc = JsonDocument.Parse(((TextContentItem)result.Content[0]).Text);
        string status = resDoc.RootElement.GetProperty("overallStatus").GetString()!;
        status.Should().Be("healthy");
        resDoc.RootElement.GetProperty("redis").GetProperty("connected").GetBoolean().Should().BeTrue();
        resDoc.RootElement.GetProperty("database").GetProperty("connected").GetBoolean().Should().BeTrue();
    }

    // --- Helper generators for synthetic textures ---

    private static byte[] CreateTgaBytes(ushort width, ushort height, byte bpp, byte descriptor)
    {
        byte[] buffer = new byte[18];
        buffer[2] = 2; // Uncompressed True-Color
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12, 2), width);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14, 2), height);
        buffer[16] = bpp;
        buffer[17] = descriptor;
        return buffer;
    }

    private static byte[] CreateSolidTga(ushort width, ushort height, byte bpp, byte r, byte g, byte b, byte a)
    {
        byte[] header = CreateTgaBytes(width, height, bpp, descriptor: 0x08);
        int bytesPerPixel = bpp / 8;
        byte[] pixels = new byte[width * height * bytesPerPixel];

        for (int i = 0; i < width * height; i++)
        {
            int offset = i * bytesPerPixel;
            pixels[offset] = b;
            pixels[offset + 1] = g;
            pixels[offset + 2] = r;
            if (bytesPerPixel >= 4) pixels[offset + 3] = a;
        }

        byte[] combined = new byte[18 + pixels.Length];
        header.CopyTo(combined, 0);
        pixels.CopyTo(combined, 18);
        return combined;
    }

    private static byte[] CreateDdsBytes(uint width, uint height, string fourCC, uint mips)
    {
        byte[] buffer = new byte[128];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), 0x20534444); // 'DDS '
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4, 4), 124);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12, 4), height);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(16, 4), width);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(28, 4), mips);

        // Pixel format
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(76, 4), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(80, 4), 0x4); // DDPF_FOURCC
        Encoding.ASCII.GetBytes(fourCC).CopyTo(buffer.AsSpan(84, 4));

        return buffer;
    }
}
