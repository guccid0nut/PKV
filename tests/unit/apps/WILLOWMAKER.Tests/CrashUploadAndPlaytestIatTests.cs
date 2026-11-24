// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Kongor.Core.Services.Native;
using NEXUS.Contracts.Telemetry;
using WILLOWMAKER.Core.Services.Diagnostics;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class CrashUploadAndPlaytestIatTests
{
    private sealed class MockDiagnosticsHttpMessageHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        public HttpStatusCode ResponseStatusCode { get; set; } = HttpStatusCode.Accepted;
        public string ResponseContent { get; set; } = "{\"accepted\":true,\"reportId\":\"CR-12345678-abcdef\",\"fingerprint\":\"TESTFINGERPRINT\",\"message\":\"Accepted\"}";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequest = request;
            if (request.Content != null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return new HttpResponseMessage(ResponseStatusCode)
            {
                Content = new StringContent(ResponseContent, Encoding.UTF8, "application/json")
            };
        }
    }

    private static byte[] CreateValidDummyMinidump()
    {
        byte[] buffer = new byte[256];
        const ulong stackStartAddr = 0x0000001000000000UL;
        const uint stackDataSize = 64u;
        const uint stackDataRva = 192u;

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), 0x504D444Du); // "MDMP"
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4, 4), 0x0000A793u); // Version
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8, 4), 3u);          // StreamCount = 3 (3, 5, 15)
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12, 4), 32u);        // StreamDirRva
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(16, 4), 0u);         // Checksum
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(20, 4), 0u);         // TimeDateStamp
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(24, 8), 0UL);        // Flags

        // [0] StreamThreadList (3) at RVA 68 (52 bytes -> 68..119)
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(32, 4), 3u);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(36, 4), 52u);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(40, 4), 68u);

        // [1] StreamMemoryList (5) at RVA 120 (20 bytes -> 120..139)
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(44, 4), 5u);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(48, 4), 20u);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(52, 4), 120u);

        // [2] StreamMiscInfo (15) at RVA 140 (24 bytes -> 140..163)
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(56, 4), 15u);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(60, 4), 24u);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(64, 4), 140u);

        // ThreadListStream payload at offset 68
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(68, 4), 1u);         // NumberOfThreads = 1
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(72, 4), 4242u);      // ThreadId
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(96, 8), stackStartAddr);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(104, 4), stackDataSize);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(108, 4), stackDataRva);

        // MemoryListStream payload at offset 120
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(120, 4), 1u);        // NumberOfMemoryRanges = 1
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(124, 8), stackStartAddr);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(132, 4), stackDataSize);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(136, 4), stackDataRva);

        // MiscInfo payload at offset 140 (24 bytes)
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(140, 4), 24u);       // Size
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(144, 4), 0x03u);     // Flags1
        return buffer;
    }

    [Fact]
    public async Task UploadCrashReport_WhenNotOptedIn_SendsZeroHttpRequests()
    {
        var handler = new MockDiagnosticsHttpMessageHandler();
        using var client = new HttpClient(handler);
        var service = new CrashReportUploadService(client);

        var result = await service.UploadCrashReportAsync(
            isCrashUploadOptedIn: false,
            buildId: "PKV-2026.11.10",
            faultingModule: "k2_x64.dll",
            exceptionCode: "0xC0000005");

        result.Success.Should().BeFalse();
        result.Skipped.Should().BeTrue();
        result.ReportId.Should().BeNull();
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task UploadCrashReport_WhenOptedIn_SanitizesMinidumpAndSendsBreadcrumb()
    {
        var handler = new MockDiagnosticsHttpMessageHandler();
        using var client = new HttpClient(handler);
        var service = new CrashReportUploadService(client);

        byte[] rawDump = CreateValidDummyMinidump();
        FrameTelemetryRecord breadcrumb = new()
        {
            FrameIndex = 98765UL,
            CpuFrameTimeMs = 4.25f,
            GpuFrameTimeMs = 5.10f,
            MemoryWorkingSetMb = 768U,
            ActiveEntities = 120U,
            ServerTick = 54321U
        };

        var result = await service.UploadCrashReportAsync(
            isCrashUploadOptedIn: true,
            buildId: "PKV-2026.11.10",
            faultingModule: "k2_x64.dll",
            exceptionCode: "0xC0000005",
            rawMinidumpBytes: rawDump,
            lastFrameBreadcrumb: breadcrumb);

        result.Success.Should().BeTrue();
        result.Skipped.Should().BeFalse();
        result.ReportId.Should().NotBeNullOrWhiteSpace();
        handler.RequestCount.Should().Be(1);

        handler.LastRequestBody.Should().NotBeNullOrWhiteSpace();
        using var jsonDoc = JsonDocument.Parse(handler.LastRequestBody!);
        var root = jsonDoc.RootElement;

        root.GetProperty("buildId").GetString().Should().Be("PKV-2026.11.10");
        root.GetProperty("faultingModule").GetString().Should().Be("k2_x64.dll");
        root.GetProperty("exceptionCode").GetString().Should().Be("0xC0000005");
        root.GetProperty("scrubbedMinidumpSizeBytes").GetInt32().Should().Be(rawDump.Length);

        var breadcrumbElem = root.GetProperty("telemetryBreadcrumb");
        breadcrumbElem.GetProperty("frameIndex").GetUInt64().Should().Be(98765UL);
        breadcrumbElem.GetProperty("cpuFrameTimeMs").GetSingle().Should().Be(4.25f);
        breadcrumbElem.GetProperty("gpuFrameTimeMs").GetSingle().Should().Be(5.10f);
        breadcrumbElem.GetProperty("memoryWorkingSetMb").GetUInt32().Should().Be(768U);
        breadcrumbElem.GetProperty("activeEntities").GetUInt32().Should().Be(120U);
        breadcrumbElem.GetProperty("serverTick").GetUInt32().Should().Be(54321U);
    }

    [Fact]
    public async Task UploadCrashReport_WhenServerError_Handles500Gracefully()
    {
        var handler = new MockDiagnosticsHttpMessageHandler
        {
            ResponseStatusCode = HttpStatusCode.InternalServerError,
            ResponseContent = "Database connection pool exhausted"
        };
        using var client = new HttpClient(handler);
        var service = new CrashReportUploadService(client);

        var result = await service.UploadCrashReportAsync(
            isCrashUploadOptedIn: true,
            buildId: "PKV-2026.11.10",
            faultingModule: "cgame_x64.dll",
            exceptionCode: "0xE06D7363");

        result.Success.Should().BeFalse();
        result.Skipped.Should().BeFalse();
        result.ReportId.Should().BeNull();
        result.ErrorMessage.Should().Contain("500");
        handler.RequestCount.Should().Be(1);
    }

    [Fact]
    public void NativeProcessInjectionService_SignaturesAndPlaytestIatParity_RemainCallable()
    {
        // Public API must remain non-throwing and source-compatible across both PLAYTEST_BUILD and normal builds
        var result = NativeProcessInjectionService.InjectDll(new nint(0x1234), "nonexistent.dll");
        result.Success.Should().BeFalse();

        bool patchOk = NativeProcessInjectionService.ApplyMemoryPatch(new nint(0x1234), new nint(0x10000), [0x90, 0x90], out string? err);
        patchOk.Should().BeFalse();

        nint trampoline = NativeProcessInjectionService.AllocateTrampolineWithin2Gb(new nint(0x1234), new nint(0x10000));
        trampoline.Should().Be(nint.Zero);

        var plan = NativeProcessInjectionService.PlanWhGetMessageSafeInjection(5678U);
        plan.AntiCheatSafeMode.Should().BeTrue();
        plan.HookId.Should().Be(3);
    }
}
