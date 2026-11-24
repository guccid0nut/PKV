// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using KONGOR.MasterServer.Controllers.News;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NEXUS.Contracts.Data;
using Xunit;

namespace KONGOR.MasterServer.Tests.Controllers;

public sealed class NewsControllerTests
{
    private readonly Mock<IAccountRepository> _accountRepositoryMock = new();

    private NewsController CreateController()
    {
        return new NewsController(
            NullLogger<NewsController>.Instance,
            _accountRepositoryMock.Object
        );
    }

    [Fact]
    public async Task GetMotdFeed_ReturnsValidMotdFeedPayload()
    {
        // Arrange
        _accountRepositoryMock
            .Setup(r => r.GetApprovedStreamerTwitchUsernamesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["hon_pro_player", "newerth_streamer"]);

        NewsController controller = CreateController();

        // Act
        IActionResult result = await controller.GetMotdFeed("en");

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        OkObjectResult okResult = (OkObjectResult)result;
        okResult.Value.Should().BeOfType<MotdFeedResponseDto>();

        MotdFeedResponseDto feed = (MotdFeedResponseDto)okResult.Value!;
        feed.Version.Should().Be(NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion);

        // Validate Spotlight (Gwynplaine)
        feed.Spotlight.Should().NotBeNull();
        feed.Spotlight.HeroId.Should().Be("Hero_Gwynplaine");
        feed.Spotlight.Name.Should().Be("Gwynplaine");
        feed.Spotlight.Model.Should().Be("/heroes/gwynplaine/preview.mdf");
        feed.Spotlight.Anim.Should().Be("idle");
        feed.Spotlight.Abilities.Should().HaveCount(4);

        // Validate Slides (Abyssal Bubbles, v5.0.1 Update, Audio & Streamers)
        feed.Slides.Should().HaveCount(4);
        MotdSlideDto slide1 = feed.Slides[0];
        slide1.Id.Should().Be("bubbles");
        slide1.Title.Should().Be("Abyssal Bubbles");
        slide1.Model.Should().Be("/heroes/bubbles/alt16/preview.mdf");
        slide1.Anim.Should().Be("idle");

        MotdSlideDto slide2 = feed.Slides[1];
        slide2.Id.Should().Be("fps");
        slide2.Badge.Should().Be("UPDATE 5.0.2");
        slide2.Title.Should().Contain("Queue Arcade");
        slide2.Title.Should().NotContain("Preload");
        slide2.Bullets.Should().Contain(b => b.Contains("Queue Arcade", StringComparison.Ordinal));
        slide2.Bullets.Should().Contain(b => b.Contains("Strikers", StringComparison.Ordinal));

        MotdSlideDto slide4 = feed.Slides[3];
        slide4.Id.Should().Be("twitch");
        slide4.Streamers.Should().Contain(["hon_pro_player", "newerth_streamer"]);

        // Validate Vault Deals
        feed.Vault.Should().HaveCount(2);
        feed.Vault[0].Title.Should().Be("Join the Discord");
        feed.Vault[0].Price.Should().Be("discord.gg/projectkongor");
    }

    [Fact]
    public async Task GetMotdFeed_HandlesRepositoryFailureGracefully()
    {
        // Arrange: Repository throws
        _accountRepositoryMock
            .Setup(r => r.GetApprovedStreamerTwitchUsernamesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB timeout"));

        NewsController controller = CreateController();

        // Act
        IActionResult result = await controller.GetMotdFeed("en");

        // Assert: Still returns Ok with fallback empty streamers list
        result.Should().BeOfType<OkObjectResult>();
        OkObjectResult okResult = (OkObjectResult)result;
        MotdFeedResponseDto feed = (MotdFeedResponseDto)okResult.Value!;
        feed.Spotlight.HeroId.Should().Be("Hero_Gwynplaine");
        feed.Slides[3].Streamers.Should().BeEmpty();
    }

    /// <summary>
    /// protects: NewsController HTML MOTD endpoint content integrity and non-commercial v5.0.1 highlights.
    /// fails_when: GenerateNewsHtml regresses to legacy commercial placeholders ("Lucky Bundles" / "Grab Bags"), stale queue-entry preload claims, or omits v5.0.1 features.
    /// seam: Real static HTML generator NewsController.GenerateNewsHtml invoked by GET /events/common/client/motd/index.php.
    /// </summary>
    [Fact]
    public void GenerateNewsHtml_Renders501HighlightsWithoutCommercialPlaceholders()
    {
        // Act
        string html = NewsController.GenerateNewsHtml();

        // Assert
        html.Should().Contain("v5.0.2 ACTIVE");
        html.Should().Contain("War Effort");
        html.Should().Contain("Queue Arcade");
        html.Should().Contain("Spectate &amp; Mentor");
        html.Should().Contain("10v10 Chaos");
        html.Should().Contain("Strikers (Soccer)");
        html.Should().Contain("PKV Guard");
        html.Should().NotContain("Queue-Entry Warming");
        html.Should().NotContain("Lucky Bundles");
        html.Should().NotContain("Grab Bags");
    }
}
