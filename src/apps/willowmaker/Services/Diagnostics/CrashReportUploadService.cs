// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: CrashReportUploadService.cs
// System: WILLOWMAKER - Diagnostics & Privacy Firewall
// Subsystem: Opt-In PII-Scrubbed Crash Report Ingestion & GlitchTip/API Gateway
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ASPIRE.Common.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Telemetry;

namespace WILLOWMAKER.Core.Services.Diagnostics;

public sealed class CrashReportPayloadDto
{
    public string TimestampUtc { get; set; } = string.Empty;
    public string BuildId { get; set; } = string.Empty;
    public string OperatingSystem { get; set; } = string.Empty;
    public string FaultingModule { get; set; } = string.Empty;
    public string ExceptionCode { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public CrashTelemetryBreadcrumbDto? TelemetryBreadcrumb { get; set; }
    public string? ScrubbedMinidumpBase64 { get; set; }
    public int ScrubbedMinidumpSizeBytes { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.Ordinal);
}

public sealed class CrashTelemetryBreadcrumbDto
{
    public ulong FrameIndex { get; set; }
    public float CpuFrameTimeMs { get; set; }
    public float GpuFrameTimeMs { get; set; }
    public uint MemoryWorkingSetMb { get; set; }
    public uint ActiveEntities { get; set; }
    public uint ServerTick { get; set; }
}

public sealed class CrashReportResponseDto
{
    public bool Accepted { get; set; }
    public string ReportId { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(CrashReportPayloadDto))]
[JsonSerializable(typeof(CrashReportResponseDto))]
internal sealed partial class CrashReportJsonSerializerContext : JsonSerializerContext
{
}

public sealed record CrashReportUploadResult(
    bool Success,
    bool Skipped,
    string? ReportId,
    string? Fingerprint,
    string? ErrorMessage);

public sealed class CrashReportUploadService
{
    public const string DefaultEndpointUrl = "http://localhost:55555/api/v1/diagnostics/crash-reports";
    public const int MaxMinidumpUploadSizeBytes = 2 * 1024 * 1024; // 2 MiB hard cap on sanitized minidump slice

    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CrashReportUploadService> _logger;

    public CrashReportUploadService(
        HttpClient? httpClient = null,
        TimeProvider? timeProvider = null,
        ILogger<CrashReportUploadService>? logger = null)
    {
        _httpClient = httpClient ?? SharedSocketsHttpClientFactory.Create(TimeSpan.FromSeconds(15));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<CrashReportUploadService>.Instance;
    }

