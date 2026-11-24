// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: Program.cs
// System: NEXUS Replay Video Renderer Service
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using ASPIRE.Common.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.Replay;
using ReplayRenderer.Service.Configuration;
using ReplayRenderer.Service.Encoding;
using ReplayRenderer.Service.Models;
using ReplayRenderer.Service.Monitoring;
using ReplayRenderer.Service.ProcessLifecycle;
using ReplayRenderer.Service.Queues;

namespace ReplayRenderer.Service;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("=================================================================");
        Console.WriteLine(" NEXUS Heroes of Newerth Replay Renderer Service (.NET 10)");
        Console.WriteLine("=================================================================");

        if (HasReplayFlag(args)) return await RunCliAsync(args);

        if (HasHelpFlag(args))
        {
            PrintUsage();
            return 0;
        }

        return await RunDaemonAsync(args);
    }

    private static async Task<int> RunCliAsync(string[] args)
    {
        RenderJobConfiguration? config = ParseCommandLineArguments(args);
        if (config == null)
        {
            Console.Error.WriteLine("[Error] Invalid command line arguments. Use --help for usage instructions.");
            return 1;
        }

        IServiceCollection services = new ServiceCollection();

        // Register Logging
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSimpleConsole(options => { options.TimestampFormat = "[HH:mm:ss.fff] "; });
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        // Register Service Components
        services.AddHttpClient();
        services.AddSingleton<TelemetryLogger>();
        services.AddSingleton<ReplayRenderJobQueue>();
        services.AddTransient<WindowsJobObjectSandbox>();
        services.AddTransient<ReplayProcessLauncher>();
        services.AddTransient<FfmpegPipelineController>();
        services.AddSingleton(config);
        services.AddSingleton<ReplayRendererService>();

        IServiceProvider serviceProvider = services.BuildServiceProvider();
        ILogger<ReplayRendererService> logger = serviceProvider.GetRequiredService<ILogger<ReplayRendererService>>();
        ReplayRendererService orchestrator = serviceProvider.GetRequiredService<ReplayRendererService>();

        using CancellationTokenSource cts = new();
        Console.CancelKeyPress += (sender, e) =>
        {
            logger.LogWarning("[CLI] Cancel key press received. Initiating graceful shutdown...");
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            bool success = await orchestrator.ExecuteJobAsync(config, cts.Token);
            FfprobeVideoVerificationResult? verification = orchestrator.LastVerificationResult;
            if (success)
            {
                if (verification != null)
                {
                    logger.LogInformation("[CLI] Render job completed & ffprobe verified: {Summary}", verification.Summary);
                }
                else
                {
                    logger.LogInformation("[CLI] Render job completed with 100% success.");
                }
                return 0;
            }

            logger.LogError("[CLI] Render job failed to produce valid output video. Verification={Summary}",
                verification?.Summary ?? "not run");
            return 1;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "[CLI] Unhandled exception occurred during render job: {Message}", ex.Message);
            return 1;
        }
    }

    private static async Task<int> RunDaemonAsync(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        // Add Aspire Service Defaults & OpenTelemetry
        builder.AddServiceDefaults();

        // Register Service Components
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton<TelemetryLogger>();
        builder.Services.AddSingleton<ReplayRenderJobQueue>();
        builder.Services.AddTransient<WindowsJobObjectSandbox>();
        builder.Services.AddTransient<ReplayProcessLauncher>();
        builder.Services.AddTransient<FfmpegPipelineController>();
        builder.Services.AddSingleton<ReplayRendererService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ReplayRendererService>());

        // Register Replay Renderer Health Check
        builder.Services.AddHealthChecks().AddCheck<ReplayRendererHealthCheck>("Replay Renderer Service Health Check");

        WebApplication app = builder.Build();

        // Map Aspire Default Health Check Endpoints
        app.MapDefaultEndpoints();

        app.MapGet("/", () => "Replay Renderer Service Daemon Active");

        // Map Active Job Ingestion Endpoint (POST /render)
        app.MapPost("/render",
            (ReplayRenderJobRequestDto request, ReplayRenderJobQueue jobQueue, ILogger<ReplayRendererService> logger) =>
            {
                if (request.MatchId <= 0 && string.IsNullOrWhiteSpace(request.ReplayFilePath))
                    return Results.BadRequest(new { error = "MatchId or ReplayFilePath must be provided" });

                ReplayRenderJob job = new()
                {
                    MatchId = request.MatchId,
                    ReplayFilePath = request.ReplayFilePath,
                    TargetAccountId = request.TargetAccountId,
                    TargetPlayerIndex = request.TargetPlayerIndex,
                    TargetPlayerName = request.TargetPlayerName,
                    CameraMode = string.IsNullOrWhiteSpace(request.CameraMode) ? "PlayerPOV" : request.CameraMode,
                    Resolution = string.IsNullOrWhiteSpace(request.Resolution) ? "1080p" : request.Resolution,
                    Framerate = request.Framerate > 0 ? request.Framerate : 60,
                    ShowHud = request.ShowHud,
                    StartMs = Math.Max(0, request.StartMs),
                    DurationMs = Math.Max(0, request.DurationMs),
                    OutputPath = request.OutputPath ?? string.Empty,
                    CallbackUrl = request.CallbackUrl,
                    Status = "Queued"
                };

                jobQueue.EnqueueJob(job);
                logger.LogInformation(
                    "[ReplayRendererDaemon] Enqueued render job {JobId} for Match #{MatchId} (Camera={CameraMode}, POV={PlayerIndex}/{PlayerName}, Res={Resolution}@{Framerate}fps, ShowHud={ShowHud}, StartMs={StartMs}, DurationMs={DurationMs}, Callback={CallbackUrl})",
                    job.JobId,
                    job.MatchId,
                    job.CameraMode,
                    job.TargetPlayerIndex,
                    job.TargetPlayerName ?? "None",
                    job.Resolution,
                    job.Framerate,
                    job.ShowHud,
                    job.StartMs,
                    job.DurationMs,
                    job.CallbackUrl ?? "None");

                return Results.Accepted($"/render/status/{job.MatchId}", new ReplayRenderJobResponseDto
                {
                    JobId = job.JobId,
                    Status = "Queued",
                    Message = "Replay rendering job queued successfully",
                    EstimatedDurationSeconds = 0
                });
            });

        // Map Job Status Check Endpoints
        app.MapGet("/render/status/{matchId:int}", (int matchId, ReplayRenderJobQueue jobQueue) =>
        {
            ReplayRenderJob? job = jobQueue.GetJobByMatchId(matchId);
            if (job == null) return Results.NotFound(new { error = $"No job found for Match #{matchId}" });

            return Results.Ok(new
            {
                jobId = job.JobId,
                matchId = job.MatchId,
                status = job.Status,
                createdAt = job.CreatedAt,
                completedAt = job.CompletedAt,
                durationMs = job.RenderDurationMs,
                outputPath = job.OutputPath
            });
        });

        app.MapGet("/jobs", (ReplayRenderJobQueue jobQueue) => Results.Ok(jobQueue.GetAllJobs()));

        await app.RunAsync();
        return 0;
    }

    private static bool HasReplayFlag(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
            if (string.Equals(args[i], "--replay", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[i], "-r", StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    private static bool HasHelpFlag(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
            if (string.Equals(args[i], "--help", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[i], "-h", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[i], "/?", StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: ReplayRenderer.Service [options]");
        Console.WriteLine();
        Console.WriteLine("Required Options (for CLI mode):");
        Console.WriteLine("  --replay, -r <path>         Absolute or relative path to .honreplay file.");
        Console.WriteLine("  --output, -o <path>         Target output video path (.mp4).");
        Console.WriteLine();
        Console.WriteLine("Optional Parameters:");
        Console.WriteLine(
            "  --player, -p <1..10|name>   Target player number (1..10), player name, or hero for locked camera (default: 1).");
        Console.WriteLine("  --resolution, -res <WxH>    Output resolution (e.g. 1920x1080, 2560x1440).");
        Console.WriteLine("  --width, -w <pixels>        Horizontal resolution (default: 1920).");
        Console.WriteLine("  --height, -h <pixels>       Vertical resolution (default: 1080).");
        Console.WriteLine("  --fps <framerate>           Target frame rate (default: 60).");
        Console.WriteLine("  --start, -s <seconds>       Playback start offset in seconds (default: 0).");
        Console.WriteLine("  --duration, -d <seconds>    Target render duration in seconds (optional).");
        Console.WriteLine(
            "  --codec, -c <name>          Video codec: h264_nvenc, hevc_nvenc, libx264 (default: h264_nvenc).");
        Console.WriteLine("  --game-dir, -g <path>       Heroes of Newerth client directory containing hon_x64.exe.");
        Console.WriteLine("  --layer-dir, -l <path>      Directory containing VkLayer_hon_replay_capture.json/dll.");
        Console.WriteLine("  --pov <index>               Replay POV client index: -1 = Spectator, 0..9 = Player.");
        Console.WriteLine("  --timescale <float>         Playback speed multiplier (default: 1.0).");
        Console.WriteLine("  --cinematic-ui <bool>       Suppress 2D HUD and UI trees (default: false).");
        Console.WriteLine("  --bitrate, -b <string>      Target video bitrate (default: 25M).");
        Console.WriteLine("  --maxrate <string>          Maximum video bitrate (default: 40M).");
        Console.WriteLine("  --cq <int>                  Constant Quality / CRF value (default: 18).");
        Console.WriteLine("  --ffmpeg <path>             Path or name of ffmpeg binary (default: ffmpeg).");
        Console.WriteLine("  --ffprobe <path>            Path or name of ffprobe binary (default: ffprobe).");
        Console.WriteLine("  --skip-ffprobe              Skip post-export ffprobe MP4 verification (not recommended).");
        Console.WriteLine("  --headless <bool>           Run engine without creating visible windows (default: true).");
        Console.WriteLine("  --allow-foreground-activation <bool>  Allow ShowWindow/HWND_TOPMOST during capture (default: false).");
        Console.WriteLine("  --shared-memory             Use Win32 Shared Memory rather than Named Pipe.");
        Console.WriteLine("  --timeout <min>             Process timeout limit in minutes (default: 30).");
        Console.WriteLine("  --stall-timeout <sec>       Stall watchdog limit in seconds (default: 5).");
    }

    public static RenderJobConfiguration? ParseCommandLineArguments(string[] args)
    {
        return CommandLineParser.Parse(args);
    }
}
