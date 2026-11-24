// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net.Security;
using Kongor.Core.Vfs;
using WILLOWMAKER.Core.Services.Serialization;

namespace WILLOWMAKER.Core.Services;

public enum MasterServerStatus
{
    Connecting,
    Active,
    Offline
}

public sealed class LeaderboardPlayerDto
{
    public int Rank { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public double WinRate { get; set; }
    public int MatchesPlayed { get; set; }
    public string RankTier { get; set; } = "Immortal";
}

public sealed class LiveTelemetryData
{
    public int OnlinePlayers { get; set; }
    public int MatchesInProgress { get; set; }
    public MasterServerStatus ServerStatus { get; set; } = MasterServerStatus.Connecting;
    public List<LeaderboardPlayerDto> TopWinratePlayers { get; set; } = [];
}

public sealed class LiveTelemetryService : IDisposable
{
    private static readonly string[] LocalClusterEndpoints =
    [
        "https://localhost:5557/api/telemetry/live",
        "http://localhost:5266/api/telemetry/live",
        "http://127.0.0.1:55555/api/telemetry/live",
        "https://localhost:5556/api/telemetry/live",
        "http://localhost:5200/api/telemetry/live",
        "http://127.0.0.1:5264/api/telemetry/live"
    ];

    private readonly HttpClient _httpClient;
    private readonly Action<string> _logger;
    private readonly string? _policyDirectory;
    private readonly TimeSpan _pollInterval;
    private CancellationTokenSource? _cts;
    private PeriodicTimer? _timer;
    private Task? _pollingTask;
    private long _pollGeneration;

    public Task? ActivePollingTask => _pollingTask;

    public LiveTelemetryService(
        Action<string>? logger = null,
        string? policyDirectory = null,
        HttpMessageHandler? httpHandler = null,
        TimeSpan? pollInterval = null)
    {
        _logger = logger ?? (msg => Trace.WriteLine($"[LiveTelemetry] {msg}"));
        _policyDirectory = policyDirectory;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(5);
        
        HttpMessageHandler handler = httpHandler ?? new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (request, _, _, sslPolicyErrors) =>
                ValidateTlsCertificate(request?.RequestUri, sslPolicyErrors, _policyDirectory)
        };

        _httpClient = new HttpClient(handler, disposeHandler: httpHandler == null)
        {
            Timeout = TimeSpan.FromSeconds(4)
        };
    }

    public LiveTelemetryData CurrentData { get; private set; } = new();

    private string? _activePollingUrl;

    public static bool ValidateTlsCertificate(Uri? requestUri, SslPolicyErrors sslPolicyErrors, string? policyDirectory = null)
    {
        if (sslPolicyErrors == SslPolicyErrors.None)
        {
            return true;
        }

        if (requestUri is not null && requestUri.IsLoopback)
        {
            return true;
        }

        PlaytestPolicyDto policy = PlaytestPolicyService.Resolve(policyDirectory);
        if (policy.EnforceStrictTlsValidation)
        {
            return false;
        }

        return true;
    }

    public static bool ShouldPollLocalClusterEndpoints(string masterServerUrl, string? policyDirectory = null)
    {
        if (ClientModMountSpec.IsLocalhostEndpoint(masterServerUrl))
        {
            return true;
        }

        PlaytestPolicyDto policy = PlaytestPolicyService.Resolve(policyDirectory);
        return !policy.DisableLocalClusterPolling;
    }

    public void Dispose()
    {
        StopPolling();
        _httpClient.Dispose();
    }

    public event Action<LiveTelemetryData>? OnTelemetryUpdated;

    private void Log(string message)
    {
        _logger.Invoke($"[LiveTelemetry] {message}");
    }

    public void StopPolling()
    {
        _activePollingUrl = null;
        var oldCts = Interlocked.Exchange(ref _cts, null);
        if (oldCts != null)
        {
            try
            {
                oldCts.Cancel();
            }
            catch (ObjectDisposedException) { }
            oldCts.Dispose();
        }
        var oldTimer = Interlocked.Exchange(ref _timer, null);
        oldTimer?.Dispose();
    }

