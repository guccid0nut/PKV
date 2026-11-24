// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

// ============================================================================
// File: LiveMailboxAcceptanceTests.cs
// System: Kongor MCP Tests - Live Engine Mailbox IPC Acceptance Verification
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

#pragma warning disable CA2026
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Kongor.Cli.Mcp.Services;
using Xunit;
using Xunit.Abstractions;

namespace Kongor.Mcp.Tests;

public sealed class LiveMailboxAcceptanceTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private NexusMailboxIpcBridge? _bridge;

    public LiveMailboxAcceptanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public Task InitializeAsync()
    {
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_bridge != null)
        {
            await _bridge.DisposeAsync();
            _bridge = null;
        }
    }

    private static bool IsLiveClientRunning()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled)
            return false;
        Process[] procs = Process.GetProcessesByName("hon_x64");
        return procs.Any(p => !p.HasExited);
    }

    private NexusMailboxIpcBridge CreateLiveBridge()
    {
        NexusMailboxIpcBridge bridge = new(
            mailboxDir: null,
            logger: msg => _output.WriteLine(msg),
            timeProvider: null,
            authKey: null,
            signalBridge: null);
        bridge.ForceConnected = true;
        return bridge;
    }

    [Fact]
    public async Task LiveMailbox_01_Ping_ExecutesThroughBridgeAndReturnsValidResponse()
    {
        if (!IsLiveClientRunning())
        {
            _output.WriteLine("SKIPPED: Live game client hon_x64 is not running.");
            return;
        }

        _bridge = CreateLiveBridge();
        long latencyMicros = await _bridge.PingAsync(timeoutMs: 5000);
        double latencyMs = latencyMicros / 1000.0;
        _output.WriteLine($"[ACCEPTANCE] Ping response received successfully in {latencyMs:F2}ms");

        latencyMs.Should().BeGreaterThan(0.0);
        latencyMs.Should().BeLessThan(2000.0);
    }

    [Fact]
    public async Task LiveMailbox_02_LatencyBenchmark_200Requests_MeasuresP50AndP95()
    {
        if (!IsLiveClientRunning())
        {
            _output.WriteLine("SKIPPED: Live game client hon_x64 is not running.");
            return;
        }

        _bridge = CreateLiveBridge();

        // Warm up with 5 pings
        for (int w = 0; w < 5; w++)
        {
            await _bridge.PingAsync(timeoutMs: 5000);
        }

        const int totalRequests = 200;
        List<double> latenciesMs = new(totalRequests);
        Stopwatch totalSw = Stopwatch.StartNew();

        for (int i = 1; i <= totalRequests; i++)
        {
            Stopwatch reqSw = Stopwatch.StartNew();
            await _bridge.PingAsync(timeoutMs: 5000);
            reqSw.Stop();

            double ms = reqSw.Elapsed.TotalMilliseconds;
            latenciesMs.Add(ms);

            if (i % 50 == 0 || i == totalRequests)
            {
                _output.WriteLine($"[BENCHMARK] Completed {i}/{totalRequests} requests. Last latency: {ms:F2}ms");
            }

            await Task.Delay(25); // Inter-request frame yield
        }
        totalSw.Stop();

        latenciesMs.Sort();
        double min = latenciesMs[0];
        double max = latenciesMs[^1];
        double avg = latenciesMs.Average();
        double p50 = latenciesMs[(int)(totalRequests * 0.50)];
        double p90 = latenciesMs[(int)(totalRequests * 0.90)];
        double p95 = latenciesMs[(int)(totalRequests * 0.95)];
        double p99 = latenciesMs[(int)(totalRequests * 0.99)];

        _output.WriteLine("=================================================================");
        _output.WriteLine($"[ACCEPTANCE BENCHMARK] 200 Consecutive Requests Telemetry Summary");
        _output.WriteLine($"Total Duration: {totalSw.Elapsed.TotalSeconds:F2}s ({totalRequests / totalSw.Elapsed.TotalSeconds:F1} req/s)");
        _output.WriteLine($"Min Latency:    {min:F2} ms");
        _output.WriteLine($"Avg Latency:    {avg:F2} ms");
        _output.WriteLine($"P50 (Median):   {p50:F2} ms");
        _output.WriteLine($"P90:            {p90:F2} ms");
        _output.WriteLine($"P95:            {p95:F2} ms");
        _output.WriteLine($"P99:            {p99:F2} ms");
        _output.WriteLine($"Max Latency:    {max:F2} ms");
        _output.WriteLine("=================================================================");

        // Empirical assertions
        latenciesMs.Count.Should().Be(totalRequests);
        p50.Should().BeLessThan(250.0, "p50 latency over IPC bridge must be responsive");
        p95.Should().BeLessThan(500.0, "p95 latency over IPC bridge must not exhibit unbounded stalls");
    }

    [Fact]
    public async Task LiveMailbox_03_LargePayload_64KB_ReassemblesByteIdentical()
    {
        if (!IsLiveClientRunning())
        {
            _output.WriteLine("SKIPPED: Live game client hon_x64 is not running.");
            return;
        }

        _bridge = CreateLiveBridge();

        // Generate deterministic 65,536-character string in Lua: 1024 blocks of 64 chars
        // "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcdefghijklmnopqrstuvwxyz01"
        const int expectedLength = 65536;
        string pattern = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcdefghijklmnopqrstuvwxyz01"; // 64 chars
        int repeats = expectedLength / pattern.Length; // 1024 repeats

        string luaCode = $"return string.rep('{pattern}', {repeats})";

        Stopwatch sw = Stopwatch.StartNew();
        string result = await _bridge.ExecuteLuaAsync(luaCode, timeoutMs: 15000);
        sw.Stop();

        _output.WriteLine($"[ACCEPTANCE] 64 KB response received in {sw.ElapsedMilliseconds}ms. Length: {result.Length} chars");

        // Verify exact length
        result.Length.Should().Be(expectedLength, "64 KB payload must not be truncated or corrupted across chunk boundary reassembly");

        // Verify byte-identical content
        string expectedPattern = string.Concat(Enumerable.Repeat(pattern, repeats));
        result.Should().Be(expectedPattern, "Reassembled chunked string must be byte-identical to source");
    }

    [Fact]
    public async Task LiveMailbox_04_MemoryTelemetry_100RequestBurst_LuaMemoryRemainsFlat()
    {
        if (!IsLiveClientRunning())
        {
            _output.WriteLine("SKIPPED: Live game client hon_x64 is not running.");
            return;
        }

        _bridge = CreateLiveBridge();

        // 1. Initial sample (force full GC collection for precise heap baseline)
        string initialMemStr = await _bridge.ExecuteLuaAsync("collectgarbage('collect'); return tostring(collectgarbage('count'))");
        double initialMemoryKb = double.Parse(initialMemStr, System.Globalization.CultureInfo.InvariantCulture);
        _output.WriteLine($"[TELEMETRY] Baseline Lua Memory: {initialMemoryKb:F2} KB");

        // 2. Burst 100 requests
        for (int i = 0; i < 100; i++)
        {
            await _bridge.PingAsync(timeoutMs: 5000);
            await Task.Delay(25);
        }

        // 3. Post-burst sample (force full GC collection to measure true retained memory)
        string postMemStr = await _bridge.ExecuteLuaAsync("collectgarbage('collect'); return tostring(collectgarbage('count'))");
        double postMemoryKb = double.Parse(postMemStr, System.Globalization.CultureInfo.InvariantCulture);
        _output.WriteLine($"[TELEMETRY] Post-Burst Lua Memory: {postMemoryKb:F2} KB");

        double memoryGrowthKb = postMemoryKb - initialMemoryKb;
        _output.WriteLine($"[TELEMETRY] Net Memory Delta across 100 requests: {memoryGrowthKb:F2} KB");

        // Across 100 requests with full collection, retained heap delta should be near zero (< 512 KB)
        memoryGrowthKb.Should().BeLessThan(512.0, "Lua memory must not leak unboundedly during request processing bursts");
    }

    [Fact]
    public async Task LiveMailbox_05_BridgeRestart_CleansUpStaleFiles()
    {
        string mailboxDir = Path.Combine(Path.GetTempPath(), "kongor_mailbox_stale_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mailboxDir);

        try
        {
            string staleTmp = Path.Combine(mailboxDir, "req_stale_test.tmp");
            string staleLua = Path.Combine(mailboxDir, "req_stale_test.lua");
            await File.WriteAllTextAsync(staleTmp, "test");
            await File.WriteAllTextAsync(staleLua, "test");

            // Set LastWriteTime to 10 minutes ago
            DateTime past = DateTime.UtcNow.AddMinutes(-10);
            File.SetLastWriteTimeUtc(staleTmp, past);
            File.SetLastWriteTimeUtc(staleLua, past);

            File.Exists(staleTmp).Should().BeTrue();
            File.Exists(staleLua).Should().BeTrue();

            // New bridge initialization cleans up stale files
            NexusMailboxIpcBridge bridge = new(
                mailboxDir: mailboxDir,
                logger: null,
                timeProvider: null,
                authKey: null,
                signalBridge: null);

            File.Exists(staleTmp).Should().BeFalse("Stale .tmp request files must be purged on startup");
            File.Exists(staleLua).Should().BeFalse("Stale .lua request files older than 5m must be purged on startup");

            await bridge.DisposeAsync();
        }
        finally
        {
            if (Directory.Exists(mailboxDir))
            {
                Directory.Delete(mailboxDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LiveMailbox_06_InterfaceSwapDuringNavigate_MailboxSurvivesAndResponds()
    {
        if (!IsLiveClientRunning())
        {
            _output.WriteLine("SKIPPED: Live game client hon_x64 is not running.");
            return;
        }

        _bridge = CreateLiveBridge();

        // 1. Initial ping to verify communication
        long ping1 = await _bridge.PingAsync(timeoutMs: 5000);
        ping1.Should().BeGreaterThan(0);

        // 2. Trigger interface reload/swap
        _output.WriteLine("[ACCEPTANCE] Triggering interface swap (Cmd('ReloadInterfaces'))...");
        await _bridge.ExecuteCommandAsync("ReloadInterfaces", timeoutMs: 5000);

        // Allow 500ms for K2 to reconstruct DOM and rebind watchers
        await Task.Delay(500);

        // 3. Post-reload dispatch: execute Lua and verify state survived
        Stopwatch sw = Stopwatch.StartNew();
        string res = await _bridge.ExecuteLuaAsync("return 'POST_RELOAD_OK: ' .. tostring(Nexus ~= nil and Nexus.Mailbox ~= nil)", timeoutMs: 8000);
        sw.Stop();

        _output.WriteLine($"[ACCEPTANCE] Post-reload Lua response in {sw.ElapsedMilliseconds}ms: {res}");
        res.Should().Contain("POST_RELOAD_OK: true", "Nexus.Mailbox must survive interface reloads/swaps via global _G registration");

        // 4. Verify consecutive request still operates with low latency
        long ping2 = await _bridge.PingAsync(timeoutMs: 5000);
        ping2.Should().BeGreaterThan(0);
        _output.WriteLine($"[ACCEPTANCE] Follow-up ping after reload: {ping2 / 1000.0:F2}ms");
    }
}
