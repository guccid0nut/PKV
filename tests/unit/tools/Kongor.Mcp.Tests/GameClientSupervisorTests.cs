// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Services;
using Moq;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class GameClientSupervisorTests
{
    [Fact]
    public void ClientOutputRingBuffer_EnforcesCapacityAndEvictsOldest()
    {
        // Arrange
        const int capacity = 5;
        ClientOutputRingBuffer buffer = new(capacity);

        // Act
        for (int i = 1; i <= 8; i++)
        {
            buffer.AppendLine($"Line {i}");
        }

        // Assert
        buffer.Count.Should().Be(5);
        buffer.IsEmpty.Should().BeFalse();

        var lines = buffer.GetLines(10);
        lines.Should().HaveCount(5);
        lines[0].Should().Be("Line 4");
        lines[^1].Should().Be("Line 8");
    }

    [Fact]
    public void ClientOutputRingBuffer_Clear_ResetsQueue()
    {
        // Arrange
        ClientOutputRingBuffer buffer = new(10);
        buffer.AppendLine("Hello");
        buffer.AppendLine("World");

        // Act
        buffer.Clear();

        // Assert
        buffer.Count.Should().Be(0);
        buffer.IsEmpty.Should().BeTrue();
        buffer.GetLines().Should().BeEmpty();
    }

    [Theory]
    [InlineData("[FATAL] Crash inside engine", "FATAL")]
    [InlineData("fatal: out of memory", "FATAL")]
    [InlineData("UnhandledException at 0x1234", "FATAL")]
    [InlineData("[ERROR] Failed to load texture", "ERROR")]
    [InlineData("error: socket timeout", "ERROR")]
    [InlineData("Shader compile failed on material", "ERROR")]
    [InlineData("[WARN] High latency detected", "WARN")]
    [InlineData("warning: missing localization", "WARN")]
    [InlineData("Client ready and waiting", "INFO")]
    public void ClientOutputRingBuffer_ParseClientLogLevel_CategorizesCorrectly(string line, string expectedLevel)
    {
        string level = ClientOutputRingBuffer.ParseClientLogLevel(line);
        level.Should().Be(expectedLevel);
    }

    [Theory]
    [InlineData("INFO", "all", true)]
    [InlineData("INFO", "error", false)]
    [InlineData("WARN", "error", false)]
    [InlineData("ERROR", "error", true)]
    [InlineData("FATAL", "error", true)]
    [InlineData("WARN", "warn", true)]
    [InlineData("ERROR", "warn", true)]
    public void ClientOutputRingBuffer_MatchesSeverityFilter_FiltersAccurately(string actual, string filter, bool expected)
    {
        bool result = ClientOutputRingBuffer.MatchesSeverityFilter(actual, filter);
        result.Should().Be(expected);
    }

    [Fact]
    public void ClientModMountBuilder_BuildClientLaunchArguments_InjectsVfsAndPerformanceArgs()
    {
        // Arrange
        ClientModMountBuilder builder = new();

        // Act
        var args = builder.BuildClientLaunchArguments(
            gameDir: "C:\\Games\\HoN",
            mode: "practice",
            connectAddress: null,
            masterServer: "127.0.0.1:55555",
            resolution: "1920x1080",
            fullscreen: false,
            maxFps: 1000,
            customNickname: "TestPlayer",
            windowMode: "headless");

        // Assert
        args.Should().Contain(a => a.StartsWith("-mod base;game") && a.EndsWith(";override"));
        args.Should().Contain(a => a.StartsWith("-pipe willowmaker-ipc"));
        args.Should().Contain("+Set host_maxFPS 1000");
        args.Should().Contain("+Set vid_pos -32000 -32000");
        args.Should().Contain("+Set sound_disable true");
        args.Should().Contain(a => a.StartsWith("+StartGame practice"));
    }

    [Fact]
    public void ClientModMountBuilder_BuildClientLaunchArguments_SecondarySlot_AppliesRamDietAndIsolatedCredentials()
    {
        // Arrange
        ClientModMountBuilder builder = new();

        // Act - Slot 1 (Secondary slot in foreground windowed mode)
        var slot1Args = builder.BuildClientLaunchArguments(
            gameDir: "C:\\Games\\HoN",
            mode: "practice",
            connectAddress: null,
            masterServer: "127.0.0.1:55555",
            resolution: "1280x720",
            fullscreen: false,
            maxFps: 60,
            customNickname: "TestSlot1",
            windowMode: "foreground",
            slotIndex: 1,
            pipeName: "hon-client-1");

        // Assert - Slot 1 RAM Diet & Isolation
        slot1Args.Should().Contain("+Set vid_textureDownsize 1");
        slot1Args.Should().Contain("+Set sound_disable true");
        slot1Args.Should().Contain("+Set login_rememberPassword false");
        slot1Args.Should().Contain("-logfile console_client_1.log");
        slot1Args.Should().Contain("-pipe hon-client-1");

        // Act - Slot 0 (Primary slot in foreground windowed mode)
        var slot0Args = builder.BuildClientLaunchArguments(
            gameDir: "C:\\Games\\HoN",
            mode: "practice",
            connectAddress: null,
            masterServer: "127.0.0.1:55555",
            resolution: "1920x1080",
            fullscreen: false,
            maxFps: 60,
            customNickname: "TestSlot0",
            windowMode: "foreground",
            slotIndex: 0,
            pipeName: "willowmaker-ipc");

        // Assert - Slot 0 Full Quality & Default Credentials
        slot0Args.Should().NotContain("+Set vid_textureDownsize 1");
        slot0Args.Should().NotContain("+Set sound_disable true");
        slot0Args.Should().Contain("+Set login_rememberPassword true");
        slot0Args.Should().Contain("-logfile console.log");
        slot0Args.Should().Contain("-pipe willowmaker-ipc");
    }

    [Fact]
    public void ClientModMountBuilder_TryParseResolution_ValidAndInvalidInputs()
    {
        ClientModMountBuilder builder = new();

        builder.TryParseResolution("1920x1080", out int width, out int height).Should().BeTrue();
        width.Should().Be(1920);
        height.Should().Be(1080);

        builder.TryParseResolution("invalid", out _, out _).Should().BeFalse();
        builder.TryParseResolution("", out _, out _).Should().BeFalse();
    }

    [Fact]
    public void ClientProcessSupervisor_IsWillowmakerExecutable_DetectsWillowmakerBinaries()
    {
        ClientProcessSupervisor.IsWillowmakerExecutable("WILLOWMAKER.exe").Should().BeTrue();
        ClientProcessSupervisor.IsWillowmakerExecutable("willowmaker").Should().BeTrue();
        ClientProcessSupervisor.IsWillowmakerExecutable("hon_x64.exe").Should().BeFalse();
        ClientProcessSupervisor.IsWillowmakerExecutable("hon.exe").Should().BeFalse();
    }

    [Fact]
    public void GameClientManageHandler_Constructor_ExposesDecomposedServices()
    {
        // Arrange
        Mock<IClientProcessSupervisor> mockSupervisor = new();
        Mock<IClientOutputRingBuffer> mockBuffer = new();
        Mock<IClientModMountBuilder> mockMountBuilder = new();

        // Act
        GameClientManageHandler handler = new(
            processSupervisor: mockSupervisor.Object,
            outputBuffer: mockBuffer.Object,
            modMountBuilder: mockMountBuilder.Object);

        // Assert
        handler.ProcessSupervisor.Should().BeSameAs(mockSupervisor.Object);
        handler.OutputRingBuffer.Should().BeSameAs(mockBuffer.Object);
        handler.ModMountBuilder.Should().BeSameAs(mockMountBuilder.Object);
    }
}