    /// <summary>
    /// Prepares, privacy-scrubs, and uploads a crash report to the central diagnostics endpoint.
    /// Strictly requires explicit user opt-in (<paramref name="isCrashUploadOptedIn"/>).
    /// </summary>
    public async Task<CrashReportUploadResult> UploadCrashReportAsync(
        bool isCrashUploadOptedIn,
        string buildId,
        string faultingModule,
        string exceptionCode,
        byte[]? rawMinidumpBytes = null,
        FrameTelemetryRecord? lastFrameBreadcrumb = null,
        IDictionary<string, string>? additionalMetadata = null,
        string endpointUrl = DefaultEndpointUrl,
        CancellationToken cancellationToken = default)
    {
        if (!isCrashUploadOptedIn)
        {
            _logger.LogInformation("[CrashReportUpload] Crash report upload skipped: player has not opted into telemetry.");
            return new CrashReportUploadResult(
                Success: false,
                Skipped: true,
                ReportId: null,
                Fingerprint: null,
                ErrorMessage: "Crash upload skipped: player has not opted in.");
        }

        try
        {
            byte[]? scrubbedBytes = null;
            string fingerprint = string.Empty;

            if (rawMinidumpBytes != null && rawMinidumpBytes.Length > 0)
            {
                byte[] bufferToSanitize = (byte[])rawMinidumpBytes.Clone();
                PrivacyScrubContext scrubContext = new(
                    faultingModuleName: faultingModule,
                    strictZeroPiiBundleMode: true);

                MinidumpSanitizeResult sanitizeResult = MinidumpPrivacySanitizer.SanitizeInPlace(
                    bufferToSanitize,
                    scrubContext);

                if (!sanitizeResult.AllowedForExport)
                {
                    _logger.LogWarning(
                        "[CrashReportUpload] Minidump failed privacy sanitization ({StatusCode}: {Reason}); rejecting raw minidump upload.",
                        sanitizeResult.StatusCode, sanitizeResult.Reason);
                    return new CrashReportUploadResult(
                        Success: false,
                        Skipped: false,
                        ReportId: null,
                        Fingerprint: null,
                        ErrorMessage: $"Minidump failed privacy sanitization: {sanitizeResult.Reason}");
                }

                if (bufferToSanitize.Length > MaxMinidumpUploadSizeBytes)
                {
                    _logger.LogWarning(
                        "[CrashReportUpload] Sanitized minidump ({Size} bytes) exceeds {Cap} byte cap; rejecting.",
                        bufferToSanitize.Length, MaxMinidumpUploadSizeBytes);
                    return new CrashReportUploadResult(
                        Success: false,
                        Skipped: false,
                        ReportId: null,
                        Fingerprint: null,
                        ErrorMessage: $"Sanitized minidump exceeds {MaxMinidumpUploadSizeBytes} byte cap.");
                }

                scrubbedBytes = bufferToSanitize;
                byte[] dumpHash = SHA256.HashData(scrubbedBytes);
                fingerprint = Convert.ToHexString(dumpHash);
            }
            else
            {
                byte[] sigHash = SHA256.HashData(Encoding.UTF8.GetBytes($"{buildId}|{faultingModule}|{exceptionCode}"));
                fingerprint = Convert.ToHexString(sigHash);
            }

            CrashReportPayloadDto payload = new()
            {
                TimestampUtc = _timeProvider.GetUtcNow().ToString("O"),
                BuildId = buildId ?? string.Empty,
                OperatingSystem = RuntimeInformation.OSDescription,
                FaultingModule = faultingModule ?? string.Empty,
                ExceptionCode = exceptionCode ?? string.Empty,
                Fingerprint = fingerprint,
                ScrubbedMinidumpBase64 = scrubbedBytes != null ? Convert.ToBase64String(scrubbedBytes) : null,
                ScrubbedMinidumpSizeBytes = scrubbedBytes?.Length ?? 0
            };

            if (lastFrameBreadcrumb.HasValue)
            {
                FrameTelemetryRecord rec = lastFrameBreadcrumb.Value;
                payload.TelemetryBreadcrumb = new CrashTelemetryBreadcrumbDto
                {
                    FrameIndex = rec.FrameIndex,
                    CpuFrameTimeMs = rec.CpuFrameTimeMs,
                    GpuFrameTimeMs = rec.GpuFrameTimeMs,
                    MemoryWorkingSetMb = rec.MemoryWorkingSetMb,
                    ActiveEntities = rec.ActiveEntities,
                    ServerTick = rec.ServerTick
                };
            }

            if (additionalMetadata != null)
            {
                foreach (var (k, v) in additionalMetadata)
                {
                    payload.Metadata[k] = v;
                }
            }

            string json = JsonSerializer.Serialize(payload, CrashReportJsonSerializerContext.Default.CrashReportPayloadDto);
            using StringContent requestContent = new(json, Encoding.UTF8, "application/json");

            _logger.LogInformation(
                "[CrashReportUpload] Dispatching PII-scrubbed crash report to {Endpoint} (Fingerprint={Fingerprint}, MinidumpBytes={Bytes}).",
                endpointUrl, fingerprint, payload.ScrubbedMinidumpSizeBytes);

            HttpResponseMessage response = await _httpClient.PostAsync(endpointUrl, requestContent, cancellationToken);
            string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "[CrashReportUpload] Diagnostics endpoint returned {StatusCode}: {Body}",
                    response.StatusCode, responseBody);
                return new CrashReportUploadResult(
                    Success: false,
                    Skipped: false,
                    ReportId: null,
                    Fingerprint: fingerprint,
                    ErrorMessage: $"HTTP {(int)response.StatusCode}: {responseBody}");
            }

            CrashReportResponseDto? responseDto = null;
            try
            {
                responseDto = JsonSerializer.Deserialize(responseBody, CrashReportJsonSerializerContext.Default.CrashReportResponseDto);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[CrashReportUpload] Failed to parse response JSON from diagnostics endpoint.");
            }

            string reportId = responseDto?.ReportId ?? Guid.NewGuid().ToString("N");
            _logger.LogInformation(
                "[CrashReportUpload] Crash report accepted successfully. ReportId={ReportId}, Fingerprint={Fingerprint}.",
                reportId, fingerprint);

            return new CrashReportUploadResult(
                Success: true,
                Skipped: false,
                ReportId: reportId,
                Fingerprint: responseDto?.Fingerprint ?? fingerprint,
                ErrorMessage: null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CrashReportUpload] Unexpected error dispatching crash report to {Endpoint}.", endpointUrl);
            return new CrashReportUploadResult(
                Success: false,
                Skipped: false,
                ReportId: null,
                Fingerprint: null,
                ErrorMessage: ex.Message);
        }
    }
}
