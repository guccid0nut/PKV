// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Connection;

public class ClientHandshakeRequestData(ChatBuffer buffer)
{
    public byte[] CommandBytes { get; init; } = buffer.ReadCommandBytes();

    public int AccountID { get; } = buffer.ReadInt32();

    public string SessionCookie { get; } = buffer.ReadString();

    public string RemoteIP { get; } = buffer.ReadString();

    public string SessionAuthenticationHash { get; } = buffer.ReadString();

    public int ChatProtocolVersion { get; } = buffer.ReadInt32();

    public byte OperatingSystemIdentifier { get; } = buffer.ReadInt8();

    public byte OperatingSystemVersionMajor { get; } = buffer.ReadInt8();

    public byte OperatingSystemVersionMinor { get; } = buffer.ReadInt8();

    public byte OperatingSystemVersionPatch { get; } = buffer.ReadInt8();

    public string OperatingSystemBuildCode { get; } = buffer.ReadString();

    public string OperatingSystemArchitecture { get; } = buffer.ReadString();

    public byte ClientVersionMajor { get; } = buffer.ReadInt8();

    public byte ClientVersionMinor { get; } = buffer.ReadInt8();

    public byte ClientVersionPatch { get; } = buffer.ReadInt8();

    public byte ClientVersionRevision { get; } = buffer.ReadInt8();

    public ChatProtocol.ChatClientStatus LastKnownClientState { get; } =
        (ChatProtocol.ChatClientStatus)buffer.ReadInt8();

    public ChatProtocol.ChatModeType ClientChatModeState { get; } = (ChatProtocol.ChatModeType)buffer.ReadInt8();

    public string ClientRegion { get; } = buffer.ReadString();

    public string ClientLanguage { get; } = buffer.ReadString();

    public ClientChatSessionMetadata ToMetadata()
    {
        return new ClientChatSessionMetadata
        {
            RemoteIP = RemoteIP,
            SessionCookie = SessionCookie,
            SessionAuthenticationHash = SessionAuthenticationHash,
            ChatProtocolVersion = ChatProtocolVersion,
            OperatingSystemIdentifier = OperatingSystemIdentifier,
            OperatingSystemVersionMajor = OperatingSystemVersionMajor,
            OperatingSystemVersionMinor = OperatingSystemVersionMinor,
            OperatingSystemVersionPatch = OperatingSystemVersionPatch,
            OperatingSystemBuildCode = OperatingSystemBuildCode,
            OperatingSystemArchitecture = OperatingSystemArchitecture,
            ClientVersionMajor = ClientVersionMajor,
            ClientVersionMinor = ClientVersionMinor,
            ClientVersionPatch = ClientVersionPatch,
            ClientVersionRevision = ClientVersionRevision,
            LastKnownClientState = LastKnownClientState,
            ClientChatModeState = ClientChatModeState,
            ClientRegion = ClientRegion,
            ClientLanguage = ClientLanguage
        };
    }
}
