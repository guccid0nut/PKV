// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text.Json;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_audio_studio' (alias: 'audio_studio').
/// Audio Voice & SFX Studio Pipeline: Broadcast audio loudness normalization (EBU R128 -16 LUFS,
/// -1.5 dBFS peak), channel layout routing (mono WAV for 3D spatialization vs stereo OGG for music),
/// multi-sample randomized K2 .sound XML definition synthesis, ElevenLabs prompt handoff, and in-engine preview.
/// </summary>
public sealed class AudioStudioHandler
{
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logger;
    private readonly AudioStudioService _audioStudioService;
    private readonly string _workspaceRoot;

    public AudioStudioHandler(
        TimeProvider? timeProvider = null,
        Action<string>? logger = null,
        AudioStudioService? audioStudioService = null,
        string? workspaceRoot = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? (msg => Console.Error.WriteLine($"[AudioStudio] {msg}"));
        _workspaceRoot = workspaceRoot ?? Directory.GetCurrentDirectory();
        _audioStudioService = audioStudioService ?? new AudioStudioService(_workspaceRoot);
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();

        string action = "normalize";
        if (arguments.TryGetProperty("action", out JsonElement actEl) && !string.IsNullOrWhiteSpace(actEl.GetString()))
        {
            action = actEl.GetString()!.Trim().ToLowerInvariant();
        }

        _logger?.Invoke($"Audio studio requested (Action: '{action}')...");

        switch (action)
        {
            case "normalize":
                return Normalize(arguments, sw);

            case "synthesize_voice":
            case "speech":
                return SynthesizeVoice(arguments, sw);

            case "bake_sound":
            case "bake":
                return await BakeSoundAsync(arguments, sw, cancellationToken).ConfigureAwait(false);

            case "preview":
                return Preview(arguments, sw);

            case "process_loopback":
            case "loopback":
                return ConfigureProcessLoopback(arguments, sw);

            case "sample_peak":
            case "meter":
                return SamplePeak(arguments, sw);

            default:
                sw.Stop();
                return ToolCallResult.Error($"Unknown action '{action}'. Valid actions are: 'normalize', 'synthesize_voice', 'bake_sound', 'preview', 'process_loopback', 'sample_peak'.");
        }
    }

    private ToolCallResult ConfigureProcessLoopback(JsonElement arguments, Stopwatch sw)
    {
        uint targetPid = 0U;
        if ((arguments.TryGetProperty("pid", out JsonElement pidEl) ||
             arguments.TryGetProperty("target_pid", out pidEl) ||
             arguments.TryGetProperty("targetProcessId", out pidEl)) &&
            pidEl.TryGetUInt32(out uint parsedPid))
        {
            targetPid = parsedPid;
        }
        else
        {
            Process[] honProcs = Process.GetProcessesByName("hon_x64");
            if (honProcs.Length > 0)
            {
                targetPid = (uint)honProcs[0].Id;
            }
        }

        bool includeTree = true;
        if ((arguments.TryGetProperty("include_tree", out JsonElement treeEl) ||
             arguments.TryGetProperty("includeTargetProcessTree", out treeEl)) &&
            (treeEl.ValueKind == JsonValueKind.True || treeEl.ValueKind == JsonValueKind.False))
        {
            includeTree = treeEl.GetBoolean();
        }

        bool built = AudioStudioService.BuildProcessIsolatedLoopbackParams(
            targetPid,
            includeTree,
            out byte[] activationBlob,
            out AudioStudioService.AudioClientActivationParamsBlob structuredParams);
        sw.Stop();

        if (!built)
        {
            return ToolCallResult.Error("Failed to build WASAPI process-isolated loopback activation parameters: provide a non-zero target PID or start hon_x64.exe.");
        }

        return ExecutionTruthEnvelope.CreateJsonResult(
            status: "success",
            executionMode: ExecutionMode.OfflineDirect,
            targetStatus: new TargetStatus
            {
                Client = new ClientLivenessStatus { Running = false, EngineReady = false },
                Server = new ServerLivenessStatus { Running = false, Port = 0 }
            },
            metrics: new ExecutionMetrics
            {
                RoundtripLatencyMs = sw.Elapsed.TotalMilliseconds,
                EngineRenderFrame = 0,
                Timestamp = _timeProvider.GetUtcNow().ToString("o")
            },
            data: new
            {
                action = "process_loopback",
                deviceInterfacePath = AudioStudioService.VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK,
                targetProcessId = structuredParams.ProcessLoopbackParams.TargetProcessId,
                loopbackMode = structuredParams.ProcessLoopbackParams.ProcessLoopbackMode.ToString(),
                activationBlobHex = Convert.ToHexString(activationBlob),
                activationBlobSizeBytes = activationBlob.Length
            },
            fidelity: ExecutionFidelity.StaticOfflineAnalysis);
    }

