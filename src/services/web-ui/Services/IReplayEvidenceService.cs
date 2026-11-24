// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace DAWNBRINGER.WebPortal.UI.Services;

public class MatchEvidenceResultDto
{
    public bool Success { get; set; }

    public int MatchID { get; set; }

    public string MapName { get; set; } = "caldavar";

    public int DurationSeconds { get; set; }

    public string FormattedDuration { get; set; } = "00:00";

    public string? ReportedAccountName { get; set; }

    public int? SuggestedTimestampMs { get; set; }

    public string? SuggestedTimestampFormatted { get; set; }

    public bool HasReplay { get; set; }

    public bool HasVoice { get; set; }

    public string EvidenceSource { get; set; } = "telemetry"; // "replay_archive" or "telemetry"

    public string? ReplayFileName { get; set; }

    public long? ReplayFileSize { get; set; }

    public List<string> RosterPlayers { get; set; } = [];

    public string? VoiceTrackLegionUrl { get; set; }

    public string? VoiceTrackHellbourneUrl { get; set; }

    public List<MatchChatMessageDto> ChatLog { get; set; } = [];

    public int TotalMessages => ChatLog.Count;

    public int ReportedPlayerMessagesCount => ChatLog.Count(c => c.IsReportedPlayer);
}

public interface IReplayEvidenceService
{
    Task<MatchEvidenceResultDto> GetMatchEvidenceAsync(
        int matchId,
        string? reportedAccountName = null,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);

    Task<MatchEvidenceResultDto> ProcessUploadedReplayAsync(
        int matchId,
        Stream replayStream,
        string fileName,
        string? reportedAccountName = null,
        CancellationToken cancellationToken = default);

    Task<Stream?> GetTeamVoiceStreamAsync(
        int matchId,
        int team,
        CancellationToken cancellationToken = default);
}
