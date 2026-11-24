// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Runtime.InteropServices;
using System.Text.Json;
using FluentAssertions;
using NEXUS.Contracts.Admin;
using NEXUS.Contracts.Cdn;
using NEXUS.Contracts.ControlPlane;
using NEXUS.Contracts.Domain;
using NEXUS.Contracts.Graphics;
using NEXUS.Contracts.Ipc;
using NEXUS.Contracts.Replay;
using NEXUS.Contracts.Security;
using NEXUS.Contracts.Telemetry;
using Xunit;

namespace NEXUS.Contracts.Tests;

public sealed class NexusContractsTests
{
    [Fact]
    public void NexusContractsAssembly_WhenLoaded_ShouldLoadSuccessfully()
    {
        typeof(NexusContractsTests).Assembly.Should().NotBeNull();
    }

    [Fact]
    public void Graphics_FrameHeader_MustBeExactly64BytesSequential()
    {
        int size = Marshal.SizeOf<FrameHeader>();
        size.Should().Be(64);

        FrameHeader header = new()
        {
            Magic = FrameHeader.ExpectedMagicHonc,
            Version = FrameHeader.ExpectedVersion,
            Width = 1920,
            Height = 1080,
            Format = FrameHeader.FormatB8G8R8A8Unorm,
            RowPitch = 1920 * 4,
            DataSizeBytes = 1920 * 1080 * 4,
            Flags = FrameHeader.FlagKeyframe | FrameHeader.FlagEndOfStream,
            TimestampNs = 1234567890UL,
            BufferSlotIndex = 1,
            FrameIndex = 42
        };

        header.IsValid().Should().BeTrue();
        header.IsEndOfStream().Should().BeTrue();
        header.Stride.Should().Be(1920 * 4);
        header.TimestampNanoseconds.Should().Be(1234567890UL);
        header.ToString().Should().Contain("1920x1080");
    }

    [Fact]
    public void Ipc_WillowmakerIpcMessage_ShouldSerializeAndDeserializeCorrectly()
    {
        EngineStateDto engineState = new()
        {
            CurrentFps = 999.5,
            MemoryWorkingSetBytes = 1024 * 1024 * 512,
            ActiveMatchId = 12345,
            CurrentMap = "caldavar",
            PlayerName = "MoonMeander",
            ConnectedPlayers = 10,
            UptimeSeconds = 3600
        };

        WillowmakerIpcMessage message = WillowmakerIpcMessage.CreateJson(IpcOpCode.EngineStateResponse, engineState);
        byte[] serialized = WillowmakerIpcMessage.Serialize(message);

        serialized.Length.Should().BeGreaterThan(WillowmakerIpcMessage.HeaderSize);

        bool success = WillowmakerIpcMessage.TryDeserialize(serialized, out WillowmakerIpcMessage? deserialized,
            out int bytesConsumed);
        success.Should().BeTrue();
        deserialized.Should().NotBeNull();
        bytesConsumed.Should().Be(serialized.Length);
        deserialized!.OpCode.Should().Be(IpcOpCode.EngineStateResponse);

        EngineStateDto? readState = deserialized.ReadJson<EngineStateDto>();
        readState.Should().NotBeNull();
        readState!.CurrentFps.Should().Be(999.5);
        readState.PlayerName.Should().Be("MoonMeander");
        readState.ActiveMatchId.Should().Be(12345);
    }

