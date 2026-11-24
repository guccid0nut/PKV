// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Kongor.Core.Services.Session;
using MediatR;
using Microsoft.EntityFrameworkCore;
using MERRICK.DatabaseContext.Entities.Game;
using MERRICK.DatabaseContext.Extensions;
using MERRICK.DatabaseContext.Persistence;
using NEXUS.Contracts.Clients;
using NEXUS.Contracts.CQRS.PlayerStats;
using NEXUS.Contracts.Telemetry;

namespace ZORGATH.WebPortal.API.Features.PlayerStats;

/// <summary>
///     Handler for retrieving evidence records recorded for a match.
/// </summary>
public sealed class GetMatchEvidenceHandler(MerrickContext db)
    : IRequestHandler<GetMatchEvidenceQuery, MatchEvidenceDto?>
{
    /// <inheritdoc />
    public async Task<MatchEvidenceDto?> Handle(GetMatchEvidenceQuery request, CancellationToken cancellationToken)
    {
        MatchEvidence? evidence = await db.MatchEvidence
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.MatchID == request.MatchId, cancellationToken);
        return evidence?.ToDto();
    }
}

/// <summary>
///     Handler for creating or updating evidence records for a match.
/// </summary>
public sealed class SaveMatchEvidenceHandler(MerrickContext db, TimeProvider? timeProvider = null)
    : IRequestHandler<SaveMatchEvidenceCommand, bool>
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<bool> Handle(SaveMatchEvidenceCommand request, CancellationToken cancellationToken)
    {
        if (request.MatchId <= 0) return false;

        MatchEvidence? existing =
            await db.MatchEvidence.FirstOrDefaultAsync(e => e.MatchID == request.MatchId, cancellationToken);

        if (existing != null)
        {
            existing.ChatLogJson = request.Evidence.ChatLogJson ?? "[]";
            existing.HasReplay = request.Evidence.HasReplay;
            existing.HasVoice = request.Evidence.HasVoice;
            existing.VoiceTrackLegionUrl = request.Evidence.VoiceTrackLegionUrl;
            existing.VoiceTrackHellbourneUrl = request.Evidence.VoiceTrackHellbourneUrl;
            existing.TimestampExtracted = _timeProvider.GetUtcNow();
        }
        else
        {
            MatchEvidence newEvidence = new()
            {
                MatchID = request.MatchId,
                ChatLogJson = request.Evidence.ChatLogJson ?? "[]",
                HasReplay = request.Evidence.HasReplay,
                HasVoice = request.Evidence.HasVoice,
                VoiceTrackLegionUrl = request.Evidence.VoiceTrackLegionUrl,
                VoiceTrackHellbourneUrl = request.Evidence.VoiceTrackHellbourneUrl,
                TimestampExtracted = _timeProvider.GetUtcNow()
            };
            db.MatchEvidence.Add(newEvidence);
        }

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}

/// <summary>
///     Handler for retrieving all approved community game server hosts.
/// </summary>
public sealed class GetServerHostsHandler(MerrickContext db)
    : IRequestHandler<GetServerHostsQuery, List<ServerHostDto>>
{
    /// <inheritdoc />
    public async Task<List<ServerHostDto>> Handle(GetServerHostsQuery request, CancellationToken cancellationToken)
    {
        List<HostApplication> approvedApps = await db.HostApplications
            .Include(h => h.User)
            .Include(h => h.HostAccount)
            .Where(h => h.Status == HostApplicationStatus.Approved)
            .ToListAsync(cancellationToken);

        return approvedApps.Select(h => new ServerHostDto(
            h.UserID,
            h.HostAccount?.Name ?? h.HostUsername,
            h.Location,
            0,
            h.TimestampApplied,
            "Community Host",
            true
        )).ToList();
    }
}

/// <summary>
///     Handler for retrieving compendium details for all heroes.
/// </summary>
public sealed class GetAllHeroDetailsHandler(MerrickContext db)
    : IRequestHandler<GetAllHeroDetailsQuery, List<HeroDetailDto>>
{
    /// <inheritdoc />
    public async Task<List<HeroDetailDto>> Handle(GetAllHeroDetailsQuery request, CancellationToken cancellationToken)
    {
        List<HeroDetail> heroes = await db.HeroDetails.AsNoTracking().ToListAsync(cancellationToken);
        return heroes.Select(h => h.ToDto()).ToList();
    }
}

