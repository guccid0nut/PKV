// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.ComponentModel;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Kongor.Cli.Mcp.Protocol;

public sealed partial class McpToolRegistry
{
    /// <summary>
    /// Resolves a canonical or aliased tool name to its registered <see cref="McpToolCategory" />.
    /// </summary>
    public McpToolCategory GetToolCategory(string name)
    {
        string? resolved = ResolveToolName(name);
        if (resolved is not null && _tools.TryGetValue(resolved, out ToolRegistration? reg))
        {
            return reg.Category;
        }
        return McpToolCategory.Default;
    }

    /// <summary>
    /// Discovers and registers methods decorated with <see cref="McpServerToolAttribute" /> on <paramref name="instance" />
    /// into this <see cref="McpToolRegistry" />, allowing source-generated or attribute-based MCP tools to participate
    /// seamlessly in Kongor's rate-limiting, alias resolution, ExecutionTruth envelopes, and Antigravity schema export.
    /// </summary>
    public int RegisterAttributedToolsFromInstance(
        object instance,
        McpToolCategory defaultCategory = McpToolCategory.Default,
        bool overwriteExisting = false)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return RegisterAttributedToolsFromType(instance.GetType(), instance, defaultCategory, overwriteExisting);
    }

    /// <summary>
    /// Discovers and registers static or instance methods decorated with <see cref="McpServerToolAttribute" /> on <paramref name="toolType" />.
    /// </summary>
    public int RegisterAttributedToolsFromType(
        Type toolType,
        object? instance = null,
        McpToolCategory defaultCategory = McpToolCategory.Default,
        bool overwriteExisting = false)
    {
        ArgumentNullException.ThrowIfNull(toolType);

        int registeredCount = 0;
        MethodInfo[] methods = toolType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);

        foreach (MethodInfo method in methods)
        {
            McpServerToolAttribute? toolAttr = method.GetCustomAttribute<McpServerToolAttribute>();
            if (toolAttr is null)
            {
                continue;
            }

            if (!method.IsStatic && instance is null)
            {
                continue;
            }

            string toolName = !string.IsNullOrWhiteSpace(toolAttr.Name)
                ? toolAttr.Name.Trim()
                : ToSnakeCase(method.Name);

            if (!overwriteExisting && _tools.ContainsKey(toolName))
            {
                continue;
            }

            string description = method.GetCustomAttribute<DescriptionAttribute>()?.Description
                ?? $"Executes MCP tool '{toolName}'.";

            McpToolCategory category = toolAttr.ReadOnly
                ? McpToolCategory.ReadOnly
                : defaultCategory;

            ToolInputSchema inputSchema = BuildInputSchemaFromMethod(toolName, method);
            ToolDefinition definition = new()
            {
                Name = toolName,
                Description = description,
                InputSchema = inputSchema
            };

            ToolHandlerDelegate handler = CreateDelegateForAttributedMethod(method, instance);
            RegisterTool(definition, handler, category);
            registeredCount++;
        }

        return registeredCount;
    }

    private static ToolInputSchema BuildInputSchemaFromMethod(string toolName, MethodInfo method)
    {
        Dictionary<string, ToolPropertySchema> properties = new(StringComparer.Ordinal);
        List<string> required = [];

        foreach (ParameterInfo param in method.GetParameters())
        {
            if (param.ParameterType == typeof(CancellationToken) ||
                param.ParameterType == typeof(JsonElement) && string.Equals(param.Name, "rawArguments", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string paramName = param.Name ?? "arg";
            string? paramDesc = param.GetCustomAttribute<DescriptionAttribute>()?.Description;
            Type underlyingType = Nullable.GetUnderlyingType(param.ParameterType) ?? param.ParameterType;

            string jsonType = MapClrTypeToJsonSchemaType(underlyingType);
            ToolPropertySchema propSchema = new()
            {
                Type = jsonType,
                Title = paramName,
                Description = paramDesc ?? string.Empty
            };

            if (underlyingType.IsEnum)
            {
                propSchema.Type = "string";
                propSchema.Enum = Enum.GetNames(underlyingType).Select(n => n.ToLowerInvariant()).ToList();
            }
            else if (propSchema.Type == "array")
            {
                Type elemType = underlyingType.IsArray
                    ? underlyingType.GetElementType() ?? typeof(string)
                    : underlyingType.IsGenericType
                        ? underlyingType.GetGenericArguments()[0]
                        : typeof(string);
                Type cleanElemType = Nullable.GetUnderlyingType(elemType) ?? elemType;
                propSchema.Items = BuildNestedSchemaFromClrType(cleanElemType, $"{paramName}Item");
            }
            else if (propSchema.Type == "object")
            {
                propSchema.Properties = BuildObjectPropertiesFromClrType(underlyingType);
            }

            if (param.HasDefaultValue && param.DefaultValue is not null)
            {
                propSchema.Default = param.DefaultValue;
            }

            properties[paramName] = propSchema;

            bool isNullable = Nullable.GetUnderlyingType(param.ParameterType) is not null || !param.ParameterType.IsValueType;
            if (!param.HasDefaultValue && !isNullable)
            {
                required.Add(paramName);
            }
        }

        return new ToolInputSchema
        {
            Type = "object",
            Title = $"{toolName}Arguments",
            Properties = properties,
            Required = required
        };
    }

    private static ToolHandlerDelegate CreateDelegateForAttributedMethod(MethodInfo method, object? instance)
    {
        ParameterInfo[] parameters = method.GetParameters();

        return async (JsonElement arguments, CancellationToken cancellationToken) =>
        {
            object?[] invokeArgs = new object?[parameters.Length];

            for (int i = 0; i < parameters.Length; i++)
            {
                ParameterInfo param = parameters[i];
                if (param.ParameterType == typeof(CancellationToken))
                {
                    invokeArgs[i] = cancellationToken;
                    continue;
                }

                if (param.ParameterType == typeof(JsonElement) &&
                    string.Equals(param.Name, "arguments", StringComparison.OrdinalIgnoreCase))
                {
                    invokeArgs[i] = arguments;
                    continue;
                }

                string paramName = param.Name ?? string.Empty;
                if (arguments.ValueKind == JsonValueKind.Object &&
                    TryGetPropertyCaseInsensitive(arguments, paramName, out JsonElement propEl) &&
                    propEl.ValueKind != JsonValueKind.Null &&
                    propEl.ValueKind != JsonValueKind.Undefined)
                {
                    invokeArgs[i] = ConvertJsonElementToParameter(propEl, param.ParameterType);
                }
                else if (param.HasDefaultValue)
                {
                    invokeArgs[i] = param.DefaultValue;
                }
                else
                {
                    invokeArgs[i] = param.ParameterType.IsValueType
                        ? Activator.CreateInstance(param.ParameterType)
                        : null;
                }
            }

            object? rawResult;
            try
            {
                rawResult = method.Invoke(method.IsStatic ? null : instance, invokeArgs);
            }
            catch (TargetInvocationException tie) when (tie.InnerException is not null)
            {
                return ToolCallResult.Error($"Error executing tool '{method.Name}': {tie.InnerException.Message}");
            }

            return await UnwrapAttributedMethodResultAsync(rawResult).ConfigureAwait(false);
        };
    }

    private static async Task<ToolCallResult> UnwrapAttributedMethodResultAsync(object? rawResult)
    {
        if (rawResult is null)
        {
            return ToolCallResult.Success(new { status = "ok" });
        }

        if (rawResult is ToolCallResult directResult)
        {
            return directResult;
        }

        if (rawResult is Task<ToolCallResult> taskToolResult)
        {
            return await taskToolResult.ConfigureAwait(false);
        }

        if (rawResult is Task<string> taskString)
        {
            string str = await taskString.ConfigureAwait(false);
            return new ToolCallResult { Content = [new TextContentItem(str)] };
        }

        if (rawResult is Task task)
        {
            await task.ConfigureAwait(false);
            Type taskType = task.GetType();
            if (taskType.IsGenericType)
            {
                PropertyInfo? resultProp = taskType.GetProperty("Result");
                object? val = resultProp?.GetValue(task);
                if (val is ToolCallResult tcr) return tcr;
                if (val is string s) return new ToolCallResult { Content = [new TextContentItem(s)] };
                return ToolCallResult.Success(val ?? new { status = "ok" });
            }
            return ToolCallResult.Success(new { status = "ok" });
        }

        if (rawResult is string textResult)
        {
            return new ToolCallResult { Content = [new TextContentItem(textResult)] };
        }

        return ToolCallResult.Success(rawResult);
    }

    private static bool TryGetPropertyCaseInsensitive(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (JsonProperty prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static object? ConvertJsonElementToParameter(JsonElement element, Type targetType)
    {
        Type underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (underlying == typeof(string))
        {
            return element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
        }
        if (underlying == typeof(int))
        {
            return element.ValueKind == JsonValueKind.Number
                ? element.GetInt32()
                : int.Parse(element.GetString() ?? "0");
        }
        if (underlying == typeof(long))
        {
            return element.ValueKind == JsonValueKind.Number
                ? element.GetInt64()
                : long.Parse(element.GetString() ?? "0");
        }
        if (underlying == typeof(double))
        {
            return element.ValueKind == JsonValueKind.Number
                ? element.GetDouble()
                : double.Parse(element.GetString() ?? "0");
        }
        if (underlying == typeof(bool))
        {
            if (element.ValueKind == JsonValueKind.True) return true;
            if (element.ValueKind == JsonValueKind.False) return false;
            return bool.Parse(element.GetString() ?? "false");
        }
        if (underlying.IsEnum)
        {
            string? str = element.GetString();
            if (!string.IsNullOrWhiteSpace(str) && Enum.TryParse(underlying, str, ignoreCase: true, out object? parsedEnum))
            {
                return parsedEnum;
            }
        }
        return JsonSerializer.Deserialize(element.GetRawText(), targetType);
    }

    private static ToolPropertySchema BuildNestedSchemaFromClrType(Type clrType, string fallbackTitle)
    {
        if (clrType.IsEnum)
        {
            return new ToolPropertySchema
            {
                Type = "string",
                Title = fallbackTitle,
                Description = $"Enum value ({clrType.Name}).",
                Enum = Enum.GetNames(clrType).Select(n => n.ToLowerInvariant()).ToList()
            };
        }

        string mapped = MapClrTypeToJsonSchemaType(clrType);
        if (mapped == "object")
        {
            return new ToolPropertySchema
            {
                Type = "object",
                Title = fallbackTitle,
                Description = $"Object item ({clrType.Name}).",
                Properties = BuildObjectPropertiesFromClrType(clrType)
            };
        }

        return new ToolPropertySchema
        {
            Type = mapped,
            Title = fallbackTitle,
            Description = $"Item of type {mapped}."
        };
    }

    private static Dictionary<string, ToolPropertySchema> BuildObjectPropertiesFromClrType(Type clrType)
    {
        Dictionary<string, ToolPropertySchema> props = new(StringComparer.Ordinal);
        foreach (PropertyInfo pi in clrType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!pi.CanRead || pi.GetIndexParameters().Length > 0) continue;
            Type pt = Nullable.GetUnderlyingType(pi.PropertyType) ?? pi.PropertyType;
            string pType = MapClrTypeToJsonSchemaType(pt);
            if (pType is "object" or "array") continue;
            props[pi.Name] = new ToolPropertySchema
            {
                Type = pt.IsEnum ? "string" : pType,
                Title = pi.Name,
                Description = pi.GetCustomAttribute<DescriptionAttribute>()?.Description ?? $"{pi.Name} property.",
                Enum = pt.IsEnum ? Enum.GetNames(pt).Select(n => n.ToLowerInvariant()).ToList() : null
            };
        }

        if (props.Count == 0)
        {
            props["value"] = new ToolPropertySchema
            {
                Type = "string",
                Title = "Value",
                Description = "Serialized value or key."
            };
        }

        return props;
    }

    private static string MapClrTypeToJsonSchemaType(Type type)
    {
        if (type == typeof(string) || type == typeof(Guid)) return "string";
        if (type == typeof(bool)) return "boolean";
        if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte)) return "integer";
        if (type == typeof(double) || type == typeof(float) || type == typeof(decimal)) return "number";
        if (type.IsArray || (typeof(System.Collections.IEnumerable).IsAssignableFrom(type) && type != typeof(string))) return "array";
        return "object";
    }

    private static string ToSnakeCase(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "tool";
        if (name.EndsWith("Async", StringComparison.Ordinal) && name.Length > 5)
        {
            name = name[..^5];
        }

        StringBuilder sb = new(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && name[i - 1] != '_')
                {
                    sb.Append('_');
                }
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
