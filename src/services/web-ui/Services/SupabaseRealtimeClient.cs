// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ASPIRE.Common.Extensions;
using ASPIRE.Common.Hosting;
using ASPIRE.Common.Models.Supabase;

namespace DAWNBRINGER.WebPortal.UI.Services;

/// <summary>
///     Lightweight client for Supabase Realtime (CDC & Broadcast channels).
///     Connects to Supabase WebSocket endpoint to listen for live match and telemetry events.
/// </summary>
public sealed class SupabaseRealtimeClient(
    IConfiguration configuration,
    ILogger<SupabaseRealtimeClient> logger) : ResilientBackgroundService(logger, nameof(SupabaseRealtimeClient))
{
    private ClientWebSocket? _webSocket;
    public bool IsConnected => _webSocket?.State == WebSocketState.Open;

    // ReSharper disable once EventNeverSubscribedTo.Global
    public event Action<string, string>? OnBroadcastReceived;

    protected override async Task ExecuteCoreAsync(CancellationToken stoppingToken)
    {
        SupabaseSettings settings = configuration.GetSupabaseSettings();
        if (string.IsNullOrWhiteSpace(settings.Url) || string.IsNullOrWhiteSpace(settings.AnonKey))
        {
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("[SupabaseRealtime] Realtime disabled: Supabase URL or AnonKey not configured.");

            return;
        }

        string cleanUrl = settings.Url.TrimEnd('/').Replace("https://", "wss://").Replace("http://", "ws://");
        Uri wsUri = new($"{cleanUrl}/realtime/v1/websocket?apikey={settings.AnonKey}&vsn=1.0.0");

        while (!stoppingToken.IsCancellationRequested)
            try
            {
                if (_webSocket == null || _webSocket.State != WebSocketState.Open)
                {
                    try
                    {
                        _webSocket?.Dispose();
                    }
                    catch
                    {
                        // Ignore disposal exception
                    }

                    _webSocket = new ClientWebSocket();
                    if (logger.IsEnabled(LogLevel.Information))
                        logger.LogInformation(
                            "[SupabaseRealtime] Connecting to Supabase Realtime WebSocket at {Uri}...", wsUri);

                    await _webSocket.ConnectAsync(wsUri, stoppingToken);
                    if (logger.IsEnabled(LogLevel.Information))
                        logger.LogInformation("[SupabaseRealtime] Connected to Supabase Realtime.");

                    // Join public broadcast channel for nexus events
                    await JoinChannelAsync("realtime:nexus-live", stoppingToken);
                }

                byte[] buffer = new byte[8192];
                while (_webSocket != null && _webSocket.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
                {
                    WebSocketReceiveResult result =
                        await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), stoppingToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", stoppingToken);
                        break;
                    }

                    string messageJson = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    HandleRealtimeMessage(messageJson);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (logger.IsEnabled(LogLevel.Debug))
                    logger.LogDebug(ex, "[SupabaseRealtime] Connection notice: {Message}. Reconnecting in 10s...",
                        ex.Message);

                await Task.Delay(10000, stoppingToken);
            }
    }

    private async Task JoinChannelAsync(string topic, CancellationToken cancellationToken)
    {
        try
        {
            Dictionary<string, object> joinPayload = new()
            {
                ["topic"] = topic,
                ["event"] = "phx_join",
                ["payload"] = new Dictionary<string, string>(),
                ["ref"] = "1"
            };

            string json = JsonSerializer.Serialize(joinPayload);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            if (_webSocket is { State: WebSocketState.Open })
            {
                await _webSocket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, cancellationToken);
                if (logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation("[SupabaseRealtime] Subscribed to topic '{Topic}'", topic);
            }
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Warning))
                logger.LogWarning(ex, "[SupabaseRealtime] Failed to subscribe to topic '{Topic}'", topic);
        }
    }

    private void HandleRealtimeMessage(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("event", out JsonElement eventElem) &&
                root.TryGetProperty("payload", out JsonElement payloadElem))
            {
                string? eventName = eventElem.GetString();
                string payloadString = payloadElem.GetRawText();

                if (!string.IsNullOrWhiteSpace(eventName)) OnBroadcastReceived?.Invoke(eventName, payloadString);
            }
        }
        catch
        {
            // Ignore malformed socket frames
        }
    }

    public override void Dispose()
    {
        try
        {
            _webSocket?.Dispose();
        }
        catch
        {
            // Ignore disposal exception
        }

        base.Dispose();
    }
}
