// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Result of a headless UI rendering operation.
/// </summary>
public sealed record UiRenderResult
{
    public bool Success { get; init; }
    public string ViewName { get; init; } = string.Empty;
    public string ResolvedType { get; init; } = string.Empty;
    public int Width { get; init; }
    public int Height { get; init; }
    public string Theme { get; init; } = "dark";
    public string? ArtifactPath { get; init; }
    public string Base64Png { get; init; } = string.Empty;
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Service interface for rendering Avalonia XAML views to PNG snapshots.
/// </summary>
public interface IHeadlessUiRenderer
{
    Task<UiRenderResult> RenderViewAsync(
        string viewName,
        int width = 1280,
        int height = 720,
        string theme = "dark",
        bool mockData = true,
        bool saveArtifact = true,
        string? customOutputPath = null,
        CancellationToken cancellationToken = default);

    IReadOnlyList<string> GetSupportedViews();
}

/// <summary>
/// Decoupled proxy renderer that executes the dedicated Kongor.HeadlessRenderer binary out-of-process,
/// keeping heavy Avalonia desktop dependencies out of the lightweight CLI distribution.
/// </summary>
public sealed class HeadlessUiRenderer : IHeadlessUiRenderer
{
    private readonly ILogger<HeadlessUiRenderer> _logger;
    private readonly TimeProvider _timeProvider;

    private static readonly string[] SupportedViewsList =
    [
        "DashboardView",
        "DiagnosticsView",
        "FileLockDialog",
        "GameServersView",
        "HeroViewportView",
        "LiveLogsView",
        "LocationGuardDialog",
        "MainView",
        "MainWindow",
        "ModManagerView",
        "ReplayConverterView",
        "ReplayStudioView",
        "SecretsConfigView",
        "SynchronisationBypassDialog",
        "UpdateDialog",
        "UsersView"
    ];

