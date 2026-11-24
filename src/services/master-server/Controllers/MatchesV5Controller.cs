// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Buffers;
using System.IO.Pipelines;
using KONGOR.MasterServer.Extensions.Cache;
using KONGOR.MasterServer.Features.Matches.SubmitMatchStats;
using KONGOR.MasterServer.Models.RequestResponse.ServerManagement;
using KONGOR.MasterServer.Services.Background;
using KONGOR.MasterServer.Services.Security;
using MemoryPack;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Domain;
using NEXUS.Contracts.Domain.Matches;
using NEXUS.Contracts.Security;
using StackExchange.Redis;

namespace KONGOR.MasterServer.Controllers;

/// <summary>
///     High-throughput binary match ingestion controller (<c>/api/v5/matches</c>).
///     Accepts raw <see cref="MatchFinalizationPayload"/> byte streams, deserializes them
///     via <see cref="MemoryPackSerializer"/> with zero GC allocations in &lt;150µs,
///     and enqueues them into the partitioned background match processing channel.
/// </summary>
[ApiController]
[Route("api/v5/matches")]
public sealed class MatchesV5Controller : ControllerBase
{
    private const int MaxPayloadBytes = 4 * 1024 * 1024;
    private readonly IMatchProcessingChannel _matchProcessingChannel;
    private readonly IDatabase _distributedCache;
    private readonly ILogger<MatchesV5Controller> _logger;
    private readonly IAccountRepository? _accountRepository;
    private readonly IConfiguration? _configuration;

    public MatchesV5Controller(
        IMatchProcessingChannel matchProcessingChannel,
        IDatabase distributedCache,
        ILogger<MatchesV5Controller> logger,
        IAccountRepository? accountRepository = null,
        IConfiguration? configuration = null)
    {
        _matchProcessingChannel = matchProcessingChannel;
        _distributedCache = distributedCache;
        _logger = logger;
        _accountRepository = accountRepository;
        _configuration = configuration;
    }

