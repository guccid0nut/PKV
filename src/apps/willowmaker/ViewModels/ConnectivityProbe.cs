// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace WILLOWMAKER.Core.ViewModels;

/// <summary>
///     Probes whether an address responds over HTTP via HEAD request, and exposes the outcome as observable state
///     for status messages beneath address inputs. Starting a new probe cancels and supersedes any in-flight probe.
/// </summary>
/// <param name="subject">The name of what is being probed (e.g. "Master Server" or "CDN").</param>
/// <param name="log">Callback to write log messages.</param>
public sealed partial class ConnectivityProbe(
    string subject,
    Action<string> log,
    Func<HttpMessageHandler?>? handlerFactory = null,
    Func<string, string?>? fallbackUrlResolver = null,
    bool disposeCustomHandler = true) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Failed))]
    [NotifyPropertyChangedFor(nameof(StatusColor))]
    public partial bool InProgress { get; set; } = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Failed))]
    [NotifyPropertyChangedFor(nameof(StatusColor))]
    public partial bool Succeeded { get; set; } = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Failed))]
    [NotifyPropertyChangedFor(nameof(StatusColor))]
    public partial string StatusMessage { get; set; } = string.Empty;

    public bool Failed => InProgress is false && Succeeded is false && string.IsNullOrEmpty(StatusMessage) is false;

    public string StatusColor => Succeeded ? "#22c55e" : Failed ? "#ef4444" : "#38bdf8";

    public event Action<ConnectivityProbe, bool>? ProbeCompleted;

    private SynchronizationContext? _uiSyncContext = SynchronizationContext.Current;
    private CancellationTokenSource? _cancellationTokenSource;
    private long _generation;

    /// <summary>
    ///     Cancels any pending or in-flight probe and returns a monotonic generation token so external pre-checks
    ///     (such as async TCP port probes) can detect when a newer selection superseded them before applying status.
    /// </summary>
    public long BeginPendingScope()
    {
        if (SynchronizationContext.Current is { } currentCtx)
            _uiSyncContext = currentCtx;

        Cancel();
        return Volatile.Read(ref _generation);
    }

    public bool IsCurrentGeneration(long expectedGeneration)
        => Volatile.Read(ref _generation) == expectedGeneration;

    private void RunOnUiContext(Action action)
    {
        SynchronizationContext? targetCtx = _uiSyncContext;
        if (targetCtx is not null && SynchronizationContext.Current != targetCtx)
        {
            targetCtx.Post(_ => action(), null);
            return;
        }

        if (Application.Current is not null && !Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(action);
            return;
        }

        action();
    }

    /// <summary>
    ///     Cancels any pending or in-flight probe without clearing an already-settled status message.
    /// </summary>
    public void CancelPending()
    {
        Cancel();
    }

    /// <summary>
    ///     Cancels the probe in progress, if any, and clears the previous result.
    /// </summary>
    public void Reset()
    {
        if (SynchronizationContext.Current is { } currentCtx)
            _uiSyncContext = currentCtx;

        Cancel();
        RunOnUiContext(() =>
        {
            Succeeded = false;
            StatusMessage = string.Empty;
        });
    }

    /// <summary>
    ///     Cancels any probe in progress and marks the endpoint as ready with an informational message
    ///     (used for Offline Mode and Local / Offline Auto when no local server is running).
    /// </summary>
    public void SetReadyStatus(string statusMessage)
    {
        if (SynchronizationContext.Current is { } currentCtx)
            _uiSyncContext = currentCtx;

        Cancel();
        RunOnUiContext(() =>
        {
            InProgress = false;
            Succeeded = true;
            StatusMessage = statusMessage;
        });
    }

    /// <summary>
    ///     Cancels any probe in progress and reports that the address is invalid.
    /// </summary>
    public void ReportInvalidAddress(string? address)
    {
        if (SynchronizationContext.Current is { } currentCtx)
            _uiSyncContext = currentCtx;

        Cancel();
        RunOnUiContext(() =>
        {
            Succeeded = false;
            StatusMessage = string.IsNullOrWhiteSpace(address) ? string.Empty : $"Invalid {subject} Address";
        });
    }

    /// <summary>
    ///     Probes the specified URL with an HTTP HEAD request after the specified debounce delay elapses.
    /// </summary>
    public void Start(string probeURL, TimeSpan delay)
    {
        if (SynchronizationContext.Current is { } currentCtx)
            _uiSyncContext = currentCtx;

        Reset();

        _cancellationTokenSource = new CancellationTokenSource();
        CancellationToken cancellationToken = _cancellationTokenSource.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
                await Probe(probeURL, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }, cancellationToken);
    }

    private void Cancel()
    {
        Interlocked.Increment(ref _generation);
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;
        if (InProgress)
        {
            RunOnUiContext(() => InProgress = false);
        }
    }

    /// <summary>
    ///     Executes a single probe pass directly without debounce scheduling (used by deterministic unit tests).
    /// </summary>
    public Task ProbeAsync(string probeURL, CancellationToken cancellationToken = default)
    {
        if (SynchronizationContext.Current is { } currentCtx)
            _uiSyncContext = currentCtx;

        return Probe(probeURL, cancellationToken);
    }

    private Task Probe(string probeURL, CancellationToken cancellationToken) =>
        ProbeCore(probeURL, allowFallback: true, cancellationToken);

    private async Task ProbeCore(string probeURL, bool allowFallback, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return;

        SetState(inProgress: true, succeeded: false, statusMessage: $"Probing {subject} Connectivity ...", cancellationToken);

        bool succeeded = false;
        string result = string.Empty;
        string? failureDetail = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            HttpMessageHandler? customHandler = handlerFactory?.Invoke();
            using HttpClient client = customHandler is not null
                ? new HttpClient(customHandler, disposeHandler: disposeCustomHandler)
                : ASPIRE.Common.Http.SharedSocketsHttpClientFactory.Create();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("WILLOWMAKER/2.2.0");
            client.Timeout = TimeSpan.FromSeconds(5);

            using HttpRequestMessage request = new(HttpMethod.Head, probeURL);
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed)
            {
                // Fall back to a lightweight GET probe if the endpoint rejects HEAD
                using HttpRequestMessage getRequest = new(HttpMethod.Get, probeURL);
                using HttpResponseMessage getResponse = await client.SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                succeeded = getResponse.IsSuccessStatusCode ||
                            getResponse.StatusCode is System.Net.HttpStatusCode.MethodNotAllowed or System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized;
                result = $"HTTP {(int)getResponse.StatusCode} ({getResponse.StatusCode})";
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                if (Uri.TryCreate(probeURL, UriKind.Absolute, out Uri? parsedUri) &&
                    parsedUri.AbsolutePath.Trim('/').Equals("cdn", StringComparison.OrdinalIgnoreCase))
                {
                    // Static-file / embedded Master Server CDN mounts without directory indexing return 404 on bare '/cdn/'.
                    // Verify the underlying host root responds 2xx so local/static CDNs do not emit false-alarm 404 errors.
                    string rootAuthorityUrl = $"{parsedUri.Scheme}://{parsedUri.Authority}/";
                    using HttpRequestMessage rootReq = new(HttpMethod.Head, rootAuthorityUrl);
                    using HttpResponseMessage rootResp = await client.SendAsync(rootReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    if (rootResp.IsSuccessStatusCode ||
                        rootResp.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized)
                    {
                        succeeded = true;
                        result = $"Host Online (HTTP {(int)rootResp.StatusCode})";
                        log($"INFO: {subject} prefix '{probeURL}' returned 404 (no directory index), verified host root '{rootAuthorityUrl}' -> HTTP {(int)rootResp.StatusCode}.");
                    }
                }

                if (!succeeded && subject.Equals("CDN", StringComparison.OrdinalIgnoreCase))
                {
                    string variant = OperatingSystem.IsWindows() ? "wac"
                        : OperatingSystem.IsLinux() ? "lac"
                        : OperatingSystem.IsMacOS() ? "mac"
                        : "wac";
                    string manifestProbeUrl = $"{(probeURL.EndsWith('/') ? probeURL : probeURL + '/')}{variant}/manifest.json";

                    using HttpRequestMessage manifestReq = new(HttpMethod.Head, manifestProbeUrl);
                    using HttpResponseMessage manifestResp = await client.SendAsync(manifestReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                    HttpResponseMessage activeManifestResp = manifestResp;
                    using HttpResponseMessage? getManifestResp = manifestResp.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed
                        ? await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, manifestProbeUrl), HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                        : null;

                    if (getManifestResp is not null)
                        activeManifestResp = getManifestResp;

                    if (activeManifestResp.IsSuccessStatusCode)
                    {
                        succeeded = true;
                        result = $"Manifest Ready (HTTP {(int)activeManifestResp.StatusCode})";
                        log($"INFO: {subject} root '{probeURL}' returned 404 (no bucket index), verified '{manifestProbeUrl}' -> HTTP {(int)activeManifestResp.StatusCode}.");
                    }
                    else
                    {
                        succeeded = false;
                        result = $"HTTP {(int)response.StatusCode} ({response.StatusCode})";
                    }
                }
                else if (!succeeded)
                {
                    succeeded = false;
                    result = $"HTTP {(int)response.StatusCode} ({response.StatusCode})";
                }
            }
            else
            {
                succeeded = response.IsSuccessStatusCode ||
                            response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized;
                result = $"HTTP {(int)response.StatusCode} ({response.StatusCode})";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            result = DescribeFailure(exception);
            failureDetail = exception.Message;
        }

        if (!succeeded && allowFallback && !cancellationToken.IsCancellationRequested)
        {
            string? fallbackUrl = fallbackUrlResolver?.Invoke(probeURL);
            if (!string.IsNullOrWhiteSpace(fallbackUrl) &&
                !string.Equals(fallbackUrl, probeURL, StringComparison.OrdinalIgnoreCase))
            {
                log($"FALLBACK: {subject} primary '{probeURL}' unreachable ({result}); probing fallback '{fallbackUrl}'...");
                await ProbeCore(fallbackUrl, allowFallback: false, cancellationToken);
                return;
            }
        }

        if (cancellationToken.IsCancellationRequested)
            return;

        SetState(inProgress: false, succeeded: succeeded,
            statusMessage: succeeded ? $"{subject} Is Online: {result}" : $"{subject} Is Unreachable: {result}",
            cancellationToken);

        log($"INIT: Probing {subject} At \"{probeURL}\"");
        log(succeeded
            ? $"INIT: {subject} Probe Succeeded: {result}"
            : $"WARN: {subject} Probe Failed: {result}" + (failureDetail is null ? string.Empty : $" :: {failureDetail}"));

        RunOnUiContext(() =>
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            try
            {
                ProbeCompleted?.Invoke(this, succeeded);
            }
            catch (Exception ex)
            {
                log($"[ConnectivityProbe] ProbeCompleted event handler threw: {ex.Message}");
            }
        });
    }

    private static string DescribeFailure(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: not null } httpEx => $"HTTP {(int)httpEx.StatusCode} ({httpEx.StatusCode})",
        HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } => "Host Not Found",
        HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError } => "Connection Failed",
        HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } => "Secure Connection Failed",
        TaskCanceledException => "Timed Out",
        _ => exception.Message
    };

    private void SetState(bool inProgress, bool succeeded, string statusMessage, CancellationToken cancellationToken)
    {
        void Apply()
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            InProgress = inProgress;
            Succeeded = succeeded;
            StatusMessage = statusMessage;
        }

        RunOnUiContext(Apply);
    }
}
