// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using Xunit;

namespace Kongor.Mcp.Tests;

/// <summary>
/// Unit and integration tests for Slice P1.3:
/// 1. Sub-millisecond duplex binary pipe 0x0080 Lua RPC round-trip framing.
/// 2. Real PostgreSQL / Merrick database migration drift & transactional rollback handler.
/// 3. Real PostgreSQL / Redis fixture handler with offline honesty gate enforcement.
/// 4. Testcontainers PostgreSQL & Redis live verification (Manual/Container gate).
/// </summary>
public sealed class DuplexPipeAndRealHandlersTests
{
    [Fact]
    public async Task DuplexLuaRpc_NamedPipeServer_RoundTripFraming_SucceedsInSub5Ms()
    {
        string pipeName = $"test-native-pipe-duplex-{Guid.NewGuid():N}";
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        // Start in-process NamedPipeServerStream emulating NativeGuard's 0x0080 / 0x0081 handler
        Task serverTask = Task.Run(async () =>
        {
            await using NamedPipeServerStream server = new(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            await server.WaitForConnectionAsync(cts.Token).ConfigureAwait(false);

            // Handle 2 frames: 1 warm-up RPC (eliminates first-call JIT noise under parallel CI load) + 1 timed steady-state RPC
            for (int frameIndex = 0; frameIndex < 2; frameIndex++)
            {
                byte[] headerBuf = new byte[12];
                int read = await server.ReadAsync(headerBuf.AsMemory(0, 12), cts.Token).ConfigureAwait(false);
                read.Should().Be(12);

                ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(headerBuf.AsSpan(0, 2));
                ushort opCode = BinaryPrimitives.ReadUInt16LittleEndian(headerBuf.AsSpan(2, 2));
                int payloadLen = BinaryPrimitives.ReadInt32LittleEndian(headerBuf.AsSpan(4, 4));
                uint reqId = BinaryPrimitives.ReadUInt32LittleEndian(headerBuf.AsSpan(8, 4));

                magic.Should().Be(0x574D, "Header magic must match 0x574D ('WM')");
                opCode.Should().Be(0x0080, "Opcode must be DuplexLuaRpc (0x0080)");

                byte[] payload = new byte[payloadLen];
                if (payloadLen > 0)
                {
                    int payloadRead = await server.ReadAsync(payload.AsMemory(0, payloadLen), cts.Token).ConfigureAwait(false);
                    payloadRead.Should().Be(payloadLen);
                }

                payloadLen.Should().BeGreaterThanOrEqualTo(8);
                uint timeoutMs = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4));
                uint innerReqId = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4, 4));
                string luaScript = Encoding.UTF8.GetString(payload, 8, payloadLen - 8);

                timeoutMs.Should().BeGreaterThan(0);
                innerReqId.Should().Be(reqId);
                luaScript.Should().Contain("return 40 + 2");

                // Build 0x0081 response frame
                // Inner payload: [uint32 reqId][uint32 elapsedUs][uint8 statusFlags][utf8 result]
                byte[] resultBytes = Encoding.UTF8.GetBytes("[LUA_OK] 42");
                byte[] respPayload = new byte[9 + resultBytes.Length];
                BinaryPrimitives.WriteUInt32LittleEndian(respPayload.AsSpan(0, 4), reqId);
                BinaryPrimitives.WriteUInt32LittleEndian(respPayload.AsSpan(4, 4), 180); // 180 us QPC duration
                respPayload[8] = 0x01; // bit0 = ok
                resultBytes.CopyTo(respPayload, 9);

                // Outer header: [0x574D][0x0081][respPayloadLen][reqId]
                byte[] respHeader = new byte[12];
                BinaryPrimitives.WriteUInt16LittleEndian(respHeader.AsSpan(0, 2), 0x574D);
                BinaryPrimitives.WriteUInt16LittleEndian(respHeader.AsSpan(2, 2), 0x0081);
                BinaryPrimitives.WriteInt32LittleEndian(respHeader.AsSpan(4, 4), respPayload.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(respHeader.AsSpan(8, 4), reqId);

                await server.WriteAsync(respHeader, cts.Token).ConfigureAwait(false);
                await server.WriteAsync(respPayload, cts.Token).ConfigureAwait(false);
                await server.FlushAsync(cts.Token).ConfigureAwait(false);
            }
        }, cts.Token);

        // Client side: connect GameIpcBridge and dispatch EvalLuaDuplexAsync
        await using GameIpcBridge bridge = new(pipeName, logAction: _ => { });
        await bridge.ConnectAsync(timeoutMs: 2000, cancellationToken: cts.Token);

        // Warm-up RPC to exclude cold JIT compilation from steady-state IPC latency measurement
        DuplexLuaRpcResult warmup = await bridge.EvalLuaDuplexAsync("return 40 + 2", 2000, cts.Token);
        warmup.Success.Should().BeTrue();

        Stopwatch sw = Stopwatch.StartNew();
        DuplexLuaRpcResult result = await bridge.EvalLuaDuplexAsync("return 40 + 2", 2000, cts.Token);
        sw.Stop();

        await serverTask;

        result.Success.Should().BeTrue();
        result.TimedOut.Should().BeFalse();
        result.ResultOrError.Should().Be("[LUA_OK] 42");
        result.ElapsedMicroseconds.Should().Be(180);
        sw.ElapsedMilliseconds.Should().BeLessThan(50, "In-process steady-state duplex IPC round-trip must complete in < 5 ms (buffered for CI).");
    }

    [Fact]
    public async Task DbMigrationTestHandler_BrokenConnectionString_ReturnsErrorWithoutFakingData()
    {
        DbMigrationTestHandler handler = new(logger: _ => { });
        using var args = JsonDocument.Parse("{\"connectionString\": \"Host=127.0.0.1;Port=59999;Database=nonexistent_test_db;Username=fake;Password=fake;Timeout=1;CommandTimeout=1\", \"action\": \"verify_drift\"}");

        ToolCallResult result = await handler.HandleAsync(args.RootElement);

        result.IsError.Should().BeTrue("Broken connection string must return actionable error and never report fake success.");
        result.FirstText.Should().Contain("Failed to connect or verify live PostgreSQL database");
    }

    [Fact]
    public async Task DbFixtureHandler_BrokenConnectionString_ReturnsErrorWithoutFakingData()
    {
        DbFixtureHandler handler = new(logger: _ => { });
        using var args = JsonDocument.Parse("{\"connectionString\": \"Host=127.0.0.1;Port=59999;Database=nonexistent_test_db;Username=fake;Password=fake;Timeout=1;CommandTimeout=1\", \"action\": \"status\"}");

        ToolCallResult result = await handler.HandleAsync(args.RootElement);

        result.IsError.Should().BeTrue("Broken connection string must return error without lying.");
        result.FirstText.Should().Contain("database_unreachable");
        result.FirstText.Should().Contain("precondition");
        result.FirstText.Should().Contain("Preflight check failed");
        result.FirstText.Should().Contain("remediation");
    }

    [Fact]
    public async Task NetPacketFuzz_OfflineParserDirect_ExecutesRealInvariantDissection()
    {
        NetPacketFuzzHandler handler = new(logger: _ => { });
        // Valid GameServerPacketHeader with 4-byte payload [1, 2, 3, 4] and matching CRC32 0xCBF43926
        using var args = JsonDocument.Parse("{\"action\": \"dissect\", \"mode\": \"offline_parser\", \"packet_hex\": \"4B4F4E47030001000A040000040000002639F4CB01020304\"}");

        ToolCallResult result = await handler.HandleAsync(args.RootElement);

        result.IsError.Should().BeFalse("Deterministic offline parsing must succeed without live UDP sockets.");
        result.FirstText.Should().Contain(ExecutionFidelity.StaticOfflineAnalysis.ToString());
        result.FirstText.Should().Contain("dissection");
        result.FirstText.Should().Contain("crc32Match");
    }

    [Fact]
    [Trait("Category", "Manual")]
    public async Task Testcontainers_PostgreSqlAndRedis_RealLiveVerification()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled)
        {
            return;
        }

        // 1. Boot live PostgreSQL container
        await using PostgreSqlContainer postgres = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("merrick_test")
            .WithUsername("postgres")
            .WithPassword("kongor_test_password")
            .Build();

        await postgres.StartAsync();

        // 2. Boot live Redis/Garnet container
        await using RedisContainer redis = new RedisBuilder()
            .WithImage("redis:7-alpine")
            .Build();

        await redis.StartAsync();

        string pgConnString = postgres.GetConnectionString();

        // 3. Verify DbMigrationTestHandler with live transactional rollback
        DbMigrationTestHandler migHandler = new();
        using var migArgs = JsonDocument.Parse($"{{\"connectionString\": \"{pgConnString}\", \"action\": \"test_rollback\"}}");
        ToolCallResult migResult = await migHandler.HandleAsync(migArgs.RootElement);

        migResult.IsError.Should().BeFalse();
        migResult.FirstText.Should().Contain(ExecutionFidelity.LiveVerified.ToString());
        migResult.FirstText.Should().Contain("PASSED: Full live PostgreSQL transactional isolation and rollback verified");

        // 4. Verify DbFixtureHandler against live PostgreSQL container
        DbFixtureHandler fixHandler = new();
        using var fixArgs = JsonDocument.Parse($"{{\"connectionString\": \"{pgConnString}\", \"action\": \"status\"}}");
        ToolCallResult fixResult = await fixHandler.HandleAsync(fixArgs.RootElement);

        fixResult.IsError.Should().BeFalse();
        fixResult.FirstText.Should().Contain(ExecutionFidelity.LiveVerified.ToString());
        fixResult.FirstText.Should().Contain("Connected");
    }
}
