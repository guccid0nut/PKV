// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kongor.Mcp.E2E.Tests.Client;

public sealed class JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public object? Id { get; set; }

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("params")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Params { get; set; }
}

public sealed class JsonRpcResponse
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public object? Id { get; set; }

    [JsonPropertyName("result")]
    public JsonElement? Result { get; set; }

    [JsonPropertyName("error")]
    public JsonRpcError? Error { get; set; }
}

public sealed class JsonRpcError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public JsonElement? Data { get; set; }
}

public sealed class InitializeParams
{
    [JsonPropertyName("protocolVersion")]
    public string ProtocolVersion { get; set; } = "2024-11-05";

    [JsonPropertyName("capabilities")]
    public ClientCapabilities Capabilities { get; set; } = new();

    [JsonPropertyName("clientInfo")]
    public ClientInfo ClientInfo { get; set; } = new();
}

public sealed class ClientCapabilities
{
    [JsonPropertyName("roots")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Roots { get; set; }

    [JsonPropertyName("sampling")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Sampling { get; set; }
}

public sealed class ClientInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "Kongor.Mcp.E2E.Tests";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "5.0.0";
}

public sealed class InitializeResult
{
    [JsonPropertyName("protocolVersion")]
    public string? ProtocolVersion { get; set; }

    [JsonPropertyName("capabilities")]
    public ServerCapabilities? Capabilities { get; set; }

    [JsonPropertyName("serverInfo")]
    public ServerInfo? ServerInfo { get; set; }
}

public sealed class ServerCapabilities
{
    [JsonPropertyName("tools")]
    public JsonElement? Tools { get; set; }

    [JsonPropertyName("prompts")]
    public JsonElement? Prompts { get; set; }

    [JsonPropertyName("resources")]
    public JsonElement? Resources { get; set; }

    [JsonPropertyName("logging")]
    public JsonElement? Logging { get; set; }
}

public sealed class ServerInfo
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }
}

public sealed class ToolsListResult
{
    [JsonPropertyName("tools")]
    public List<ToolDefinition> Tools { get; set; } = [];
}

public sealed class ToolDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("inputSchema")]
    public JsonElement? InputSchema { get; set; }
}

public sealed class ToolCallParams
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    public object? Arguments { get; set; }
}

public sealed class ToolCallResult
{
    [JsonPropertyName("content")]
    public List<ContentBlock> Content { get; set; } = [];

    [JsonPropertyName("isError")]
    public bool IsError { get; set; }

    public string? GetFirstText() => Content.FirstOrDefault(c => c.Type == "text")?.Text;
    public string? GetFirstImageData() => Content.FirstOrDefault(c => c.Type == "image")?.Data;
}

public sealed class ContentBlock
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("data")]
    public string? Data { get; set; }

    [JsonPropertyName("mimeType")]
    public string? MimeType { get; set; }

    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

// -------------------------------------------------------------------------
// Tool 1: kongor_server_manage Models
// -------------------------------------------------------------------------
public sealed class ServerManageArgs
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "status";

    [JsonPropertyName("target")]
    public string Target { get; set; } = "cluster";

    [JsonPropertyName("withGameServers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? WithGameServers { get; set; }
}

public sealed class ServerManageResult
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("action")]
    public string? Action { get; set; }

    [JsonPropertyName("target")]
    public string? Target { get; set; }

    [JsonPropertyName("clusterState")]
    public string? ClusterState { get; set; }

    [JsonPropertyName("dashboardUrl")]
    public string? DashboardUrl { get; set; }

    [JsonPropertyName("services")]
    public Dictionary<string, ServiceStatusDto>? Services { get; set; }
}

public sealed class ServiceStatusDto
{
    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("processId")]
    public int ProcessId { get; set; }
}

