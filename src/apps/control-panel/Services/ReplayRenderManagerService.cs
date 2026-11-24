// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kongor.Core.Configuration;
using NEXUS.Contracts.Replay;

namespace NEXUS.ControlPanel.GUI.Services;

public interface IReplayRenderManagerService
{
    ReplayRenderJobItemDto? ActiveJob { get; }
    IReadOnlyList<ReplayRenderJobItemDto> QueuedJobs { get; }
    IReadOnlyList<ReplayRenderJobItemDto> CompletedJobs { get; }
    bool IsProcessing { get; }

    event EventHandler? QueueChanged;
    event EventHandler<ReplayRenderJobItemDto>? JobProgressUpdated;

    Task<ReplayRenderJobItemDto> EnqueueJobAsync(ReplayRenderJobRequestDto request,
        ParsedReplayMetadata? metadata = null);

    Task<bool> CancelActiveJobAsync();
    bool RemoveQueuedJob(string jobId);
    string GetDefaultOutputDirectory();
}

public sealed class ReplayRenderManagerService(Action<string, string, bool>? logSink = null, HttpClient? httpClient = null, string? daemonEndpoint = null)
    : IReplayRenderManagerService
{
    private static readonly HttpClient SharedHttpClient = ASPIRE.Common.Http.SharedSocketsHttpClientFactory.Create(TimeSpan.FromSeconds(15));
    private static readonly JsonSerializerOptions DaemonJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly List<ReplayRenderJobItemDto> _completed = [];
    private readonly string? _daemonEndpoint = ResolveDaemonEndpoint(daemonEndpoint);
    private readonly HttpClient _httpClient = httpClient ?? SharedHttpClient;
    private readonly Lock _lock = new();
    private readonly Action<string, string, bool>? _logSink = logSink;
    private readonly List<ReplayRenderJobItemDto> _queue = [];
    private CancellationTokenSource? _activeJobCts;
    private bool _isProcessingLoopRunning;

    public ReplayRenderJobItemDto? ActiveJob { get; private set; }

    public IReadOnlyList<ReplayRenderJobItemDto> QueuedJobs
    {
        get
        {
            lock (_lock)
            {
                return [.. _queue];
            }
        }
    }

    public IReadOnlyList<ReplayRenderJobItemDto> CompletedJobs
    {
        get
        {
            lock (_lock)
            {
                return [.. _completed];
            }
        }
    }

    public bool IsProcessing => ActiveJob != null;

    public event EventHandler? QueueChanged;
    public event EventHandler<ReplayRenderJobItemDto>? JobProgressUpdated;

    public string GetDefaultOutputDirectory()
    {
        string solutionRoot = FindSolutionRoot();
        string outputDir = Path.Combine(solutionRoot, "artifacts", "videos");
        if (!Directory.Exists(outputDir))
            try
            {
                Directory.CreateDirectory(outputDir);
            }
            catch (Exception ex)
            {
                _logSink?.Invoke("REPLAY_RENDER",
                    $"Could not create default replay output directory '{outputDir}': {ex.Message}", true);
            }

        return outputDir;
    }

    public Task<ReplayRenderJobItemDto> EnqueueJobAsync(ReplayRenderJobRequestDto request,
        ParsedReplayMetadata? metadata = null)
    {
        ReplayRenderJobItemDto item = new()
        {
            JobId = Guid.NewGuid().ToString("N")[..8],
            MatchId = request.MatchId,
            ReplayFilePath = request.ReplayFilePath,
            MapName = metadata?.MapName ?? "Forests of Caldavar",
            TargetAccountId = request.TargetAccountId,
            TargetPlayerIndex = request.TargetPlayerIndex,
            TargetPlayerName = request.TargetPlayerName ??
                               (request.TargetAccountId > 0 ? $"Player #{request.TargetAccountId}" : "Free Cam"),
            CameraMode = request.CameraMode,
            Resolution = request.Resolution,
            Framerate = request.Framerate,
            ShowHud = request.ShowHud,
            StartMs = Math.Max(0, request.StartMs),
            DurationMs = Math.Max(0, request.DurationMs),
            Status = "Queued",
            OutputPath = string.IsNullOrWhiteSpace(request.OutputPath)
                ? Path.Combine(GetDefaultOutputDirectory(), $"Match_{request.MatchId}_{request.CameraMode}.mp4")
                : request.OutputPath,
            CreatedAt = TimeProvider.System.GetUtcNow()
        };

        lock (_lock)
        {
            _queue.Add(item);
        }

        _logSink?.Invoke("REPLAY_RENDER",
            $"Enqueued render job {item.JobId} for Match #{item.MatchId} (POV: {item.TargetPlayerName} [slot {item.TargetPlayerIndex}], {item.Resolution} @ {item.Framerate}fps, ShowHud={item.ShowHud}, StartMs={item.StartMs}, DurationMs={item.DurationMs})",
            false);
        QueueChanged?.Invoke(this, EventArgs.Empty);

        EnsureProcessingLoop();
        return Task.FromResult(item);
    }

    public Task<bool> CancelActiveJobAsync()
    {
        if (ActiveJob == null) return Task.FromResult(false);

        _logSink?.Invoke("REPLAY_RENDER",
            $"Cancelling active render job {ActiveJob.JobId} for Match #{ActiveJob.MatchId}...", false);
        ActiveJob.Status = "Cancelled";
        _activeJobCts?.Cancel();
        return Task.FromResult(true);
    }

    public bool RemoveQueuedJob(string jobId)
    {
        lock (_lock)
        {
            int idx = _queue.FindIndex(j => j.JobId == jobId);
            if (idx >= 0)
            {
                ReplayRenderJobItemDto removed = _queue[idx];
                _queue.RemoveAt(idx);
                _logSink?.Invoke("REPLAY_RENDER", $"Removed job {jobId} from queue.", false);
                QueueChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }
        }

        return false;
    }

    public static string GetPrimaryReplayDirectory()
    {
        string solutionRoot = FindSolutionRoot();
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Heroes of Newerth x64",
                "game", "replays"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive", "Documents",
                "Heroes of Newerth x64", "game", "replays"),
            Path.Combine(solutionRoot, "games", "client", "userdata", "game", "replays"),
            Path.Combine(solutionRoot, "games", "client", "game", "replays"),
            Path.Combine(solutionRoot, "tests", "fixtures", "replays")
        ];

        foreach (string candidate in candidates)
            if (Directory.Exists(candidate))
                return candidate;

        return candidates[0];
    }

    private static string FindSolutionRoot()
    {
        if (BundledLayoutResolver.TryResolve(out BundledLayout? layout))
        {
            return layout.Root;
        }

        string current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "ProjectKongor.slnx")) ||
                File.Exists(Path.Combine(current, "NEXUS.DistributedApplication.slnx")) ||
                File.Exists(Path.Combine(current, "NEXUS.DistributedApplication.sln")))
                return current;

            DirectoryInfo? parent = Directory.GetParent(current);
            if (parent == null) break;
            current = parent.FullName;
        }

        return Directory.GetCurrentDirectory();
    }

    private void EnsureProcessingLoop()
    {
        lock (_lock)
        {
            if (_isProcessingLoopRunning) return;
            _isProcessingLoopRunning = true;
        }

        Task.Run(ProcessQueueLoopAsync);
    }

    private async Task ProcessQueueLoopAsync()
    {
        while (true)
        {
            ReplayRenderJobItemDto? job = null;
            lock (_lock)
            {
                if (_queue.Count == 0)
                {
                    _isProcessingLoopRunning = false;
                    ActiveJob = null;
                    QueueChanged?.Invoke(this, EventArgs.Empty);
                    return;
                }

                job = _queue[0];
                _queue.RemoveAt(0);
                ActiveJob = job;
                _activeJobCts = new CancellationTokenSource();
            }

            QueueChanged?.Invoke(this, EventArgs.Empty);

            if (job != null) await ExecuteRenderJobAsync(job, _activeJobCts.Token);
        }
    }

    private static string? ResolveDaemonEndpoint(string? explicitEndpoint)
    {
        string? candidate = !string.IsNullOrWhiteSpace(explicitEndpoint)
            ? explicitEndpoint
            : Environment.GetEnvironmentVariable("REPLAY_RENDERER_DAEMON_URL")
              ?? Environment.GetEnvironmentVariable("SERVICES__REPLAY-RENDERER__HTTP__0")
              ?? Environment.GetEnvironmentVariable("services__replay-renderer__http__0");

        return string.IsNullOrWhiteSpace(candidate) ? null : candidate.Trim().TrimEnd('/');
    }

    private async Task ExecuteRenderJobAsync(ReplayRenderJobItemDto job, CancellationToken ct)
    {
        Stopwatch sw = Stopwatch.StartNew();
        int durationSec = job.DurationMs > 0 ? Math.Max(1, job.DurationMs / 1000) : 60;
        long totalEstimatedFrames = (long)Math.Max(1, job.Framerate) * durationSec;
        job.TotalFrames = totalEstimatedFrames;

        try
        {
            if (!string.IsNullOrWhiteSpace(_daemonEndpoint))
            {
                await ExecuteLiveDaemonRenderJobAsync(job, _daemonEndpoint, sw, totalEstimatedFrames, ct);
            }
            else
            {
                job.Status = "Processing [SIMULATED_PREVIEW]";
                _logSink?.Invoke("REPLAY_RENDER",
                    $"[SIMULATED_PREVIEW] No REPLAY_RENDERER_DAEMON_URL configured; running UI preview progression for job {job.JobId} (Match #{job.MatchId}). Configure REPLAY_RENDERER_DAEMON_URL to dispatch live FFmpeg/Vulkan renders.",
                    false);

                for (int step = 1; step <= 100; step++)
                {
                    if (ct.IsCancellationRequested)
                    {
                        job.Status = "Cancelled";
                        _logSink?.Invoke("REPLAY_RENDER",
                            $"[SIMULATED_PREVIEW] Job {job.JobId} was cancelled by operator.", false);
                        break;
                    }

                    await Task.Delay(120, ct);

                    job.ProgressPercent = step;
                    job.CurrentFrame = (long)(totalEstimatedFrames * (step / 100.0));
                    job.ElapsedSeconds = sw.Elapsed.TotalSeconds;
                    job.Fps = Math.Round(58.0 + Random.Shared.NextDouble() * 4.0, 1);

                    double rate = step / Math.Max(0.1, job.ElapsedSeconds);
                    job.EstimatedRemainingSeconds = Math.Round((100.0 - step) / Math.Max(0.1, rate), 1);

                    JobProgressUpdated?.Invoke(this, job);
                }

                if (job.Status != "Cancelled")
                {
                    job.Status = "Completed [SIMULATED_PREVIEW]";
                    job.ProgressPercent = 100;
                    job.CompletedAt = TimeProvider.System.GetUtcNow();
                    _logSink?.Invoke("REPLAY_RENDER",
                        $"[SIMULATED_PREVIEW] UI preview job {job.JobId} finished (target path: {job.OutputPath}).",
                        false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            job.Status = "Cancelled";
            _logSink?.Invoke("REPLAY_RENDER", $"Job {job.JobId} cancelled.", false);
        }
        catch (Exception ex)
        {
            job.Status = "Failed";
            job.ErrorMessage = ex.Message;
            _logSink?.Invoke("REPLAY_RENDER", $"Render job {job.JobId} failed: {ex.Message}", true);
        }
        finally
        {
            lock (_lock)
            {
                _completed.Insert(0, job);
                if (_completed.Count > 20) _completed.RemoveAt(_completed.Count - 1);
                ActiveJob = null;
            }

            QueueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task ExecuteLiveDaemonRenderJobAsync(
        ReplayRenderJobItemDto job,
        string baseEndpoint,
        Stopwatch sw,
        long totalEstimatedFrames,
        CancellationToken ct)
    {
        job.Status = "Submitting";
        _logSink?.Invoke("REPLAY_RENDER",
            $"[LIVE_DAEMON] Submitting job {job.JobId} for Match #{job.MatchId} to ReplayRenderer daemon at {baseEndpoint}/render...",
            false);
        JobProgressUpdated?.Invoke(this, job);

        ReplayRenderJobRequestDto submitPayload = new()
        {
            MatchId = job.MatchId,
            ReplayFilePath = job.ReplayFilePath,
            TargetAccountId = job.TargetAccountId,
            TargetPlayerIndex = job.TargetPlayerIndex,
            TargetPlayerName = job.TargetPlayerName,
            CameraMode = job.CameraMode,
            Resolution = job.Resolution,
            Framerate = job.Framerate,
            ShowHud = job.ShowHud,
            StartMs = job.StartMs,
            DurationMs = job.DurationMs,
            OutputPath = job.OutputPath
        };

        using HttpResponseMessage postResp = await _httpClient.PostAsJsonAsync(
            $"{baseEndpoint}/render", submitPayload, DaemonJsonOptions, ct);
        if (!postResp.IsSuccessStatusCode)
        {
            string errBody = await postResp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"ReplayRenderer daemon POST {baseEndpoint}/render returned HTTP {(int)postResp.StatusCode}: {errBody}");
        }

        ReplayRenderJobResponseDto? ack = await postResp.Content.ReadFromJsonAsync<ReplayRenderJobResponseDto>(
            DaemonJsonOptions, ct);
        string daemonJobId = !string.IsNullOrWhiteSpace(ack?.JobId) ? ack.JobId : job.JobId;
        job.Status = "Processing";
        _logSink?.Invoke("REPLAY_RENDER",
            $"[LIVE_DAEMON] Daemon accepted render job {job.JobId} (daemonJobId={daemonJobId}, status={ack?.Status ?? "Queued"}). Polling live telemetry...",
            false);

        double estimatedDurationSec = ack?.EstimatedDurationSeconds > 0
            ? ack.EstimatedDurationSeconds
            : Math.Max(10.0, job.DurationMs > 0 ? job.DurationMs / 1000.0 : 60.0);

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(1000, ct);

            DaemonJobSnapshot? snapshot = await PollDaemonJobSnapshotAsync(baseEndpoint, daemonJobId, job.MatchId, ct);
            job.ElapsedSeconds = Math.Round(sw.Elapsed.TotalSeconds, 1);

            if (snapshot != null)
            {
                if (!string.IsNullOrWhiteSpace(snapshot.OutputPath))
                    job.OutputPath = snapshot.OutputPath;

                string remoteStatus = snapshot.Status ?? "Processing";
                if (string.Equals(remoteStatus, "Completed", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(remoteStatus, "Succeeded", StringComparison.OrdinalIgnoreCase))
                {
                    job.Status = "Completed";
                    job.ProgressPercent = 100;
                    job.CurrentFrame = totalEstimatedFrames;
                    job.EstimatedRemainingSeconds = 0;
                    job.CompletedAt = snapshot.CompletedAt ?? TimeProvider.System.GetUtcNow();
                    if (job.ElapsedSeconds > 0.1)
                        job.Fps = Math.Round(totalEstimatedFrames / job.ElapsedSeconds, 1);

                    JobProgressUpdated?.Invoke(this, job);
                    _logSink?.Invoke("REPLAY_RENDER",
                        $"[LIVE_DAEMON] Render job {job.JobId} (daemonJobId={daemonJobId}) completed in {job.ElapsedSeconds:F1}s! Output: {job.OutputPath}",
                        false);
                    return;
                }

                if (string.Equals(remoteStatus, "Failed", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(remoteStatus, "Error", StringComparison.OrdinalIgnoreCase))
                {
                    string reason = !string.IsNullOrWhiteSpace(snapshot.ErrorMessage)
                        ? snapshot.ErrorMessage
                        : "Daemon reported status=Failed without ErrorMessage.";
                    throw new InvalidOperationException(
                        $"ReplayRenderer daemon job {daemonJobId} failed: {reason}");
                }

                if (string.Equals(remoteStatus, "Cancelled", StringComparison.OrdinalIgnoreCase))
                {
                    job.Status = "Cancelled";
                    _logSink?.Invoke("REPLAY_RENDER",
                        $"[LIVE_DAEMON] Daemon confirmed cancellation of job {daemonJobId}.", false);
                    return;
                }

                job.Status = remoteStatus;
            }

            double heuristicPct = Math.Min(95.0, Math.Round((sw.Elapsed.TotalSeconds / estimatedDurationSec) * 100.0, 1));
            job.ProgressPercent = Math.Max(job.ProgressPercent, heuristicPct);
            job.CurrentFrame = (long)(totalEstimatedFrames * (job.ProgressPercent / 100.0));
            if (job.ElapsedSeconds > 0.1 && job.CurrentFrame > 0)
                job.Fps = Math.Round(job.CurrentFrame / job.ElapsedSeconds, 1);
            job.EstimatedRemainingSeconds = Math.Max(0.0, Math.Round(estimatedDurationSec - sw.Elapsed.TotalSeconds, 1));

            JobProgressUpdated?.Invoke(this, job);
        }

        ct.ThrowIfCancellationRequested();
    }

    private async Task<DaemonJobSnapshot?> PollDaemonJobSnapshotAsync(
        string baseEndpoint,
        string daemonJobId,
        int matchId,
        CancellationToken ct)
    {
        try
        {
            using HttpResponseMessage jobsResp = await _httpClient.GetAsync($"{baseEndpoint}/jobs", ct);
            if (jobsResp.IsSuccessStatusCode)
            {
                List<DaemonJobSnapshot>? allJobs = await jobsResp.Content.ReadFromJsonAsync<List<DaemonJobSnapshot>>(
                    DaemonJsonOptions, ct);
                if (allJobs != null)
                {
                    DaemonJobSnapshot? byId = allJobs.FirstOrDefault(j =>
                        string.Equals(j.JobId, daemonJobId, StringComparison.OrdinalIgnoreCase));
                    if (byId != null) return byId;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logSink?.Invoke("REPLAY_RENDER",
                $"[LIVE_DAEMON] Transient poll error on GET {baseEndpoint}/jobs: {ex.Message}", true);
        }

        if (matchId > 0)
        {
            try
            {
                using HttpResponseMessage statusResp = await _httpClient.GetAsync(
                    $"{baseEndpoint}/render/status/{matchId}", ct);
                if (statusResp.IsSuccessStatusCode)
                    return await statusResp.Content.ReadFromJsonAsync<DaemonJobSnapshot>(DaemonJsonOptions, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logSink?.Invoke("REPLAY_RENDER",
                    $"[LIVE_DAEMON] Transient poll error on GET {baseEndpoint}/render/status/{matchId}: {ex.Message}",
                    true);
            }
        }

        return null;
    }

    private sealed class DaemonJobSnapshot
    {
        [JsonPropertyName("jobId")] public string? JobId { get; set; }
        [JsonPropertyName("matchId")] public int MatchId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; set; }
        [JsonPropertyName("outputPath")] public string? OutputPath { get; set; }
        [JsonPropertyName("completedAt")] public DateTimeOffset? CompletedAt { get; set; }
    }
}
