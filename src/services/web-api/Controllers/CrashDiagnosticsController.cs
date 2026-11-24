// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: CrashDiagnosticsController.cs
// System: ZORGATH.WebPortal.API - Diagnostics & Telemetry Intake
// Subsystem: PII-Scrubbed Crash Report Intake & Deterministic Triage Gateway
// ============================================================================

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ZORGATH.WebPortal.API.Infrastructure.Attributes;

namespace ZORGATH.WebPortal.API.Controllers;

public sealed class IncomingCrashReportDto
{
    public string TimestampUtc { get; set; } = string.Empty;
    public string BuildId { get; set; } = string.Empty;
    public string OperatingSystem { get; set; } = string.Empty;
    public string FaultingModule { get; set; } = string.Empty;
    public string ExceptionCode { get; set; } = string.Empty;
    public string? Fingerprint { get; set; }
    public IncomingTelemetryBreadcrumbDto? TelemetryBreadcrumb { get; set; }
    public string? ScrubbedMinidumpBase64 { get; set; }
    public int ScrubbedMinidumpSizeBytes { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.Ordinal);
}

public sealed class IncomingTelemetryBreadcrumbDto
{
    public ulong FrameIndex { get; set; }
    public float CpuFrameTimeMs { get; set; }
    public float GpuFrameTimeMs { get; set; }
    public uint MemoryWorkingSetMb { get; set; }
    public uint ActiveEntities { get; set; }
    public uint ServerTick { get; set; }
}

public sealed class CrashDiagnosticsIntakeResponseDto
{
    public bool Accepted { get; set; }
    public string ReportId { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Receives and triages opt-in, PII-scrubbed minidumps and crash breadcrumbs from WILLOWMAKER clients.
/// </summary>
[ApiController]
[Route("api/v1/diagnostics")]
[Produces("application/json")]
[Consumes("application/json")]
[EnableRateLimiting(RateLimiterPolicies.Relaxed)]
public class CrashDiagnosticsController : ControllerBase
{
    public const int MaxMinidumpUploadSizeBytes = 2 * 1024 * 1024; // 2 MiB bound
    public const int MaxBase64StringLength = 3 * 1024 * 1024; // ~3 MiB Base64 length limit

    private readonly ILogger<CrashDiagnosticsController> _logger;

    public CrashDiagnosticsController(ILogger<CrashDiagnosticsController>? logger = null)
    {
        _logger = logger ?? NullLogger<CrashDiagnosticsController>.Instance;
    }

    /// <summary>
    /// Accepts a sanitized crash report payload, validates size and PII boundaries, and issues a triage identifier.
    /// </summary>
    [HttpPost("crash-reports")]
    [ProducesResponseType(typeof(CrashDiagnosticsIntakeResponseDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult IngestCrashReport([FromBody] IncomingCrashReportDto? report)
    {
        if (report == null)
        {
            _logger.LogWarning("[CRASH-INTAKE] Rejected null crash report payload.");
            return BadRequest("Crash report payload cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(report.BuildId) || string.IsNullOrWhiteSpace(report.TimestampUtc))
        {
            _logger.LogWarning("[CRASH-INTAKE] Rejected crash report with missing BuildId or TimestampUtc.");
            return BadRequest("Missing required crash metadata (BuildId, TimestampUtc).");
        }

        if (report.ScrubbedMinidumpSizeBytes > MaxMinidumpUploadSizeBytes)
        {
            _logger.LogWarning(
                "[CRASH-INTAKE] Rejected oversized minidump ({Size} > {Limit} bytes).",
                report.ScrubbedMinidumpSizeBytes, MaxMinidumpUploadSizeBytes);
            return BadRequest($"Scrubbed minidump exceeds {MaxMinidumpUploadSizeBytes} byte cap.");
        }

        if (!string.IsNullOrEmpty(report.ScrubbedMinidumpBase64) && report.ScrubbedMinidumpBase64.Length > MaxBase64StringLength)
        {
            _logger.LogWarning(
                "[CRASH-INTAKE] Rejected oversized Base64 minidump payload ({Length} characters).",
                report.ScrubbedMinidumpBase64.Length);
            return BadRequest("Base64 minidump payload exceeds size limit.");
        }

        // Zero-PII boundary guard: reject workstation file paths
        if (ContainsWorkstationPath(report.FaultingModule))
        {
            _logger.LogWarning("[CRASH-INTAKE] Rejected payload with personal path in FaultingModule.");
            return BadRequest("Crash report contains unscrubbed workstation paths (PII firewall violation).");
        }

        foreach (var (k, v) in report.Metadata)
        {
            if (ContainsWorkstationPath(k) || ContainsWorkstationPath(v))
            {
                _logger.LogWarning("[CRASH-INTAKE] Rejected payload with personal path in metadata key '{Key}'.", k);
                return BadRequest("Crash report contains unscrubbed workstation paths (PII firewall violation).");
            }
        }

        string fingerprint = !string.IsNullOrWhiteSpace(report.Fingerprint)
            ? report.Fingerprint
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{report.BuildId}|{report.FaultingModule}|{report.ExceptionCode}|{report.ScrubbedMinidumpSizeBytes}")));

        string reportId = "CR-" + fingerprint[..Math.Min(16, fingerprint.Length)].ToLowerInvariant() + "-" +
                          Math.Abs(report.TimestampUtc.GetHashCode()).ToString("x8");

        _logger.LogInformation(
            "[CRASH-INTAKE] Ingested crash report {ReportId} for {BuildId} in {Module} (Code={Code}, Size={Size}B, Fingerprint={Fingerprint})",
            reportId, report.BuildId, report.FaultingModule, report.ExceptionCode, report.ScrubbedMinidumpSizeBytes, fingerprint);

        CrashDiagnosticsIntakeResponseDto response = new()
        {
            Accepted = true,
            ReportId = reportId,
            Fingerprint = fingerprint,
            Message = "Crash report accepted for diagnostics triage."
        };

        return StatusCode(StatusCodes.Status202Accepted, response);
    }

    private static bool ContainsWorkstationPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return value.Contains(@"\Users\", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("/Users/", StringComparison.OrdinalIgnoreCase) ||
               value.Contains(@"/home/", StringComparison.OrdinalIgnoreCase) ||
               value.Contains(@"\home\", StringComparison.OrdinalIgnoreCase);
    }
}
