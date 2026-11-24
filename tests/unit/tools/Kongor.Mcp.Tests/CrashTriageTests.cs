// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Kongor.Core.Services.Native;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class CrashTriageTests : IDisposable
{
    private sealed class FakeTimeProvider(DateTimeOffset fixedTime) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => fixedTime;
    }

    private readonly FakeTimeProvider _timeProvider;
    private readonly string _testTempDir;

    public CrashTriageTests()
    {
        _timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero));
        _testTempDir = Path.Combine(Path.GetTempPath(), $"kongor_crash_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testTempDir);
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
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CrashTriageTests] Cleanup warning: {ex.Message}");
        }
    }

    [Fact]
    public async Task CrashTriageOrchestrator_ParsesSyntheticBinaryMinidump_AndExtractsMdmpHeader()
    {
        string dumpPath = Path.Combine(_testTempDir, "crash.dmp");

        // Construct a synthetic 64-byte MDMP minidump header
        using (FileStream fs = File.Create(dumpPath))
        using (BinaryWriter writer = new BinaryWriter(fs))
        {
            writer.Write((uint)0x504D444D); // 'MDMP' signature
            writer.Write((ushort)0xA793);    // version
            writer.Write((ushort)0x0000);    // internal flags
            writer.Write((uint)1);           // streamCount = 1
            writer.Write((uint)32);          // streamDirRva = 32

            // Stream directory entry: StreamType = 6 (ExceptionStream), DataSize = 12, RVA = 44
            writer.Write((uint)6);
            writer.Write((uint)12);
            writer.Write((uint)44);

            // Exception Stream payload: ThreadId = 100, Alignment = 0, ExceptionCode = 0xC0000005
            writer.Write((uint)100);
            writer.Write((uint)0);
            writer.Write((uint)0xC0000005);
        }

        CrashTriageOrchestrator orchestrator = new CrashTriageOrchestrator(
            _timeProvider,
            logsDirectory: _testTempDir);

        CrashTriageRequest request = new CrashTriageRequest
        {
            DumpPath = dumpPath,
            TargetModule = "k2_x64.dll",
            FaultingAddress = "0x3a20",
            CommandLineArgs = "-dedicated -port 11235 -map caldavar"
        };

        CrashTriageReport report = await orchestrator.TriageAndOrchestrateAsync(request);

        Assert.True(report.Triaged);
        Assert.True(report.IsActualDumpFile);
        Assert.Equal("k2_x64.dll", report.FaultingModule);
        Assert.Equal("0x3a20", report.FaultingRva);
        Assert.Equal("0xC0000005", report.ExceptionCode);
        Assert.Equal("STATUS_ACCESS_VIOLATION", report.ExceptionName);
        Assert.Empty(report.Callstack);

        // Verify Ghidra triangulation
        Assert.Equal("ghidra", report.GhidraTriangulation.SuggestedTool);
        Assert.Equal("decompile_function(file_name: \"k2_x64.dll\", address: \"0x3a20\")", report.GhidraTriangulation.Command);

        // Verify Resuscitation manifest preserves args and uses real games/client path
        Assert.NotNull(report.ResuscitationManifest);
        Assert.True(report.ResuscitationManifest.CanAutoRestart);
        Assert.Equal("11235", report.ResuscitationManifest.PreservedFlags["port"]);
        Assert.Equal("caldavar", report.ResuscitationManifest.PreservedFlags["map"]);
        Assert.DoesNotContain("win-x64", report.ResuscitationManifest.ExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CrashTriageOrchestrator_ParsesTextDiagnosticLog_WithCallstackAndRva()
    {
        string logPath = Path.Combine(_testTempDir, "diag_sample.txt");
        string[] logLines =
        [
            "K2 Engine Diagnostic Crash Log",
            "Exception code: 0xC0000005 (STATUS_ACCESS_VIOLATION)",
            "RAX=0x0000000000000000 RCX=0x000002255b119200 RIP=0x00007ff728ab1402",
            "Callstack trace:",
            "  cgame_x64.dll!CUICommander::Disconnect+0x1e5de0",
            "  k2_x64.dll!CHostClient::Disconnect+0x31a",
            "  hon_x64.exe!Main+0x54"
        ];
        await File.WriteAllLinesAsync(logPath, logLines);

        CrashTriageOrchestrator orchestrator = new CrashTriageOrchestrator(
            _timeProvider,
            logsDirectory: _testTempDir);

        CrashTriageRequest request = new CrashTriageRequest
        {
            DumpPath = logPath
        };

        CrashTriageReport report = await orchestrator.TriageAndOrchestrateAsync(request);

        Assert.True(report.Triaged);
        Assert.True(report.IsActualDumpFile);
        Assert.Equal("cgame_x64.dll", report.FaultingModule);
        Assert.Equal("0x1e5de0", report.FaultingRva);
        Assert.Equal("0xC0000005", report.ExceptionCode);
        Assert.Contains("cgame_x64.dll!CUICommander::Disconnect+0x1e5de0", report.Callstack);
        Assert.Equal("0x00007ff728ab1402", report.Registers["RIP"]);
        Assert.Null(report.ResuscitationManifest);
    }

    [Fact]
    public async Task CrashTriageHandler_HandleAsync_ReturnsValidEnvelopeWithOrchestrationData()
    {
        CrashTriageOrchestrator orchestrator = new CrashTriageOrchestrator(_timeProvider, logsDirectory: _testTempDir);
        CrashTriageHandler handler = new CrashTriageHandler(_timeProvider, logsDirectory: _testTempDir, orchestrator: orchestrator);

        string argsJson = """
        {
            "action": "triage",
            "module": "cgame_x64.dll",
            "address": "0x1234"
        }
        """;
        JsonElement doc = JsonElement.Parse(argsJson);
        ToolCallResult result = await handler.HandleAsync(doc);

        Assert.False(result.IsError);
        string contentJson = ((TextContentItem)result.Content[0]).Text;
        JsonElement root = JsonElement.Parse(contentJson);
        Assert.Equal("success", root.GetProperty("status").GetString());
        Assert.True(root.GetProperty("triaged").GetBoolean());
        Assert.Equal("cgame_x64.dll", root.GetProperty("faultingModule").GetString());
        Assert.Equal("0x1234", root.GetProperty("faultingRva").GetString());
        Assert.True(root.TryGetProperty("resuscitationManifest", out _));

        // Verify empty logsDirectory with no target returns honest no_artifact status
        string emptyArgsJson = """{"action": "triage"}""";
        ToolCallResult noArtifactResult = await handler.HandleAsync(JsonElement.Parse(emptyArgsJson));
        JsonElement noArtifactRoot = JsonElement.Parse(((TextContentItem)noArtifactResult.Content[0]).Text);
        Assert.Equal("no_artifact", noArtifactRoot.GetProperty("status").GetString());
        Assert.False(noArtifactRoot.GetProperty("triaged").GetBoolean());
        Assert.False(noArtifactRoot.GetProperty("dumpFound").GetBoolean());
        Assert.Equal(0, noArtifactRoot.GetProperty("handoffCount").GetInt32());
    }

    [Fact]
    public async Task NetcodeDiagnosticService_AuditPortNetcodeAsync_ReturnsAccurateMetricsAndFallback()
    {
        NetcodeDiagnosticService serviceWithNpcap = new NetcodeDiagnosticService(_timeProvider, forceNpcapAvailable: true);
        NetcodeDiagnosticReport report1 = await serviceWithNpcap.AuditPortNetcodeAsync(11235);

        Assert.Equal(11235, report1.Port);
        Assert.True(report1.NpcapAvailable);
        Assert.Contains("Npcap", report1.TelemetrySource);
        Assert.Equal(NetcodeHealthLevel.Optimal, report1.HealthLevel);

        NetcodeDiagnosticService serviceFallback = new NetcodeDiagnosticService(_timeProvider, forceNpcapAvailable: false);
        NetcodeDiagnosticReport report2 = await serviceFallback.AuditPortNetcodeAsync(11236);

        Assert.Equal(11236, report2.Port);
        Assert.False(report2.NpcapAvailable);
        Assert.Contains("Fallback", report2.TelemetrySource);
        Assert.Equal(NetcodeHealthLevel.Optimal, report2.HealthLevel);
    }

    [Fact]
    public async Task NetcodeDiagnosticService_ReconcileLobbySessionAsync_RestoresRosterAndDraft()
    {
        NetcodeDiagnosticService service = new NetcodeDiagnosticService(_timeProvider);
        LobbyReconciliationResult result = await service.ReconcileLobbySessionAsync("lobby_ranked_55");

        Assert.Equal("lobby_ranked_55", result.LobbyId);
        Assert.True(result.Recovered);
        Assert.Equal(10, result.PlayerSlotsRestored);
        Assert.Equal("HeroPicking", result.DraftState);
    }

    [Fact]
    public async Task ServerManageHandler_NetcodeAuditAndLobbyReconciliation_DispatchesSuccessfully()
    {
        NetcodeDiagnosticService netcodeService = new NetcodeDiagnosticService(_timeProvider, forceNpcapAvailable: false);
        ServerManageHandler handler = new ServerManageHandler(timeProvider: _timeProvider, netcodeDiagnosticService: netcodeService);

        string auditArgs = """{"action": "netcode_audit", "port": 11235}""";
        JsonElement auditDoc = JsonElement.Parse(auditArgs);
        ToolCallResult auditResult = await handler.HandleAsync(auditDoc);

        Assert.False(auditResult.IsError);
        string auditContent = ((TextContentItem)auditResult.Content[0]).Text;
        JsonElement auditJson = JsonElement.Parse(auditContent);
        Assert.Equal("success", auditJson.GetProperty("status").GetString());
        Assert.Equal("netcode_audit", auditJson.GetProperty("action").GetString());

        string lobbyArgs = """{"action": "reconcile_lobby", "lobbyId": "test_lobby_1"}""";
        JsonElement lobbyDoc = JsonElement.Parse(lobbyArgs);
        ToolCallResult lobbyResult = await handler.HandleAsync(lobbyDoc);

        Assert.False(lobbyResult.IsError);
        string lobbyContent = ((TextContentItem)lobbyResult.Content[0]).Text;
        JsonElement lobbyJson = JsonElement.Parse(lobbyContent);
        Assert.Equal("success", lobbyJson.GetProperty("status").GetString());
        Assert.Equal("reconcile_lobby", lobbyJson.GetProperty("action").GetString());
    }

    [Fact]
    public void AutoRemediationService_RemediateLuaScriptContent_RewritesWindowsAndDoEvent()
    {
        AutoRemediationService service = new AutoRemediationService(_timeProvider);
        string luaCode = """
        local btn = Windows.main:GetWidget('practice_button')
        btn:DoEvent('onclick')
        """;

        RemediationActionResult result = service.RemediateLuaScriptContent(luaCode);

        Assert.True(result.Success);
        Assert.Equal("LuaScript", result.TargetType);
        Assert.True(result.ReplacementsMade >= 3);
        Assert.Contains("UIManager.GetInterface('main')", result.RemediatedContent);
        Assert.Contains("sysbar_quickStart", result.RemediatedContent);
        Assert.Contains("btn:DoEvent()", result.RemediatedContent);
        Assert.DoesNotContain("Windows.main", result.RemediatedContent);
        Assert.DoesNotContain("'onclick'", result.RemediatedContent);
    }

    [Fact]
    public void AutoRemediationService_RemediateXmlUiContent_RewritesEmbeddedAttributes_WithoutCorruptingXml()
    {
        AutoRemediationService service = new AutoRemediationService(_timeProvider);
        string xmlUi = """
        <interface name="main">
          <panel name="button_panel">
            <button name="practice_launch" onclick="Windows.main:GetWidget('practice_button'):DoEvent('onclick');" />
          </panel>
        </interface>
        """;

        RemediationActionResult result = service.RemediateXmlUiContent(xmlUi);

        Assert.True(result.Success);
        Assert.Equal("XmlUiDocument", result.TargetType);
        Assert.True(result.ReplacementsMade >= 2);

        // Verify valid XML structure parsing
        XDocument remediatedDoc = XDocument.Parse(result.RemediatedContent!);
        XElement btn = remediatedDoc.Descendants("button").First();
        string onclickVal = btn.Attribute("onclick")!.Value;

        Assert.Contains("UIManager.GetInterface('main')", onclickVal);
        Assert.Contains("DoEvent()", onclickVal);
        Assert.DoesNotContain("Windows.main", onclickVal);
        Assert.DoesNotContain("'onclick'", onclickVal);
    }

    [Fact]
    public async Task IssueTrackerService_RecordsStructuredIssue_AndIncrementsCounters()
    {
        string mockIssuesFile = Path.Combine(_testTempDir, "ISSUES.md");
        string initialContent = """
        # ProjectKongor Living Issue & Self-Healing Backlog

        ## Summary Status
        - **Total Tracked**: 139
        - **Open**: 0
        - **Investigating**: 0
        - **Patched / Resolved**: 139

        ---

        ## Active Issues

        *(No active open issues. All 139 tracked issues have been patched, validated, and resolved.)*

        ---

        ## Resolved Archive

        ### [ISSUE-139] Prior Resolved Issue
        * **Subsystem**: `Prior`
        """;
        await File.WriteAllTextAsync(mockIssuesFile, initialContent);

        IssueTrackerService service = new IssueTrackerService(mockIssuesFile);
        int nextId = await service.GetNextIssueIdAsync();
        Assert.Equal(140, nextId);

        IssueEntry entry = new IssueEntry
        {
            Title = "Phase 2 Autonomous Crash Triage & Self-Healing Integration",
            Subsystem = "Native Runtime & Mcp Services",
            DiscoveryMethod = "Automated integration testing suite",
            Symptom = "Null pointer dereference during ungraceful server termination",
            RootCause = "Unhandled 0xC0000005 access violation requiring Ghidra triangulation",
            Status = IssueStatus.Resolved,
            Remediation = "Integrated CrashTriageOrchestrator and AutoRemediationService."
        };

        string recordedId = await service.RecordIssueAsync(entry);
        Assert.Equal("[ISSUE-140]", recordedId);

        string updatedFile = await File.ReadAllTextAsync(mockIssuesFile);
        Assert.Contains("[ISSUE-140] Phase 2 Autonomous Crash Triage & Self-Healing Integration", updatedFile);
        Assert.Contains("- **Total Tracked**: 140", updatedFile);
        Assert.Contains("- **Patched / Resolved**: 140", updatedFile);
    }

    [Fact]
    public async Task CrashTriageOrchestrator_SynthesizesHotPatchRecipe_WithUnverifiedCandidateFlag()
    {
        CrashTriageOrchestrator orchestrator = new CrashTriageOrchestrator(
            _timeProvider,
            logsDirectory: _testTempDir);

        CrashTriageRequest request = new CrashTriageRequest
        {
            TargetModule = "k2_x64.dll",
            FaultingAddress = "0x3a20"
        };

        CrashTriageReport report = await orchestrator.TriageAndOrchestrateAsync(request);

        Assert.NotNull(report.HotPatchRecipe);
        Assert.Equal("k2_x64.dll", report.HotPatchRecipe.TargetModule);
        Assert.Equal("0x3a20", report.HotPatchRecipe.FaultingRva);
        Assert.False(report.HotPatchRecipe.CanLiveInject);
        Assert.NotEmpty(report.HotPatchRecipe.OriginalBytesHex);
        Assert.NotEmpty(report.HotPatchRecipe.DetourBytesHex);
        Assert.Contains("null-check", report.HotPatchRecipe.SuggestedAction, StringComparison.OrdinalIgnoreCase);

        // Verify hex bytes can be converted to byte array
        byte[] detourBytes = Convert.FromHexString(report.HotPatchRecipe.DetourBytesHex.Replace(" ", ""));
        Assert.True(detourBytes.Length > 0);
    }

    [Fact]
    public async Task NativeManageHandler_TestTrampoline_ReturnsPassWithRel32Displacement()
    {
        NativeManageHandler handler = new NativeManageHandler();
        string argsJson = """{"action": "test_trampoline"}""";
        JsonElement doc = JsonElement.Parse(argsJson);

        ToolCallResult result = await handler.HandleAsync(doc);

        Assert.False(result.IsError);
        string contentJson = ((TextContentItem)result.Content[0]).Text;
        JsonElement root = JsonElement.Parse(contentJson);
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("test_trampoline", root.GetProperty("action").GetString());
        Assert.True(root.GetProperty("fitsWithin").GetBoolean());
        Assert.False(root.GetProperty("fitsOutside").GetBoolean());
        Assert.Contains("PASS", root.GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task NativeManageHandler_HotPatch_ValidatesParametersAndHandlesErrorsDeterministically()
    {
        NativeManageHandler handler = new NativeManageHandler();

        // 1. Missing PID when game is not running or explicit non-existent PID
        string missingBytesJson = """{"action": "hot_patch", "pid": 99999999}""";
        JsonElement missingBytesDoc = JsonElement.Parse(missingBytesJson);
        ToolCallResult result1 = await handler.HandleAsync(missingBytesDoc);
        Assert.True(result1.IsError);
        Assert.Contains("Missing patch bytes", ((TextContentItem)result1.Content[0]).Text);

        // 2. Invalid hex string
        string invalidHexJson = """{"action": "hot_patch", "pid": 99999999, "patch_bytes": "ZZ_INVALID_HEX"}""";
        JsonElement invalidHexDoc = JsonElement.Parse(invalidHexJson);
        ToolCallResult result2 = await handler.HandleAsync(invalidHexDoc);
        Assert.True(result2.IsError);
        Assert.Contains("Failed to parse patch hex bytes", ((TextContentItem)result2.Content[0]).Text);

        // 3. Process not running / inaccessible PID with valid patch bytes
        string noProcJson = """{"action": "hot_patch", "pid": 99999999, "patch_bytes": "909090", "address": "0x7FF710001000"}""";
        JsonElement noProcDoc = JsonElement.Parse(noProcJson);
        ToolCallResult result3 = await handler.HandleAsync(noProcDoc);
        Assert.True(result3.IsError);
        Assert.Contains("PID 99999999", ((TextContentItem)result3.Content[0]).Text);
    }

    [Fact]
    public void NativeProcessInjectionService_ApplyMemoryPatch_RejectsInvalidProcessHandleOrEmptyBytes()
    {
        // 1. Invalid process handle (nint.Zero)
        bool result1 = NativeProcessInjectionService.ApplyMemoryPatch(
            nint.Zero,
            unchecked((nint)0x7FF710000000L),
            [0x90, 0x90],
            out string? error1);

        Assert.False(result1);
        Assert.NotNull(error1);
        Assert.Contains("Invalid process handle", error1);

        // 2. Empty byte array with invalid handle
        bool result2 = NativeProcessInjectionService.ApplyMemoryPatch(
            nint.Zero,
            unchecked((nint)0x7FF710000000L),
            [],
            out string? error2);

        Assert.False(result2);
        Assert.NotNull(error2);
    }

    [Fact]
    public async Task NativeManageHandler_GuardStatus_ReturnsStagedDllMetrics()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;

        NativeManageHandler handler = new NativeManageHandler();
        string argsJson = """{"action": "guard_status"}""";
        JsonElement doc = JsonElement.Parse(argsJson);

        ToolCallResult result = await handler.HandleAsync(doc);

        Assert.False(result.IsError);
        string contentJson = ((TextContentItem)result.Content[0]).Text;
        JsonElement root = JsonElement.Parse(contentJson);

        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("guard_status", root.GetProperty("action").GetString());
        Assert.True(root.GetProperty("exists").GetBoolean());
        Assert.True(root.GetProperty("exportsCount").GetInt32() >= 8);
        Assert.Contains("AMD64", root.GetProperty("architecture").GetString());
    }

    [Fact]
    public async Task NativeManageHandler_GuardTelemetry_ReturnsRealProcessOrDetachedNoProcess()
    {
        NativeManageHandler handler = new NativeManageHandler();
        int currentPid = Environment.ProcessId;
        string argsJson = $$"""{"action": "guard_telemetry", "pid": {{currentPid}}}""";
        JsonElement doc = JsonElement.Parse(argsJson);

        ToolCallResult result = await handler.HandleAsync(doc);

        Assert.False(result.IsError);
        string contentJson = ((TextContentItem)result.Content[0]).Text;
        JsonElement root = JsonElement.Parse(contentJson);

        Assert.Equal("success", root.GetProperty("status").GetString());
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.True(root.GetProperty("processFound").GetBoolean());
        Assert.Equal("guard_telemetry", root.GetProperty("action").GetString());
        Assert.Equal("Active", root.GetProperty("supervisionStatus").GetString());
        Assert.True(root.GetProperty("hookedSubsystems").GetArrayLength() >= 4);

        JsonElement metrics = root.GetProperty("telemetryMetrics");
        Assert.True(metrics.GetProperty("liveHeapBytes").GetInt64() > 0);
        Assert.Equal(65535, metrics.GetProperty("socketBoundsMaxBytes").GetInt32());
        Assert.NotEmpty(metrics.GetProperty("fastBusTelemetryRing").GetString()!);

        // Verify non-running PID returns honest no_process status
        string missingJson = """{"action": "guard_telemetry", "pid": 99999999}""";
        ToolCallResult missingResult = await handler.HandleAsync(JsonElement.Parse(missingJson));
        JsonElement missingRoot = JsonElement.Parse(((TextContentItem)missingResult.Content[0]).Text);
        Assert.Equal("no_process", missingRoot.GetProperty("status").GetString());
        Assert.False(missingRoot.GetProperty("processFound").GetBoolean());
        Assert.Equal("Detached", missingRoot.GetProperty("supervisionStatus").GetString());
        Assert.False(missingRoot.TryGetProperty("telemetryMetrics", out JsonElement missingTelemetry) && missingTelemetry.ValueKind != JsonValueKind.Null);
    }
}
