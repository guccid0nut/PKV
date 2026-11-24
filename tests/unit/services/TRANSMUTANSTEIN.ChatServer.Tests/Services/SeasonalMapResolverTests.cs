// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Network;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using TRANSMUTANSTEIN.ChatServer.Internals;
using TRANSMUTANSTEIN.ChatServer.Services;
using TRANSMUTANSTEIN.ChatServer.Utilities;
using Xunit;

namespace TRANSMUTANSTEIN.ChatServer.Tests.Services;

public sealed class SeasonalMapResolverTests
{
    public SeasonalMapResolverTests()
    {
        Log.Initialise(NullLogger.Instance);
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset initialUtc) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = initialUtc;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private static IConfiguration BuildConfig(
        string serverMapsDirectory,
        bool enabled = true,
        string forceSeason = "auto",
        bool includeRankedQueues = true)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Matchmaking:SeasonalMaps:Enabled"] = enabled ? "true" : "false",
                ["Matchmaking:SeasonalMaps:ForceSeason"] = forceSeason,
                ["Matchmaking:SeasonalMaps:IncludeRankedQueues"] = includeRankedQueues ? "true" : "false",
                ["Matchmaking:SeasonalMaps:ServerMapsDirectory"] = serverMapsDirectory
            })
            .Build();
    }

    [Theory]
    [InlineData("2026-09-30T23:59:59Z", "midwars", "midwars")]
    [InlineData("2026-10-01T00:00:00Z", "midwars", "midwars_halloween")]
    [InlineData("2026-10-31T23:59:59Z", "caldavar", "caldavar_halloween")]
    [InlineData("2026-11-01T11:59:59Z", "midwars", "midwars_halloween")]
    [InlineData("2026-11-01T12:00:00Z", "midwars", "midwars")]
    [InlineData("2026-10-15T18:00:00Z", "riftwars", "riftwars")]
    public void ResolveMatchmakingMap_RespectsAuthoritativeUtcHalloweenWindow(
        string isoUtc,
        string requestedMap,
        string expectedMap)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"kongor_seasonal_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllBytes(Path.Combine(tempDir, "midwars_halloween.s2z"), [0x50, 0x4B, 0x05, 0x06]);
            File.WriteAllBytes(Path.Combine(tempDir, "caldavar_halloween.s2z"), [0x50, 0x4B, 0x05, 0x06]);

            IConfiguration config = BuildConfig(tempDir, enabled: true, forceSeason: "auto", includeRankedQueues: true);
            AdjustableTimeProvider clock = new(DateTimeOffset.Parse(isoUtc));

            string resolved = SeasonalMapResolver.ResolveMatchmakingMap(
                requestedMap,
                ChatProtocol.TMMType.TMM_TYPE_PVP,
                ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL,
                config,
                clock);

            resolved.Should().Be(expectedMap);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveMatchmakingMap_IncludeRankedQueuesFalse_ExemptsRankedAndCoop_WhileRotatingCasualAndMidwars()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"kongor_seasonal_ranked_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllBytes(Path.Combine(tempDir, "midwars_halloween.s2z"), [0x50, 0x4B, 0x05, 0x06]);
            File.WriteAllBytes(Path.Combine(tempDir, "caldavar_halloween.s2z"), [0x50, 0x4B, 0x05, 0x06]);

            IConfiguration config = BuildConfig(tempDir, enabled: true, forceSeason: "halloween", includeRankedQueues: false);
            AdjustableTimeProvider clock = new(DateTimeOffset.Parse("2026-07-15T12:00:00Z"));

            SeasonalMapResolver.ResolveMatchmakingMap(
                    "caldavar",
                    ChatProtocol.TMMType.TMM_TYPE_PVP,
                    ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL,
                    config,
                    clock)
                .Should().Be("caldavar");

            SeasonalMapResolver.ResolveMatchmakingMap(
                    "caldavar",
                    ChatProtocol.TMMType.TMM_TYPE_PVP,
                    ChatProtocol.TMMGameType.TMM_GAME_TYPE_CASUAL,
                    config,
                    clock)
                .Should().Be("caldavar_halloween");

            SeasonalMapResolver.ResolveMatchmakingMap(
                    "midwars",
                    ChatProtocol.TMMType.TMM_TYPE_PVP,
                    ChatProtocol.TMMGameType.TMM_GAME_TYPE_MIDWARS,
                    config,
                    clock)
                .Should().Be("midwars_halloween");

            // Co-Op (TMM_TYPE_COOP) exposes both standard and _halloween maps explicitly in its 19-map catalog,
            // so explicit map choices must never be silently swapped.
            IConfiguration fullHalloweenConfig = BuildConfig(tempDir, enabled: true, forceSeason: "halloween", includeRankedQueues: true);
            SeasonalMapResolver.ResolveMatchmakingMap(
                    "caldavar",
                    ChatProtocol.TMMType.TMM_TYPE_COOP,
                    ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL,
                    fullHalloweenConfig,
                    clock)
                .Should().Be("caldavar");

            SeasonalMapResolver.ResolveMatchmakingMap(
                    "caldavar_halloween",
                    ChatProtocol.TMMType.TMM_TYPE_COOP,
                    ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL,
                    fullHalloweenConfig,
                    clock)
                .Should().Be("caldavar_halloween");

            SeasonalMapResolver.ResolveMatchmakingMap(
                    "midwars",
                    ChatProtocol.TMMType.TMM_TYPE_COOP,
                    ChatProtocol.TMMGameType.TMM_GAME_TYPE_MIDWARS,
                    fullHalloweenConfig,
                    clock)
                .Should().Be("midwars");

            SeasonalMapResolver.ResolveMatchmakingMap(
                    "midwars_halloween",
                    ChatProtocol.TMMType.TMM_TYPE_COOP,
                    ChatProtocol.TMMGameType.TMM_GAME_TYPE_MIDWARS,
                    fullHalloweenConfig,
                    clock)
                .Should().Be("midwars_halloween");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveMatchmakingMap_FallsBackSafelyWhenSeasonalArchiveMissingOnDisk()
    {
        string emptyDir = Path.Combine(Path.GetTempPath(), $"kongor_seasonal_missing_{Guid.NewGuid():N}");
        Directory.CreateDirectory(emptyDir);
        try
        {
            IConfiguration config = BuildConfig(emptyDir, enabled: true, forceSeason: "halloween", includeRankedQueues: true);
            AdjustableTimeProvider clock = new(DateTimeOffset.Parse("2026-10-31T20:00:00Z"));

            string resolved = SeasonalMapResolver.ResolveMatchmakingMap(
                "midwars",
                ChatProtocol.TMMType.TMM_TYPE_PVP,
                ChatProtocol.TMMGameType.TMM_GAME_TYPE_MIDWARS,
                config,
                clock);

            resolved.Should().Be("midwars");
        }
        finally
        {
            Directory.Delete(emptyDir, recursive: true);
        }
    }
}
