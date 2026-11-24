// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace DAWNBRINGER.WebPortal.UI.Services;

public sealed class TwitchStreamInfo
{
    public bool IsLive { get; set; }
    public string TwitchUsername { get; set; } = string.Empty;
    public string? TwitchId { get; set; }
    public string? ChannelUrl { get; set; }
    public string? StreamTitle { get; set; }
    public string? GameName { get; set; }
    public int ViewerCount { get; set; }
    public string? ThumbnailUrl { get; set; }
    public DateTimeOffset? StartedAt { get; set; }

    public bool IsPlayingHon => IsLive &&
                                !string.IsNullOrWhiteSpace(GameName) &&
                                (GameName.Contains("Heroes of Newerth", StringComparison.OrdinalIgnoreCase) ||
                                 GameName.Equals("HoN", StringComparison.OrdinalIgnoreCase));
}

public interface ITwitchStreamService
{
    Task<TwitchStreamInfo> GetStreamStatusAsync(string twitchUsername, string? twitchId = null,
        CancellationToken cancellationToken = default);

    Task<List<TwitchStreamInfo>> GetLiveHonStreamsAsync(IEnumerable<string> twitchLogins,
        CancellationToken cancellationToken = default);
}
