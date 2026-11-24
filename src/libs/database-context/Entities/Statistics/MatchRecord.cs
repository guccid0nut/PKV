// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace MERRICK.DatabaseContext.Entities.Statistics;

/// <summary>
///     Authoritative ledger entity for all match instances across their entire lifecycle.
///     Mapped to <c>stat.MatchRecords</c>.
/// </summary>
[Table("MatchRecords", Schema = "stat")]
[Index(nameof(MatchID), IsUnique = true)]
[Index(nameof(ServerID), nameof(State))]
[Index(nameof(HostAccountName))]
[Index(nameof(CreatedAt))]
public class MatchRecord
{
    /// <summary>
    ///     Authoritative unique match identifier (positive integer >= 1).
    /// </summary>
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int MatchID { get; set; }

    /// <summary>
    ///     Identifier of the dedicated game server worker assigned to this match.
    /// </summary>
    public int ServerID { get; set; }

    /// <summary>
    ///     Network listen port of the dedicated server worker (e.g. 11235).
    /// </summary>
    public int ServerPort { get; set; }

    /// <summary>
    ///     Display name of the server hosting the match.
    /// </summary>
    [MaxLength(128)]
    public string ServerName { get; set; } = string.Empty;

    /// <summary>
    ///     Account nickname of the player who created/hosted the lobby.
    /// </summary>
    [MaxLength(64)]
    public string HostAccountName { get; set; } = string.Empty;

    /// <summary>
    ///     Active map name (e.g. "caldavar", "midwars", "tutorial").
    /// </summary>
    [MaxLength(64)]
    public string Map { get; set; } = string.Empty;

    /// <summary>
    ///     Match mode string (e.g. "ap", "ar", "sd", "practice").
    /// </summary>
    [MaxLength(32)]
    public string MatchMode { get; set; } = string.Empty;

    /// <summary>
    ///     Whether the match is unranked / casual.
    /// </summary>
    public bool IsCasual { get; set; }

    /// <summary>
    ///     Arranged match type code.
    /// </summary>
    public int MatchType { get; set; }

    /// <summary>
    ///     Current lifecycle progression state of this match.
    /// </summary>
    public MatchLifecycleState State { get; set; } = MatchLifecycleState.Lobby;

    /// <summary>
    ///     Human-readable explanation if the match was cancelled or terminated prematurely.
    /// </summary>
    [MaxLength(255)]
    public string? TerminationReason { get; set; }

    /// <summary>
    ///     Timestamp when the match record was created in Master Server.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = TimeProvider.System.GetUtcNow();

    /// <summary>
    ///     Timestamp when the match officially started (loaded into map / combat commenced).
    /// </summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>
    ///     Timestamp when the match ended (completed, cancelled, or abandoned).
    /// </summary>
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>
    ///     Peak number of connected human players in the match.
    /// </summary>
    public int PlayerCount { get; set; }

    public override string ToString() => $"[MatchRecord #{MatchID} ({State})] Server: {ServerName} ({ServerPort}), Map: {Map}, Host: {HostAccountName}";
}