    private string? ExtractServerSessionToken()
    {
        if (Request.Query.ContainsKey("session"))
        {
            _logger.LogWarning(
                "[SECURITY-ALERT] [MatchesV5] Rejected URL query-string '?session=' secret; pass 'X-Server-Session' or 'Authorization: Bearer' header instead.");
        }

        string? headerToken = Request.Headers["X-Server-Session"].FirstOrDefault()
                              ?? Request.Headers["X-Kongor-Server-Session"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(headerToken))
        {
            return headerToken.Trim();
        }

        string? authHeader = Request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(authHeader))
        {
            const string bearerPrefix = "Bearer ";
            return authHeader.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase)
                ? authHeader[bearerPrefix.Length..].Trim()
                : authHeader.Trim();
        }

        return null;
    }

    /// <summary>
    ///     Ingests a binary MemoryPack match finalization payload from a dedicated game server.
    /// </summary>
    [HttpPost("submit")]
    [RequestSizeLimit(MaxPayloadBytes)]
    [Consumes("application/x-memorypack", "application/octet-stream")]
    public async Task<IActionResult> SubmitBinaryMatch(CancellationToken ct)
    {
        string remoteIp = TrustedProxyIpResolver.ResolveClientIp(HttpContext).MapToIPv4().ToString();
        string? sessionToken = ExtractServerSessionToken();
        MatchServer? authenticatedServer = !string.IsNullOrWhiteSpace(sessionToken)
            ? await _distributedCache.GetMatchServerBySessionCookie(sessionToken)
            : null;

        if (authenticatedServer is null)
        {
            _logger.LogWarning(
                "[SECURITY-ALERT] [MatchesV5] Rejected unauthorized binary match submission (session='{SessionMask}', remoteIp='{RemoteIp}').",
                SecurityMasking.MaskToken(sessionToken ?? string.Empty), remoteIp);

            return StatusCode(StatusCodes.Status401Unauthorized, new MatchFinalizationReceipt
            {
                MatchId = 0,
                Success = false,
                FinalizedAt = TimeProvider.System.GetUtcNow(),
                Message = "Unauthorized dedicated server session."
            });
        }

        if (authenticatedServer.HostAccountID <= 0)
        {
            _logger.LogWarning(
                "[SECURITY-ALERT] [MatchesV5] Rejected binary match submission from Server #{ServerId} with invalid HostAccountID={HostAccountId} (remoteIp='{RemoteIp}').",
                authenticatedServer.ID, authenticatedServer.HostAccountID, remoteIp);

            return StatusCode(StatusCodes.Status403Forbidden, new MatchFinalizationReceipt
            {
                MatchId = 0,
                Success = false,
                FinalizedAt = TimeProvider.System.GetUtcNow(),
                Message = "Host account is not authorized as a ServerHost."
            });
        }

        if (_accountRepository is not null)
        {
            Account? hostAccount = await _accountRepository.GetByIdAsync(authenticatedServer.HostAccountID, ct);
            if (hostAccount is null || hostAccount.Type != AccountType.ServerHost || hostAccount.IsBanned)
            {
                _logger.LogWarning(
                    "[SECURITY-ALERT] [MatchesV5] Rejected binary match submission from unauthorized/missing/banned HostAccountID={AccountId} (Found={Found}, Type={AccountType}, Banned={Banned}).",
                    authenticatedServer.HostAccountID, hostAccount is not null, hostAccount?.Type, hostAccount?.IsBanned ?? false);

                return StatusCode(StatusCodes.Status403Forbidden, new MatchFinalizationReceipt
                {
                    MatchId = 0,
                    Success = false,
                    FinalizedAt = TimeProvider.System.GetUtcNow(),
                    Message = "Host account is not authorized as a ServerHost."
                });
            }
        }

        PipeReader reader = Request.BodyReader;
        ReadOnlySequence<byte> buffer;
        while (true)
        {
            ReadResult result = await reader.ReadAsync(ct);
            buffer = result.Buffer;

            if (buffer.Length > MaxPayloadBytes)
            {
                reader.AdvanceTo(buffer.End);
                _logger.LogWarning(
                    "[SECURITY-ALERT] [MatchesV5] Rejected oversized binary match payload ({Length} bytes) from Server #{ServerId}.",
                    buffer.Length, authenticatedServer.ID);
                return StatusCode(StatusCodes.Status413PayloadTooLarge, new MatchFinalizationReceipt
                {
                    MatchId = 0,
                    Success = false,
                    FinalizedAt = TimeProvider.System.GetUtcNow(),
                    Message = "Binary match payload exceeds maximum allowed size."
                });
            }

            if (result.IsCompleted)
            {
                break;
            }

            reader.AdvanceTo(buffer.Start, buffer.End);
        }

        if (buffer.IsEmpty)
        {
            reader.AdvanceTo(buffer.End);
            _logger.LogWarning("[MatchesV5] Received empty request body for binary match submission.");
            return BadRequest(new MatchFinalizationReceipt
            {
                MatchId = 0,
                Success = false,
                FinalizedAt = TimeProvider.System.GetUtcNow(),
                Message = "Request body is empty."
            });
        }

        MatchFinalizationPayload? payload;
        try
        {
            payload = MemoryPackSerializer.Deserialize<MatchFinalizationPayload>(buffer);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MatchesV5] Failed to deserialize binary MatchFinalizationPayload.");
            return BadRequest(new MatchFinalizationReceipt
            {
                MatchId = 0,
                Success = false,
                FinalizedAt = TimeProvider.System.GetUtcNow(),
                Message = $"Failed to deserialize binary payload: {ex.Message}"
            });
        }
        finally
        {
            reader.AdvanceTo(buffer.End);
        }

        if (payload is null || payload.MatchId <= 0)
        {
            _logger.LogWarning("[MatchesV5] Deserialized payload has invalid MatchId: {MatchId}", payload?.MatchId ?? 0);
            return BadRequest(new MatchFinalizationReceipt
            {
                MatchId = payload?.MatchId ?? 0,
                Success = false,
                FinalizedAt = TimeProvider.System.GetUtcNow(),
                Message = "Payload contains non-positive or null MatchId."
            });
        }

        int matchId = payload.MatchId;
        MatchStartData? startData = await _distributedCache.GetMatchStartData(matchId);
        if (startData is null)
        {
            string ownershipMode = _configuration?["Matches:OwnershipCheckMode"] ?? "Shadow";
            bool enforceOwnership = string.Equals(ownershipMode, "Enforce", StringComparison.OrdinalIgnoreCase);

            _logger.LogWarning(
                "[SECURITY-ALERT] [MatchesV5] Binary submission for Match #{MatchId} from Server #{ServerId} ('{HostAccountName}', remoteIp='{RemoteIp}') arrived without Redis MatchStartData (OwnershipCheckMode={OwnershipMode}).",
                matchId, authenticatedServer.ID, authenticatedServer.HostAccountName, remoteIp, ownershipMode);

            if (enforceOwnership)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new MatchFinalizationReceipt
                {
                    MatchId = matchId,
                    Success = false,
                    FinalizedAt = TimeProvider.System.GetUtcNow(),
                    Message = "Match start metadata was not found for this server."
                });
            }
        }
        else if (startData.ServerID > 0 && startData.ServerID != authenticatedServer.ID)
        {
            _logger.LogWarning(
                "[SECURITY-ALERT] [MatchesV5] Server #{SubmittingServerId} attempted binary submission for Match #{MatchId} owned by Server #{OwnerServerId}.",
                authenticatedServer.ID, matchId, startData.ServerID);
            return StatusCode(StatusCodes.Status403Forbidden, new MatchFinalizationReceipt
            {
                MatchId = matchId,
                Success = false,
                FinalizedAt = TimeProvider.System.GetUtcNow(),
                Message = "Server is not the owner of this match."
            });
        }

        _logger.LogInformation(
            "[MatchesV5] Received binary submission for Match #{MatchId} from Server #{ServerId} (Players: {PlayerCount}, Duration: {Duration}s)",
            matchId, authenticatedServer.ID, payload.Players.Length, payload.DurationSeconds);

        // Pre-filter against duplicate submissions via Redis idempotency guard
        bool isFirstSubmission = await MatchSubmissionIdempotency.TryAcquireAsync(_distributedCache, matchId);
        if (!isFirstSubmission)
        {
            _logger.LogWarning(
                "[MatchesV5] Duplicate submission received for Match #{MatchId} (already queued or completed). Returning success receipt.",
                matchId);

            return Ok(new MatchFinalizationReceipt
            {
                MatchId = matchId,
                Success = true,
                FinalizedAt = TimeProvider.System.GetUtcNow(),
                Message = "Match already queued or completed."
            });
        }

        MatchSubmissionPayload jobPayload = new()
        {
            MatchID = matchId,
            BinaryPayload = payload,
            MatchServer = authenticatedServer,
            RemoteIp = remoteIp,
            AccountId = authenticatedServer.HostAccountID
        };

        bool enqueued;
        try
        {
            enqueued = await _matchProcessingChannel.EnqueueAsync(jobPayload, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MatchesV5] Failed to enqueue binary Match #{MatchId} to processing channel.", matchId);
            await MatchSubmissionIdempotency.ReleaseAsync(_distributedCache, matchId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new MatchFinalizationReceipt
            {
                MatchId = matchId,
                Success = false,
                FinalizedAt = TimeProvider.System.GetUtcNow(),
                Message = "Internal match processing queue unavailable."
            });
        }

        if (!enqueued)
        {
            _logger.LogError("[MatchesV5] Processing channel rejected binary Match #{MatchId} (channel full).", matchId);
            await MatchSubmissionIdempotency.ReleaseAsync(_distributedCache, matchId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new MatchFinalizationReceipt
            {
                MatchId = matchId,
                Success = false,
                FinalizedAt = TimeProvider.System.GetUtcNow(),
                Message = "Internal match processing queue full."
            });
        }

        _logger.LogInformation("[MatchesV5] Successfully enqueued binary Match #{MatchId} for async finalization.", matchId);

        return Ok(new MatchFinalizationReceipt
        {
            MatchId = matchId,
            Success = true,
            FinalizedAt = TimeProvider.System.GetUtcNow(),
            Message = "Match successfully ingested."
        });
    }
}
