// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using TRANSMUTANSTEIN.ChatServer.Logging;
using Log = TRANSMUTANSTEIN.ChatServer.Utilities.Log;

namespace TRANSMUTANSTEIN.ChatServer.Services;

public class ChatService(IServiceProvider serviceProvider) : IHostedService, IDisposable
{
    public Domain.Core.ChatServer? ChatServer { get; set; }

    public void Dispose()
    {
        if (ChatServer is null)
        {
            Log.Debug(
                "Chat Server Is NULL During Disposal (Attempting To Dispose Before Start or After Startup Failure?)");
            return;
        }

        if (ChatServer.IsDisposed) return;

        ChatServer.Dispose();
        GC.SuppressFinalize(this);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Log.Initialise(serviceProvider.GetRequiredService<ILogger<Log>>());

        IConfiguration configuration = serviceProvider.GetRequiredService<IConfiguration>();

        IPAddress clientBindAddress = ResolveBindAddress(
            configuration["CHAT_SERVER_CLIENT_BIND_ADDRESS"],
            "CHAT_SERVER_CLIENT_BIND_ADDRESS");
        IPAddress internalBindAddress = ResolveBindAddress(
            configuration["CHAT_SERVER_INTERNAL_BIND_ADDRESS"],
            "CHAT_SERVER_INTERNAL_BIND_ADDRESS");

        bool isTest = configuration.GetValue<bool>("IS_TEST_ENVIRONMENT");

        int clientConnectionsPort = configuration.GetValue<int>("CHAT_SERVER_PORT_CLIENT");
        if (clientConnectionsPort == 0)
            clientConnectionsPort = configuration.GetValue<int>("CHAT_SERVER_PORT_CLIENT_LOCAL");

        if (clientConnectionsPort == 0) clientConnectionsPort = configuration.GetValue<int>("ChatServer:ClientPort");

        if (clientConnectionsPort == 0 && !isTest) clientConnectionsPort = 11031;

        int matchServerConnectionsPort = configuration.GetValue<int>("CHAT_SERVER_PORT_MATCH_SERVER");
        if (matchServerConnectionsPort == 0)
            matchServerConnectionsPort = configuration.GetValue<int>("ChatServer:MatchServerPort");

        if (matchServerConnectionsPort == 0 && !isTest) matchServerConnectionsPort = 11032;

        int matchServerManagerConnectionsPort = configuration.GetValue<int>("CHAT_SERVER_PORT_MATCH_SERVER_MANAGER");
        if (matchServerManagerConnectionsPort == 0)
            matchServerManagerConnectionsPort = configuration.GetValue<int>("ChatServer:MatchServerManagerPort");

        if (matchServerManagerConnectionsPort == 0 && !isTest) matchServerManagerConnectionsPort = 11033;

        ChatServer = new Domain.Core.ChatServer(serviceProvider, clientBindAddress, internalBindAddress,
            clientConnectionsPort, matchServerConnectionsPort, matchServerManagerConnectionsPort);

        Log.Information(
            $"[Standalone] chat bind client={clientBindAddress}:{clientConnectionsPort} internal={internalBindAddress}:{matchServerConnectionsPort},{matchServerManagerConnectionsPort}");

        ChatServer.Start();

        ITelemetryPublisherService? telemetryPublisher = serviceProvider.GetService<ITelemetryPublisherService>();
        if (telemetryPublisher != null) _ = telemetryPublisher.ResetBaselineOnStartupAsync();

        return Task.CompletedTask;
    }

    internal static IPAddress ResolveBindAddress(string? rawAddress, string keyName)
    {
        if (string.IsNullOrWhiteSpace(rawAddress))
        {
            return IPAddress.Any;
        }

        if (IPAddress.TryParse(rawAddress, out IPAddress? parsedAddress))
        {
            return parsedAddress;
        }

        try
        {
            Log.Error(
                $"[Standalone] Invalid bind address for {keyName}: '{rawAddress}'. Falling back to {IPAddress.Any}.");
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(
                $"[Standalone] Invalid bind address for {keyName}: '{rawAddress}' (logger uninitialized: {ex.Message}). Falling back to {IPAddress.Any}.");
        }

        return IPAddress.Any;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (ChatServer is null)
        {
            ILogger<ChatService> logger = serviceProvider.GetRequiredService<ILogger<ChatService>>();
            logger.LogChatServerNullDuringStop();

            return Task.FromException(new ApplicationException("Chat Server Is NULL"));
        }

        if (ChatServer.IsStarted)
        {
            ChatServer.DisconnectAll();
            ChatServer.Stop();
        }

        else
        {
            Log.Debug("Chat Server is not running or already stopped.");
            return Task.CompletedTask;
        }

        Log.Information("Chat Server Has Stopped");

        return Task.CompletedTask;
    }
}
