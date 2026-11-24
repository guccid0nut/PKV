// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;
using ZORGATH.WebPortal.API.Controllers;

namespace ZORGATH.WebPortal.API.Tests;

public sealed class CrashDiagnosticsEndpointTests
{
    private readonly CrashDiagnosticsController _controller = new();

    [Fact]
    public void IngestCrashReport_ValidPayload_Returns202AcceptedWithReportIdAndFingerprint()
    {
        var payload = new IncomingCrashReportDto
        {
            TimestampUtc = "2026-11-10T14:30:00.0000000Z",
            BuildId = "PKV-2026.11.10",
            OperatingSystem = "Microsoft Windows 11 Pro 10.0.26100",
            FaultingModule = "k2_x64.dll",
            ExceptionCode = "0xC0000005",
            Fingerprint = "ABCDEF1234567890",
            ScrubbedMinidumpSizeBytes = 65536,
            ScrubbedMinidumpBase64 = Convert.ToBase64String(new byte[128]),
            TelemetryBreadcrumb = new IncomingTelemetryBreadcrumbDto
            {
                FrameIndex = 1000UL,
                CpuFrameTimeMs = 3.5f,
                GpuFrameTimeMs = 4.0f,
                MemoryWorkingSetMb = 512U,
                ActiveEntities = 50U,
                ServerTick = 200U
            }
        };

        var result = _controller.IngestCrashReport(payload);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status202Accepted);

        var responseDto = objectResult.Value.Should().BeOfType<CrashDiagnosticsIntakeResponseDto>().Subject;
        responseDto.Accepted.Should().BeTrue();
        responseDto.Fingerprint.Should().Be("ABCDEF1234567890");
        responseDto.ReportId.Should().StartWith("CR-");
    }

    [Fact]
    public void IngestCrashReport_NullPayload_Returns400BadRequest()
    {
        var result = _controller.IngestCrashReport(null);

        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.Value.Should().Be("Crash report payload cannot be empty.");
    }

    [Theory]
    [InlineData("", "2026-11-10T14:00:00Z")]
    [InlineData("PKV-2026.11.10", "")]
    [InlineData("   ", "2026-11-10T14:00:00Z")]
    public void IngestCrashReport_MissingMetadata_Returns400BadRequest(string buildId, string timestamp)
    {
        var payload = new IncomingCrashReportDto
        {
            BuildId = buildId,
            TimestampUtc = timestamp,
            FaultingModule = "k2_x64.dll"
        };

        var result = _controller.IngestCrashReport(payload);

        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.Value.Should().Be("Missing required crash metadata (BuildId, TimestampUtc).");
    }

    [Fact]
    public void IngestCrashReport_OversizedMinidump_Returns400BadRequest()
    {
        var payload = new IncomingCrashReportDto
        {
            BuildId = "PKV-2026.11.10",
            TimestampUtc = "2026-11-10T14:00:00Z",
            FaultingModule = "k2_x64.dll",
            ScrubbedMinidumpSizeBytes = 3 * 1024 * 1024 // 3 MiB > 2 MiB cap
        };

        var result = _controller.IngestCrashReport(payload);

        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.Value.Should().Be($"Scrubbed minidump exceeds {CrashDiagnosticsController.MaxMinidumpUploadSizeBytes} byte cap.");
    }

    [Fact]
    public void IngestCrashReport_WorkstationPiiInFaultingModule_Returns400BadRequest()
    {
        var payload = new IncomingCrashReportDto
        {
            BuildId = "PKV-2026.11.10",
            TimestampUtc = "2026-11-10T14:00:00Z",
            FaultingModule = @"C:\Users\canary_dev_user\AppData\Local\Game\k2_x64.dll",
            ScrubbedMinidumpSizeBytes = 1024
        };

        var result = _controller.IngestCrashReport(payload);

        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.Value.Should().Be("Crash report contains unscrubbed workstation paths (PII firewall violation).");
    }

    [Fact]
    public void IngestCrashReport_WorkstationPiiInMetadata_Returns400BadRequest()
    {
        var payload = new IncomingCrashReportDto
        {
            BuildId = "PKV-2026.11.10",
            TimestampUtc = "2026-11-10T14:00:00Z",
            FaultingModule = "k2_x64.dll",
            ScrubbedMinidumpSizeBytes = 1024,
            Metadata = new Dictionary<string, string>
            {
                ["CustomPath"] = @"C:\Users\JohnDoe\Desktop\mod.dll"
            }
        };

        var result = _controller.IngestCrashReport(payload);

        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.Value.Should().Be("Crash report contains unscrubbed workstation paths (PII firewall violation).");
    }
}
