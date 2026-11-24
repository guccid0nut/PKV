// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Kongor.Cli.Mcp.Protocol;

/// <summary>
/// Standard JSON-RPC 2.0 Error Codes.
/// </summary>
public static class JsonRpcErrorCode
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
}

/// <summary>
/// JSON-RPC 2.0 Identifier supporting numeric, string, and null representations.
/// </summary>
[JsonConverter(typeof(JsonRpcIdConverter))]
public readonly struct JsonRpcId : IEquatable<JsonRpcId>
{
    public object? Value { get; }

    public bool IsNull => Value is null;
    public bool IsNumber => Value is long or int or double;
    public bool IsString => Value is string;

    public JsonRpcId(long number) => Value = number;
    public JsonRpcId(string text) => Value = text;
    public JsonRpcId(object? value) => Value = value;

    public static implicit operator JsonRpcId(long number) => new(number);
    public static implicit operator JsonRpcId(int number) => new(number);
    public static implicit operator JsonRpcId(string text) => new(text);

    public override string ToString() => Value?.ToString() ?? "null";

    public bool Equals(JsonRpcId other) => Equals(Value, other.Value);
    public override bool Equals(object? obj) => obj is JsonRpcId other && Equals(other);
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;
    public static bool operator ==(JsonRpcId left, JsonRpcId right) => left.Equals(right);
    public static bool operator !=(JsonRpcId left, JsonRpcId right) => !left.Equals(right);
}

public sealed class JsonRpcIdConverter : JsonConverter<JsonRpcId>
{
    public override JsonRpcId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Number => reader.TryGetInt64(out long l) ? new JsonRpcId(l) : new JsonRpcId(reader.GetDouble()),
            JsonTokenType.String => new JsonRpcId(reader.GetString()!),
            JsonTokenType.Null => default,
            _ => throw new JsonException($"Unsupported token type for JSON-RPC ID: {reader.TokenType}")
        };
    }

    public override void Write(Utf8JsonWriter writer, JsonRpcId value, JsonSerializerOptions options)
    {
        switch (value.Value)
        {
            case long l:
                writer.WriteNumberValue(l);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case null:
                writer.WriteNullValue();
                break;
            default:
                writer.WriteStringValue(value.Value.ToString());
                break;
        }
    }
}

/// <summary>
/// JSON-RPC 2.0 incoming request or notification representation.
/// </summary>
public sealed class JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonRpcId? Id { get; set; }

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("params")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Params { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    [JsonIgnore]
    public string? RawJson { get; set; }

    [JsonIgnore]
    public bool IsNotification => Id is null;
}

/// <summary>
/// JSON-RPC 2.0 Response message.
/// </summary>
public sealed class JsonRpcResponse
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public JsonRpcId? Id { get; set; }

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Result { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonRpcError? Error { get; set; }

    public static JsonRpcResponse Success(JsonRpcId? id, object? result) => new()
    {
        Id = id,
        Result = result ?? new JsonObject()
    };

    public static JsonRpcResponse CreateError(JsonRpcId? id, int code, string message, object? data = null) => new()
    {
        Id = id,
        Error = new JsonRpcError
        {
            Code = code,
            Message = message,
            Data = data
        }
    };
}

/// <summary>
/// JSON-RPC 2.0 Error details.
/// </summary>
public sealed class JsonRpcError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Data { get; set; }
}

/// <summary>
/// JSON-RPC 2.0 Outgoing Notification message.
/// </summary>
public sealed class JsonRpcNotification
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("params")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Params { get; set; }

    public static JsonRpcNotification Create(string method, object? @params = null) => new()
    {
        Method = method,
        Params = @params
    };
}

/// <summary>
/// Shared JSON serialization options for MCP JSON-RPC protocol.
/// </summary>
public static class JsonRpcSerializer
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public static readonly JsonSerializerOptions IndentedOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static string SerializeIndented<T>(T value) => JsonSerializer.Serialize(value, IndentedOptions);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
    public static T? Deserialize<T>(JsonElement element) => JsonSerializer.Deserialize<T>(element.GetRawText(), Options);
}