    private ToolCallResult SamplePeak(JsonElement arguments, Stopwatch sw)
    {
        uint targetPid = 0U;
        if ((arguments.TryGetProperty("pid", out JsonElement pidEl) ||
             arguments.TryGetProperty("target_pid", out pidEl) ||
             arguments.TryGetProperty("targetProcessId", out pidEl)) &&
            pidEl.TryGetUInt32(out uint parsedPid))
        {
            targetPid = parsedPid;
        }

        var result = AudioStudioService.SamplePidPeak(targetPid);
        sw.Stop();

        return ExecutionTruthEnvelope.CreateJsonResult(
            status: "success",
            executionMode: ExecutionMode.OfflineDirect,
            targetStatus: new TargetStatus
            {
                Client = new ClientLivenessStatus { Running = false, EngineReady = false },
                Server = new ServerLivenessStatus { Running = false, Port = 0 }
            },
            metrics: new ExecutionMetrics
            {
                RoundtripLatencyMs = sw.Elapsed.TotalMilliseconds,
                EngineRenderFrame = 0,
                Timestamp = _timeProvider.GetUtcNow().ToString("o")
            },
            data: new
            {
                action = "sample_peak",
                targetPid,
                success = result.Success,
                pidMaxPeak = result.PidMaxPeak,
                masterEndpointPeak = result.MasterEndpointPeak,
                matchingActiveSessions = result.MatchingActiveSessions,
                expiredSessionsSkipped = result.ExpiredSessionsSkipped,
                statusReason = result.StatusReason
            },
            fidelity: ExecutionFidelity.StaticOfflineAnalysis);
    }

    private ToolCallResult Normalize(JsonElement arguments, Stopwatch sw)
    {
        string inputFile = "voice_sample.wav";
        if ((arguments.TryGetProperty("input_file", out JsonElement ifEl) ||
             arguments.TryGetProperty("inputFile", out ifEl) ||
             arguments.TryGetProperty("file", out ifEl)) &&
            !string.IsNullOrWhiteSpace(ifEl.GetString()))
        {
            inputFile = ifEl.GetString()!.Trim();
        }

        string typeStr = "voice";
        if ((arguments.TryGetProperty("audio_type", out JsonElement atEl) ||
             arguments.TryGetProperty("audioType", out atEl) ||
             arguments.TryGetProperty("type", out atEl)) &&
            !string.IsNullOrWhiteSpace(atEl.GetString()))
        {
            typeStr = atEl.GetString()!.Trim();
        }
        AudioType audioType = AudioStudioService.ParseAudioType(typeStr);

        double lufs = -16.0;
        if ((arguments.TryGetProperty("lufs", out JsonElement lufsEl) ||
             arguments.TryGetProperty("integrated_loudness", out lufsEl)) &&
            lufsEl.TryGetDouble(out double l))
        {
            lufs = l;
        }

        double truePeak = -1.5;
        if ((arguments.TryGetProperty("true_peak", out JsonElement tpEl) ||
             arguments.TryGetProperty("truePeak", out tpEl)) &&
            tpEl.TryGetDouble(out double tp))
        {
            truePeak = tp;
        }

        string? outputFile = null;
        if ((arguments.TryGetProperty("output_file", out JsonElement ofEl) ||
             arguments.TryGetProperty("outputFile", out ofEl)) &&
            !string.IsNullOrWhiteSpace(ofEl.GetString()))
        {
            outputFile = ofEl.GetString()!.Trim();
        }

        AudioNormalizationRecipe recipe = _audioStudioService.FormulateNormalization(inputFile, audioType, lufs, truePeak, 11.0, outputFile);
        sw.Stop();

        List<ToolHandoff> handoffs = new List<ToolHandoff>
        {
            ToolHandoff.Create(
                server: "ffmpeg",
                tool: "ffmpeg_transcode_audio",
                reason: $"Normalize audio to EBU R128 standard ({recipe.IntegratedLoudnessLufs} LUFS, {recipe.TruePeakCeilingDb} dBFS peak)",
                arguments: new
                {
                    input_file = recipe.InputFile,
                    output_file = outputFile ?? Path.ChangeExtension(inputFile, recipe.OutputFormat),
                    loudnorm = true,
                    sample_rate = recipe.SampleRateHz,
                    channels = recipe.Channels
                },
                path: recipe.InputFile)
        };

        return ExecutionTruthEnvelope.CreateJsonResult(
            status: "success",
            executionMode: ExecutionMode.OfflineDirect,
            targetStatus: new TargetStatus
            {
                Client = new ClientLivenessStatus { Running = false, EngineReady = false },
                Server = new ServerLivenessStatus { Running = false, Port = 0 }
            },
            metrics: new ExecutionMetrics
            {
                RoundtripLatencyMs = sw.Elapsed.TotalMilliseconds,
                EngineRenderFrame = 0,
                Timestamp = _timeProvider.GetUtcNow().ToString("o")
            },
            data: new
            {
                action = "normalize",
                recipe = recipe
            },
            handoffs: handoffs,
            fidelity: ExecutionFidelity.StaticOfflineAnalysis);
    }

