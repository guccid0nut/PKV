// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class TaoPhase6cAssetAudioPipelineTests
{
    [Fact]
    public void AssetBakeService_SuffixRuleMatching_AndNormalInversion()
    {
        var service = new AssetBakeService();

        // 1. Color texture -> BC7 sRGB
        var colorRes = service.InspectTexture("pyromancer_color.png");
        colorRes.Semantic.Should().Be(TextureSemantic.Color);
        colorRes.TargetDdsFormat.Should().Be("BC7_UNORM_SRGB");
        colorRes.ExtraFlags.Should().NotContain("-inverty");

        // 2. Normal texture -> BC5 linear with -inverty
        var normRes = service.InspectTexture("pyromancer_norm.png", invertNormalY: true);
        normRes.Semantic.Should().Be(TextureSemantic.Normal);
        normRes.TargetDdsFormat.Should().Be("BC5_UNORM");
        normRes.ExtraFlags.Should().Contain("-nocr");
        normRes.ExtraFlags.Should().Contain("-inverty");
        normRes.InvertNormalY.Should().BeTrue();

        // 3. Specular texture -> BC4 scalar
        var specRes = service.InspectTexture("pyromancer_spec.png");
        specRes.Semantic.Should().Be(TextureSemantic.Specular);
        specRes.TargetDdsFormat.Should().Be("BC4_UNORM");

        // 4. Glow texture -> BC4 scalar
        var glowRes = service.InspectTexture("pyromancer_glow.png");
        glowRes.Semantic.Should().Be(TextureSemantic.Glow);
        glowRes.TargetDdsFormat.Should().Be("BC4_UNORM");

        // 5. Fallback unknown texture -> BC7 UNORM
        var unkRes = service.InspectTexture("pyromancer_misc.png");
        unkRes.Semantic.Should().Be(TextureSemantic.Unknown);
        unkRes.TargetDdsFormat.Should().Be("BC7_UNORM");
    }

    [Fact]
    public void AssetBakeService_PotDimensionCalculations()
    {
        // Non-POT dimensions get rounded up to standard power-of-two boundaries
        var (w1, h1) = AssetBakeService.CalculatePotDimensions(500, 500);
        w1.Should().Be(512);
        h1.Should().Be(512);

        var (w2, h2) = AssetBakeService.CalculatePotDimensions(896, 1152);
        w2.Should().Be(1024);
        h2.Should().Be(2048);

        // Standard POT dimensions remain identical
        var (w3, h3) = AssetBakeService.CalculatePotDimensions(1024, 1024);
        w3.Should().Be(1024);
        h3.Should().Be(1024);
    }

    [Fact]
    public void AssetBakeService_MaterialXmlGeneration_AndPreflightGuard()
    {
        var service = new AssetBakeService();

        // 1. Hero shader with missing normalmap fallback to $flat_norm
        var samplers = new Dictionary<string, string>
        {
            ["diffuse"] = "pyromancer_color.dds"
            // omitted normalmap -> should trigger preflight guard to $flat_norm
        };

        var recipe = service.GenerateMaterial("pyromancer", "hero", samplers, enablePreflightGuard: true);
        recipe.ShaderType.Should().Be("hero");
        recipe.FlatNormFallbackUsed.Should().BeTrue();
        recipe.Samplers["normalmap"].Should().Be("$flat_norm");
        recipe.XmlContent.Should().Contain("<parameters shader=\"/shared/shaders/hero.shader\"");
        recipe.XmlContent.Should().Contain("<sampler name=\"teamcolor\" file=\"$team\" />");
        recipe.XmlContent.Should().Contain("<sampler name=\"normalmap\" file=\"$flat_norm\" />");

        // 2. Effect shader with translucent blend
        var effectRecipe = service.GenerateMaterial("fire_blast", "effect", samplers, enablePreflightGuard: false);
        effectRecipe.ShaderType.Should().Be("effect");
        effectRecipe.XmlContent.Should().Contain("<parameters shader=\"/shared/shaders/effect.shader\"");
        effectRecipe.XmlContent.Should().Contain("blend=\"translucent\"");
    }

    [Fact]
    public void AudioStudioService_FfmpegNormalization_AndFormatRouting()
    {
        var service = new AudioStudioService();

        // 1. Voice audio -> Mono 44.1kHz WAV at -16.0 LUFS
        var voiceNorm = service.FormulateNormalization("voice_raw.wav", AudioType.Voice, lufs: -16.0, truePeak: -1.5);
        voiceNorm.Channels.Should().Be(1);
        voiceNorm.SampleRateHz.Should().Be(44100);
        voiceNorm.OutputFormat.Should().Be("wav");
        voiceNorm.FfmpegArguments.Should().Contain("-af loudnorm=I=-16.0:TP=-1.5:LRA=11.0");
        voiceNorm.FfmpegArguments.Should().Contain("-ar 44100 -ac 1");

        // 2. Music audio -> Stereo 44.1kHz Vorbis OGG
        var musicNorm = service.FormulateNormalization("theme_raw.wav", AudioType.Music, lufs: -14.0, truePeak: -1.0);
        musicNorm.Channels.Should().Be(2);
        musicNorm.OutputFormat.Should().Be("ogg");
        musicNorm.FfmpegArguments.Should().Contain("-ac 2 -c:a libvorbis -q:a 5");
    }

    [Fact]
    public void AudioStudioService_MultiSampleSoundXmlGeneration()
    {
        var service = new AudioStudioService();
        var sampleFiles = new List<string>
        {
            "/heroes/pyromancer/sounds/voice/attack_01.wav",
            "/heroes/pyromancer/sounds/voice/attack_02.wav",
            "/heroes/pyromancer/sounds/voice/attack_03.wav"
        };

        var recipe = service.GenerateSoundXml("pyromancer_attack", sampleFiles, AudioType.Voice, volume: 1.0, falloff: 1200);
        recipe.SoundName.Should().Be("pyromancer_attack");
        recipe.SampleFiles.Count.Should().Be(3);
        recipe.XmlContent.Should().Contain("<sound name=\"pyromancer_attack\">");
        recipe.XmlContent.Should().Contain("<sample file=\"/heroes/pyromancer/sounds/voice/attack_01.wav\" volume=\"1.0\" falloff=\"1200\" minrange=\"200\" maxrange=\"1500\" />");
        recipe.XmlContent.Should().Contain("<sample file=\"/heroes/pyromancer/sounds/voice/attack_02.wav\" volume=\"1.0\" falloff=\"1200\" minrange=\"200\" maxrange=\"1500\" />");
        recipe.XmlContent.Should().Contain("<sample file=\"/heroes/pyromancer/sounds/voice/attack_03.wav\" volume=\"1.0\" falloff=\"1200\" minrange=\"200\" maxrange=\"1500\" />");
    }

    [Fact]
    public async Task AssetBakePipelineHandler_Inspect_AndBakeMaterial_EmitsToolHandoffs()
    {
        var handler = new AssetBakePipelineHandler();

        // 1. Inspect action
        var inspectArgs = JsonElement.Parse("{\"action\": \"inspect\", \"texture_path\": \"pyromancer_norm.png\", \"width\": 896, \"height\": 1152, \"invert_normal_y\": true}");
        var inspectRes = await handler.HandleAsync(inspectArgs);
        inspectRes.IsError.Should().BeFalse();

        string inspectJson = ((TextContentItem)inspectRes.Content[0]).Text;
        using var inspectDoc = JsonDocument.Parse(inspectJson);
        inspectDoc.RootElement.GetProperty("status").GetString().Should().Be("success");
        inspectDoc.RootElement.TryGetProperty("handoffs", out var inspectHandoffs).Should().BeTrue();
        inspectHandoffs.GetArrayLength().Should().BeGreaterOrEqualTo(1);
        inspectHandoffs[0].GetProperty("server").GetString().Should().Be("texconv");

        // 2. Bake material action
        var bakeArgs = JsonElement.Parse("{\"action\": \"bake_material\", \"material_name\": \"pyromancer_alt1\", \"shader_type\": \"hero\"}");
        var bakeRes = await handler.HandleAsync(bakeArgs);
        bakeRes.IsError.Should().BeFalse();

        string bakeJson = ((TextContentItem)bakeRes.Content[0]).Text;
        using var bakeDoc = JsonDocument.Parse(bakeJson);
        bakeDoc.RootElement.GetProperty("status").GetString().Should().Be("success");
        bakeDoc.RootElement.TryGetProperty("handoffs", out var bakeHandoffs).Should().BeTrue();
        var servers = bakeHandoffs.EnumerateArray().Select(h => h.GetProperty("server").GetString()).ToList();
        servers.Should().Contain("texconv");
        servers.Should().Contain("kongor");
    }

    [Fact]
    public async Task AudioStudioHandler_Normalize_AndBakeSound_EmitsToolHandoffs()
    {
        var handler = new AudioStudioHandler();

        // 1. Normalize action
        var normArgs = JsonElement.Parse("{\"action\": \"normalize\", \"input_file\": \"voice_raw.wav\", \"audio_type\": \"voice\"}");
        var normRes = await handler.HandleAsync(normArgs);
        normRes.IsError.Should().BeFalse();

        string normJson = ((TextContentItem)normRes.Content[0]).Text;
        using var normDoc = JsonDocument.Parse(normJson);
        normDoc.RootElement.GetProperty("status").GetString().Should().Be("success");
        normDoc.RootElement.TryGetProperty("handoffs", out var normHandoffs).Should().BeTrue();
        normHandoffs[0].GetProperty("server").GetString().Should().Be("ffmpeg");

        // 2. Bake sound action
        var soundArgs = JsonElement.Parse("{\"action\": \"bake_sound\", \"sound_name\": \"hero_attack\", \"sample_files\": [\"attack_01.wav\", \"attack_02.wav\"]}");
        var soundRes = await handler.HandleAsync(soundArgs);
        soundRes.IsError.Should().BeFalse();

        string soundJson = ((TextContentItem)soundRes.Content[0]).Text;
        using var soundDoc = JsonDocument.Parse(soundJson);
        soundDoc.RootElement.GetProperty("status").GetString().Should().Be("success");
        soundDoc.RootElement.TryGetProperty("handoffs", out var soundHandoffs).Should().BeTrue();
        soundHandoffs[0].GetProperty("server").GetString().Should().Be("kongor");
    }

    [Fact]
    public void McpToolRegistry_RegistersPhase6cToolsAndAliases()
    {
        McpToolRegistry registry = new(registerDefaults: true);

        registry.HasTool("kongor_asset_bake_pipeline").Should().BeTrue();
        registry.HasTool("asset_bake_pipeline").Should().BeTrue();
        registry.HasTool("kongor_audio_studio").Should().BeTrue();
        registry.HasTool("audio_studio").Should().BeTrue();

        var assetTool = registry.GetTool("kongor_asset_bake_pipeline");
        assetTool.Should().NotBeNull();
        assetTool!.InputSchema.Properties.Should().ContainKey("shader_type");
        assetTool.InputSchema.Properties.Should().ContainKey("invert_normal_y");

        var audioTool = registry.GetTool("kongor_audio_studio");
        audioTool.Should().NotBeNull();
        audioTool!.InputSchema.Properties.Should().ContainKey("audio_type");
        audioTool.InputSchema.Properties.Should().ContainKey("sample_files");
    }
}