// -------------------------------------------------------------------------
// Tool 2: kongor_game_manage Models
// -------------------------------------------------------------------------
public sealed class GameManageArgs
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "status";

    [JsonPropertyName("mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mode { get; set; }

    [JsonPropertyName("connectAddress")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConnectAddress { get; set; }

    [JsonPropertyName("resolution")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Resolution { get; set; }

    [JsonPropertyName("fullscreen")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Fullscreen { get; set; }

    [JsonPropertyName("masterServer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MasterServer { get; set; }

    [JsonPropertyName("customClientPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CustomClientPath { get; set; }
}

public sealed class GameManageResult
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("action")]
    public string? Action { get; set; }

    [JsonPropertyName("running")]
    public bool Running { get; set; }

    [JsonPropertyName("processId")]
    public int ProcessId { get; set; }

    [JsonPropertyName("mainWindowHandle")]
    public string? MainWindowHandle { get; set; }

    [JsonPropertyName("executable")]
    public string? Executable { get; set; }

    [JsonPropertyName("arguments")]
    public List<string>? Arguments { get; set; }
}

// -------------------------------------------------------------------------
// Tool 3: kongor_game_command Models
// -------------------------------------------------------------------------
public sealed class GameCommandArgs
{
    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("timeoutMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TimeoutMs { get; set; }
}

public sealed class GameCommandResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("command")]
    public string? Command { get; set; }

    [JsonPropertyName("response")]
    public string? Response { get; set; }

    [JsonPropertyName("executionTimeMs")]
    public double ExecutionTimeMs { get; set; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }
}

// -------------------------------------------------------------------------
// Tool 4: kongor_game_telemetry Models
// -------------------------------------------------------------------------
public sealed class GameTelemetryArgs
{
    [JsonPropertyName("timeoutMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TimeoutMs { get; set; }
}

public sealed class GameTelemetryResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("connected")]
    public bool Connected { get; set; }

    [JsonPropertyName("telemetry")]
    public GameTelemetrySnapshot? Telemetry { get; set; }
}

public sealed class GameTelemetrySnapshot
{
    [JsonPropertyName("currentFps")]
    public double CurrentFps { get; set; }

    [JsonPropertyName("memoryWorkingSetBytes")]
    public long MemoryWorkingSetBytes { get; set; }

    [JsonPropertyName("memoryWorkingSetMb")]
    public double MemoryWorkingSetMb { get; set; }

    [JsonPropertyName("activeMatchId")]
    public long ActiveMatchId { get; set; }

    [JsonPropertyName("currentMap")]
    public string? CurrentMap { get; set; }

    [JsonPropertyName("playerName")]
    public string? PlayerName { get; set; }

    [JsonPropertyName("connectedPlayers")]
    public int ConnectedPlayers { get; set; }

    [JsonPropertyName("uptimeSeconds")]
    public double UptimeSeconds { get; set; }
}

// -------------------------------------------------------------------------
// Tool 5: kongor_capture_window Models
// -------------------------------------------------------------------------
public sealed class CaptureWindowArgs
{
    [JsonPropertyName("target")]
    public string Target { get; set; } = "active";

    [JsonPropertyName("capture_method")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CaptureMethod { get; set; }

    [JsonPropertyName("save_artifact")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? SaveArtifact { get; set; }

    [JsonPropertyName("custom_output_path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CustomOutputPath { get; set; }
}

public sealed class CaptureWindowResult
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("target")]
    public string? Target { get; set; }

    [JsonPropertyName("windowTitle")]
    public string? WindowTitle { get; set; }

    [JsonPropertyName("hwnd")]
    public string? Hwnd { get; set; }

    [JsonPropertyName("pid")]
    public int Pid { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("artifactPath")]
    public string? ArtifactPath { get; set; }

    [JsonPropertyName("artifactUri")]
    public string? ArtifactUri { get; set; }

    [JsonPropertyName("base64Png")]
    public string? Base64Png { get; set; }

    [JsonPropertyName("base64Omitted")]
    public bool Base64Omitted { get; set; }
}

// -------------------------------------------------------------------------
// Tool 6: kongor_ui_render_view Models
// -------------------------------------------------------------------------
public sealed class UiRenderViewArgs
{
    [JsonPropertyName("view_name")]
    public string ViewName { get; set; } = string.Empty;

    [JsonPropertyName("width")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Width { get; set; }

    [JsonPropertyName("height")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Height { get; set; }

    [JsonPropertyName("theme")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Theme { get; set; }

    [JsonPropertyName("mock_data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? MockData { get; set; }

    [JsonPropertyName("save_artifact")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? SaveArtifact { get; set; }

    [JsonPropertyName("custom_output_path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CustomOutputPath { get; set; }
}

public sealed class UiRenderViewResult
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("view_name")]
    public string? ViewName { get; set; }

    [JsonPropertyName("resolvedType")]
    public string? ResolvedType { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("theme")]
    public string? Theme { get; set; }

    [JsonPropertyName("artifactPath")]
    public string? ArtifactPath { get; set; }

    [JsonPropertyName("artifactUri")]
    public string? ArtifactUri { get; set; }

    [JsonPropertyName("base64Png")]
    public string? Base64Png { get; set; }

    [JsonPropertyName("base64Omitted")]
    public bool Base64Omitted { get; set; }
}