    public void StartPolling(string masterServerUrl)
    {
        if (string.Equals(_activePollingUrl, masterServerUrl, StringComparison.OrdinalIgnoreCase) && _timer != null && _cts != null && !_cts.IsCancellationRequested)
        {
            return;
        }

        _activePollingUrl = masterServerUrl;

        var newCts = new CancellationTokenSource();
        var oldCts = Interlocked.Exchange(ref _cts, newCts);
        if (oldCts != null)
        {
            try
            {
                oldCts.Cancel();
            }
            catch (ObjectDisposedException) { }
            oldCts.Dispose();
        }

        var newTimer = new PeriodicTimer(_pollInterval);
        var oldTimer = Interlocked.Exchange(ref _timer, newTimer);
        oldTimer?.Dispose();

        long pollGen = Interlocked.Increment(ref _pollGeneration);
        CancellationToken token = newCts.Token;

        _pollingTask = Task.Run(async () =>
        {
            try
            {
                await FetchTelemetryAsync(masterServerUrl, pollGen, token).ConfigureAwait(false);
                while (!token.IsCancellationRequested && await newTimer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    if (token.IsCancellationRequested || Volatile.Read(ref _pollGeneration) != pollGen)
                    {
                        break;
                    }
                    await FetchTelemetryAsync(masterServerUrl, pollGen, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Clean polling task cancellation
            }
            catch (ObjectDisposedException)
            {
                // Timer disposed
            }
            catch (Exception ex)
            {
                Log($"Unexpected polling loop exception ({masterServerUrl}): {ex.Message}");
            }
            finally
            {
                newTimer.Dispose();
            }
        }, token);
    }

    public Task FetchTelemetryAsync(string masterServerUrl, CancellationToken cancellationToken = default)
    {
        return FetchTelemetryAsync(masterServerUrl, 0, cancellationToken);
    }

    private async Task FetchTelemetryAsync(string masterServerUrl, long pollGen, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested || (pollGen > 0 && Volatile.Read(ref _pollGeneration) != pollGen))
        {
            return;
        }

        try
        {
            string cleanUrl = masterServerUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? masterServerUrl
                : $"http://{masterServerUrl}";

            cleanUrl = cleanUrl.Replace("localhost:", "127.0.0.1:");

            string endpoint = $"{cleanUrl.TrimEnd('/')}/api/telemetry/live";

            // 1. Try to query rich live telemetry API on the selected master server
            try
            {
                using HttpResponseMessage response = await _httpClient.GetAsync(endpoint, cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested || (pollGen > 0 && Volatile.Read(ref _pollGeneration) != pollGen))
                {
                    return;
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    Trace.WriteLine(
                        $"[LiveTelemetry] Rate-limited (429 TooManyRequests) by primary endpoint ({endpoint}); backing off until next poll tick without cascading to LocalClusterEndpoints.");
                    return;
                }

                if (response.IsSuccessStatusCode)
                {
                    string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    if (cancellationToken.IsCancellationRequested || (pollGen > 0 && Volatile.Read(ref _pollGeneration) != pollGen))
                    {
                        return;
                    }

                    LiveTelemetryData? data = JsonSerializer.Deserialize(json, WillowmakerJsonSerializerContext.Default.LiveTelemetryData);
                    if (data != null)
                    {
                        bool isChanged = CurrentData.OnlinePlayers != data.OnlinePlayers ||
                                         CurrentData.MatchesInProgress != data.MatchesInProgress ||
                                         CurrentData.ServerStatus != MasterServerStatus.Active;

                        LiveTelemetryData snapshot = new()
                        {
                            OnlinePlayers = data.OnlinePlayers,
                            MatchesInProgress = data.MatchesInProgress,
                            ServerStatus = MasterServerStatus.Active,
                            TopWinratePlayers = data.TopWinratePlayers != null ? new List<LeaderboardPlayerDto>(data.TopWinratePlayers) : []
                        };

                        CurrentData = snapshot;
                        OnTelemetryUpdated?.Invoke(snapshot);
                        if (isChanged)
                            Log(
                                $"Updated live telemetry from target: {snapshot.OnlinePlayers} online, {snapshot.MatchesInProgress} matches, status Active.");
                        return;
                    }
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                Trace.WriteLine($"[LiveTelemetry] Optional telemetry endpoint unavailable ({endpoint}): {ex.Message}");
            }

            if (cancellationToken.IsCancellationRequested || (pollGen > 0 && Volatile.Read(ref _pollGeneration) != pollGen))
            {
                return;
            }

            // 2. Query local cluster telemetry endpoints (skipped for remote servers in Playtest mode)
            if (ShouldPollLocalClusterEndpoints(masterServerUrl, _policyDirectory))
            {
                LiveTelemetryData? localClusterData = null;
                foreach (string localEndpoint in LocalClusterEndpoints)
                {
                    if (cancellationToken.IsCancellationRequested || (pollGen > 0 && Volatile.Read(ref _pollGeneration) != pollGen))
                    {
                        return;
                    }

                    try
                    {
                        using HttpResponseMessage localResp = await _httpClient.GetAsync(localEndpoint, cancellationToken).ConfigureAwait(false);
                        if (cancellationToken.IsCancellationRequested || (pollGen > 0 && Volatile.Read(ref _pollGeneration) != pollGen))
                        {
                            return;
                        }

                        if (localResp.IsSuccessStatusCode)
                        {
                            string localJson = await localResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                            if (cancellationToken.IsCancellationRequested || (pollGen > 0 && Volatile.Read(ref _pollGeneration) != pollGen))
                            {
                                return;
                            }

                            LiveTelemetryData? parsed = JsonSerializer.Deserialize(localJson, WillowmakerJsonSerializerContext.Default.LiveTelemetryData);
                            if (parsed != null)
                            {
                                localClusterData = new LiveTelemetryData
                                {
                                    OnlinePlayers = parsed.OnlinePlayers,
                                    MatchesInProgress = parsed.MatchesInProgress,
                                    ServerStatus = MasterServerStatus.Active,
                                    TopWinratePlayers = parsed.TopWinratePlayers != null ? new List<LeaderboardPlayerDto>(parsed.TopWinratePlayers) : []
                                };
                                break;
                            }
                        }
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        Trace.WriteLine($"[LiveTelemetry] Local cluster port unreachable ({localEndpoint}): {ex.Message}");
                    }
                }

                if (cancellationToken.IsCancellationRequested || (pollGen > 0 && Volatile.Read(ref _pollGeneration) != pollGen))
                {
                    return;
                }

                if (localClusterData != null)
                {
                    bool isChanged = CurrentData.OnlinePlayers != localClusterData.OnlinePlayers ||
                                     CurrentData.MatchesInProgress != localClusterData.MatchesInProgress ||
                                     CurrentData.ServerStatus != MasterServerStatus.Active;

                    CurrentData = localClusterData;
                    OnTelemetryUpdated?.Invoke(localClusterData);
                    if (isChanged)
                        Log(
                            $"Synced live ecosystem telemetry from local cluster: {localClusterData.OnlinePlayers} online, {localClusterData.MatchesInProgress} matches.");
                    return;
                }
            }

            if (cancellationToken.IsCancellationRequested || (pollGen > 0 && Volatile.Read(ref _pollGeneration) != pollGen))
            {
                return;
            }

            // 3. Fallback probe: Probe standard patcher endpoint to check master server vitality
            try
            {
                string probeEndpoint = $"{cleanUrl.TrimEnd('/')}/patcher/patcher.php";
                using HttpResponseMessage probeResponse = await _httpClient.GetAsync(probeEndpoint, cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested || (pollGen > 0 && Volatile.Read(ref _pollGeneration) != pollGen))
                {
                    return;
                }

                if ((int)probeResponse.StatusCode < 500 || probeResponse.StatusCode == HttpStatusCode.MethodNotAllowed)
                {
                    bool wasNotActive = CurrentData.ServerStatus != MasterServerStatus.Active;
                    LiveTelemetryData activeSnapshot = new()
                    {
                        OnlinePlayers = 0,
                        MatchesInProgress = 0,
                        ServerStatus = MasterServerStatus.Active,
                        TopWinratePlayers = []
                    };
                    CurrentData = activeSnapshot;
                    OnTelemetryUpdated?.Invoke(activeSnapshot);
                    if (wasNotActive)
                        Log(
                            $"Master server vitality confirmed ({probeResponse.StatusCode}): {masterServerUrl} is Active.");
                    return;
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                Trace.WriteLine($"[LiveTelemetry] Master server vitality probe failed ({masterServerUrl}): {ex.Message}");
            }

            if (cancellationToken.IsCancellationRequested || (pollGen > 0 && Volatile.Read(ref _pollGeneration) != pollGen))
            {
                return;
            }

            LiveTelemetryData offlineSnapshot = new()
            {
                ServerStatus = MasterServerStatus.Offline,
                OnlinePlayers = 0,
                MatchesInProgress = 0,
                TopWinratePlayers = []
            };
            CurrentData = offlineSnapshot;
            OnTelemetryUpdated?.Invoke(offlineSnapshot);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            if (pollGen > 0 && Volatile.Read(ref _pollGeneration) != pollGen)
            {
                return;
            }

            Log($"Could not reach master server ({masterServerUrl}): {ex.Message}");
            LiveTelemetryData offlineSnapshot = new()
            {
                ServerStatus = MasterServerStatus.Offline,
                OnlinePlayers = 0,
                MatchesInProgress = 0,
                TopWinratePlayers = []
            };
            CurrentData = offlineSnapshot;
            OnTelemetryUpdated?.Invoke(offlineSnapshot);
        }
    }
}
