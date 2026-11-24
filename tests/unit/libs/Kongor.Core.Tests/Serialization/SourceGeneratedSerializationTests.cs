// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json;
using FluentAssertions;
using Kongor.Core.Serialization.JSON;
using NEXUS.Contracts.Cdn;
using NEXUS.Contracts.Serialization;
using NEXUS.Contracts.Telemetry;
using Xunit;

namespace Kongor.Core.Tests.Serialization;

public class SourceGeneratedSerializationTests
{
    [Fact]
    public void NexusJsonContext_SerializesAndDeserializes_DiscordPresenceUpdateDto()
    {
        DiscordPresenceUpdateDto dto = new()
        {
            AccountId = 12345,
            State = "In Match",
            Details = "Playing Caldavar",
            LargeImageKey = "hero_pyromancer",
            LargeImageText = "Pyromancer",
            SmallImageKey = "icon_rank_gold",
            SmallImageText = "Gold I"
        };

        string json = JsonSerializer.Serialize(dto, NexusJsonContext.Default.DiscordPresenceUpdateDto);
        json.Should().NotBeNullOrWhiteSpace();
        json.Should().Contain("\"AccountId\":12345");
        json.Should().Contain("\"State\":\"In Match\"");

        DiscordPresenceUpdateDto? deserialized =
            JsonSerializer.Deserialize(json, NexusJsonContext.Default.DiscordPresenceUpdateDto);

        deserialized.Should().NotBeNull();
        deserialized!.AccountId.Should().Be(12345);
        deserialized.State.Should().Be("In Match");
        deserialized.Details.Should().Be("Playing Caldavar");
        deserialized.LargeImageKey.Should().Be("hero_pyromancer");
        deserialized.LargeImageText.Should().Be("Pyromancer");
        deserialized.SmallImageKey.Should().Be("icon_rank_gold");
        deserialized.SmallImageText.Should().Be("Gold I");
    }

    [Fact]
    public void NexusJsonContext_SerializesAndDeserializes_TelemetryEventMessage()
    {
        Guid pubId = Guid.NewGuid();
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        TelemetryEventMessage msg = new(
            TelemetryEventType.ClientConnected,
            pubId,
            "session-abc",
            1001,
            "us-east",
            now);

        string json = JsonSerializer.Serialize(msg, NexusJsonContext.Default.TelemetryEventMessage);
        json.Should().NotBeNullOrWhiteSpace();
        json.Should().Contain("\"EventType\":");
        json.Should().Contain("\"Location\":\"us-east\"");

        TelemetryEventMessage? deserialized =
            JsonSerializer.Deserialize(json, NexusJsonContext.Default.TelemetryEventMessage);

        deserialized.Should().NotBeNull();
        deserialized!.EventType.Should().Be(TelemetryEventType.ClientConnected);
        deserialized.PublisherInstanceId.Should().Be(pubId);
        deserialized.StringIdentifier.Should().Be("session-abc");
        deserialized.NumericId.Should().Be(1001);
        deserialized.Location.Should().Be("us-east");
    }

    [Fact]
    public void NexusJsonContext_SerializesAndDeserializes_ManifestDocument()
    {
        ManifestDocument doc = new()
        {
            Version = "5.0.0",
            Channel = "release",
            Files = new Dictionary<string, ManifestEntry>
            {
                ["game/resources0.s2z"] = new()
                {
                    Size = 1048576,
                    Hash = "abcdef0123456789"
                }
            }
        };

        string json = JsonSerializer.Serialize(doc, NexusJsonContext.Default.ManifestDocument);
        json.Should().NotBeNullOrWhiteSpace();
        json.Should().Contain("\"Version\":\"5.0.0\"");

        ManifestDocument? deserialized =
            JsonSerializer.Deserialize(json, NexusJsonContext.Default.ManifestDocument);

        deserialized.Should().NotBeNull();
        deserialized!.Version.Should().Be("5.0.0");
        deserialized.Channel.Should().Be("release");
        deserialized.Files.Should().ContainKey("game/resources0.s2z");
        deserialized.Files["game/resources0.s2z"].Size.Should().Be(1048576);
    }

    [Fact]
    public void KongorCoreJsonContext_SerializesAndDeserializes_PresenceList()
    {
        List<DiscordPresenceUpdateDto> list =
        [
            new() { AccountId = 1, State = "Online", Details = "Idle" },
            new() { AccountId = 2, State = "In Game", Details = "Midwars" }
        ];

        byte[] utf8Bytes = JsonSerializer.SerializeToUtf8Bytes(list, KongorCoreJsonContext.Default.ListDiscordPresenceUpdateDto);
        utf8Bytes.Should().NotBeEmpty();

        List<DiscordPresenceUpdateDto>? deserialized =
            JsonSerializer.Deserialize(utf8Bytes, KongorCoreJsonContext.Default.ListDiscordPresenceUpdateDto);

        deserialized.Should().NotBeNull();
        deserialized!.Should().HaveCount(2);
        deserialized[0].AccountId.Should().Be(1);
        deserialized[1].AccountId.Should().Be(2);
    }

    [Fact]
    public void KongorCoreJsonContext_SerializesAndDeserializes_PrimitiveLists()
    {
        List<string> strings = ["us-east", "eu-central", "sea"];
        string strJson = JsonSerializer.Serialize(strings, KongorCoreJsonContext.Default.ListString);
        List<string>? deserializedStrings = JsonSerializer.Deserialize(strJson, KongorCoreJsonContext.Default.ListString);

        deserializedStrings.Should().NotBeNull();
        deserializedStrings.Should().BeEquivalentTo(strings);

        List<int> ints = [100, 200, 300];
        string intJson = JsonSerializer.Serialize(ints, KongorCoreJsonContext.Default.ListInt32);
        List<int>? deserializedInts = JsonSerializer.Deserialize(intJson, KongorCoreJsonContext.Default.ListInt32);

        deserializedInts.Should().NotBeNull();
        deserializedInts.Should().BeEquivalentTo(ints);
    }
}