/// <summary>
///     Handler for retrieving compendium details for a specific hero by hero code or display name.
/// </summary>
public sealed class GetHeroDetailHandler(MerrickContext db)
    : IRequestHandler<GetHeroDetailQuery, HeroDetailDto?>
{
    /// <inheritdoc />
    public async Task<HeroDetailDto?> Handle(GetHeroDetailQuery request, CancellationToken cancellationToken)
    {
        HeroDetail? hero = await db.HeroDetails.AsNoTracking()
            .FirstOrDefaultAsync(h => h.HeroCode == request.HeroId || h.DisplayName == request.HeroId, cancellationToken);
        return hero?.ToDto();
    }
}

/// <summary>
///     Handler for retrieving compendium definitions and attributes for all items.
/// </summary>
public sealed class GetAllHeroItemsHandler(MerrickContext db)
    : IRequestHandler<GetAllHeroItemsQuery, List<HeroItemDto>>
{
    /// <inheritdoc />
    public async Task<List<HeroItemDto>> Handle(GetAllHeroItemsQuery request, CancellationToken cancellationToken)
    {
        List<HeroItem> items = await db.HeroItems
            .AsNoTracking()
            .Include(i => i.Components)
            .ToListAsync(cancellationToken);
        return items.Select(i => i.ToDto()).ToList();
    }
}

/// <summary>
///     Handler for submitting a player conduct report.
/// </summary>
public sealed class SubmitPlayerReportHandler(MerrickContext db, TimeProvider? timeProvider = null)
    : IRequestHandler<SubmitPlayerReportCommand, bool>
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<bool> Handle(SubmitPlayerReportCommand request, CancellationToken cancellationToken)
    {
        PlayerReport report = new()
        {
            ReporterAccountID = request.Report.ReporterAccountID,
            ReporterAccountName = request.Report.ReporterAccountName ?? string.Empty,
            ReportedAccountID = request.Report.ReportedAccountID,
            ReportedAccountName = request.Report.ReportedAccountName ?? string.Empty,
            MatchID = request.Report.MatchID,
            Reason = request.Report.Reason ?? "Unspecified",
            Description = request.Report.Description,
            Status = "Pending",
            TimestampCreated = _timeProvider.GetUtcNow()
        };

        db.PlayerReports.Add(report);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}

/// <summary>
///     Handler for retrieving approved streamer account names.
/// </summary>
public sealed class GetApprovedStreamersHandler(MerrickContext db)
    : IRequestHandler<GetApprovedStreamersQuery, List<string>>
{
    /// <inheritdoc />
    public async Task<List<string>> Handle(GetApprovedStreamersQuery request, CancellationToken cancellationToken)
    {
        return await db.StreamerApplications
            .Include(s => s.MainAccount)
            .Where(s => s.Status == StreamerApplicationStatus.Approved && s.MainAccount != null)
            .Select(s => s.MainAccount!.Name)
            .ToListAsync(cancellationToken);
    }
}

/// <summary>
///     Handler for retrieving live cluster telemetry data.
/// </summary>
public sealed class GetLiveClusterTelemetryHandler(MerrickContext db, TimeProvider? timeProvider = null)
    : IRequestHandler<GetLiveClusterTelemetryQuery, LiveTelemetryDataDto>
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<LiveTelemetryDataDto> Handle(GetLiveClusterTelemetryQuery request, CancellationToken cancellationToken)
    {
        DateTimeOffset userCutoff = _timeProvider.GetUtcNow().AddMinutes(-15);
        DateTimeOffset matchCutoff = _timeProvider.GetUtcNow().AddMinutes(-45);

        int onlinePlayers = await db.Users.CountAsync(u => u.TimestampLastActive >= userCutoff, cancellationToken);
        int matchesInProgress = await db.MatchStatistics.CountAsync(m => m.TimestampRecorded >= matchCutoff, cancellationToken);

        return new LiveTelemetryDataDto(
            onlinePlayers > 0 ? onlinePlayers : 1,
            matchesInProgress > 0 ? matchesInProgress : 1,
            1,
            ["US East", "EU West"],
            _timeProvider.GetUtcNow()
        );
    }
}

/// <summary>
///     Handler for retrieving live match telemetry from session cache.
/// </summary>
public sealed class GetLiveMatchTelemetryHandler(IRedisMatchSessionCacheService? sessionCache = null)
    : IRequestHandler<GetLiveMatchTelemetryQuery, LiveServerTickPayloadDto?>
{
    /// <inheritdoc />
    public async Task<LiveServerTickPayloadDto?> Handle(GetLiveMatchTelemetryQuery request, CancellationToken cancellationToken)
    {
        if (request.MatchId <= 0 || sessionCache == null) return null;
        return await sessionCache.GetLiveMatchStateAsync(request.MatchId, cancellationToken);
    }
}
