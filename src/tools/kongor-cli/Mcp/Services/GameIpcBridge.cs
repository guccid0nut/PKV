// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NEXUS.Contracts.Ipc;
using ProjectKongor.Contracts.Generated;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// OpCodes for ProjectKongor NativeGuard Named Pipe IPC Protocol (\\.\pipe\kongor-native-client).
/// Generated 1:1 from <see cref="NativeGuardOpCode"/> (data/schemas/protocol.json).
/// Distinct from <see cref="NEXUS.Contracts.Ipc.IpcOpCode"/> on willowmaker-ipc.
/// </summary>
public enum IpcOpCode : ushort
{
    Ping = (ushort)NativeGuardOpCode.Ping,
    Pong = (ushort)NativeGuardOpCode.Pong,
    ExecuteCommand = (ushort)NativeGuardOpCode.ExecuteCommand,
    CommandResponse = (ushort)NativeGuardOpCode.CommandResponse,
    GetEngineState = (ushort)NativeGuardOpCode.GetEngineState,
    EngineStateResponse = (ushort)NativeGuardOpCode.EngineStateResponse,
    GetLuaMemoryMetrics = (ushort)NativeGuardOpCode.GetLuaMemoryMetrics,
    LuaMemoryResponse = (ushort)NativeGuardOpCode.LuaMemoryResponse,
    DispatchUiEvent = (ushort)NativeGuardOpCode.DispatchUiEvent,
    DispatchBackgroundHumanClick = (ushort)NativeGuardOpCode.DispatchBackgroundHumanClick,
    EvalLuaSync = (ushort)NativeGuardOpCode.EvalLuaSync,
    EvalLuaResponse = (ushort)NativeGuardOpCode.EvalLuaResponse,
    Authenticate = (ushort)NativeGuardOpCode.Authenticate,
    AuthResponse = (ushort)NativeGuardOpCode.AuthResponse,
    InjectSubTickInput = (ushort)NativeGuardOpCode.InjectSubTickInput,
    SubTickInputResponse = (ushort)NativeGuardOpCode.SubTickInputResponse,
    UpdateTelemetryMetrics = (ushort)NativeGuardOpCode.UpdateTelemetryMetrics,
    TelemetryMetricsResponse = (ushort)NativeGuardOpCode.TelemetryMetricsResponse,
    DispatchVoiceUiTrigger = (ushort)NativeGuardOpCode.DispatchVoiceUiTrigger,
    DispatchVoiceUiTriggerResponse = (ushort)NativeGuardOpCode.DispatchVoiceUiTriggerResponse,
    GetVoicePttState = (ushort)NativeGuardOpCode.GetVoicePttState,
    GetVoicePttStateResponse = (ushort)NativeGuardOpCode.GetVoicePttStateResponse,
    GetRawInputState = (ushort)NativeGuardOpCode.GetRawInputState,
    GetRawInputStateResponse = (ushort)NativeGuardOpCode.GetRawInputStateResponse,
    SetRawInputConfig = (ushort)NativeGuardOpCode.SetRawInputConfig,
    SetRawInputConfigResponse = (ushort)NativeGuardOpCode.SetRawInputConfigResponse,
    PrewarmHero = (ushort)NativeGuardOpCode.PrewarmHero,
    PrewarmCompetitivePool = (ushort)NativeGuardOpCode.PrewarmCompetitivePool,
    GetPrewarmStats = (ushort)NativeGuardOpCode.GetPrewarmStats,
    PrewarmHeroResponse = (ushort)NativeGuardOpCode.PrewarmHeroResponse,
    PrewarmCompetitivePoolResponse = (ushort)NativeGuardOpCode.PrewarmCompetitivePoolResponse,
    GetPrewarmStatsResponse = (ushort)NativeGuardOpCode.GetPrewarmStatsResponse,
    GetJobPoolStats = (ushort)NativeGuardOpCode.GetJobPoolStats,
    SetJobPoolConfig = (ushort)NativeGuardOpCode.SetJobPoolConfig,
    GetJobPoolStatsResponse = (ushort)NativeGuardOpCode.GetJobPoolStatsResponse,
    SetJobPoolConfigResponse = (ushort)NativeGuardOpCode.SetJobPoolConfigResponse,
    GetPredictionStats = (ushort)NativeGuardOpCode.GetPredictionStats,
    SetPredictionConfig = (ushort)NativeGuardOpCode.SetPredictionConfig,
    GetPredictionStatsResponse = (ushort)NativeGuardOpCode.GetPredictionStatsResponse,
    SetPredictionConfigResponse = (ushort)NativeGuardOpCode.SetPredictionConfigResponse,
    HookToggleRequest = (ushort)NativeGuardOpCode.HookToggleRequest,
    HookToggleResponse = (ushort)NativeGuardOpCode.HookToggleResponse,
    PeekStructRequest = (ushort)NativeGuardOpCode.PeekStructRequest,
    PeekStructResponse = (ushort)NativeGuardOpCode.PeekStructResponse,
    DuplexLuaRpc = (ushort)NativeGuardOpCode.DuplexLuaRpc,
    DuplexLuaRpcResponse = (ushort)NativeGuardOpCode.DuplexLuaRpcResponse
}

/// <summary>
/// Result of a binary duplex Lua evaluation RPC (0x0080 / 0x0081).
/// </summary>
public readonly record struct DuplexLuaRpcResult(
    bool Success,
    bool TimedOut,
    uint RequestId,
    uint ElapsedMicroseconds,
    string ResultOrError,
    bool TransportUnsupported = false)
{
    public bool IsUninitializedDefault => ResultOrError is null && RequestId == 0;
}

/// <summary>
/// Deserializes unsigned 32-bit native JSON fields (%u in dllmain.cpp) into <see cref="long"/>,
/// mapping K2's INVALID_INDEX sentinel (0xFFFFFFFF / 4294967295) and negative values to 0L.
/// </summary>
internal sealed class Uint32SentinelZeroLongJsonConverter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return 0L;
        }

        if (reader.TokenType == JsonTokenType.Number)
        {
            if (reader.TryGetInt64(out long signedValue))
            {
                if (signedValue < 0 || signedValue == uint.MaxValue)
                {
                    return 0L;
                }
                return signedValue;
            }

            if (reader.TryGetUInt64(out ulong unsignedValue))
            {
                if (unsignedValue == uint.MaxValue || unsignedValue == ulong.MaxValue)
                {
                    return 0L;
                }
                return unsignedValue > (ulong)long.MaxValue ? long.MaxValue : (long)unsignedValue;
            }
        }

        if (reader.TokenType == JsonTokenType.String && long.TryParse(reader.GetString(), out long parsed))
        {
            return parsed < 0 || parsed == uint.MaxValue ? 0L : parsed;
        }

        return 0L;
    }

    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value);
}

/// <summary>
/// Deserializes unsigned 32-bit native JSON fields (%u in dllmain.cpp) safely into <see cref="int"/>,
/// mapping K2's 0xFFFFFFFF sentinel (4294967295) to 0 and clamping values above <see cref="int.MaxValue"/>.
/// </summary>
internal sealed class SafeUint32ToIntJsonConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return 0;
        }

        if (reader.TokenType == JsonTokenType.Number)
        {
            if (reader.TryGetInt64(out long signedValue))
            {
                if (signedValue < 0 || signedValue == uint.MaxValue)
                {
                    return 0;
                }
                return signedValue > int.MaxValue ? int.MaxValue : (int)signedValue;
            }

            if (reader.TryGetUInt64(out ulong unsignedValue))
            {
                if (unsignedValue == uint.MaxValue || unsignedValue == ulong.MaxValue)
                {
                    return 0;
                }
                return unsignedValue > (ulong)int.MaxValue ? int.MaxValue : (int)unsignedValue;
            }
        }

        if (reader.TokenType == JsonTokenType.String && long.TryParse(reader.GetString(), out long parsed))
        {
            if (parsed < 0 || parsed == uint.MaxValue)
            {
                return 0;
            }
            return parsed > int.MaxValue ? int.MaxValue : (int)parsed;
        }

        return 0;
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value);
}