    [Fact]
    public void ControlPlane_CompelStatusResponseDto_ShouldSerializeWithCamelCaseAttributes()
    {
        CompelStatusResponseDto status = new()
        {
            Application = "COMPEL",
            Version = "5.0.0",
            ServerNamePrefix = "Community Server",
            Location = "USE",
            Instances = 4,
            UseProxy = true,
            PortRangeOffset = 20,
            Ports = new CompelPortAllocationDto
            {
                GameStart = 11235,
                GameEnd = 11238,
                VoiceStart = 11435,
                VoiceEnd = 11438,
                PublicGameStart = 21235,
                PublicGameEnd = 21238,
                PublicVoiceStart = 21435,
                PublicVoiceEnd = 21438,
                PingPort = 11234
            },
            DistributionVersion = "2026.09.04",
            SynchronisationState = "UpToDate",
            ManagerRunning = true,
            ProxyRunning = true,
            UptimeSeconds = 1234.5
        };

        string json = JsonSerializer.Serialize(status);
        json.Should().Contain("\"application\":\"COMPEL\"");
        json.Should().Contain("\"serverNamePrefix\":\"Community Server\"");
        json.Should().Contain("\"ports\":{");

        CompelStatusResponseDto? deserialized = JsonSerializer.Deserialize<CompelStatusResponseDto>(json);
        deserialized.Should().NotBeNull();
        deserialized!.Application.Should().Be("COMPEL");
        deserialized.Ports.Should().NotBeNull();
        deserialized.Ports!.PublicGameStart.Should().Be(21235);
    }

    [Fact]
    public void ControlPlane_CompelPingResponseDto_TimestampAliasShouldWork()
    {
        CompelPingResponseDto ping = new()
        {
            Application = "COMPEL",
            Version = "5.0.0",
            ServerTimeUnixMilliseconds = 1756598400000
        };

        ping.Timestamp.Should().Be(1756598400000);
        ping.Timestamp = 1756598500000;
        ping.ServerTimeUnixMilliseconds.Should().Be(1756598500000);
    }

    [Fact]
    public void Cdn_Manifest_ShouldParseAndReportCorrectExclusions()
    {
        string manifestJson = """
                              {
                                  "version": "5.0.0.1",
                                  "hashAlgorithm": "xxhash64",
                                  "excludeFromSource": ["manifest.json", "manifest.sig"],
                                  "excludeFromTarget": ["userdata/**", "WILLOWMAKER.log"],
                                  "files": {
                                      "game/resources0.s2z": { "size": 1048576, "hash": "0123456789abcdef" }
                                  }
                              }
                              """;

        Manifest? manifest = ManifestParserService.Parse(manifestJson);
        manifest.Should().NotBeNull();
        manifest!.Version.Should().Be("5.0.0.1");
        manifest.HashAlgorithm.Should().Be("xxhash64");
        manifest.ExcludeFromSource.Should().Contain("manifest.json");
        manifest.ExcludeFromTarget.Should().Contain("userdata/**");
        manifest.Files.Should().ContainKey("game/resources0.s2z");
        manifest.Files["game/resources0.s2z"].Size.Should().Be(1048576);
    }

    [Fact]
    public void Cdn_SynchronisationSummary_ToString_ShouldFormatCorrectly()
    {
        SynchronisationSummary summary = new(10, 2, 88, 0, 1048576, []);
        summary.ToString().Should()
            .Be("10 Downloaded, 2 Deleted, 88 Up To Date, 0 Failed, 1,048,576 Bytes Transferred");
    }

    [Fact]
    public void Telemetry_CoordinateNormalizationHelper_ShouldNormalizeCorrectly()
    {
        (float normX, float normY) = CoordinateNormalizationHelper.NormalizeCoordinates(0f, 0f);
        normX.Should().Be(0.5f);
        normY.Should().Be(0.5f);

        (float minX, float minY) = CoordinateNormalizationHelper.NormalizeCoordinates(-8000f, 8000f);
        minX.Should().Be(0.0f);
        minY.Should().Be(0.0f);

        (float maxX, float maxY) = CoordinateNormalizationHelper.NormalizeCoordinates(8000f, -8000f);
        maxX.Should().Be(1.0f);
        maxY.Should().Be(1.0f);
    }

