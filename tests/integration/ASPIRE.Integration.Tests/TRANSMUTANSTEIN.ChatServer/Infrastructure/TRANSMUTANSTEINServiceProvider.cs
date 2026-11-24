// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using ASPIRE.Tests.InProcess;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;
using TRANSMUTANSTEIN.ChatServer.Internals;
using TRANSMUTANSTEIN.ChatServer.Services;
using IDatabase = StackExchange.Redis.IDatabase;

namespace ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Infrastructure;

public class TRANSMUTANSTEINServiceProvider : WebApplicationFactory<global::TRANSMUTANSTEIN.ChatServer.TRANSMUTANSTEIN>
{
    private readonly InMemoryDatabaseRoot _databaseRoot = new();
    private readonly string _databaseName = $"InMemoryMerrickDbForChat_{Guid.NewGuid():N}";
    private static readonly string CachedContentRoot;

    static TRANSMUTANSTEINServiceProvider()
    {
        string contentRoot = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(contentRoot) && !File.Exists(Path.Combine(contentRoot, "ProjectKongor.slnxx")) && !File.Exists(Path.Combine(contentRoot, "ProjectKongor.slnx")))
        {
            string? parent = Directory.GetParent(contentRoot)?.FullName;
            if (parent == null || parent == contentRoot) break;

            contentRoot = parent;
        }

