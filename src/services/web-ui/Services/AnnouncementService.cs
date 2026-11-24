// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json;
using DAWNBRINGER.WebPortal.UI.Models;

namespace DAWNBRINGER.WebPortal.UI.Services;

public interface IAnnouncementService
{
    IReadOnlyList<AnnouncementItemDto> GetAllAnnouncements();
    IReadOnlyList<AnnouncementItemDto> GetActiveFeed();
    AnnouncementItemDto? GetById(string id);
    bool CreateAnnouncement(AnnouncementItemDto item);
    bool UpdateAnnouncement(AnnouncementItemDto item);
    bool DeleteAnnouncement(string id);
    bool ToggleActive(string id);
}

public sealed class AnnouncementService : IAnnouncementService
{
    private readonly List<AnnouncementItemDto> _items = [];
    private readonly Lock _lock = new();
    private readonly ILogger<AnnouncementService> _logger;
    private readonly string _storageFilePath;
    private bool _loaded;

    public AnnouncementService(IConfiguration configuration, IWebHostEnvironment environment,
        ILogger<AnnouncementService> logger)
    {
        _logger = logger;
        string contentRoot = environment.ContentRootPath;
        string dataDir = Path.Combine(contentRoot, "wwwroot", "data");
        if (!Directory.Exists(dataDir)) Directory.CreateDirectory(dataDir);
        _storageFilePath = Path.Combine(dataDir, "announcements.json");
        EnsureLoaded();
    }

    public IReadOnlyList<AnnouncementItemDto> GetAllAnnouncements()
    {
        lock (_lock)
        {
            EnsureLoaded();
            return [.. _items.OrderByDescending(x => x.Priority).ThenByDescending(x => x.CreatedAt)];
        }
    }

    public IReadOnlyList<AnnouncementItemDto> GetActiveFeed()
    {
        lock (_lock)
        {
            EnsureLoaded();
            return
            [
                .. _items.Where(x => x.IsActive).OrderByDescending(x => x.Priority).ThenByDescending(x => x.CreatedAt)
            ];
        }
    }

    public AnnouncementItemDto? GetById(string id)
    {
        lock (_lock)
        {
            EnsureLoaded();
            return _items.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        }
    }

    public bool CreateAnnouncement(AnnouncementItemDto item)
    {
        lock (_lock)
        {
            EnsureLoaded();
            if (string.IsNullOrWhiteSpace(item.Id)) item.Id = Guid.NewGuid().ToString("N");
            item.CreatedAt = TimeProvider.System.GetUtcNow().UtcDateTime;
            _items.Add(item);
            SaveInternal();
            return true;
        }
    }

    public bool UpdateAnnouncement(AnnouncementItemDto item)
    {
        lock (_lock)
        {
            EnsureLoaded();
            int index = _items.FindIndex(x => string.Equals(x.Id, item.Id, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return false;

            item.UpdatedAt = TimeProvider.System.GetUtcNow().UtcDateTime;
            _items[index] = item;
            SaveInternal();
            return true;
        }
    }

    public bool DeleteAnnouncement(string id)
    {
        lock (_lock)
        {
            EnsureLoaded();
            int removed = _items.RemoveAll(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                SaveInternal();
                return true;
            }

            return false;
        }
    }

    public bool ToggleActive(string id)
    {
        lock (_lock)
        {
            EnsureLoaded();
            AnnouncementItemDto? item =
                _items.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (item != null)
            {
                item.IsActive = !item.IsActive;
                item.UpdatedAt = TimeProvider.System.GetUtcNow().UtcDateTime;
                SaveInternal();
                return true;
            }

            return false;
        }
    }

    private void EnsureLoaded()
    {
        lock (_lock)
        {
            if (_loaded) return;

            try
            {
                if (File.Exists(_storageFilePath))
                {
                    string json = File.ReadAllText(_storageFilePath);
                    List<AnnouncementItemDto>? loaded = JsonSerializer.Deserialize<List<AnnouncementItemDto>>(json,
                        new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });

                    if (loaded is { Count: > 0 })
                    {
                        _items.Clear();
                        _items.AddRange(loaded);
                        _loaded = true;
                        _logger.LogInformation("[AnnouncementService] Loaded {Count} announcements from {Path}",
                            _items.Count, _storageFilePath);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AnnouncementService] Failed loading announcements from {Path}",
                    _storageFilePath);
            }

            // Seed default announcements if none exist
            SeedDefaults();
            SaveInternal();
            _loaded = true;
        }
    }

    private void SeedDefaults()
    {
        _items.Clear();
        _items.Add(new AnnouncementItemDto
        {
            Id = "kongor_update_502",
            Title = "Project Kongor v5.0.2 Update",
            Category = "PATCH NOTES",
            BadgeColor = "#00e5ff",
            Date = TimeProvider.System.GetUtcNow().UtcDateTime.ToString("MMM dd, yyyy"),
            Description = "Initial War Effort daily quest & 14-bag reward rollout, Valkey matchmaking & unique clan tag fixes, Queue Arcade bot/player slot swaps, custom map stats & scoreboards, Strikers 3-2-1 kickoff fix, and custom mode/avatar fixes.",
            ImageUrl = "/ui/fe2/mainmenu/bg3_new.tga",
            ActionType = "url",
            ActionTarget = "https://discord.gg/projectkongor",
            VideoUrl = null,
            IsActive = true,
            Priority = 100
        });

        _items.Add(new AnnouncementItemDto
        {
            Id = "custom_maps_10v10",
            Title = "10v10 Chaos & Repaired Custom Maps",
            Category = "CUSTOM MAPS",
            BadgeColor = "#ffb300",
            Date = TimeProvider.System.GetUtcNow().UtcDateTime.ToString("MMM dd, yyyy"),
            Description = "Host 20-player Devo Wars, True Devo & Mid Wars 10v10, Three Kingdoms, Persian Pass, Strikers, Valkyrie/Archer Wars, Enfo's, Nightmarish Co-Op, WTF, and Halloween maps!",
            ImageUrl = "/ui/fe2/mainmenu/bg3_new.tga",
            ActionType = "url",
            ActionTarget = "https://discord.gg/projectkongor",
            IsActive = true,
            Priority = 90
        });

        _items.Add(new AnnouncementItemDto
        {
            Id = "guccidonut_twitch",
            Title = "guccidonut Live on Twitch",
            Category = "STREAM",
            BadgeColor = "#a855f7",
            Date = "LIVE NOW",
            Description = "Watch competitive HoN matches, community viewer lobbies, and custom map nights live on Twitch!",
            ImageUrl = "/ui/fe2/mainmenu/bg3_new.tga",
            ActionType = "video",
            ActionTarget = "https://twitch.tv/theguccidonut/",
            VideoUrl = "https://twitch.tv/theguccidonut/",
            IsActive = true,
            Priority = 80
        });
    }

    private void SaveInternal()
    {
        try
        {
            string json = JsonSerializer.Serialize(_items, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(_storageFilePath, json);
            _logger.LogInformation("[AnnouncementService] Saved {Count} announcements to {Path}", _items.Count,
                _storageFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AnnouncementService] Failed saving announcements to {Path}", _storageFilePath);
        }
    }
}