/// <summary>
/// Deserializes unsigned 64-bit native JSON fields (%llu in dllmain.cpp) safely into <see cref="long"/>,
/// mapping 0xFFFFFFFFFFFFFFFF to 0L and clamping values above <see cref="long.MaxValue"/>.
/// </summary>
internal sealed class SafeUInt64ToLongJsonConverter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return 0L;
        }

        if (reader.TokenType == JsonTokenType.Number)
        {
            if (reader.TryGetInt64(out long signedValue))
            {
                return signedValue < 0 ? 0L : signedValue;
            }

            if (reader.TryGetUInt64(out ulong unsignedValue))
            {
                if (unsignedValue == ulong.MaxValue)
                {
                    return 0L;
                }
                return unsignedValue > (ulong)long.MaxValue ? long.MaxValue : (long)unsignedValue;
            }
        }

        if (reader.TokenType == JsonTokenType.String && ulong.TryParse(reader.GetString(), out ulong parsed))
        {
            if (parsed == ulong.MaxValue)
            {
                return 0L;
            }
            return parsed > (ulong)long.MaxValue ? long.MaxValue : (long)parsed;
        }

        return 0L;
    }

    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value);
}

/// <summary>
/// Deserializes optional unsigned 64-bit native JSON fields (%llu in dllmain.cpp) safely into <see cref="Nullable{Int64}"/>,
/// preserving null when omitted or null on older binaries and clamping values above <see cref="long.MaxValue"/>.
/// </summary>
internal sealed class NullableSafeUInt64ToLongJsonConverter : JsonConverter<long?>
{
    public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.Number)
        {
            if (reader.TryGetInt64(out long signedValue))
            {
                return signedValue < 0 ? 0L : signedValue;
            }

            if (reader.TryGetUInt64(out ulong unsignedValue))
            {
                if (unsignedValue == ulong.MaxValue)
                {
                    return 0L;
                }
                return unsignedValue > (ulong)long.MaxValue ? long.MaxValue : (long)unsignedValue;
            }
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            string? str = reader.GetString();
            if (string.IsNullOrWhiteSpace(str))
            {
                return null;
            }
            if (ulong.TryParse(str, out ulong parsed))
            {
                if (parsed == ulong.MaxValue)
                {
                    return 0L;
                }
                return parsed > (ulong)long.MaxValue ? long.MaxValue : (long)parsed;
            }
        }

        return null;
    }

    public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
        {
            writer.WriteNumberValue(value.Value);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}

/// <summary>
/// Structured engine telemetry snapshot exchanged over IPC.
/// </summary>
public sealed class EngineStateDto
{
    public double CurrentFps { get; set; }

    [JsonConverter(typeof(SafeUInt64ToLongJsonConverter))]
    public long MemoryWorkingSetBytes { get; set; }

    public double MemoryWorkingSetMb => Math.Round((double)MemoryWorkingSetBytes / (1024.0 * 1024.0), 2);

    [JsonConverter(typeof(SafeUint32ToIntJsonConverter))]
    public int LuaMemoryKb { get; set; }

    [JsonConverter(typeof(Uint32SentinelZeroLongJsonConverter))]
    public long ActiveMatchId { get; set; }

    [JsonIgnore]
    public long? ServerMatchId => ActiveMatchId > 0 ? ActiveMatchId : null;

    public string CurrentMap { get; set; } = string.Empty;
    public string PlayerName { get; set; } = string.Empty;

    [JsonConverter(typeof(SafeUint32ToIntJsonConverter))]
    public int ConnectedPlayers { get; set; }

    [JsonConverter(typeof(SafeUInt64ToLongJsonConverter))]
    public long UptimeSeconds { get; set; }

    [JsonConverter(typeof(SafeUint32ToIntJsonConverter))]
    public int GamePhase { get; set; }

    public string GamePhaseName { get; set; } = "Login (0-1)";
    public bool IsSpectator { get; set; }
    public int ActiveSpectatorCount { get; set; }
    public int MaxSpectators { get; set; }
    public string? SpectatorTarget { get; set; }

    [JsonConverter(typeof(SafeUint32ToIntJsonConverter))]
    public int CanonicalGamePhase { get; set; }

    public bool IsAuthenticated { get; set; }
    public bool IsActiveMatch { get; set; }
    public bool Connected { get; set; } = false;

    [JsonConverter(typeof(SafeUint32ToIntJsonConverter))]
    public int ActiveEntities { get; set; }

    [JsonConverter(typeof(Uint32SentinelZeroLongJsonConverter))]
    public long ServerTick { get; set; }

    public int? HostFrame { get; set; }

    [JsonConverter(typeof(NullableSafeUInt64ToLongJsonConverter))]
    public long? MsSinceHostFrameAdvance { get; set; }

    public bool MainThreadBusy { get; set; }

    [JsonConverter(typeof(SafeUInt64ToLongJsonConverter))]
    public long ActiveOpElapsedMs { get; set; }
}

/// <summary>
/// 12-byte framed binary message for the ProjectKongor IPC protocol.
/// Magic (2 bytes: 0x574D) | OpCode (2 bytes) | PayloadLength (4 bytes) | RequestId (4 bytes) | Payload (N bytes).
/// </summary>
public sealed class IpcMessage
{
    public const ushort HeaderMagic = 0x574D; // 'W', 'M'
    public const int HeaderSize = 12;
    public const int LegacyHeaderSize = 8; // HeaderSize = 8 for legacy backwards compatibility
    public const int MaxPayloadSize = 16 * 1024 * 1024; // 16 MB max frame size

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public IpcOpCode OpCode { get; init; }
    public uint RequestId { get; init; }
    public byte[] Payload { get; init; } = [];

    public string PayloadString => Payload.Length == 0 ? string.Empty : Encoding.UTF8.GetString(Payload);

    public static IpcMessage CreateString(IpcOpCode opCode, string text, uint requestId = 0)
    {
        byte[] bytes = string.IsNullOrEmpty(text) ? [] : Encoding.UTF8.GetBytes(text);
        return new IpcMessage
        {
            OpCode = opCode,
            RequestId = requestId,
            Payload = bytes
        };
    }

