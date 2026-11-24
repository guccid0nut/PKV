// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json.Serialization;
using ASPIRE.Common.Configuration.Matchmaking;
using ASPIRE.Common.Models.ServerManagement;
using KONGOR.MasterServer.Configuration;
using KONGOR.MasterServer.Models.RequestResponse.SRP;
using KONGOR.MasterServer.Services;
using KONGOR.MasterServer.Services.Replay;
using NEXUS.Contracts.Telemetry;

using KONGOR.MasterServer.Models.RequestResponse.ServerManagement;
using KONGOR.MasterServer.Models.RequestResponse.Store;
using NEXUS.Contracts.CQRS.Telemetry;
using NEXUS.Contracts.Events;
using NEXUS.Contracts.Replay;

namespace KONGOR.MasterServer.Serialization;

/// <summary>
///     Source-generated JSON serialization context for KONGOR.MasterServer,
///     providing reflection-free, Native AOT compatible serialization across
///     Redis cache keys, SignalR presence hubs, and configurations.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(SRPAuthenticationSessionDataStageOne))]
[JsonSerializable(typeof(DiscordPresenceUpdateDto))]
[JsonSerializable(typeof(List<DiscordPresenceUpdateDto>))]
[JsonSerializable(typeof(MatchServerManager))]
[JsonSerializable(typeof(List<MatchServerManager>))]
[JsonSerializable(typeof(MatchServer))]
[JsonSerializable(typeof(List<MatchServer>))]
[JsonSerializable(typeof(MatchStartData))]
[JsonSerializable(typeof(List<MatchStartData>))]
[JsonSerializable(typeof(PendingReplayMetadata))]
[JsonSerializable(typeof(HeroDefinitionService.UpgradeDefinition))]
[JsonSerializable(typeof(List<HeroDefinitionService.UpgradeDefinition>))]
[JsonSerializable(typeof(HeroDefinitionService.StoreItemDefinition))]
[JsonSerializable(typeof(List<HeroDefinitionService.StoreItemDefinition>))]
[JsonSerializable(typeof(EconomyConfiguration))]
[JsonSerializable(typeof(MasteryRewardsConfiguration))]
[JsonSerializable(typeof(List<MasteryCoupon>))]
[JsonSerializable(typeof(DailySpecialsConfiguration))]
[JsonSerializable(typeof(FeaturedItemsConfiguration))]
[JsonSerializable(typeof(MatchmakingConfiguration))]
[JsonSerializable(typeof(StoreItem))]
[JsonSerializable(typeof(List<StoreItem>))]
[JsonSerializable(typeof(PlinkoConfiguration))]
[JsonSerializable(typeof(List<PlinkoTierProducts>))]
[JsonSerializable(typeof(TicketExchangeConfiguration))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(MatchConcludedPayload))]
[JsonSerializable(typeof(PlayerStatusHydrationEvent))]
[JsonSerializable(typeof(LiveServerTickPayloadDto))]
[JsonSerializable(typeof(BundleContentResponse))]
[JsonSerializable(typeof(Dictionary<string, Dictionary<int, GetProductsResponseEntry>>))]
[JsonSerializable(typeof(ReplayRenderJobRequestDto))]
[JsonSerializable(typeof(ReplayRenderJobResponseDto))]
[JsonSerializable(typeof(PopulationBreakdownResponseDto))]
[JsonSerializable(typeof(RegionCountDto))]
[JsonSerializable(typeof(TmmQueueBreakdownDto))]
[JsonSerializable(typeof(List<RegionCountDto>))]
[JsonSerializable(typeof(List<TmmQueueBreakdownDto>))]
public sealed partial class MasterServerJsonContext : JsonSerializerContext
{
}
