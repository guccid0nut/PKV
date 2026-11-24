// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using DAWNBRINGER.WebPortal.UI.Services.Replay;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayVulkanRenderer;

namespace ASPIRE.Tests;

public class ReplayVulkanRendererTests
{
    [Trait("Category", "Manual")]
    [Fact]
    public async Task ReplayVulkanRenderer_ParseLocalReplay_SuccessfullyExtractsKeyframesAndEvents()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        string? replayPath = null;
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidateShared = Path.Combine(dir.FullName, "games", "shared", "replays", "M2060568725.honreplay");
            if (File.Exists(candidateShared))
            {
                replayPath = candidateShared;
                break;
            }

            string candidateClient = Path.Combine(dir.FullName, "games", "client", "replays", "M2060568725.honreplay");
            if (File.Exists(candidateClient))
            {
                replayPath = candidateClient;
                break;
            }

            string candidateClientDirect = Path.Combine(dir.FullName, "games", "client", "M2060568725.honreplay");
            if (File.Exists(candidateClientDirect))
            {
                replayPath = candidateClientDirect;
                break;
            }

            dir = dir.Parent;
        }

        Assert.True(!string.IsNullOrEmpty(replayPath) && File.Exists(replayPath),
            $"Test replay file missing across parent search from: {AppContext.BaseDirectory}");

        HonReplayParser parser = new(NullLogger<HonReplayParser>.Instance);
        MatchReplayTimelineDto? timeline = await parser.ParseReplayFileAsync(replayPath, 2060568725);

        Assert.NotNull(timeline);
        Assert.True(timeline.Keyframes.Count > 0, "Parsed keyframes should not be empty");
        Assert.True(timeline.TotalDurationSeconds > 0, "Total duration should be positive");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void SilkVulkanContext_InitializeHeadless_Succeeds()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        using SilkVulkanContext context = new();
        bool success = context.InitializeHeadless(640, 480);
        Assert.True(success, "SilkVulkanContext headless initialization failed");
    }
}
