// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.IO;
using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Moq;
using Xunit;

namespace Kongor.Mcp.Tests;

/// <summary>
/// Horizon 2.6 Zero-Gap Closure contract & regression tests (MCP-283, MCP-293, MCP-295, MCP-297, MCP-298).
/// </summary>
public sealed class ZeroGapClosureTests
{
    [Fact]
    public async Task ExecuteToolAsync_WhenHandlerReturnsSimulatedTestDoubleWithoutOptIn_FailsClosedWithSimulationNotAllowed()
    {
        // protects: McpToolRegistry.ExecuteToolAsync single authoritative ExecutionFidelity gate across stdio, sdk-stdio, http, and kongor_call_tool (MCP-283)
        // fails_when: A handler returns executionFidelity="SimulatedTestDouble" without allowSimulation=true and ExecuteToolAsync returns IsError=false
        // seam: McpToolRegistry.ExecuteToolAsync
        var registry = new McpToolRegistry(registerDefaults: false);
        registry.RegisterTool(
            new ToolDefinition
            {
                Name = "kongor_test_simulated_probe",
                Description = "Test probe returning SimulatedTestDouble fidelity",
                InputSchema = new ToolInputSchema
                {
                    Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
                    {
                        ["action"] = new() { Type = "string", Description = "Probe action" }
                    }
                }
            },
            (_, _) => Task.FromResult(ToolCallResult.Json(new
            {
                status = "success",
                ok = true,
                executionFidelity = ExecutionFidelity.SimulatedTestDouble.ToString(),
                sampleValue = 42
            })),
            McpToolCategory.ReadOnly);

        ToolCallResult blocked = await registry.ExecuteToolAsync(
            "kongor_test_simulated_probe",
            JsonElement.Parse("{}"),
            CancellationToken.None);

        blocked.IsError.Should().BeTrue("SimulatedTestDouble output must fail closed at McpToolRegistry.ExecuteToolAsync without allowSimulation: true");
        using JsonDocument errDoc = JsonDocument.Parse(blocked.FirstText);
        errDoc.RootElement.GetProperty("errorCode").GetString().Should().Be("simulation_not_allowed");
        errDoc.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();

        ToolCallResult permitted = await registry.ExecuteToolAsync(
            "kongor_test_simulated_probe",
            JsonElement.Parse("{\"allowSimulation\": true}"),
            CancellationToken.None);

        permitted.IsError.Should().BeFalse("Explicit allowSimulation: true must permit synthetic test doubles");
    }

    [Fact]
    public async Task ExecuteToolAsync_WhenNonSimulatedPayloadMentionsSimulatedTestDoubleInProse_DoesNotFalsePositive()
    {
        // protects: Parsed JSON executionFidelity check vs fragile substring matching (AGENTS.md no-string-matching rule)
        // fails_when: A StaticOfflineAnalysis or LiveVerified response containing the substring "SimulatedTestDouble" in documentation/logs is falsely rejected
        // seam: McpToolRegistry.ExecuteToolAsync + McpServer.ExecuteToolWithMiddlewareAsync
        var registry = new McpToolRegistry(registerDefaults: false);
        registry.RegisterTool(
            new ToolDefinition
            {
                Name = "kongor_test_doc_probe",
                Description = "Doc probe mentioning SimulatedTestDouble",
                InputSchema = new ToolInputSchema()
            },
            (_, _) => Task.FromResult(ToolCallResult.Json(new
            {
                status = "success",
                ok = true,
                executionFidelity = ExecutionFidelity.StaticOfflineAnalysis.ToString(),
                documentation = "Handlers must never return SimulatedTestDouble as live data."
            })),
            McpToolCategory.ReadOnly);

        ToolCallResult result = await registry.ExecuteToolAsync(
            "kongor_test_doc_probe",
            JsonElement.Parse("{}"),
            CancellationToken.None);

        result.IsError.Should().BeFalse("Prose mentioning SimulatedTestDouble inside a StaticOfflineAnalysis payload must not trigger simulation rejection");
    }