    [Fact]
    public void Telemetry_LiveServerTickPayloadDto_Clone_ShouldDeepCopy()
    {
        LiveServerTickPayloadDto payload = new()
        {
            MatchID = 1001,
            Map = "caldavar",
            ElapsedSeconds = 120,
            Players =
            [
                new LivePlayerTelemetryDto
                {
                    Slot = 0,
                    AccountID = 42,
                    AccountName = "Player1",
                    HeroIdentifier = "Hero_Devourer"
                }
            ],
            ActiveWards =
            [
                new LiveWardTelemetryDto
                {
                    PositionX = 0.4f,
                    PositionY = 0.6f
                }
            ],
            RecentEvents =
            [
                new LiveCombatEventTelemetryDto
                {
                    EventType = "KILL",
                    AttackerName = "Player1"
                }
            ],
            Structures = new Dictionary<string, bool> { ["t1_legion_mid"] = true }
        };

        LiveServerTickPayloadDto clone = payload.Clone();
        clone.MatchID.Should().Be(payload.MatchID);
        clone.Players.Should().HaveCount(1);
        clone.Players[0].AccountName.Should().Be("Player1");
        clone.ActiveWards.Should().HaveCount(1);
        clone.RecentEvents.Should().HaveCount(1);
        clone.Structures["t1_legion_mid"].Should().BeTrue();

        // Mutate clone and verify isolation
        clone.Players[0].AccountName = "Mutated";
        payload.Players[0].AccountName.Should().Be("Player1");
    }

    [Fact]
    public void Replay_ReplayMetadata_ResolvePlayer_ShouldSupportMultipleQueryModes()
    {
        ReplayMetadata metadata = new()
        {
            MatchId = "9999",
            GameName = "Champions League Grand Final",
            Winner = "1",
            Players =
            [
                new ReplayPlayerMetadata
                {
                    ClientNum = 0,
                    Team = 1,
                    TeamIndex = 0,
                    Name = "MoonMeander",
                    HeroName = "Hero_Devourer",
                    Kills = 10,
                    Deaths = 2,
                    Assists = 8
                },
                new ReplayPlayerMetadata
                {
                    ClientNum = 5,
                    Team = 2,
                    TeamIndex = 0,
                    Name = "Testie",
                    HeroName = "Hero_FlintBeastwood",
                    Kills = 8,
                    Deaths = 4,
                    Assists = 6
                }
            ]
        };

        metadata.WinnerTeamName.Should().Be("Legion");

        // Direct Slot query
        PlayerResolutionResult resSlot = metadata.ResolvePlayer("0");
        resSlot.Confidence.Should().Be(PlayerMatchConfidence.Exact);
        resSlot.Player!.Name.Should().Be("MoonMeander");

        // Team alias query
        PlayerResolutionResult resTeam = metadata.ResolvePlayer("L1");
        resTeam.Confidence.Should().Be(PlayerMatchConfidence.Exact);
        resTeam.Player!.Name.Should().Be("MoonMeander");

        // Exact Player Name query
        PlayerResolutionResult resName = metadata.ResolvePlayer("testie");
        resName.Confidence.Should().Be(PlayerMatchConfidence.Exact);
        resName.Player!.Name.Should().Be("Testie");

        // Exact Hero Name query
        PlayerResolutionResult resHero = metadata.ResolvePlayer("Devourer");
        resHero.Confidence.Should().Be(PlayerMatchConfidence.Exact);
        resHero.Player!.HeroName.Should().Be("Hero_Devourer");

        // Fuzzy Name query
        PlayerResolutionResult resFuzzy = metadata.ResolvePlayer("moon");
        resFuzzy.Confidence.Should().Be(PlayerMatchConfidence.Fuzzy);
        resFuzzy.Player!.Name.Should().Be("MoonMeander");

        // Roster summary check
        string roster = metadata.GetRosterSummary();
        roster.Should().Contain("LEGION (Team 1)");
        roster.Should().Contain("MoonMeander");
        roster.Should().Contain("Testie");
    }

    [Fact]
    public void Security_Constants_ShouldMatchExpectedValues()
    {
        SecurityConstants.InternalServiceKeyHeader.Should().Be("X-Internal-Service-Key");
        SecurityConstants.DefaultInternalServiceKey.Should().Be("NEXUS-Internal-Service-Key-Dev-2026");
    }

