// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using NEXUS.Contracts.CQRS.Telemetry;

namespace TRANSMUTANSTEIN.ChatServer.Services;

public interface ITelemetryPublisherService
{
    Task ResetBaselineOnStartupAsync();

    Task OnClientConnectedAsync(string accountName);

    Task OnClientDisconnectedAsync(string accountName);

    Task OnMatchServerConnectedAsync(int serverId, string location);

    Task OnMatchServerDisconnectedAsync(int serverId, string location);

    Task RecordQueueStateAsync(int activeQueuesCount);

    Task PublishPopulationBreakdownAsync(PopulationBreakdownResponseDto snapshot, bool forceImmediate = false);

    Task RecordMatchFoundWaitTimeAsync(string queueType, int playerCount, double waitSeconds);

    Task OnMatchStartedAsync(int matchId);

    Task OnMatchEndedAsync(int matchId);

    Task RecordChatCommandAsync();
}