    private ToolCallResult SynthesizeVoice(JsonElement arguments, Stopwatch sw)
    {
        string prompt = "Prepare for battle!";
        if (arguments.TryGetProperty("prompt", out JsonElement pEl) && !string.IsNullOrWhiteSpace(pEl.GetString()))
        {
            prompt = pEl.GetString()!.Trim();
        }
        else if (arguments.TryGetProperty("text", out pEl) && !string.IsNullOrWhiteSpace(pEl.GetString()))
        {
            prompt = pEl.GetString()!.Trim();
        }

        string voiceId = "pNInz6obpgDQGcFmaJgB"; // Default narrator / hero voice ID
        if ((arguments.TryGetProperty("voice_id", out JsonElement viEl) ||
             arguments.TryGetProperty("voiceId", out viEl)) &&
            !string.IsNullOrWhiteSpace(viEl.GetString()))
        {
            voiceId = viEl.GetString()!.Trim();
        }

        sw.Stop();

        List<ToolHandoff> handoffs = new List<ToolHandoff>
        {
            ToolHandoff.Create(
                server: "ElevenLabs",
                tool: "creative_generate_speech",
                reason: $"Synthesize speech line with ElevenLabs: \"{prompt}\"",
                arguments: new
                {
                    text = prompt,
                    voice_id = voiceId
                })
        };

        return ExecutionTruthEnvelope.CreateJsonResult(
            status: "success",
            executionMode: ExecutionMode.OfflineDirect,
            targetStatus: new TargetStatus
            {
                Client = new ClientLivenessStatus { Running = false, EngineReady = false },
                Server = new ServerLivenessStatus { Running = false, Port = 0 }
            },
            metrics: new ExecutionMetrics
            {
                RoundtripLatencyMs = sw.Elapsed.TotalMilliseconds,
                EngineRenderFrame = 0,
                Timestamp = _timeProvider.GetUtcNow().ToString("o")
            },
            data: new
            {
                action = "synthesize_voice",
                prompt = prompt,
                voiceId = voiceId
            },
            handoffs: handoffs,
            fidelity: ExecutionFidelity.StaticOfflineAnalysis);
    }

    private async Task<ToolCallResult> BakeSoundAsync(JsonElement arguments, Stopwatch sw, CancellationToken cancellationToken)
    {
        string soundName = "hero_attack";
        if ((arguments.TryGetProperty("sound_name", out JsonElement snEl) ||
             arguments.TryGetProperty("soundName", out snEl) ||
             arguments.TryGetProperty("name", out snEl)) &&
            !string.IsNullOrWhiteSpace(snEl.GetString()))
        {
            soundName = snEl.GetString()!.Trim();
        }

        string typeStr = "voice";
        if ((arguments.TryGetProperty("audio_type", out JsonElement atEl) ||
             arguments.TryGetProperty("audioType", out atEl)) &&
            !string.IsNullOrWhiteSpace(atEl.GetString()))
        {
            typeStr = atEl.GetString()!.Trim();
        }
        AudioType audioType = AudioStudioService.ParseAudioType(typeStr);

        List<string> sampleFiles = new List<string>();
        if (arguments.TryGetProperty("sample_files", out JsonElement sfEl) && sfEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in sfEl.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                {
                    sampleFiles.Add(item.GetString()!.Trim());
                }
            }
        }
        else if (arguments.TryGetProperty("sample_file", out JsonElement sSingle) && !string.IsNullOrWhiteSpace(sSingle.GetString()))
        {
            sampleFiles.Add(sSingle.GetString()!.Trim());
        }

        double volume = 1.0;
        if (arguments.TryGetProperty("volume", out JsonElement volEl) && volEl.TryGetDouble(out double v))
        {
            volume = v;
        }

        int falloff = 1200;
        if (arguments.TryGetProperty("falloff", out JsonElement foEl) && foEl.TryGetInt32(out int fo))
        {
            falloff = fo;
        }

        string? vfsPath = null;
        if ((arguments.TryGetProperty("vfs_path", out JsonElement vpEl) ||
             arguments.TryGetProperty("vfsPath", out vpEl)) &&
            !string.IsNullOrWhiteSpace(vpEl.GetString()))
        {
            vfsPath = vpEl.GetString()!.Trim();
        }

        SoundXmlRecipe recipe = _audioStudioService.GenerateSoundXml(soundName, sampleFiles, audioType, volume, falloff, 200, 1500, vfsPath);

