// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DAWNBRINGER.WebPortal.UI.Hubs;
using DAWNBRINGER.WebPortal.UI.Models.DeveloperIssues;
using DAWNBRINGER.WebPortal.UI.Services.DeveloperIssues;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class TaoPhase8bAdminDashboardTests : IDisposable
{
    private readonly string _testTempDir;
    private readonly Mock<IHostEnvironment> _mockEnv;
    private readonly Mock<ILogger<DeveloperIssueAdminService>> _mockLogger;
    private readonly Mock<IHubContext<DeveloperIssuesHub, IDeveloperIssuesClient>> _mockHubContext;
    private readonly Mock<IHubClients<IDeveloperIssuesClient>> _mockHubClients;
    private readonly Mock<IDeveloperIssuesClient> _mockClientProxy;

    public TaoPhase8bAdminDashboardTests()
    {
        _testTempDir = Path.Combine(Path.GetTempPath(), $"kongor_phase8b_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testTempDir);

        _mockEnv = new Mock<IHostEnvironment>();
        _mockEnv.Setup(e => e.ContentRootPath).Returns(_testTempDir);

        _mockLogger = new Mock<ILogger<DeveloperIssueAdminService>>();

        _mockHubContext = new Mock<IHubContext<DeveloperIssuesHub, IDeveloperIssuesClient>>();
        _mockHubClients = new Mock<IHubClients<IDeveloperIssuesClient>>();
        _mockClientProxy = new Mock<IDeveloperIssuesClient>();

        _mockHubClients.Setup(c => c.Group(DeveloperIssuesHub.FeedGroupName)).Returns(_mockClientProxy.Object);
        _mockHubContext.Setup(h => h.Clients).Returns(_mockHubClients.Object);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testTempDir))
            {
                Directory.Delete(_testTempDir, recursive: true);
            }
        }
        catch { }
    }

    private DeveloperIssueAdminService CreateService(string? customNdjson = null)
    {
        string ndjson = customNdjson ?? Path.Combine(_testTempDir, $"developer_issues_{Guid.NewGuid():N}.ndjson");
        return new DeveloperIssueAdminService(
            scopeFactory: null,
            hubContext: _mockHubContext.Object,
            env: _mockEnv.Object,
            logger: _mockLogger.Object,
            customNdjsonFilePath: ndjson);
    }

    [Fact]
    public async Task DashboardViewModel_HydratesDefaultSeeds_WhenNoDbOrFilePresent()
    {
        var service = CreateService();

        DeveloperIssuesDashboardViewModel model = await service.GetDashboardViewModelAsync();

        Assert.NotNull(model);
        Assert.NotNull(model.Kpis);
        Assert.True(model.Kpis.TotalCaught >= 5, "Store should contain seeded telemetry");
        Assert.True(model.Kpis.AutoResolvedCount >= 3, "Seeded issues should contain auto-resolved items");
        Assert.NotEmpty(model.Issues);
        Assert.NotEmpty(model.RecentActivities);
    }

    [Fact]
    public async Task FilterMatrix_FiltersBySubsystemAndSeverity_ReturnsExactMatches()
    {
        var service = CreateService();

        // Filter Subsystem: AssetPipeline
        var assetIssues = await service.GetIssuesAsync(subsystem: "AssetPipeline");
        Assert.NotEmpty(assetIssues);
        Assert.All(assetIssues, i => Assert.Equal("AssetPipeline", i.Subsystem, ignoreCase: true));

        // Filter Severity: CriticalCrash
        var crashIssues = await service.GetIssuesAsync(severity: "CriticalCrash");
        Assert.NotEmpty(crashIssues);
        Assert.All(crashIssues, i => Assert.Equal("CriticalCrash", i.Severity, ignoreCase: true));

        // Instant Search Query
        var searchIssues = await service.GetIssuesAsync(search: "CXMLDocument");
        Assert.NotEmpty(searchIssues);
        Assert.Contains(searchIssues, i => i.Title.Contains("CXMLDocument", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReflexHeal_WhenNativeCrashDetourTargeted_SynthesizesDetourAndUpdatesKpis()
    {
        var service = CreateService();

        string crashFingerprint = "7a8b9c0d1e2f3a4b";
        DeveloperIssueItemDto? initial = await service.GetIssueByFingerprintAsync(crashFingerprint);
        Assert.NotNull(initial);
        Assert.Equal("Open", initial.Status);

        ReflexTriggerResultDto result = await service.TriggerReflexHealAsync(crashFingerprint);

        Assert.True(result.Success);
        Assert.True(result.Healed);
        Assert.Equal("CrashDetourRecipe", result.RecipeName);
        Assert.Contains("AbiSafeDetour", result.Receipt);
        Assert.Equal("kongor_crash_patch", result.SuggestedDownstreamTool);

        // Verify status changed to AutoResolved
        DeveloperIssueItemDto? after = await service.GetIssueByFingerprintAsync(crashFingerprint);
        Assert.NotNull(after);
        Assert.Equal("AutoResolved", after.Status);
        Assert.Equal("CrashDetourRecipe", after.ResolutionRecipeApplied);

        // Verify SignalR broadcast
        _mockClientProxy.Verify(c => c.OnIssueUpdated(It.Is<DeveloperIssueItemDto>(i => i.Fingerprint == crashFingerprint)), Times.AtLeastOnce);
        _mockClientProxy.Verify(c => c.OnReflexResult(It.Is<ReflexTriggerResultDto>(r => r.Fingerprint == crashFingerprint)), Times.AtLeastOnce);
        _mockClientProxy.Verify(c => c.OnActivityRecorded(It.Is<DeveloperIssueActivityDto>(a => a.Fingerprint == crashFingerprint)), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ReflexHeal_WhenMissingTextureTargeted_AppliesAssetBakeRecipe()
    {
        var service = CreateService();

        string testFp = "test-texture-fail-001";
        await service.IngestOrUpdateIssueAsync(new DeveloperIssueItemDto
        {
            Id = "test-iss-1",
            Fingerprint = testFp,
            Subsystem = "AssetPipeline",
            Severity = "Error",
            Status = "Open",
            Title = "Missing Texture: test_hero_diff.tga",
            RawMessage = "bitmap_load: file not found '/heroes/test/textures/test_hero_diff.tga'",
            NormalizedSignature = "AssetBake!BitmapLoad:test_hero_diff.tga",
            FirstSeenUtc = TimeProvider.System.GetUtcNow(),
            LastSeenUtc = TimeProvider.System.GetUtcNow()
        });

        ReflexTriggerResultDto result = await service.TriggerReflexHealAsync(testFp);

        Assert.True(result.Success);
        Assert.Equal("MissingTextureRecipe", result.RecipeName);
        Assert.Contains("$flat_norm", result.Receipt);
        Assert.Equal("kongor_asset_bake_pipeline", result.SuggestedDownstreamTool);
    }

    [Fact]
    public async Task ReflexHeal_WhenZombiePortCollisionTargeted_AppliesZombiePortRecipe()
    {
        var service = CreateService();

        string testFp = "test-port-zombie-002";
        await service.IngestOrUpdateIssueAsync(new DeveloperIssueItemDto
        {
            Id = "test-iss-2",
            Fingerprint = testFp,
            Subsystem = "DedicatedServer",
            Severity = "Error",
            Status = "ActionRequired",
            Title = "Port Collision 11235",
            RawMessage = "Socket bind failed on port 11235: WSAEADDRINUSE (10048)",
            NormalizedSignature = "DedicatedServer!SocketBind:11235",
            FirstSeenUtc = TimeProvider.System.GetUtcNow(),
            LastSeenUtc = TimeProvider.System.GetUtcNow()
        });

        ReflexTriggerResultDto result = await service.TriggerReflexHealAsync(testFp);

        Assert.True(result.Success);
        Assert.Equal("ZombiePortRecipe", result.RecipeName);
        Assert.Contains("DedicatedServer", result.Receipt);
        Assert.Equal("kongor_game_server_manage", result.SuggestedDownstreamTool);
    }

    [Fact]
    public async Task ManualResolution_MarksIssueResolvedAndRecordsActivity()
    {
        var service = CreateService();

        string testFp = "test-manual-res-003";
        await service.IngestOrUpdateIssueAsync(new DeveloperIssueItemDto
        {
            Id = "test-iss-3",
            Fingerprint = testFp,
            Subsystem = "General",
            Severity = "Warning",
            Status = "Open",
            Title = "Transient Configuration Warning",
            RawMessage = "Config key missing: MaxRetryAttempts",
            NormalizedSignature = "General!ConfigCheck",
            FirstSeenUtc = TimeProvider.System.GetUtcNow(),
            LastSeenUtc = TimeProvider.System.GetUtcNow()
        });

        bool success = await service.ResolveIssueManuallyAsync(testFp, "Added default fallback in appsettings.json");
        Assert.True(success);

        DeveloperIssueItemDto? resolved = await service.GetIssueByFingerprintAsync(testFp);
        Assert.NotNull(resolved);
        Assert.Equal("ManualResolved", resolved.Status);
        Assert.Equal("Added default fallback in appsettings.json", resolved.ResolutionReceipt);

        _mockClientProxy.Verify(c => c.OnIssueUpdated(It.Is<DeveloperIssueItemDto>(i => i.Fingerprint == testFp)), Times.AtLeastOnce);
    }
}
