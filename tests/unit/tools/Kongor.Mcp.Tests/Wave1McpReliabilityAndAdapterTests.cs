// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.IO.Pipelines;
using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Logging;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Moq;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class Wave1McpReliabilityAndAdapterTests
{
    [Fact]
    public async Task ClientEvalLuaHandler_PreSendLuacSyntaxGate_RejectsInvalidLuaBeforeTouchingIpc()
    {
        // protects: K2 engine main-thread stability and MCP 1st-call remediation by rejecting invalid Lua 5.1 syntax before IPC.
        // fails_when: Invalid Lua (e.g. '!=' or unclosed 'if') connects to Named Pipe / Mailbox IPC instead of returning luaSyntaxError + line + hint.
        // seam: Real ClientEvalLuaHandler + real luac51 binary + strict IGameIpcBridge mock verifying zero ConnectAsync/ExecuteLuaAsync calls.

        Mock<IGameIpcBridge> bridgeMock = new(MockBehavior.Strict);
        ClientEvalLuaHandler handler = new(ipcBridge: bridgeMock.Object);

        using JsonDocument argsDoc = JsonDocument.Parse("""{"action":"eval","code":"if x != 5 then\n  Echo('bad')\nend"}""");
        ToolCallResult res = await handler.HandleAsync(argsDoc.RootElement);

        res.IsError.Should().BeTrue();
        using JsonDocument outDoc = JsonDocument.Parse(res.FirstText);
        JsonElement root = outDoc.RootElement;
        root.GetProperty("errorCode").GetString().Should().Be("luaSyntaxError");
        root.GetProperty("executed").GetBoolean().Should().BeFalse();
        root.GetProperty("syntaxLine").GetInt32().Should().Be(1);
        root.GetProperty("remediationHint").GetString().Should().Contain("~=");
    }

    [Fact]
    public async Task ClientEvalLuaHandler_RetriesOnceWithJitterWhenEngineReportsBusy()
    {
        // protects: Live hot-reload and Lua eval resilience during transient K2 frame spikes (engine_busy).
        // fails_when: First transient engine_busy fails immediately without bounded jitter backoff + readiness retry.
        // seam: Real ClientEvalLuaHandler + mock IGameIpcBridge transitioning from engine_busy -> [LUA_OK] 42.

        Mock<IGameIpcBridge> bridgeMock = new();
        bridgeMock.SetupGet(b => b.IsConnected).Returns(true);
        bridgeMock.Setup(b => b.EvalLuaDuplexAsync(It.IsAny<string>(), It.IsAny<uint>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DuplexLuaRpcResult { TransportUnsupported = true, ResultOrError = "unsupported" });
        bridgeMock.Setup(b => b.GetEngineStateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EngineStateDto { MainThreadBusy = false, HostFrame = 120, MsSinceHostFrameAdvance = 5 });

        int calls = 0;
        bridgeMock.Setup(b => b.ExecuteLuaAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                calls++;
                return calls == 1 ? "[ERROR] engine_busy" : "[LUA_OK] 42";
            });

        ClientEvalLuaHandler handler = new(ipcBridge: bridgeMock.Object);
        using JsonDocument argsDoc = JsonDocument.Parse("""{"action":"eval","code":"return 40 + 2","timeoutMs":2000}""");
        ToolCallResult res = await handler.HandleAsync(argsDoc.RootElement);

        res.IsError.Should().BeFalse();
        calls.Should().Be(2);
        res.FirstText.Should().Contain("[LUA_OK] 42");
    }

    [Fact]
    public async Task WaitForHandler_ClassifiesWrongGamePhaseAndHiddenWidgetOnTimeout()
    {
        // protects: Agent self-recovery on kongor_wait_for timeouts by distinguishing wrong gamePhase vs hidden widgets vs loading stalls.
        // fails_when: Timeout returns generic "Run kongor_k2_ui_inspect" without timeoutClassification or live engineSnapshot.
        // seam: Real WaitForHandler + simulated ITaoClient + IGameIpcBridge engine state.

        Mock<IGameIpcBridge> bridgeMock = new();
        bridgeMock.SetupGet(b => b.IsConnected).Returns(true);
        int frame = 500;
        bridgeMock.Setup(b => b.GetEngineStateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new EngineStateDto
            {
                IsActiveMatch = false,
                CurrentMap = "none",
                MainThreadBusy = false,
                HostFrame = Interlocked.Increment(ref frame)
            });

        Mock<ITaoClient> taoMock = new();
        using JsonDocument notFoundDoc = JsonDocument.Parse("""{"found":false}""");
        taoMock.Setup(t => t.DispatchAsync("inspect_ui", It.IsAny<object>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TaoResponse.Ok("inspect_ui", notFoundDoc.RootElement.Clone(), 1.0));

        WaitForHandler handler = new(taoClient: taoMock.Object, gameIpcBridge: bridgeMock.Object);
        using JsonDocument hudArgs = JsonDocument.Parse("""{"condition":"ui_visible","interface":"game_apex","target":"ability_bar","timeoutMs":150,"intervalMs":40}""");
        ToolCallResult hudRes = await handler.HandleAsync(hudArgs.RootElement);

        hudRes.IsError.Should().BeTrue();
        using JsonDocument hudOut = JsonDocument.Parse(hudRes.FirstText);
        hudOut.RootElement.GetProperty("timeoutClassification").GetString().Should().Be("wrong_game_phase");
        hudOut.RootElement.GetProperty("actionableRemediation").GetString().Should().Contain("kongor_client_practice");
    }

    [Fact]
    public async Task McpConversationLogger_CorrelatesTargetToolAndOriginOnOutboundResponse()
    {
        // protects: DuckDB/NDJSON MCP outcome analytics hygiene by tagging test vs live origin and correlating outbound responses to targetTool.
        // fails_when: Outbound NDJSON records omit 'origin' or 'targetTool'.
        // seam: Real McpConversationLogger writing & flushing NDJSON in isolated temp directory.

        string tmpDir = Path.Combine(Path.GetTempPath(), $"kongor_mcp_log_test_{Guid.NewGuid():N}");
        try
        {
            await using (McpConversationLogger logger = new(tmpDir))
            {
                using JsonDocument args = JsonDocument.Parse("""{"action":"status"}""");
                logger.LogInbound("""{"jsonrpc":"2.0","id":"req-99","method":"tools/call"}""", "tools/call", "req-99", "kongor_health_check", args.RootElement);
                logger.LogOutbound("""{"jsonrpc":"2.0","id":"req-99","result":{"ok":true}}""", "req-99", isError: false, durationMs: 12.5);
                await logger.FlushAsync();
            }

            string ndjsonFile = Directory.GetFiles(tmpDir, "*.ndjson").Single();
            string[] lines = await File.ReadAllLinesAsync(ndjsonFile);
            lines.Should().HaveCount(2);

            using JsonDocument outRecord = JsonDocument.Parse(lines[1]);
            outRecord.RootElement.GetProperty("type").GetString().Should().Be("outbound_response");
            outRecord.RootElement.GetProperty("targetTool").GetString().Should().Be("kongor_health_check");
            outRecord.RootElement.GetProperty("origin").GetString().Should().Be("test");
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public async Task OfficialSdkStdio_EndToEndParity_Annotations_StructuredContent_Templates_Sampling_AndWarmInitBudget()
    {
        // protects: Official ModelContextProtocol SDK stdio transport parity (Annotations, OutputSchema, StructuredContent, ResourceTemplates, SampleAsync) and <= 1,000ms warm init budget.
        // fails_when: KongorOfficialMcpAdapter drops ToolAnnotations/OutputSchema/StructuredContent, omits resources/templates/list, fails SampleAsync, or exceeds 1,000ms warm init.
        // seam: Real OfficialMcpHttpHost.RunSdkStdioAsync over System.IO.Pipelines.Pipe streams + official McpClient.

        using JsonDocument outputSchemaDoc = JsonDocument.Parse("""{"type":"object","properties":{"ok":{"type":"boolean"},"diagnosis":{"type":"string"}}}""");
        McpToolRegistry warmRegistry = new(registerDefaults: false);
        warmRegistry.RegisterTool(
            new ToolDefinition
            {
                Name = "kongor_sdk_parity_probe",
                Description = "Probes official SDK parity for annotations, structuredContent, and reverse LLM sampling.",
                InputSchema = new ToolInputSchema { Type = "object" },
                OutputSchema = outputSchemaDoc.RootElement.Clone(),
                Annotations = new Kongor.Cli.Mcp.Protocol.ToolAnnotations
                {
                    Title = "SDK Parity Probe",
                    ReadOnlyHint = true,
                    IdempotentHint = true,
                    DestructiveHint = false,
                    OpenWorldHint = false
                }
            },
            async (_, ct) =>
            {
                string? diagnosis = null;
                if (McpSamplingContext.Current?.IsSamplingSupported == true)
                {
                    diagnosis = await McpSamplingContext.Current.RequestCrashAnalysisAsync("0xC0000005", "k2_x64.dll+0x1234", ct);
                }

                return ToolCallResult.Success(new { ok = true, diagnosis }).WithStructuredContentIfJson();
            },
            McpToolCategory.ReadOnly);

        // Warm JIT once so the measured session reflects warm sdk-stdio transport initialization.
        using (Kongor.Cli.Mcp.Protocol.McpServer warmupServer = new(warmRegistry, TextWriter.Null))
        {
            Pipe c2sWarm = new();
            Pipe s2cWarm = new();
            using CancellationTokenSource warmCts = new(TimeSpan.FromSeconds(10));
            Task warmHostTask = OfficialMcpHttpHost.RunSdkStdioAsync(warmupServer, c2sWarm.Reader.AsStream(), s2cWarm.Writer.AsStream(), warmCts.Token);
            StreamClientTransport warmTransport = new(c2sWarm.Writer.AsStream(), s2cWarm.Reader.AsStream());
            await using (McpClient warmClient = await McpClient.CreateAsync(warmTransport, new McpClientOptions { ProtocolVersion = "2025-06-18" }, cancellationToken: warmCts.Token))
            {
                _ = await warmClient.ListToolsAsync(cancellationToken: warmCts.Token);
            }
            await warmCts.CancelAsync();
            try { await warmHostTask; } catch (OperationCanceledException) { }
        }

        using Kongor.Cli.Mcp.Protocol.McpServer kongorServer = new(warmRegistry, TextWriter.Null);
        Pipe clientToServer = new();
        Pipe serverToClient = new();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(15));

        Stopwatch initSw = Stopwatch.StartNew();
        Task serverTask = OfficialMcpHttpHost.RunSdkStdioAsync(
            kongorServer,
            clientToServer.Reader.AsStream(),
            serverToClient.Writer.AsStream(),
            cts.Token);

        StreamClientTransport clientTransport = new(
            clientToServer.Writer.AsStream(),
            serverToClient.Reader.AsStream());

#pragma warning disable MCP9005
        McpClientOptions clientOptions = new()
        {
            ProtocolVersion = "2025-06-18",
            Capabilities = new ModelContextProtocol.Protocol.ClientCapabilities
            {
                Sampling = new SamplingCapability()
            },
            Handlers = new McpClientHandlers
            {
                SamplingHandler = (_, _, _) => ValueTask.FromResult(new ModelContextProtocol.Protocol.CreateMessageResult
                {
                    Model = "kongor-test-llm",
                    Role = Role.Assistant,
                    StopReason = "endTurn",
                    Content = [new TextContentBlock { Text = "Null entity dereference in CGameClient::Frame" }]
                })
            }
        };
#pragma warning restore MCP9005

        try
        {
            await using McpClient client = await McpClient.CreateAsync(clientTransport, clientOptions, cancellationToken: cts.Token);
            initSw.Stop();
            initSw.ElapsedMilliseconds.Should().BeLessThanOrEqualTo(1000, "warm sdk-stdio initialization must stay <= 1,000 ms");

            IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: cts.Token);
            McpClientTool probeTool = tools.Single(t => t.Name == "kongor_sdk_parity_probe");
            probeTool.ProtocolTool.Annotations.Should().NotBeNull();
            probeTool.ProtocolTool.Annotations!.Title.Should().Be("SDK Parity Probe");
            probeTool.ProtocolTool.Annotations.ReadOnlyHint.Should().BeTrue();
            probeTool.ProtocolTool.OutputSchema.Should().NotBeNull();

            IList<McpClientResourceTemplate> templates = await client.ListResourceTemplatesAsync(cancellationToken: cts.Token);
            templates.Should().NotBeEmpty();

            CallToolResult callRes = await client.CallToolAsync("kongor_sdk_parity_probe", cancellationToken: cts.Token);
            callRes.IsError.Should().NotBeTrue();
            callRes.StructuredContent.Should().NotBeNull();
            callRes.StructuredContent!.Value.GetProperty("ok").GetBoolean().Should().BeTrue();
            callRes.StructuredContent.Value.GetProperty("diagnosis").GetString().Should().Contain("Null entity dereference");
        }
        finally
        {
            await cts.CancelAsync();
            try { await serverTask; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public void WaitForHandler_ColdStartGraceFloor_ExtendsShortTimeoutDuringClientBoot_AndHonorsStrictTimeoutOptOut()
    {
        // protects: Eliminating MCP-194/MCP-198 first-call timeouts when kongor_wait_for is called with a short timeout while hon_x64 is still cold-booting (<25s).
        // fails_when: A 2500ms wait on a 4s-old hon_x64 process is not extended to the 15000ms cold-start floor, or warm/strictTimeout calls are unexpectedly extended.
        // seam: Real WaitForHandler.ResolveEffectiveTimeoutMs with injected clientProcessAgeProvider.

        WaitForHandler bootingHandler = new(clientProcessAgeProvider: () => TimeSpan.FromSeconds(4));
        using JsonDocument shortArgs = JsonDocument.Parse("""{"condition":"ipc_ready","timeoutMs":2500}""");
        int effective = bootingHandler.ResolveEffectiveTimeoutMs(shortArgs.RootElement, "ipc_ready", 2500, out bool graceApplied, out double? ageSeconds);

        effective.Should().Be(WaitForHandler.ColdStartTimeoutFloorMs);
        graceApplied.Should().BeTrue();
        ageSeconds.Should().Be(4.0);

        using JsonDocument strictArgs = JsonDocument.Parse("""{"condition":"ipc_ready","timeoutMs":2500,"strictTimeout":true}""");
        int strictEffective = bootingHandler.ResolveEffectiveTimeoutMs(strictArgs.RootElement, "ipc_ready", 2500, out bool strictGrace, out _);
        strictEffective.Should().Be(2500);
        strictGrace.Should().BeFalse();

        WaitForHandler warmHandler = new(clientProcessAgeProvider: () => TimeSpan.FromSeconds(45));
        int warmEffective = warmHandler.ResolveEffectiveTimeoutMs(shortArgs.RootElement, "ipc_ready", 2500, out bool warmGrace, out _);
        warmEffective.Should().Be(2500);
        warmGrace.Should().BeFalse();
    }

    [Fact]
    public async Task ClientEvalLuaHandler_ColdStartIpcGrace_RetriesUntilPipeConnectsDuringBoot()
    {
        // protects: Eliminating MCP-215 precondition failures when kongor_client_eval_lua runs seconds after hon_x64 launch before the Named Pipe is created.
        // fails_when: ClientEvalLuaHandler fails immediately on the first ConnectAsync failure while client is booting instead of awaiting IPC readiness.
        // seam: Real ClientEvalLuaHandler + mock IGameIpcBridge becoming connected on attempt #3.

        bool connected = false;
        int connectCalls = 0;
        Mock<IGameIpcBridge> bridgeMock = new();
        bridgeMock.SetupGet(b => b.PipeName).Returns("kongor-native-client");
        bridgeMock.SetupGet(b => b.IsConnected).Returns(() => connected);
        bridgeMock.Setup(b => b.ConnectAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                connectCalls++;
                if (connectCalls >= 3)
                {
                    connected = true;
                    return Task.CompletedTask;
                }
                throw new TimeoutException("Pipe not created yet");
            });
        bridgeMock.Setup(b => b.GetEngineStateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EngineStateDto { MainThreadBusy = false, HostFrame = 100, MsSinceHostFrameAdvance = 5 });
        bridgeMock.Setup(b => b.EvalLuaDuplexAsync(It.IsAny<string>(), It.IsAny<uint>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DuplexLuaRpcResult { Success = true, ResultOrError = "[LUA_OK] 99" });

        ClientEvalLuaHandler handler = new(
            ipcBridge: bridgeMock.Object,
            isClientBootingProvider: () => true,
            defaultIpcReadyGraceMs: 3000);

        using JsonDocument argsDoc = JsonDocument.Parse("""{"action":"eval","code":"return 99","timeoutMs":2000}""");
        ToolCallResult res = await handler.HandleAsync(argsDoc.RootElement);

        res.IsError.Should().BeFalse();
        connectCalls.Should().Be(3);
        res.FirstText.Should().Contain("[LUA_OK] 99");
    }

    [Fact]
    public async Task ClientEvalLuaHandler_WhenNotBooting_FailsFastWithClientOfflineErrorCode()
    {
        // protects: Fast fail (<100ms) with structured clientOffline errorCode when hon_x64 is not running.
        // fails_when: Disconnected eval hangs in retry loop when no client is starting up or omits errorCode=clientOffline.
        // seam: Real ClientEvalLuaHandler with isClientBootingProvider=() => false.

        int connectCalls = 0;
        Mock<IGameIpcBridge> bridgeMock = new();
        bridgeMock.SetupGet(b => b.PipeName).Returns("kongor-native-client");
        bridgeMock.SetupGet(b => b.IsConnected).Returns(false);
        bridgeMock.Setup(b => b.ConnectAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                connectCalls++;
                throw new TimeoutException("Pipe offline");
            });

        ClientEvalLuaHandler handler = new(
            ipcBridge: bridgeMock.Object,
            isClientBootingProvider: () => false);

        using JsonDocument argsDoc = JsonDocument.Parse("""{"action":"eval","code":"return 1","timeoutMs":1000}""");
        ToolCallResult res = await handler.HandleAsync(argsDoc.RootElement);

        res.IsError.Should().BeTrue();
        connectCalls.Should().Be(1);
        using JsonDocument outDoc = JsonDocument.Parse(res.FirstText);
        outDoc.RootElement.GetProperty("errorCode").GetString().Should().Be("clientOffline");
    }

    [Fact]
    public async Task DbFixtureHandler_PreFlightChecks_ClassifyMissingMalformedAndUnreachableDbAsPrecondition()
    {
        // protects: Preventing kongor_db_fixture from emitting unhandled tool_fault errors when DB config is missing, malformed, or PostgreSQL is offline.
        // fails_when: Missing/invalid/unreachable PostgreSQL returns plain text or classifies as tool_fault instead of structured precondition JSON.
        // seam: Real DbFixtureHandler with injectable TCP probe + real McpToolRegistry.ClassifyErrorClass.

        string? origEnv1 = Environment.GetEnvironmentVariable("KONGOR_DB_CONNECTION_STRING");
        string? origEnv2 = Environment.GetEnvironmentVariable("ConnectionStrings__merrick-db");
        try
        {
            Environment.SetEnvironmentVariable("KONGOR_DB_CONNECTION_STRING", null);
            Environment.SetEnvironmentVariable("ConnectionStrings__merrick-db", null);

            DbFixtureHandler handler = new(
                tcpProbeAsync: (host, port, _, _) => Task.FromResult<(bool, string?)>((false, $"Connection refused at {host}:{port}")));

            // 1. Missing connection string
            using JsonDocument emptyArgs = JsonDocument.Parse("""{"action":"status"}""");
            ToolCallResult missingRes = await handler.HandleAsync(emptyArgs.RootElement);
            missingRes.IsError.Should().BeTrue();
            using JsonDocument missingDoc = JsonDocument.Parse(missingRes.FirstText);
            string missingCode = missingDoc.RootElement.GetProperty("errorCode").GetString()!;
            missingCode.Should().Be("missing_connection_string");
            McpToolRegistry.ClassifyErrorClass("kongor_db_fixture", true, missingRes.FirstText, missingCode, preflightFailed: false)
                .Should().Be("precondition");

            // 2. Malformed connection string
            using JsonDocument badArgs = JsonDocument.Parse("""{"action":"status","connectionString":"NotAValidKey===Value;;;"}""");
            ToolCallResult badRes = await handler.HandleAsync(badArgs.RootElement);
            badRes.IsError.Should().BeTrue();
            using JsonDocument badDoc = JsonDocument.Parse(badRes.FirstText);
            string badCode = badDoc.RootElement.GetProperty("errorCode").GetString()!;
            badCode.Should().Be("invalid_connection_string");
            McpToolRegistry.ClassifyErrorClass("kongor_db_fixture", true, badRes.FirstText, badCode, preflightFailed: false)
                .Should().Be("precondition");

            // 3. Unreachable TCP endpoint (fast pre-flight fails before NpgsqlConnection.OpenAsync)
            using JsonDocument unreachableArgs = JsonDocument.Parse("""{"action":"status","connectionString":"Host=127.0.0.1;Port=59999;Database=merrick;Username=postgres"}""");
            ToolCallResult unreachableRes = await handler.HandleAsync(unreachableArgs.RootElement);
            unreachableRes.IsError.Should().BeTrue();
            using JsonDocument unreachableDoc = JsonDocument.Parse(unreachableRes.FirstText);
            string unreachableCode = unreachableDoc.RootElement.GetProperty("errorCode").GetString()!;
            unreachableCode.Should().Be("database_unreachable");
            unreachableDoc.RootElement.GetProperty("endpoint").GetString().Should().Be("127.0.0.1:59999");
            McpToolRegistry.ClassifyErrorClass("kongor_db_fixture", true, unreachableRes.FirstText, unreachableCode, preflightFailed: false)
                .Should().Be("precondition");
        }
        finally
        {
            Environment.SetEnvironmentVariable("KONGOR_DB_CONNECTION_STRING", origEnv1);
            Environment.SetEnvironmentVariable("ConnectionStrings__merrick-db", origEnv2);
        }
    }
}