        bool writeToDisk = false;
        if (arguments.TryGetProperty("write_to_disk", out JsonElement wdEl) ||
            arguments.TryGetProperty("writeToDisk", out wdEl))
        {
            writeToDisk = wdEl.GetBoolean();
        }

        string? diskFileWritten = null;
        if (writeToDisk)
        {
            string outDir = Path.Combine(_workspaceRoot, "sounds", soundName);
            if (arguments.TryGetProperty("output_directory", out JsonElement odEl) && !string.IsNullOrWhiteSpace(odEl.GetString()))
            {
                outDir = odEl.GetString()!.Trim();
            }

            Directory.CreateDirectory(outDir);
            diskFileWritten = Path.Combine(outDir, $"{soundName}.sound");
            await File.WriteAllTextAsync(diskFileWritten, recipe.XmlContent, cancellationToken).ConfigureAwait(false);
            _logger?.Invoke($"Wrote K2 sound definition to '{diskFileWritten}'.");
        }

        sw.Stop();

        List<ToolHandoff> handoffs = new List<ToolHandoff>
        {
            ToolHandoff.Create(
                server: "kongor",
                tool: "kongor_audio_preview",
                reason: $"Preview 3D spatialized sound '{soundName}' in-engine",
                arguments: new
                {
                    sound_name = soundName,
                    sound_path = recipe.SampleFiles.FirstOrDefault() ?? $"{soundName}.wav",
                    x = 0,
                    y = 0,
                    z = 100
                },
                path: recipe.VfsDestinationPath)
        };

        return ExecutionTruthEnvelope.CreateJsonResult(
            status: "success",
            executionMode: ExecutionMode.OfflineDirect,
            targetStatus: new TargetStatus
            {
                Client = new ClientLivenessStatus { Running = false, EngineReady = false },
                Server = new ServerLivenessStatus { Running = false, Port = 0 }
            },
            metrics: new ExecutionMetrics
            {
                RoundtripLatencyMs = sw.Elapsed.TotalMilliseconds,
                EngineRenderFrame = 0,
                Timestamp = _timeProvider.GetUtcNow().ToString("o")
            },
            data: new
            {
                action = "bake_sound",
                recipe = recipe,
                diskFileWritten = diskFileWritten
            },
            handoffs: handoffs,
            fidelity: ExecutionFidelity.StaticOfflineAnalysis);
    }

    private ToolCallResult Preview(JsonElement arguments, Stopwatch sw)
    {
        string soundName = "hero_voice_preview";
        if (arguments.TryGetProperty("sound_name", out JsonElement snEl) && !string.IsNullOrWhiteSpace(snEl.GetString()))
        {
            soundName = snEl.GetString()!.Trim();
        }

        string soundPath = "sounds/preview.wav";
        if ((arguments.TryGetProperty("sound_path", out JsonElement spEl) ||
             arguments.TryGetProperty("file", out spEl)) &&
            !string.IsNullOrWhiteSpace(spEl.GetString()))
        {
            soundPath = spEl.GetString()!.Trim();
        }

        int x = 0, y = 0, z = 100;
        if (arguments.TryGetProperty("x", out JsonElement xEl) && xEl.TryGetInt32(out int xVal)) x = xVal;
        if (arguments.TryGetProperty("y", out JsonElement yEl) && yEl.TryGetInt32(out int yVal)) y = yVal;
        if (arguments.TryGetProperty("z", out JsonElement zEl) && zEl.TryGetInt32(out int zVal)) z = zVal;

        sw.Stop();

        List<ToolHandoff> handoffs = new List<ToolHandoff>
        {
            ToolHandoff.Create(
                server: "kongor",
                tool: "kongor_audio_preview",
                reason: $"Audition 3D audio playback at ({x}, {y}, {z})",
                arguments: new
                {
                    sound_name = soundName,
                    sound_path = soundPath,
                    x = x,
                    y = y,
                    z = z
                },
                path: soundPath)
        };

        return ExecutionTruthEnvelope.CreateJsonResult(
            status: "success",
            executionMode: ExecutionMode.OfflineDirect,
            targetStatus: new TargetStatus
            {
                Client = new ClientLivenessStatus { Running = false, EngineReady = false },
                Server = new ServerLivenessStatus { Running = false, Port = 0 }
            },
            metrics: new ExecutionMetrics
            {
                RoundtripLatencyMs = sw.Elapsed.TotalMilliseconds,
                EngineRenderFrame = 0,
                Timestamp = _timeProvider.GetUtcNow().ToString("o")
            },
            data: new
            {
                action = "preview",
                soundName = soundName,
                soundPath = soundPath,
                position = new { x, y, z }
            },
            handoffs: handoffs,
            fidelity: ExecutionFidelity.StaticOfflineAnalysis);
    }
}
