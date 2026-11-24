// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Extensions;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Events;
using StackExchange.Redis;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using TRANSMUTANSTEIN.ChatServer.Extensions.Protocol;

namespace TRANSMUTANSTEIN.ChatServer.Infrastructure.Services;

public partial class PlayerStatusHydrationSubscriber(
    IConnectionMultiplexer redis,
    ILogger<PlayerStatusHydrationSubscriber> logger,
    IServiceProvider serviceProvider,
    IChatContext chatContext)
    : IHostedService
{
    public const string ChannelName = "ChatServer:HydratePlayerStatus";
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private ISubscriber? _subscriber;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _subscriber = redis.GetSubscriber();
        await _subscriber.SubscribeAsync(RedisChannel.Literal(ChannelName), OnMessageReceived);
        logger.LogInformation("[PlayerStatusHydrationSubscriber] Subscribed to Redis channel: {Channel}", ChannelName);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscriber != null)
        {
            await _subscriber.UnsubscribeAsync(RedisChannel.Literal(ChannelName));
        }
    }

    private void OnMessageReceived(RedisChannel channel, RedisValue message)
    {
        try
        {
            string payloadStr = message.ToString();
            logger.LogInformation("[PlayerStatusHydrationSubscriber] Received Hydration Event: {Payload}", payloadStr);

            using JsonDocument doc = JsonDocument.Parse(payloadStr);
            JsonElement root = doc.RootElement;

            int spectatorAccountId = 0;
            if (root.TryGetProperty("spectator_account_id", out JsonElement spIdProp) ||
                root.TryGetProperty("SpectatorAccountId", out spIdProp))
            {
                spectatorAccountId = spIdProp.GetInt32();
            }

            int starPlayerAccountId = 0;
            if (root.TryGetProperty("star_player_account_id", out JsonElement starIdProp) ||
                root.TryGetProperty("StarPlayerAccountId", out starIdProp))
            {
                starPlayerAccountId = starIdProp.GetInt32();
            }

            int matchId = 0;
            if (root.TryGetProperty("match_id", out JsonElement matchIdProp) ||
                root.TryGetProperty("MatchId", out matchIdProp))
            {
                matchId = matchIdProp.GetInt32();
            }

            string serverAddress = "127.0.0.1:11235";
            if (root.TryGetProperty("server_address", out JsonElement srvProp) && !string.IsNullOrWhiteSpace(srvProp.GetString()))
            {
                serverAddress = srvProp.GetString()!;
            }
            else if (root.TryGetProperty("ServerAddress", out srvProp) && !string.IsNullOrWhiteSpace(srvProp.GetString()))
            {
                serverAddress = srvProp.GetString()!;
            }

            string matchName = $"Match {matchId}";
            if (root.TryGetProperty("match_name", out JsonElement nameProp) && !string.IsNullOrWhiteSpace(nameProp.GetString()))
            {
                matchName = nameProp.GetString()!;
            }
            else if (root.TryGetProperty("MatchName", out nameProp) && !string.IsNullOrWhiteSpace(nameProp.GetString()))
            {
                matchName = nameProp.GetString()!;
            }

            string? starPlayerName = null;
            if (root.TryGetProperty("star_player_name", out JsonElement starNameProp) && !string.IsNullOrWhiteSpace(starNameProp.GetString()))
            {
                starPlayerName = starNameProp.GetString();
            }
            else if (root.TryGetProperty("StarPlayerName", out starNameProp) && !string.IsNullOrWhiteSpace(starNameProp.GetString()))
            {
                starPlayerName = starNameProp.GetString();
            }

            string? spectatorAccountName = null;
            if (root.TryGetProperty("spectator_account_name", out JsonElement specNameProp) && !string.IsNullOrWhiteSpace(specNameProp.GetString()))
            {
                spectatorAccountName = specNameProp.GetString();
            }
            else if (root.TryGetProperty("SpectatorAccountName", out specNameProp) && !string.IsNullOrWhiteSpace(specNameProp.GetString()))
            {
                spectatorAccountName = specNameProp.GetString();
            }

            _ = ProcessHydrationAsync(spectatorAccountId, starPlayerAccountId, matchId, serverAddress, matchName, starPlayerName, spectatorAccountName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[PlayerStatusHydrationSubscriber] Error processing hydration message");
        }
    }

    public async Task ProcessHydrationAsync(
        int spectatorAccountId,
        int starPlayerAccountId,
        int matchId,
        string serverAddress,
        string matchName,
        string? starPlayerName = null,
        string? spectatorAccountName = null)
    {
        // 1. Locate spectator session
        ChatSession? spectatorSession = null;
        if (spectatorAccountId > 0)
        {
            spectatorSession = chatContext.ClientChatSessions.Values.FirstOrDefault(cs =>
                (object?)cs.Account is not null && cs.Account.ID == spectatorAccountId);
        }

        if (spectatorSession == null && !string.IsNullOrWhiteSpace(spectatorAccountName))
        {
            if (chatContext.ClientChatSessions.TryGetValue(spectatorAccountName, out ChatSession? namedSession) &&
                (object?)namedSession.Account is not null)
            {
                spectatorSession = namedSession;
            }
            else
            {
                spectatorSession = chatContext.ClientChatSessions.Values.FirstOrDefault(cs =>
                    (object?)cs.Account is not null &&
                    string.Equals(cs.Account.Name, spectatorAccountName, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (spectatorSession == null)
        {
            logger.LogWarning("[PlayerStatusHydrationSubscriber] Spectator session not found for AccountID {SpectatorId} / Name {SpectatorName}. Client might be offline or using different session.", spectatorAccountId, spectatorAccountName);
            return;
        }

        if (spectatorAccountId == 0 && spectatorSession.Account is not null)
        {
            spectatorAccountId = spectatorSession.Account.ID;
        }

        // 2. Locate star player session if online
        ChatSession? starSession = null;
        if (starPlayerAccountId > 0)
        {
            starSession = chatContext.ClientChatSessions.Values.FirstOrDefault(cs =>
                (object?)cs.Account is not null && cs.Account.ID == starPlayerAccountId);
        }

        if (starSession == null && !string.IsNullOrWhiteSpace(starPlayerName))
        {
            if (chatContext.ClientChatSessions.TryGetValue(starPlayerName, out ChatSession? namedStarSession) &&
                (object?)namedStarSession.Account is not null)
            {
                starSession = namedStarSession;
            }
            else
            {
                starSession = chatContext.ClientChatSessions.Values.FirstOrDefault(cs =>
                    (object?)cs.Account is not null &&
                    string.Equals(cs.Account.Name, starPlayerName, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (starPlayerAccountId == 0 && starSession?.Account is not null)
        {
            starPlayerAccountId = starSession.Account.ID;
        }

        string effectiveServerAddress = !string.IsNullOrWhiteSpace(starSession?.ClientMetadata?.MatchServerAddress)
            ? starSession.ClientMetadata.MatchServerAddress
            : serverAddress;

        string effectiveMatchName = !string.IsNullOrWhiteSpace(starSession?.ClientMetadata?.MatchName)
            ? starSession.ClientMetadata.MatchName
            : matchName;

        int effectiveMatchId = starSession?.ClientMetadata?.MatchID ?? matchId;

        // 3. Resolve star player account details
        Account? starAccount = starSession?.Account;
        if (starAccount == null && _serviceProvider is not null)
        {
            try
            {
                using IServiceScope scope = _serviceProvider.CreateScope();
                IAccountRepository? accountRepo = scope.ServiceProvider.GetService<IAccountRepository>();
                if (accountRepo is not null)
                {
                    if (starPlayerAccountId > 0)
                    {
                        starAccount = await accountRepo.GetByIdAsync(starPlayerAccountId);
                    }
                    else if (!string.IsNullOrWhiteSpace(starPlayerName))
                    {
                        starAccount = await accountRepo.GetByNicknameAsync(starPlayerName);
                        if (starAccount is not null)
                        {
                            starPlayerAccountId = starAccount.ID;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[PlayerStatusHydrationSubscriber] Failed to resolve star account from database for ID {StarId} Name {StarName}", starPlayerAccountId, starPlayerName);
            }
        }

        // 4. Construct and send binary CHAT_CMD_UPDATE_STATUS (0x000C) packet directly to spectator client
        ChatBuffer update = new();
        update.WriteCommand(ChatProtocol.Command.CHAT_CMD_UPDATE_STATUS);
        update.WriteInt32(starPlayerAccountId);
        update.WriteInt8((byte)ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME);
        update.WriteInt8(starAccount?.GetChatClientFlags() ?? 0);
        update.WriteInt32(starAccount?.Clan?.ID ?? 0);
        update.WriteString(starAccount?.Clan?.Name ?? string.Empty);
        update.WriteString(starAccount?.GetChatSymbolNoPrefixCode() ?? string.Empty);
        update.WriteString(starAccount?.GetNameColourNoPrefixCode() ?? string.Empty);
        update.WriteString(starAccount?.GetIconNoPrefixCode() ?? string.Empty);
        update.WriteString(effectiveServerAddress);
        update.WriteString(effectiveMatchName);
        update.WriteInt32(effectiveMatchId);
        update.WriteInt8(0);
        update.WriteInt32(starAccount?.AscensionLevel ?? 0);

        spectatorSession.Send(update);
        logger.LogInformation(
            "[PlayerStatusHydrationSubscriber] Successfully pushed CHAT_CMD_UPDATE_STATUS (0x000C) for StarPlayer ID {StarId} (Server: {ServerAddress}) to Spectator ID {SpectatorId}",
            starPlayerAccountId, effectiveServerAddress, spectatorAccountId);
    }
}