    [Theory]
    [InlineData("inspect_mounts")]
    [InlineData("resolve_conflict")]
    [InlineData("detect_shadowing")]
    public async Task VfsMountGraphHandler_WhenDirectoryMissingAndSimulationNotAllowed_ReturnsDirectoryNotFoundInsteadOfResources999(string action)
    {
        // protects: kongor_vfs_mount_graph honest missing-directory error handling (MCP-283)
        // fails_when: VfsMountGraphHandler fabricates mods/resources999.s2z or sha256:abc12345 when game_dir does not exist
        // seam: VfsMountGraphHandler.HandleAsync via McpToolRegistry.ExecuteToolAsync
        var registry = new McpToolRegistry(registerDefaults: true);
        string missingDir = Path.Combine(Path.GetTempPath(), "kongor_missing_vfs_" + Guid.NewGuid().ToString("N"));

        string jsonArgs = JsonSerializer.Serialize(new
        {
            action,
            game_dir = missingDir
        });

        ToolCallResult res = await registry.ExecuteToolAsync(
            "kongor_vfs_mount_graph",
            JsonElement.Parse(jsonArgs),
            CancellationToken.None);

        res.IsError.Should().BeTrue($"Action '{action}' on nonexistent directory must fail closed");
        res.FirstText.Should().NotContain("resources999.s2z");
        res.FirstText.Should().NotContain("abc12345");
        using JsonDocument doc = JsonDocument.Parse(res.FirstText);
        doc.RootElement.GetProperty("errorCode").GetString().Should().Be("directory_not_found");
    }