    public HeadlessUiRenderer(ILogger<HeadlessUiRenderer>? logger = null, TimeProvider? timeProvider = null)
    {
        _logger = logger ?? NullLogger<HeadlessUiRenderer>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public IReadOnlyList<string> GetSupportedViews() => SupportedViewsList;

    public async Task<UiRenderResult> RenderViewAsync(
        string viewName,
        int width = 1280,
        int height = 720,
        string theme = "dark",
        bool mockData = true,
        bool saveArtifact = true,
        string? customOutputPath = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(viewName))
        {
            return new UiRenderResult
            {
                Success = false,
                ErrorMessage = "Parameter 'view_name' cannot be null or whitespace."
            };
        }

        string repoRoot = FindRepoRoot();
        string rendererProjectPath = Path.Combine(repoRoot, "src", "tools", "headless-renderer", "Kongor.HeadlessRenderer.csproj");

        // Check for compiled executable first in Release/Debug output
        string[] candidatePaths =
        [
            Path.Combine(repoRoot, "src", "tools", "headless-renderer", "bin", "Release", "net11.0", "kongor-render.exe"),
            Path.Combine(repoRoot, "src", "tools", "headless-renderer", "bin", "Debug", "net11.0", "kongor-render.exe")
        ];

        string? executable = candidatePaths.FirstOrDefault(File.Exists);
        string runExecutable = executable ?? "dotnet";
        string runArguments = executable != null
            ? $"--view \"{viewName}\" --width {width} --height {height} --theme {theme} {(customOutputPath != null ? $"--output \"{customOutputPath}\"" : "")}"
            : $"run --project \"{rendererProjectPath}\" -c Release -- --view \"{viewName}\" --width {width} --height {height} --theme {theme} {(customOutputPath != null ? $"--output \"{customOutputPath}\"" : "")}";

        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = runExecutable,
                Arguments = runArguments,
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.Environment["KONGOR_HEADLESS_RENDER"] = "1";
            psi.Environment["KONGOR_DISABLE_DOCKER_PROBE"] = "1";

            using Process? proc = Process.Start(psi);
            if (proc == null)
            {
                return new UiRenderResult
                {
                    Success = false,
                    ViewName = viewName,
                    ErrorMessage = "Failed to launch headless renderer process."
                };
            }

            StringBuilder stdoutBuilder = new();
            StringBuilder stderrBuilder = new();
            object outputSync = new();
            TaskCompletionSource stdoutEof = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource stderrEof = new(TaskCreationOptions.RunContinuationsAsynchronously);

            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null)
                {
                    stdoutEof.TrySetResult();
                    return;
                }

                lock (outputSync)
                {
                    stdoutBuilder.AppendLine(e.Data);
                }
            };

            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null)
                {
                    stderrEof.TrySetResult();
                    return;
                }

                lock (outputSync)
                {
                    stderrBuilder.AppendLine(e.Data);
                }
            };

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using CancellationTokenSource timeoutCts = new(TimeSpan.FromSeconds(45));
            using CancellationTokenSource linkedCts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            try
            {
                await proc.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogWarning(
                    ex,
                    "[HeadlessUiRenderer] Headless renderer timed out or was cancelled for view '{ViewName}'; terminating process tree (PID={Pid}).",
                    viewName,
                    proc.Id);

                try
                {
                    if (!proc.HasExited)
                    {
                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(1000);
                    }
                }
                catch (Exception killEx)
                {
                    _logger.LogWarning(
                        killEx,
                        "[HeadlessUiRenderer] Failed to terminate headless renderer process tree for view '{ViewName}': {Message}",
                        viewName,
                        killEx.Message);
                }

                throw;
            }

            // Once the renderer process has exited, wait briefly for stdout/stderr EOF.
            // If an unexpected grandchild process kept inherited pipe handles open, cancel async reads
            // instead of blocking indefinitely after the renderer already emitted its JSON payload.
            Task drainTask = Task.WhenAll(stdoutEof.Task, stderrEof.Task);
            Task completedDrain = await Task.WhenAny(drainTask, Task.Delay(TimeSpan.FromSeconds(2), cancellationToken)).ConfigureAwait(false);
            if (!ReferenceEquals(completedDrain, drainTask))
            {
                _logger.LogWarning(
                    "[HeadlessUiRenderer] Renderer process (PID={Pid}) exited with code {ExitCode} for view '{ViewName}', but stdout/stderr pipes did not close within 2s (likely an inherited handle); cancelling async pipe reads.",
                    proc.Id,
                    proc.ExitCode,
                    viewName);
                try
                {
                    proc.CancelOutputRead();
                    proc.CancelErrorRead();
                }
                catch (InvalidOperationException cancelEx)
                {
                    _logger.LogDebug(cancelEx, "[HeadlessUiRenderer] Pipe read cancellation already finalized: {Message}", cancelEx.Message);
                }
            }

            string stdout;
            string stderr;
            lock (outputSync)
            {
                stdout = stdoutBuilder.ToString();
                stderr = stderrBuilder.ToString();
            }

            if (!string.IsNullOrWhiteSpace(stdout))
            {
                try
                {
                    UiRenderResult? parsed = JsonSerializer.Deserialize<UiRenderResult>(stdout, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (parsed != null) return parsed;
                }
                catch (JsonException jsonEx)
                {
                    _logger.LogWarning(jsonEx, "[HeadlessUiRenderer] Failed to parse renderer JSON output for view '{ViewName}': {Message}", viewName, jsonEx.Message);
                }
            }

            return new UiRenderResult
            {
                Success = false,
                ViewName = viewName,
                ErrorMessage = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : $"Renderer exited with code {proc.ExitCode}"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[HeadlessUiRenderer] Failed to execute decoupled renderer process: {Message}", ex.Message);
            return new UiRenderResult
            {
                Success = false,
                ViewName = viewName,
                ErrorMessage = $"Process execution failure: {ex.Message}"
            };
        }
    }

    private static string FindRepoRoot()
    {
        string[] searchStarts = [Directory.GetCurrentDirectory(), AppContext.BaseDirectory];
        foreach (string start in searchStarts)
        {
            DirectoryInfo? current = new(start);
            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, "ProjectKongor.slnx")) ||
                    (File.Exists(Path.Combine(current.FullName, "PROJECT.md")) && Directory.Exists(Path.Combine(current.FullName, "src"))))
                    return current.FullName;
                current = current.Parent;
            }
        }

        return Directory.GetCurrentDirectory();
    }
}