    public static IpcMessage CreateJson<T>(IpcOpCode opCode, T data, uint requestId = 0)
    {
        ArgumentNullException.ThrowIfNull(data);
        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions);
        return new IpcMessage
        {
            OpCode = opCode,
            RequestId = requestId,
            Payload = jsonBytes
        };
    }

    public T? ReadJson<T>()
    {
        if (Payload.Length == 0) return default;
        return JsonSerializer.Deserialize<T>(Payload, JsonOptions);
    }

    public static IpcMessage CreateBytes(IpcOpCode opCode, ReadOnlySpan<byte> bytes, uint requestId = 0)
    {
        return new IpcMessage
        {
            OpCode = opCode,
            RequestId = requestId,
            Payload = bytes.ToArray()
        };
    }

    public static byte[] Serialize(IpcMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        int payloadLength = message.Payload.Length;
        if (payloadLength > MaxPayloadSize)
            throw new InvalidOperationException($"Payload size {payloadLength} exceeds maximum limit of {MaxPayloadSize} bytes.");

        byte[] buffer = new byte[HeaderSize + payloadLength];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(0, 2), HeaderMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(2, 2), (ushort)message.OpCode);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4, 4), payloadLength);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8, 4), message.RequestId);

        if (payloadLength > 0)
        {
            message.Payload.CopyTo(buffer.AsSpan(HeaderSize));
        }

        return buffer;
    }

    public static bool TryDeserialize(ReadOnlySpan<byte> buffer, out IpcMessage? message, out int bytesConsumed)
    {
        message = null;
        bytesConsumed = 0;

        if (buffer.Length < HeaderSize)
            return false;

        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(0, 2));
        if (magic != HeaderMagic)
            throw new InvalidDataException($"Invalid IPC magic header: 0x{magic:X4}. Expected 0x{HeaderMagic:X4}.");

        IpcOpCode opCode = (IpcOpCode)BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(2, 2));
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(4, 4));

        if (payloadLength is < 0 or > MaxPayloadSize)
            throw new InvalidDataException($"Invalid IPC payload length: {payloadLength} bytes.");

        if (buffer.Length < HeaderSize + payloadLength)
            return false; // Incomplete packet, wait for more bytes

        uint requestId = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(8, 4));
        byte[] payload = payloadLength == 0 ? [] : buffer.Slice(HeaderSize, payloadLength).ToArray();
        bytesConsumed = HeaderSize + payloadLength;

        message = new IpcMessage
        {
            OpCode = opCode,
            RequestId = requestId,
            Payload = payload
        };

        return true;
    }

    public static bool TryDeserializeLegacy(ReadOnlySpan<byte> buffer, out IpcMessage? message, out int bytesConsumed)
    {
        message = null;
        bytesConsumed = 0;

        if (buffer.Length < LegacyHeaderSize) return false;

        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(buffer[..2]);
        if (magic != HeaderMagic)
            throw new InvalidDataException($"Invalid IPC magic header: 0x{magic:X4}. Expected 0x{HeaderMagic:X4}.");

        IpcOpCode opCode = (IpcOpCode)BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(2, 2));
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(4, 4));

        if (payloadLength is < 0 or > MaxPayloadSize)
            throw new InvalidDataException($"Invalid IPC payload length: {payloadLength} bytes.");

        if (buffer.Length < LegacyHeaderSize + payloadLength) return false;

        byte[] payload = payloadLength == 0 ? [] : buffer.Slice(LegacyHeaderSize, payloadLength).ToArray();
        bytesConsumed = LegacyHeaderSize + payloadLength;

        message = new IpcMessage
        {
            OpCode = opCode,
            RequestId = 0,
            Payload = payload
        };

        return true;
    }

    public static bool TryDeserializeLine(ReadOnlySpan<byte> buffer, out IpcMessage? message, out int bytesConsumed)
    {
        message = null;
        bytesConsumed = 0;

        int newlineIndex = buffer.IndexOf((byte)'\n');
        if (newlineIndex < 0)
        {
            if (buffer.Length > MaxPayloadSize)
                throw new InvalidDataException($"Line exceeds maximum payload size of {MaxPayloadSize} bytes.");
            return false;
        }

        if (newlineIndex > MaxPayloadSize)
            throw new InvalidDataException($"Line exceeds maximum payload size of {MaxPayloadSize} bytes.");

        ReadOnlySpan<byte> lineSpan = buffer[..newlineIndex];
        if (!lineSpan.IsEmpty && lineSpan[^1] == (byte)'\r')
            lineSpan = lineSpan[..^1];

        string text = Encoding.UTF8.GetString(lineSpan).Trim();
        bytesConsumed = newlineIndex + 1;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        uint reqId = 0;
        IpcOpCode opCode = IpcOpCode.CommandResponse;

        // Parse JSON response attributes if formatted as JSON
        if (text.StartsWith('{') && text.EndsWith('}'))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(text);
                JsonElement root = doc.RootElement;
                if (root.TryGetProperty("id", out JsonElement idProp) && idProp.TryGetUInt32(out uint parsedId))
                {
                    reqId = parsedId;
                }
                else if (root.TryGetProperty("requestId", out JsonElement reqIdProp) && reqIdProp.TryGetUInt32(out uint parsedReqId))
                {
                    reqId = parsedReqId;
                }

                if (root.TryGetProperty("opCode", out JsonElement opProp) && opProp.TryGetUInt16(out ushort parsedOp))
                {
                    opCode = (IpcOpCode)parsedOp;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[IpcMessage] Non-JSON line payload fallback: {ex.Message}");
            }
        }

        message = new IpcMessage
        {
            OpCode = opCode,
            RequestId = reqId,
            Payload = Encoding.UTF8.GetBytes(text)
        };
        return true;
    }

    public static bool TryDeserializeAny(ReadOnlySpan<byte> buffer, out IpcMessage? message, out int bytesConsumed)
    {
        message = null;
        bytesConsumed = 0;

        if (buffer.IsEmpty) return false;

        // 1. Binary frames starting with HeaderMagic (0x574D)
        if (buffer.Length >= 2 && BinaryPrimitives.ReadUInt16LittleEndian(buffer[..2]) == HeaderMagic)
        {
            if (buffer.Length < LegacyHeaderSize) return false;

            int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(4, 4));
            if (payloadLength is < 0 or > MaxPayloadSize)
                throw new InvalidDataException($"Invalid IPC payload length: {payloadLength} bytes.");

            int legacyFrameSize = LegacyHeaderSize + payloadLength;
            int modernFrameSize = HeaderSize + payloadLength;

            bool hasLegacyNextMagic = buffer.Length >= legacyFrameSize + 2 &&
                                      BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(legacyFrameSize, 2)) == HeaderMagic;
            bool hasModernNextMagic = buffer.Length >= modernFrameSize + 2 &&
                                      BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(modernFrameSize, 2)) == HeaderMagic;
            bool hasLegacyNextLine = buffer.Length > legacyFrameSize &&
                                     buffer.Slice(legacyFrameSize).IndexOf((byte)'\n') >= 0;

            if (hasLegacyNextMagic && !hasModernNextMagic)
            {
                if (TryDeserializeLegacy(buffer, out message, out bytesConsumed))
                    return true;
            }
            else if (hasLegacyNextLine && !hasModernNextMagic)
            {
                if (TryDeserializeLegacy(buffer, out message, out bytesConsumed))
                    return true;
            }
            else if (hasModernNextMagic && !hasLegacyNextMagic)
            {
                if (TryDeserialize(buffer, out message, out bytesConsumed))
                    return true;
            }
            else
            {
                if (buffer.Length >= modernFrameSize)
                {
                    if (TryDeserialize(buffer, out message, out bytesConsumed))
                        return true;
                }
                else if (buffer.Length >= legacyFrameSize)
                {
                    if (TryDeserializeLegacy(buffer, out message, out bytesConsumed))
                        return true;
                }
            }

            return false; // Incomplete binary frame, wait for more bytes
        }

        // 2. Text-based newline-delimited command framing
        if (TryDeserializeLine(buffer, out message, out bytesConsumed))
            return true;

        if (buffer.Length > MaxPayloadSize)
            throw new InvalidDataException($"Incoming IPC stream ({buffer.Length} bytes) exceeded maximum frame capacity without valid delimiter.");

        return false;
    }
}

/// <summary>
/// Interface for In-Game IPC Command Bridge over Named Pipes.
/// </summary>
public interface IGameIpcBridge : IAsyncDisposable
{
    bool IsConnected { get; }
    bool IsAuthenticated { get; }
    string? AuthKey { get; set; }
    string PipeName { get; }
    string ServerName { get; }

    event Action? OnConnected;
    event Action<Exception?>? OnDisconnected;
    event Action<IpcMessage>? OnMessageReceived;
    event Action<string>? OnLogStreamReceived;

    Task ConnectAsync(int timeoutMs = 3000, CancellationToken cancellationToken = default);
    Task<bool> AuthenticateAsync(string? authKey = null, int timeoutMs = 2000, CancellationToken cancellationToken = default);
    Task<string> ExecuteCommandAsync(string command, int timeoutMs = 3000, CancellationToken cancellationToken = default);
    Task<string> ExecuteLuaAsync(string luaCode, int timeoutMs = 3000, CancellationToken cancellationToken = default)
    {
        string raw = Handlers.ClientEvalLuaHandler.NormalizeRawLua(luaCode);
        string escaped = raw
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n");
        return ExecuteCommandAsync($"Script \"{escaped}\"", timeoutMs, cancellationToken);
    }
    Task<DuplexLuaRpcResult> EvalLuaDuplexAsync(string luaCode, uint timeoutMs = 3000, CancellationToken ct = default)
        => Task.FromResult(new DuplexLuaRpcResult(false, false, 0, 0, "Duplex Lua RPC not supported by this bridge", TransportUnsupported: true));
    Task<EngineStateDto?> GetEngineStateAsync(int timeoutMs = 2000, CancellationToken cancellationToken = default);
    Task<(ulong LuaMemoryBytes, bool GcPacingActive, ulong FullGcCollections)> GetLuaMemoryMetricsAsync(int timeoutMs = 2000, CancellationToken cancellationToken = default)
        => Task.FromResult((0UL, false, 0UL));
    Task<long> PingAsync(int timeoutMs = 1000, CancellationToken cancellationToken = default);
    Task SendMessageAsync(IpcMessage message, CancellationToken cancellationToken = default);
    Task<IpcMessage?> SendRequestAsync(IpcMessage request, int timeoutMs = 5000, CancellationToken cancellationToken = default);
    Task<string> DispatchUiEventAsync(string interfaceName, string widgetName, string eventType, int? eventIndex = null, int timeoutMs = 3000, CancellationToken cancellationToken = default);
    Task<string> DispatchBackgroundHumanClickAsync(string interfaceName, string widgetName, int clientX, int clientY, bool rightClick = false, int timeoutMs = 3000, CancellationToken cancellationToken = default)
        => DispatchUiEventAsync(interfaceName, widgetName, rightClick ? "rightclick" : "click", null, timeoutMs, cancellationToken);
    Task<JsonElement> WaitForEventAsync(string eventName, int timeoutMs = 5000, CancellationToken cancellationToken = default) => Task.FromResult(default(JsonElement));
    Task<T?> ExecuteRpcAsync<T>(string method, object? parameters = null, int timeoutMs = 5000, CancellationToken cancellationToken = default) => Task.FromResult<T?>(default);
    Task<SubTickInputResult> InjectSubTickInputAsync(string action, long hardwareTimestampQpc = 0, uint inputSequence = 0, int timeoutMs = 3000, CancellationToken cancellationToken = default)
        => Task.FromResult(new SubTickInputResult(false, inputSequence, 0f, 0, "Not implemented by base bridge"));
    Task<bool> PushTelemetryUpdateAsync(uint drawCalls, uint activeEntities, uint particlesAlive, uint pingMicroseconds, uint serverTick, float inputLatencyMs = 0f, int timeoutMs = 2000, CancellationToken cancellationToken = default)
        => Task.FromResult(false);
    Task DisconnectAsync();
}

