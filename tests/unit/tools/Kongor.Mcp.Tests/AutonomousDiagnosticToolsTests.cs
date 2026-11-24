// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

#pragma warning disable CA2026
using System.IO;
using System.Linq;
using System.Text.Json;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Imaging;
using Kongor.Cli.Mcp.Protocol;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class AutonomousDiagnosticToolsTests
{
    private readonly McpToolRegistry _registry = new(registerDefaults: true);

    private static string GetJsonText(ToolCallResult result)
    {
        if (result.Content.Count > 0 && result.Content[0] is TextContentItem textItem)
        {
            return textItem.Text;
        }
        return result.FirstText;
    }

    // =========================================================================
    // 1. kongor_telemetry_stream (20Hz/60Hz Entity Telemetry Streaming)
    // =========================================================================

    [Fact]
    public async Task TelemetryStream_Snapshot_ReturnsComprehensiveEntityData()
    {
        TelemetryStreamHandler handler = _registry.TelemetryStreamHandler;
        JsonElement args = JsonDocument.Parse("""{"action": "snapshot", "port": 11235, "rateHz": 60, "allowSimulation": true}""").RootElement;

        ToolCallResult result = await handler.HandleAsync(args);

        Assert.False(result.IsError);
        Assert.NotEmpty(result.Content);
        string text = GetJsonText(result);
        using JsonDocument doc = JsonDocument.Parse(text);
        JsonElement root = doc.RootElement;

        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("snapshot", root.GetProperty("action").GetString());
        Assert.Equal(11235, root.GetProperty("serverPort").GetInt32());
        Assert.Equal(60, root.GetProperty("rateHz").GetInt32());

        JsonElement entities = root.GetProperty("entities");
        Assert.True(entities.GetProperty("heroCount").GetInt32() >= 3);
        Assert.True(entities.GetProperty("creepCount").GetInt32() >= 3);
        Assert.True(entities.GetProperty("towerCount").GetInt32() >= 2);
        Assert.True(entities.GetProperty("projectileCount").GetInt32() >= 1);

        // Verify Hero Pyromancer attributes
        JsonElement heroes = entities.GetProperty("heroes");
        JsonElement pyro = heroes[0];
        Assert.Equal("Hero_Pyromancer", pyro.GetProperty("entityName").GetString());
        Assert.Equal(1, pyro.GetProperty("team").GetInt32());
        Assert.True(pyro.GetProperty("health").GetDouble() > 0);
        Assert.True(pyro.GetProperty("mana").GetDouble() > 0);
        Assert.True(pyro.GetProperty("position").GetProperty("x").GetDouble() > 0);
    }

    [Fact]
    public async Task TelemetryStream_StartAndStop_ManagesSessionLifecycle()
    {
        TelemetryStreamHandler handler = _registry.TelemetryStreamHandler;
        string session = $"test_stream_{Guid.NewGuid():N}";

        // Start
        JsonElement startArgs = JsonDocument.Parse($$"""{"action": "start", "sessionId": "{{session}}", "port": 11236, "rateHz": 20}""").RootElement;
        ToolCallResult startRes = await handler.HandleAsync(startArgs);
        Assert.False(startRes.IsError);
        using JsonDocument startDoc = JsonDocument.Parse(GetJsonText(startRes));
        Assert.Equal("streaming", startDoc.RootElement.GetProperty("status").GetString());
        Assert.Equal(50.0, startDoc.RootElement.GetProperty("frameIntervalMs").GetDouble());

        // Snapshot increments frame count
        JsonElement snapArgs = JsonDocument.Parse($$"""{"action": "snapshot", "sessionId": "{{session}}"}""").RootElement;
        await handler.HandleAsync(snapArgs);

        // Stop
        JsonElement stopArgs = JsonDocument.Parse($$"""{"action": "stop", "sessionId": "{{session}}"}""").RootElement;
        ToolCallResult stopRes = await handler.HandleAsync(stopArgs);
        Assert.False(stopRes.IsError);
        using JsonDocument stopDoc = JsonDocument.Parse(GetJsonText(stopRes));
        Assert.Equal("stopped", stopDoc.RootElement.GetProperty("status").GetString());
        Assert.True(stopDoc.RootElement.GetProperty("totalFramesStreamed").GetInt64() >= 1);
    }

    // =========================================================================
    // 2. kongor_db_migration_test (Drift Verification & Rollback)
    // =========================================================================

    [Fact]
    public async Task DbMigrationTest_VerifyDrift_RequiresLiveDatabaseConnectionString()
    {
        DbMigrationTestHandler handler = _registry.DbMigrationTestHandler;
        JsonElement args = JsonDocument.Parse("""{"action": "verify_drift", "provider": "all"}""").RootElement;

        ToolCallResult result = await handler.HandleAsync(args);

        Assert.True(result.IsError);
        string error = (result.Content[0] as TextContentItem)!.Text;
        Assert.Contains("requires an active database connection string", error);
    }

    [Fact]
    public async Task DbMigrationTest_TestRollback_RequiresLiveDatabaseConnectionString()
    {
        DbMigrationTestHandler handler = _registry.DbMigrationTestHandler;
        JsonElement args = JsonDocument.Parse("""{"action": "test_rollback", "provider": "postgresql"}""").RootElement;

        ToolCallResult result = await handler.HandleAsync(args);

        Assert.True(result.IsError);
        string error = (result.Content[0] as TextContentItem)!.Text;
        Assert.Contains("requires an active database connection string", error);
    }

    // =========================================================================
    // 3. kongor_memory_leak_detect (Working Set & Handle Regression Profiling)
    // =========================================================================

    [Fact]
    public async Task MemoryLeakDetect_Sample_ReturnsProcessMemoryMetrics()
    {
        MemoryLeakDetectHandler handler = _registry.MemoryLeakDetectHandler;
        int currentPid = Environment.ProcessId;
        JsonElement args = JsonDocument.Parse($$"""{"action": "sample", "pid": {{currentPid}}}""").RootElement;

        ToolCallResult result = await handler.HandleAsync(args);

        Assert.False(result.IsError);
        using JsonDocument doc = JsonDocument.Parse(GetJsonText(result));
        JsonElement root = doc.RootElement;

        Assert.Equal("success", root.GetProperty("status").GetString());
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.True(root.GetProperty("processFound").GetBoolean());
        JsonElement metrics = root.GetProperty("metrics");
        Assert.True(metrics.GetProperty("workingSetBytes").GetInt64() > 0);
        Assert.True(metrics.GetProperty("privateBytes").GetInt64() > 0);
        Assert.True(metrics.GetProperty("handleCount").GetInt32() > 0);
        Assert.True(metrics.GetProperty("gdiHandles").GetInt32() >= 0);
        Assert.True(metrics.GetProperty("userHandles").GetInt32() >= 0);

        JsonElement missingArgs = JsonDocument.Parse("""{"action": "sample", "pid": 99999999}""").RootElement;
        ToolCallResult missingResult = await handler.HandleAsync(missingArgs);
        using JsonDocument missingDoc = JsonDocument.Parse(GetJsonText(missingResult));
        Assert.Equal("no_process", missingDoc.RootElement.GetProperty("status").GetString());
        Assert.False(missingDoc.RootElement.GetProperty("processFound").GetBoolean());
        Assert.False(missingDoc.RootElement.TryGetProperty("metrics", out JsonElement missingMetrics) && missingMetrics.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task MemoryLeakDetect_Profile_ComputesRegressionSlope()
    {
        MemoryLeakDetectHandler handler = _registry.MemoryLeakDetectHandler;
        int currentPid = Environment.ProcessId;
        JsonElement args = JsonDocument.Parse($$"""{"action": "profile", "pid": {{currentPid}}, "durationSeconds": 1, "intervalMs": 100}""").RootElement;

        ToolCallResult result = await handler.HandleAsync(args);

        Assert.False(result.IsError);
        using JsonDocument doc = JsonDocument.Parse(GetJsonText(result));
        JsonElement root = doc.RootElement;

        Assert.Equal("success", root.GetProperty("status").GetString());
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.True(root.GetProperty("processFound").GetBoolean());
        Assert.True(root.GetProperty("sampleCount").GetInt32() >= 2);
        Assert.True(root.TryGetProperty("growthRateBytesPerSec", out _));
        Assert.True(root.TryGetProperty("handleGrowthRatePerSec", out _));
        Assert.NotNull(root.GetProperty("recommendation").GetString());
    }

    // =========================================================================
    // 4. kongor_crash_dump_analyze (Win32 Minidump Parsing & Stack Unwinding)
    // =========================================================================

    [Fact]
    public async Task CrashDumpAnalyze_Analyze_ExtractsExceptionCodeAndStack()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"kongor_dump_analyze_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string dumpPath = Path.Combine(tempDir, "synthetic_crash.dmp");
        try
        {
            using (FileStream fs = File.Create(dumpPath))
            using (BinaryWriter writer = new BinaryWriter(fs))
            {
                writer.Write(0x504D444DU); // 'MDMP'
                writer.Write(0x0000A793U); // Version
                writer.Write(1U);          // StreamCount = 1
                writer.Write(32U);         // StreamDirectoryRva = 32
                writer.Write(0U);          // CheckSum
                writer.Write(0U);          // TimeDateStamp
                writer.Write(0UL);         // Flags

                // Stream 6: ExceptionStream at RVA 44, size 48
                writer.Write(6U);
                writer.Write(48U);
                writer.Write(44U);

                writer.Write(100U);        // ThreadId
                writer.Write(0U);          // Alignment
                writer.Write(0xC0000005U); // ExceptionCode
                writer.Write(0U);          // ExceptionFlags
                writer.Write(0UL);         // ExceptionRecord
                writer.Write(0x00007FF700001402UL); // ExceptionAddress
                writer.Write(2U);          // NumberParameters
                writer.Write(0U);          // UnusedAlignment
                writer.Write(0UL);         // Read violation
                writer.Write(0x18UL);      // Target address
            }

            CrashDumpAnalyzeHandler handler = new CrashDumpAnalyzeHandler(searchDirectory: tempDir);
            JsonElement args = JsonDocument.Parse($$"""{"action": "analyze", "dumpPath": "{{dumpPath.Replace("\\", "/")}}"}""").RootElement;

            ToolCallResult result = await handler.HandleAsync(args);

            Assert.False(result.IsError);
            using JsonDocument doc = JsonDocument.Parse(GetJsonText(result));
            JsonElement root = doc.RootElement;

            Assert.Equal("success", root.GetProperty("status").GetString());
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.True(root.GetProperty("dumpFound").GetBoolean());
            Assert.Equal("0xC0000005", root.GetProperty("exceptionCode").GetString());
            Assert.Equal("STATUS_ACCESS_VIOLATION", root.GetProperty("exceptionName").GetString());
            Assert.Equal("x64", root.GetProperty("architecture").GetString());

            JsonElement stack = root.GetProperty("callstack");
            Assert.Equal(1, stack.GetArrayLength());
            Assert.Contains("0x7FF700001402", stack[0].GetString());

            string emptyDir = Path.Combine(tempDir, "empty");
            Directory.CreateDirectory(emptyDir);
            CrashDumpAnalyzeHandler emptyHandler = new CrashDumpAnalyzeHandler(searchDirectory: emptyDir);
            ToolCallResult missingResult = await emptyHandler.HandleAsync(JsonDocument.Parse("""{"action": "analyze"}""").RootElement);
            using JsonDocument missingDoc = JsonDocument.Parse(GetJsonText(missingResult));
            Assert.Equal("no_artifact", missingDoc.RootElement.GetProperty("status").GetString());
            Assert.False(missingDoc.RootElement.GetProperty("dumpFound").GetBoolean());
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CrashDumpAnalyze_ListRecent_ReturnsArray()
    {
        CrashDumpAnalyzeHandler handler = _registry.CrashDumpAnalyzeHandler;
        JsonElement args = JsonDocument.Parse("""{"action": "list_recent"}""").RootElement;

        ToolCallResult result = await handler.HandleAsync(args);

        Assert.False(result.IsError);
        using JsonDocument doc = JsonDocument.Parse(GetJsonText(result));
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.True(doc.RootElement.TryGetProperty("dumps", out _));
    }

    // =========================================================================
    // 5. kongor_bot_choreography (Port-Bound Dual Channel Routing)
    // =========================================================================

    [Fact]
    public async Task BotChoreography_PresetPyroCombo_StrictlyRoutesToPort11235()
    {
        BotChoreographyHandler handler = _registry.BotChoreographyHandler;
        JsonElement args = JsonDocument.Parse("""{"action": "preset", "preset": "pyro_mid_combo", "port": 11235}""").RootElement;

        ToolCallResult result = await handler.HandleAsync(args);

        Assert.False(result.IsError);
        using JsonDocument doc = JsonDocument.Parse(GetJsonText(result));
        JsonElement root = doc.RootElement;

        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal(11235, root.GetProperty("port").GetInt32());
        Assert.Equal(@"\\.\pipe\hon-server-11235", root.GetProperty("targetPipe").GetString());
        Assert.True(root.GetProperty("commandRoutingRuleSatisfied").GetBoolean());

        JsonElement commands = root.GetProperty("commands");
        Assert.True(commands.GetArrayLength() >= 3);

        // Step 1: AddFakePlayer on redirected stdin
        Assert.Equal("stdin", commands[0].GetProperty("channel").GetString());
        Assert.Equal("AddFakePlayer 2", commands[0].GetProperty("command").GetString());

        // Step 2: Ability sequence on named pipe
        Assert.Equal("ipc_pipe", commands[1].GetProperty("channel").GetString());
        Assert.Contains("BotCastAbility", commands[1].GetProperty("command").GetString());
    }

    [Fact]
    public async Task BotChoreography_Stop_HaltedCleanly()
    {
        BotChoreographyHandler handler = _registry.BotChoreographyHandler;
        JsonElement args = JsonDocument.Parse("""{"action": "stop", "port": 11235}""").RootElement;

        ToolCallResult result = await handler.HandleAsync(args);

        Assert.False(result.IsError);
        using JsonDocument doc = JsonDocument.Parse(GetJsonText(result));
        Assert.Equal("halted", doc.RootElement.GetProperty("status").GetString());
    }

    // =========================================================================
    // 6. kongor_network_impair_simulate (Dual Channel Netcode Impairment)
    // =========================================================================

    [Fact]
    public async Task NetworkImpairSimulate_SetAndReset_EmitsPortSpecificCvars()
    {
        NetworkImpairSimulateHandler handler = _registry.NetworkImpairSimulateHandler;

        // Set
        JsonElement setArgs = JsonDocument.Parse("""{"action": "set", "port": 11235, "latencyMs": 150, "packetLossPercent": 2.5, "jitterMs": 20}""").RootElement;
        ToolCallResult setRes = await handler.HandleAsync(setArgs);

        Assert.False(setRes.IsError);
        using JsonDocument setDoc = JsonDocument.Parse(GetJsonText(setRes));
        JsonElement setRoot = setDoc.RootElement;
        Assert.Equal(11235, setRoot.GetProperty("port").GetInt32());
        Assert.Equal(150, setRoot.GetProperty("impairment").GetProperty("latencyMs").GetInt32());
        Assert.Equal(2.5, setRoot.GetProperty("impairment").GetProperty("packetLossPercent").GetDouble());
        Assert.Equal(20, setRoot.GetProperty("impairment").GetProperty("jitterMs").GetInt32());

        // Reset
        JsonElement resetArgs = JsonDocument.Parse("""{"action": "reset", "port": 11235}""").RootElement;
        ToolCallResult resetRes = await handler.HandleAsync(resetArgs);
        Assert.False(resetRes.IsError);
        using JsonDocument resetDoc = JsonDocument.Parse(GetJsonText(resetRes));
        Assert.Equal("cleared", resetDoc.RootElement.GetProperty("status").GetString());
        Assert.Equal(0, resetDoc.RootElement.GetProperty("impairment").GetProperty("latencyMs").GetInt32());
    }

    [Fact]
    public async Task NetworkImpairSimulate_ProfileOceanicLag_AppliesPresetValues()
    {
        NetworkImpairSimulateHandler handler = _registry.NetworkImpairSimulateHandler;
        JsonElement args = JsonDocument.Parse("""{"action": "profile", "profile": "oceanic_lag", "port": 11235}""").RootElement;

        ToolCallResult result = await handler.HandleAsync(args);

        Assert.False(result.IsError);
        using JsonDocument doc = JsonDocument.Parse(GetJsonText(result));
        Assert.Equal(250, doc.RootElement.GetProperty("impairment").GetProperty("latencyMs").GetInt32());
        Assert.Equal(15, doc.RootElement.GetProperty("impairment").GetProperty("jitterMs").GetInt32());
    }

    // =========================================================================
    // 7. kongor_cache_coherence_verify (EF Core & Redis Reconciliation)
    // =========================================================================

    [Fact]
    public async Task CacheCoherenceVerify_Reconcile_VerifiesKeyNamespaces()
    {
        CacheCoherenceVerifyHandler handler = _registry.CacheCoherenceVerifyHandler;
        JsonElement args = JsonDocument.Parse("""{"action": "reconcile", "scope": "all", "allowSimulation": true}""").RootElement;

        ToolCallResult result = await handler.HandleAsync(args);

        Assert.False(result.IsError);
        using JsonDocument doc = JsonDocument.Parse(GetJsonText(result));
        JsonElement root = doc.RootElement;

        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal(1.0, root.GetProperty("coherenceRatio").GetDouble());
        Assert.Equal(0, root.GetProperty("desyncedKeysCount").GetInt32());
        Assert.True(root.GetProperty("totalKeysScanned").GetInt32() > 0);
    }

    [Fact]
    public async Task CacheCoherenceVerify_Heal_ReportsSynchronizedState()
    {
        CacheCoherenceVerifyHandler handler = _registry.CacheCoherenceVerifyHandler;
        JsonElement args = JsonDocument.Parse("""{"action": "heal", "scope": "sessions", "allowSimulation": true}""").RootElement;

        ToolCallResult result = await handler.HandleAsync(args);

        Assert.False(result.IsError);
        using JsonDocument doc = JsonDocument.Parse(GetJsonText(result));
        Assert.Equal("Healed and Synchronized", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task CacheCoherenceVerify_Keys_QueriesAvailableKeys()
    {
        CacheCoherenceVerifyHandler handler = _registry.CacheCoherenceVerifyHandler;
        JsonElement args = JsonDocument.Parse("""{"action": "keys", "pattern": "MATCH*", "allowSimulation": true}""").RootElement;

        ToolCallResult result = await handler.HandleAsync(args);

        Assert.False(result.IsError);
        using JsonDocument doc = JsonDocument.Parse(GetJsonText(result));
        JsonElement root = doc.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("keys", root.GetProperty("action").GetString());
        Assert.True(root.GetProperty("count").GetInt32() >= 0);
    }

    [Fact]
    public async Task CacheCoherenceVerify_HGetAll_BinarySafe_HandlesBinaryStructs()
    {
        CacheCoherenceVerifyHandler handler = _registry.CacheCoherenceVerifyHandler;
        JsonElement args = JsonDocument.Parse("""{"action": "hgetall", "name": "MATCH-SERVERS", "allowSimulation": true}""").RootElement;

        ToolCallResult result = await handler.HandleAsync(args);

        Assert.False(result.IsError);
        using JsonDocument doc = JsonDocument.Parse(GetJsonText(result));
        JsonElement root = doc.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("MATCH-SERVERS", root.GetProperty("key").GetString());
        Assert.True(root.TryGetProperty("entries", out _));
    }

    [Fact]
    public async Task CacheCoherenceVerify_Inspect_DetectsKeyTypeAndReturnsData()
    {
        CacheCoherenceVerifyHandler handler = _registry.CacheCoherenceVerifyHandler;
        JsonElement args = JsonDocument.Parse("""{"action": "inspect", "key": "MATCH-SERVER-MANAGERS", "allowSimulation": true}""").RootElement;

        ToolCallResult result = await handler.HandleAsync(args);

        Assert.False(result.IsError);
        using JsonDocument doc = JsonDocument.Parse(GetJsonText(result));
        JsonElement root = doc.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
    }

    // =========================================================================
    // 8. kongor_ui_visual_diff (SSIM Perceptual Diffing & Validated Screenshots)
    // =========================================================================

    [Fact]
    [Trait("Category", "Manual")]
    public async Task UiVisualDiff_Diff_ComputesSsimScoreAndGeneratesScreenshotArtifact()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        UiVisualDiffHandler handler = _registry.UiVisualDiffHandler;
        string tempOut = Path.Combine(Path.GetTempPath(), $"test_diff_{Guid.NewGuid():N}.png");

        try
        {
            JsonElement args = JsonDocument.Parse($$"""
            {
                "action": "diff",
                "threshold": 0.90,
                "outputPath": "{{tempOut.Replace("\\", "\\\\")}}"
            }
            """).RootElement;

            ToolCallResult result = await handler.HandleAsync(args);

            Assert.False(result.IsError);
            using JsonDocument doc = JsonDocument.Parse(GetJsonText(result));
            JsonElement root = doc.RootElement;

            Assert.True(root.GetProperty("success").GetBoolean());
            double ssim = root.GetProperty("ssimScore").GetDouble();
            Assert.True(ssim > 0.90 && ssim <= 1.0, $"SSIM score {ssim} was expected to be between 0.90 and 1.0");
            Assert.True(root.GetProperty("meetsThreshold").GetBoolean());

            // Validate that the visual diff screenshot artifact was actually created on disk!
            Assert.True(File.Exists(tempOut), "Visual diff PNG screenshot artifact should exist on disk.");
            FileInfo fileInfo = new FileInfo(tempOut);
            Assert.True(fileInfo.Length > 100, $"PNG screenshot file size ({fileInfo.Length} bytes) should be non-trivial.");

            // Persist to conversation artifact directory if active, dynamically discovered without hardcoding
            string? conversationArtifactDir = Environment.GetEnvironmentVariable("ANTIGRAVITY_ARTIFACT_DIR")
                ?? Environment.GetEnvironmentVariable("KONGOR_TEST_ARTIFACT_DIR");

            if (string.IsNullOrEmpty(conversationArtifactDir))
            {
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string brainDir = Path.Combine(userProfile, ".gemini", "antigravity", "brain");
                if (Directory.Exists(brainDir))
                {
                    conversationArtifactDir = Directory.GetDirectories(brainDir)
                        .OrderByDescending(Directory.GetLastWriteTimeUtc)
                        .FirstOrDefault();
                }
            }

            if (!string.IsNullOrEmpty(conversationArtifactDir) && Directory.Exists(conversationArtifactDir))
            {
                File.Copy(tempOut, Path.Combine(conversationArtifactDir, "mcp_ui_visual_diff_screenshot.png"), overwrite: true);
            }

            // Verify with ImageSharp that the file is a valid readable PNG
            using Image<Rgba32> img = Image.Load<Rgba32>(tempOut);
            Assert.True(img.Width > 0 && img.Height > 0);
        }
        finally
        {
            if (File.Exists(tempOut)) File.Delete(tempOut);
        }
    }

    [Fact]
    public async Task UiVisualDiff_Assert_PassesWhenAboveThreshold()
    {
        UiVisualDiffHandler handler = _registry.UiVisualDiffHandler;
        JsonElement args = JsonDocument.Parse("""{"action": "assert", "threshold": 0.85}""").RootElement;

        ToolCallResult result = await handler.HandleAsync(args);

        Assert.False(result.IsError);
        using JsonDocument doc = JsonDocument.Parse(GetJsonText(result));
        Assert.True(doc.RootElement.GetProperty("assertPassed").GetBoolean());
    }

    // =========================================================================
    // 9. McpToolRegistry Integration & Alias Resolution Tests
    // =========================================================================

    [Theory]
    [InlineData("kongor_telemetry_stream", "kongor_telemetry")]
    [InlineData("kongor_db_migration_test", "kongor_drift")]
    [InlineData("kongor_memory_leak_detect", "kongor_leak_detect")]
    [InlineData("kongor_crash_dump_analyze", "kongor_crash_dump")]
    [InlineData("kongor_bot_choreography", "kongor_bot")]
    [InlineData("kongor_network_impair_simulate", "kongor_net_impair")]
    [InlineData("kongor_cache_coherence_verify", "kongor_cache_coherence")]
    [InlineData("kongor_ui_visual_diff", "kongor_visual_diff")]
    public void Registry_WhenInvoked_ExposesCanonicalToolAndAliasRedirect(string canonicalName, string aliasName)
    {
        Assert.True(_registry.HasTool(canonicalName), $"Registry should contain {canonicalName}");
        Assert.True(_registry.HasTool(aliasName), $"Registry should recognize alias {aliasName}");

        ToolDefinition? canonicalDef = _registry.GetTool(canonicalName);
        ToolDefinition? aliasDef = _registry.GetTool(aliasName);

        Assert.NotNull(canonicalDef);
        Assert.NotNull(aliasDef);
        Assert.Equal(canonicalDef.Name, aliasDef.Name);
    }
}