    [Fact]
    public async Task VfsMountGraphHandler_WhenValidClientDirectoryExists_PerformsStaticOfflineScanWithoutLiveScanFlag()
    {
        // protects: kongor_vfs_mount_graph real filesystem scanning by default even in test assemblies (MCP-283)
        // fails_when: ShouldUseLiveVfsScan short-circuits on IsTestExecution and returns SimulatedTestDouble for a real directory
        // seam: VfsMountGraphHandler.HandleAsync
        string tempClient = Path.Combine(Path.GetTempPath(), "kongor_vfs_real_" + Guid.NewGuid().ToString("N"));
        string overrideUiDir = Path.Combine(tempClient, "override", "ui");
        string cpfUiDir = Path.Combine(tempClient, "ClientPackageFiles", "ui");
        Directory.CreateDirectory(overrideUiDir);
        Directory.CreateDirectory(cpfUiDir);
        await File.WriteAllTextAsync(Path.Combine(overrideUiDir, "main.interface"), "<interface name=\"main\"/>");
        await File.WriteAllTextAsync(Path.Combine(cpfUiDir, "main.interface"), "<interface name=\"main_old\"/>");

        try
        {
            var registry = new McpToolRegistry(registerDefaults: true);
            string jsonArgs = JsonSerializer.Serialize(new
            {
                action = "detect_shadowing",
                game_dir = tempClient
            });

            ToolCallResult res = await registry.ExecuteToolAsync(
                "kongor_vfs_mount_graph",
                JsonElement.Parse(jsonArgs),
                CancellationToken.None);

            res.IsError.Should().BeFalse();
            res.FirstText.Should().NotContain("resources999.s2z");
            using JsonDocument doc = JsonDocument.Parse(res.FirstText);
            doc.RootElement.GetProperty("executionFidelity").GetString().Should().Be(ExecutionFidelity.StaticOfflineAnalysis.ToString());
            doc.RootElement.GetProperty("shadowedFilesCount").GetInt32().Should().Be(1);
        }
        finally
        {
            if (Directory.Exists(tempClient))
            {
                Directory.Delete(tempClient, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("kongor_transient_capture", "{}")]
    [InlineData("kongor_capture_window", "{\"capture_method\":\"transient\"}")]
    public async Task TransientCapture_WithoutAllowWindowExposure_WhenZeroExposureUnavailable_FailsClosedWithWindowExposureBlocked(
        string toolName,
        string rawArgs)
    {
        // protects: User desktop focus & window position when an offscreen capture cannot be satisfied via zero-exposure shared memory (MCP-298)
        // fails_when: kongor_transient_capture or capture_method="transient" raises an offscreen window onto (0,0) without allowWindowExposure: true / steal_focus: true
        // seam: CaptureWindowHandler.HandleAsync + HandleTransientAsync
        var mockCapturer = new Moq.Mock<Kongor.Cli.Mcp.Services.IWindowCapturer>();
        mockCapturer
            .Setup(c => c.CaptureAsync(
                Moq.It.IsAny<string>(),
                "vulkan_offscreen",
                Moq.It.IsAny<bool>(),
                Moq.It.IsAny<string?>(),
                Moq.It.IsAny<bool>(),
                Moq.It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Kongor.Cli.Mcp.Services.WindowCaptureResult
            {
                Success = false,
                Target = "hon_x64",
                ErrorMessage = "Vulkan shared memory frame unavailable."
            });

        var handler = new CaptureWindowHandler(mockCapturer.Object);
        ToolCallResult res = toolName == "kongor_transient_capture"
            ? await handler.HandleTransientAsync(JsonElement.Parse(rawArgs), CancellationToken.None)
            : await handler.HandleAsync(JsonElement.Parse(rawArgs), CancellationToken.None);

        res.IsError.Should().BeTrue($"{toolName} without allowWindowExposure must fail closed when zero-exposure Vulkan capture is unavailable");
        using JsonDocument doc = JsonDocument.Parse(res.FirstText);
        doc.RootElement.GetProperty("errorCode").GetString().Should().Be("window_exposure_blocked");

        mockCapturer.Verify(
            c => c.CaptureTransientAsync(
                Moq.It.IsAny<string>(),
                Moq.It.IsAny<int>(),
                Moq.It.IsAny<int>(),
                Moq.It.IsAny<int>(),
                Moq.It.IsAny<int>(),
                Moq.It.IsAny<int>(),
                Moq.It.IsAny<bool>(),
                Moq.It.IsAny<string?>(),
                Moq.It.IsAny<CancellationToken>()),
            Moq.Times.Never);
    }

    [Fact]
    public void McpCodebase_NeverSwallowsProcessKillOrFileDeleteExceptionsWithEmptyCatch()
    {
        // protects: AGENTS.md Slop-Scan invariant ("No Empty Catches on Process Kills" & "No Empty Catches on File Operations") (MCP-297)
        // fails_when: Any .cs file under src/tools/kongor-cli/Mcp/ wraps .Kill(, File.Delete(, or Directory.Delete( in an empty catch { } block
        // seam: Static source ratchet across src/tools/kongor-cli/Mcp/**/*.cs
        DirectoryInfo? cur = new(AppContext.BaseDirectory);
        while (cur != null && !File.Exists(Path.Combine(cur.FullName, "ProjectKongor.slnx")))
        {
            cur = cur.Parent;
        }
        cur.Should().NotBeNull();

        string mcpRoot = Path.Combine(cur!.FullName, "src", "tools", "kongor-cli", "Mcp");
        var violations = new List<string>();
        var inlinePattern = new System.Text.RegularExpressions.Regex(
            @"(\.Kill\s*\(|File\.Delete\s*\(|Directory\.Delete\s*\()[^\r\n]*catch\s*\{\s*\}",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        foreach (string csFile in Directory.EnumerateFiles(mcpRoot, "*.cs", SearchOption.AllDirectories))
        {
            string[] lines = File.ReadAllLines(csFile);
            for (int i = 0; i < lines.Length; i++)
            {
                if (inlinePattern.IsMatch(lines[i]))
                {
                    violations.Add($"{Path.GetRelativePath(cur.FullName, csFile)}:{i + 1}: {lines[i].Trim()}");
                    continue;
                }

                if (lines[i].Contains(".Kill(", StringComparison.Ordinal) ||
                    lines[i].Contains("File.Delete(", StringComparison.Ordinal) ||
                    lines[i].Contains("Directory.Delete(", StringComparison.Ordinal))
                {
                    int lookaheadEnd = Math.Min(lines.Length - 1, i + 8);
                    for (int j = i + 1; j <= lookaheadEnd; j++)
                    {
                        if (System.Text.RegularExpressions.Regex.IsMatch(lines[j], @"^\s*catch\s*\{\s*\}\s*$"))
                        {
                            violations.Add($"{Path.GetRelativePath(cur.FullName, csFile)}:{i + 1}->catch@{j + 1}: {lines[i].Trim()}");
                            break;
                        }
                    }
                }
            }
        }

        violations.Should().BeEmpty("Process.Kill and File/Directory.Delete operations must log or surface errors instead of swallowing them in empty catch blocks");
    }

    [Fact]
    public void ClientProcessRegistry_WhenPidIsRecycledWithDifferentStartTime_RejectsOwnership()
    {
        // protects: ClientProcessRegistry PID-recycling collision defense (MCP-297)
        // fails_when: A new user hon_x64 process reuses an exited MCP client's OS PID with a different start time and IsManagedClient returns true
        // seam: ClientProcessRegistry.IsManagedClient + OsProcessStartTimeProbe
        var originalProbe = Kongor.Cli.Mcp.Services.ClientProcessRegistry.OsProcessStartTimeProbe;
        try
        {
            long initialTicks = new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero).Ticks;
            long recycledTicks = initialTicks + TimeSpan.FromMinutes(15).Ticks;
            long currentOsTicks = initialTicks;

            Kongor.Cli.Mcp.Services.ClientProcessRegistry.OsProcessStartTimeProbe = _ => currentOsTicks;
            var registry = new Kongor.Cli.Mcp.Services.ClientProcessRegistry(_ => { });
            registry.RegisterClient(new Kongor.Cli.Mcp.Services.ClientInstance
            {
                ProcessId = 42424,
                ProcessStartTimeTicks = initialTicks,
                LaunchedAt = TimeProvider.System.GetUtcNow()
            });

            registry.IsManagedClient(42424).Should().BeTrue("Matching ProcessStartTimeTicks must be recognized as MCP-owned");

            currentOsTicks = recycledTicks;
            registry.IsManagedClient(42424).Should().BeFalse("Recycled OS PID with mismatched StartTimeUtc must be rejected and evicted");
        }
        finally
        {
            Kongor.Cli.Mcp.Services.ClientProcessRegistry.OsProcessStartTimeProbe = originalProbe;
        }
    }

    [Fact]
    public async Task MatchLaunchHandler_WhenForeignClientIsRunning_BlocksHijackOnLaunch_PreservesWindowOnAdopt_AndReportsTruthOnStatus()
    {
        // protects: User's personal hon_x64 game window & session from silent MCP hijacking and offscreen repositioning (MCP-297)
        // fails_when: MatchLaunchHandler adopts an un-owned hon_x64 without adoptExisting: true, moves its window offscreen without repositionExisting: true, or reports running: true on status
        // seam: MatchLaunchHandler.HandleAsync (launch + status)
        var registry = new Kongor.Cli.Mcp.Services.ClientProcessRegistry(_ => { });
        var ipcMock = new Mock<Kongor.Cli.Mcp.Services.IGameIpcBridge>();
        ipcMock.SetupGet(b => b.IsConnected).Returns(true);
        ipcMock
            .Setup(b => b.ExecuteRpcAsync<object>(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new object());

        int repositionCalls = 0;
        var foreignCandidate = new MatchLaunchHandler.ClientProcessCandidate(
            ProcessId: 88801,
            MainWindowHandle: new IntPtr(0x00AA1122),
            CommandLine: "hon_x64.exe",
            KillTree: () => { },
            WaitForExit: _ => true,
            IsAlive: () => true);

        var handler = new MatchLaunchHandler(
            ipcBridge: ipcMock.Object,
            clientManageHandler: null,
            uiControlService: null,
            timeProvider: TimeProvider.System,
            logger: _ => { },
            clientRegistry: registry,
            candidateEnumerator: () => [foreignCandidate],
            windowOffscreenRepositioner: _ => repositionCalls++);

        // 1. Read-only status reports running: false, foreignClientRunning: true
        ToolCallResult statusRes = await handler.HandleAsync(JsonElement.Parse("{\"action\":\"status\"}"), CancellationToken.None);
        statusRes.IsError.Should().BeFalse();
        using (JsonDocument statusDoc = JsonDocument.Parse(statusRes.FirstText))
        {
            statusDoc.RootElement.GetProperty("running").GetBoolean().Should().BeFalse();
            statusDoc.RootElement.GetProperty("foreignClientRunning").GetBoolean().Should().BeTrue();
            statusDoc.RootElement.GetProperty("foreignProcessId").GetInt32().Should().Be(88801);
        }

        // 2. Default launch fails closed with foreign_client_running and never moves the window
        ToolCallResult blockedLaunch = await handler.HandleAsync(
            JsonElement.Parse("{\"action\":\"launch\",\"headless\":true}"),
            CancellationToken.None);
        blockedLaunch.IsError.Should().BeTrue();
        repositionCalls.Should().Be(0);
        using (JsonDocument blockedDoc = JsonDocument.Parse(blockedLaunch.FirstText))
        {
            blockedDoc.RootElement.GetProperty("errorCode").GetString().Should().Be("foreign_client_running");
            blockedDoc.RootElement.GetProperty("foreignProcessId").GetInt32().Should().Be(88801);
        }

        // 3. Explicit adoptExisting: true dispatches into foreign client WITHOUT moving its window offscreen when repositionExisting is false
        ToolCallResult adoptedLaunch = await handler.HandleAsync(
            JsonElement.Parse("{\"action\":\"launch\",\"headless\":true,\"adoptExisting\":true}"),
            CancellationToken.None);
        adoptedLaunch.IsError.Should().BeFalse();
        repositionCalls.Should().Be(0, "Adopted user client window must never be moved offscreen unless repositionExisting: true is also set");
    }

    [Fact]
    public async Task MatchLaunchHandler_StopMatch_WhenProcessSurvivesOrKillThrows_ReturnsProcessTerminationFailed()
    {
        // protects: Stop confirmation truth guarantee & non-swallowed process kill errors (MCP-297)
        // fails_when: StopMatch swallows Kill(true) exceptions or reports running: false / status: success when a targeted PID survives
        // seam: MatchLaunchHandler.HandleAsync (action = "stop")
        var registry = new Kongor.Cli.Mcp.Services.ClientProcessRegistry(_ => { });
        var stubbornCandidate = new MatchLaunchHandler.ClientProcessCandidate(
            ProcessId: 88802,
            MainWindowHandle: IntPtr.Zero,
            CommandLine: "hon_x64.exe",
            KillTree: () => throw new System.ComponentModel.Win32Exception(5, "Access is denied"),
            WaitForExit: _ => false,
            IsAlive: () => true);

        var handler = new MatchLaunchHandler(
            ipcBridge: null,
            clientManageHandler: null,
            uiControlService: null,
            timeProvider: TimeProvider.System,
            logger: _ => { },
            clientRegistry: registry,
            candidateEnumerator: () => [stubbornCandidate],
            windowOffscreenRepositioner: null);

        ToolCallResult stopRes = await handler.HandleAsync(
            JsonElement.Parse("{\"action\":\"stop\",\"all\":true}"),
            CancellationToken.None);

        stopRes.IsError.Should().BeTrue("Surviving process or Win32Exception on Kill must return an error result");
        using JsonDocument doc = JsonDocument.Parse(stopRes.FirstText);
        doc.RootElement.GetProperty("errorCode").GetString().Should().Be("process_termination_failed");
        doc.RootElement.GetProperty("running").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("survivingProcessIds")[0].GetInt32().Should().Be(88802);
        doc.RootElement.GetProperty("killErrors").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ExecuteToolAsync_WhenUnknownArgumentIsSupplied_FailsClosedWithUnknownArgumentAndDidYouMean()
    {
        // protects: Schema-driven parameter validation so typos never silently vanish and run unintended default behavior (MCP-293)
        // fails_when: NormalizeAndValidateToolArguments ignores unrecognized keys instead of returning errorCode="unknown_argument" + didYouMean
        // seam: McpToolRegistry.ExecuteToolAsync -> NormalizeAndValidateToolArguments
        var registry = new McpToolRegistry(registerDefaults: false);
        registry.RegisterTool(
            new ToolDefinition
            {
                Name = "kongor_test_widget_probe",
                Description = "Widget probe",
                InputSchema = new ToolInputSchema
                {
                    Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
                    {
                        ["action"] = new() { Type = "string", Description = "Action" },
                        ["widget"] = new() { Type = "string", Description = "Widget name" }
                    }
                }
            },
            (_, _) => Task.FromResult(ToolCallResult.Json(new { status = "success", ok = true })),
            McpToolCategory.ReadOnly);

        ToolCallResult res = await registry.ExecuteToolAsync(
            "kongor_test_widget_probe",
            JsonElement.Parse("{\"action\":\"inspect\",\"widgt\":\"main_top_bar\"}"),
            CancellationToken.None);

        res.IsError.Should().BeTrue("Unknown argument 'widgt' must fail closed");
        using JsonDocument doc = JsonDocument.Parse(res.FirstText);
        doc.RootElement.GetProperty("errorCode").GetString().Should().Be("unknown_argument");
        doc.RootElement.GetProperty("unknownArgument").GetString().Should().Be("widgt");
        doc.RootElement.GetProperty("didYouMean").GetString().Should().Be("widget");
        doc.RootElement.GetProperty("validParameters").EnumerateArray().Select(e => e.GetString()).Should().Contain(["action", "widget"]);
    }

    [Fact]
    public async Task DefaultResponseCap_EnforcedByDefault_PreservesNestedObjects_OmitsBase64_AndCapsErrorPayloads()
    {
        // protects: 8 KB default response budget in both production and test runs without dropping nested objects or corrupting base64 strings (MCP-295)
        // fails_when: IsTestExecution bypasses the cap, nested objects outside 4 hardcoded names vanish, base64Png is sliced to a 600-char prefix, or large error payloads bypass capping
        // seam: McpToolRegistry.ExecuteToolAsync -> ApplyDefaultResponseCapIfNeeded
        string tempPayloadDir = Path.Combine(Path.GetTempPath(), "kongor_cap_test_" + Guid.NewGuid().ToString("N"));
        string? prevPayloadDir = Environment.GetEnvironmentVariable("KONGOR_MCP_PAYLOAD_DIR");
        Environment.SetEnvironmentVariable("KONGOR_MCP_PAYLOAD_DIR", tempPayloadDir);
        try
        {
            var registry = new McpToolRegistry(registerDefaults: false);
            string fakeBase64 = new('A', 6000);
            string[] bigArray = Enumerable.Range(0, 60).Select(i => $"entry_{i:D3}_{new string('x', 80)}").ToArray();

            registry.RegisterTool(
                new ToolDefinition
                {
                    Name = "kongor_test_large_payload",
                    Description = "Large payload probe",
                    InputSchema = new ToolInputSchema
                    {
                        Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
                        {
                            ["failMode"] = new() { Type = "boolean", Description = "Emit large error payload" }
                        }
                    }
                },
                (args, _) =>
                {
                    bool isErr = args.ValueKind == JsonValueKind.Object &&
                                 args.TryGetProperty("failMode", out var fm) &&
                                 fm.ValueKind == JsonValueKind.True;
                    return Task.FromResult(ToolCallResult.Json(new
                    {
                        status = isErr ? "error" : "success",
                        ok = !isErr,
                        errorCode = isErr ? "synthetic_large_failure" : (string?)null,
                        error = isErr ? "Synthetic failure with large diagnostic dump" : (string?)null,
                        message = isErr ? "Synthetic failure with large diagnostic dump" : "OK",
                        base64Png = fakeBase64,
                        items = bigArray,
                        customNestedObject = new
                        {
                            bounds = new { x = 10, y = 20, width = 300, height = 120 },
                            tag = "preserved_nested_key"
                        },
                        remediation_call = new
                        {
                            tool = "kongor_health_check",
                            arguments = new { action = "status" }
                        }
                    }, isError: isErr));
                },
                McpToolCategory.ReadOnly);

            // 1. Default call (NO enforceResponseCap flag) must be capped <= 8 KB
            ToolCallResult capped = await registry.ExecuteToolAsync("kongor_test_large_payload", JsonElement.Parse("{}"), CancellationToken.None);
            capped.IsError.Should().BeFalse();
            System.Text.Encoding.UTF8.GetByteCount(capped.FirstText).Should().BeLessThanOrEqualTo(8192);
            using (JsonDocument doc = JsonDocument.Parse(capped.FirstText))
            {
                doc.RootElement.GetProperty("truncated").GetBoolean().Should().BeTrue();
                string savedPath = doc.RootElement.GetProperty("fullOutputPath").GetString()!;
                savedPath.Should().StartWith(tempPayloadDir);
                File.Exists(savedPath).Should().BeTrue();

                // Base64 must be cleanly omitted, not truncated to 600 corrupt base64 chars
                doc.RootElement.GetProperty("base64Png").GetString().Should().StartWith("<omitted ");

                // Arbitrary nested objects (customNestedObject, remediation_call) must be preserved
                doc.RootElement.GetProperty("customNestedObject").GetProperty("tag").GetString().Should().Be("preserved_nested_key");
                doc.RootElement.GetProperty("remediation_call").GetProperty("tool").GetString().Should().Be("kongor_health_check");
            }

            // 2. Large error payload is also capped while preserving errorCode and remediation_call
            ToolCallResult cappedErr = await registry.ExecuteToolAsync("kongor_test_large_payload", JsonElement.Parse("{\"failMode\":true}"), CancellationToken.None);
            cappedErr.IsError.Should().BeTrue();
            System.Text.Encoding.UTF8.GetByteCount(cappedErr.FirstText).Should().BeLessThanOrEqualTo(8192);
            using (JsonDocument errDoc = JsonDocument.Parse(cappedErr.FirstText))
            {
                errDoc.RootElement.GetProperty("truncated").GetBoolean().Should().BeTrue();
                errDoc.RootElement.GetProperty("errorCode").GetString().Should().Be("synthetic_large_failure");
                errDoc.RootElement.GetProperty("remediation_call").GetProperty("tool").GetString().Should().Be("kongor_health_check");
            }

            // 3. detail: "full" bypasses truncation
            ToolCallResult full = await registry.ExecuteToolAsync("kongor_test_large_payload", JsonElement.Parse("{\"detail\":\"full\"}"), CancellationToken.None);
            using (JsonDocument fullDoc = JsonDocument.Parse(full.FirstText))
            {
                fullDoc.RootElement.TryGetProperty("truncated", out _).Should().BeFalse();
                fullDoc.RootElement.GetProperty("items").GetArrayLength().Should().Be(60);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("KONGOR_MCP_PAYLOAD_DIR", prevPayloadDir);
            if (Directory.Exists(tempPayloadDir)) Directory.Delete(tempPayloadDir, recursive: true);
        }
    }

    [Fact]
    public async Task McpServer_StructuredContent_EmittedOnlyWhenProtocol20250618OrNewerIsNegotiated()
    {
        // protects: Protocol-negotiated structuredContent emission so 2024-11-05 clients stay spec-pure without double token cost
        // fails_when: McpServer emits structuredContent under 2024-11-05 or fails to emit structuredContent under 2025-06-18
        // seam: McpServer.HandleMessageAsync (initialize + tools/call)
        static McpToolRegistry BuildTestRegistry()
        {
            var reg = new McpToolRegistry(registerDefaults: false);
            reg.RegisterTool(
                new ToolDefinition
                {
                    Name = "kongor_test_echo",
                    Description = "Echo JSON",
                    InputSchema = new ToolInputSchema()
                },
                (_, _) => Task.FromResult(ToolCallResult.Json(new { status = "success", ok = true, answer = 7 })),
                McpToolCategory.ReadOnly);
            return reg;
        }

        // 1. Negotiate 2024-11-05 -> structuredContent must be omitted
        using (var serverLegacy = new McpServer(toolRegistry: BuildTestRegistry()))
        {
            await serverLegacy.ProcessMessageAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\"}}");
            string? legacyCallResp = await serverLegacy.ProcessMessageAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"kongor_test_echo\",\"arguments\":{}}}");
            legacyCallResp.Should().NotBeNull();
            using JsonDocument legacyDoc = JsonDocument.Parse(legacyCallResp!);
            legacyDoc.RootElement.GetProperty("result").TryGetProperty("structuredContent", out _).Should().BeFalse();
        }

        // 2. Negotiate 2025-06-18 -> structuredContent must be populated
        using (var serverModern = new McpServer(toolRegistry: BuildTestRegistry()))
        {
            await serverModern.ProcessMessageAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\"}}");
            string? modernCallResp = await serverModern.ProcessMessageAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"kongor_test_echo\",\"arguments\":{}}}");
            modernCallResp.Should().NotBeNull();
            using JsonDocument modernDoc = JsonDocument.Parse(modernCallResp!);
            JsonElement sc = modernDoc.RootElement.GetProperty("result").GetProperty("structuredContent");
            sc.GetProperty("status").GetString().Should().Be("success");
            sc.GetProperty("answer").GetInt32().Should().Be(7);
        }
    }
}