/// <summary>
/// Thread-safe Named Pipe IPC Bridge communicating directly with hon_x64 or WILLOWMAKER.
/// </summary>
public sealed class GameIpcBridge : IGameIpcBridge
{
    public const string DefaultPipeName = "willowmaker-ipc";

    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private int _nextRequestId;
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<IpcMessage>> _pendingRequestsByRequestId = new();
    private readonly ConcurrentDictionary<IpcOpCode, ConcurrentQueue<TaskCompletionSource<IpcMessage>>> _pendingRequestsByOpCode = new();
    private readonly Action<string>? _logAction;
    private readonly NexusEventCoordinator _eventCoordinator;

    private NamedPipeClientStream? _pipeClient;
    private Task? _readLoopTask;
    private bool _isDisposed;
    private bool _manuallyDisconnected;

    public string PipeName { get; private set; }
    public string ServerName { get; }
    public bool IsConnected => _pipeClient != null && _pipeClient.IsConnected && !_isDisposed;
    public bool IsAuthenticated { get; private set; }
    public string? AuthKey { get; set; }
    public ScrcpyDroppableControlQueue<IpcMessage> ControlQueue { get; } = new();
    public ScrcpyAckSyncBarrier AckSyncBarrier { get; } = new();

    public event Action? OnConnected;
    public event Action<Exception?>? OnDisconnected;
    public event Action<IpcMessage>? OnMessageReceived;
    public event Action<string>? OnLogStreamReceived;

    public GameIpcBridge(string? pipeName = null, string serverName = ".", Action<string>? logAction = null, string? authKey = null)
    {
        PipeName = pipeName ?? DefaultPipeName;
        ServerName = serverName;
        _logAction = logAction;
        AuthKey = authKey ?? Environment.GetEnvironmentVariable("KONGOR_IPC_AUTH_KEY");
        _eventCoordinator = new NexusEventCoordinator((cmd, timeout, ct) => ExecuteCommandAsync(cmd, timeout, ct), _logAction);
    }

    public bool EnqueueControlMessage(IpcMessage message, bool isDroppable)
    {
        ArgumentNullException.ThrowIfNull(message);
        return ControlQueue.Push(message, isDroppable);
    }

    private void Log(string message)
    {
        _logAction?.Invoke(message);
    }

    public async Task ConnectAsync(int timeoutMs = 3000, CancellationToken cancellationToken = default)
    {
        _manuallyDisconnected = false;
        await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConnected) return;

            CleanupExistingConnection();

            string[] candidatePipes = string.Equals(PipeName, DefaultPipeName, StringComparison.OrdinalIgnoreCase)
                ? (McpIpcServer.IsPipeHostedInCurrentProcess("willowmaker-ipc")
                    ? ["kongor-native-client"]
                    : ["kongor-native-client", "willowmaker-ipc"])
                : [PipeName];

            NamedPipeClientStream? connectedClient = null;
            string? connectedPipe = null;

            foreach (string candidate in candidatePipes)
            {
                Log($"Connecting to Named Pipe: \\\\{ServerName}\\pipe\\{candidate} (Timeout: {timeoutMs}ms)...");

                NamedPipeClientStream client = new(
                    ServerName,
                    candidate,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous | PipeOptions.WriteThrough);

                int attemptTimeout = candidatePipes.Length > 1 ? Math.Max(1000, timeoutMs / candidatePipes.Length) : timeoutMs;
                using CancellationTokenSource timeoutCts = new(attemptTimeout);
                using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token, _cts.Token);