    [Fact]
    public void ServerManagement_CacheModels_MustNotBeDuplicatedInContracts()
    {
        // The Redis-cached server management models (MatchServer, MatchServerManager, MatchStartData, ServerStatus,
        // MatchOptions) live ONLY in protocol-core (ASPIRE.Common.Models.ServerManagement / ASPIRE.Common.Enumerations)
        // where they are [MemoryPackable] and define the on-the-wire Redis format shared by the chat and master servers.
        // An unused, non-serializable copy previously lived in NEXUS.Contracts.ServerManagement and silently drifted.
        string[] forbiddenTypeNames = ["MatchServer", "MatchServerManager", "MatchStartData", "ServerStatus", "MatchOptions"];

        Type[] contractTypes = typeof(SecurityConstants).Assembly.GetTypes();

        contractTypes
            .Where(type => forbiddenTypeNames.Contains(type.Name))
            .Select(type => type.FullName)
            .Should().BeEmpty("server management cache models must only be defined in protocol-core");

        contractTypes
            .Where(type => type.Namespace == "NEXUS.Contracts.ServerManagement")
            .Should().BeEmpty("the NEXUS.Contracts.ServerManagement namespace was retired in favour of protocol-core");
    }

    [Fact]
    public void Domain_EnumsAndDtos_ShouldBeCorrect()
    {
        AccountType.Staff.Should().Be((AccountType)5);
        AccountType.VIP.Should().Be((AccountType)102);

        HostApplicationStatus.Approved.Should().Be((HostApplicationStatus)1);
        StreamerApplicationStatus.Approved.Should().Be((StreamerApplicationStatus)1);
        ClanTier.Leader.Should().Be((ClanTier)3);

        MatchChatMessageDto chat = new()
        {
            TimestampMs = 15000,
            PlayerSlot = 0,
            PlayerName = "MoonMeander",
            Team = 1,
            Channel = "ALL",
            Message = "good luck have fun"
        };

        chat.TeamName.Should().Be("Legion");
        chat.Message.Should().Be("good luck have fun");
    }

    [Fact]
    public void Admin_AdminUserDetailDto_SubAccountsAndHostAccountsShouldFilterProperly()
    {
        AdminUserDetailDto user = new()
        {
            Id = 1,
            EmailAddress = "admin@kongor.net",
            Accounts =
            [
                new AdminAccountItemDto { Id = 101, Name = "MainAcc", IsMain = true, Type = AccountType.Staff },
                new AdminAccountItemDto { Id = 102, Name = "HostAcc", IsMain = false, Type = AccountType.ServerHost },
                new AdminAccountItemDto { Id = 103, Name = "SmurfAcc", IsMain = false, Type = AccountType.Normal }
            ]
        };

        user.MainAccount.Should().NotBeNull();
        user.MainAccount!.Name.Should().Be("MainAcc");
        user.HostAccounts.Should().HaveCount(1);
        user.HostAccounts[0].Name.Should().Be("HostAcc");
        user.SubAccounts.Should().HaveCount(1);
        user.SubAccounts[0].Name.Should().Be("SmurfAcc");
        user.HasHostAccounts.Should().BeTrue();
        user.HasSubAccounts.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SecurityMasking_WithNullOrEmpty_ReturnsNoneMarker(string? input)
    {
        string masked = SecurityMasking.MaskToken(input);
        masked.Should().Be("<none>");
    }

    [Fact]
    public void SecurityMasking_WithStandardToken_ReturnsDefaultEightCharacterPrefix()
    {
        string token = "abcdef1234567890xyz";
        string masked = SecurityMasking.MaskToken(token);
        masked.Should().Be("abcdef12...");
    }

    [Fact]
    public void SecurityMasking_WithShortToken_ReturnsEntireTokenAndEllipsis()
    {
        string token = "abc";
        string masked = SecurityMasking.MaskToken(token);
        masked.Should().Be("abc...");
    }

    [Fact]
    public void SecurityMasking_WithCustomPrefixLength_RespectsSpecifiedLength()
    {
        string token = "1234567890";
        string masked = SecurityMasking.MaskToken(token, 4);
        masked.Should().Be("1234...");
    }
}
