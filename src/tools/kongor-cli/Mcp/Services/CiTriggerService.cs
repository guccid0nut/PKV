// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Kongor.Cli.Mcp.Protocol;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Service for triggering, monitoring, and inspecting GitHub Actions CI/CD workflows.
/// Uses raw HttpClient without third-party SDK dependencies, with intelligent auth token
/// resolution (environment variable or gh CLI fallback).
/// </summary>
public sealed class CiTriggerService
{
    private const string DefaultOwner = "guccidonut";
    private const string DefaultRepo = "PKV";
    private const string BaseUrl = "https://api.github.com";

    private readonly HttpClient _http;
    private readonly Action<string> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly string _owner;
    private readonly string _repo;

    public CiTriggerService(
        Action<string>? logger = null,
        HttpClient? httpClient = null,
        TimeProvider? timeProvider = null,
        string? owner = null,
        string? repo = null)
    {
        _logger = logger ?? (msg => Console.Error.WriteLine($"[CI] {msg}"));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _owner = owner ?? Environment.GetEnvironmentVariable("GITHUB_REPOSITORY_OWNER") ?? DefaultOwner;
        _repo = repo ?? Environment.GetEnvironmentVariable("GITHUB_REPOSITORY_NAME") ?? DefaultRepo;

        _http = httpClient ?? ASPIRE.Common.Http.SharedSocketsHttpClientFactory.Create();
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("kongor-mcp/1.0");
        }
        if (_http.DefaultRequestHeaders.Accept.Count == 0)
        {
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }
    }

    private async Task EnsureAuthHeaderAsync(CancellationToken ct)
    {
        if (_http.DefaultRequestHeaders.Authorization != null)
        {
            return;
        }

        string? token = Environment.GetEnvironmentVariable("GITHUB_TOKEN")
                     ?? Environment.GetEnvironmentVariable("GH_TOKEN");

        if (string.IsNullOrWhiteSpace(token))
        {
            token = await TryGetGhCliTokenAsync(ct);
        }

        if (!string.IsNullOrWhiteSpace(token))
        {
            _logger("Authenticated via GitHub token.");
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        }
        else
        {
            _logger("Warning: No GitHub token discovered. Authenticated actions will fail.");
        }
    }

    private async Task<string?> TryGetGhCliTokenAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "gh",
                Arguments = "auth token",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return null;

            string token = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(token))
            {
                _logger("Resolved GitHub token via 'gh auth token'.");
                return token.Trim();
            }
        }
        catch (Exception ex)
        {
            _logger($"Failed to resolve token via gh CLI: {ex.Message}");
        }

        return null;
    }

    public async Task<ToolCallResult> TriggerWorkflowAsync(string workflow, string branch, CancellationToken ct)
    {
        await EnsureAuthHeaderAsync(ct);
        _logger($"Dispatching workflow '{workflow}' on branch '{branch}' in {_owner}/{_repo}...");

        DateTimeOffset dispatchTime = _timeProvider.GetUtcNow().AddSeconds(-2);
        string url = $"{BaseUrl}/repos/{_owner}/{_repo}/actions/workflows/{workflow}/dispatches";
        var body = new { @ref = branch };

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync(url, body, ct);
        }
        catch (Exception ex)
        {
            _logger($"Dispatch HTTP request error: {ex.Message}");
            return ToolCallResult.Error($"Failed to reach GitHub API: {ex.Message}");
        }

        if (response.StatusCode == System.Net.HttpStatusCode.NoContent || response.IsSuccessStatusCode)
        {
            _logger($"[CI] Successfully triggered '{workflow}' on branch '{branch}'. Polling for new run...");

            // Poll for up to 10 seconds to discover the newly triggered run without race conditions
            long? newRunId = null;
            JsonElement latestRun = default;

            for (int i = 0; i < 5; i++)
            {
                await Task.Delay(2000, ct);
                try
                {
                    string listUrl = $"{BaseUrl}/repos/{_owner}/{_repo}/actions/workflows/{workflow}/runs?per_page=5";
                    var listResp = await _http.GetFromJsonAsync<JsonElement>(listUrl, ct);
                    if (listResp.TryGetProperty("workflow_runs", out var runs) && runs.GetArrayLength() > 0)
                    {
                        foreach (var run in runs.EnumerateArray())
                        {
                            if (run.TryGetProperty("created_at", out var createdEl) &&
                                DateTimeOffset.TryParse(createdEl.GetString(), out var createdAt) &&
                                createdAt >= dispatchTime)
                            {
                                latestRun = run;
                                newRunId = run.GetProperty("id").GetInt64();
                                break;
                            }
                        }
                    }

                    if (newRunId.HasValue) break;
                }
                catch (Exception pollEx)
                {
                    _logger($"Polling warning: {pollEx.Message}");
                }
            }

            if (newRunId.HasValue)
            {
                return ToolCallResult.Success(
                    $"Workflow '{workflow}' dispatched on branch '{branch}'.\n\n" +
                    FormatRunDetails(latestRun));
            }

            return ToolCallResult.Success(
                $"Workflow '{workflow}' dispatched on branch '{branch}'.\n" +
                $"Run is currently queueing in GitHub Actions. Use action='status' workflow='{workflow}' to monitor.");
        }

        string errorContent = await response.Content.ReadAsStringAsync(ct);
        return ToolCallResult.Error($"GitHub API error ({response.StatusCode}):\n{errorContent}");
    }

    public async Task<ToolCallResult> GetWorkflowStatusAsync(string workflow, long? runId, CancellationToken ct)
    {
        await EnsureAuthHeaderAsync(ct);

        if (runId.HasValue)
        {
            _logger($"Fetching status for run #{runId.Value}...");
            string url = $"{BaseUrl}/repos/{_owner}/{_repo}/actions/runs/{runId.Value}";
            try
            {
                var run = await _http.GetFromJsonAsync<JsonElement>(url, ct);
                return ToolCallResult.Success(FormatRunDetails(run));
            }
            catch (Exception ex)
            {
                return ToolCallResult.Error($"Failed to fetch run #{runId}: {ex.Message}");
            }
        }

        _logger($"Fetching latest run for workflow '{workflow}'...");
        string listUrl = $"{BaseUrl}/repos/{_owner}/{_repo}/actions/workflows/{workflow}/runs?per_page=1";
        try
        {
            var listResp = await _http.GetFromJsonAsync<JsonElement>(listUrl, ct);
            if (listResp.TryGetProperty("workflow_runs", out var runs) && runs.GetArrayLength() > 0)
            {
                return ToolCallResult.Success(FormatRunDetails(runs[0]));
            }

            return ToolCallResult.Success($"No recent runs found for workflow '{workflow}'.");
        }
        catch (Exception ex)
        {
            return ToolCallResult.Error($"Failed to fetch workflow runs: {ex.Message}");
        }
    }

    public async Task<ToolCallResult> ListWorkflowRunsAsync(string workflow, CancellationToken ct)
    {
        await EnsureAuthHeaderAsync(ct);
        _logger($"Listing recent runs for workflow '{workflow}'...");

        string url = $"{BaseUrl}/repos/{_owner}/{_repo}/actions/workflows/{workflow}/runs?per_page=10";
        try
        {
            var resp = await _http.GetFromJsonAsync<JsonElement>(url, ct);
            if (!resp.TryGetProperty("workflow_runs", out var runs) || runs.GetArrayLength() == 0)
            {
                return ToolCallResult.Success($"No runs found for workflow '{workflow}'.");
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Recent runs for '{workflow}':\n");
            sb.AppendLine("| Run ID | Status | Conclusion | Branch | Duration | SHA |");
            sb.AppendLine("|--------|--------|------------|--------|----------|-----|");

            foreach (var run in runs.EnumerateArray())
            {
                long id = run.GetProperty("id").GetInt64();
                string status = run.GetProperty("status").GetString() ?? "unknown";
                string conclusion = run.TryGetProperty("conclusion", out var c) ? c.GetString() ?? "-" : "-";
                string sha = run.TryGetProperty("head_sha", out var s) ? s.GetString()![..Math.Min(7, s.GetString()!.Length)] : "-------";
                string branch = run.TryGetProperty("head_branch", out var b) ? b.GetString() ?? "-" : "-";

                string duration = "-";
                if (run.TryGetProperty("run_started_at", out var started) &&
                    run.TryGetProperty("updated_at", out var updated) &&
                    DateTimeOffset.TryParse(started.GetString(), out var start) &&
                    DateTimeOffset.TryParse(updated.GetString(), out var end))
                {
                    duration = $"{(end - start).TotalMinutes:F1}m";
                }

                string icon = conclusion switch
                {
                    "success" => "[PASS]",
                    "failure" => "[FAIL]",
                    "cancelled" => "[CANCEL]",
                    _ => status == "in_progress" ? "[RUNNING]" : "[QUEUED]"
                };

                sb.AppendLine($"| {id} | {icon} {status} | {conclusion} | {branch} | {duration} | {sha} |");
            }

            return ToolCallResult.Success(sb.ToString());
        }
        catch (Exception ex)
        {
            return ToolCallResult.Error($"Failed to list runs: {ex.Message}");
        }
    }

    public async Task<ToolCallResult> GetWorkflowLogsAsync(long? runId, string workflow, int tailLines, CancellationToken ct)
    {
        await EnsureAuthHeaderAsync(ct);

        long targetRunId;
        if (runId.HasValue)
        {
            targetRunId = runId.Value;
        }
        else
        {
            string listUrl = $"{BaseUrl}/repos/{_owner}/{_repo}/actions/workflows/{workflow}/runs?per_page=1";
            var listResp = await _http.GetFromJsonAsync<JsonElement>(listUrl, ct);
            if (!listResp.TryGetProperty("workflow_runs", out var runs) || runs.GetArrayLength() == 0)
            {
                return ToolCallResult.Error($"No runs found for workflow '{workflow}'.");
            }
            targetRunId = runs[0].GetProperty("id").GetInt64();
        }

        _logger($"Fetching jobs and log diagnostics for run #{targetRunId}...");

        // Query jobs to get exact failure steps and logs
        string jobsUrl = $"{BaseUrl}/repos/{_owner}/{_repo}/actions/runs/{targetRunId}/jobs";
        try
        {
            var jobsResp = await _http.GetFromJsonAsync<JsonElement>(jobsUrl, ct);
            if (jobsResp.TryGetProperty("jobs", out var jobs) && jobs.GetArrayLength() > 0)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"Job Summary for Run #{targetRunId}:\n");

                foreach (var job in jobs.EnumerateArray())
                {
                    string jobName = job.GetProperty("name").GetString() ?? "job";
                    string jobStatus = job.GetProperty("status").GetString() ?? "";
                    string jobConclusion = job.TryGetProperty("conclusion", out var jc) ? jc.GetString() ?? "in_progress" : "in_progress";
                    long jobId = job.GetProperty("id").GetInt64();

                    sb.AppendLine($"Job '{jobName}' (ID: {jobId}) - Status: {jobStatus}, Conclusion: {jobConclusion}");

                    if (job.TryGetProperty("steps", out var steps))
                    {
                        foreach (var step in steps.EnumerateArray())
                        {
                            string stepName = step.GetProperty("name").GetString() ?? "step";
                            string stepConclusion = step.TryGetProperty("conclusion", out var sc) ? sc.GetString() ?? "pending" : "pending";
                            sb.AppendLine($"   - Step: {stepName} [{stepConclusion}]");
                        }
                    }
                }

                // If gh CLI is available, fetch stdout log snippet
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "gh",
                        Arguments = $"run view {targetRunId} --log-failed",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        string ghLog = await proc.StandardOutput.ReadToEndAsync(ct);
                        await proc.WaitForExitAsync(ct);
                        if (!string.IsNullOrWhiteSpace(ghLog))
                        {
                            var lines = ghLog.Split('\n');
                            var tail = lines.TakeLast(Math.Min(lines.Length, tailLines));
                            sb.AppendLine("\nFailed Step Log Tail:\n");
                            sb.AppendLine(string.Join("\n", tail));
                        }
                    }
                }
                catch
                {
                    // Fall back to job summary above
                }

                return ToolCallResult.Success(sb.ToString());
            }

            return ToolCallResult.Success($"Run #{targetRunId} has no completed jobs yet.");
        }
        catch (Exception ex)
        {
            return ToolCallResult.Error($"Failed to fetch workflow logs: {ex.Message}");
        }
    }

    public async Task<ToolCallResult> CancelWorkflowRunAsync(long runId, CancellationToken ct)
    {
        await EnsureAuthHeaderAsync(ct);
        _logger($"Cancelling run #{runId}...");

        string url = $"{BaseUrl}/repos/{_owner}/{_repo}/actions/runs/{runId}/cancel";
        try
        {
            var resp = await _http.PostAsync(url, null, ct);
            if (resp.IsSuccessStatusCode || resp.StatusCode == System.Net.HttpStatusCode.Accepted)
            {
                return ToolCallResult.Success($"Workflow run #{runId} cancelled.");
            }

            string err = await resp.Content.ReadAsStringAsync(ct);
            return ToolCallResult.Error($"Failed to cancel run #{runId} ({resp.StatusCode}): {err}");
        }
        catch (Exception ex)
        {
            return ToolCallResult.Error($"Error cancelling run #{runId}: {ex.Message}");
        }
    }

    private static string FormatRunDetails(JsonElement run)
    {
        long id = run.GetProperty("id").GetInt64();
        string status = run.GetProperty("status").GetString() ?? "unknown";
        string conclusion = run.TryGetProperty("conclusion", out var c) ? c.GetString() ?? "in progress" : "in progress";
        string sha = run.TryGetProperty("head_sha", out var s) ? s.GetString()![..Math.Min(7, s.GetString()!.Length)] : "-------";
        string url = run.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "";
        string branch = run.TryGetProperty("head_branch", out var b) ? b.GetString() ?? "main" : "main";

        return $"Run #{id} ({branch})\n" +
               $"Status: {status} | Conclusion: {conclusion}\n" +
               $"Commit: {sha}\n" +
               $"URL: {url}";
    }
}