                try
                {
                    await client.ConnectAsync(attemptTimeout, linkedCts.Token).ConfigureAwait(false);
                    connectedClient = client;
                    connectedPipe = candidate;
                    break;
                }
                catch (Exception ex)
                {
                    Log($"[GameIpcBridge] Connect attempt to pipe '{candidate}' failed: {ex.Message}");
                    client.Dispose();
                }
            }

            if (connectedClient == null)
            {
                throw new TimeoutException($"Failed to connect to Named Pipe '{PipeName}' after {timeoutMs}ms.");
            }

            _pipeClient = connectedClient;
            if (string.Equals(connectedPipe, "kongor-native-client", StringComparison.OrdinalIgnoreCase))
            {
                PipeName = connectedPipe!;
            }

            _readLoopTask = Task.Run(() => ReadLoopAsync(_cts.Token), CancellationToken.None);
            Log($"Connected successfully to Named Pipe: \\\\{ServerName}\\pipe\\{PipeName}");
            OnConnected?.Invoke();

            if (!string.IsNullOrWhiteSpace(AuthKey))
            {
                try
                {
                    await AuthenticateAsync(AuthKey, 2000, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log($"[AUTH] Auto-handshake warning: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Failed to connect to Named Pipe '\\\\{ServerName}\\pipe\\{PipeName}' within {timeoutMs}ms. Is the game running with '-pipe {PipeName}'?");
        }
        finally
        {
            try { _connectionLock.Release(); } catch (ObjectDisposedException ex) { Log($"[GameIpcBridge] Connection lock already disposed: {ex.Message}"); }
        }
    }

    public async Task<bool> AuthenticateAsync(string? authKey = null, int timeoutMs = 2000, CancellationToken cancellationToken = default)
    {
        string keyToUse = authKey ?? AuthKey ?? string.Empty;
        if (string.IsNullOrWhiteSpace(keyToUse))
        {
            Log("[AUTH] Cannot authenticate: AuthKey is null or empty.");
            IsAuthenticated = false;
            return false;
        }

        if (!IsConnected)
        {
            await ConnectAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
        }

        IpcAuthPayload payload = NEXUS.Contracts.Ipc.IpcSecurityValidator.CreateHandshakePayload(keyToUse, "kongor-cli");
        IpcMessage authMsg = IpcMessage.CreateJson(IpcOpCode.Authenticate, payload);

        IpcMessage? response = await SendRequestAsync(authMsg, timeoutMs, cancellationToken).ConfigureAwait(false);
        if (response != null)
        {
            IpcAuthResponseDto? authResp = response.ReadJson<NEXUS.Contracts.Ipc.IpcAuthResponseDto>();
            if (authResp != null && authResp.Authenticated)
            {
                IsAuthenticated = true;
                Log($"[AUTH] Handshake successful: {authResp.Message}");
                return true;
            }

            Log($"[AUTH] Handshake rejected: {authResp?.Message ?? "Unknown authentication failure"}");
        }

        IsAuthenticated = false;
        return false;
    }

    private async Task EnsureConnectedOrUpgradeAsync(int timeoutMs, CancellationToken cancellationToken)
    {
        if (IsConnected && string.Equals(PipeName, DefaultPipeName, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (Directory.GetFiles(@"\\.\pipe\", "kongor-native-client").Length > 0)
                {
                    Log("Detected active kongor-native-client pipe while connected to fallback willowmaker-ipc; upgrading connection...");
                    CleanupExistingConnection();
                }
            }
            catch (Exception ex)
            {
                Log($"[GameIpcBridge] Pipe directory enumeration skipped: {ex.Message}");
            }
        }

        if (!IsConnected)
        {
            await ConnectAsync(Math.Min(timeoutMs, 2000), cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<string> ExecuteCommandAsync(string command, int timeoutMs = 3000, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        await EnsureConnectedOrUpgradeAsync(timeoutMs, cancellationToken).ConfigureAwait(false);

        if (command.StartsWith("Script ", StringComparison.OrdinalIgnoreCase))
        {
            string raw = command[7..].Trim();
            if (!Handlers.ClientEvalLuaHandler.IsSingleOuterQuotedString(raw, '"') && raw.Contains(';'))
            {
                string escapedRaw = raw
                    .Replace("\\", "\\\\")
                    .Replace("\"", "\\\"")
                    .Replace("\r", "\\r")
                    .Replace("\n", "\\n");
                command = $"Script \"{escapedRaw}\"";
            }
        }

        Log($"Dispatching console command: '{command}' (Timeout: {timeoutMs}ms)");

        bool wasFallbackPipe = string.Equals(PipeName, DefaultPipeName, StringComparison.OrdinalIgnoreCase);
        IpcMessage request = IpcMessage.CreateString(IpcOpCode.ExecuteCommand, command);
        IpcMessage? response;
        try
        {
            response = await SendRequestAsync(request, timeoutMs, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException || (ex is TimeoutException && wasFallbackPipe))
        {
            Log($"[GameIpcBridge] Command dispatch failed on '{PipeName}' ({ex.Message}); reconnecting and retrying once...");
            CleanupExistingConnection();
            await ConnectAsync(Math.Min(timeoutMs, 1500), cancellationToken).ConfigureAwait(false);
            if (!wasFallbackPipe || !string.Equals(PipeName, DefaultPipeName, StringComparison.OrdinalIgnoreCase))
            {
                IpcMessage retryReq = IpcMessage.CreateString(IpcOpCode.ExecuteCommand, command);
                response = await SendRequestAsync(retryReq, timeoutMs, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                throw;
            }
        }

        if (response == null || response.OpCode != IpcOpCode.CommandResponse)
        {
            throw new InvalidOperationException($"Invalid or null response received for command '{command}'.");
        }

        string result = response.PayloadString;
        Log($"Received command response: '{result}'");

        if (result.Contains("[SECURITY ERROR] 401 Unauthorized"))
        {
            Log($"[SECURITY ERROR] Remote host rejected unauthenticated command: '{command}'");
            return "[SECURITY ERROR] 401 Unauthorized: Command execution blocked by Named Pipe sandbox. Please configure 'KONGOR_IPC_AUTH_KEY' to authorize mutating commands.";
        }

        return result;
    }

    public async Task<string> ExecuteLuaAsync(string luaCode, int timeoutMs = 3000, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(luaCode);

        await EnsureConnectedOrUpgradeAsync(timeoutMs, cancellationToken).ConfigureAwait(false);

        string cleanLua = Handlers.ClientEvalLuaHandler.NormalizeRawLua(luaCode);

        if (string.Equals(PipeName, "kongor-native-client", StringComparison.OrdinalIgnoreCase) ||
            PipeName.StartsWith("hon-server-native-", StringComparison.OrdinalIgnoreCase) ||
            PipeName.StartsWith("test-native-pipe", StringComparison.OrdinalIgnoreCase))
        {
            bool isTestPipe = PipeName.StartsWith("test-native-pipe", StringComparison.OrdinalIgnoreCase);
            int effectiveTimeoutMs = (!isTestPipe || timeoutMs >= 2500)
                ? Math.Max(timeoutMs, 2700)
                : timeoutMs;
            Log($"Dispatching synchronous native Lua evaluation (OpCode 0x000E): '{cleanLua}' (Timeout: {effectiveTimeoutMs}ms)");
            IpcMessage request = IpcMessage.CreateString(IpcOpCode.EvalLuaSync, cleanLua);
            IpcMessage? response;
            try
            {
                response = await SendRequestAsync(request, effectiveTimeoutMs, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Log($"[GameIpcBridge] Native Lua evaluation timed out on '{PipeName}' after {effectiveTimeoutMs}ms; recycling pipe connection without re-executing Lua to prevent duplicate side effects.");
                CleanupExistingConnection();
                throw;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                Log($"[GameIpcBridge] Native Lua evaluation failed on '{PipeName}' ({ex.Message}); reconnecting and retrying once...");
                CleanupExistingConnection();
                await ConnectAsync(Math.Min(effectiveTimeoutMs, 1500), cancellationToken).ConfigureAwait(false);
                IpcMessage retryReq = IpcMessage.CreateString(IpcOpCode.EvalLuaSync, cleanLua);
                response = await SendRequestAsync(retryReq, effectiveTimeoutMs, cancellationToken).ConfigureAwait(false);
            }

            if (response != null && response.OpCode == IpcOpCode.EvalLuaResponse)
            {
                string payloadStr = response.PayloadString;
                Log($"Received native Lua evaluation response: '{payloadStr}'");

                if (!string.IsNullOrWhiteSpace(payloadStr) && payloadStr.StartsWith('{'))
                {
                    try
                    {
                        using JsonDocument doc = JsonDocument.Parse(payloadStr);
                        JsonElement root = doc.RootElement;
                        bool success = root.TryGetProperty("success", out JsonElement succEl) && succEl.ValueKind == JsonValueKind.True;
                        if (success)
                        {
                            string resVal = root.TryGetProperty("result", out JsonElement resEl) && resEl.ValueKind == JsonValueKind.String
                                ? resEl.GetString() ?? "nil"
                                : "nil";
                            return resVal;
                        }
                        else
                        {
                            string errVal = root.TryGetProperty("error", out JsonElement errEl) && errEl.ValueKind == JsonValueKind.String
                                ? errEl.GetString() ?? "Unknown Lua evaluation error"
                                : "Unknown Lua evaluation error";
                            return $"[ERROR] {errVal}";
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"[GameIpcBridge] Non-JSON EvalLuaResponse payload fallback: {ex.Message}");
                    }
                }

                return payloadStr;
            }
        }

        string escapedFallback = cleanLua
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n");
        return await ExecuteCommandAsync($"Script \"{escapedFallback}\"", timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DuplexLuaRpcResult> EvalLuaDuplexAsync(string luaCode, uint timeoutMs = 3000, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(luaCode);

        uint effectiveTimeoutMs = timeoutMs == 0 ? 3000 : timeoutMs;
        await EnsureConnectedOrUpgradeAsync((int)effectiveTimeoutMs, ct).ConfigureAwait(false);

        string cleanLua = Handlers.ClientEvalLuaHandler.NormalizeRawLua(luaCode);
        byte[] scriptBytes = string.IsNullOrEmpty(cleanLua) ? [] : Encoding.UTF8.GetBytes(cleanLua);
        byte[] payload = new byte[8 + scriptBytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), effectiveTimeoutMs);
        uint reqId = (uint)Interlocked.Increment(ref _nextRequestId);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), reqId);
        if (scriptBytes.Length > 0)
        {
            scriptBytes.CopyTo(payload, 8);
        }

        IpcMessage request = new()
        {
            OpCode = IpcOpCode.DuplexLuaRpc,
            RequestId = reqId,
            Payload = payload
        };

        IpcMessage? response;
        try
        {
            response = await SendRequestAsync(request, (int)effectiveTimeoutMs, ct).ConfigureAwait(false);
        }
        catch (TimeoutException timeoutEx)
        {
            CleanupExistingConnection();
            return new DuplexLuaRpcResult(false, true, reqId, effectiveTimeoutMs * 1000, timeoutEx.Message);
        }

        if (response == null || response.OpCode != IpcOpCode.DuplexLuaRpcResponse)
        {
            return new DuplexLuaRpcResult(false, false, reqId, 0, "Null or invalid response opcode from engine", TransportUnsupported: true);
        }

        byte[] respPayload = response.Payload;
        if (respPayload.Length < 9)
        {
            return new DuplexLuaRpcResult(false, false, reqId, 0, "Response payload too short (< 9 bytes)", TransportUnsupported: true);
        }

        uint respReqId = BinaryPrimitives.ReadUInt32LittleEndian(respPayload.AsSpan(0, 4));
        uint elapsedUs = BinaryPrimitives.ReadUInt32LittleEndian(respPayload.AsSpan(4, 4));
        byte statusFlags = respPayload[8];
        bool ok = (statusFlags & 0x01) != 0;
        bool timedOut = (statusFlags & 0x02) != 0;
        string resultOrError = respPayload.Length > 9 ? Encoding.UTF8.GetString(respPayload, 9, respPayload.Length - 9) : string.Empty;

        return new DuplexLuaRpcResult(ok, timedOut, respReqId, elapsedUs, resultOrError);
    }

    public async Task<EngineStateDto?> GetEngineStateAsync(int timeoutMs = 2000, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            await ConnectAsync(Math.Min(timeoutMs, 2000), cancellationToken).ConfigureAwait(false);
        }

        Log($"Querying engine telemetry state (Timeout: {timeoutMs}ms)...");

        IpcMessage request = IpcMessage.CreateString(IpcOpCode.GetEngineState, "ALL");
        IpcMessage? response = await SendRequestAsync(request, timeoutMs, cancellationToken).ConfigureAwait(false);

        if (response == null || response.OpCode != IpcOpCode.EngineStateResponse)
        {
            throw new InvalidOperationException("Invalid or null telemetry response received from engine.");
        }

        EngineStateDto? state = response.ReadJson<EngineStateDto>();
        if (state != null)
        {
            state.Connected = true;
            Log($"Telemetry received: FPS: {state.CurrentFps:F1}, Memory: {state.MemoryWorkingSetMb}MB, Map: {state.CurrentMap}");
        }

        return state;
    }

    public async Task<(ulong LuaMemoryBytes, bool GcPacingActive, ulong FullGcCollections)> GetLuaMemoryMetricsAsync(
        int timeoutMs = 2000,
        CancellationToken cancellationToken = default)
    {
        await EnsureConnectedOrUpgradeAsync(timeoutMs, cancellationToken).ConfigureAwait(false);

        Log($"Querying cached main-thread Lua memory metrics (OpCode 0x0007, Timeout: {timeoutMs}ms)...");
        IpcMessage request = IpcMessage.CreateString(IpcOpCode.GetLuaMemoryMetrics, string.Empty);
        IpcMessage? response = await SendRequestAsync(request, timeoutMs, cancellationToken).ConfigureAwait(false);

        if (response == null || response.OpCode != IpcOpCode.LuaMemoryResponse)
        {
            throw new InvalidOperationException("Invalid or null LuaMemoryResponse (0x0008) received from engine.");
        }

        string payloadStr = response.PayloadString;
        ulong luaMemoryBytes = 0UL;
        bool gcPacingActive = false;
        ulong fullGcCollections = 0UL;

        if (!string.IsNullOrWhiteSpace(payloadStr) && payloadStr.StartsWith('{'))
        {
            using JsonDocument doc = JsonDocument.Parse(payloadStr);
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("luaMemoryBytes", out JsonElement memEl) && memEl.TryGetUInt64(out ulong parsedMem))
            {
                luaMemoryBytes = parsedMem;
            }
            if (root.TryGetProperty("gcPacingActive", out JsonElement gcEl) &&
                (gcEl.ValueKind == JsonValueKind.True || gcEl.ValueKind == JsonValueKind.False))
            {
                gcPacingActive = gcEl.GetBoolean();
            }
            if (root.TryGetProperty("fullGcCollections", out JsonElement fullEl) && fullEl.TryGetUInt64(out ulong parsedFull))
            {
                fullGcCollections = parsedFull;
            }
        }

        Log($"Lua memory metrics received: {luaMemoryBytes} bytes, gcPacingActive={gcPacingActive}, fullGcCollections={fullGcCollections}");
        return (luaMemoryBytes, gcPacingActive, fullGcCollections);
    }

    public async Task<long> PingAsync(int timeoutMs = 1000, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            await ConnectAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
        }

        Stopwatch sw = Stopwatch.StartNew();
        IpcMessage ping = IpcMessage.CreateString(IpcOpCode.Ping, "PING");
        IpcMessage? pong = await SendRequestAsync(ping, timeoutMs, cancellationToken).ConfigureAwait(false);
        sw.Stop();

        if (pong == null || pong.OpCode != IpcOpCode.Pong)
        {
            throw new InvalidOperationException("IPC Ping failed: invalid response received from server.");
        }

        return sw.ElapsedTicks * 1_000_000 / Stopwatch.Frequency; // Microseconds
    }

    public async Task SendMessageAsync(IpcMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!IsConnected || _pipeClient == null)
            throw new InvalidOperationException($"IPC Client is not connected to pipe '\\\\{ServerName}\\pipe\\{PipeName}'.");

        byte[] serialized = IpcMessage.Serialize(message);

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _pipeClient.WriteAsync(serialized, cancellationToken).ConfigureAwait(false);
            await _pipeClient.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { _sendLock.Release(); } catch (ObjectDisposedException ex) { Log($"[GameIpcBridge] Send lock already disposed: {ex.Message}"); }
        }
    }

    public async Task<IpcMessage?> SendRequestAsync(IpcMessage request, int timeoutMs = 5000, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        uint requestId = request.RequestId;
        IpcMessage finalRequest = request;
        if (requestId == 0)
        {
            int next = Interlocked.Increment(ref _nextRequestId);
            if (next <= 0)
            {
                Interlocked.Exchange(ref _nextRequestId, 1);
                next = 1;
            }
            requestId = (uint)next;
            finalRequest = new IpcMessage
            {
                OpCode = request.OpCode,
                RequestId = requestId,
                Payload = request.Payload
            };
        }

        TaskCompletionSource<IpcMessage> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequestsByRequestId[requestId] = tcs;

        IpcOpCode expectedResponseOpCode = GetExpectedResponseOpCode(finalRequest.OpCode);
        ConcurrentQueue<TaskCompletionSource<IpcMessage>> queue = _pendingRequestsByOpCode.GetOrAdd(
            expectedResponseOpCode,
            _ => new ConcurrentQueue<TaskCompletionSource<IpcMessage>>());
        queue.Enqueue(tcs);

        byte[] serialized = IpcMessage.Serialize(finalRequest);

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            NamedPipeClientStream? pipeClient = _pipeClient;
            if (!IsConnected || pipeClient == null)
                throw new InvalidOperationException($"IPC Client is not connected to pipe '\\\\{ServerName}\\pipe\\{PipeName}'.");

            try
            {
                await pipeClient.WriteAsync(serialized, cancellationToken).ConfigureAwait(false);
                if (pipeClient.IsConnected)
                {
                    await pipeClient.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (!tcs.Task.IsCompletedSuccessfully)
            {
                _pendingRequestsByRequestId.TryRemove(requestId, out _);
                tcs.TrySetException(ex);
                throw;
            }
            catch (Exception ex)
            {
                Log($"[GameIpcBridge] Post-completion flush exception ignored: {ex.Message}");
            }
        }
        finally
        {
            try { _sendLock.Release(); } catch (ObjectDisposedException ex) { Log($"[GameIpcBridge] Send lock already disposed: {ex.Message}"); }
        }

        if (tcs.Task.IsCompleted)
        {
            _pendingRequestsByRequestId.TryRemove(requestId, out _);
            DrainCompletedFromOpCodeQueue(expectedResponseOpCode);
            return await tcs.Task.ConfigureAwait(false);
        }

        using CancellationTokenSource timeoutCts = new(timeoutMs);
        CancellationToken internalToken = _cts?.Token ?? CancellationToken.None;
        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token, internalToken);

        using CancellationTokenRegistration reg = linkedCts.Token.Register(() =>
        {
            _pendingRequestsByRequestId.TryRemove(requestId, out _);
            if (timeoutCts.IsCancellationRequested)
            {
                if (string.Equals(PipeName, DefaultPipeName, StringComparison.OrdinalIgnoreCase))
                {
                    CleanupExistingConnection();
                }
                tcs.TrySetException(new TimeoutException($"IPC request '{finalRequest.OpCode}' (RequestId: {requestId}) timed out after {timeoutMs}ms."));
            }
            else
            {
                tcs.TrySetCanceled(linkedCts.Token);
            }
            DrainCompletedFromOpCodeQueue(expectedResponseOpCode);
        });

        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pendingRequestsByRequestId.TryRemove(requestId, out _);
            DrainCompletedFromOpCodeQueue(expectedResponseOpCode);
        }
    }

    public async Task DisconnectAsync()
    {
        _manuallyDisconnected = true;

        try
        {
            await _connectionLock.WaitAsync().ConfigureAwait(false);
            try
            {
                CleanupExistingConnection();

                if (_readLoopTask != null)
                {
                    try { await _readLoopTask.ConfigureAwait(false); } catch (Exception ex) { Log($"[GameIpcBridge] Read loop shutdown completed: {ex.Message}"); }
                    _readLoopTask = null;
                }
            }
            finally
            {
                try { _connectionLock.Release(); } catch (ObjectDisposedException ex) { Log($"[GameIpcBridge] Connection lock already disposed: {ex.Message}"); }
            }
        }
        catch (ObjectDisposedException)
        {
            CleanupExistingConnection();
        }

        OnDisconnected?.Invoke(null);
    }

    public async Task<string> DispatchUiEventAsync(string interfaceName, string widgetName, string eventType, int? eventIndex = null, int timeoutMs = 3000, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(widgetName);
        interfaceName = string.IsNullOrWhiteSpace(interfaceName) ? "main" : interfaceName;
        eventType = string.IsNullOrWhiteSpace(eventType) ? "click" : eventType;

        if (!IsConnected)
        {
            await ConnectAsync(Math.Min(timeoutMs, 2000), cancellationToken).ConfigureAwait(false);
        }

        if (IsConnected)
        {
            try
            {
                Dictionary<string, object?> payload = new Dictionary<string, object?>
                {
                    ["interfaceName"] = interfaceName,
                    ["widgetName"] = widgetName,
                    ["eventType"] = eventType,
                    ["eventIndex"] = eventIndex
                };
                IpcMessage request = IpcMessage.CreateJson(IpcOpCode.DispatchUiEvent, payload);
                IpcMessage? response = await SendRequestAsync(request, Math.Min(timeoutMs, 1000), cancellationToken).ConfigureAwait(false);
                if (response != null && response.OpCode == IpcOpCode.CommandResponse && !string.IsNullOrWhiteSpace(response.PayloadString))
                {
                    if (response.PayloadString.Contains("[SECURITY ERROR] 401 Unauthorized"))
                    {
                        Log($"[SECURITY ERROR] Remote host rejected unauthenticated UI event: {interfaceName}:{widgetName}");
                        return "[SECURITY ERROR] 401 Unauthorized: UI dispatch blocked by Named Pipe sandbox. Please configure 'KONGOR_IPC_AUTH_KEY' to authorize UI actuation.";
                    }

                    return response.PayloadString;
                }
            }
            catch (Exception ex)
            {
                Log($"[GameIpcBridge] Native UI event dispatch failed ({ex.Message}); falling back to console command.");
            }
        }

        string fallbackLua = NativeClientIpcBridge.BuildSafeUiDispatchLua(interfaceName, widgetName, eventType, eventIndex);
        return await ExecuteLuaAsync(fallbackLua, timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> DispatchBackgroundHumanClickAsync(string interfaceName, string widgetName, int clientX, int clientY, bool rightClick = false, int timeoutMs = 3000, CancellationToken cancellationToken = default)
    {
        interfaceName = string.IsNullOrWhiteSpace(interfaceName) ? "main" : interfaceName;
        widgetName ??= string.Empty;

        if (!IsConnected)
        {
            await ConnectAsync(Math.Min(timeoutMs, 2000), cancellationToken).ConfigureAwait(false);
        }

        if (IsConnected)
        {
            try
            {
                Dictionary<string, object?> payload = new()
                {
                    ["interfaceName"] = interfaceName,
                    ["widgetName"] = widgetName,
                    ["fidelity"] = "human",
                    ["clientX"] = clientX,
                    ["clientY"] = clientY,
                    ["rightClick"] = rightClick
                };
                IpcOpCode clickOpCode = string.Equals(PipeName, DefaultPipeName, StringComparison.OrdinalIgnoreCase)
                    ? IpcOpCode.DispatchUiEvent
                    : IpcOpCode.DispatchBackgroundHumanClick;
                IpcMessage request = IpcMessage.CreateJson(clickOpCode, payload);
                IpcMessage? response = await SendRequestAsync(request, Math.Min(timeoutMs, 1500), cancellationToken).ConfigureAwait(false);
                if (response != null && response.OpCode == IpcOpCode.CommandResponse && !string.IsNullOrWhiteSpace(response.PayloadString))
                {
                    return response.PayloadString;
                }
            }
            catch (Exception ex)
            {
                Log($"[GameIpcBridge] Background human click IPC failed ({ex.Message}); falling back to DOM UI event.");
            }
        }

        if (!string.IsNullOrWhiteSpace(widgetName))
        {
            return await DispatchUiEventAsync(interfaceName, widgetName, rightClick ? "rightclick" : "click", null, timeoutMs, cancellationToken).ConfigureAwait(false);
        }

        return "{\"success\":false,\"error\":\"IPC disconnected and no fallback widgetName provided\"}";
    }

    public Task<JsonElement> WaitForEventAsync(string eventName, int timeoutMs = 5000, CancellationToken cancellationToken = default)
        => _eventCoordinator.WaitForEventAsync(eventName, timeoutMs, cancellationToken);

    public Task<T?> ExecuteRpcAsync<T>(string method, object? parameters = null, int timeoutMs = 5000, CancellationToken cancellationToken = default)
        => _eventCoordinator.ExecuteRpcAsync<T>(method, parameters, timeoutMs, cancellationToken);

    public async Task<SubTickInputResult> InjectSubTickInputAsync(
        string action,
        long hardwareTimestampQpc = 0,
        uint inputSequence = 0,
        int timeoutMs = 3000,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        if (!IsConnected)
        {
            await ConnectAsync(Math.Min(timeoutMs, 2000), cancellationToken).ConfigureAwait(false);
        }

        if (hardwareTimestampQpc == 0)
        {
            hardwareTimestampQpc = Stopwatch.GetTimestamp();
        }

        SubTickInputPayloadDto payload = new()
        {
            Action = action,
            HardwareTimestampQpc = hardwareTimestampQpc,
            InputSequence = inputSequence
        };

        IpcMessage request = IpcMessage.CreateJson(IpcOpCode.InjectSubTickInput, payload);
        IpcMessage? response = await SendRequestAsync(request, timeoutMs, cancellationToken).ConfigureAwait(false);

        if (response == null || response.OpCode != IpcOpCode.SubTickInputResponse)
        {
            return new SubTickInputResult(false, inputSequence, 0f, 0, "Null or invalid sub-tick input response from engine.");
        }

        SubTickResponseDto? dto = response.ReadJson<SubTickResponseDto>();
        if (dto != null)
        {
            return new SubTickInputResult(dto.Success, dto.InputSequence, dto.MeasuredLatencyMs, dto.TimestampQpc, dto.Message);
        }

        return new SubTickInputResult(false, inputSequence, 0f, 0, "Failed to deserialize sub-tick input response.");
    }

    public async Task<bool> PushTelemetryUpdateAsync(
        uint drawCalls,
        uint activeEntities,
        uint particlesAlive,
        uint pingMicroseconds,
        uint serverTick,
        float inputLatencyMs = 0f,
        int timeoutMs = 2000,
        CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            await ConnectAsync(Math.Min(timeoutMs, 2000), cancellationToken).ConfigureAwait(false);
        }

        TelemetryUpdatePayloadDto payload = new()
        {
            DrawCalls = drawCalls,
            ActiveEntities = activeEntities,
            ParticlesAlive = particlesAlive,
            PingMicroseconds = pingMicroseconds,
            ServerTick = serverTick,
            InputLatencyMs = inputLatencyMs
        };

        IpcMessage request = IpcMessage.CreateJson(IpcOpCode.UpdateTelemetryMetrics, payload);
        IpcMessage? response = await SendRequestAsync(request, timeoutMs, cancellationToken).ConfigureAwait(false);

        if (response == null || response.OpCode != IpcOpCode.TelemetryMetricsResponse)
        {
            return false;
        }

        TelemetryMetricsResponseDto? dto = response.ReadJson<TelemetryMetricsResponseDto>();
        return dto?.Success ?? false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        AckSyncBarrier.Interrupt();
        try { _cts.Cancel(); } catch (ObjectDisposedException ex) { Log($"[GameIpcBridge] CTS already disposed: {ex.Message}"); }

        await DisconnectAsync().ConfigureAwait(false);
        _connectionLock.Dispose();
        _sendLock.Dispose();
        _cts.Dispose();
    }

    private static IpcOpCode GetExpectedResponseOpCode(IpcOpCode requestOpCode)
    {
        return requestOpCode switch
        {
            IpcOpCode.Ping => IpcOpCode.Pong,
            IpcOpCode.ExecuteCommand => IpcOpCode.CommandResponse,
            IpcOpCode.EvalLuaSync => IpcOpCode.EvalLuaResponse,
            IpcOpCode.DispatchUiEvent => IpcOpCode.CommandResponse,
            IpcOpCode.DispatchBackgroundHumanClick => IpcOpCode.CommandResponse,
            IpcOpCode.GetEngineState => IpcOpCode.EngineStateResponse,
            IpcOpCode.GetLuaMemoryMetrics => IpcOpCode.LuaMemoryResponse,
            IpcOpCode.Authenticate => IpcOpCode.AuthResponse,
            IpcOpCode.InjectSubTickInput => IpcOpCode.SubTickInputResponse,
            IpcOpCode.UpdateTelemetryMetrics => IpcOpCode.TelemetryMetricsResponse,
            IpcOpCode.DispatchVoiceUiTrigger => IpcOpCode.DispatchVoiceUiTriggerResponse,
            IpcOpCode.GetVoicePttState => IpcOpCode.GetVoicePttStateResponse,
            IpcOpCode.GetRawInputState => IpcOpCode.GetRawInputStateResponse,
            IpcOpCode.SetRawInputConfig => IpcOpCode.SetRawInputConfigResponse,
            IpcOpCode.PrewarmHero => IpcOpCode.PrewarmHeroResponse,
            IpcOpCode.PrewarmCompetitivePool => IpcOpCode.PrewarmCompetitivePoolResponse,
            IpcOpCode.GetPrewarmStats => IpcOpCode.GetPrewarmStatsResponse,
            IpcOpCode.GetJobPoolStats => IpcOpCode.GetJobPoolStatsResponse,
            IpcOpCode.SetJobPoolConfig => IpcOpCode.SetJobPoolConfigResponse,
            IpcOpCode.GetPredictionStats => IpcOpCode.GetPredictionStatsResponse,
            IpcOpCode.SetPredictionConfig => IpcOpCode.SetPredictionConfigResponse,
            IpcOpCode.HookToggleRequest => IpcOpCode.HookToggleResponse,
            IpcOpCode.PeekStructRequest => IpcOpCode.PeekStructResponse,
            IpcOpCode.DuplexLuaRpc => IpcOpCode.DuplexLuaRpcResponse,
            _ => throw new ArgumentException($"OpCode '{requestOpCode}' does not expect a direct response.", nameof(requestOpCode))
        };
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        byte[] readBuffer = new byte[64 * 1024];
        using MemoryStream accumulatedBuffer = new();
        Exception? disconnectException = null;

        try
        {
            while (_pipeClient != null && _pipeClient.IsConnected && !cancellationToken.IsCancellationRequested)
            {
                int bytesRead = await _pipeClient.ReadAsync(readBuffer, cancellationToken).ConfigureAwait(false);
                if (bytesRead <= 0) break; // End of stream

                accumulatedBuffer.Write(readBuffer, 0, bytesRead);
                if (accumulatedBuffer.Length > IpcMessage.MaxPayloadSize + IpcMessage.HeaderSize)
                    throw new InvalidDataException($"IPC bridge incoming buffer overflow: {accumulatedBuffer.Length} bytes exceeds limit.");

                byte[] currentBytes = accumulatedBuffer.ToArray();
                int offset = 0;

                while (offset < currentBytes.Length)
                {
                    ReadOnlySpan<byte> remainingSpan = currentBytes.AsSpan(offset);
                    if (IpcMessage.TryDeserializeAny(remainingSpan, out IpcMessage? message, out int bytesConsumed))
                    {
                        offset += bytesConsumed;
                        if (message != null)
                        {
                            DispatchIncomingMessage(message);
                        }
                    }
                    else
                    {
                        break; // Incomplete frame
                    }
                }

                if (offset > 0)
                {
                    byte[] unreadBytes = currentBytes.AsSpan(offset).ToArray();
                    accumulatedBuffer.SetLength(0);
                    if (unreadBytes.Length > 0)
                    {
                        accumulatedBuffer.Write(unreadBytes, 0, unreadBytes.Length);
                    }
                }
            }
        }
        catch (OperationCanceledException ex)
        {
            Log($"[GameIpcBridge] ReadLoopAsync cancelled cleanly: {ex.Message}");
        }
        catch (Exception ex)
        {
            disconnectException = ex;
            Log($"IPC ReadLoop terminated with exception: {ex.Message}");
        }
        finally
        {
            HandleDisconnection(disconnectException);
        }
    }

    private void DispatchIncomingMessage(IpcMessage message)
    {
        if (message.RequestId > 0)
        {
            AckSyncBarrier.Ack(message.RequestId);
        }

        if (message.OpCode == (IpcOpCode)(ushort)NEXUS.Contracts.Ipc.IpcOpCode.LogStreamEvent &&
            message.RequestId == 0 &&
            string.Equals(PipeName, DefaultPipeName, StringComparison.OrdinalIgnoreCase))
        {
            _eventCoordinator.HandleLogStream(message.PayloadString);
            OnLogStreamReceived?.Invoke(message.PayloadString);
            return;
        }

        if (message.RequestId > 0)
        {
            if (_pendingRequestsByRequestId.TryRemove(message.RequestId, out TaskCompletionSource<IpcMessage>? reqTcs))
            {
                reqTcs.TrySetResult(message);
            }
            else
            {
                Log($"[GameIpcBridge] Dropping orphaned late response for RequestId {message.RequestId} (OpCode: {message.OpCode}).");
            }

            DrainCompletedFromOpCodeQueue(message.OpCode);
            return;
        }

        if (_pendingRequestsByOpCode.TryGetValue(message.OpCode, out ConcurrentQueue<TaskCompletionSource<IpcMessage>>? queue))
        {
            while (queue.TryDequeue(out TaskCompletionSource<IpcMessage>? tcs))
            {
                if (tcs.TrySetResult(message))
                    return;
            }
        }

        OnMessageReceived?.Invoke(message);
    }

    private void DrainCompletedFromOpCodeQueue(IpcOpCode opCode)
    {
        if (_pendingRequestsByOpCode.TryGetValue(opCode, out ConcurrentQueue<TaskCompletionSource<IpcMessage>>? queue))
        {
            while (queue.TryPeek(out TaskCompletionSource<IpcMessage>? head) && head.Task.IsCompleted)
            {
                queue.TryDequeue(out _);
            }
        }
    }

    private void HandleDisconnection(Exception? ex)
    {
        if (_isDisposed || _manuallyDisconnected)
            return;

        AckSyncBarrier.Interrupt();
        CleanupExistingConnection();
        _eventCoordinator.Reset();

        Log($"Disconnected from Named Pipe \\\\{ServerName}\\pipe\\{PipeName}.");

        // Fail pending TCS by RequestId
        foreach (KeyValuePair<uint, TaskCompletionSource<IpcMessage>> kvp in _pendingRequestsByRequestId)
        {
            if (_pendingRequestsByRequestId.TryRemove(kvp.Key, out TaskCompletionSource<IpcMessage>? tcs))
            {
                tcs.TrySetException(new IOException("IPC pipe connection terminated while awaiting response.", ex));
            }
        }

        // Fail pending TCS by OpCode
        foreach (KeyValuePair<IpcOpCode, ConcurrentQueue<TaskCompletionSource<IpcMessage>>> kvp in _pendingRequestsByOpCode)
        {
            while (kvp.Value.TryDequeue(out TaskCompletionSource<IpcMessage>? tcs))
            {
                tcs.TrySetException(new IOException("IPC pipe connection terminated while awaiting response.", ex));
            }
        }

        OnDisconnected?.Invoke(ex);
    }

    private void CleanupExistingConnection()
    {
        IsAuthenticated = false;
        if (_pipeClient != null)
        {
            try { _pipeClient.Dispose(); } catch (Exception ex) { Log($"[GameIpcBridge] Pipe client dispose warning: {ex.Message}"); }
            _pipeClient = null;
        }
    }
}
