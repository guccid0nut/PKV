// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: WillowmakerIntegrationTests.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using FluentAssertions;
using WILLOWMAKER.Core.ViewModels;
using Xunit;

namespace ReplayStudio.Tests;

public class WillowmakerIntegrationTests
{
    [Fact]
    public void ReplayConverterViewModel_OpenInReplayStudio_HandlesValidReplayPath()
    {
        ReplayConverterViewModel vm = new()
        {
            ReplayFilePath = "C:\\Replays\\grand_finals_match.honreplay"
        };

        vm.OpenInReplayStudioCommand.Execute(null);

        vm.LogText.Should().Contain("Launching Replay Studio Desktop GUI");
        vm.LogText.Should().Contain("grand_finals_match.honreplay");
    }

    [Fact]
    public void ReplayConverterViewModel_OpenInReplayStudio_HandlesEmptyReplayPath()
    {
        ReplayConverterViewModel vm = new()
        {
            ReplayFilePath = ""
        };

        vm.OpenInReplayStudioCommand.Execute(null);

        vm.LogText.Should().Contain("Launching Replay Studio Desktop GUI");
    }
}
