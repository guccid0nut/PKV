// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Kongor.Cli.Mcp.Protocol;

/// <summary>
/// Bridges Project Kongor's <see cref="McpServer" /> and <see cref="McpToolRegistry" /> (including all 99+ canonical tools,
/// 71+ hidden aliases, multi-modal <see cref="ImageContentItem" /> captures, <see cref="ExecutionTruthEnvelope" />,
/// <see cref="IMcpRateLimiter" />, <see cref="IMcpConversationLogger" />, and live <c>kongor://*</c> resources)
/// into the official C# <c>ModelContextProtocol</c> SDK v2.2.0 handlers.
/// </summary>
internal static class KongorOfficialMcpAdapter
{
    public static IMcpServerBuilder ConfigureKongorHandlers(
        this IMcpServerBuilder mcpBuilder,
        McpServer kongorServer)
    {
        ArgumentNullException.ThrowIfNull(mcpBuilder);
        ArgumentNullException.ThrowIfNull(kongorServer);

        mcpBuilder
            .WithListToolsHandler((request, cancellationToken) =>
            {
                bool? includeAliases = null;
                string? toolsets = null;
                if (request.Params?.Meta is { } meta)
                {
                    if ((meta.TryGetPropertyValue("includeAliases", out var incNode) ||
                         meta.TryGetPropertyValue("include_aliases", out incNode)) &&
                        incNode is not null &&
                        bool.TryParse(incNode.ToString(), out bool parsedInc))
                    {
                        includeAliases = parsedInc;
                    }

                    if (meta.TryGetPropertyValue("toolsets", out var tsNode) && tsNode is not null)
                    {
                        toolsets = tsNode.ToString();
                    }
                }

                IReadOnlyList<ToolDefinition> definitions = kongorServer.Registry.ListTools(includeAliases: includeAliases, toolsets: toolsets);
                List<Tool> sdkTools = new(definitions.Count);

                bool supportsSchemaAndStructured = !string.Equals(
                    request.Server?.NegotiatedProtocolVersion,
                    "2024-11-05",
                    StringComparison.Ordinal);

                foreach (ToolDefinition def in definitions)
                {
                    string schemaJson = JsonRpcSerializer.Serialize(def.InputSchema);
                    using JsonDocument doc = JsonDocument.Parse(schemaJson);
                    ModelContextProtocol.Protocol.ToolAnnotations? sdkAnnotations = def.Annotations is null
                        ? null
                        : new ModelContextProtocol.Protocol.ToolAnnotations
                        {
                            Title = def.Annotations.Title,
                            ReadOnlyHint = def.Annotations.ReadOnlyHint,
                            DestructiveHint = def.Annotations.DestructiveHint,
                            IdempotentHint = def.Annotations.IdempotentHint,
                            OpenWorldHint = def.Annotations.OpenWorldHint
                        };

                    sdkTools.Add(new Tool
                    {
                        Name = def.Name,
                        Description = def.Description,
                        InputSchema = doc.RootElement.Clone(),
                        OutputSchema = supportsSchemaAndStructured ? def.OutputSchema?.Clone() : null,
                        Annotations = sdkAnnotations
                    });
                }

                return ValueTask.FromResult(new ListToolsResult
                {
                    Tools = sdkTools
                });
            })
            .WithCallToolHandler(async (request, cancellationToken) =>
            {
                string toolName = request.Params?.Name ?? string.Empty;
                if (string.IsNullOrWhiteSpace(toolName))
                {
                    return new CallToolResult
                    {
                        IsError = true,
                        Content =
                        [
                            new TextContentBlock
                            {
                                Text = "Invalid params for 'tools/call': 'name' property is required and must be a string."
                            }
                        ]
                    };
                }

                JsonElement argumentsElement;
                if (request.Params?.Arguments is { Count: > 0 } argDict)
                {
                    string argsJson = JsonSerializer.Serialize(argDict);
                    using JsonDocument doc = JsonDocument.Parse(argsJson);
                    argumentsElement = doc.RootElement.Clone();
                }
                else
                {
                    using JsonDocument emptyDoc = JsonDocument.Parse("{}");
                    argumentsElement = emptyDoc.RootElement.Clone();
                }

                IMcpProgressReporter? progressReporter = null;
                if (request.Params?.Meta is { } metaObj &&
                    metaObj.TryGetPropertyValue("progressToken", out var ptNode) &&
                    ptNode is not null &&
                    request.Server is not null)
                {
                    string progressTokenStr = ptNode.ToString();
                    progressReporter = new ActionMcpProgressReporter(async (prog, total, msg, ct) =>
                    {
                        try
                        {
                            await request.Server.SendNotificationAsync(
                                NotificationMethods.ProgressNotification,
                                new ProgressNotificationParams
                                {
                                    ProgressToken = progressTokenStr,
                                    Progress = prog,
                                    Total = total,
                                    Message = msg
                                },
                                cancellationToken: ct).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"[MCP Adapter] Non-fatal progress notification failure (token={progressTokenStr}): {ex.Message}");
                        }
                    });
                }

                string? agentId = null;
                if (argumentsElement.ValueKind == JsonValueKind.Object)
                {
                    if (argumentsElement.TryGetProperty("agent_id", out JsonElement aidEl) && aidEl.ValueKind == JsonValueKind.String)
                        agentId = aidEl.GetString();
                    else if (argumentsElement.TryGetProperty("agentId", out JsonElement aidEl2) && aidEl2.ValueKind == JsonValueKind.String)
                        agentId = aidEl2.GetString();
                }

                IMcpSamplingService? samplingService = request.Server is not null
                    ? new SdkMcpSamplingBridge(request.Server)
                    : null;

                ToolCallResult kongorResult = await kongorServer.ExecuteToolWithMiddlewareAsync(
                    toolName,
                    argumentsElement,
                    requestId: null,
                    progressReporter: progressReporter,
                    agentId: agentId,
                    samplingServiceOverride: samplingService,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                kongorResult = kongorResult.WithStructuredContentIfJson();
                List<ContentBlock> contentBlocks = new(kongorResult.Content.Count);
                foreach (ContentItem item in kongorResult.Content)
                {
                    if (item is ImageContentItem img)
                    {
                        byte[] rawImageBytes;
                        try
                        {
                            rawImageBytes = Convert.FromBase64String(img.Data);
                        }
                        catch
                        {
                            rawImageBytes = System.Text.Encoding.UTF8.GetBytes(img.Data);
                        }

                        contentBlocks.Add(ImageContentBlock.FromBytes(
                            rawImageBytes,
                            string.IsNullOrWhiteSpace(img.MimeType) ? "image/png" : img.MimeType));
                    }
                    else if (item is TextContentItem txt)
                    {
                        contentBlocks.Add(new TextContentBlock
                        {
                            Text = txt.Text
                        });
                    }
                    else
                    {
                        contentBlocks.Add(new TextContentBlock
                        {
                            Text = JsonRpcSerializer.Serialize(item)
                        });
                    }
                }

                bool emitStructured = !string.Equals(
                    request.Server?.NegotiatedProtocolVersion,
                    "2024-11-05",
                    StringComparison.Ordinal);

                return new CallToolResult
                {
                    IsError = kongorResult.IsError,
                    Content = contentBlocks,
                    StructuredContent = emitStructured ? kongorResult.StructuredContent?.Clone() : null
                };
            })
            .WithListResourcesHandler((request, cancellationToken) =>
            {
                ResourceListResult list = kongorServer.GetResourcesListResult();
                List<Resource> sdkResources = new(list.Resources.Count);
                foreach (McpResource res in list.Resources)
                {
                    sdkResources.Add(new Resource
                    {
                        Uri = res.Uri,
                        Name = res.Name,
                        Description = res.Description,
                        MimeType = res.MimeType
                    });
                }

                if (kongorServer.Registry.IncludeSidecars)
                {
                    sdkResources.Add(new Resource
                    {
                        Uri = "kongor://sidecars/catalog",
                        Name = "Federated Python MCP Sidecars (JIT Discovery)",
                        Description = "Catalog of all 11 standardized FastMCP Python sidecars with shared _common/mcp_telemetry.py logging (zero startup tool token bloat).",
                        MimeType = "application/json"
                    });
                }

                return ValueTask.FromResult(new ListResourcesResult
                {
                    Resources = sdkResources
                });
            })
            .WithListResourceTemplatesHandler((request, cancellationToken) =>
            {
                ResourceTemplateListResult templates = kongorServer.GetResourceTemplatesListResult();
                List<ModelContextProtocol.Protocol.ResourceTemplate> sdkTemplates = new(templates.ResourceTemplates.Count);
                foreach (ResourceTemplate tpl in templates.ResourceTemplates)
                {
                    sdkTemplates.Add(new ModelContextProtocol.Protocol.ResourceTemplate
                    {
                        UriTemplate = tpl.UriTemplate,
                        Name = tpl.Name,
                        Description = tpl.Description,
                        MimeType = tpl.MimeType
                    });
                }

                return ValueTask.FromResult(new ListResourceTemplatesResult
                {
                    ResourceTemplates = sdkTemplates
                });
            })
            .WithReadResourceHandler(async (request, cancellationToken) =>
            {
                string uri = request.Params?.Uri ?? string.Empty;
                if (string.Equals(uri, "kongor://sidecars/catalog", StringComparison.OrdinalIgnoreCase))
                {
                    string catalogJson = JsonSerializer.Serialize(new
                    {
                        federated = kongorServer.Registry.IncludeSidecars,
                        telemetryModule = "src/tools/_common/mcp_telemetry.py",
                        telemetryLog = "logs/mcp_python_outcomes.ndjson",
                        sidecars = new[]
                        {
                            new { name = "blender", script = "src/tools/blender/mcp_server.py", sdk = "FastMCP" },
                            new { name = "clangd", script = "src/tools/clangd/mcp_server.py", sdk = "FastMCP" },
                            new { name = "comfyui", script = "src/tools/comfyui/mcp_server.py", sdk = "FastMCP" },
                            new { name = "everything", script = "src/tools/everything/mcp_server.py", sdk = "FastMCP" },
                            new { name = "ffmpeg", script = "src/tools/ffmpeg/mcp_server.py", sdk = "FastMCP" },
                            new { name = "frida", script = "src/tools/frida/mcp_server.py", sdk = "FastMCP" },
                            new { name = "ghidra", script = "src/tools/ghidra/mcp_server.py", sdk = "FastMCP" },
                            new { name = "luals", script = "src/tools/luals/mcp_server.py", sdk = "FastMCP" },
                            new { name = "texconv", script = "src/tools/texconv/mcp_server.py", sdk = "FastMCP" },
                            new { name = "voicebox", script = "src/tools/voicebox/mcp_server.py", sdk = "FastMCP" },
                            new { name = "windbg", script = "src/tools/windbg/mcp_server.py", sdk = "FastMCP" }
                        }
                    });
                    return new ModelContextProtocol.Protocol.ReadResourceResult
                    {
                        Contents =
                        [
                            new TextResourceContents
                            {
                                Uri = uri,
                                MimeType = "application/json",
                                Text = catalogJson
                            }
                        ]
                    };
                }

                ReadResourceResult kongorRead = await kongorServer.ReadResourceByUriAsync(uri, cancellationToken).ConfigureAwait(false);
                List<ModelContextProtocol.Protocol.ResourceContents> sdkContents = new(kongorRead.Contents.Count);
                foreach (ResourceContents item in kongorRead.Contents)
                {
                    if (!string.IsNullOrEmpty(item.Blob))
                    {
                        byte[] blobBytes;
                        try
                        {
                            blobBytes = Convert.FromBase64String(item.Blob);
                        }
                        catch
                        {
                            blobBytes = System.Text.Encoding.UTF8.GetBytes(item.Blob);
                        }

                        sdkContents.Add(new BlobResourceContents
                        {
                            Uri = item.Uri,
                            MimeType = item.MimeType,
                            Blob = blobBytes
                        });
                    }
                    else
                    {
                        sdkContents.Add(new TextResourceContents
                        {
                            Uri = item.Uri,
                            MimeType = item.MimeType,
                            Text = item.Text ?? string.Empty
                        });
                    }
                }

                return new ModelContextProtocol.Protocol.ReadResourceResult
                {
                    Contents = sdkContents
                };
            })
            .WithSubscribeToResourcesHandler((request, cancellationToken) =>
            {
                string uri = request.Params?.Uri ?? string.Empty;
                kongorServer.SubscribeResourceByUri(uri);
                return ValueTask.FromResult(new EmptyResult());
            })
            .WithUnsubscribeFromResourcesHandler((request, cancellationToken) =>
            {
                string uri = request.Params?.Uri ?? string.Empty;
                kongorServer.UnsubscribeResourceByUri(uri);
                return ValueTask.FromResult(new EmptyResult());
            })
            .WithListPromptsHandler((request, cancellationToken) =>
            {
                PromptListResult prompts = kongorServer.GetPromptsListResult();
                List<Prompt> sdkPrompts = new(prompts.Prompts.Count);
                foreach (McpPrompt p in prompts.Prompts)
                {
                    List<ModelContextProtocol.Protocol.PromptArgument>? args = null;
                    if (p.Arguments is { Count: > 0 })
                    {
                        args = new List<ModelContextProtocol.Protocol.PromptArgument>(p.Arguments.Count);
                        foreach (PromptArgument a in p.Arguments)
                        {
                            args.Add(new ModelContextProtocol.Protocol.PromptArgument
                            {
                                Name = a.Name,
                                Description = a.Description,
                                Required = a.Required
                            });
                        }
                    }

                    sdkPrompts.Add(new Prompt
                    {
                        Name = p.Name,
                        Description = p.Description,
                        Arguments = args
                    });
                }

                return ValueTask.FromResult(new ListPromptsResult
                {
                    Prompts = sdkPrompts
                });
            })
            .WithGetPromptHandler((request, cancellationToken) =>
            {
                string name = request.Params?.Name ?? string.Empty;
                GetPromptResult kongorPrompt = kongorServer.GetPromptResultByName(name, request.Params?.Arguments);
                List<ModelContextProtocol.Protocol.PromptMessage> sdkMessages = new(kongorPrompt.Messages.Count);
                foreach (PromptMessage msg in kongorPrompt.Messages)
                {
                    Role role = string.Equals(msg.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                        ? Role.Assistant
                        : Role.User;

                    string text = msg.Content is TextContentItem tci
                        ? tci.Text
                        : JsonRpcSerializer.Serialize(msg.Content);

                    sdkMessages.Add(new ModelContextProtocol.Protocol.PromptMessage
                    {
                        Role = role,
                        Content = new TextContentBlock
                        {
                            Text = text
                        }
                    });
                }

                return ValueTask.FromResult(new ModelContextProtocol.Protocol.GetPromptResult
                {
                    Description = kongorPrompt.Description,
                    Messages = sdkMessages
                });
            });

        return mcpBuilder;
    }

#pragma warning disable MCP9005 // Backward-compatible MCP 2025-06-18/2025-11-25 sampling bridge (SEP-2577)
    private sealed class SdkMcpSamplingBridge(ModelContextProtocol.Server.McpServer sdkServer) : IMcpSamplingService
    {
        public bool IsSamplingSupported => sdkServer.ClientCapabilities?.Sampling is not null;

        public async Task<CreateMessageResult> CreateMessageAsync(CreateMessageParams parameters, CancellationToken cancellationToken = default)
        {
            if (!IsSamplingSupported)
            {
                throw new InvalidOperationException("Connected MCP client did not advertise 'sampling' capability during initialization.");
            }

            string wireJson = JsonRpcSerializer.Serialize(parameters);
            CreateMessageRequestParams sdkParams = JsonSerializer.Deserialize<CreateMessageRequestParams>(
                wireJson,
                ModelContextProtocol.McpJsonUtilities.DefaultOptions)
                ?? throw new InvalidOperationException("Failed to serialize CreateMessageParams for official SDK SampleAsync.");

            ModelContextProtocol.Protocol.CreateMessageResult sdkResult = await sdkServer.SampleAsync(sdkParams, cancellationToken).ConfigureAwait(false);
            TextContentBlock? firstText = sdkResult.Content.OfType<TextContentBlock>().FirstOrDefault();

            return new CreateMessageResult
            {
                Role = sdkResult.Role == Role.User ? "user" : "assistant",
                Model = sdkResult.Model,
                StopReason = sdkResult.StopReason,
                Content = new SamplingContent
                {
                    Type = "text",
                    Text = firstText?.Text ?? string.Empty
                }
            };
        }

        public async Task<string?> RequestCrashAnalysisAsync(string exitCode, string errorOrLogText, CancellationToken cancellationToken = default)
        {
            if (!IsSamplingSupported)
            {
                return null;
            }

            CreateMessageParams parameters = new()
            {
                SystemPrompt = "You are an expert systems engineer and K2 game engine debugger for ProjectKongor.",
                Messages =
                [
                    new SamplingMessage
                    {
                        Role = "user",
                        Content = new SamplingContent
                        {
                            Type = "text",
                            Text = $"A dedicated game server or client crashed with exit code {exitCode}.\nDiagnostic log / context:\n{errorOrLogText}\n\nPlease analyze the root cause and provide an actionable fix."
                        }
                    }
                ],
                MaxTokens = 500,
                Temperature = 0.2
            };

            try
            {
                CreateMessageResult result = await CreateMessageAsync(parameters, cancellationToken).ConfigureAwait(false);
                return result.Content?.Text;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[MCP Adapter] Crash analysis sampling failed: {ex.Message}");
                return null;
            }
        }
    }
#pragma warning restore MCP9005
}
