// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Xunit;

namespace Kongor.Mcp.Tests;

public class TaoPhase7SelfHealingTests : IDisposable
{
    private readonly string _tempTestDir;

    public TaoPhase7SelfHealingTests()
    {
        _tempTestDir = Path.Combine(Path.GetTempPath(), "KongorSelfHealTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempTestDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempTestDir))
            {
                Directory.Delete(_tempTestDir, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public void ErrorFingerprintService_NormalizesDynamicTokens_ProducesDeterministicHash()
    {
        var service = new ErrorFingerprintService();

        string raw1 = "[11:20:15.123] [ERROR] Access violation reading 0x00007FF63B81A120 at k2_x64.dll+0x3A20 (PID: 14220, Unit #412)";
        string raw2 = "[18:45:02.999] [ERROR] Access violation reading 0x00007FF89C110040 at k2_x64.dll+0x3A20 (PID: 8812, Unit #905)";

        var res1 = service.ProcessError(raw1);
        var res2 = service.ProcessError(raw2);

        Assert.Equal(res1.Fingerprint, res2.Fingerprint);
        Assert.Contains("<ADDR>", res1.NormalizedSignature);
        Assert.Contains("<TIME>", res1.NormalizedSignature);
        Assert.Contains("<PID>", res1.NormalizedSignature);
        Assert.Contains("<ID>", res1.NormalizedSignature);
    }

    [Fact]
    public void ErrorFingerprintService_ThrottlesLogSpam_WithinSlidingWindow()
    {
        var service = new ErrorFingerprintService(defaultWindow: TimeSpan.FromSeconds(2.0));
        string error = "[ERROR] Bitmap_Load: File not found: textures/heroes/pyro/fire.tga";

        var res1 = service.ProcessError(error);
        Assert.False(res1.IsThrottled);
        Assert.Equal(1, res1.OccurrenceCount);

        // Immediate subsequent call should be throttled
        var res2 = service.ProcessError(error);
        Assert.True(res2.IsThrottled);
        Assert.Equal(2, res2.OccurrenceCount);
        Assert.Equal(res1.Fingerprint, res2.Fingerprint);
    }

    [Fact]
    public void DeveloperIssueStore_DualStateReflex_RecordsFirstThenResolves()
    {
        using var store = new DeveloperIssueStore(logDirectory: _tempTestDir);

        var issue = new DeveloperIssueRecord
        {
            Fingerprint = "fp_test_1",
            Title = "Crash in cgame_x64.dll",
            Subsystem = IssueSubsystem.GameClient,
            Severity = IssueSeverity.CriticalCrash,
            Status = IssueResolutionStatus.Open
        };

        var saved = store.UpsertIssue(issue);
        Assert.Equal(IssueResolutionStatus.Open, saved.Status);
        Assert.Equal(1, saved.OccurrenceCount);

        // Simulate reflex healing resolution
        bool marked = store.MarkAutoResolved("fp_test_1", "CrashDetourRecipe", "Detour applied");
        Assert.True(marked);

        var updated = store.GetIssue("fp_test_1");
        Assert.NotNull(updated);
        Assert.Equal(IssueResolutionStatus.AutoResolved, updated.Status);
        Assert.Equal("CrashDetourRecipe", updated.ResolutionRecipeApplied);
    }

    [Fact]
    public void DeveloperIssueStore_DetectsRegression_AndTripsCircuitBreaker()
    {
        using var store = new DeveloperIssueStore(logDirectory: _tempTestDir);

        var issue = new DeveloperIssueRecord
        {
            Fingerprint = "fp_regress_test",
            Title = "Transient audio glitch",
            Subsystem = IssueSubsystem.AudioSpatializer,
            Status = IssueResolutionStatus.Open
        };

        store.UpsertIssue(issue);
        store.MarkAutoResolved("fp_regress_test", "AudioLoudnessRecipe", "Normalized");

        // First recurrence -> status becomes Regression
        var regressed = store.UpsertIssue(new DeveloperIssueRecord { Fingerprint = "fp_regress_test" });
        Assert.Equal(IssueResolutionStatus.Regression, regressed.Status);
        Assert.Equal(IssueSeverity.CriticalCrash, regressed.Severity);

        // Multiple recurrences trip the circuit breaker to ActionRequired
        store.UpsertIssue(new DeveloperIssueRecord { Fingerprint = "fp_regress_test" });
        var circuitBroken = store.UpsertIssue(new DeveloperIssueRecord { Fingerprint = "fp_regress_test" });
        Assert.Equal(IssueResolutionStatus.ActionRequired, circuitBroken.Status);
    }

    [Fact]
    public void LogTailWatcherService_MultiLineStackAccumulator_ParsesErrorBlocks()
    {
        var watcher = new LogTailWatcherService();
        string logContent = @"
[11:00:00] Engine initialized.
[11:00:01] [ERROR] Unhandled Access Violation in k2_x64.dll+0x3A20
   at K2_EntityUpdate() in k2_x64.dll:0x3A20
   at K2_FrameStep() in k2_x64.dll:0x1200
[11:00:02] Next regular log line.
";

        var events = watcher.ScanText(logContent, source: "console.log");
        Assert.Single(events);
        var evt = events[0];

        Assert.Equal(IssueSubsystem.GameClient, evt.Subsystem);
        Assert.Equal(IssueSeverity.CriticalCrash, evt.Severity);
        Assert.Equal("k2_x64.dll", evt.FaultingModule);
        Assert.Equal((ulong)0x3A20, evt.FaultingAddress);
        Assert.NotNull(evt.StackTrace);
        Assert.Contains("K2_EntityUpdate", evt.StackTrace);
    }

    [Fact]
    public async Task SelfHealOrchestratorService_MatchesRecipes_TransitionsToAutoResolved()
    {
        using var store = new DeveloperIssueStore(logDirectory: _tempTestDir);
        var orchestrator = new SelfHealOrchestratorService(issueStore: store);

        // Ingest crash
        string crashLog = "[ERROR] Access violation reading 0x0 at k2_x64.dll+0x3A20";
        var summary1 = await orchestrator.IngestTextAsync(crashLog);
        Assert.Equal(1, summary1.TotalCaught);
        Assert.Equal(1, summary1.AutoResolvedCount);

        // Ingest missing texture
        string textureLog = "[WARNING] Bitmap_Load: File not found: textures/missing_sword.tga";
        var summary2 = await orchestrator.IngestTextAsync(textureLog);
        Assert.Equal(2, summary2.TotalCaught);
        Assert.Equal(2, summary2.AutoResolvedCount);

        // Ingest audio clipping
        string audioLog = "[ERROR] Audio clipping detected: LUFS -8.2 exceeds broadcast ceiling";
        var summary3 = await orchestrator.IngestTextAsync(audioLog);
        Assert.Equal(3, summary3.TotalCaught);
        Assert.Equal(3, summary3.AutoResolvedCount);
        Assert.Equal(0, summary3.ActionRequiredCount);
    }

    [Fact]
    public async Task SelfHealHandler_ScanAndIngest_AndPendingBacklog_EmitsTruthEnvelope()
    {
        using var store = new DeveloperIssueStore(logDirectory: _tempTestDir);
        var handler = new SelfHealHandler(issueStore: store);

        // Action: scan_and_ingest with raw log
        var scanArgs = JsonElement.Parse(@"{
            ""action"": ""scan_and_ingest"",
            ""raw_log"": ""[ERROR] Access violation in k2_x64.dll+0x3A20""
        }");

        var scanResult = await handler.HandleAsync(scanArgs);
        Assert.False(scanResult.IsError);
        string scanJson = ((TextContentItem)scanResult.Content[0]).Text;
        using var scanDoc = JsonDocument.Parse(scanJson);
        Assert.Equal("success", scanDoc.RootElement.GetProperty("status").GetString());
        Assert.True(scanDoc.RootElement.TryGetProperty("handoffs", out var handoffs));

        // Action: journey_summary
        var journeyArgs = JsonElement.Parse(@"{ ""action"": ""journey_summary"" }");
        var journeyResult = await handler.HandleAsync(journeyArgs);
        Assert.False(journeyResult.IsError);
        string journeyJson = ((TextContentItem)journeyResult.Content[0]).Text;
        using var journeyDoc = JsonDocument.Parse(journeyJson);
        Assert.Equal("success", journeyDoc.RootElement.GetProperty("status").GetString());
        Assert.Contains("Self-Healing Journey Summary", journeyDoc.RootElement.GetProperty("report").GetString());

        // Action: sync_tracker
        var syncArgs = JsonElement.Parse(@"{ ""action"": ""sync_tracker"" }");
        var syncResult = await handler.HandleAsync(syncArgs);
        Assert.False(syncResult.IsError);
        string syncJson = ((TextContentItem)syncResult.Content[0]).Text;
        using var syncDoc = JsonDocument.Parse(syncJson);
        Assert.Equal("success", syncDoc.RootElement.GetProperty("status").GetString());
        Assert.Contains("Self-Healing Sync Report", syncDoc.RootElement.GetProperty("report").GetString());
    }

    [Fact]
    public void McpToolRegistry_RegistersPhase7ToolsAndAliases()
    {
        var registry = new McpToolRegistry(registerDefaults: true);

        Assert.NotNull(registry.GetTool("kongor_self_heal"));
        Assert.NotNull(registry.GetTool("self_heal"));
    }

    [Fact]
    public void ErrorFingerprintService_StripsCaretColorCodes_FromTitles()
    {
        var service = new ErrorFingerprintService();

        string colored = "^r[DEVELOPER_ISSUE][^gAssetPipeline^r][^yWarning^r] ^wMissingTexture^r | Texture not found";
        string clean = "[DEVELOPER_ISSUE][AssetPipeline][Warning] MissingTexture | Texture not found";

        string normColored = service.Normalize(colored);
        string normClean = service.Normalize(clean);

        Assert.Equal(normClean, normColored);
        Assert.DoesNotContain("^r", normColored);
        Assert.DoesNotContain("^g", normColored);
        Assert.DoesNotContain("^y", normColored);
        Assert.DoesNotContain("^w", normColored);
    }

    [Fact]
    public void LogTailWatcherService_ParsesStructuredDeveloperIssueAndLuaErrors_WithStack()
    {
        var watcher = new LogTailWatcherService();

        string logText = @"
^r[DEVELOPER_ISSUE][AssetPipeline][Warning] MissingTexture | Texture heroes/pyromancer/diffuse.tga not found
^r[LUA_ERROR][HeroSelect] attempt to index global 'Windows' (a nil value)
^r[LUA_STACK] stack traceback:
^r[LUA_STACK]   games/client/override/ui/scripts/hero_select.lua:42: in function 'Init'
^r[LUA_STACK]   games/client/override/ui/scripts/main_v2.lua:105: in function 'OnLoad'
";

        var events = watcher.ScanText(logText, "client/console.log");
        Assert.Equal(2, events.Count);

        // Event 1: Structured [DEVELOPER_ISSUE] tag
        var evt1 = events[0];
        Assert.Equal(IssueSubsystem.AssetPipeline, evt1.Subsystem);
        Assert.Equal(IssueSeverity.Warning, evt1.Severity);
        Assert.Equal("MissingTexture", evt1.HeaderLine);
        Assert.Contains("heroes/pyromancer/diffuse.tga not found", evt1.FullPayload);

        // Event 2: In-Engine [LUA_ERROR] with accumulated [LUA_STACK]
        var evt2 = events[1];
        Assert.Equal(IssueSubsystem.GameClient, evt2.Subsystem);
        Assert.Equal(IssueSeverity.Error, evt2.Severity);
        Assert.Equal("[LUA_ERROR][HeroSelect] attempt to index global 'Windows' (a nil value)", evt2.HeaderLine);
        Assert.NotNull(evt2.StackTrace);
        Assert.Contains("hero_select.lua:42", evt2.StackTrace);
        Assert.Contains("main_v2.lua:105", evt2.StackTrace);
    }
}
