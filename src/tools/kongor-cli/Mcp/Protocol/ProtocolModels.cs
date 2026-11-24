// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kongor.Cli.Mcp.Protocol;

/// <summary>
/// MCP Protocol version constant.
/// </summary>
public static class McpProtocolVersion
{
    public const string Current = "2024-11-05";
    public const string Latest = "2025-06-18";

    public static readonly IReadOnlyList<string> SupportedVersions =
    [
        "2025-06-18",
        "2025-03-26",
        "2024-11-05"
    ];

    public const string ServerName = "kongor";
    public static readonly string ServerVersion = NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion;
    public const string ServerTitle = "TAO (Together As One) - KONGOR MCP";
    public const string ServerDescription = "TAO (Together As One) - KONGOR Model Context Protocol Server for Project Kongor V (created by guccidonut, AGPL-3.0-or-later, https://github.com/guccidonut/PKV).";
    public const string ServerAuthor = "guccidonut";
    public const string ServerLicense = "AGPL-3.0-or-later";
    public const string ServerRepositoryUrl = "https://github.com/guccidonut/PKV";
    public const string ServerInstructions =
        "TAO KONGOR MCP Operating Card: (1) 18 Core tools are exposed by default; use kongor_call_tool (action='search'|'describe'|'call') to discover and invoke all 98+ tools across any IDE. " +
        "(2) Client launches default to loopback masterServer='127.0.0.1:55555' and window_mode='offscreen' (1280x720); remote login requires KONGOR_MCP_ALLOW_REMOTE_LOGIN=1 and remote DB writes require KONGOR_MCP_ALLOW_REMOTE_DB_WRITE=1. " +
        "(3) On offscreen clients, use kongor_ui_control(action='invoke'|'trigger_widget') or kongor_client_navigate instead of OS mouse/keyboard input. " +
        "(4) Prefer kongor_game_client_manage over kongor_game_manage, kongor_network_chaos over kongor_network_impair_simulate, and kongor_match_state for live match state.";

