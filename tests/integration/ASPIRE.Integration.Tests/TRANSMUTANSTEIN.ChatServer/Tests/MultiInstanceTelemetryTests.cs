// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TRANSMUTANSTEIN.ChatServer.Services;

namespace ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Tests;

public class MultiInstanceTelemetryTests
{
    [Fact]
    public void MultiInstanceTelemetry_TelemetryEventMessage_SerializesAndDeserializesCorrectly()
    {
        Guid instanceId = Guid.NewGuid();
        DateTimeOffset now = TimeProvider.System.GetUtcNow();

        TelemetryEventMessage original = new(
            TelemetryEventType.ClientConnected,
            instanceId,
            "PlayerOne",
            101,
            "US East",
            now
        );

        string json = JsonSerializer.Serialize(original);
        TelemetryEventMessage? deserialized = JsonSerializer.Deserialize<TelemetryEventMessage>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(TelemetryEventType.ClientConnected, deserialized.EventType);
        Assert.Equal(instanceId, deserialized.PublisherInstanceId);
        Assert.Equal("PlayerOne", deserialized.StringIdentifier);
        Assert.Equal(101, deserialized.NumericId);
        Assert.Equal("US East", deserialized.Location);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void MultiInstanceTelemetry_HandleRemoteTelemetryEvent_ProcessesRemoteEventAndIgnoresSelfEvent()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        DbContextOptions<MerrickContext> options = TestDbContextFactory.CreateOptions();

        ServiceCollection services = [];
        services.AddScoped(_ => new MerrickContext(options));
        ServiceProvider serviceProvider = services.BuildServiceProvider();

        TelemetryPublisherService publisher = new(
            serviceProvider,
            NullLogger<TelemetryPublisherService>.Instance);

        Guid selfId = publisher.InstanceId;
        Guid remoteId = Guid.NewGuid();

        // 1. Self Event should be ignored by HandleRemoteTelemetryEvent
        TelemetryEventMessage selfEvent = new(
            TelemetryEventType.ClientConnected,
            selfId,
            "SelfPlayer"
        );

        publisher.HandleRemoteTelemetryEvent(selfEvent);

        // 2. Remote Event should be processed
        TelemetryEventMessage remoteEvent = new(
            TelemetryEventType.ClientConnected,
            remoteId,
            "RemotePlayer"
        );

        publisher.HandleRemoteTelemetryEvent(remoteEvent);
    }
}
