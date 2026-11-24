// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Kongor.Mcp.E2E.Tests.Client;

public sealed class McpProcessClient : IAsyncDisposable, IDisposable
{
    private readonly string _repoRoot;
    private readonly Action<string>? _logger;
    private Process? _process;
    private StreamWriter? _stdin;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonRpcResponse>> _pendingRequests = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly List<string> _stderrLogs = [];
    private readonly CancellationTokenSource _cts = new();
    private Task? _stdoutReaderTask;
    private Task? _stderrReaderTask;
    private int _requestIdCounter;
    private bool _isDisposed;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public IReadOnlyList<string> StderrLogs => _stderrLogs.ToArray();
    public bool IsProcessRunning => _process != null && !_process.HasExited;
    public int? ProcessId => _process?.Id;

    public McpProcessClient(Action<string>? logger = null)
    {
        _logger = logger;
        _repoRoot = FindRepoRoot();
    }

    public static string FindRepoRoot()
    {
        string[] searchOrigins = [AppContext.BaseDirectory, Directory.GetCurrentDirectory()];
        foreach (string origin in searchOrigins)
        {
            DirectoryInfo? current = new(origin);
            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, "PROJECT.md")) ||
                    File.Exists(Path.Combine(current.FullName, "ProjectKongor.slnx")) ||
                    (Directory.Exists(Path.Combine(current.FullName, "src")) && Directory.Exists(Path.Combine(current.FullName, "tests"))))
                {
                    return current.FullName;
                }
                current = current.Parent;
            }
        }
        return Directory.GetCurrentDirectory();
    }

    public async Task StartAsync(TimeSpan? startupTimeout = null)
    {
        if (_process != null && !_process.HasExited)
            return;

        TimeSpan timeout = startupTimeout ?? TimeSpan.FromSeconds(30);

#if DEBUG
        string primaryConfig = "Debug";
        string fallbackConfig = "Release";
#else
        string primaryConfig = "Release";
        string fallbackConfig = "Debug";
#endif

        string cliDll = Path.Combine(_repoRoot, "src", "tools", "kongor-cli", "bin", primaryConfig, "net11.0", "kongor.dll");
        string cliExe = Path.Combine(_repoRoot, "src", "tools", "kongor-cli", "bin", primaryConfig, "net11.0", "kongor.exe");

        if (!File.Exists(cliExe) && !File.Exists(cliDll))
        {
            cliDll = Path.Combine(_repoRoot, "src", "tools", "kongor-cli", "bin", fallbackConfig, "net11.0", "kongor.dll");
            cliExe = Path.Combine(_repoRoot, "src", "tools", "kongor-cli", "bin", fallbackConfig, "net11.0", "kongor.exe");
        }

        string cliProj = Path.Combine(_repoRoot, "src", "tools", "kongor-cli", "Kongor.Cli.csproj");

        ProcessStartInfo psi = new()
        {
            WorkingDirectory = _repoRoot,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        psi.Environment["KONGOR_MCP_OUTCOME_LOG_DISABLED"] = "1";

        if (File.Exists(cliExe))
        {
            psi.FileName = cliExe;
            psi.Arguments = "mcp";
        }
        else if (File.Exists(cliDll))
        {
            psi.FileName = "dotnet";
            psi.Arguments = $"exec \"{cliDll}\" mcp";
        }
        else
        {
            psi.FileName = "dotnet";
            psi.Arguments = $"run --project \"{cliProj}\" -- mcp";
        }

        _logger?.Invoke($"[McpProcessClient] Launching: {psi.FileName} {psi.Arguments} in {psi.WorkingDirectory}");

        _process = new Process { StartInfo = psi };
        _process.Start();

        _stdin = new StreamWriter(_process.StandardInput.BaseStream, new UTF8Encoding(false))
        {
            AutoFlush = true
        };

        _stdoutReaderTask = Task.Run(ReadStdoutPumpAsync);
        _stderrReaderTask = Task.Run(ReadStderrPumpAsync);

        // Give the process a brief moment to initialize
        await Task.Delay(100);

        if (_process.HasExited)
        {
            string err = string.Join("\n", _stderrLogs);
            throw new InvalidOperationException($"MCP server exited immediately with code {_process.ExitCode}. Stderr: {err}");
        }

        _logger?.Invoke($"[McpProcessClient] Started process PID={_process.Id}");
    }

    public async Task<InitializeResult?> InitializeAsync(string clientName = "Kongor.Mcp.E2E.Tests", string clientVersion = "5.0.0")
    {
        InitializeParams initParams = new()
        {
            ProtocolVersion = "2024-11-05",
            ClientInfo = new ClientInfo { Name = clientName, Version = clientVersion },
            Capabilities = new ClientCapabilities()
        };

        JsonRpcResponse resp = await SendRequestAsync("initialize", initParams);
        if (resp.Error != null)
        {
            throw new InvalidOperationException($"Initialize failed with error [{resp.Error.Code}]: {resp.Error.Message}");
        }

        if (resp.Result.HasValue)
        {
            InitializeResult? result = JsonSerializer.Deserialize<InitializeResult>(resp.Result.Value.GetRawText(), JsonOpts);
            
            // Send initialized notification as per MCP spec
            await SendNotificationAsync("notifications/initialized", new { });
            return result;
        }

        return null;
    }

    public async Task<JsonRpcResponse> PingAsync()
    {
        return await SendRequestAsync("ping", new { });
    }

    public async Task<ToolsListResult?> ListToolsAsync()
    {
        JsonRpcResponse resp = await SendRequestAsync("tools/list", new { });
        if (resp.Error != null)
            throw new InvalidOperationException($"tools/list failed [{resp.Error.Code}]: {resp.Error.Message}");

        if (resp.Result.HasValue)
        {
            return JsonSerializer.Deserialize<ToolsListResult>(resp.Result.Value.GetRawText(), JsonOpts);
        }

        return new ToolsListResult();
    }

    public async Task<ToolCallResult> CallToolAsync(string toolName, object? arguments, TimeSpan? timeout = null)
    {
        ToolCallParams callParams = new()
        {
            Name = toolName,
            Arguments = arguments
        };

        JsonRpcResponse resp = await SendRequestAsync("tools/call", callParams, timeout);
        if (resp.Error != null)
        {
            return new ToolCallResult
            {
                IsError = true,
                Content = [new ContentBlock { Type = "text", Text = $"RPC Error {resp.Error.Code}: {resp.Error.Message}" }]
            };
        }

        if (resp.Result.HasValue)
        {
            return JsonSerializer.Deserialize<ToolCallResult>(resp.Result.Value.GetRawText(), JsonOpts)
                   ?? new ToolCallResult { IsError = true };
        }

        return new ToolCallResult { IsError = true };
    }

    public async Task<TResult?> CallToolTypedAsync<TResult>(string toolName, object? arguments, TimeSpan? timeout = null) where TResult : class
    {
        ToolCallResult toolResult = await CallToolAsync(toolName, arguments, timeout);
        string? text = toolResult.GetFirstText();
        if (string.IsNullOrWhiteSpace(text))
            return null;

        try
        {
            return JsonSerializer.Deserialize<TResult>(text, JsonOpts);
        }
        catch (JsonException ex)
        {
            _logger?.Invoke($"[McpProcessClient] Failed to deserialize tool result: {ex.Message}. Text was: {text}");
            throw;
        }
    }

    public async Task<JsonRpcResponse> SendRequestAsync(string method, object? parameters, TimeSpan? timeout = null)
    {
        int id = Interlocked.Increment(ref _requestIdCounter);
        JsonRpcRequest req = new()
        {
            JsonRpc = "2.0",
            Id = id,
            Method = method,
            Params = parameters
        };

        string json = JsonSerializer.Serialize(req, JsonOpts);
        string idKey = id.ToString();
        TaskCompletionSource<JsonRpcResponse> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[idKey] = tcs;

        _logger?.Invoke($"[McpProcessClient] --> {json}");
        await WriteLineAsync(json);

        TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromSeconds(15);
        using CancellationTokenSource timeoutCts = new(effectiveTimeout);

        try
        {
            using (timeoutCts.Token.Register(() => tcs.TrySetCanceled()))
            {
                return await tcs.Task;
            }
        }
        catch (TaskCanceledException)
        {
            _pendingRequests.TryRemove(idKey, out _);
            throw new TimeoutException($"JSON-RPC request '{method}' with id={id} timed out after {effectiveTimeout.TotalSeconds}s.");
        }
    }

    public async Task SendNotificationAsync(string method, object? parameters)
    {
        JsonRpcRequest req = new()
        {
            JsonRpc = "2.0",
            Method = method,
            Params = parameters
        };

        string json = JsonSerializer.Serialize(req, JsonOpts);
        _logger?.Invoke($"[McpProcessClient] --> (notification) {json}");
        await WriteLineAsync(json);
    }

    public async Task SendRawLineAsync(string rawLine)
    {
        _logger?.Invoke($"[McpProcessClient] --> RAW: {rawLine}");
        await WriteLineAsync(rawLine);
    }

    public async Task<string> SendRawLineAndReadNextResponseAsync(string rawLine, TimeSpan? timeout = null)
    {
        _logger?.Invoke($"[McpProcessClient] --> RAW: {rawLine}");
        
        TaskCompletionSource<string> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void RawCaptureHandler(string line) => tcs.TrySetResult(line);

        _onRawLineReceived += RawCaptureHandler;
        try
        {
            await WriteLineAsync(rawLine);
            TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromSeconds(10);
            using CancellationTokenSource timeoutCts = new(effectiveTimeout);
            using (timeoutCts.Token.Register(() => tcs.TrySetCanceled()))
            {
                return await tcs.Task;
            }
        }
        finally
        {
            _onRawLineReceived -= RawCaptureHandler;
        }
    }

    private event Action<string>? _onRawLineReceived;

    private async Task WriteLineAsync(string text)
    {
        if (_stdin == null || _process == null || _process.HasExited)
            throw new InvalidOperationException("MCP process is not running or stdin is closed.");

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _stdin.WriteLineAsync(text).ConfigureAwait(false);
            await _stdin.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            try { _writeLock.Release(); } catch (ObjectDisposedException) { }
        }
    }

    private async Task ReadStdoutPumpAsync()
    {
        if (_process == null) return;
        using StreamReader reader = _process.StandardOutput;

        try
        {
            while (!_cts.Token.IsCancellationRequested && !_process.HasExited)
            {
                string? line = await reader.ReadLineAsync(_cts.Token);
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                _logger?.Invoke($"[McpProcessClient] <-- {line}");
                _onRawLineReceived?.Invoke(line);

                try
                {
                    JsonRpcResponse? resp = JsonSerializer.Deserialize<JsonRpcResponse>(line, JsonOpts);
                    if (resp != null && resp.Id != null)
                    {
                        string idKey = resp.Id.ToString() ?? "";
                        if (_pendingRequests.TryRemove(idKey, out TaskCompletionSource<JsonRpcResponse>? tcs))
                        {
                            tcs.TrySetResult(resp);
                        }
                    }
                }
                catch (JsonException)
                {
                    // Non-JSON output on stdout or malformed framing
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.Invoke($"[McpProcessClient] Stdout pump exception: {ex.Message}");
        }
    }

    private async Task ReadStderrPumpAsync()
    {
        if (_process == null) return;
        using StreamReader reader = _process.StandardError;

        try
        {
            while (!_cts.Token.IsCancellationRequested && !_process.HasExited)
            {
                string? line = await reader.ReadLineAsync(_cts.Token);
                if (line == null) break;
                lock (_stderrLogs)
                {
                    _stderrLogs.Add(line);
                }
                _logger?.Invoke($"[McpProcessClient STDERR] {line}");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.Invoke($"[McpProcessClient] Stderr pump exception: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _cts.Cancel();

        try
        {
            if (_stdin != null)
            {
                await _stdin.DisposeAsync();
                _stdin = null;
            }
        }
        catch { }

        try
        {
            if (_process != null && !_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(2000);
                _process.Dispose();
                _process = null;
            }
        }
        catch { }

        _cts.Dispose();
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
