// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ASPIRE.Common.Resilience;

/// <summary>
///     Lightweight, zero-allocation asynchronous alert dispatcher for circuit breaker state transitions.
///     Supports Discord Rich Webhooks and structured console/trace logging with debounce protection.
/// </summary>
public static class NexusCircuitBreakerAlertDispatcher
{
    private static readonly HttpClient HttpClient = ASPIRE.Common.Http.SharedSocketsHttpClientFactory.Create(TimeSpan.FromSeconds(3));

    private static readonly ConcurrentDictionary<string, DateTimeOffset> LastAlertSentUtc = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan DefaultDebounceWindow = TimeSpan.FromSeconds(15);
    private static string? _explicitWebhookUrl;

    /// <summary>
    ///     Optionally configures or overrides the webhook URL programmatically.
    /// </summary>
    public static void ConfigureWebhookUrl(string? webhookUrl)
    {
        _explicitWebhookUrl = webhookUrl;
    }

    /// <summary>
    ///     Resolves the active alert webhook URL from configuration or environment variables.
    /// </summary>
    public static string? GetActiveWebhookUrl()
    {
        if (!string.IsNullOrWhiteSpace(_explicitWebhookUrl))
            return _explicitWebhookUrl;

        return Environment.GetEnvironmentVariable("DISCORD_ALERT_WEBHOOK_URL") ??
               Environment.GetEnvironmentVariable("ALERT_WEBHOOK_URL");
    }

    /// <summary>
    ///     Dispatches an asynchronous, non-blocking notification when a circuit breaker opens.
    /// </summary>
    public static void DispatchOpenAlert(string pipelineName, TimeSpan breakDuration, string? reason = null)
    {
        DateTimeOffset now = TimeProvider.System.GetUtcNow();

        if (LastAlertSentUtc.TryGetValue(pipelineName, out DateTimeOffset lastSent) && (now - lastSent) < DefaultDebounceWindow)
        {
            Trace.WriteLine($"[RESILIENCE-ALERT] Webhook suppressed for '{pipelineName}' due to debounce window ({DefaultDebounceWindow.TotalSeconds}s).");
            return;
        }

        LastAlertSentUtc[pipelineName] = now;

        string? webhookUrl = GetActiveWebhookUrl();
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            Trace.WriteLine($"[RESILIENCE-ALERT] No webhook URL configured. Pipeline '{pipelineName}' trip logged locally.");
            return;
        }

        // Fire-and-forget background dispatch
        _ = Task.Run(async () =>
        {
            try
            {
                byte[] jsonBytes = BuildOpenAlertPayload(pipelineName, breakDuration, reason, now);
                using ByteArrayContent content = new(jsonBytes);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

                using HttpResponseMessage response = await HttpClient.PostAsync(webhookUrl, content);
                Trace.WriteLine($"[RESILIENCE-ALERT] Webhook dispatched to Discord for '{pipelineName}'. Status: {response.StatusCode}");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[RESILIENCE-ALERT] Failed to dispatch webhook alert for '{pipelineName}': {ex.Message}");
            }
        });
    }

    /// <summary>
    ///     Dispatches an asynchronous, non-blocking notification when a circuit breaker recovers and closes.
    /// </summary>
    public static void DispatchClosedAlert(string pipelineName)
    {
        string? webhookUrl = GetActiveWebhookUrl();
        if (string.IsNullOrWhiteSpace(webhookUrl)) return;

        DateTimeOffset now = TimeProvider.System.GetUtcNow();

        _ = Task.Run(async () =>
        {
            try
            {
                byte[] jsonBytes = BuildClosedAlertPayload(pipelineName, now);
                using ByteArrayContent content = new(jsonBytes);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

                using HttpResponseMessage response = await HttpClient.PostAsync(webhookUrl, content);
                Trace.WriteLine($"[RESILIENCE-ALERT] Recovery webhook dispatched to Discord for '{pipelineName}'. Status: {response.StatusCode}");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[RESILIENCE-ALERT] Failed to dispatch recovery webhook for '{pipelineName}': {ex.Message}");
            }
        });
    }

    private static byte[] BuildOpenAlertPayload(string pipelineName, TimeSpan breakDuration, string? reason, DateTimeOffset timestamp)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("content", $"⚠️ **Circuit Breaker Tripped**: `{pipelineName}`");
            writer.WriteStartArray("embeds");
            writer.WriteStartObject();
            writer.WriteString("title", "🚨 Resilience Pipeline Tripped");
            writer.WriteString("description", $"Downstream failures detected on pipeline `{pipelineName}`. Circuit is now **OPEN**.");
            writer.WriteNumber("color", 15158332); // Red / Orange

            writer.WriteStartArray("fields");
            WriteEmbedField(writer, "Pipeline", pipelineName, true);
            WriteEmbedField(writer, "Break Duration", $"{breakDuration.TotalSeconds:F1}s", true);
            WriteEmbedField(writer, "Timestamp (UTC)", timestamp.ToString("yyyy-MM-dd HH:mm:ss"), true);
            WriteEmbedField(writer, "Failure Cause", string.IsNullOrWhiteSpace(reason) ? "Downstream Exception / Timeout" : reason, false);
            WriteEmbedField(writer, "Pyroscope Profiler", "Inspect http://localhost:4040 around incident timestamp", false);
            writer.WriteEndArray();

            writer.WriteStartObject("footer");
            writer.WriteString("text", "ProjectKongor Telemetry • NEXUS.Resilience");
            writer.WriteEndObject();

            writer.WriteEndObject(); // embed
            writer.WriteEndArray(); // embeds
            writer.WriteEndObject(); // root
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] BuildClosedAlertPayload(string pipelineName, DateTimeOffset timestamp)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("content", $"✅ **Circuit Breaker Recovered**: `{pipelineName}`");
            writer.WriteStartArray("embeds");
            writer.WriteStartObject();
            writer.WriteString("title", "✅ Resilience Pipeline Recovered");
            writer.WriteString("description", $"Downstream service has recovered and circuit breaker `{pipelineName}` is now **CLOSED**.");
            writer.WriteNumber("color", 3066993); // Green

            writer.WriteStartArray("fields");
            WriteEmbedField(writer, "Pipeline", pipelineName, true);
            WriteEmbedField(writer, "Status", "Healthy / Closed", true);
            WriteEmbedField(writer, "Timestamp (UTC)", timestamp.ToString("yyyy-MM-dd HH:mm:ss"), true);
            writer.WriteEndArray();

            writer.WriteStartObject("footer");
            writer.WriteString("text", "ProjectKongor Telemetry • NEXUS.Resilience");
            writer.WriteEndObject();

            writer.WriteEndObject(); // embed
            writer.WriteEndArray(); // embeds
            writer.WriteEndObject(); // root
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteEmbedField(Utf8JsonWriter writer, string name, string value, bool inline)
    {
        writer.WriteStartObject();
        writer.WriteString("name", name);
        writer.WriteString("value", value);
        writer.WriteBoolean("inline", inline);
        writer.WriteEndObject();
    }

    /// <summary>
    ///     Resets debounce timers (primarily for unit testing).
    /// </summary>
    public static void Reset()
    {
        LastAlertSentUtc.Clear();
        _explicitWebhookUrl = null;
    }
}
