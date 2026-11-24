// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ModernizationSynergyTests.cs
// System: Kongor.Mcp.Tests - Phase 3 Engine Modernization
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using WILLOWMAKER.Core.Services;
using Xunit;

namespace Kongor.Mcp.Tests;

/// <summary>
///     Validates Phase 3: Zero-Touch Engine Modernization subsystems:
///     - 3D Spatial Audio & OpenAL Soft HRTF coordinate transformations and config generation.
///     - K2 engine low-latency audio cvar tuning.
///     - Vulkan 1.3 / DXVK driver report inspection and error handling.
/// </summary>
public sealed class ModernizationSynergyTests
{
    private static JsonElement ParseResult(ToolCallResult result)
    {
        result.IsError.Should().BeFalse(result.FirstText);
        result.Content.Should().NotBeEmpty();
        string text = result.Content[0] is TextContentItem tc ? tc.Text : result.FirstText;
        using JsonDocument doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void SpatialAudio_K2ToOpenAlCoordinates_AccurateConversion()
    {
        // K2: +X Right, +Y Forward, +Z Up
        // OpenAL: +X Right, +Y Up, -Z Forward

        // Cardinal: Center
        (float X, float Y, float Z) center = AudioModernizationService.ConvertK2ToOpenAlCoordinates(0f, 0f, 0f);
        center.X.Should().Be(0f);
        center.Y.Should().Be(0f);
        center.Z.Should().Be(0f);

        // Cardinal: 100 units forward in K2 (+Y) -> -100 units in OpenAL (-Z)
        (float X, float Y, float Z) forward = AudioModernizationService.ConvertK2ToOpenAlCoordinates(0f, 100f, 0f);
        forward.X.Should().Be(0f);
        forward.Y.Should().Be(0f);
        forward.Z.Should().Be(-100f);

        // Cardinal: 100 units right in K2 (+X) -> +100 units in OpenAL (+X)
        (float X, float Y, float Z) right = AudioModernizationService.ConvertK2ToOpenAlCoordinates(100f, 0f, 0f);
        right.X.Should().Be(100f);
        right.Y.Should().Be(0f);
        right.Z.Should().Be(0f);

        // Cardinal: 100 units up in K2 (+Z) -> +100 units in OpenAL (+Y)
        (float X, float Y, float Z) up = AudioModernizationService.ConvertK2ToOpenAlCoordinates(0f, 0f, 100f);
        up.X.Should().Be(0f);
        up.Y.Should().Be(100f);
        up.Z.Should().Be(0f);
    }

    [Fact]
    public void SpatialAudio_GenerateAlsoftConfig_ProducesValidDirectives()
    {
        AudioModernizationService service = new(NullLogger<AudioModernizationService>.Instance);

        string config = service.GenerateAlsoftConfig(
            enableHrtf: true,
            hrtfProfileName: AudioModernizationService.BuiltInKemarName,
            enableOcclusionDamping: true,
            frequency: 48000);

        config.Should().Contain(OperatingSystem.IsWindows() ? "drivers = wasapi" : "drivers = ");
        config.Should().Contain("hrtf = true");
        config.Should().Contain("frequency = 48000");
        config.Should().Contain("stereo-mode = headphones");
        config.Should().Contain("stereo-encoding = hrtf");
        config.Should().Contain("default-hrtf = Built-In 48000hz");
        config.Should().Contain("resampler = cubic");
        config.Should().Contain("occlusion-gain = 0.25");
        config.Should().Contain("sources = 256");
    }

    [Fact]
    public void SpatialAudio_GenerateK2AudioConfig_GeneratesOptimizedCvars()
    {
        AudioModernizationService service = new(NullLogger<AudioModernizationService>.Instance);

        string cvars = service.GenerateK2AudioConfig(
            enableSpatialAudio: true,
            enableOcclusionDamping: true,
            numChannels: 128,
            mixRate: 48000);

        cvars.Should().Contain("SetSave \"sound_numChannels\" \"128\"");
        cvars.Should().Contain("SetSave \"sound_bufferLength\" \"256\"");
        cvars.Should().Contain("SetSave \"sound_mixRate\" \"48000\"");
        cvars.Should().Contain("SetSave \"sound_geometryOcclusion\" \"true\"");
        cvars.Should().Contain("SetSave \"sound_customRolloff\" \"true\"");
    }

    [Fact]
    public async Task AudioPreviewHandler_VerifySoundbank_ReturnsSpatialReadyReport()
    {
        AudioPreviewHandler handler = new(TimeProvider.System);

        using JsonDocument argsDoc = JsonDocument.Parse("""
        {
            "action": "verify_soundbank"
        }
        """);

        ToolCallResult result = await handler.HandleAsync(argsDoc.RootElement, CancellationToken.None);
        JsonElement root = ParseResult(result);

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("verify_soundbank");
        root.GetProperty("spatialAudioReady").GetBoolean().Should().BeTrue();
        root.GetProperty("totalFilesScanned").GetInt32().Should().BeGreaterThan(0);
        root.GetProperty("corruptedFiles").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task VulkanManageHandler_Status_ReturnsValidVulkanDiagnostics()
    {
        VulkanManageHandler handler = new();

        using JsonDocument argsDoc = JsonDocument.Parse("""
        {
            "action": "status",
            "target": "client"
        }
        """);

        ToolCallResult result = await handler.HandleAsync(argsDoc.RootElement, CancellationToken.None);
        JsonElement root = ParseResult(result);

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("status");
        root.GetProperty("target").GetString().Should().Be("client");
        root.TryGetProperty("hasSystemVulkanLoader", out _).Should().BeTrue();
        root.TryGetProperty("hasDxvkD3D9", out _).Should().BeTrue();
        root.TryGetProperty("hasDotLocalRedirection", out _).Should().BeTrue();
    }

    [Fact]
    public async Task VulkanManageHandler_InvalidAction_ReturnsGracefulError()
    {
        VulkanManageHandler handler = new();

        using JsonDocument argsDoc = JsonDocument.Parse("""
        {
            "action": "nonexistent_action"
        }
        """);

        ToolCallResult result = await handler.HandleAsync(argsDoc.RootElement, CancellationToken.None);
        result.IsError.Should().BeTrue();
        result.FirstText.Should().Contain("Unknown action");
    }
}