    /// <summary>
    /// Negotiates the MCP wire protocol version per specification:
    /// echoes the client's requested version if supported, defaults to <see cref="Current"/> when omitted,
    /// and returns <see cref="Latest"/> when an unknown version is requested.
    /// </summary>
    public static string Negotiate(string? requestedVersion)
    {
        if (string.IsNullOrWhiteSpace(requestedVersion))
        {
            return Current;
        }

        string trimmed = requestedVersion.Trim();
        foreach (string supported in SupportedVersions)
        {
            if (string.Equals(supported, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return supported;
            }
        }

        return Latest;
    }
}

public sealed class ImplementationInfo
{
    [JsonPropertyName("author")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Author { get; set; }

    [JsonPropertyName("license")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? License { get; set; }

    [JsonPropertyName("repositoryUrl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RepositoryUrl { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }
}

public sealed class ToolsCapability
{
    [JsonPropertyName("listChanged")]
    public bool ListChanged { get; set; }
}

public sealed class LoggingCapability
{
}

public sealed class ResourceCapability
{
    [JsonPropertyName("subscribe")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Subscribe { get; set; } = true;

    [JsonPropertyName("listChanged")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ListChanged { get; set; } = false;
}

public sealed class PromptsCapability
{
    [JsonPropertyName("listChanged")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ListChanged { get; set; } = false;
}

public sealed class ServerCapabilities
{
    [JsonPropertyName("tools")]
    public ToolsCapability Tools { get; set; } = new() { ListChanged = false };

    [JsonPropertyName("logging")]
    public LoggingCapability Logging { get; set; } = new();

    [JsonPropertyName("resources")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResourceCapability? Resources { get; set; } = new();

    [JsonPropertyName("prompts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PromptsCapability? Prompts { get; set; } = new();
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

public sealed class InitializeParams
{
    [JsonPropertyName("protocolVersion")]
    public string ProtocolVersion { get; set; } = McpProtocolVersion.Current;

    [JsonPropertyName("capabilities")]
    public ClientCapabilities? Capabilities { get; set; }

    [JsonPropertyName("clientInfo")]
    public ImplementationInfo? ClientInfo { get; set; }
}

public sealed class InitializeResult
{
    [JsonPropertyName("protocolVersion")]
    public string ProtocolVersion { get; set; } = McpProtocolVersion.Current;

    [JsonPropertyName("capabilities")]
    public ServerCapabilities Capabilities { get; set; } = new();

    [JsonPropertyName("serverInfo")]
    public ImplementationInfo ServerInfo { get; set; } = new()
    {
        Name = McpProtocolVersion.ServerName,
        Version = McpProtocolVersion.ServerVersion,
        Description = McpProtocolVersion.ServerDescription
    };

    [JsonPropertyName("instructions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Instructions { get; set; }
}

public sealed class ToolPropertySchema
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "string";

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("enum")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Enum { get; set; }

    [JsonPropertyName("default")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Default { get; set; }

    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }

    [JsonPropertyName("examples")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Examples { get; set; }

    [JsonPropertyName("items")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ToolPropertySchema? Items { get; set; }

    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, ToolPropertySchema>? Properties { get; set; }

    [JsonPropertyName("required")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Required { get; set; }
}

public sealed class ToolAnnotations
{
    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }

    [JsonPropertyName("readOnlyHint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ReadOnlyHint { get; set; }

    [JsonPropertyName("destructiveHint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? DestructiveHint { get; set; }

    [JsonPropertyName("idempotentHint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IdempotentHint { get; set; }

    [JsonPropertyName("openWorldHint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? OpenWorldHint { get; set; }
}

public sealed class ToolInputSchema
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "object";

    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }

    [JsonPropertyName("properties")]
    public Dictionary<string, ToolPropertySchema> Properties { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("required")]
    public List<string> Required { get; set; } = [];
}

public sealed class ToolDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("inputSchema")]
    public ToolInputSchema InputSchema { get; set; } = new();

    [JsonPropertyName("annotations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ToolAnnotations? Annotations { get; set; }

    [JsonPropertyName("outputSchema")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? OutputSchema { get; set; }

    [JsonIgnore]
    public IReadOnlyDictionary<string, string>? ParameterAliases { get; init; }

    [JsonIgnore]
    public IReadOnlySet<string>? AcceptedHandlerKeys { get; init; }
}

public sealed class ToolsListResult
{
    [JsonPropertyName("tools")]
    public List<ToolDefinition> Tools { get; set; } = [];
}

internal sealed class ToolCallParams
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    public JsonElement Arguments { get; set; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextContentItem), "text")]
[JsonDerivedType(typeof(ImageContentItem), "image")]
[JsonDerivedType(typeof(ResourceLinkContentItem), "resource_link")]
public abstract class ContentItem
{
    [JsonIgnore]
    public abstract string Type { get; }
}

public sealed class TextContentItem : ContentItem
{
    [JsonIgnore]
    public override string Type => "text";

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    public TextContentItem() { }
    public TextContentItem(string text) => Text = text;
}

public sealed class ImageContentItem : ContentItem
{
    [JsonIgnore]
    public override string Type => "image";

    [JsonPropertyName("data")]
    public string Data { get; set; } = string.Empty;

    [JsonPropertyName("mimeType")]
    public string MimeType { get; set; } = "image/png";

    public ImageContentItem() { }
    public ImageContentItem(string base64Data, string mimeType = "image/png")
    {
        Data = base64Data;
        MimeType = mimeType;
    }
}

public sealed class ResourceLinkContentItem : ContentItem
{
    [JsonIgnore]
    public override string Type => "resource_link";

    [JsonPropertyName("uri")]
    public string Uri { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("mimeType")]
    public string MimeType { get; set; } = "image/png";

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }
}

public sealed class ToolCallResult
{
    [JsonPropertyName("content")]
    public List<ContentItem> Content { get; set; } = [];

    [JsonPropertyName("isError")]
    public bool IsError { get; set; }

    [JsonPropertyName("structuredContent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? StructuredContent { get; set; }

    [JsonIgnore]
    public string FirstText => Content.OfType<TextContentItem>().FirstOrDefault()?.Text ?? string.Empty;

    public ToolCallResult WithStructuredContentIfJson()
    {
        if (StructuredContent.HasValue)
        {
            return this;
        }

        string text = FirstText;
        if (string.IsNullOrWhiteSpace(text) || !text.AsSpan().TrimStart().StartsWith("{", StringComparison.Ordinal))
        {
            return this;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                return new ToolCallResult
                {
                    Content = Content,
                    IsError = IsError,
                    StructuredContent = doc.RootElement.Clone()
                };
            }
        }
        catch (JsonException)
        {
            // Plain text payload; omit structuredContent
        }

        return this;
    }

    public static ToolCallResult Text(string text, bool isError = false) => new()
    {
        Content = [new TextContentItem(text)],
        IsError = isError
    };

    public static ToolCallResult Json(object payload, bool isError = false) => new()
    {
        Content = [new TextContentItem(JsonRpcSerializer.SerializeIndented(payload))],
        IsError = isError
    };

    public static ToolCallResult Success(object payload) => Json(payload);

    public const int MaxInlineImageBase64Chars = 400 * 1024;

    public static ToolCallResult Image(string base64Png, string? summaryText = null, bool isError = false, string? artifactPath = null)
    {
        List<ContentItem> content = [];
        string safeBase64 = base64Png ?? string.Empty;
        bool exceedsFirewall = safeBase64.Length > MaxInlineImageBase64Chars;

        string? resolvedPath = !string.IsNullOrWhiteSpace(artifactPath) ? artifactPath : TryExtractArtifactPath(summaryText);
        if (exceedsFirewall && (string.IsNullOrWhiteSpace(resolvedPath) || !File.Exists(resolvedPath)))
        {
            resolvedPath = SpillBase64ImageToDisk(safeBase64, resolvedPath);
        }

        string artifactUri = !string.IsNullOrWhiteSpace(resolvedPath) ? ToFileUriSafe(resolvedPath) : string.Empty;

        if (!string.IsNullOrEmpty(summaryText))
        {
            string normalizedSummary = EnsureImageFirewallMetadataInJson(summaryText, resolvedPath, artifactUri);
            content.Add(new TextContentItem(normalizedSummary));
        }

        if (exceedsFirewall)
        {
            string fileName = !string.IsNullOrWhiteSpace(resolvedPath) ? Path.GetFileName(resolvedPath) : "capture.png";
            content.Add(new ResourceLinkContentItem
            {
                Uri = artifactUri,
                Name = fileName,
                MimeType = "image/png",
                Description = $"PNG frame ({safeBase64.Length} Base64 chars > 400 KB stdio firewall) spilled to {artifactUri}"
            });
        }
        else
        {
            content.Add(new ImageContentItem(safeBase64, "image/png"));
        }

        return new ToolCallResult
        {
            Content = content,
            IsError = isError
        };
    }

    public static string ToFileUriSafe(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return new Uri(Path.GetFullPath(path)).AbsoluteUri;
        }
        catch
        {
            return $"file:///{path.Replace('\\', '/').TrimStart('/')}";
        }
    }

    private static string? TryExtractArtifactPath(string? summaryText)
    {
        if (string.IsNullOrWhiteSpace(summaryText) || !summaryText.TrimStart().StartsWith('{'))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(summaryText);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("artifactPath", out JsonElement ap) &&
                    ap.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(ap.GetString()))
                {
                    return ap.GetString();
                }

                if (doc.RootElement.TryGetProperty("filePath", out JsonElement fp) &&
                    fp.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(fp.GetString()))
                {
                    return fp.GetString();
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static string SpillBase64ImageToDisk(string base64Png, string? preferredPath)
    {
        string targetPath = preferredPath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            string spillDir = Path.Combine(Path.GetTempPath(), "kongor_mcp_captures");
            Directory.CreateDirectory(spillDir);
            targetPath = Path.Combine(spillDir, $"mcp_capture_{TimeProvider.System.GetUtcNow():yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.png");
        }
        else
        {
            string? parent = Path.GetDirectoryName(Path.GetFullPath(targetPath));
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }
        }

        try
        {
            byte[] bytes = Convert.FromBase64String(base64Png);
            File.WriteAllBytes(targetPath, bytes);
        }
        catch
        {
            File.WriteAllText(targetPath, base64Png);
        }

        return Path.GetFullPath(targetPath);
    }

    private static string EnsureImageFirewallMetadataInJson(string summaryText, string? artifactPath, string artifactUri)
    {
        if (!summaryText.TrimStart().StartsWith('{'))
        {
            return summaryText;
        }

        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(summaryText) is System.Text.Json.Nodes.JsonObject obj)
            {
                obj["base64Png"] = string.Empty;
                obj["base64Omitted"] = true;
                if (!string.IsNullOrWhiteSpace(artifactPath) &&
                    (!obj.ContainsKey("artifactPath") || string.IsNullOrWhiteSpace(obj["artifactPath"]?.ToString())))
                {
                    obj["artifactPath"] = artifactPath;
                }
                if (!string.IsNullOrWhiteSpace(artifactUri))
                {
                    obj["artifactUri"] = artifactUri;
                }
                else if (!obj.ContainsKey("artifactUri"))
                {
                    obj["artifactUri"] = string.Empty;
                }

                return obj.ToJsonString(JsonRpcSerializer.IndentedOptions);
            }
        }
        catch
        {
        }

        return summaryText;
    }

    public static ToolCallResult Error(string errorMessage) => new()
    {
        Content = [new TextContentItem(errorMessage)],
        IsError = true
    };
}

public sealed class LoggingMessageParams
{
    [JsonPropertyName("level")]
    public string Level { get; set; } = "info";

    [JsonPropertyName("data")]
    public object? Data { get; set; }

    [JsonPropertyName("logger")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Logger { get; set; } = "kongor.mcp";
}

public sealed class SetLogLevelParams
{
    [JsonPropertyName("level")]
    public string Level { get; set; } = "info";
}

// ============================================================================
// MCP Native Resources & Prompts Models
// ============================================================================

public sealed class McpResource
{
    [JsonPropertyName("uri")]
    public string Uri { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("mimeType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MimeType { get; set; }
}

public sealed class ResourceListResult
{
    [JsonPropertyName("resources")]
    public List<McpResource> Resources { get; set; } = [];
}

public sealed class ReadResourceParams
{
    [JsonPropertyName("uri")]
    public string Uri { get; set; } = string.Empty;
}

public sealed class ResourceContents
{
    [JsonPropertyName("uri")]
    public string Uri { get; set; } = string.Empty;

    [JsonPropertyName("mimeType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MimeType { get; set; }

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    [JsonPropertyName("blob")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Blob { get; set; }
}

public sealed class ReadResourceResult
{
    [JsonPropertyName("contents")]
    public List<ResourceContents> Contents { get; set; } = [];
}

public sealed class PromptArgument
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("required")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Required { get; set; }
}

public sealed class McpPrompt
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("arguments")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<PromptArgument>? Arguments { get; set; }
}

public sealed class PromptListResult
{
    [JsonPropertyName("prompts")]
    public List<McpPrompt> Prompts { get; set; } = [];
}

public sealed class GetPromptParams
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Arguments { get; set; }
}

public sealed class PromptMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("content")]
    public ContentItem Content { get; set; } = new TextContentItem();
}

public sealed class GetPromptResult
{
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("messages")]
    public List<PromptMessage> Messages { get; set; } = [];
}

// ============================================================================
// MCP Progress & Resource Templates Models (MCP 2024-11-05 Specification)
// ============================================================================

public sealed class ProgressNotificationParams
{
    [JsonPropertyName("progressToken")]
    public object ProgressToken { get; set; } = string.Empty;

    [JsonPropertyName("progress")]
    public double Progress { get; set; }

    [JsonPropertyName("total")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Total { get; set; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }
}

public sealed class ResourceUpdatedNotificationParams
{
    [JsonPropertyName("uri")]
    public string Uri { get; set; } = string.Empty;
}

public sealed class ResourceTemplate
{
    [JsonPropertyName("uriTemplate")]
    public string UriTemplate { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("mimeType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MimeType { get; set; }
}

public sealed class ResourceTemplateListResult
{
    [JsonPropertyName("resourceTemplates")]
    public List<ResourceTemplate> ResourceTemplates { get; set; } = [];
}

public interface IMcpProgressReporter
{
    Task ReportProgressAsync(double progress, double? total = null, string? message = null, CancellationToken ct = default);
}

public static class McpProgressContext
{
    private static readonly AsyncLocal<IMcpProgressReporter?> s_current = new();

    public static IMcpProgressReporter? Current
    {
        get => s_current.Value;
        set => s_current.Value = value;
    }
}

internal sealed class ActionMcpProgressReporter : IMcpProgressReporter
{
    private readonly Func<double, double?, string?, CancellationToken, Task> _report;

    public ActionMcpProgressReporter(Func<double, double?, string?, CancellationToken, Task> report)
    {
        _report = report;
    }

    public Task ReportProgressAsync(double progress, double? total = null, string? message = null, CancellationToken ct = default)
    {
        return _report(progress, total, message, ct);
    }
}

// ============================================================================
// MCP Sampling Models (MCP 2024-11-05 Specification: sampling/createMessage)
// ============================================================================

public sealed class SamplingContent
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Data { get; set; }

    [JsonPropertyName("mimeType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MimeType { get; set; }
}

public sealed class SamplingMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("content")]
    public SamplingContent Content { get; set; } = new();
}

public sealed class ModelHint
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

public sealed class ModelPreferences
{
    [JsonPropertyName("hints")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ModelHint>? Hints { get; set; }

    [JsonPropertyName("costPriority")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? CostPriority { get; set; }

    [JsonPropertyName("speedPriority")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? SpeedPriority { get; set; }

    [JsonPropertyName("intelligencePriority")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? IntelligencePriority { get; set; }
}

public sealed class CreateMessageParams
{
    [JsonPropertyName("messages")]
    public List<SamplingMessage> Messages { get; set; } = [];

    [JsonPropertyName("modelPreferences")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ModelPreferences? ModelPreferences { get; set; }

    [JsonPropertyName("systemPrompt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SystemPrompt { get; set; }

    [JsonPropertyName("includeContext")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IncludeContext { get; set; }

    [JsonPropertyName("temperature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Temperature { get; set; }

    [JsonPropertyName("maxTokens")]
    public int MaxTokens { get; set; } = 1000;

    [JsonPropertyName("stopSequences")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? StopSequences { get; set; }

    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Metadata { get; set; }
}

public sealed class CreateMessageResult
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "assistant";

    [JsonPropertyName("content")]
    public SamplingContent Content { get; set; } = new();

    [JsonPropertyName("model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; set; }

    [JsonPropertyName("stopReason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StopReason { get; set; }
}

public interface IMcpSamplingService
{
    bool IsSamplingSupported { get; }
    Task<CreateMessageResult> CreateMessageAsync(CreateMessageParams parameters, CancellationToken cancellationToken = default);
    Task<string?> RequestCrashAnalysisAsync(string exitCode, string errorOrLogText, CancellationToken cancellationToken = default);
}

public static class McpSamplingContext
{
    private static readonly AsyncLocal<IMcpSamplingService?> s_current = new();

    public static IMcpSamplingService? Current
    {
        get => s_current.Value;
        set => s_current.Value = value;
    }
}

/// <summary>
/// Ambient execution context capturing the active agent identifier or conversation ID
/// for the currently running JSON-RPC tool call. Enables automatic agent-to-client isolation
/// and routing without requiring explicit process IDs.
/// </summary>
public static class McpAgentContext
{
    private static readonly AsyncLocal<string?> s_currentAgentId = new();

    public static string? CurrentAgentId
    {
        get => s_currentAgentId.Value;
        set => s_currentAgentId.Value = value;
    }
}