        CachedContentRoot = Path.Combine(contentRoot, "src", "services", "chat-server");
    }

    public TRANSMUTANSTEINServiceProvider()
    {
        Context.ClientChatSessions.Clear();
        Context.MatchServerChatSessions.Clear();
        Context.MatchServerManagerChatSessions.Clear();
        Context.ChatChannels.Clear();
    }

    public int ClientPort { get; set; } = 0;
    public int MatchmakingPlayersPerTeam { get; set; } = 5;
    public bool AllowTwoPlayerFallback { get; set; } = false;
    public bool EnableReadyCheck { get; set; } = false;

    public int MatchServerPort { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        string contentRoot = CachedContentRoot;
        builder.UseContentRoot(contentRoot);

        builder.ConfigureAppConfiguration((_, config) =>
        {
            Dictionary<string, string?> settings = new()
            {
                { "IS_TEST_ENVIRONMENT", "true" },
                { "CHAT_SERVER_PORT_CLIENT", ClientPort.ToString(CultureInfo.InvariantCulture) },
                { "ChatServer:ClientPort", ClientPort.ToString(CultureInfo.InvariantCulture) },
                {
                    "CHAT_SERVER_PORT_MATCH_SERVER",
                    (ClientPort == 0 ? 0 : ClientPort + 100).ToString(CultureInfo.InvariantCulture)
                },
                {
                    "ChatServer:MatchServerPort",
                    (ClientPort == 0 ? 0 : ClientPort + 100).ToString(CultureInfo.InvariantCulture)
                },
                {
                    "CHAT_SERVER_PORT_MATCH_SERVER_MANAGER",
                    (ClientPort == 0 ? 0 : ClientPort + 200).ToString(CultureInfo.InvariantCulture)
                },
                {
                    "ChatServer:MatchServerManagerPort",
                    (ClientPort == 0 ? 0 : ClientPort + 200).ToString(CultureInfo.InvariantCulture)
                },
                // Use localhost for infrastructure to avoid DNS issues in tests
                { "INFRASTRUCTURE_GATEWAY", "localhost" },
                { "Matchmaking:EnableReadyCheck", EnableReadyCheck.ToString() },
                { "Matchmaking:AllowTwoPlayerFallback", AllowTwoPlayerFallback.ToString() },
                { "Matchmaking:PlayersPerTeam", MatchmakingPlayersPerTeam.ToString() },
                { "Matchmaking:IntervalInMilliseconds", "50" }
            };

            config.AddInMemoryCollection(settings);
        });

        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.SetMinimumLevel(LogLevel.Warning);
        });

        builder.ConfigureTestServices(services =>
        {
            // Remove real database context & MerrickContext registrations
            bool databaseContextPredicate(ServiceDescriptor descriptor)
            {
                return descriptor.ServiceType == typeof(MerrickContext) ||
                       descriptor.ServiceType == typeof(DbContextOptions<MerrickContext>) ||
                       descriptor.ServiceType == typeof(DbContextOptions) ||
                       descriptor.ServiceType.FullName?.Contains(nameof(MerrickContext)) is true ||
                       descriptor.ImplementationType?.FullName?.Contains(nameof(MerrickContext)) is true;
            }

            foreach (ServiceDescriptor descriptor in services.Where(databaseContextPredicate).ToList())
                services.Remove(descriptor);

            // Replace FloodPreventionService with NullFloodPreventionService to prevent test bans
            services.RemoveAll<FloodPreventionService>();
            services.AddSingleton<FloodPreventionService, NullFloodPreventionService>();

            string dbName = _databaseName;
            services.AddDbContext<MerrickContext>(options =>
            {
                options.UseModel(TestDbContextFactory.SharedModel);
                options.UseInMemoryDatabase(dbName, _databaseRoot);
            });

            // Remove real Redis
            services.RemoveAll<IConnectionMultiplexer>();
            services.RemoveAll<IDatabase>();

            // Register Mock Redis (using Moq to avoid massive interface stubbing)
            Mock<IConnectionMultiplexer> mockMuxer = new();
            Mock<ISubscriber> mockSubscriber = new();
            InProcessDistributedCacheStore inProcessDb = new();

            mockMuxer.Setup(m => m.IsConnected).Returns(true);
            mockMuxer.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(inProcessDb);
            mockMuxer.Setup(m => m.GetSubscriber(It.IsAny<object>())).Returns(mockSubscriber.Object);
            mockMuxer.Setup(m => m.ClientName).Returns("MockRedis");
            mockMuxer.Setup(m => m.ToString()).Returns("MockConnectionMultiplexer");

            // Ensure Subscriber returns connected state for checks
            mockSubscriber.Setup(s => s.IsConnected(It.IsAny<RedisChannel>())).Returns(true);
            mockSubscriber.Setup(s => s.Multiplexer).Returns(mockMuxer.Object);

            services.AddSingleton(mockMuxer.Object);
            services.AddSingleton<IDatabase>(inProcessDb);

            // Fast-path Health Checks for test runner (eliminate 15s remote probe timeout)
            services.PostConfigure<HealthCheckServiceOptions>(options =>
            {
                List<HealthCheckRegistration> toRemove =
                [
                    .. options.Registrations
                        .Where(r => r.Name.Contains("redis", StringComparison.OrdinalIgnoreCase) ||
                                    r.Name.Contains("npgsql", StringComparison.OrdinalIgnoreCase) ||
                                    r.Name.Contains("sqlserver", StringComparison.OrdinalIgnoreCase) ||
                                    r.Name.Contains("Database", StringComparison.OrdinalIgnoreCase))
                ];
                foreach (HealthCheckRegistration reg in toRemove) options.Registrations.Remove(reg);
            });
        });
    }

    public static async Task<TRANSMUTANSTEINServiceProvider> CreateOrchestratedInstanceAsync(int clientPort = 0,
        int playersPerTeam = 5, bool allowTwoPlayerFallback = false)
    {
        // If clientPort is 0, we use it as-is to signal dynamic port allocation.
        // Otherwise, we use the provided port.
        TRANSMUTANSTEINServiceProvider provider = new()
        {
            ClientPort = clientPort,
            MatchmakingPlayersPerTeam = playersPerTeam,
            AllowTwoPlayerFallback = allowTwoPlayerFallback
        };

        // Reset static context to prevent test pollution
        Context.ClientChatSessions.Clear();
        Context.MatchServerChatSessions.Clear();
        Context.MatchServerManagerChatSessions.Clear();
        Context.ChatChannels.Clear();

        // This forces the server to start
        provider.CreateClient();

        IChatContext chatContext = provider.Services.GetRequiredService<IChatContext>();
        chatContext.ClientChatSessions.Clear();
        chatContext.MatchServerChatSessions.Clear();
        chatContext.MatchServerManagerChatSessions.Clear();
        chatContext.ChatChannels.Clear();

        // If we requested a dynamic port (0), we need to find out which port was actually assigned.
        if (clientPort == 0)
        {
            // Resolve ChatService to access the ChatServer and its ClientServer endpoint
            ChatService chatService = provider.Services.GetRequiredService<ChatService>();

            // We need to wait until the server is started and bound to a non-zero port
            int attempts = 0;
            while ((chatService.ChatServer is not { IsStarted: true } ||
                    chatService.ChatServer.ClientServer.Endpoint is not IPEndPoint ep || ep.Port == 0 ||
                    chatService.ChatServer.MatchServer.Endpoint is not IPEndPoint matchEp || matchEp.Port == 0) &&
                   attempts < 100)
            {
                await Task.Delay(10);
                attempts++;
            }

            if (chatService.ChatServer is not { IsStarted: true })
                throw new Exception("Chat Server failed to start or ChatService.ChatServer is null.");

            // Retrieve the bound port
            if (chatService.ChatServer.ClientServer.Endpoint is IPEndPoint ipEndpoint)
            {
                provider.ClientPort = ipEndpoint.Port;
                clientPort = provider.ClientPort; // Update local variable for the probe check
            }
            else
            {
                throw new Exception("Could not determine bound port from ClientServer Endpoint.");
            }

            // Retrieve MatchServer Port
            if (chatService.ChatServer.MatchServer.Endpoint is IPEndPoint matchEndpoint)
                provider.MatchServerPort = matchEndpoint.Port;
            else
                throw new Exception("Could not determine bound port from MatchServer Endpoint.");
        }
        else
        {
            // Static Port Calculation
            provider.MatchServerPort = clientPort + 100;
        }

        // Wait for the TCP listeners to spin up using fast 10ms polling
        int attempt = 0;
        const int maxAttempts = 100;
        const int delayMs = 10;

        while (attempt < maxAttempts)
            try
            {
                using TcpClient probe = new();
                await probe.ConnectAsync("127.0.0.1", clientPort);
                if (probe.Connected) break;
            }
            catch
            {
                attempt++;
                if (attempt == maxAttempts)
                    throw new Exception(
                        $"Failed to connect to Chat Server on port {clientPort} after {maxAttempts} attempts.");

                await Task.Delay(delayMs);
            }

        return provider;
    }

    protected override void Dispose(bool disposing)
    {
        Context.ClientChatSessions.Clear();
        Context.MatchServerChatSessions.Clear();
        Context.MatchServerManagerChatSessions.Clear();
        Context.ChatChannels.Clear();
        base.Dispose(disposing);
    }
}

