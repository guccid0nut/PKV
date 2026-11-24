// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Kongor.Cli.Commands;
using Kongor.Cli.Mcp.Services;
using Xunit;

namespace Kongor.Mcp.Tests;

[Collection("ConsoleTests")]
public sealed class TaoPhase8aCliCommandsTests : IDisposable
{
    private readonly string _testTempDir;

    public TaoPhase8aCliCommandsTests()
    {
        _testTempDir = Path.Combine(Path.GetTempPath(), $"kongor_phase8a_tests_{Guid.NewGuid():N}");
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
        catch { }
    }

    [Fact]
    [Trait("Category", "Manual")]
    public async Task DoctorCommand_ExecutesFullProbeSweep_RendersDiagnosticTable()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        var command = new DoctorCommand();
        var settings = new DoctorCommandSettings
        {
            Subsystem = "Infrastructure"
        };

        int exitCode = await command.ExecuteAsync(null!, settings);
        Assert.True(exitCode == 0 || exitCode == 1);
    }

    [Fact]
    [Trait("Category", "Manual")]
    public async Task DoctorCommand_WhenJsonRequested_EmitsValidTruthEnvelope()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);

        var command = new DoctorCommand(outputWriter: writer);
        var settings = new DoctorCommandSettings
        {
            Subsystem = "Infrastructure",
            Json = true
        };

        int exitCode = await command.ExecuteAsync(null!, settings);
        string jsonOutput = ExtractJsonEnvelope(sb.ToString());

        Assert.NotEmpty(jsonOutput);
        using var doc = JsonDocument.Parse(jsonOutput);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("status", out var statusProp));
        Assert.Equal("success", statusProp.GetString());
        Assert.True(root.TryGetProperty("health", out _));
        Assert.True(root.TryGetProperty("probes", out var probesProp));
        Assert.True(probesProp.GetArrayLength() > 0);
    }

    [Fact]
    public async Task HealCommand_TargetedFingerprint_ExecutesReflexRecipeDirectly()
    {
        using var store = new DeveloperIssueStore(logDirectory: _testTempDir);
        var orchestrator = new SelfHealOrchestratorService(issueStore: store);

        // Record a synthetic crash issue
        string crashError = "[ERROR] Access violation reading 0x00007FF63B81A120 at k2_x64.dll+0x3A20";
        var summary = await orchestrator.IngestTextAsync(crashError, "unit_test");
        Assert.True(summary.TotalCaught >= 1);

        var allIssues = store.GetAllIssues();
        Assert.NotEmpty(allIssues);
        string targetFp = allIssues[0].Fingerprint;

        string capturedOutput = string.Empty;
        var command = new HealCommand(
            orchestrator: orchestrator,
            issueStore: store,
            outputWriter: msg => capturedOutput = msg);

        var settings = new HealCommandSettings
        {
            Fingerprint = targetFp,
            PrSummary = true
        };

        int exitCode = await command.ExecuteAsync(null!, settings);
        Assert.Equal(0, exitCode);

        // Verify targeted healing resolved the issue
        var resolvedIssue = store.GetIssue(targetFp);
        Assert.NotNull(resolvedIssue);
        Assert.Equal(IssueResolutionStatus.AutoResolved, resolvedIssue.Status);
        Assert.Equal("CrashDetourRecipe", resolvedIssue.ResolutionRecipeApplied);

        // Verify clean PR summary output
        Assert.Contains("### 🛡️ ProjectKongor Targeted Reflex Healing", capturedOutput);
        Assert.Contains(targetFp, capturedOutput);
        Assert.Contains("CrashDetourRecipe", capturedOutput);
        Assert.DoesNotContain('\u001b', capturedOutput);
    }

    [Fact]
    public async Task HealCommand_PrSummary_EmitsCleanMarkdownWithoutAnsiEscapes()
    {
        using var store = new DeveloperIssueStore(logDirectory: _testTempDir);
        var orchestrator = new SelfHealOrchestratorService(issueStore: store);

        string capturedMarkdown = string.Empty;
        var command = new HealCommand(
            orchestrator: orchestrator,
            issueStore: store,
            outputWriter: msg => capturedMarkdown = msg);

        var settings = new HealCommandSettings
        {
            TargetDirectory = _testTempDir,
            Journey = true,
            DryRun = true,
            PrSummary = true
        };

        int exitCode = await command.ExecuteAsync(null!, settings);
        Assert.Equal(0, exitCode);

        Assert.NotEmpty(capturedMarkdown);
        Assert.Contains("### 🛡️ ProjectKongor Autonomous Self-Healing Journey Summary", capturedMarkdown);
        Assert.Contains("#### Subsystem Remediation & Netcode Health", capturedMarkdown);
        Assert.Contains("| Lua Script AST |", capturedMarkdown);
        Assert.Contains("| K2 UI XML Documents |", capturedMarkdown);
        Assert.Contains("[!NOTE]", capturedMarkdown);
        Assert.Contains("DRY RUN", capturedMarkdown);
        Assert.DoesNotContain('\u001b', capturedMarkdown);
    }

    [Fact]
    public async Task HealCommand_JourneySweep_IngestsErrorsAndAutoResolvesMatchingRecipes()
    {
        using var store = new DeveloperIssueStore(logDirectory: _testTempDir);
        var orchestrator = new SelfHealOrchestratorService(issueStore: store);

        // Seed store with multiple known issues
        string missingTexture = "[ERROR] Bitmap_Load: File not found: textures/heroes/pyro/fire.tga";
        string audioClipping = "[ERROR] Audio clipping: Integrated loudness -11.2 LUFS exceeds ceiling of -14.0 LUFS";
        await orchestrator.IngestTextAsync(missingTexture, "test_log");
        await orchestrator.IngestTextAsync(audioClipping, "test_log");

        var preSummary = store.GetJourneySummary();
        Assert.Equal(2, preSummary.TotalCaught);

        var command = new HealCommand(
            orchestrator: orchestrator,
            issueStore: store);

        var settings = new HealCommandSettings
        {
            TargetDirectory = _testTempDir,
            Journey = true,
            DryRun = true
        };

        int exitCode = await command.ExecuteAsync(null!, settings);
        Assert.Equal(0, exitCode);

        var postSummary = store.GetJourneySummary();
        Assert.Equal(2, postSummary.AutoResolvedCount);
        Assert.Equal(0, postSummary.ActionRequiredCount);
    }

    [Fact]
    public async Task HealCommand_WithSubsystemFilter_FiltersProcessingScope()
    {
        using var store = new DeveloperIssueStore(logDirectory: _testTempDir);
        var orchestrator = new SelfHealOrchestratorService(issueStore: store);

        // Pre-populate with Client and Dedicated Server issues
        var clientIssue = new DeveloperIssueRecord
        {
            Fingerprint = "fp_client_1",
            Subsystem = IssueSubsystem.GameClient,
            Severity = IssueSeverity.Error,
            Title = "Bitmap_Load: File not found: ui/minimap.dds",
            RawMessage = "Bitmap_Load: File not found: ui/minimap.dds"
        };
        var serverIssue = new DeveloperIssueRecord
        {
            Fingerprint = "fp_server_1",
            Subsystem = IssueSubsystem.DedicatedServer,
            Severity = IssueSeverity.Error,
            Title = "Bind failed: Port already in use",
            RawMessage = "Bind failed: Port already in use"
        };

        store.UpsertIssue(clientIssue);
        store.UpsertIssue(serverIssue);

        string capturedMarkdown = string.Empty;
        var command = new HealCommand(
            orchestrator: orchestrator,
            issueStore: store,
            outputWriter: msg => capturedMarkdown = msg);

        var settings = new HealCommandSettings
        {
            TargetDirectory = _testTempDir,
            Journey = true,
            DryRun = true,
            PrSummary = true,
            Subsystem = "GameClient"
        };

        int exitCode = await command.ExecuteAsync(null!, settings);
        Assert.Equal(0, exitCode);

        // The summary report should only list GameClient issues
        Assert.Contains("GameClient", capturedMarkdown);
        Assert.DoesNotContain("DedicatedServer", capturedMarkdown);
    }

    [Fact]
    [Trait("Category", "Manual")]
    public async Task DoctorCommand_ProbesNativeModernizations_EvaluatesGuardAndTls13()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);

        var command = new DoctorCommand(outputWriter: writer);
        var settings = new DoctorCommandSettings
        {
            Subsystem = "Native",
            Json = true
        };

        int exitCode = await command.ExecuteAsync(null!, settings);
        string jsonOutput = ExtractJsonEnvelope(sb.ToString());

        Assert.NotEmpty(jsonOutput);
        using var doc = JsonDocument.Parse(jsonOutput);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("probes", out var probesProp));

        bool foundNativeGuard = false;
        bool foundTls13 = false;

        foreach (var probe in probesProp.EnumerateArray())
        {
            string name = probe.GetProperty("name").GetString() ?? string.Empty;
            string endpoint = probe.GetProperty("endpoint").GetString() ?? string.Empty;

            if (name == "Native Guard Supervision")
            {
                foundNativeGuard = true;
                Assert.True(
                    endpoint is "version.dll" or "Kongor.NativeGuard.dll",
                    $"Expected 'version.dll' or 'Kongor.NativeGuard.dll' endpoint, got '{endpoint}'.");
                Assert.True(probe.TryGetProperty("active", out var activeProp));
                Assert.True(activeProp.ValueKind == JsonValueKind.True || activeProp.ValueKind == JsonValueKind.False);
            }
            else if (name == "Native TLS 1.3 & Schannel")
            {
                foundTls13 = true;
                Assert.Equal("libcurl_x64.dll", endpoint);
                Assert.True(probe.TryGetProperty("active", out var activeProp));
                Assert.True(activeProp.ValueKind == JsonValueKind.True || activeProp.ValueKind == JsonValueKind.False);
            }
        }

        Assert.True(foundNativeGuard, "Doctor probe 'Native Guard Supervision' must be present in diagnostic output.");
        Assert.True(foundTls13, "Doctor probe 'Native TLS 1.3 & Schannel' must be present in diagnostic output.");
    }

    private static string ExtractJsonEnvelope(string output)
    {
        int startIdx = output.IndexOf('{');
        while (startIdx >= 0)
        {
            int braceDepth = 0;
            for (int i = startIdx; i < output.Length; i++)
            {
                if (output[i] == '{') braceDepth++;
                else if (output[i] == '}')
                {
                    braceDepth--;
                    if (braceDepth == 0)
                    {
                        string candidate = output.Substring(startIdx, i - startIdx + 1);
                        try
                        {
                            using var doc = JsonDocument.Parse(candidate);
                            if (doc.RootElement.TryGetProperty("probes", out _))
                            {
                                return candidate;
                            }
                        }
                        catch { }
                        break;
                    }
                }
            }
            startIdx = output.IndexOf('{', startIdx + 1);
        }
        return output;
    }

    [Fact]
    public async Task DoctorCommand_WhenHygieneRequested_EvaluatesRepositoryRootCleanliness()
    {
        DoctorCommand command = new();
        DoctorCommandSettings settings = new()
        {
            Subsystem = "Hygiene"
        };

        int exitCode = await command.ExecuteAsync(null!, settings);
        Assert.Equal(0, exitCode);
    }
}

