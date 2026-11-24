// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json.Serialization;

namespace NEXUS.Contracts.ControlPlane;

/// <summary>
/// Individual captured log line emitted by a dedicated game server or recorded by operator interaction.
/// </summary>
public sealed class CompelDedicatedServerLogLineDto
{
    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; } = TimeProvider.System.GetUtcNow();

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("isError")]
    public bool IsError { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = "server";
}

/// <summary>
/// Response containing recent captured console and event logs for an individual dedicated game server.
/// </summary>
public sealed class CompelDedicatedServerLogsResponseDto
{
    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("serverName")]
    public string ServerName { get; set; } = string.Empty;

    [JsonPropertyName("logs")]
    public List<CompelDedicatedServerLogLineDto> Logs { get; set; } = [];
}

/// <summary>
/// Command execution request targeting an individual dedicated server worker.
/// </summary>
public sealed class CompelExecuteWorkerCommandRequestDto
{
    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;
}

/// <summary>
/// Result of piping a command to an individual dedicated server worker.
/// </summary>
public sealed class CompelExecuteWorkerCommandResponseDto
{
    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = "none";

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}
