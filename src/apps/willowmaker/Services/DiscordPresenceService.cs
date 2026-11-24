// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Constants;
using DiscordRPC;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Button = DiscordRPC.Button;
using ILogger = DiscordRPC.Logging.ILogger;
using LogLevel = DiscordRPC.Logging.LogLevel;

namespace WILLOWMAKER.Core.Services;

public class DiscordPresenceService : IDisposable, IAsyncDisposable
{
    public const string DefaultDiscordAppId = "1003760104199422052";

    private static readonly SocketsHttpHandler PortalAssetsHttpHandler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    };

    private static readonly HttpClient PortalAssetsHttpClient = new(PortalAssetsHttpHandler, disposeHandler: false)
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    private static readonly string LogFilePath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory,
        "userdata",
        "discord_rpc_debug.log"
    );

    private static readonly Dictionary<string, string> DiscordHeroAssetKeyOverrides =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [Heroes.Bombardier.Identifier] = "hero_bomb",
            [Heroes.CorruptedDisciple.Identifier] = "hero_corrupted_disciple",
            [Heroes.DoctorRepulsor.Identifier] = "hero_doctor_repulsor",
            [Heroes.Blacksmith.Identifier] = "hero_dwarf_magi",
            [Heroes.EmeraldWarden.Identifier] = "hero_emerald_warden",
            [Heroes.FlintBeastwood.Identifier] = "hero_flint_beastwood",
            [Heroes.ForsakenArcher.Identifier] = "hero_forsaken_archer",
            [Heroes.KingKlout.Identifier] = "hero_king_klout",
            [Heroes.MasterOfArms.Identifier] = "hero_master_of_arms",
            [Heroes.MonkeyKing.Identifier] = "hero_monkey_king",
            [Heroes.SandWraith.Identifier] = "hero_sand_wraith",
            [Heroes.SirBenzington.Identifier] = "hero_sir_benzington",
            [Heroes.WitchSlayer.Identifier] = "hero_witch_slayer"
        };

    private static readonly string[] PortalNonHeroAssetKeys =
    [
        "large",
        "map_archers",
        "map_arena",
        "map_caldavar",
        "map_capturetheflag",
        "map_colosseumduel",
        "map_colosseum_duel",
        "map_darkwoodvale",
        "map_devowars",
        "map_empathmaze",
        "map_empath_maze",
        "map_enfos",
        "map_grimmscrossing",
        "map_midwars",
        "map_nightmarishcoop",
        "map_nightmarish_coop",
        "map_pebblesduel",
        "map_pebbles_duel",
        "map_persianpass",
        "map_persian_pass",
        "map_prophets",
        "map_riftwars",
        "map_spotlight",
        "map_strikers",
        "map_teamdeathmatch",
        "map_thegrimmhunt",
        "map_threekingdoms",
        "map_three_kingdoms",
        "map_truedevo",
        "map_tutorial",
        "map_valkwars",
        "map_watchtower",
        "small"
    ];

    private static readonly (string Identifier, string Name)[] StandardHeroContracts =
    [
        (Heroes.Accursed.Identifier, Heroes.Accursed.Name), (Heroes.Adrenaline.Identifier, Heroes.Adrenaline.Name),
        (Heroes.Aluna.Identifier, Heroes.Aluna.Name), (Heroes.Andromeda.Identifier, Heroes.Andromeda.Name),
        (Heroes.Apex.Identifier, Heroes.Apex.Name), (Heroes.Arachna.Identifier, Heroes.Arachna.Name),
        (Heroes.Armadon.Identifier, Heroes.Armadon.Name), (Heroes.Artesia.Identifier, Heroes.Artesia.Name),
        (Heroes.Artillery.Identifier, Heroes.Artillery.Name), (Heroes.WretchedHag.Identifier, Heroes.WretchedHag.Name),
        (Heroes.Behemoth.Identifier, Heroes.Behemoth.Name), (Heroes.Balphagore.Identifier, Heroes.Balphagore.Name),
        (Heroes.Berzerker.Identifier, Heroes.Berzerker.Name), (Heroes.Blitz.Identifier, Heroes.Blitz.Name),
        (Heroes.Bombardier.Identifier, Heroes.Bombardier.Name), (Heroes.Bubbles.Identifier, Heroes.Bubbles.Name),
        (Heroes.Bushwack.Identifier, Heroes.Bushwack.Name), (Heroes.Calamity.Identifier, Heroes.Calamity.Name),
        (Heroes.Qi.Identifier, Heroes.Qi.Name), (Heroes.TheChipper.Identifier, Heroes.TheChipper.Name),
        (Heroes.Chronos.Identifier, Heroes.Chronos.Name), (Heroes.Circe.Identifier, Heroes.Circe.Name),
        (Heroes.CorruptedDisciple.Identifier, Heroes.CorruptedDisciple.Name), (Heroes.Cthulhuphant.Identifier, Heroes.Cthulhuphant.Name),
        (Heroes.Dampeer.Identifier, Heroes.Dampeer.Name), (Heroes.Deadlift.Identifier, Heroes.Deadlift.Name),
        (Heroes.Deadwood.Identifier, Heroes.Deadwood.Name), (Heroes.Defiler.Identifier, Heroes.Defiler.Name),
        (Heroes.Devourer.Identifier, Heroes.Devourer.Name), (Heroes.PlagueRider.Identifier, Heroes.PlagueRider.Name),
        (Heroes.DoctorRepulsor.Identifier, Heroes.DoctorRepulsor.Name), (Heroes.LordSalforis.Identifier, Heroes.LordSalforis.Name),
        (Heroes.DrunkenMaster.Identifier, Heroes.DrunkenMaster.Name), (Heroes.Blacksmith.Identifier, Heroes.Blacksmith.Name),
        (Heroes.Slither.Identifier, Heroes.Slither.Name), (Heroes.Electrician.Identifier, Heroes.Electrician.Name),
        (Heroes.Ellonia.Identifier, Heroes.Ellonia.Name), (Heroes.EmeraldWarden.Identifier, Heroes.EmeraldWarden.Name),
        (Heroes.Empath.Identifier, Heroes.Empath.Name), (Heroes.Engineer.Identifier, Heroes.Engineer.Name),
        (Heroes.Fayde.Identifier, Heroes.Fayde.Name), (Heroes.Nymphora.Identifier, Heroes.Nymphora.Name),
        (Heroes.Draconis.Identifier, Heroes.Draconis.Name), (Heroes.FlintBeastwood.Identifier, Heroes.FlintBeastwood.Name),
        (Heroes.Flux.Identifier, Heroes.Flux.Name), (Heroes.ForsakenArcher.Identifier, Heroes.ForsakenArcher.Name),
        (Heroes.Glacius.Identifier, Heroes.Glacius.Name), (Heroes.Gauntlet.Identifier, Heroes.Gauntlet.Name),
        (Heroes.Gemini.Identifier, Heroes.Gemini.Name), (Heroes.Geomancer.Identifier, Heroes.Geomancer.Name),
        (Heroes.TheGladiator.Identifier, Heroes.TheGladiator.Name), (Heroes.Goldenveil.Identifier, Heroes.Goldenveil.Name),
        (Heroes.Grinex.Identifier, Heroes.Grinex.Name), (Heroes.Gunblade.Identifier, Heroes.Gunblade.Name),
        (Heroes.Hammerstorm.Identifier, Heroes.Hammerstorm.Name), (Heroes.NightHound.Identifier, Heroes.NightHound.Name),
        (Heroes.SoulReaper.Identifier, Heroes.SoulReaper.Name), (Heroes.Hellbringer.Identifier, Heroes.Hellbringer.Name),
        (Heroes.Swiftblade.Identifier, Heroes.Swiftblade.Name), (Heroes.BloodHunter.Identifier, Heroes.BloodHunter.Name),
        (Heroes.Myrmidon.Identifier, Heroes.Myrmidon.Name), (Heroes.Ichor.Identifier, Heroes.Ichor.Name),
        (Heroes.Magebane.Identifier, Heroes.Magebane.Name), (Heroes.Jeraziah.Identifier, Heroes.Jeraziah.Name),
        (Heroes.Kane.Identifier, Heroes.Kane.Name), (Heroes.Kinesis.Identifier, Heroes.Kinesis.Name),
        (Heroes.KingKlout.Identifier, Heroes.KingKlout.Name), (Heroes.Klanx.Identifier, Heroes.Klanx.Name),
        (Heroes.Kraken.Identifier, Heroes.Kraken.Name), (Heroes.MoonQueen.Identifier, Heroes.MoonQueen.Name),
        (Heroes.Thunderbringer.Identifier, Heroes.Thunderbringer.Name), (Heroes.Legionnaire.Identifier, Heroes.Legionnaire.Name),
        (Heroes.Lodestone.Identifier, Heroes.Lodestone.Name), (Heroes.Magmus.Identifier, Heroes.Magmus.Name),
        (Heroes.Maliken.Identifier, Heroes.Maliken.Name), (Heroes.Martyr.Identifier, Heroes.Martyr.Name),
        (Heroes.MasterOfArms.Identifier, Heroes.MasterOfArms.Name), (Heroes.Midas.Identifier, Heroes.Midas.Name),
        (Heroes.Xemplar.Identifier, Heroes.Xemplar.Name), (Heroes.Moira.Identifier, Heroes.Moira.Name),
        (Heroes.Monarch.Identifier, Heroes.Monarch.Name), (Heroes.MonkeyKing.Identifier, Heroes.MonkeyKing.Name),
        (Heroes.Moraxus.Identifier, Heroes.Moraxus.Name), (Heroes.Pharaoh.Identifier, Heroes.Pharaoh.Name),
        (Heroes.Nitro.Identifier, Heroes.Nitro.Name), (Heroes.Nomad.Identifier, Heroes.Nomad.Name),
        (Heroes.Oogie.Identifier, Heroes.Oogie.Name), (Heroes.Ophelia.Identifier, Heroes.Ophelia.Name),
        (Heroes.Pandamonium.Identifier, Heroes.Pandamonium.Name), (Heroes.Parallax.Identifier, Heroes.Parallax.Name),
        (Heroes.Parasite.Identifier, Heroes.Parasite.Name), (Heroes.Pearl.Identifier, Heroes.Pearl.Name),
        (Heroes.Pestilence.Identifier, Heroes.Pestilence.Name), (Heroes.Bramble.Identifier, Heroes.Bramble.Name),
        (Heroes.PollywogPriest.Identifier, Heroes.PollywogPriest.Name), (Heroes.Predator.Identifier, Heroes.Predator.Name),
        (Heroes.Prisoner945.Identifier, Heroes.Prisoner945.Name), (Heroes.Prophet.Identifier, Heroes.Prophet.Name),
        (Heroes.PuppetMaster.Identifier, Heroes.PuppetMaster.Name), (Heroes.Pyromancer.Identifier, Heroes.Pyromancer.Name),
        (Heroes.AmunRa.Identifier, Heroes.AmunRa.Name), (Heroes.Rally.Identifier, Heroes.Rally.Name),
        (Heroes.Rampage.Identifier, Heroes.Rampage.Name), (Heroes.Ravenor.Identifier, Heroes.Ravenor.Name),
        (Heroes.Revenant.Identifier, Heroes.Revenant.Name), (Heroes.Rhapsody.Identifier, Heroes.Rhapsody.Name),
        (Heroes.Riftwalker.Identifier, Heroes.Riftwalker.Name), (Heroes.Riptide.Identifier, Heroes.Riptide.Name),
        (Heroes.Pebbles.Identifier, Heroes.Pebbles.Name), (Heroes.Salomon.Identifier, Heroes.Salomon.Name),
        (Heroes.SandWraith.Identifier, Heroes.SandWraith.Name), (Heroes.Sapphire.Identifier, Heroes.Sapphire.Name),
        (Heroes.TheMadman.Identifier, Heroes.TheMadman.Name), (Heroes.Scout.Identifier, Heroes.Scout.Name),
        (Heroes.Shadowblade.Identifier, Heroes.Shadowblade.Name), (Heroes.DementedShaman.Identifier, Heroes.DementedShaman.Name),
        (Heroes.Shellshock.Identifier, Heroes.Shellshock.Name), (Heroes.Silhouette.Identifier, Heroes.Silhouette.Name),
        (Heroes.SirBenzington.Identifier, Heroes.SirBenzington.Name), (Heroes.Skrap.Identifier, Heroes.Skrap.Name),
        (Heroes.Solstice.Identifier, Heroes.Solstice.Name), (Heroes.Soulstealer.Identifier, Heroes.Soulstealer.Name),
        (Heroes.Succubus.Identifier, Heroes.Succubus.Name), (Heroes.Gravekeeper.Identifier, Heroes.Gravekeeper.Name),
        (Heroes.Tarot.Identifier, Heroes.Tarot.Name), (Heroes.Tempest.Identifier, Heroes.Tempest.Name),
        (Heroes.KeeperOfTheForest.Identifier, "Keeper of the Forest"), (Heroes.Tremble.Identifier, Heroes.Tremble.Name),
        (Heroes.Tundra.Identifier, Heroes.Tundra.Name), (Heroes.Valkyrie.Identifier, Heroes.Valkyrie.Name),
        (Heroes.TheDarkLady.Identifier, Heroes.TheDarkLady.Name), (Heroes.Vindicator.Identifier, Heroes.Vindicator.Name),
        (Heroes.VoodooJester.Identifier, Heroes.VoodooJester.Name), (Heroes.Warchief.Identifier, Heroes.Warchief.Name),
        (Heroes.WitchSlayer.Identifier, Heroes.WitchSlayer.Name), (Heroes.WarBeast.Identifier, Heroes.WarBeast.Name),
        (Heroes.Torturer.Identifier, Heroes.Torturer.Name), (Heroes.Wildsoul.Identifier, Heroes.Wildsoul.Name),
        (Heroes.Zephyr.Identifier, Heroes.Zephyr.Name)
    ];

    // Canonical hero registry derived from ASPIRE.Common.Constants.Heroes + Discord Developer Portal asset keys
    private static readonly Dictionary<string, (string AssetKey, string DisplayName)> HeroRegistry = BuildHeroRegistry();

    private static readonly HashSet<string> ValidAssetKeys = BuildValidAssetKeys(HeroRegistry);

    private static Dictionary<string, (string AssetKey, string DisplayName)> BuildHeroRegistry()
    {
        Dictionary<string, (string AssetKey, string DisplayName)> registry = new(StringComparer.OrdinalIgnoreCase);

        static void RegisterHero(
            Dictionary<string, (string AssetKey, string DisplayName)> target,
            string identifier,
            string assetKey,
            string displayName)
        {
            target[identifier] = (assetKey, displayName);
            if (identifier.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase))
            {
                string idSlug = identifier["Hero_".Length..];
                target[idSlug] = (assetKey, displayName);
                target["hero_" + idSlug] = (assetKey, displayName);
            }

            target[assetKey] = (assetKey, displayName);
            if (assetKey.StartsWith("hero_", StringComparison.OrdinalIgnoreCase))
            {
                target[assetKey["hero_".Length..]] = (assetKey, displayName);
            }
        }

        foreach ((string identifier, string displayName) in StandardHeroContracts)
        {
            string assetKey = DiscordHeroAssetKeyOverrides.TryGetValue(identifier, out string? customKey)
                ? customKey
                : identifier.ToLowerInvariant();
            RegisterHero(registry, identifier, assetKey, displayName);
        }

        RegisterHero(registry, "Hero_Gwynplaine", "hero_gwynplaine", "Gwynplaine");
        registry["pollywog_priest"] = ("hero_pollywogpriest", "Pollywog Priest");
        registry["hero_pollywog_priest"] = ("hero_pollywogpriest", "Pollywog Priest");
        registry["wildsoul"] = ("hero_yogi", "Wildsoul");
        registry["hero_wildsoul"] = ("hero_yogi", "Wildsoul");

        return registry;
    }

    private static HashSet<string> BuildValidAssetKeys(
        Dictionary<string, (string AssetKey, string DisplayName)> heroRegistry)
    {
        HashSet<string> keys = new(PortalNonHeroAssetKeys, StringComparer.OrdinalIgnoreCase)
        {
            "hero_pollywog_priest"
        };

        foreach ((string assetKey, _) in heroRegistry.Values)
        {
            keys.Add(assetKey);
        }

        return keys;
    }

    private long _accountId; // Default to 0, meaning we haven't found the ID yet
    private string _accountName = string.Empty;
    private DiscordRpcClient? _client;
    private string? _currentMatchMode; // "Practice Match", "Bot Match", or null
    private string _currentGameMode = string.Empty;
    private RichPresence? _currentPresence;
    private HubConnection? _hubConnection;
    private string? _activeHubUrl;
    private string _sessionCookie = string.Empty;
    private string _connectedHubCookie = string.Empty;
    private readonly SemaphoreSlim _hubSyncLock = new(1, 1);
    private string? _lastSentDetails;
    private string? _lastSentLargeKey;
    private string? _lastSentSmallKey;
    private string? _lastSentState;
    private DateTime _lastSentTime = DateTime.MinValue;
    private int? _localPlayerSlot;
    private string _masterServerUrl = string.Empty;
    private CancellationTokenSource? _pollingCts;
    private int _isDisposed;
    private EventHandler? _processExitHandler;
    private string? _selectedHeroAssetKey;
    private string? _selectedHeroDisplayName;
    private long _subscribedAccountId; // Tracks currently active SignalR group subscription
    private DateTime? _matchStartUtc;
    private DateTime? _queueStartUtc;
    private string _queuedGameMode = string.Empty;
    private bool _isMenuBootPreloadActive;
    private DateTimeOffset? _menuBootPreloadStartUtc;
    private int _partySize = 1;
    private int _partyMax = 5;
    private string? _partyId;
    private uint _fastBusGamePhase;
    private string? _selectedAvatarDisplayName;
    private string? _lastTelemetryKda;
    private string? _lastTelemetryLevel;
    private bool _streamerPrivacyMode;
    private string? _customDiscordAppId;
    private HashSet<string>? _livePortalAssetKeys;
    private string? _unredactedDetails;
    private string? _unredactedState;
    private string? _unredactedSmallText;
    private string? _unredactedLargeKey;
    private string? _unredactedSmallKey;
    private string? _unredactedFallbackPartyId;
    private bool _hasAuthoritativeAccountName;
    private bool _hasAuthoritativeOnlineAccountId;
    private bool _hasLiveFastBus;
    private bool _hasSeenStructuredArcadeTelemetry;
    private bool _isHostedLobbyActive;
    private int _hostedLobbySize;
    private int _hostedLobbyMax;
    private bool _isFastBusSeededName;
    private bool _isQueueArcadeActive;
    private bool _isMapEditor;
    private bool _isDiscordIpcConnected;
    private DateTime? _recentArcadeQueueStartUtc;
    private DateTime? _recentArcadeDisconnectUtc;
    private int _logBatchDepth;
    private RichPresence? _pendingLogBatchPresence;
    private bool _pendingLogBatchForceImmediate;
    private readonly Queue<DateTime> _ipcSendWindowUtc = new();
    private RichPresence? _pendingRateLimitedPresence;
    private const int MaxIpcSendsPerWindow = 4;
    private static readonly TimeSpan IpcRateLimitWindow = TimeSpan.FromSeconds(20);

    public static IReadOnlySet<string> ValidPortalAssetKeys => ValidAssetKeys;

    public long AccountId => _accountId;
    public bool HasSessionCookie => !string.IsNullOrWhiteSpace(Volatile.Read(ref _sessionCookie));
    public string AccountName => _accountName;
    public string CleanAccountName => StripClanTag(_accountName);
    public bool HasAuthoritativeAccountName => _hasAuthoritativeAccountName;
    public bool HasAuthoritativeOnlineAccountId => _hasAuthoritativeOnlineAccountId;
    public bool IsQueueArcadeActive => _isQueueArcadeActive;
    public bool HasLiveFastBus => _hasLiveFastBus;
    public bool IsMapEditor => _isMapEditor;
    public bool IsDiscordIpcConnected => _isDiscordIpcConnected;
    public string? CurrentMatchMode => _currentMatchMode;
    public string CurrentGameMode => _currentGameMode;
    public string QueuedGameMode => _queuedGameMode;
    public string? SelectedHeroAssetKey => _selectedHeroAssetKey;
    public string? SelectedHeroDisplayName => _selectedHeroDisplayName;
    public string? SelectedAvatarDisplayName => _selectedAvatarDisplayName;
    public bool StreamerPrivacyMode => _streamerPrivacyMode;
    public string ActiveDiscordAppId => ResolveActiveDiscordAppId(_customDiscordAppId);
    public RichPresence? CurrentPresence => _currentPresence;
    public RichPresence? LastDispatchedIpcPresence { get; private set; }
    public int DispatchedIpcCount { get; private set; }
    public bool HasPendingRateLimitedPresence => _pendingRateLimitedPresence != null;
    public DateTime? MatchStartUtc => _matchStartUtc;
    public DateTime? QueueStartUtc => _queueStartUtc;
    public int PartySize => _partySize;
    public int PartyMax => _partyMax;
    public string? PartyId => _partyId;
    public uint FastBusGamePhase => _fastBusGamePhase;

    public bool IsInActiveMatchOrLobby =>
        _isQueueArcadeActive ||
        (_hasLiveFastBus ? _fastBusGamePhase >= 1 : _fastBusGamePhase >= 2) ||
        !string.IsNullOrEmpty(_currentMatchMode) ||
        !string.IsNullOrEmpty(_selectedHeroAssetKey) ||
        _matchStartUtc.HasValue;

    private void SetQueueArcadeActive(bool active, string source, bool preserveRotationTimer = false)
    {
        bool previous = _isQueueArcadeActive;
        _isQueueArcadeActive = active;
        if (!active && !preserveRotationTimer)
        {
            _recentArcadeQueueStartUtc = null;
            _recentArcadeDisconnectUtc = null;
        }

        if (previous != active)
        {
            LogDebug($"[DiscordRPC][QA-FLIP] {previous} -> {active} (source={source}, fastBusPhase={_fastBusGamePhase}, map='{_currentGameMode}', queuedMode='{_queuedGameMode}')");
        }
    }

    private bool ShouldPreserveContinuousQueueOnArcadeExit(DateTime nowUtc)
    {
        if (_isQueueArcadeActive && _queueStartUtc.HasValue)
            return true;

        return _queueStartUtc.HasValue &&
               _recentArcadeQueueStartUtc.HasValue &&
               _recentArcadeDisconnectUtc.HasValue &&
               (nowUtc - _recentArcadeDisconnectUtc.Value) <= TimeSpan.FromSeconds(45);
    }

    private void ClearQueueState(string reason)
    {
        if (_queueStartUtc.HasValue || _recentArcadeQueueStartUtc.HasValue || !string.IsNullOrEmpty(_queuedGameMode))
        {
            LogDebug($"[DiscordRPC] Cleared queue state (reason='{reason}', queuedMode='{_queuedGameMode}', queueStart={_queueStartUtc:O}).");
        }

        _queueStartUtc = null;
        _recentArcadeQueueStartUtc = null;
        _recentArcadeDisconnectUtc = null;
        _queuedGameMode = string.Empty;
    }

    private static bool IsDevConsoleLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        return line.TrimStart().StartsWith("Dev:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsNonGameplayAssetPath(string lower)
    {
        return lower.Contains("/heroes/") ||
               lower.Contains("/triggers/") ||
               lower.Contains("/world/") ||
               lower.Contains("/items/") ||
               lower.Contains("/buildings/") ||
               lower.Contains(".entity") ||
               lower.Contains(".mdf") ||
               lower.Contains(".tga") ||
               lower.Contains(".dds") ||
               lower.Contains(".effect") ||
               lower.Contains(".material") ||
               lower.Contains(".model") ||
               lower.Contains(".clip") ||
               lower.Contains(".anim") ||
               lower.Contains(".wav") ||
               lower.Contains(".ogg") ||
               lower.Contains(".mp3") ||
               lower.Contains("rewardstat_ctf") ||
               lower.Contains("[nexus_globals]");
    }

    private void ClearHostedLobbyState(string reason)
    {
        if (_isHostedLobbyActive || _hostedLobbySize > 0 || _hostedLobbyMax > 0)
        {
            LogDebug($"[DiscordRPC] Cleared hosted lobby slot count ({_hostedLobbySize}/{_hostedLobbyMax}) due to '{reason}'.");
        }
        _isHostedLobbyActive = false;
        _hostedLobbySize = 0;
        _hostedLobbyMax = 0;
    }

    public static bool IsValidOnlineAccountId(long accountId) =>
        accountId > 0 && accountId <= int.MaxValue;

    public static string? ExtractNormalizedSessionCookie(string? rawToken)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return null;
        }

        string trimmed = rawToken.Trim().Trim('[', ']', '"', '\'');
        int colonIdx = trimmed.IndexOf(':');
        if (colonIdx > 0)
        {
            trimmed = trimmed[..colonIdx].Trim();
        }

        if (trimmed.Length is < 8 or > 128 ||
            trimmed.StartsWith("REDACTED", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("[REDACTED", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (char c in trimmed)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c) || c is '?' or '&' or '=')
            {
                return null;
            }
        }

        return trimmed;
    }

    public void SetSessionCookie(string? sessionCookie, string source = "external")
    {
        string? normalized = ExtractNormalizedSessionCookie(sessionCookie);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        string previous = Interlocked.Exchange(ref _sessionCookie, normalized);
        if (!string.Equals(previous, normalized, StringComparison.Ordinal))
        {
            _subscribedAccountId = 0;
            LogDebug($"[DiscordRPC] Updated active session cookie from {source} (length={normalized.Length}).");
            if (IsValidOnlineAccountId(_accountId) && Volatile.Read(ref _isDisposed) == 0)
            {
                _ = EnsureSubscribedAsync();
            }
        }
    }

    private void TryUpdateAccountId(long candidateId, string source, bool isExplicitLoginOrPipe = false)
    {
        if (!IsValidOnlineAccountId(candidateId))
        {
            if (candidateId > int.MaxValue)
            {
                LogDebug($"[DiscordRPC] Ignored unauthenticated sentinel Account ID {candidateId} from {source}.");
            }
            return;
        }

        if (_hasAuthoritativeOnlineAccountId && _accountId > 1 && candidateId == 1 && !isExplicitLoginOrPipe)
        {
            LogDebug($"[DiscordRPC] Ignored transient local-server Account ID 1 from {source} (keeping online Account ID {_accountId}).");
            return;
        }

        if (_accountId != candidateId)
        {
            _accountId = candidateId;
            _subscribedAccountId = 0;
        }

        if (candidateId > 1 && (isExplicitLoginOrPipe || source.Contains("HOMEPAGE_LUA", StringComparison.OrdinalIgnoreCase)))
        {
            _hasAuthoritativeOnlineAccountId = true;
        }
    }

    public void SetMatchMode(string? matchMode) => _currentMatchMode = matchMode;
    public void SetMatchStartTime(DateTime? utcTime) => _matchStartUtc = utcTime;
    public void SetQueueStartTime(DateTime? utcTime) => _queueStartUtc = utcTime;

    public void SetCustomDiscordAppId(string? customAppId)
    {
        _customDiscordAppId = string.IsNullOrWhiteSpace(customAppId) ? null : customAppId.Trim();
        LogDebug($"[DiscordRPC] Custom Discord Application ID configured: '{ActiveDiscordAppId}'");
    }

    public static string ResolveActiveDiscordAppId(string? customAppId = null)
    {
        if (!string.IsNullOrWhiteSpace(customAppId))
        {
            string trimmed = customAppId.Trim();
            if (Regex.IsMatch(trimmed, @"^\d{15,22}$"))
                return trimmed;
        }

        string? envOverride = Environment.GetEnvironmentVariable("KONGOR_DISCORD_APP_ID")
                              ?? Environment.GetEnvironmentVariable("DISCORD_CLIENT_ID");
        if (!string.IsNullOrWhiteSpace(envOverride))
        {
            string trimmedEnv = envOverride.Trim();
            if (Regex.IsMatch(trimmedEnv, @"^\d{15,22}$"))
                return trimmedEnv;
        }

        return DefaultDiscordAppId;
    }

    public void SetStreamerPrivacyMode(bool enabled)
    {
        if (_streamerPrivacyMode == enabled)
            return;

        _streamerPrivacyMode = enabled;
        LogDebug($"[DiscordRPC] Streamer Privacy Mode {(enabled ? "ENABLED (hiding handle, clan tag, KDA, and party metadata)" : "DISABLED (restoring full presence identity & stats)")}.");

        if (_currentPresence != null)
        {
            ReapplyCurrentPresenceFromUnredacted();
        }
    }

    public void SetLivePortalAssetKeys(IEnumerable<string>? assetKeys)
    {
        _livePortalAssetKeys = assetKeys == null
            ? null
            : new HashSet<string>(assetKeys.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()), StringComparer.OrdinalIgnoreCase);
        LogDebug($"[DiscordRPC] Live portal asset manifest updated ({_livePortalAssetKeys?.Count ?? 0} explicit keys; null/empty uses compiled ValidAssetKeys).");
        if (_currentPresence != null)
        {
            ReapplyCurrentPresenceFromUnredacted();
        }
    }

    private void ReapplyCurrentPresenceFromUnredacted()
    {
        if (_currentPresence == null)
            return;

        RichPresence updated = new()
        {
            Details = _unredactedDetails ?? _currentPresence.Details,
            State = _unredactedState ?? _currentPresence.State,
            Assets = new Assets
            {
                LargeImageKey = _unredactedLargeKey ?? _currentPresence.Assets?.LargeImageKey ?? "large",
                LargeImageText = _currentPresence.Assets?.LargeImageText ?? "Heroes of Newerth",
                SmallImageKey = _unredactedSmallKey ?? _currentPresence.Assets?.SmallImageKey ?? "small",
                SmallImageText = _unredactedSmallText ?? _currentPresence.Assets?.SmallImageText ?? "Heroes of Newerth"
            },
            Party = BuildPartyOrNull(_unredactedFallbackPartyId),
            Buttons = BuildActionButtons(),
            Timestamps = _currentPresence.Timestamps
        };
        SetPresenceInternal(updated, forceImmediate: true);
    }

    public void UpdateMapContext(string? currentMap)
    {
        if (string.IsNullOrWhiteSpace(currentMap))
            return;

        string resolvedMode = DetectGameModeFromLog(currentMap, _currentGameMode);
        if (string.IsNullOrWhiteSpace(resolvedMode) ||
            string.Equals(resolvedMode, _currentGameMode, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _currentGameMode = resolvedMode;
        LogDebug($"[DiscordRPC] Map context updated non-destructively from engine telemetry: '{_currentGameMode}' (raw='{currentMap}')");

        if (_currentPresence != null && IsInActiveMatchOrLobby)
        {
            string mapKey = GetMapAssetKeyFromGameMode(_currentGameMode);
            _unredactedLargeKey = mapKey;
            if (_currentPresence.Assets != null)
            {
                _currentPresence.Assets.LargeImageKey = mapKey;
                _currentPresence.Assets.LargeImageText = _currentGameMode;
            }
            ReapplyCurrentPresenceFromUnredacted();
        }
    }

    private void ClearHeroSelectionState()
    {
        _selectedHeroAssetKey = null;
        _selectedHeroDisplayName = null;
        _selectedAvatarDisplayName = null;
        _lastTelemetryKda = null;
        _lastTelemetryLevel = null;
    }

    private string BuildPlayerTooltipIdentity(string fallback = "Player")
    {
        return !string.IsNullOrEmpty(_accountName) ? _accountName : fallback;
    }

    private string BuildHeroSmallTooltip(string fallbackContext = "Player")
    {
        string identity = !string.IsNullOrEmpty(_accountName) ? _accountName : fallbackContext;
        if (string.IsNullOrEmpty(_selectedHeroDisplayName))
            return identity;

        if (!string.IsNullOrWhiteSpace(_selectedAvatarDisplayName) &&
            !string.Equals(_selectedAvatarDisplayName, _selectedHeroDisplayName, StringComparison.OrdinalIgnoreCase))
        {
            return $"{_selectedAvatarDisplayName} • {_selectedHeroDisplayName} ({identity})";
        }

        return $"{_selectedHeroDisplayName} ({identity})";
    }

    private Party? BuildPartyOrNull(string? fallbackPartyId = null)
    {
        if (_streamerPrivacyMode)
            return null;

        string cleanName = CleanAccountName;
        if (_isHostedLobbyActive && _hostedLobbySize >= 1 && _hostedLobbyMax > 1)
        {
            int hostedMax = Math.Max(_hostedLobbySize, _hostedLobbyMax);
            string hostedId = !string.IsNullOrEmpty(fallbackPartyId)
                ? fallbackPartyId
                : string.IsNullOrEmpty(cleanName)
                    ? "lobby_hon"
                    : $"lobby_{cleanName}";

            return new Party
            {
                ID = hostedId,
                Size = _hostedLobbySize,
                Max = hostedMax,
                Privacy = Party.PrivacySetting.Public
            };
        }

        if (_partySize <= 1)
            return null;

        int safeMax = Math.Max(_partySize, _partyMax);
        string resolvedId = !string.IsNullOrEmpty(_partyId)
            ? _partyId
            : !string.IsNullOrEmpty(fallbackPartyId)
                ? fallbackPartyId
                : string.IsNullOrEmpty(cleanName)
                    ? "party_hon"
                    : $"party_{cleanName}";

        return new Party
        {
            ID = resolvedId,
            Size = _partySize,
            Max = safeMax,
            Privacy = Party.PrivacySetting.Public
        };
    }

    public void UpdateHostedLobbySlots(int currentPlayers, int maxPlayers)
    {
        _isHostedLobbyActive = maxPlayers > 1;
        _hostedLobbySize = Math.Max(1, currentPlayers);
        _hostedLobbyMax = Math.Max(_hostedLobbySize, maxPlayers);
        if (_currentPresence != null)
        {
            _currentPresence.Party = BuildPartyOrNull(_unredactedFallbackPartyId);
        }
        LogDebug($"[DiscordRPC] Hosted lobby slots updated: {_hostedLobbySize} of {_hostedLobbyMax} (Active={_isHostedLobbyActive})");
    }

    public void UpdateParty(int size, int max = 5, string? partyId = null)
    {
        _partySize = Math.Max(1, size);
        _partyMax = Math.Max(_partySize, max);
        if (!string.IsNullOrEmpty(partyId)) _partyId = partyId;
        if (_currentPresence != null)
        {
            _currentPresence.Party = BuildPartyOrNull(_unredactedFallbackPartyId);
        }
        LogDebug($"[DiscordRPC] Party updated: {_partySize} of {_partyMax} (ID: {_partyId ?? "none"})");
    }

    static DiscordPresenceService()
    {
        List<KeyValuePair<string, (string AssetKey, string DisplayName)>> entries = [.. HeroRegistry];
        foreach (KeyValuePair<string, (string AssetKey, string DisplayName)> entry in entries)
        {
            string display = entry.Value.DisplayName;
            string key = entry.Value.AssetKey;

            HeroRegistry[display] = (key, display);
            HeroRegistry[display.ToLowerInvariant()] = (key, display);
            HeroRegistry[display.Replace(" ", "").ToLowerInvariant()] = (key, display);
            HeroRegistry[display.Replace(" ", "_").ToLowerInvariant()] = (key, display);
            if (display.StartsWith("The ", StringComparison.OrdinalIgnoreCase))
            {
                string strippedThe = display.Substring(4);
                HeroRegistry[strippedThe] = (key, display);
                HeroRegistry[strippedThe.ToLowerInvariant()] = (key, display);
                HeroRegistry[strippedThe.Replace(" ", "").ToLowerInvariant()] = (key, display);
            }

            if (display.StartsWith("Lord ", StringComparison.OrdinalIgnoreCase))
            {
                string strippedLord = display.Substring(5);
                HeroRegistry[strippedLord] = (key, display);
                HeroRegistry[strippedLord.ToLowerInvariant()] = (key, display);
                HeroRegistry[strippedLord.Replace(" ", "").ToLowerInvariant()] = (key, display);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
        {
            return;
        }

        if (_processExitHandler != null)
        {
            try
            {
                AppDomain.CurrentDomain.ProcessExit -= _processExitHandler;
            }
            catch
            {
            }
            _processExitHandler = null;
        }

        if (_pollingCts != null)
        {
            try
            {
                if (!_pollingCts.IsCancellationRequested)
                {
                    _pollingCts.Cancel();
                }
            }
            catch
            {
            }

            try
            {
                _pollingCts.Dispose();
            }
            catch
            {
            }
            _pollingCts = null;
        }

        if (_hubConnection != null)
        {
            try
            {
                await _hubConnection.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[DISCORD_RPC] HubConnection.DisposeAsync warning: {ex.Message}");
            }
            _hubConnection = null;
        }

        if (_client != null)
        {
            try
            {
                _client.ClearPresence();
                if (_client is { IsDisposed: false, IsInitialized: true })
                {
                    _client.Invoke();
                    await Task.Delay(50).ConfigureAwait(false);
                }
            }
            catch
            {
            }

            try
            {
                _client.Dispose();
            }
            catch
            {
            }
            _client = null; _isDiscordIpcConnected = false; OnActivityUpdated?.Invoke(string.Empty, string.Empty);
        }

        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
#pragma warning disable RS0030 // Justification: Offloaded to thread-pool with bounded timeout so UI SynchronizationContext never deadlocks
        try
        {
            _ = Task.Run(async () => await DisposeAsync().ConfigureAwait(false)).Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[DISCORD_RPC] Synchronous Dispose notice: {ex.Message}");
        }
#pragma warning restore RS0030
        GC.SuppressFinalize(this);
    }

    public event Action<string>? OnPlayerIdentified;
    public event Action<string, string>? OnActivityUpdated;

    public static string StripClanTag(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        string trimmed = name.Trim();
        return Regex.Replace(trimmed, @"^\[[^\]]+\]\s*", string.Empty).Trim();
    }

    public static bool IsPlaceholderPlayerName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return true;

        string stripped = StripClanTag(name);
        return string.IsNullOrWhiteSpace(stripped) ||
               stripped.Equals("Cheater", StringComparison.OrdinalIgnoreCase) ||
               stripped.Equals("UnnamedNewbie", StringComparison.OrdinalIgnoreCase) ||
               stripped.Equals("KONGOR Player", StringComparison.OrdinalIgnoreCase) ||
               stripped.Equals("Kongor", StringComparison.OrdinalIgnoreCase) ||
               stripped.Equals("KongorSrv", StringComparison.OrdinalIgnoreCase) ||
               stripped.Equals("false", StringComparison.OrdinalIgnoreCase) ||
               stripped.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               stripped.StartsWith("Player #", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsDisallowedPreloadedConfigName(string? name)
    {
        if (IsPlaceholderPlayerName(name))
            return true;

        string stripped = StripClanTag(name);
        return !string.IsNullOrWhiteSpace(Environment.UserName) &&
               stripped.Equals(Environment.UserName, StringComparison.OrdinalIgnoreCase);
    }

    public bool IsLocalPlayerName(string? candidate)
    {
        if (IsPlaceholderPlayerName(candidate))
            return false;

        if (string.IsNullOrEmpty(_accountName))
            return true;

        string cleanCandidate = StripClanTag(candidate);
        string cleanLocal = StripClanTag(_accountName);
        if (string.IsNullOrEmpty(cleanCandidate))
            return false;

        if (cleanCandidate.Equals(cleanLocal, StringComparison.OrdinalIgnoreCase))
            return true;

        // Shared-memory KongorTelemetryState.PlayerName[12] truncates "[CLAN]username" at 11 characters (e.g. "[GUCI]gucci").
        // When _accountName was seeded from non-authoritative FastBus/config, accept full local names sharing that truncated prefix.
        if (!_hasAuthoritativeAccountName &&
            (_isFastBusSeededName || _accountName.Length == 11) &&
            !string.IsNullOrEmpty(cleanLocal) &&
            cleanLocal.Length >= 3 &&
            cleanCandidate.Length > cleanLocal.Length &&
            cleanCandidate.StartsWith(cleanLocal, StringComparison.OrdinalIgnoreCase))
        {
            string trimmedCandidate = candidate!.Trim();
            if (!_accountName.StartsWith('[') ||
                !trimmedCandidate.StartsWith('[') ||
                trimmedCandidate.StartsWith(_accountName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public void SetAccountName(string? name, bool isAuthoritative = true)
    {
        if (IsPlaceholderPlayerName(name)) return;

        string trimmed = name!.Trim();
        if (!isAuthoritative && !string.IsNullOrEmpty(_accountName))
        {
            return;
        }

        string previousAccountName = _accountName;
        string previousCleanName = CleanAccountName;

        if (!string.IsNullOrEmpty(_accountName) &&
            _accountName.StartsWith('[') &&
            !trimmed.StartsWith('['))
        {
            string existingClean = StripClanTag(_accountName);
            if (existingClean.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
            {
                if (isAuthoritative)
                {
                    _hasAuthoritativeAccountName = true;
                    _isFastBusSeededName = false;
                }
                return;
            }

            // Preserve the clan tag if FastBus seeded "[GUCI]gucci" and bare "guccidonut" arrives next.
            int closeBracket = _accountName.IndexOf(']');
            if (!_hasAuthoritativeAccountName &&
                closeBracket > 0 &&
                !string.IsNullOrEmpty(existingClean) &&
                trimmed.Length > existingClean.Length &&
                trimmed.StartsWith(existingClean, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = _accountName[..(closeBracket + 1)] + trimmed;
            }
        }

        bool isNew = !string.Equals(_accountName, trimmed, StringComparison.OrdinalIgnoreCase);
        _accountName = trimmed;
        if (isAuthoritative)
        {
            _hasAuthoritativeAccountName = true;
            _isFastBusSeededName = false;
        }

        if (isNew)
        {
            if (!string.IsNullOrEmpty(previousAccountName))
            {
                LogDebug($"[DiscordRPC] Upgraded account identity '{previousAccountName}' -> '{_accountName}' (Clean='{CleanAccountName}', Authoritative={isAuthoritative}).");
            }

            OnPlayerIdentified?.Invoke(_accountName);

            if (_currentPresence != null && !string.IsNullOrEmpty(previousAccountName))
            {
                if (!string.IsNullOrEmpty(previousCleanName) &&
                    !string.Equals(previousCleanName, CleanAccountName, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrEmpty(_unredactedState))
                {
                    _unredactedState = ReplaceLeadingHandleInState(_unredactedState, previousCleanName, CleanAccountName);
                }

                if (!string.IsNullOrEmpty(_unredactedSmallText))
                {
                    _unredactedSmallText = _unredactedSmallText.Replace(previousAccountName, _accountName, StringComparison.OrdinalIgnoreCase);
                }

                ReapplyCurrentPresenceFromUnredacted();
            }
        }
    }

    private static string ReplaceLeadingHandleInState(string state, string oldClean, string newClean)
    {
        if (state.Equals(oldClean, StringComparison.OrdinalIgnoreCase))
            return newClean;
        if (state.StartsWith($"{oldClean} ", StringComparison.OrdinalIgnoreCase))
            return $"{newClean}{state[oldClean.Length..]}";
        return state;
    }

    private Button[] BuildActionButtons()
    {
        string primaryUrl = "https://kongor.net";

        if (!string.IsNullOrWhiteSpace(_masterServerUrl))
        {
            if (_masterServerUrl.Contains("newbs.io", StringComparison.OrdinalIgnoreCase))
                primaryUrl = "https://www.newbs.io/";
            else if (_masterServerUrl.Contains("kongor", StringComparison.OrdinalIgnoreCase) ||
                     _masterServerUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
                     _masterServerUrl.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase))
                primaryUrl = "https://kongor.net";
            else
                try
                {
                    string cleanUrl = _masterServerUrl.Trim();
                    if (!cleanUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                        !cleanUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        cleanUrl = $"https://{cleanUrl}";

                    if (Uri.TryCreate(cleanUrl, UriKind.Absolute, out Uri? uri) && !string.IsNullOrEmpty(uri.Host))
                    {
                        string host = uri.Host;
                        if (!IPAddress.TryParse(host, out _))
                        {
                            string[] parts = host.Split('.');
                            if (parts.Length >= 2)
                            {
                                string mainDomain = $"{parts[parts.Length - 2]}.{parts[parts.Length - 1]}";
                                primaryUrl = $"https://{mainDomain}/";
                            }
                            else
                            {
                                primaryUrl = $"https://{host}/";
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogDebug($"[DiscordRPC] Custom master server domain extraction error: {ex.Message}");
                    primaryUrl = "https://kongor.net";
                }
        }

        return
        [
            new Button { Label = "Play Project KONGOR", Url = primaryUrl },
            new Button { Label = "Discord Community", Url = "https://discord.com/invite/N6pKzGDqUH" }
        ];
    }

    private void LogDebug(string message)
    {
        string formatted = $"[{TimeProvider.System.GetUtcNow().UtcDateTime:yyyy-MM-dd HH:mm:ss.fff}] {message}";
        if (Environment.GetEnvironmentVariable("KONGOR_CONSOLE_LOGS") == "true") Console.WriteLine(formatted);
        try
        {
            string? dir = Path.GetDirectoryName(LogFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(LogFilePath, formatted + Environment.NewLine);
        }
        catch
        {
            System.Diagnostics.Trace.WriteLine("[DISCORD_RPC] Primary log append skipped due to transient file lock.");
        }

        try
        {
            string localLog = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "discord_rpc_debug.log");
            File.AppendAllText(localLog, formatted + Environment.NewLine);
        }
        catch
        {
            System.Diagnostics.Trace.WriteLine("[DISCORD_RPC] Secondary log append skipped due to transient file lock.");
        }
    }

    public void HandleDiscordIpcClosed(string reason = "IPC closed")
    {
        _isDiscordIpcConnected = false;
        _lastSentDetails = string.Empty;
        _lastSentState = string.Empty;
        _lastSentLargeKey = string.Empty;
        _lastSentSmallKey = string.Empty;
        _lastSentTime = DateTime.MinValue;
        _ipcSendWindowUtc.Clear();
        LogDebug($"[DiscordRPC] Discord IPC disconnected ({reason}); cleared dedup cache for auto-resync.");
    }

    public void HandleDiscordIpcReady()
    {
        _isDiscordIpcConnected = true;
        _lastSentDetails = string.Empty;
        _lastSentState = string.Empty;
        _lastSentLargeKey = string.Empty;
        _lastSentSmallKey = string.Empty;
        _lastSentTime = DateTime.MinValue;
        _ipcSendWindowUtc.Clear();
        if (_currentPresence != null)
        {
            ReapplyCurrentPresenceFromUnredacted();
        }
    }

    public Task InitializeAsync(string? masterServerUrl, bool isMapEditor = false)
    {
        _isMapEditor = isMapEditor;
        bool isLocalOnlyMode = isMapEditor || string.IsNullOrWhiteSpace(masterServerUrl);
        _masterServerUrl = masterServerUrl ?? string.Empty;
        LogDebug(isMapEditor
            ? "Initializing Discord RPC Bridge for K2 Map Editor (local-only mode)..."
            : isLocalOnlyMode
                ? "Initializing Discord RPC Bridge in local-only mode (Offline Hub / P2P; SignalR disabled)..."
                : $"Initializing Discord RPC Bridge for master server '{masterServerUrl}'...");

        if (!isLocalOnlyMode)
        {
            // Ensure URL is well-formed
            if (!masterServerUrl!.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !masterServerUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                masterServerUrl = masterServerUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
                                  masterServerUrl.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                    ? $"http://{masterServerUrl}"
                    : $"https://{masterServerUrl}";
        }

        string activeAppId = ActiveDiscordAppId;
        LogDebug($"[DiscordRPC] Binding Discord IPC client with Application ID '{activeAppId}' (StreamerPrivacyMode={_streamerPrivacyMode}, MapEditor={isMapEditor}).");
        _client = new DiscordRpcClient(activeAppId, autoEvents: false)
        {
            Logger = new CustomDiscordLogger(LogDebug) { Level = LogLevel.Trace }
        };
        // Static community buttons only (RegisterUriScheme & dead OnJoin stubs removed per Phase 6 alignment)

        _client.OnReady += (sender, e) =>
        {
            string username = e?.User?.Username ?? "User";
            LogDebug($"[DiscordRPC] READY! Connected to Discord IPC for user: {username} (Version: {e?.Version ?? 1})");
            HandleDiscordIpcReady();
        };

        _client.OnError += (sender, e) => { LogDebug($"[DiscordRPC] ERROR ({e.Code}): {e.Message}"); };

        _client.OnConnectionFailed += (sender, e) =>
        {
            LogDebug($"[DiscordRPC] CONNECTION FAILED on pipe {e.FailedPipe}!");
            HandleDiscordIpcClosed($"FailedPipe {e.FailedPipe}");
        };

        _client.OnPresenceUpdate += (sender, e) =>
        {
            LogDebug(
                $"[DiscordRPC] Discord ACKNOWLEDGED Presence Update! State='{e.Presence?.State}', Details='{e.Presence?.Details}'");
        };

        _client.OnClose += (sender, e) =>
        {
            LogDebug($"[DiscordRPC] Connection CLOSED ({e.Code}): {e.Reason}");
            HandleDiscordIpcClosed($"{e.Code}: {e.Reason}");
        };



        _processExitHandler = (s, e) => Dispose();
        AppDomain.CurrentDomain.ProcessExit += _processExitHandler;

        _client.Initialize();

        // Preload saved account name from login.cfg / startup.cfg / environment if available
        TryPreloadSavedAccountName();

        string initialTooltipAccount = !string.IsNullOrEmpty(_accountName) ? _accountName : "Player";
        string cleanAccount = CleanAccountName;
        string initialDetails = isMapEditor ? "In Map Editor" : "In Main Menu";
        string initialLargeText = isMapEditor ? "K2 World Editor" : "Heroes of Newerth";
        string initialContext = isMapEditor ? "World Editor" : isLocalOnlyMode ? "Offline Hub" : "In Lobby";
        string initialState = !string.IsNullOrEmpty(cleanAccount)
            ? $"{cleanAccount} ({initialContext})"
            : isMapEditor
                ? "World Editor"
                : isLocalOnlyMode
                    ? "Player (Offline Hub)"
                    : "In Lobby";

        // Set default launch Rich Presence ("In Main Menu" or "In Map Editor")
        SetPresenceInternal(new RichPresence
        {
            Details = initialDetails,
            State = initialState,
            Assets = new Assets
            {
                LargeImageKey = "large",
                LargeImageText = initialLargeText,
                SmallImageKey = "small",
                SmallImageText = initialTooltipAccount
            },
            Party = isMapEditor ? null : BuildPartyOrNull(),
            Buttons = BuildActionButtons(),
            Timestamps = Timestamps.Now
        });

        // Start polling console.log for Account ID and sync live Discord Portal asset manifest in background
        _pollingCts = new CancellationTokenSource();
        _ = RefreshPortalAssetManifestAsync(activeAppId, _pollingCts.Token);
        if (!isMapEditor)
        {
            _ = PollForAccountIdAsync(_pollingCts.Token);
        }

        // Start background event pump to ensure Discord RPC messages and IPC frames are dispatched reliably under Native AOT
        _ = Task.Run(async () =>
        {
            while (!_pollingCts.Token.IsCancellationRequested)
            {
                try
                {
                    FlushPendingPresenceIfReady();
                    if (_client is { IsDisposed: false, IsInitialized: true }) _client.Invoke();
                }
                catch (Exception ex)
                {
                    LogDebug($"[DiscordRPC] Event pump error: {ex.Message}");
                }

                await Task.Delay(100, _pollingCts.Token);
            }
        });

        if (isLocalOnlyMode)
        {
            return Task.CompletedTask;
        }

        // Connect to SignalR Presence Hub asynchronously in background with candidate endpoints and retry loop
        string[] candidateHubUrls =
        [
            $"{masterServerUrl!.TrimEnd('/')}/hubs/presence",
            "http://127.0.0.1:55555/hubs/presence",
            "https://localhost:5555/hubs/presence",
            "http://localhost:55555/hubs/presence"
        ];

        _ = Task.Run(async () =>
        {
            CancellationToken pollToken = _pollingCts.Token;
            while (!pollToken.IsCancellationRequested && string.IsNullOrWhiteSpace(Volatile.Read(ref _sessionCookie)))
            {
                try
                {
                    await Task.Delay(500, pollToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            foreach (string hubUrl in candidateHubUrls)
                try
                {
                    if (pollToken.IsCancellationRequested) break;
                    string activeCookie = Volatile.Read(ref _sessionCookie);
                    HubConnection candidateHub = CreatePresenceHubConnection(hubUrl, activeCookie);

                    await _hubSyncLock.WaitAsync(pollToken).ConfigureAwait(false);
                    try
                    {
                        if (_hubConnection is { State: HubConnectionState.Connected })
                        {
                            await candidateHub.DisposeAsync().ConfigureAwait(false);
                            break;
                        }

                        if (_hubConnection != null)
                        {
                            await _hubConnection.DisposeAsync().ConfigureAwait(false);
                        }

                        _hubConnection = candidateHub;
                        _activeHubUrl = hubUrl;
                        _connectedHubCookie = activeCookie;
                    }
                    finally
                    {
                        _hubSyncLock.Release();
                    }

                    // Background connection monitor for SignalR Presence Hub
                    while (!pollToken.IsCancellationRequested)
                    {
                        if (_hubConnection.State == HubConnectionState.Disconnected)
                            try
                            {
                                using CancellationTokenSource signalRCts = new(TimeSpan.FromSeconds(8));
                                using CancellationTokenSource linkedCts =
                                    CancellationTokenSource.CreateLinkedTokenSource(pollToken, signalRCts.Token);
                                LogDebug($"[DiscordRPC] Connecting to Presence Hub ({hubUrl})...");
                                await _hubConnection.StartAsync(linkedCts.Token).ConfigureAwait(false);
                                LogDebug($"[DiscordRPC] Connected to Presence Hub successfully at {hubUrl}!");
                                await EnsureSubscribedAsync(linkedCts.Token).ConfigureAwait(false);
                                break;
                            }
                            catch (Exception ex)
                            {
                                LogDebug(
                                    $"[DiscordRPC] Presence Hub connection to {hubUrl} failed: {ex.Message}. Trying next candidate...");
                                break;
                            }

                        await Task.Delay(3000, pollToken).ConfigureAwait(false);
                    }

                    if (_hubConnection is { State: HubConnectionState.Connected }) break;
                }
                catch (OperationCanceledException) when (pollToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogDebug($"[DiscordRPC] Presence Hub candidate builder error: {ex.Message}");
                }
        });

        return Task.CompletedTask;
    }

    internal HubConnection CreatePresenceHubConnection(string hubUrl, string? sessionCookie)
    {
        string? normalizedCookie = ExtractNormalizedSessionCookie(sessionCookie);
        HubConnection hub = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.Transports = HttpTransportType.WebSockets | HttpTransportType.ServerSentEvents |
                                     HttpTransportType.LongPolling;
                if (!string.IsNullOrWhiteSpace(normalizedCookie))
                {
                    options.Headers["X-Kongor-Cookie"] = normalizedCookie;
                }
                options.HttpMessageHandlerFactory = handler =>
                {
                    if (handler is HttpClientHandler clientHandler)
                        clientHandler.ServerCertificateCustomValidationCallback =
                            (sender, cert, chain, sslPolicyErrors) => true;
                    return handler;
                };
                options.WebSocketConfiguration = sockets =>
                {
                    sockets.RemoteCertificateValidationCallback =
                        (sender, cert, chain, sslPolicyErrors) => true;
                };
            })
            .WithAutomaticReconnect(
            [
                TimeSpan.Zero,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(30)
            ])
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.TypeInfoResolver = PresenceJsonContext.Default;
                options.PayloadSerializerOptions.PropertyNameCaseInsensitive = true;
            })
            .Build();

        hub.Reconnected += async connectionId =>
        {
            LogDebug($"[DiscordRPC] SignalR Reconnected (ConnectionId: {connectionId}). Re-subscribing...");
            _subscribedAccountId = 0;
            await EnsureSubscribedAsync().ConfigureAwait(false);
        };

        hub.On<PresenceDto>("PresenceUpdated", ProcessPresenceUpdate);
        return hub;
    }

    public void ProcessFastBusState(uint gamePhase, uint activeMatchId, string? currentMap, string? playerName)
    {
        uint previousPhase = _fastBusGamePhase;
        _hasLiveFastBus = true;
        _fastBusGamePhase = gamePhase;

        // Shared-memory KongorTelemetryState.PlayerName[12] holds at most 11 characters (e.g. "[GUCI]gucci").
        // Only seed _accountName when completely unknown; never overwrite an existing full account name!
        if (!string.IsNullOrWhiteSpace(playerName) && string.IsNullOrEmpty(_accountName))
        {
            SetAccountName(playerName, isAuthoritative: false);
            if (!string.IsNullOrEmpty(_accountName))
            {
                _isFastBusSeededName = true;
                LogDebug($"[DiscordRPC] Seeded provisional FastBus player prefix '{_accountName}' (awaiting full log/pipe identity).");
            }
        }

        if (!string.IsNullOrWhiteSpace(currentMap) && !(_isMenuBootPreloadActive && gamePhase == 1))
        {
            _currentGameMode = DetectGameModeFromLog(currentMap, _currentGameMode);
        }

        // Native shm_telemetry.cpp canonical phases:
        // 0 = Main Menu / Lobby, 1 = Loading Map, 2 = Hero Selection, 3 = Active Match / In-Game, 4 = Post-Game Summary.
        // Also honor legacy (phase 1 with matchId=0 & empty map) after >=2 as Main Menu exit.
        bool isLegacyPhase1MenuExit = gamePhase == 1 &&
                                      previousPhase >= 2 &&
                                      activeMatchId == 0 &&
                                      string.IsNullOrWhiteSpace(currentMap);
        if (gamePhase == 0 || isLegacyPhase1MenuExit || gamePhase == 4)
        {
            if (previousPhase >= 1 || gamePhase == 4)
            {
                ClearHostedLobbyState($"FastBus {previousPhase}->{gamePhase}");
                DateTime nowUtc = TimeProvider.System.GetUtcNow().UtcDateTime;
                bool preserveQueue = ShouldPreserveContinuousQueueOnArcadeExit(nowUtc);
                SetQueueArcadeActive(false, $"FastBus phase {previousPhase}->{gamePhase}", preserveRotationTimer: preserveQueue);
                _currentMatchMode = null;
                ClearHeroSelectionState();
                _localPlayerSlot = null;
                _matchStartUtc = null;
                if (preserveQueue && _queueStartUtc.HasValue)
                {
                    _recentArcadeQueueStartUtc ??= _queueStartUtc;
                    _recentArcadeDisconnectUtc = nowUtc;
                    _currentGameMode = _queuedGameMode;
                    string queueModeSuffix = !string.IsNullOrWhiteSpace(_currentGameMode) ? $" ({_currentGameMode})" : string.Empty;
                    string cleanQueueName = CleanAccountName;
                    string queueState = string.IsNullOrEmpty(cleanQueueName) ? "Finding Match..." : $"{cleanQueueName} in Queue";
                    UpdateLocalPresence($"In Matchmaking{queueModeSuffix}", queueState, "small", BuildPlayerTooltipIdentity("Player"));
                    LogDebug($"[DiscordRPC] FastBus phase {previousPhase} -> {gamePhase}: left Queue Arcade warm-up while preserving queued mode '{_currentGameMode}' & queue timer ({_queueStartUtc:O}).");
                    return;
                }

                ClearQueueState($"FastBus phase {previousPhase}->{gamePhase}");
                _currentGameMode = string.Empty;
                string smallTip = BuildPlayerTooltipIdentity("Player");
                string cleanName = CleanAccountName;
                string lobbyState = string.IsNullOrEmpty(cleanName) ? "In Lobby" : $"{cleanName} (In Lobby)";
                UpdateLocalPresence("In Main Menu", lobbyState, "small", smallTip);
                LogDebug($"[DiscordRPC] FastBus phase {previousPhase} -> {gamePhase}: returned to Main Menu.");
            }
            return;
        }

        if (gamePhase == 1)
        {
            if (_isMenuBootPreloadActive)
            {
                bool preloadExpired = _menuBootPreloadStartUtc.HasValue &&
                                      (TimeProvider.System.GetUtcNow() - _menuBootPreloadStartUtc.Value) > TimeSpan.FromSeconds(45);
                if (!preloadExpired)
                {
                    return;
                }
                _isMenuBootPreloadActive = false;
                _menuBootPreloadStartUtc = null;
            }

            ClearHostedLobbyState("FastBus Loading (phase 1)");
            if (!_isQueueArcadeActive)
            {
                if (_queueStartUtc.HasValue && activeMatchId == 0)
                {
                    _recentArcadeQueueStartUtc ??= _queueStartUtc;
                    _recentArcadeDisconnectUtc = TimeProvider.System.GetUtcNow().UtcDateTime;
                }
                _queueStartUtc = null;
            }
            string modeSuffix = !string.IsNullOrWhiteSpace(_currentGameMode) ? $" ({_currentGameMode})" : string.Empty;
            string cleanName = CleanAccountName;
            string smallTip = BuildPlayerTooltipIdentity("Player");
            string joinState = string.IsNullOrEmpty(cleanName) ? "Connecting to Server" : $"{cleanName} (Connecting)";
            UpdateLocalPresence($"Joining Game{modeSuffix}...", joinState, "small", smallTip);
            return;
        }

        if (gamePhase == 2)
        {
            ClearHostedLobbyState("FastBus Hero Selection (phase 2)");
            if (!_isQueueArcadeActive)
            {
                if (_queueStartUtc.HasValue && activeMatchId == 0)
                {
                    _recentArcadeQueueStartUtc ??= _queueStartUtc;
                    _recentArcadeDisconnectUtc = TimeProvider.System.GetUtcNow().UtcDateTime;
                }
                _queueStartUtc = null;
                if (activeMatchId > 0)
                {
                    ClearQueueState($"FastBus HeroSelect matchId={activeMatchId}");
                }
            }
            string modeSuffix = !string.IsNullOrWhiteSpace(_currentGameMode) ? $" ({_currentGameMode})" : string.Empty;
            string cleanName = CleanAccountName;
            string smallTip = !string.IsNullOrEmpty(_selectedHeroDisplayName)
                ? BuildHeroSmallTooltip("Hero Selection")
                : BuildPlayerTooltipIdentity("Player");
            string stateMsg = !string.IsNullOrEmpty(_selectedHeroDisplayName)
                ? (string.IsNullOrEmpty(cleanName) ? $"Playing as {_selectedHeroDisplayName}" : $"{cleanName} as {_selectedHeroDisplayName}")
                : (string.IsNullOrEmpty(cleanName) ? "Hero Selection" : $"{cleanName} (Hero Selection)");
            UpdateLocalPresence($"Picking Hero{modeSuffix}", stateMsg, _selectedHeroAssetKey ?? "small", smallTip);
            return;
        }

        // Canonical phase 3 (In-Game) and legacy >= 5 callers
        if (gamePhase == 3 || gamePhase >= 5)
        {
            ClearHostedLobbyState($"FastBus InGame (phase {gamePhase})");
            if (!_isQueueArcadeActive)
            {
                if (activeMatchId > 0)
                {
                    ClearQueueState($"FastBus InGame matchId={activeMatchId}");
                }
                else
                {
                    _queueStartUtc = null;
                }
                _matchStartUtc ??= TimeProvider.System.GetUtcNow().UtcDateTime;
            }
            if (string.IsNullOrEmpty(_currentGameMode))
            {
                _currentGameMode = "Forests of Caldavar";
            }

            string cleanName = CleanAccountName;
            string stateMsg = !string.IsNullOrEmpty(_selectedHeroDisplayName)
                ? (string.IsNullOrEmpty(cleanName) ? $"Playing as {_selectedHeroDisplayName}" : $"{cleanName} as {_selectedHeroDisplayName}")
                : (string.IsNullOrEmpty(cleanName) ? "In Match" : $"{cleanName} (In Match)");
            if (!string.IsNullOrEmpty(_lastTelemetryLevel))
            {
                stateMsg += $" ({_lastTelemetryLevel})";
            }
            string detailsMsg = !string.IsNullOrEmpty(_lastTelemetryKda)
                ? $"Playing {_currentGameMode} • {_lastTelemetryKda}"
                : $"Playing {_currentGameMode}";
            string smallTip = BuildHeroSmallTooltip("In Match");
            UpdateLocalPresence(detailsMsg, stateMsg, _selectedHeroAssetKey ?? "small", smallTip);
        }
    }

    public void ProcessPresenceUpdate(PresenceDto? presenceData)
    {
        if (presenceData == null) return;

        // 3-Tier Account Isolation: never let a foreign player's SignalR broadcast stomp local Rich Presence
        if (IsValidOnlineAccountId(_accountId) && IsValidOnlineAccountId(presenceData.AccountId) && presenceData.AccountId != _accountId)
        {
            LogDebug($"[DiscordRPC] Ignored foreign PresenceDto for Account {presenceData.AccountId} (Local Account: {_accountId}).");
            return;
        }

        if (!IsValidOnlineAccountId(_accountId) &&
            !string.IsNullOrEmpty(_accountName) &&
            !string.IsNullOrWhiteSpace(presenceData.SmallImageText) &&
            !IsPlaceholderPlayerName(presenceData.SmallImageText) &&
            !IsLocalPlayerName(presenceData.SmallImageText) &&
            !presenceData.SmallImageText.Contains(StripClanTag(_accountName), StringComparison.OrdinalIgnoreCase))
        {
            LogDebug($"[DiscordRPC] Ignored foreign PresenceDto for '{presenceData.SmallImageText}' (Local Player: '{_accountName}').");
            return;
        }

        LogDebug(
            $"[DiscordRPC] Received Instant Presence Update for Account {presenceData.AccountId}: {presenceData.State} - {presenceData.Details}");

        if (IsValidOnlineAccountId(presenceData.AccountId) && !IsValidOnlineAccountId(_accountId)) _accountId = presenceData.AccountId;

        if (!string.IsNullOrWhiteSpace(presenceData.SmallImageText) &&
            (string.IsNullOrEmpty(_accountName) || IsLocalPlayerName(presenceData.SmallImageText)))
        {
            SetAccountName(presenceData.SmallImageText);
        }

        if (!string.IsNullOrEmpty(presenceData.Details) ||
            !string.IsNullOrEmpty(presenceData.State) ||
            !string.IsNullOrEmpty(presenceData.LargeImageKey))
            _currentGameMode = DetectGameModeFromLog(
                $"{presenceData.Details} {presenceData.State} {presenceData.LargeImageKey} {presenceData.LargeImageText}",
                _currentGameMode);

        if (!string.IsNullOrEmpty(presenceData.SmallImageKey) &&
            ValidAssetKeys.Contains(presenceData.SmallImageKey) &&
            presenceData.SmallImageKey.StartsWith("hero_", StringComparison.OrdinalIgnoreCase))
        {
            _selectedHeroAssetKey = presenceData.SmallImageKey;
            (string AssetKey, string DisplayName)? heroInfo =
                ResolveHero(presenceData.SmallImageKey);
            if (heroInfo.HasValue) _selectedHeroDisplayName = heroInfo.Value.DisplayName;
        }

        // Track party and queue timing from SignalR updates
        if (presenceData.PartySize.HasValue && presenceData.PartySize.Value > 0)
        {
            _partySize = presenceData.PartySize.Value;
        }
        if (presenceData.PartyMax.HasValue && presenceData.PartyMax.Value > 0)
        {
            _partyMax = Math.Max(_partySize, presenceData.PartyMax.Value);
        }
        if (!string.IsNullOrEmpty(presenceData.PartyId))
        {
            _partyId = presenceData.PartyId;
        }
        if (presenceData.StartTimestamp.HasValue && presenceData.State?.Contains("Queue", StringComparison.OrdinalIgnoreCase) == true)
        {
            _queueStartUtc ??= DateTimeOffset.FromUnixTimeSeconds(presenceData.StartTimestamp.Value).UtcDateTime;
        }

        bool isJoining = !string.IsNullOrEmpty(presenceData.State) &&
                         presenceData.State.Contains("Joining", StringComparison.OrdinalIgnoreCase);

        bool isInMatch = !string.IsNullOrEmpty(presenceData.State) &&
                         (presenceData.State.Contains("In a Game",
                              StringComparison.OrdinalIgnoreCase) ||
                          presenceData.State.Contains("Playing",
                              StringComparison.OrdinalIgnoreCase) ||
                          presenceData.State.Contains("Hero Selection",
                              StringComparison.OrdinalIgnoreCase) ||
                          presenceData.State.Contains("Banning",
                              StringComparison.OrdinalIgnoreCase));

        string? effectiveSmallKey;
        string effectiveSmallText;
        string effectiveState;
        string effectiveDetails;
        string modeSuffix = !string.IsNullOrWhiteSpace(_currentGameMode)
            ? $" ({_currentGameMode})"
            : string.Empty;

        string cleanName = CleanAccountName;
        bool signalRIndicatesQueue = presenceData.State?.Contains("Queue", StringComparison.OrdinalIgnoreCase) == true;
        bool signalRIndicatesArcade =
            signalRIndicatesQueue &&
            (presenceData.Details?.StartsWith("In Queue •", StringComparison.OrdinalIgnoreCase) == true ||
             presenceData.Details?.Contains("Queue Arcade", StringComparison.OrdinalIgnoreCase) == true);
        bool inConfirmedNonArcadeMatch = !_isQueueArcadeActive &&
                                         (!string.IsNullOrEmpty(_currentMatchMode) ||
                                          (_hasLiveFastBus && _fastBusGamePhase >= 2));

        if (signalRIndicatesArcade && !inConfirmedNonArcadeMatch)
        {
            SetQueueArcadeActive(true, $"SignalR PresenceUpdated ('{presenceData.Details}')");
            _currentMatchMode = null;
        }
        if (presenceData.StartTimestamp is > 0 && signalRIndicatesQueue)
        {
            DateTime serverQueueUtc = DateTimeOffset.FromUnixTimeSeconds(presenceData.StartTimestamp.Value).UtcDateTime;
            if (!_queueStartUtc.HasValue || serverQueueUtc < _queueStartUtc.Value)
            {
                _queueStartUtc = serverQueueUtc;
            }
        }
        if ((presenceData.State?.Equals("Online in Menus", StringComparison.OrdinalIgnoreCase) == true ||
             presenceData.State?.Equals("Offline", StringComparison.OrdinalIgnoreCase) == true) &&
            _fastBusGamePhase <= 1)
        {
            if (_isQueueArcadeActive)
            {
                SetQueueArcadeActive(false, $"SignalR '{presenceData.State}'");
            }
            ClearQueueState($"SignalR '{presenceData.State}'");
        }

        if (_isQueueArcadeActive)
        {
            _unredactedFallbackPartyId = string.IsNullOrEmpty(presenceData.PartyId)
                ? $"party_{presenceData.AccountId}"
                : presenceData.PartyId;
            string arcadeQueueDetails = !string.IsNullOrWhiteSpace(_currentGameMode)
                ? $"In Queue • {_currentGameMode}"
                : "In Queue (Queue Arcade)";
            string arcadeQueueState = !string.IsNullOrEmpty(_selectedHeroDisplayName)
                ? (string.IsNullOrEmpty(cleanName) ? $"In Queue as {_selectedHeroDisplayName}" : $"{cleanName} as {_selectedHeroDisplayName} (In Queue)")
                : (string.IsNullOrEmpty(cleanName) ? "Finding Match..." : $"{cleanName} in Queue");
            UpdateLocalPresence(arcadeQueueDetails, arcadeQueueState, _selectedHeroAssetKey ?? "small");
            return;
        }

        if (isJoining)
        {
            effectiveSmallKey = "small";
            effectiveSmallText =
                !string.IsNullOrEmpty(_accountName) ? _accountName : "UnnamedNewbie";
            effectiveState = !string.IsNullOrEmpty(cleanName)
                ? $"{cleanName} (Connecting)"
                : "Connecting to Server";
            effectiveDetails = $"Joining Game{modeSuffix}...";
        }
        else if (isInMatch)
        {
            if (!_isQueueArcadeActive &&
                presenceData.StartTimestamp.HasValue && presenceData.StartTimestamp.Value > 0 &&
                (presenceData.State?.Contains("In a Game", StringComparison.OrdinalIgnoreCase) == true ||
                 presenceData.State?.Contains("Playing", StringComparison.OrdinalIgnoreCase) == true))
            {
                _matchStartUtc ??= DateTimeOffset.FromUnixTimeSeconds(presenceData.StartTimestamp.Value).UtcDateTime;
                if (!_isQueueArcadeActive) _queueStartUtc = null;
            }

            bool signalRReportsActiveGameplay =
                presenceData.State?.Contains("In a Game", StringComparison.OrdinalIgnoreCase) == true ||
                presenceData.State?.Contains("Playing", StringComparison.OrdinalIgnoreCase) == true ||
                _fastBusGamePhase == 3 ||
                _fastBusGamePhase >= 5 ||
                _matchStartUtc.HasValue;

            effectiveSmallKey = !string.IsNullOrEmpty(presenceData.SmallImageKey) &&
                                presenceData.SmallImageKey.StartsWith("hero_", StringComparison.OrdinalIgnoreCase) &&
                                ValidAssetKeys.Contains(presenceData.SmallImageKey)
                ? presenceData.SmallImageKey
                : _selectedHeroAssetKey ?? "small";

            effectiveSmallText = !string.IsNullOrEmpty(_selectedHeroDisplayName)
                ? BuildHeroSmallTooltip(!string.IsNullOrEmpty(presenceData.SmallImageText) ? presenceData.SmallImageText : "UnnamedNewbie")
                : !string.IsNullOrEmpty(_accountName)
                    ? _accountName
                    : !string.IsNullOrEmpty(presenceData.SmallImageText)
                        ? presenceData.SmallImageText
                        : "UnnamedNewbie";

            if (!string.IsNullOrEmpty(_selectedHeroDisplayName))
            {
                effectiveState = !string.IsNullOrEmpty(cleanName)
                    ? $"{cleanName} as {_selectedHeroDisplayName}"
                    : $"Playing as {_selectedHeroDisplayName}";
                if (!string.IsNullOrEmpty(_lastTelemetryLevel))
                {
                    effectiveState += $" ({_lastTelemetryLevel})";
                }
            }
            else if (signalRReportsActiveGameplay)
            {
                effectiveState = !string.IsNullOrEmpty(cleanName)
                    ? $"{cleanName} (In Match)"
                    : "In Match";
            }
            else
            {
                effectiveState = !string.IsNullOrEmpty(cleanName)
                    ? $"{cleanName} (Hero Selection)"
                    : "Hero Selection";
            }

            if (!string.IsNullOrEmpty(_selectedHeroDisplayName) || signalRReportsActiveGameplay)
            {
                effectiveDetails = !string.IsNullOrEmpty(_currentGameMode)
                    ? $"Playing {_currentGameMode}"
                    : "Playing in Match";
                if (!string.IsNullOrEmpty(_lastTelemetryKda))
                {
                    effectiveDetails += $" • {_lastTelemetryKda}";
                }
            }
            else
            {
                effectiveDetails = !string.IsNullOrEmpty(_currentGameMode)
                    ? $"Picking Hero ({_currentGameMode})"
                    : "Picking Hero";
            }
        }
        else
        {
            // If locally in an active Practice or Bot Match, or if a hero/map is active, do not let an idle SignalR heartbeat stomp over it
            if (!string.IsNullOrEmpty(_currentMatchMode) ||
                !string.IsNullOrEmpty(_selectedHeroDisplayName) ||
                (_hasLiveFastBus ? _fastBusGamePhase >= 2 : _fastBusGamePhase >= 3))
            {
                LogDebug($"[DiscordRPC] Suppressed idle SignalR lobby heartbeat while active in {_currentMatchMode ?? "Match"} (Hero: {_selectedHeroDisplayName ?? "None"}, Phase: {_fastBusGamePhase}).");
                return;
            }

            if (presenceData.State?.Equals("Offline", StringComparison.OrdinalIgnoreCase) == true &&
                presenceData.Details?.Equals("Logged Out", StringComparison.OrdinalIgnoreCase) == true)
            {
                _hasAuthoritativeOnlineAccountId = false;
                _accountId = 0;
            }

            if (signalRIndicatesQueue)
            {
                if (!string.IsNullOrWhiteSpace(_currentGameMode))
                {
                    _queuedGameMode = _currentGameMode;
                }
            }
            else
            {
                ClearQueueState($"SignalR '{presenceData.State ?? "MainMenu"}'");
                _currentGameMode = string.Empty;
            }
            effectiveSmallKey = "small";
            effectiveSmallText = !string.IsNullOrEmpty(_accountName) ? _accountName : "In Lobby";
            effectiveState = string.IsNullOrEmpty(presenceData.State) ||
                             presenceData.State.Equals("Online in Menus",
                                 StringComparison.OrdinalIgnoreCase)
                ? !string.IsNullOrEmpty(cleanName) ? $"{cleanName} (In Lobby)" : "In Lobby"
                : !string.IsNullOrEmpty(cleanName)
                    ? $"{cleanName} ({presenceData.State})"
                    : presenceData.State;
            effectiveDetails = !string.IsNullOrEmpty(presenceData.Details) &&
                               !presenceData.Details.Equals("Idle", StringComparison.OrdinalIgnoreCase)
                ? presenceData.Details
                : "In Main Menu";
        }

        string fallbackPartyId = string.IsNullOrEmpty(presenceData.PartyId)
            ? $"party_{presenceData.AccountId}"
            : presenceData.PartyId;
        _unredactedFallbackPartyId = fallbackPartyId;

        bool isLobbyMenu = effectiveDetails.Equals("In Main Menu", StringComparison.OrdinalIgnoreCase) ||
                           effectiveDetails.Equals("Idle", StringComparison.OrdinalIgnoreCase);
        string mapKeyFromMode = isLobbyMenu ? "large" : GetMapAssetKeyFromGameMode(_currentGameMode);
        string effectiveLargeKey = isLobbyMenu
            ? "large"
            : mapKeyFromMode != "large"
                ? mapKeyFromMode
                : !string.IsNullOrEmpty(presenceData.LargeImageKey) &&
                  ValidAssetKeys.Contains(presenceData.LargeImageKey) &&
                  !presenceData.LargeImageKey.Equals("large", StringComparison.OrdinalIgnoreCase)
                    ? presenceData.LargeImageKey
                    : "large";

        SetPresenceInternal(new RichPresence
        {
            State = effectiveState,
            Details = effectiveDetails,
            Assets = new Assets
            {
                LargeImageKey = effectiveLargeKey,
                LargeImageText = !isLobbyMenu && !string.IsNullOrEmpty(_currentGameMode)
                    ? _currentGameMode
                    : "Heroes of Newerth",
                SmallImageKey = effectiveSmallKey,
                SmallImageText = effectiveSmallText
            },
            Party = BuildPartyOrNull(
                string.IsNullOrEmpty(presenceData.PartyId)
                    ? $"party_{presenceData.AccountId}"
                    : presenceData.PartyId),
            Buttons = BuildActionButtons(),
            Timestamps = GetEffectiveTimestamps(presenceData.StartTimestamp)
        });
    }

    public static bool IsPickerDummyHero(string? rawHeroNameOrEntity)
    {
        if (string.IsNullOrWhiteSpace(rawHeroNameOrEntity))
            return true;

        string trimmed = rawHeroNameOrEntity.Trim();
        return trimmed.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("Hero Select", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("Hero_Select", StringComparison.OrdinalIgnoreCase) ||
               trimmed.EndsWith("_Dummy", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("Dummy", StringComparison.OrdinalIgnoreCase);
    }

    public static (string AssetKey, string DisplayName)? ResolveHero(string? rawHeroNameOrEntity)
    {
        if (IsPickerDummyHero(rawHeroNameOrEntity)) return null;

        string clean = rawHeroNameOrEntity!.Trim().Trim('"', '\'').TrimEnd('.');

        // Strip pipe/colon suffixes if present (e.g. "Hero_Bubbles.Alt16|0" or "Hero_Bubbles:Alt2")
        int pipeIdx = clean.IndexOf('|');
        if (pipeIdx > 0) clean = clean[..pipeIdx].Trim();
        int colonIdx = clean.IndexOf(':');
        if (colonIdx > 0) clean = clean[..colonIdx].Trim();

        // Strip cosmetic store prefixes ("aa.Hero_Mimix.Alt", "h.Hero_Bubbles", "Avatar_Bubbles")
        if (clean.StartsWith("aa.", StringComparison.OrdinalIgnoreCase))
            clean = clean[3..];
        else if (clean.StartsWith("h.", StringComparison.OrdinalIgnoreCase))
            clean = clean[2..];
        else if (clean.StartsWith("Avatar_", StringComparison.OrdinalIgnoreCase))
            clean = clean[7..];

        // Strip map/mod namespace prefixes on cloned standard heroes (e.g. "arena_Hero_Engineer" -> "Hero_Engineer")
        clean = Regex.Replace(clean, @"^[A-Za-z0-9]+_Hero_", "Hero_", RegexOptions.IgnoreCase);

        // Strip Alt Avatar / skin dot-modifiers ("Hero_Wolfman.pog_skin", "Hero_Bubbles.Alt16", "bubbles.alt2")
        int dotIdx = clean.IndexOf('.');
        if (dotIdx > 0)
        {
            clean = clean[..dotIdx].Trim();
        }

        if (IsPickerDummyHero(clean)) return null;

        if (clean.Equals("Hero_Succubus", StringComparison.OrdinalIgnoreCase) ||
            clean.Equals("Succubus", StringComparison.OrdinalIgnoreCase))
        {
            return ("hero_succubis", "Succubus");
        }

        if (HeroRegistry.TryGetValue(clean, out (string AssetKey, string DisplayName) info)) return info;

        if (clean.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase))
        {
            string stripped = clean.Substring(5);
            if (HeroRegistry.TryGetValue(stripped, out info)) return info;
        }

        string normalized = clean.Replace(" ", "").Replace("_", "").Replace("-", "").ToLowerInvariant();
        if (HeroRegistry.TryGetValue(normalized, out info)) return info;
        if (normalized.StartsWith("hero") && HeroRegistry.TryGetValue(normalized.Substring(4), out info)) return info;
        if (normalized.StartsWith("the") && HeroRegistry.TryGetValue(normalized.Substring(3), out info)) return info;
        if (normalized.StartsWith("lord") && HeroRegistry.TryGetValue(normalized.Substring(4), out info)) return info;

        foreach (KeyValuePair<string, (string AssetKey, string DisplayName)> kv in HeroRegistry)
            if (kv.Value.DisplayName.Equals(clean, StringComparison.OrdinalIgnoreCase) ||
                kv.Value.DisplayName.Replace(" ", "").Replace("-", "").Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                kv.Key.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                return kv.Value;

        return null;
    }

    public static (string AssetKey, string DisplayName)? TryDetectHeroFromLog(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        // 1. Look for explicit Hero_ tokens including Alt Avatar dot-suffixes (e.g. "Hero_Wolfman.pog_skin", "aa.Hero_Bubbles.Alt16", "SpawnHero Hero_Pyromancer")
        Match heroMatch = Regex.Match(line, @"(?:\baa\.)?\b(Hero_[A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)?)\b", RegexOptions.IgnoreCase);
        if (heroMatch.Success)
        {
            string rawName = heroMatch.Groups[1].Value;
            (string AssetKey, string DisplayName)? resolved = ResolveHero(rawName);
            if (resolved.HasValue) return resolved;
        }

        // 2. Look for hero_id or hero key-value logs (e.g. "hero_id, STRING: Valkyrie", "hero_cli_name, STRING: Hero_Rampage.Alt3", "picked_hero, STRING: Hero_Hiro")
        Match kvMatch = Regex.Match(line,
            @"(?:hero_id|hero_cli_name|hero_name|picked_hero|selected_hero|spawn_hero|hero)\s*,\s*(?:STRING:\s*)?(?:aa\.)?([A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)?)",
            RegexOptions.IgnoreCase);
        if (kvMatch.Success)
        {
            string rawName = kvMatch.Groups[1].Value;
            (string AssetKey, string DisplayName)? resolved = ResolveHero(rawName);
            if (resolved.HasValue) return resolved;
        }

        // 3. Look for explicit action phrases (e.g. "SpawnHero Valkyrie", "SelectHero Hero_Bubbles.Alt16", "Morph 0 Valkyrie")
        Match actionMatch = Regex.Match(line,
            @"(?:SpawnHero|SelectHero|Spawning|Selected\s+hero|Picked\s+hero|Morph\s+(?:\d+\s+)?)(?:aa\.)?([A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)?)", RegexOptions.IgnoreCase);
        if (actionMatch.Success)
        {
            string rawName = actionMatch.Groups[1].Value;
            (string AssetKey, string DisplayName)? resolved = ResolveHero(rawName);
            if (resolved.HasValue) return resolved;
        }

        // 4. Look for match combat ability / projectile patterns (e.g. Ability_Engineer1, Projectile_Engineer_Attack)
        Match abilityMatch = Regex.Match(line, @"(?:Ability|Projectile)_([A-Za-z0-9_]+?)(?:\d|_Attack|_)",
            RegexOptions.IgnoreCase);
        if (abilityMatch.Success)
        {
            string rawName = abilityMatch.Groups[1].Value;
            (string AssetKey, string DisplayName)? resolved = ResolveHero(rawName);
            if (resolved.HasValue) return resolved;
        }

        return null;
    }

    private string FormatDetailsForMatchMode(string details, string? state = null)
    {
        if (string.IsNullOrEmpty(_currentMatchMode))
            return details;

        // In Option B: Details line shows "Practice Match" or "Bot Match", while the Large Image tooltip shows the map.
        if (details.StartsWith("Picking Hero", StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrEmpty(state) && state.Contains("Hero Selection", StringComparison.OrdinalIgnoreCase)))
        {
            return $"{_currentMatchMode} (Hero Selection)";
        }

        if (details.StartsWith("Playing", StringComparison.OrdinalIgnoreCase) ||
            details.StartsWith("In Match", StringComparison.OrdinalIgnoreCase))
        {
            int bulletIdx = details.IndexOf(" • ", StringComparison.Ordinal);
            if (bulletIdx >= 0)
            {
                return $"{_currentMatchMode}{details.Substring(bulletIdx)}";
            }
            return _currentMatchMode;
        }

        if (details.StartsWith("Joining Game", StringComparison.OrdinalIgnoreCase))
        {
            return $"In {_currentMatchMode}";
        }

        if (details.StartsWith("Banning Phase", StringComparison.OrdinalIgnoreCase))
        {
            return $"{_currentMatchMode} (Banning)";
        }

        return details;
    }

    private void UpdateLocalPresence(string details, string state, string? smallKey = null, string? smallText = null)
    {
        bool isLobbyMenu = details.Equals("In Main Menu", StringComparison.OrdinalIgnoreCase) ||
                           details.Equals("Idle", StringComparison.OrdinalIgnoreCase);
        if (isLobbyMenu)
        {
            _currentMatchMode = null;
            _currentGameMode = string.Empty;
        }

        string effectiveDetails = FormatDetailsForMatchMode(details, state);
        string largeKey = isLobbyMenu ? "large" : GetMapAssetKeyFromGameMode(_currentGameMode);
        if (!isLobbyMenu && largeKey == "large" && !string.IsNullOrEmpty(_currentMatchMode))
        {
            largeKey = "map_caldavar";
        }
        string effectiveSmallKey = !string.IsNullOrEmpty(smallKey) && smallKey != "small"
            ? smallKey
            : _selectedHeroAssetKey ?? (!string.IsNullOrEmpty(smallKey) ? smallKey : "small");
        string effectiveSmallText = smallText ?? (!string.IsNullOrEmpty(_selectedHeroDisplayName)
            ? BuildHeroSmallTooltip("Player")
            : BuildPlayerTooltipIdentity("Player"));

        string largeText = !isLobbyMenu && !string.IsNullOrEmpty(_currentGameMode)
            ? (_isQueueArcadeActive ? $"Queue Arcade • {_currentGameMode}" : _currentGameMode)
            : (!isLobbyMenu && _isQueueArcadeActive
                ? "Queue Arcade"
                : (!isLobbyMenu && !string.IsNullOrEmpty(_currentMatchMode) ? "Forests of Caldavar" : "Heroes of Newerth"));

        if (!isLobbyMenu && _isQueueArcadeActive)
        {
            _currentMatchMode = null;
            EnsureQueueArcadeTimerAnchored();
            effectiveDetails = !string.IsNullOrWhiteSpace(_currentGameMode)
                ? $"In Queue • {_currentGameMode}"
                : "In Queue (Queue Arcade)";
            string cleanQueueName = CleanAccountName;
            state = !string.IsNullOrEmpty(_selectedHeroDisplayName)
                ? (string.IsNullOrEmpty(cleanQueueName)
                    ? $"In Queue as {_selectedHeroDisplayName}"
                    : $"{cleanQueueName} as {_selectedHeroDisplayName} (In Queue)")
                : (string.IsNullOrEmpty(cleanQueueName) ? "Finding Match..." : $"{cleanQueueName} in Queue");
            effectiveSmallKey = _selectedHeroAssetKey ?? "small";
            effectiveSmallText = !string.IsNullOrEmpty(_selectedHeroDisplayName)
                ? BuildHeroSmallTooltip("In Queue")
                : BuildPlayerTooltipIdentity("In Queue");
        }

        _unredactedFallbackPartyId ??= null;
        SetPresenceInternal(new RichPresence
        {
            State = state,
            Details = effectiveDetails,
            Assets = new Assets
            {
                LargeImageKey = largeKey,
                LargeImageText = largeText,
                SmallImageKey = effectiveSmallKey,
                SmallImageText = effectiveSmallText
            },
            Party = BuildPartyOrNull(_unredactedFallbackPartyId),
            Buttons = BuildActionButtons(),
            Timestamps = _isQueueArcadeActive && _queueStartUtc.HasValue
                ? new Timestamps { Start = _queueStartUtc.Value }
                : GetEffectiveTimestamps()
        });
    }

    private Timestamps GetEffectiveTimestamps(long? signalRStart = null)
    {
        if (_isQueueArcadeActive && _queueStartUtc.HasValue)
        {
            return new Timestamps { Start = _queueStartUtc.Value };
        }

        if (_matchStartUtc.HasValue)
        {
            return new Timestamps { Start = _matchStartUtc.Value };
        }

        if (_queueStartUtc.HasValue)
        {
            return new Timestamps { Start = _queueStartUtc.Value };
        }

        if (signalRStart.HasValue && signalRStart.Value > 0)
        {
            return new Timestamps
            {
                Start = DateTimeOffset.FromUnixTimeSeconds(signalRStart.Value).UtcDateTime
            };
        }

        return Timestamps.Now;
    }

    public static string GetMapAssetKeyFromGameMode(string modeOrMap)
    {
        if (string.IsNullOrWhiteSpace(modeOrMap)) return "large";
        if (IsDevConsoleLine(modeOrMap)) return "large";
        string lower = modeOrMap.ToLowerInvariant();
        if (ContainsNonGameplayAssetPath(lower))
            return "large";
        if ((lower.Contains(".skin") || lower.Contains("_skin")) && !lower.Contains("map") && !lower.Contains("mode"))
            return "large";

        // 11 Standalone Custom Maps (check specific substrings like truedevo and 15-char FastBus prefixes)
        if (lower.Contains("truedevo") || lower.Contains("true devo") || lower.Contains("true_devo"))
            return "map_truedevo";
        if (lower.Contains("colosseumduel") || lower.Contains("colosseum_duel") || lower.Contains("colosseum duel"))
            return "map_colosseumduel";
        if (lower.Contains("empathmaze") || lower.Contains("empath_maze") || lower.Contains("empath maze"))
            return "map_empathmaze";
        if (lower.Contains("nightmarishcoop") || lower.Contains("nightmarish_coop") || lower.Contains("nightmarish_coo") ||
            lower.Contains("nightmarish_004") || lower.Contains("nightmarish co-op") || lower.Contains("nightmarish coop"))
            return "map_nightmarishcoop";
        if (lower.Contains("pebblesduel") || lower.Contains("pebbles_duel") || lower.Contains("pebbles duel"))
            return "map_pebblesduel";
        if (lower.Contains("persianpass") || lower.Contains("persian_pass") || lower.Contains("persian pass"))
            return "map_persianpass";
        if (lower.Contains("threekingdoms") || lower.Contains("three_kingdoms") || lower.Contains("three kingdoms"))
            return "map_threekingdoms";
        if (lower.Contains("valkwars") || lower.Contains("valk_wars") || lower.Contains("valkyrie wars"))
            return "map_valkwars";
        if (Regex.IsMatch(lower, @"\b(?:map_archers|archers|archer_wars|archer wars)\b"))
            return "map_archers";
        if (Regex.IsMatch(lower, @"\b(?:map_enfos|enfos)\b") || lower.Contains("enfo's team survival") || lower.Contains("enfos team survival"))
            return "map_enfos";
        if (!lower.Contains("garena") && (lower.Contains("hon arena") || lower.Contains("map_arena") || Regex.IsMatch(lower, @"^(?:arena)$|\bmap[:=\s""']+arena\b")))
            return "map_arena";

        // 14 Official Maps + Variant Reuse (caldavar_*, midwars_*, mids_omg, devowars_*)
        if (lower.Contains("caldavar") || lower.Contains("forests of caldavar")) return "map_caldavar";
        if (Regex.IsMatch(lower, @"\b(?:capturetheflag|capture_the_flag|capture the flag|ctf)\b"))
            return "map_capturetheflag";
        if (lower.Contains("darkwoodvale") || lower.Contains("darkwood vale")) return "map_darkwoodvale";
        if (lower.Contains("devowars") || lower.Contains("devo wars") || lower.Contains("devo_wars"))
            return "map_devowars";
        if (lower.Contains("grimmscrossing") || lower.Contains("grimm's crossing") ||
            lower.Contains("grimms crossing") || lower.Contains("grimrnscrossing")) return "map_grimmscrossing";
        if (lower.Contains("midwars") || lower.Contains("mid wars") || lower.Contains("mid_wars") || lower.Contains("mids_omg"))
            return "map_midwars";
        if (lower.Contains("prophets") && !lower.Contains("hero_prophet")) return "map_prophets";
        if (lower.Contains("riftwars") || lower.Contains("rift wars") || lower.Contains("rift_wars"))
            return "map_riftwars";
        if (lower.Contains("spotlight")) return "map_spotlight";
        if ((lower.Contains("soccer") || lower.Contains("strikers")) && !lower.Contains("skin")) return "map_strikers";
        if (Regex.IsMatch(lower, @"\b(?:team_deathmatch|teamdeathmatch|team deathmatch|team death match|deathmatch|tdm)\b"))
            return "map_teamdeathmatch";
        if (lower.Contains("thegrimmhunt") || lower.Contains("the grimm hunt") || lower.Contains("grimm hunt") ||
            lower.Contains("grimmhunt")) return "map_thegrimmhunt";
        if (lower.Contains("tutorial")) return "map_tutorial";
        if (lower.Contains("watchtower")) return "map_watchtower";
        if (lower.Contains("practice") || lower.Contains("botmatch") || lower.Contains("bot_match") || lower.Contains("bot match"))
            return "map_caldavar";
        return "large";
    }

    private static string ExtractPhasePrefix(string? details)
    {
        if (string.IsNullOrWhiteSpace(details)) return string.Empty;
        int bulletIdx = details.IndexOf(" • ", StringComparison.Ordinal);
        return bulletIdx >= 0 ? details[..bulletIdx].Trim() : details.Trim();
    }

    private bool IsAssetAvailable(string key)
    {
        HashSet<string>? liveKeys = _livePortalAssetKeys;
        if (liveKeys is { Count: > 0 })
        {
            return liveKeys.Contains(key);
        }

        return ValidAssetKeys.Contains(key);
    }

    private async Task RefreshPortalAssetManifestAsync(string appId, CancellationToken token)
    {
        try
        {
            string url = $"https://discord.com/api/v10/oauth2/applications/{appId}/assets";
            using HttpResponseMessage response = await PortalAssetsHttpClient.GetAsync(url, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LogDebug($"[DiscordRPC] Portal asset manifest probe returned HTTP {(int)response.StatusCode} for AppId {appId}; using compiled ValidAssetKeys.");
                return;
            }

            string json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
                return;

            HashSet<string> discovered = new(StringComparer.OrdinalIgnoreCase);
            foreach (System.Text.Json.JsonElement el in doc.RootElement.EnumerateArray())
            {
                if (el.TryGetProperty("name", out System.Text.Json.JsonElement nameProp) &&
                    nameProp.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    string? assetName = nameProp.GetString();
                    if (!string.IsNullOrWhiteSpace(assetName))
                        discovered.Add(assetName);
                }
            }

            if (discovered.Count > 0)
            {
                _livePortalAssetKeys = discovered;
                LogDebug($"[DiscordRPC] Synced {discovered.Count} unique portal assets from Discord API for AppId {appId}.");
                if (_currentPresence != null)
                {
                    ReapplyCurrentPresenceFromUnredacted();
                }
            }
        }
        catch (Exception ex)
        {
            LogDebug($"[DiscordRPC] Portal asset manifest probe skipped ({ex.Message}); using compiled ValidAssetKeys.");
        }
    }

    private void EnsureQueueArcadeTimerAnchored()
    {
        if (_queueStartUtc.HasValue)
        {
            return;
        }

        DateTime nowUtc = TimeProvider.System.GetUtcNow().UtcDateTime;
        if (_recentArcadeQueueStartUtc.HasValue &&
            _recentArcadeDisconnectUtc.HasValue &&
            (nowUtc - _recentArcadeDisconnectUtc.Value) <= TimeSpan.FromSeconds(45))
        {
            _queueStartUtc = _recentArcadeQueueStartUtc;
            LogDebug($"[DiscordRPC] Restored continuous queue timer across Queue Arcade map rotation ({_queueStartUtc:O}).");
            return;
        }

        _queueStartUtc = nowUtc;
    }

    private string RedactStateForStreamerPrivacy(string? state)
    {
        if (string.IsNullOrWhiteSpace(state))
            return string.Empty;

        string cleanName = CleanAccountName;
        string candidate = state.Trim();
        if (!string.IsNullOrEmpty(cleanName))
        {
            string asPrefix = $"{cleanName} as ";
            if (candidate.StartsWith(asPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string heroRemainder = candidate[asPrefix.Length..];
                const string inQueueSuffix = " (In Queue)";
                if (heroRemainder.EndsWith(inQueueSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    return $"In Queue as {heroRemainder[..^inQueueSuffix.Length]}";
                }

                return $"Playing as {heroRemainder}";
            }

            if (candidate.Equals($"{cleanName} in Queue", StringComparison.OrdinalIgnoreCase))
            {
                return "Finding Match...";
            }

            if (candidate.Equals($"{cleanName} (Connecting)", StringComparison.OrdinalIgnoreCase))
            {
                return "Connecting to Server";
            }

            string parenPrefix = $"{cleanName} (";
            if (candidate.StartsWith(parenPrefix, StringComparison.OrdinalIgnoreCase) && candidate.EndsWith(')'))
            {
                return candidate[parenPrefix.Length..^1];
            }
        }

        if (candidate.Equals("Player (Offline Hub)", StringComparison.OrdinalIgnoreCase))
        {
            return "Offline Hub";
        }

        return candidate;
    }

    private string RedactSmallTooltipForStreamerPrivacy()
    {
        if (!string.IsNullOrEmpty(_selectedHeroDisplayName))
        {
            if (!string.IsNullOrWhiteSpace(_selectedAvatarDisplayName) &&
                !string.Equals(_selectedAvatarDisplayName, _selectedHeroDisplayName, StringComparison.OrdinalIgnoreCase))
            {
                return $"{_selectedAvatarDisplayName} • {_selectedHeroDisplayName}";
            }

            return _selectedHeroDisplayName;
        }

        return "Heroes of Newerth";
    }

    private void SanitizeAndFormatPresencePayload(RichPresence presence)
    {
        presence.StatusDisplay = StatusDisplayType.Details;

        if (!ReferenceEquals(presence, _currentPresence))
        {
            _unredactedDetails = presence.Details;
            _unredactedState = presence.State;
            _unredactedSmallText = presence.Assets?.SmallImageText;
            _unredactedLargeKey = presence.Assets?.LargeImageKey;
            _unredactedSmallKey = presence.Assets?.SmallImageKey;
        }

        if (_streamerPrivacyMode)
        {
            if (!string.IsNullOrEmpty(presence.Details))
            {
                presence.Details = Regex.Replace(presence.Details, @"\s*•\s*\d+/\d+/\d+\s*$", string.Empty);
            }

            presence.State = RedactStateForStreamerPrivacy(presence.State);
            if (presence.Assets != null)
            {
                presence.Assets.SmallImageText = RedactSmallTooltipForStreamerPrivacy();
            }

            presence.Party = null;
        }

        if (presence.Assets != null)
        {
            string key = presence.Assets.LargeImageKey?.ToLowerInvariant() ?? "large";
            if (!IsAssetAvailable(key))
            {
                string fallbackLarge = key == "map_truedevo" && IsAssetAvailable("map_devowars")
                    ? "map_devowars"
                    : "large";
                LogDebug($"[DiscordRPC] LargeImageKey '{key}' not yet available in portal; falling back to '{fallbackLarge}'.");
                presence.Assets.LargeImageKey = fallbackLarge;
            }
            else
            {
                presence.Assets.LargeImageKey = key;
            }

            if (!string.IsNullOrEmpty(presence.Assets.SmallImageKey))
            {
                string sKey = presence.Assets.SmallImageKey.ToLowerInvariant();
                if (!IsAssetAvailable(sKey))
                {
                    LogDebug($"[DiscordRPC] SmallImageKey '{sKey}' not yet available in portal; falling back to 'small'.");
                    presence.Assets.SmallImageKey = "small";
                }
                else
                {
                    presence.Assets.SmallImageKey = sKey;
                }
            }
        }

        if (presence.Party != null)
        {
            int safeSize = Math.Max(1, presence.Party.Size);
            int safeMax = Math.Max(safeSize, presence.Party.Max);
            presence.Party.Size = safeSize;
            presence.Party.Max = safeMax;
        }
    }

    public void BeginLogBatch()
    {
        _logBatchDepth++;
    }

    public void EndLogBatch(DateTime? nowUtcOverride = null)
    {
        if (_logBatchDepth > 0)
        {
            _logBatchDepth--;
        }

        if (_logBatchDepth == 0 && _pendingLogBatchPresence != null)
        {
            RichPresence batchPresence = _pendingLogBatchPresence;
            bool forceImmediate = _pendingLogBatchForceImmediate;
            _pendingLogBatchPresence = null;
            _pendingLogBatchForceImmediate = false;
            DispatchPresenceToDiscordIpc(batchPresence, forceImmediate, nowUtcOverride ?? TimeProvider.System.GetUtcNow().UtcDateTime);
        }
    }

    public bool FlushPendingPresenceIfReady(DateTime? nowUtcOverride = null)
    {
        if (_pendingRateLimitedPresence == null)
        {
            return false;
        }

        DateTime nowUtc = nowUtcOverride ?? TimeProvider.System.GetUtcNow().UtcDateTime;
        PruneExpiredIpcWindow(nowUtc);
        if (_ipcSendWindowUtc.Count >= MaxIpcSendsPerWindow)
        {
            return false;
        }

        RichPresence pending = _pendingRateLimitedPresence;
        _pendingRateLimitedPresence = null;
        LogDebug($"[DiscordRPC] Trailing-edge rate-limit slot opened -> flushing deferred presence: Details='{pending.Details}', State='{pending.State}', SmallKey='{pending.Assets?.SmallImageKey}'");
        DispatchPresenceToDiscordIpc(pending, forceImmediate: true, nowUtc);
        return true;
    }

    private void PruneExpiredIpcWindow(DateTime nowUtc)
    {
        while (_ipcSendWindowUtc.Count > 0 && (nowUtc - _ipcSendWindowUtc.Peek()) >= IpcRateLimitWindow)
        {
            _ipcSendWindowUtc.Dequeue();
        }
    }

    private void SetPresenceInternal(RichPresence presence, bool forceImmediate = false)
    {
        SanitizeAndFormatPresencePayload(presence);
        _currentPresence = presence;

        if (_logBatchDepth > 0)
        {
            _pendingLogBatchPresence = presence;
            _pendingLogBatchForceImmediate |= forceImmediate;
            return;
        }

        DispatchPresenceToDiscordIpc(presence, forceImmediate, TimeProvider.System.GetUtcNow().UtcDateTime);
    }

    private void DispatchPresenceToDiscordIpc(RichPresence presence, bool forceImmediate, DateTime nowUtc)
    {
        string currentLarge = presence.Assets?.LargeImageKey ?? string.Empty;
        string currentSmall = presence.Assets?.SmallImageKey ?? string.Empty;
        string currentDetails = presence.Details ?? string.Empty;
        string currentState = presence.State ?? string.Empty;
        double elapsedSeconds = (nowUtc - _lastSentTime).TotalSeconds;

        OnActivityUpdated?.Invoke(currentDetails, currentState);

        if (!forceImmediate)
        {
            // Deduplicate rapid identical presence commands to prevent Discord IPC queue congestion
            if (currentDetails == _lastSentDetails &&
                currentState == _lastSentState &&
                currentLarge == _lastSentLargeKey &&
                currentSmall == _lastSentSmallKey &&
                elapsedSeconds < 3.0)
                return;

            // Edge-Immediate / 15s Coalesced Sender:
            // Phase, map, or hero icon changes dispatch immediately; continuous KDA/Level stat updates coalesce to 15s
            // so Discord's 5-updates-per-20s IPC rate limiter never drops phase or hero transitions.
            bool isPureStatTick = !string.IsNullOrEmpty(_lastSentDetails) &&
                                  currentLarge == _lastSentLargeKey &&
                                  currentSmall == _lastSentSmallKey &&
                                  string.Equals(ExtractPhasePrefix(currentDetails), ExtractPhasePrefix(_lastSentDetails), StringComparison.OrdinalIgnoreCase) &&
                                  (currentDetails.Contains(" • ", StringComparison.Ordinal) || currentState.Contains("(Lvl ", StringComparison.Ordinal));
            if (isPureStatTick && elapsedSeconds < 15.0)
                return;
        }

        // Sliding 4-per-20s rate guard: Discord's local IPC socket silently drops frames exceeding 5 updates per 20 seconds.
        // Hold burst overflow in _pendingRateLimitedPresence so the 100ms background pump flushes the latest hero/state as soon as a slot opens.
        PruneExpiredIpcWindow(nowUtc);
        if (_ipcSendWindowUtc.Count >= MaxIpcSendsPerWindow)
        {
            _pendingRateLimitedPresence = presence;
            LogDebug(
                $"[DiscordRPC] Coalesced SetPresence into trailing-edge slot ({_ipcSendWindowUtc.Count}/{MaxIpcSendsPerWindow} in 20s window): Details='{presence.Details}', State='{presence.State}', SmallKey='{presence.Assets?.SmallImageKey}'");
            return;
        }

        _pendingRateLimitedPresence = null;
        _ipcSendWindowUtc.Enqueue(nowUtc);
        _lastSentDetails = currentDetails;
        _lastSentState = currentState;
        _lastSentLargeKey = currentLarge;
        _lastSentSmallKey = currentSmall;
        _lastSentTime = nowUtc;
        LastDispatchedIpcPresence = presence;
        DispatchedIpcCount++;

        if (_client == null || !_client.IsInitialized || _client.IsDisposed) return;

        try
        {
            _client.SetPresence(presence);
            LogDebug(
                $"[DiscordRPC] SetPresence sent: Details='{presence.Details}', State='{presence.State}', LargeKey='{presence.Assets?.LargeImageKey}', SmallKey='{presence.Assets?.SmallImageKey}', SmallText='{presence.Assets?.SmallImageText}', StatusDisplay='{presence.StatusDisplay}'");
        }
        catch (Exception ex)
        {
            LogDebug($"[DiscordRPC] SetPresence note ({ex.Message}). Retrying with simplified presence payload...");
            try
            {
                RichPresence fallbackPresence = new()
                {
                    Details = presence.Details,
                    State = presence.State,
                    StatusDisplay = StatusDisplayType.Details,
                    Assets = new Assets
                    {
                        LargeImageKey = "large", LargeImageText = presence.Assets?.LargeImageText ?? "Heroes of Newerth"
                    },
                    Timestamps = presence.Timestamps
                };
                _client.SetPresence(fallbackPresence);
                LogDebug(
                    $"[DiscordRPC] Fallback SetPresence delivered to Discord successfully: '{fallbackPresence.Details}' - '{fallbackPresence.State}'");
            }
            catch (Exception innerEx)
            {
                LogDebug($"[DiscordRPC] Fallback SetPresence error: {innerEx.Message}");
            }
        }
    }

    private void TryPreloadSavedAccountName()
    {
        try
        {
            string myDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string currentDir = Directory.GetCurrentDirectory();

            List<string> searchDirectories =
            [
                Path.Combine(currentDir, "games", "client", "userdata", "game"),
                Path.Combine(currentDir, "games", "client", "userdata"),
                Path.Combine(currentDir, "userdata", "game"),
                Path.Combine(currentDir, "userdata"),
                Path.Combine(baseDir, "userdata", "game"),
                Path.Combine(baseDir, "userdata"),
                Path.Combine(baseDir, "KONGOR", "configuration"),
                Path.Combine(currentDir, "games", "client", "config"),
                Path.Combine(currentDir, "games", "shared", "config"),
                Path.Combine(currentDir, "games", "client"),
                Path.Combine(currentDir, "games", "shared"),
                Path.Combine(userProfile, "OneDrive", "Documents", "Heroes of Newerth x64"),
                Path.Combine(userProfile, "Documents", "Heroes of Newerth x64"),
                Path.Combine(myDocuments, "Heroes of Newerth x64")
            ];

            List<string> candidateConfigFiles = [];
            foreach (string dir in searchDirectories)
                if (Directory.Exists(dir))
                    try
                    {
                        candidateConfigFiles.AddRange(Directory.GetFiles(dir, "*.cfg", SearchOption.AllDirectories));
                    }
                    catch
                    {
                        System.Diagnostics.Trace.WriteLine($"[DISCORD_RPC] Config directory scan skipped due to access restriction: '{dir}'.");
                    }

            // Sort files by local priority first (userdata / local client configs over legacy OneDrive/Documents), then LastWriteTimeUtc descending
            string legacyDocs = Path.Combine(myDocuments, "Heroes of Newerth x64");
            string legacyOneDrive = Path.Combine(userProfile, "OneDrive", "Documents", "Heroes of Newerth x64");
            string legacyDocsUser = Path.Combine(userProfile, "Documents", "Heroes of Newerth x64");

            List<string> sortedFiles =
            [
                .. candidateConfigFiles
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(File.Exists)
                    .OrderByDescending(f => !f.StartsWith(legacyDocs, StringComparison.OrdinalIgnoreCase) &&
                                            !f.StartsWith(legacyOneDrive, StringComparison.OrdinalIgnoreCase) &&
                                            !f.StartsWith(legacyDocsUser, StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(f => f.Contains("userdata", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(File.GetLastWriteTimeUtc)
            ];

            foreach (string file in sortedFiles)
            {
                string cfgFileName = Path.GetFileName(file);
                if (!cfgFileName.Equals("login.cfg", StringComparison.OrdinalIgnoreCase) &&
                    !cfgFileName.Equals("startup.cfg", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!file.Contains("userdata", StringComparison.OrdinalIgnoreCase) &&
                    !file.Contains("Heroes of Newerth", StringComparison.OrdinalIgnoreCase))
                    continue;
                string text = ReadConfigFileText(file);
                if (string.IsNullOrWhiteSpace(text)) continue;

                Match m = Regex.Match(text, @"(?:Set|SetSave)?\s*""?(?:login_name|svr_login)""?\s+""?([^""\r\n\s]+)""?",
                    RegexOptions.IgnoreCase);
                if (m.Success && !string.IsNullOrWhiteSpace(m.Groups[1].Value))
                {
                    string candidate = m.Groups[1].Value.Trim();
                    if (IsDisallowedPreloadedConfigName(candidate)) continue;
                    SetAccountName(candidate, isAuthoritative: false);
                    LogDebug(
                        $"[DiscordRPC] Preloaded Account Name '{_accountName}' from {file} (Modified: {File.GetLastWriteTime(file)})");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            LogDebug($"[DiscordRPC] Error preloading account name: {ex.Message}");
        }
    }

    private static string ReadConfigFileText(string filePath)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(filePath);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            if (bytes.Length >= 4 && bytes[1] == 0x00 && bytes[3] == 0x00) return Encoding.Unicode.GetString(bytes);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return string.Empty;
        }
    }

    internal async Task EnsureSubscribedAsync(CancellationToken token = default)
    {
        long targetAccountId = _accountId;
        if (!IsValidOnlineAccountId(targetAccountId))
        {
            return;
        }

        string? cookie = ExtractNormalizedSessionCookie(Volatile.Read(ref _sessionCookie));
        if (string.IsNullOrWhiteSpace(cookie))
        {
            LogDebug($"[DiscordRPC] Deferring Presence Hub subscription for Account ID {targetAccountId} until session cookie is authenticated.");
            return;
        }

        if (Volatile.Read(ref _isDisposed) != 0)
        {
            return;
        }

        bool lockAcquired = false;
        try
        {
            using CancellationTokenSource timeoutCts = new(TimeSpan.FromSeconds(10));
            using CancellationTokenSource linkedCts =
                CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);
            await _hubSyncLock.WaitAsync(linkedCts.Token).ConfigureAwait(false);
            lockAcquired = true;

            if (Volatile.Read(ref _isDisposed) != 0)
            {
                return;
            }

            if (_hubConnection != null &&
                !string.IsNullOrWhiteSpace(_activeHubUrl) &&
                !string.Equals(_connectedHubCookie, cookie, StringComparison.Ordinal))
            {
                LogDebug($"[DiscordRPC] Rebuilding Presence Hub connection for Account ID {targetAccountId} with updated 'X-Kongor-Cookie' header.");
                HubConnection previousHub = _hubConnection;
                _hubConnection = CreatePresenceHubConnection(_activeHubUrl, cookie);
                _connectedHubCookie = cookie;
                _subscribedAccountId = 0;
                await previousHub.DisposeAsync().ConfigureAwait(false);
            }

            if (_hubConnection == null)
            {
                return;
            }

            if (_hubConnection.State == HubConnectionState.Disconnected)
            {
                await _hubConnection.StartAsync(linkedCts.Token).ConfigureAwait(false);
                _connectedHubCookie = cookie;
            }

            if (_hubConnection.State == HubConnectionState.Connected)
            {
                if (!string.Equals(_connectedHubCookie, cookie, StringComparison.Ordinal))
                {
                    bool cookieAccepted = await _hubConnection
                        .InvokeAsync<bool>("AuthenticateSessionCookie", cookie, linkedCts.Token)
                        .ConfigureAwait(false);
                    if (!cookieAccepted)
                    {
                        LogDebug($"[DiscordRPC] MasterServer Presence Hub rejected session cookie for Account ID {targetAccountId}.");
                        return;
                    }
                    _connectedHubCookie = cookie;
                }

                if (_subscribedAccountId != targetAccountId)
                {
                    await _hubConnection.InvokeAsync("SubscribeToPresence", targetAccountId, linkedCts.Token).ConfigureAwait(false);
                    _subscribedAccountId = targetAccountId;
                    LogDebug($"[DiscordRPC] Subscribed to MasterServer Presence Hub for Account ID {targetAccountId}.");
                }
            }
        }
        catch (Exception ex)
        {
            LogDebug($"[DiscordRPC] Failed to subscribe to Presence Hub for Account ID {targetAccountId}: {ex.Message}");
        }
        finally
        {
            if (lockAcquired)
            {
                _hubSyncLock.Release();
            }
        }
    }

    public void ProcessConsoleLogLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        try
        {
            // Strip K2 color codes (e.g. ^999, ^r, ^g, ^*, ^:, etc.) so all pattern matches work reliably
            string cleanLine = Regex.Replace(line, @"\^(?:\d{3}|#[0-9a-fA-F]{6}|[a-zA-Z0-9*:\-~])", string.Empty);

            // Ignore asset loading, entity XML parsing, VFS, Lua symbol dumps, and UI diagnostic logs that occur during startup / precaching
            if (cleanLine.StartsWith("Resource:", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.StartsWith("Net: [", StringComparison.OrdinalIgnoreCase) ||
                IsDevConsoleLine(cleanLine) ||
                cleanLine.Contains("Loading Entity definition:", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("Loading Model definition:", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("Loading Effect definition:", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("Loading Material definition:", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("Loading Texture definition:", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("Loading Sample:", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("[VFS]", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("[MODS_______]", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("[NEXUS_GLOBALS]", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("[NEXUS]", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("[STORE", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("[COMPENDIUM", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("[MAIN-ACCOUNT]", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("[MAIN_LUA]", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("[MAIN_V2_LUA]", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("[HOMEPAGE_LUA_UNUSED]", StringComparison.Ordinal) ||
                cleanLine.Contains("[PLAYER_STATS_V2]", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("[CHAT-USERLIST]", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("[ESC-GUARD]", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("[ESC-NAV]", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("Discovered and cataloged", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("hero_definitions_cache", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains(".entity", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains(".mdf", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains(".tga", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains(".dds", StringComparison.OrdinalIgnoreCase) ||
                ContainsNonGameplayAssetPath(cleanLine.ToLowerInvariant()) ||
                Regex.IsMatch(cleanLine, @"^\s*\['[^']+'\]\s*=\s*\{"))
                return;

            // 0a. Capture authoritative local account identity from NewUI Homepage AccountInfo watch
            if (cleanLine.Contains("[HOMEPAGE_LUA]", StringComparison.OrdinalIgnoreCase))
            {
                Match hpMatch = Regex.Match(
                    cleanLine,
                    @"AccountInfo watch fired\s*->\s*accountId:\s*(\d+),\s*nickname:\s*([^\r\n,]+)",
                    RegexOptions.IgnoreCase);
                if (hpMatch.Success)
                {
                    string hpNick = hpMatch.Groups[2].Value.Trim();
                    bool hasValidHomepageId = long.TryParse(hpMatch.Groups[1].Value, out long hpAccId) && IsValidOnlineAccountId(hpAccId);
                    if (hasValidHomepageId)
                    {
                        TryUpdateAccountId(hpAccId, "[HOMEPAGE_LUA]");
                    }
                    if (!string.IsNullOrWhiteSpace(hpNick) && !IsPlaceholderPlayerName(hpNick))
                    {
                        SetAccountName(hpNick, isAuthoritative: hasValidHomepageId);
                    }
                    LogDebug($"[DiscordRPC] Homepage AccountInfo identity synced: Account ID {_accountId}, Name '{_accountName}' (Clean='{CleanAccountName}')");
                    if (IsValidOnlineAccountId(_accountId))
                    {
                        _ = EnsureSubscribedAsync();
                    }
                }

                return;
            }

            // 0b. Track One-Time Menu Gameplay Asset Prewarm ([KONGOR-BOOT-PRELOAD] / [BOOT_PRELOAD])
            // PreloadWorld('caldavar') emits "Start loading world caldavar" / "CWorld::StartLoad" while still on the menu;
            // suppress false "In Match (Forests of Caldavar)" Discord Rich Presence transitions until prewarm finishes.
            if (cleanLine.Contains("[KONGOR-BOOT-PRELOAD]", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("[BOOT_PRELOAD]", StringComparison.OrdinalIgnoreCase))
            {
                if (cleanLine.Contains("START", StringComparison.OrdinalIgnoreCase) ||
                    cleanLine.Contains("Starting one-time", StringComparison.OrdinalIgnoreCase))
                {
                    _isMenuBootPreloadActive = true;
                    _menuBootPreloadStartUtc = TimeProvider.System.GetUtcNow();
                    LogDebug("[DiscordRPC] Menu asset prewarm started -> suppressing PreloadWorld match presence transitions.");
                }
                else if (cleanLine.Contains("COMPLETE", StringComparison.OrdinalIgnoreCase) ||
                         cleanLine.Contains("SKIP", StringComparison.OrdinalIgnoreCase) ||
                         cleanLine.Contains("preload finished", StringComparison.OrdinalIgnoreCase))
                {
                    _isMenuBootPreloadActive = false;
                    _menuBootPreloadStartUtc = null;
                    LogDebug("[DiscordRPC] Menu asset prewarm finished -> re-enabling normal match presence transitions.");
                }

                return;
            }

            // 0. Direct In-Game Lua Structured Telemetry Bridge ([NEXUS_TELEMETRY] or [NEXUS_EVENT])
            if (cleanLine.Contains("[NEXUS_TELEMETRY]", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("[NEXUS_EVENT]", StringComparison.OrdinalIgnoreCase))
            {
                ProcessNexusTelemetry(cleanLine);
                return;
            }

            // Proactively detect match mode (Practice Match / Bot Match) from server ports, bot commands, or launch cvars
            if (HandleQueueArcadeLogTransitions(cleanLine))
                return;

            string? proactiveMode = _isQueueArcadeActive ? null : DetectMatchModeFromLog(cleanLine);
            if (!string.IsNullOrEmpty(proactiveMode))
            {
                _currentMatchMode = proactiveMode;
                _localPlayerSlot ??= 0;
                LogDebug($"[DiscordRPC] Proactive match mode detected from log: '{_currentMatchMode}'");
            }

            // 1. Detect Account Login / ID / Name / Config
            // e.g. "Login success: #59 guccidonut [01a1c416abdb7db9ab2f0db883d9cdd4:b2e5ba0f...]" or "Login success: [PK]GUEST-05"
            Match loginMatch = Regex.Match(
                cleanLine,
                @"Login success:\s*(?:#(\d+)\s+)?([^\s\r\n,]+)(?:\s+\[([0-9a-fA-F\-]{16,64}(?::[0-9a-fA-F]{16,64})?)\])?",
                RegexOptions.IgnoreCase);
            if (loginMatch.Success)
            {
                if (loginMatch.Groups[1].Success && long.TryParse(loginMatch.Groups[1].Value, out long id) && IsValidOnlineAccountId(id))
                {
                    _hasAuthoritativeOnlineAccountId = false;
                    TryUpdateAccountId(id, "Login success", isExplicitLoginOrPipe: true);
                }
                if (loginMatch.Groups[3].Success)
                {
                    SetSessionCookie(loginMatch.Groups[3].Value, "Login success");
                }
                SetAccountName(loginMatch.Groups[2].Value);
                ClearHostedLobbyState("Login success");
                SetQueueArcadeActive(false, "Login success");
                ClearQueueState("Login success");
                _currentMatchMode = null;
                _currentGameMode = string.Empty;
                _selectedHeroAssetKey = null;
                _selectedHeroDisplayName = null;
                LogDebug($"[DiscordRPC] Login detected: Account ID {_accountId}, Name '{_accountName}' (Clean='{CleanAccountName}')");
                ClearHeroSelectionState();
                UpdateLocalPresence("In Main Menu", $"{CleanAccountName} (In Lobby)");
                _ = EnsureSubscribedAsync();
                return;
            }

            Match setCookieMatch = Regex.Match(
                cleanLine,
                @"(?:SetSave|Set)\s+[""']?(?:login_cookie|session_cookie)[""']?\s+[""']([^""'\r\n\s]+)[""']",
                RegexOptions.IgnoreCase);
            if (setCookieMatch.Success)
            {
                SetSessionCookie(setCookieMatch.Groups[1].Value, "K2 session CVar");
            }

            // e.g. Set "login_name" "Player" or Set login_name "Player" or login_name Player
            Match setLoginMatch = Regex.Match(cleanLine,
                @"(?:Set|SetSave)?\s*""?(?:login_name|svr_login)""?\s+""?([^""\r\n\s]+)""?", RegexOptions.IgnoreCase);
            if (setLoginMatch.Success)
            {
                string val = setLoginMatch.Groups[1].Value.Trim();
                if (!IsInActiveMatchOrLobby && !_queueStartUtc.HasValue)
                {
                    SetAccountName(val);
                    _currentMatchMode = null;
                    _currentGameMode = string.Empty;
                    LogDebug($"[DiscordRPC] Login Name detected from config: '{_accountName}'");
                    if (!string.IsNullOrEmpty(_accountName))
                        UpdateLocalPresence("In Main Menu", $"{CleanAccountName} (In Lobby)");
                }
                else if (string.IsNullOrEmpty(_accountName))
                {
                    SetAccountName(val);
                }
            }

            // e.g. "Voice: Adding client num 0, Player, ID: 1" or "Sv: [13:38:14] Voice: Adding client num 0, [PK]GUEST-05, ID: 1"
            Match voiceMatch = Regex.Match(cleanLine, @"Voice:\s*Adding client num (\d+),\s*([^,\r\n]+),\s*ID:\s*(\d+)",
                RegexOptions.IgnoreCase);
            if (voiceMatch.Success)
            {
                int slot = int.Parse(voiceMatch.Groups[1].Value);
                string name = voiceMatch.Groups[2].Value.Trim();
                if (!_localPlayerSlot.HasValue || _localPlayerSlot.Value == slot || string.IsNullOrEmpty(_accountName))
                {
                    _localPlayerSlot = slot;
                    if (!name.StartsWith("Player #", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(name))
                        SetAccountName(name);
                    LogDebug($"[DiscordRPC] Voice identity detected: Slot {slot}, Name '{name}'");
                }
            }

            // e.g. "Client #0 : Player : switched to Team 1"
            Match clientTeamMatch = Regex.Match(cleanLine,
                @"Client #(\d+)\s*:\s*([^:\r\n]+)\s*:\s*switched to Team (\d+)", RegexOptions.IgnoreCase);
            if (clientTeamMatch.Success)
            {
                int slot = int.Parse(clientTeamMatch.Groups[1].Value);
                string name = clientTeamMatch.Groups[2].Value.Trim();
                if (!_localPlayerSlot.HasValue || _localPlayerSlot.Value == slot || string.IsNullOrEmpty(_accountName))
                {
                    _localPlayerSlot = slot;
                    if (!name.StartsWith("Player #", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(name))
                        SetAccountName(name);
                }
            }

            // e.g. "nickname, STRING: [PK]GUEST-05" or "nickname, STRING: louiscrouton"
            Match nickMatch = Regex.Match(cleanLine, @"nickname,\s*(?:STRING|INT):\s*([^\r\n,]+)",
                RegexOptions.IgnoreCase);
            if (nickMatch.Success)
            {
                string nickVal = nickMatch.Groups[1].Value.Trim();
                if (IsInActiveMatchOrLobby)
                {
                    if (string.IsNullOrEmpty(_accountName) || (!_hasAuthoritativeAccountName && IsLocalPlayerName(nickVal)))
                        SetAccountName(nickVal);
                    return;
                }

                if (!string.IsNullOrEmpty(_accountName) && !IsLocalPlayerName(nickVal))
                {
                    return;
                }

                SetAccountName(nickVal);
                _currentMatchMode = null;
                _currentGameMode = string.Empty;
                LogDebug($"[DiscordRPC] Nickname detected: '{_accountName}'");
                if (!string.IsNullOrEmpty(_accountName))
                    UpdateLocalPresence("In Main Menu", $"{CleanAccountName} (In Lobby)");
                return;
            }

            // e.g. "^g59|Player|5" or "^g12|louiscrouton|5"
            Match pipeMatch = Regex.Match(cleanLine, @"(?:\^g)?(\d+)\|([^|\r\n]+)\|[0-9]+", RegexOptions.IgnoreCase);
            if (pipeMatch.Success)
            {
                long parsedPipeId = 0;
                long.TryParse(pipeMatch.Groups[1].Value, out parsedPipeId);
                string pipeName = pipeMatch.Groups[2].Value.Trim();

                if (IsValidOnlineAccountId(_accountId) && IsValidOnlineAccountId(parsedPipeId) && parsedPipeId != _accountId)
                {
                    return;
                }

                if (parsedPipeId == 0 && !string.IsNullOrEmpty(_accountName) && !IsLocalPlayerName(pipeName))
                {
                    return;
                }

                if (IsValidOnlineAccountId(parsedPipeId)) TryUpdateAccountId(parsedPipeId, "pipe", isExplicitLoginOrPipe: parsedPipeId > 1);
                SetAccountName(pipeName, isAuthoritative: true);
                if (!IsInActiveMatchOrLobby && !_queueStartUtc.HasValue)
                {
                    _currentMatchMode = null;
                    _currentGameMode = string.Empty;
                    LogDebug($"[DiscordRPC] Player pipe identity detected: Account ID {_accountId}, Name '{_accountName}'");
                    if (!string.IsNullOrEmpty(_accountName))
                        UpdateLocalPresence("In Main Menu", $"{CleanAccountName} (In Lobby)");
                }
                _ = EnsureSubscribedAsync();
                return;
            }

            // e.g. "account_id, STRING: 59" or "account_id, INT: 12"
            Match accIdMatch =
                Regex.Match(cleanLine, @"account_id,\s*(?:STRING|INT):\s*(\d+)", RegexOptions.IgnoreCase);
            if (accIdMatch.Success && long.TryParse(accIdMatch.Groups[1].Value, out long parsedAccId) && IsValidOnlineAccountId(parsedAccId))
            {
                TryUpdateAccountId(parsedAccId, "account_id");
                _ = EnsureSubscribedAsync();
            }

            // 1b. Track party / group size updates from console logs
            Match partySizeMatch = Regex.Match(cleanLine, @"(?:party_size|group_size|party_count)\s*,\s*(?:INT|STRING)?\s*:?\s*(\d+)", RegexOptions.IgnoreCase);
            if (partySizeMatch.Success && int.TryParse(partySizeMatch.Groups[1].Value, out int parsedPartySize))
            {
                UpdateParty(parsedPartySize);
            }

            Match partySlashMatch = Regex.Match(cleanLine, @"(?:Party|Group)\s*:?\s*(\d+)\s*(?:/|of)\s*(\d+)", RegexOptions.IgnoreCase);
            if (partySlashMatch.Success && int.TryParse(partySlashMatch.Groups[1].Value, out int pSize) && int.TryParse(partySlashMatch.Groups[2].Value, out int pMax))
            {
                UpdateParty(pSize, pMax);
            }

            if (cleanLine.Contains("Left group", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("Left party", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("Party disbanded", StringComparison.OrdinalIgnoreCase))
            {
                UpdateParty(1, 5);
            }

            // 2. Track Match Player Connections & Local Slot Mapping
            // e.g. PLAYER_CONNECT player:0 name:"guccidonut" id:59 psr:1500.0000
            Match playerConnMatch = Regex.Match(cleanLine,
                @"PLAYER_CONNECT\s+player:(\d+)\s+name:""([^""]+)""\s+id:(\d+)", RegexOptions.IgnoreCase);
            if (playerConnMatch.Success)
            {
                string slotStr = playerConnMatch.Groups[1].Value;
                string nameStr = playerConnMatch.Groups[2].Value;
                string idStr = playerConnMatch.Groups[3].Value;

                if ((!string.IsNullOrEmpty(_accountName) &&
                     IsLocalPlayerName(nameStr)) ||
                    (_accountId > 0 && idStr.Equals(_accountId.ToString(), StringComparison.OrdinalIgnoreCase)) ||
                    (!_localPlayerSlot.HasValue && string.IsNullOrEmpty(_accountName)))
                    if (int.TryParse(slotStr, out int slot))
                    {
                        _localPlayerSlot = slot;
                        SetAccountName(nameStr);
                        LogDebug(
                            $"[DiscordRPC] Local player slot mapped to player:{_localPlayerSlot} for '{nameStr}' (ID {idStr})");
                    }
            }

            string modeSuffix = !string.IsNullOrWhiteSpace(_currentGameMode) ? $" ({_currentGameMode})" : string.Empty;

            // 3. Detect Matchmaking Queue
            if (!_isQueueArcadeActive && ((!IsInActiveMatchOrLobby && cleanLine.Contains("matchmaking", StringComparison.OrdinalIgnoreCase)) ||
                cleanLine.Contains("matchmaking pool", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("matchmaking queue", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("Matchmaking update", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("Searching for match", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("Entering matchmaking", StringComparison.OrdinalIgnoreCase)))
            {
                _currentMatchMode = null;
                _selectedHeroAssetKey = null;
                _selectedHeroDisplayName = null;
                _localPlayerSlot = null;
                _matchStartUtc = null;
                _queueStartUtc ??= TimeProvider.System.GetUtcNow().UtcDateTime;
                _currentGameMode = DetectGameModeFromLog(cleanLine, _currentGameMode);
                modeSuffix = !string.IsNullOrWhiteSpace(_currentGameMode) ? $" ({_currentGameMode})" : string.Empty;
                ClearHeroSelectionState();
                string stateMsg = string.IsNullOrEmpty(CleanAccountName) ? "Finding Match..." : $"{CleanAccountName} in Queue";
                string smallTip = string.IsNullOrEmpty(_accountName) ? "UnnamedNewbie" : _accountName;
                if (string.IsNullOrWhiteSpace(_currentGameMode) && !string.IsNullOrWhiteSpace(_queuedGameMode))
                {
                    _currentGameMode = _queuedGameMode;
                    modeSuffix = $" ({_currentGameMode})";
                }
                else if (!string.IsNullOrWhiteSpace(_currentGameMode))
                {
                    _queuedGameMode = _currentGameMode;
                    LogDebug($"[DiscordRPC] Captured queued matchmaking mode: '{_queuedGameMode}' (queueStart={_queueStartUtc:O}).");
                }
                UpdateLocalPresence($"In Matchmaking{modeSuffix}", stateMsg, "small", smallTip);
                return;
            }

            // 4. Detect Picking Phase & Hero Selection
            if (cleanLine.Contains("Begin Picking Phase", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("Picking Phase", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("Hero Selection", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("Hero Select", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("['gamePhase'] = 3", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("gamePhase, INT: 3", StringComparison.OrdinalIgnoreCase))
            {
                if (!_isQueueArcadeActive) _queueStartUtc = null;
                if (!_isQueueArcadeActive) _currentMatchMode = DetectMatchModeFromLog(cleanLine, _currentMatchMode);
                _currentGameMode = DetectGameModeFromLog(cleanLine, _currentGameMode);
                if (string.IsNullOrEmpty(_currentGameMode) && !string.IsNullOrEmpty(_currentMatchMode))
                    _currentGameMode = "Forests of Caldavar";
                modeSuffix = !string.IsNullOrWhiteSpace(_currentGameMode) ? $" ({_currentGameMode})" : string.Empty;
                string smallTip = string.IsNullOrEmpty(_accountName) ? "UnnamedNewbie" : _accountName;
                if (string.IsNullOrEmpty(_selectedHeroDisplayName))
                {
                    string stateMsg = string.IsNullOrEmpty(_accountName)
                        ? "Hero Selection"
                        : $"{CleanAccountName} (Hero Selection)";
                    UpdateLocalPresence($"Picking Hero{modeSuffix}", stateMsg, "small", smallTip);
                }
                else
                {
                    string stateMsg = string.IsNullOrEmpty(_accountName)
                        ? $"Picking Hero: {_selectedHeroDisplayName}"
                        : $"{CleanAccountName} as {_selectedHeroDisplayName}";
                    string heroTip =
                        BuildHeroSmallTooltip("Hero Selection");
                    UpdateLocalPresence($"Picking Hero{modeSuffix}", stateMsg, _selectedHeroAssetKey, heroTip);
                }

                LogDebug(
                    $"[DiscordRPC] Picking Phase active for {_currentGameMode} (Selected Hero='{_selectedHeroDisplayName}')");
                return;
            }

            DateTime? preLoadQueueStartUtc = _queueStartUtc;
            string preLoadGameMode = _currentGameMode;
            // 5. Detect Joining Game / Connecting to Server / Loading Map / Starting Match
            if (cleanLine.Contains("Joining game '", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("Joining Game", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("Connecting to game server", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("Create new session to", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("JoinGame", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("StartGame", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("Started game", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("StartPractice", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("PracticeMatch", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("LocalBotsGame", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("PracticeGame", StringComparison.OrdinalIgnoreCase))
            {
                _isMenuBootPreloadActive = false;
                _menuBootPreloadStartUtc = null;
                if (!_isQueueArcadeActive) _queueStartUtc = null;
                ClearHeroSelectionState();
                _selectedHeroAssetKey = null;
                _selectedHeroDisplayName = null;
                if (!_isQueueArcadeActive) _currentMatchMode = DetectMatchModeFromLog(cleanLine, _currentMatchMode);
                if (!string.IsNullOrEmpty(_currentMatchMode))
                    _localPlayerSlot ??= 0;
                else
                    _localPlayerSlot = null;

                _currentGameMode = DetectGameModeFromLog(cleanLine, _currentGameMode);
                if (string.IsNullOrEmpty(_currentGameMode) && !string.IsNullOrEmpty(_currentMatchMode))
                    _currentGameMode = "Forests of Caldavar";

                modeSuffix = !string.IsNullOrWhiteSpace(_currentGameMode) ? $" ({_currentGameMode})" : string.Empty;
                string smallTip = string.IsNullOrEmpty(_accountName) ? "UnnamedNewbie" : _accountName;
                string connectingState = !string.IsNullOrEmpty(_accountName)
                    ? $"{CleanAccountName} (Connecting)"
                    : "Connecting to Server";
                if (!_isQueueArcadeActive &&
                    preLoadQueueStartUtc.HasValue &&
                    string.IsNullOrEmpty(_currentMatchMode) &&
                    (!string.IsNullOrWhiteSpace(_queuedGameMode) ||
                     cleanLine.Contains("soccer", StringComparison.OrdinalIgnoreCase) ||
                     cleanLine.Contains("capturetheflag", StringComparison.OrdinalIgnoreCase)))
                {
                    if (string.IsNullOrWhiteSpace(_queuedGameMode) && !string.IsNullOrWhiteSpace(preLoadGameMode))
                    {
                        _queuedGameMode = preLoadGameMode;
                    }
                    _queueStartUtc = preLoadQueueStartUtc;
                    _recentArcadeQueueStartUtc ??= preLoadQueueStartUtc;
                    _recentArcadeDisconnectUtc = TimeProvider.System.GetUtcNow().UtcDateTime;
                    LogDebug($"[DiscordRPC] Preserved active matchmaking queue timer ({_queueStartUtc:O}, queuedMode='{_queuedGameMode}') during map load '{cleanLine}'.");
                }
                else if (!_isQueueArcadeActive && !string.IsNullOrEmpty(_currentMatchMode))
                {
                    ClearQueueState($"LoadGame:{_currentMatchMode}");
                }

                UpdateLocalPresence($"Joining Game{modeSuffix}...", connectingState, "small", smallTip);
                return;
            }

            if (cleanLine.Contains("Loading map:", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("Loading world", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("loading world", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("Start loading", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("CWorld::StartLoad", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("Setting m_bStartedLoadingWorld", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("CreateGame", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("HostGame", StringComparison.OrdinalIgnoreCase))
            {
                bool isExplicitHostOrCreate =
                    cleanLine.Contains("CreateGame", StringComparison.OrdinalIgnoreCase) ||
                    cleanLine.Contains("HostGame", StringComparison.OrdinalIgnoreCase);
                if (_isMenuBootPreloadActive)
                {
                    bool preloadExpired = _menuBootPreloadStartUtc.HasValue &&
                                          (TimeProvider.System.GetUtcNow() - _menuBootPreloadStartUtc.Value) > TimeSpan.FromSeconds(45);
                    if (isExplicitHostOrCreate || preloadExpired)
                    {
                        _isMenuBootPreloadActive = false;
                        _menuBootPreloadStartUtc = null;
                    }
                    else
                    {
                        LogDebug($"[DiscordRPC] Ignoring PreloadWorld log line during menu asset prewarm: '{cleanLine}'");
                        return;
                    }
                }

                if (!isExplicitHostOrCreate && !string.IsNullOrEmpty(_selectedHeroAssetKey) && _matchStartUtc.HasValue)
                {
                    _currentGameMode = DetectGameModeFromLog(cleanLine, _currentGameMode);
                    LogDebug($"[DiscordRPC] Updated map context '{_currentGameMode}' without resetting active match hero '{_selectedHeroDisplayName}'.");
                    return;
                }

                if (_isQueueArcadeActive)
                {
                    ClearHeroSelectionState();
                }

                if (!_isQueueArcadeActive) _currentMatchMode = DetectMatchModeFromLog(cleanLine, _currentMatchMode);
                if (!string.IsNullOrEmpty(_currentMatchMode))
                    _localPlayerSlot ??= 0;
                else
                    _localPlayerSlot = null;

                _currentGameMode = DetectGameModeFromLog(cleanLine, _currentGameMode);
                if (string.IsNullOrEmpty(_currentGameMode) && !string.IsNullOrEmpty(_currentMatchMode))
                    _currentGameMode = "Forests of Caldavar";

                modeSuffix = !string.IsNullOrWhiteSpace(_currentGameMode) ? $" ({_currentGameMode})" : string.Empty;
                string smallTip = string.IsNullOrEmpty(_accountName) ? "UnnamedNewbie" : _accountName;
                string loadingState = !string.IsNullOrEmpty(_accountName)
                    ? $"{CleanAccountName} (Hero Selection)"
                    : "Hero Selection / Loading";
                UpdateLocalPresence($"In Match{modeSuffix}", loadingState, "small", smallTip);
                return;
            }

            // 6. Detect Hero Guides / Serialized Guide Lookups (only when explicit guide command, not generic server HTTP arrays)
            if (cleanLine.Contains("guide", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("HeroGuide", StringComparison.OrdinalIgnoreCase))
            {
                Match guideHeroMatch = Regex.Match(cleanLine,
                    @"\b(?:hero_cli_name|guide_hero_name|guide_hero),\s*STRING:\s*([A-Za-z0-9_.]+)",
                    RegexOptions.IgnoreCase);
                if (guideHeroMatch.Success)
                {
                    string rawHero = guideHeroMatch.Groups[1].Value;
                    (string AssetKey, string DisplayName)? resolved = ResolveHero(rawHero);
                    if (resolved.HasValue)
                    {
                        _selectedHeroAssetKey = resolved.Value.AssetKey;
                        _selectedHeroDisplayName = resolved.Value.DisplayName;
                        _currentGameMode = DetectGameModeFromLog(cleanLine, _currentGameMode);

                        string stateMsg = string.IsNullOrEmpty(_accountName)
                            ? $"Playing as {_selectedHeroDisplayName}"
                            : $"{CleanAccountName} as {_selectedHeroDisplayName}";
                        string smallTip =
                            BuildHeroSmallTooltip("UnnamedNewbie");

                        UpdateLocalPresence($"Playing {_currentGameMode}", stateMsg, _selectedHeroAssetKey, smallTip);
                        LogDebug(
                            $"[DiscordRPC] Live Hero Picked from Guide/Console: {_selectedHeroDisplayName} (AssetKey='{_selectedHeroAssetKey}')");
                        return;
                    }
                }
            }

            // 7. Detect Client Hero Selection / Pick (e.g. "Client #0 selected hero Hero_Pyromancer" or "Client #0 picked Hero_Pyromancer")
            Match clientPickMatch = Regex.Match(cleanLine,
                @"Client #(\d+)\s*(?:selected|picked)\s*(?:hero:?\s*)?([A-Za-z0-9_.]+)", RegexOptions.IgnoreCase);
            if (clientPickMatch.Success)
            {
                int slot = int.Parse(clientPickMatch.Groups[1].Value);
                if (!_localPlayerSlot.HasValue || _localPlayerSlot.Value == slot)
                {
                    _localPlayerSlot ??= slot;
                    string rawHero = clientPickMatch.Groups[2].Value;
                    (string AssetKey, string DisplayName)? resolved = ResolveHero(rawHero);
                    if (resolved.HasValue)
                    {
                        if (!_isQueueArcadeActive) _currentMatchMode = DetectMatchModeFromLog(cleanLine, _currentMatchMode);
                        _selectedHeroAssetKey = resolved.Value.AssetKey;
                        _selectedHeroDisplayName = resolved.Value.DisplayName;
                        _currentGameMode = DetectGameModeFromLog(cleanLine, _currentGameMode);
                        if (string.IsNullOrEmpty(_currentGameMode) && !string.IsNullOrEmpty(_currentMatchMode))
                            _currentGameMode = "Forests of Caldavar";

                        string stateMsg = string.IsNullOrEmpty(_accountName)
                            ? $"Playing as {_selectedHeroDisplayName}"
                            : $"{CleanAccountName} as {_selectedHeroDisplayName}";
                        string smallTip =
                            BuildHeroSmallTooltip("UnnamedNewbie");

                        UpdateLocalPresence($"Playing {_currentGameMode}", stateMsg, _selectedHeroAssetKey, smallTip);
                        LogDebug(
                            $"[DiscordRPC] Live Hero Picked from Client Output (Slot {slot}): {_selectedHeroDisplayName} (AssetKey='{_selectedHeroAssetKey}')");
                        return;
                    }
                }
            }

            // 8. Detect Chat-Announced Hero Selection, Random, and Repick (e.g. "Player has randomed Drunken Master." or "[PK]Player selected Pyromancer")
            Match chatPickMatch = Regex.Match(cleanLine,
                @"(?:([A-Za-z0-9_\-\[\]]+)\s+(?:has\s+)?(?:randomed|picked|selected|repicked)\s+([A-Za-z0-9_.\s\-]+))",
                RegexOptions.IgnoreCase);
            if (chatPickMatch.Success)
            {
                string player = chatPickMatch.Groups[1].Value.Trim();
                string heroRaw = chatPickMatch.Groups[2].Value.Trim().TrimEnd('.');
                (string AssetKey, string DisplayName)? resolved = ResolveHero(heroRaw);
                if (resolved.HasValue && IsLocalPlayerName(player))
                {
                    if (string.IsNullOrEmpty(_accountName) && !_localPlayerSlot.HasValue)
                    {
                        SetAccountName(player);
                    }

                    if (!_isQueueArcadeActive) _currentMatchMode = DetectMatchModeFromLog(cleanLine, _currentMatchMode);
                    if (_selectedHeroAssetKey != resolved.Value.AssetKey)
                        _selectedAvatarDisplayName = null;
                    _selectedHeroAssetKey = resolved.Value.AssetKey;
                    _selectedHeroDisplayName = resolved.Value.DisplayName;
                    _currentGameMode = DetectGameModeFromLog(cleanLine, _currentGameMode);
                    if (string.IsNullOrEmpty(_currentGameMode) && !string.IsNullOrEmpty(_currentMatchMode))
                        _currentGameMode = "Forests of Caldavar";

                    string cleanAccount = string.IsNullOrEmpty(CleanAccountName) ? "Player" : CleanAccountName;
                    string tooltipAccount = string.IsNullOrEmpty(_accountName) ? "Player" : _accountName;
                    string stateMsg = $"{cleanAccount} as {_selectedHeroDisplayName}";
                    string smallTip = BuildHeroSmallTooltip(tooltipAccount);
                    UpdateLocalPresence($"Playing {_currentGameMode}", stateMsg, _selectedHeroAssetKey, smallTip);
                    LogDebug(
                        $"[DiscordRPC] Live Hero Picked from Chat: '{_selectedHeroDisplayName}' by '{tooltipAccount}'");
                    return;
                }
            }

            // 9. Detect Slotted Hero Selection, Repick & Random
            Match slotHeroMatch = Regex.Match(cleanLine,
                @"(?:PLAYER_SELECT|PLAYER_RANDOM|PLAYER_REPICK|PLAYER_HERO|SPAWN_HERO|PLAYER_SPAWN)\s+player:(\d+)\s+hero:""?([^""\s]+)""?",
                RegexOptions.IgnoreCase);
            if (slotHeroMatch.Success)
            {
                if (int.TryParse(slotHeroMatch.Groups[1].Value, out int slot))
                    if (!_localPlayerSlot.HasValue || _localPlayerSlot.Value == slot)
                    {
                        _localPlayerSlot ??= slot;
                        string rawHero = slotHeroMatch.Groups[2].Value;
                        (string AssetKey, string DisplayName)? resolved = ResolveHero(rawHero);
                        if (resolved.HasValue)
                        {
                            if (!_isQueueArcadeActive) _currentMatchMode = DetectMatchModeFromLog(cleanLine, _currentMatchMode);
                            _selectedHeroAssetKey = resolved.Value.AssetKey;
                            _selectedHeroDisplayName = resolved.Value.DisplayName;
                            _currentGameMode = DetectGameModeFromLog(cleanLine, _currentGameMode);
                            if (string.IsNullOrEmpty(_currentGameMode) && !string.IsNullOrEmpty(_currentMatchMode))
                                _currentGameMode = "Forests of Caldavar";

                            string stateMsg = string.IsNullOrEmpty(_accountName)
                                ? $"Playing as {_selectedHeroDisplayName}"
                                : $"{CleanAccountName} as {_selectedHeroDisplayName}";
                            string smallTip =
                                BuildHeroSmallTooltip("UnnamedNewbie");

                            UpdateLocalPresence($"Playing {_currentGameMode}", stateMsg, _selectedHeroAssetKey,
                                smallTip);
                            LogDebug(
                                $"[DiscordRPC] Live Hero Picked/Repicked (Slot {slot}): {_selectedHeroDisplayName} (AssetKey='{_selectedHeroAssetKey}')");
                        }
                    }

                return;
            }

            // 10. Detect Slotted Ability Upgrade (ABILITY_UPGRADE ... player:0 ... name:"Ability_Nomad1")
            Match slotAbilityMatch = Regex.Match(cleanLine,
                @"ABILITY_UPGRADE\s+.*player:(\d+)\s+.*name:""Ability_([A-Za-z0-9_]+?)\d""", RegexOptions.IgnoreCase);
            if (slotAbilityMatch.Success)
            {
                if (int.TryParse(slotAbilityMatch.Groups[1].Value, out int slot))
                    if (!_localPlayerSlot.HasValue || _localPlayerSlot.Value == slot)
                    {
                        _localPlayerSlot ??= slot;
                        string rawHero = slotAbilityMatch.Groups[2].Value;
                        (string AssetKey, string DisplayName)? resolved = ResolveHero(rawHero);
                        if (resolved.HasValue && _selectedHeroAssetKey != resolved.Value.AssetKey)
                        {
                            if (!_isQueueArcadeActive) _currentMatchMode = DetectMatchModeFromLog(cleanLine, _currentMatchMode);
                            _selectedHeroAssetKey = resolved.Value.AssetKey;
                            _selectedHeroDisplayName = resolved.Value.DisplayName;
                            _currentGameMode = DetectGameModeFromLog(cleanLine, _currentGameMode);
                            if (string.IsNullOrEmpty(_currentGameMode) && !string.IsNullOrEmpty(_currentMatchMode))
                                _currentGameMode = "Forests of Caldavar";

                            if (!_isQueueArcadeActive) _matchStartUtc ??= TimeProvider.System.GetUtcNow().UtcDateTime;
                            if (!_isQueueArcadeActive) _queueStartUtc = null;

                            string stateMsg = string.IsNullOrEmpty(_accountName)
                                ? $"Playing as {_selectedHeroDisplayName}"
                                : $"{CleanAccountName} as {_selectedHeroDisplayName}";
                            string smallTip =
                                BuildHeroSmallTooltip("In Match");

                            UpdateLocalPresence($"Playing {_currentGameMode}", stateMsg, _selectedHeroAssetKey,
                                smallTip);
                            LogDebug(
                                $"[DiscordRPC] Live Hero Determined from Ability (Slot {slot}): {_selectedHeroDisplayName} (AssetKey='{_selectedHeroAssetKey}')");
                        }
                    }

                return;
            }

            // 11. General Fallback Hero Pick in Local Single-Player / Practice Console Logs
            if (cleanLine.Contains("SpawnHero", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("SelectHero", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("Spawning Hero_", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("Selected hero", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("Morph", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("hero_cli_name", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("hero_name", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("picked_hero", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("selected_hero", StringComparison.OrdinalIgnoreCase))
            {
                Match morphSlotMatch = Regex.Match(cleanLine, @"\bMorph\s+(\d+)\s+", RegexOptions.IgnoreCase);
                if (morphSlotMatch.Success && int.TryParse(morphSlotMatch.Groups[1].Value, out int morphSlot))
                {
                    if (_localPlayerSlot.HasValue && _localPlayerSlot.Value != morphSlot)
                    {
                        return;
                    }
                }

                bool isExplicitLocalCommand = cleanLine.Contains("SpawnHero", StringComparison.OrdinalIgnoreCase) ||
                                              cleanLine.Contains("SelectHero", StringComparison.OrdinalIgnoreCase) ||
                                              cleanLine.Contains("Morph", StringComparison.OrdinalIgnoreCase);
                if (!string.IsNullOrEmpty(_selectedHeroAssetKey) && !isExplicitLocalCommand)
                {
                    return;
                }

                if (!_isQueueArcadeActive) _currentMatchMode = DetectMatchModeFromLog(cleanLine, _currentMatchMode);
                (string AssetKey, string DisplayName)? hero = TryDetectHeroFromLog(cleanLine);
                if (hero != null && _selectedHeroAssetKey != hero.Value.AssetKey)
                {
                    _selectedHeroAssetKey = hero.Value.AssetKey;
                    _selectedHeroDisplayName = hero.Value.DisplayName;
                    _currentGameMode = DetectGameModeFromLog(cleanLine, _currentGameMode);
                    if (string.IsNullOrEmpty(_currentGameMode) && !string.IsNullOrEmpty(_currentMatchMode))
                        _currentGameMode = "Forests of Caldavar";

                    if (!_isQueueArcadeActive) _matchStartUtc ??= TimeProvider.System.GetUtcNow().UtcDateTime;
                    if (!_isQueueArcadeActive) _queueStartUtc = null;

                    string stateMsg = string.IsNullOrEmpty(_accountName)
                        ? $"Playing as {_selectedHeroDisplayName}"
                        : $"{CleanAccountName} as {_selectedHeroDisplayName}";
                    string smallTip =
                        BuildHeroSmallTooltip("In Match");

                    UpdateLocalPresence($"Playing {_currentGameMode}", stateMsg, _selectedHeroAssetKey, smallTip);
                    LogDebug(
                        $"[DiscordRPC] Live Hero Picked/Spawned: {_selectedHeroDisplayName} (AssetKey='{_selectedHeroAssetKey}')");
                    return;
                }
            }

            // 12. Active Gameplay & Match Start Transitions
            if (cleanLine.Contains("['gamePhase'] = 6", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("gamePhase'] = 6", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("gamePhase6", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("gamePhase, INT: 6", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("gamePhase = 6", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("NETCMD_CLIENT_IN_GAME", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("NETCMD_FINISHED_LOADING_WORLD", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("StartMatch", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("Game Started", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("GAME_START", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("Gameplay started", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("soccer_mode", StringComparison.OrdinalIgnoreCase))
            {
                if (!_isQueueArcadeActive) _currentMatchMode = DetectMatchModeFromLog(cleanLine, _currentMatchMode);
                _currentGameMode = DetectGameModeFromLog(cleanLine, _currentGameMode);
                if (string.IsNullOrEmpty(_currentGameMode) && !string.IsNullOrEmpty(_currentMatchMode))
                    _currentGameMode = "Forests of Caldavar";

                if (!_isQueueArcadeActive) _matchStartUtc ??= TimeProvider.System.GetUtcNow().UtcDateTime;
                if (!_isQueueArcadeActive) _queueStartUtc = null;

                string stateMsg = !string.IsNullOrEmpty(_selectedHeroDisplayName)
                    ? string.IsNullOrEmpty(_accountName)
                        ? $"Playing as {_selectedHeroDisplayName}"
                        : $"{CleanAccountName} as {_selectedHeroDisplayName}"
                    : string.IsNullOrEmpty(_accountName)
                        ? "In Match"
                        : $"{CleanAccountName} (In Match)";
                string? smallTip = !string.IsNullOrEmpty(_selectedHeroDisplayName)
                    ? BuildHeroSmallTooltip("In Match")
                    : !string.IsNullOrEmpty(_accountName)
                        ? _accountName
                        : "In Match";

                UpdateLocalPresence($"Playing {_currentGameMode}", stateMsg, _selectedHeroAssetKey, smallTip);
                LogDebug($"[DiscordRPC] Active Game In-Progress for mode: {_currentGameMode}");
                return;
            }

            if (cleanLine.Contains("VICTORY", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("DEFEAT", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("MATCH_END", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("BASE_DESTROYED", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("Game Finished", StringComparison.OrdinalIgnoreCase))
            {
                ClearHostedLobbyState("Match End");
                if (_isQueueArcadeActive)
                {
                    ClearHeroSelectionState();
                    _matchStartUtc = null;
                    string cleanQueueName = CleanAccountName;
                    string queueState = string.IsNullOrEmpty(cleanQueueName) ? "Finding Match..." : $"{cleanQueueName} in Queue";
                    UpdateLocalPresence("In Queue", queueState, "small", BuildPlayerTooltipIdentity("In Queue"));
                    LogDebug($"[DiscordRPC] Queue Arcade round finished -> preserving continuous queue timer ({_queueStartUtc:O}).");
                    return;
                }

                _currentMatchMode = null;
                _currentGameMode = string.Empty;
                ClearHeroSelectionState();
                _localPlayerSlot = null;
                _matchStartUtc = null;
                ClearQueueState("Match End");
                _fastBusGamePhase = _hasLiveFastBus ? 0u : 1u;
                string smallTip = BuildPlayerTooltipIdentity("Player");
                string lobbyState = string.IsNullOrEmpty(CleanAccountName) ? "In Lobby" : $"{CleanAccountName} (In Lobby)";
                UpdateLocalPresence("In Main Menu", lobbyState, "small", smallTip);
                LogDebug($"[DiscordRPC] Match ended -> returned immediately to Main Menu ('{lobbyState}').");
                return;
            }

            if (!cleanLine.Contains("has disconnected", StringComparison.OrdinalIgnoreCase) &&
                (cleanLine.Contains("Disconnected from server", StringComparison.OrdinalIgnoreCase) ||
                 cleanLine.Contains("Disconnected from game server", StringComparison.OrdinalIgnoreCase) ||
                 cleanLine.Contains("Client disconnected", StringComparison.OrdinalIgnoreCase) ||
                 cleanLine.Contains("Left Game", StringComparison.OrdinalIgnoreCase) ||
                 cleanLine.Contains("NET_CHAT_CL_DISCONNECT", StringComparison.OrdinalIgnoreCase) ||
                 cleanLine.Contains("Sending disconnect notice", StringComparison.OrdinalIgnoreCase) ||
                 cleanLine.Contains("Shutting down client", StringComparison.OrdinalIgnoreCase) ||
                 cleanLine.Contains("Shutting down server", StringComparison.OrdinalIgnoreCase) ||
                 cleanLine.Contains("GameClient released", StringComparison.OrdinalIgnoreCase)))
            {
                ClearHostedLobbyState("Disconnect");
                DateTime nowUtc = TimeProvider.System.GetUtcNow().UtcDateTime;
                bool preserveQueue = ShouldPreserveContinuousQueueOnArcadeExit(nowUtc);
                if (preserveQueue && _queueStartUtc.HasValue)
                {
                    _recentArcadeQueueStartUtc ??= _queueStartUtc;
                    _recentArcadeDisconnectUtc = nowUtc;
                }

                SetQueueArcadeActive(false, "Client/Server disconnect", preserveRotationTimer: preserveQueue);
                _currentMatchMode = null;
                ClearHeroSelectionState();
                _localPlayerSlot = null;
                _matchStartUtc = null;
                _fastBusGamePhase = _hasLiveFastBus ? 0u : 1u;

                if (preserveQueue && _queueStartUtc.HasValue)
                {
                    _currentGameMode = _queuedGameMode;
                    string queueModeSuffix = !string.IsNullOrWhiteSpace(_currentGameMode) ? $" ({_currentGameMode})" : string.Empty;
                    string cleanQueueName = CleanAccountName;
                    string queueState = string.IsNullOrEmpty(cleanQueueName) ? "Finding Match..." : $"{cleanQueueName} in Queue";
                    UpdateLocalPresence($"In Matchmaking{queueModeSuffix}", queueState, "small", BuildPlayerTooltipIdentity("Player"));
                    LogDebug($"[DiscordRPC] Disconnect from Queue Arcade while still in queue -> preserved queued mode '{_currentGameMode}' & timer ({_queueStartUtc:O}).");
                    return;
                }

                ClearQueueState("Disconnect");
                _currentGameMode = string.Empty;
                string smallTip = string.IsNullOrEmpty(_accountName) ? "Player" : _accountName;
                UpdateLocalPresence("In Main Menu",
                    string.IsNullOrEmpty(CleanAccountName) ? "In Lobby" : $"{CleanAccountName} (In Lobby)", "small", smallTip);
            }
        }
        catch (Exception ex)
        {
            LogDebug($"[DiscordRPC] Line process exception: {ex.Message}");
        }
    }

    private bool HandleQueueArcadeLogTransitions(string cleanLine)
    {
        if (cleanLine.Contains("RANKED MATCH FOUND", StringComparison.OrdinalIgnoreCase))
        {
            ClearHostedLobbyState("RANKED MATCH FOUND");
            SetQueueArcadeActive(false, "RANKED MATCH FOUND");
            ClearQueueState("RANKED MATCH FOUND");
            _matchStartUtc = null;
            _currentMatchMode = null;
            ClearHeroSelectionState();
            _currentGameMode = DetectGameModeFromLog(cleanLine, string.Empty);
            string foundSuffix = !string.IsNullOrWhiteSpace(_currentGameMode) ? $" ({_currentGameMode})" : string.Empty;
            string joinState = !string.IsNullOrEmpty(CleanAccountName) ? $"{CleanAccountName} (Match Found)" : "Match Found";
            UpdateLocalPresence($"Joining Game{foundSuffix}...", joinState, "small", BuildPlayerTooltipIdentity("Player"));
            LogDebug($"[DiscordRPC] Queue Arcade exited on real match allocation -> '{cleanLine}'");
            return true;
        }

        if (cleanLine.Contains("Manual leave from Queue Arcade mini-game", StringComparison.OrdinalIgnoreCase))
        {
            DateTime nowUtc = TimeProvider.System.GetUtcNow().UtcDateTime;
            _queueStartUtc ??= nowUtc;
            _recentArcadeQueueStartUtc ??= _queueStartUtc;
            _recentArcadeDisconnectUtc = nowUtc;
            SetQueueArcadeActive(false, cleanLine, preserveRotationTimer: true);
            _currentMatchMode = null;
            _matchStartUtc = null;
            _localPlayerSlot = null;
            ClearHeroSelectionState();
            _currentGameMode = _queuedGameMode;
            string modeSuffix = !string.IsNullOrWhiteSpace(_currentGameMode) ? $" ({_currentGameMode})" : string.Empty;
            string cleanQueueName = CleanAccountName;
            string queueState = string.IsNullOrEmpty(cleanQueueName) ? "Finding Match..." : $"{cleanQueueName} in Queue";
            UpdateLocalPresence($"In Matchmaking{modeSuffix}", queueState, "small", BuildPlayerTooltipIdentity("Player"));
            LogDebug($"[DiscordRPC] Manual leave from Queue Arcade while staying in queue -> mode='{_currentGameMode}', queueStart={_queueStartUtc:O}.");
            return true;
        }

        if (cleanLine.Contains("Clean disconnect confirmed", StringComparison.OrdinalIgnoreCase))
        {
            DateTime nowUtc = TimeProvider.System.GetUtcNow().UtcDateTime;
            bool hasActiveQueue = _queueStartUtc.HasValue;
            if (hasActiveQueue)
            {
                _recentArcadeQueueStartUtc ??= _queueStartUtc;
                _recentArcadeDisconnectUtc = nowUtc;
            }
            SetQueueArcadeActive(false, cleanLine, preserveRotationTimer: hasActiveQueue);
            return false;
        }

        if ((cleanLine.Contains("Hiding top-left Queue Arcade banner", StringComparison.OrdinalIgnoreCase) &&
             cleanLine.Contains("inQueue=false", StringComparison.OrdinalIgnoreCase)) ||
            (cleanLine.Contains("[QueueArcade]", StringComparison.OrdinalIgnoreCase) &&
             cleanLine.Contains("inQueue=false", StringComparison.OrdinalIgnoreCase)) ||
            cleanLine.Contains("Queue pop pre-transfer window expired without active queue", StringComparison.OrdinalIgnoreCase))
        {
            SetQueueArcadeActive(false, cleanLine);
            ClearQueueState(cleanLine);
            return false;
        }

        if (cleanLine.Contains("isQueueArcade=false", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(
                cleanLine,
                @"_stats_is_queue_arcade_match\s*(?:=|\s+|,\s*(?:BOOL|STRING|INT):\s*)""?(?:false|0)""?\b",
                RegexOptions.IgnoreCase))
        {
            SetQueueArcadeActive(false, cleanLine);
            if (!_isQueueArcadeActive)
            {
                _recentArcadeQueueStartUtc = null;
                _recentArcadeDisconnectUtc = null;
            }
            return false;
        }

        if (cleanLine.Contains("Left matchmaking", StringComparison.OrdinalIgnoreCase) ||
            cleanLine.Contains("Removed from matchmaking", StringComparison.OrdinalIgnoreCase) ||
            cleanLine.Contains("Cancelled matchmaking", StringComparison.OrdinalIgnoreCase) ||
            cleanLine.Contains("CancelMatchmaking", StringComparison.OrdinalIgnoreCase) ||
            cleanLine.Contains("LeaveMatchmaking", StringComparison.OrdinalIgnoreCase))
        {
            ClearHostedLobbyState("Left matchmaking");
            SetQueueArcadeActive(false, "Left matchmaking");
            ClearQueueState("Left matchmaking");
            _matchStartUtc = null;
            _currentMatchMode = null;
            _currentGameMode = string.Empty;
            ClearHeroSelectionState();
            string lobbyState = string.IsNullOrEmpty(CleanAccountName) ? "In Lobby" : $"{CleanAccountName} (In Lobby)";
            UpdateLocalPresence("In Main Menu", lobbyState, "small", BuildPlayerTooltipIdentity("Player"));
            LogDebug($"[DiscordRPC] Left matchmaking queue -> cleared Queue Arcade & queue timer: '{cleanLine}'");
            return true;
        }

        if (cleanLine.Contains("StartPractice", StringComparison.OrdinalIgnoreCase) ||
            cleanLine.Contains("PracticeMatch", StringComparison.OrdinalIgnoreCase) ||
            cleanLine.Contains("LocalBotsGame", StringComparison.OrdinalIgnoreCase) ||
            cleanLine.Contains("PracticeGame", StringComparison.OrdinalIgnoreCase) ||
            cleanLine.Contains("SysBar_LaunchPractice", StringComparison.OrdinalIgnoreCase) ||
            cleanLine.Contains("StartBotMatch", StringComparison.OrdinalIgnoreCase))
        {
            ClearHostedLobbyState("Practice/BotMatch");
            SetQueueArcadeActive(false, "Practice/BotMatch");
            ClearQueueState("Practice/BotMatch");
            return false;
        }

        // Ignore UI toggle syncs, help output, and post-game diagnostic logs mentioning Queue Arcade
        if (cleanLine.Contains("[QUEUE_ARCADE][STATE_SYNC]", StringComparison.OrdinalIgnoreCase) ||
            cleanLine.Contains("Loading_V2:OnClickJoinArcade", StringComparison.OrdinalIgnoreCase) ||
            cleanLine.Contains("Dispatching chat command", StringComparison.OrdinalIgnoreCase) ||
            cleanLine.Contains("Dispatching in-game chat command", StringComparison.OrdinalIgnoreCase) ||
            cleanLine.Contains("Injecting Project KONGOR /arcade help", StringComparison.OrdinalIgnoreCase) ||
            cleanLine.Contains("Queue Mini-Games DISABLED", StringComparison.OrdinalIgnoreCase) ||
            cleanLine.Contains("Commands: /arcade", StringComparison.OrdinalIgnoreCase) ||
            cleanLine.Contains("Unknown subcommand", StringComparison.OrdinalIgnoreCase) ||
            cleanLine.Contains("AfterMatchStatsTrigger", StringComparison.OrdinalIgnoreCase) ||
            cleanLine.Contains("ChatShowPostGameStats", StringComparison.OrdinalIgnoreCase) ||
            cleanLine.Contains("Skipping post-game stats", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        bool isExplicitMatchArcadeSignal =
            cleanLine.Contains("LobbyGameInfo detected Queue Arcade match", StringComparison.OrdinalIgnoreCase) ||
            cleanLine.Contains("Showing top-left Queue Arcade banner", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(
                cleanLine,
                @"_stats_is_queue_arcade_match\s*(?:=|\s+|,\s*(?:BOOL|STRING|INT):\s*)""?(?:true|1)""?\b",
                RegexOptions.IgnoreCase);

        bool isLegacyFallbackArcadeSignal =
            !_hasSeenStructuredArcadeTelemetry &&
            (cleanLine.Contains("Joining game 'QUEUE ARCADE", StringComparison.OrdinalIgnoreCase) ||
             Regex.IsMatch(cleanLine, @"\bQUEUE ARCADE\s*(?:#Q\d+|\[)", RegexOptions.IgnoreCase));

        if (isExplicitMatchArcadeSignal || isLegacyFallbackArcadeSignal)
        {
            bool wasActive = _isQueueArcadeActive;
            if (!wasActive && string.IsNullOrEmpty(_queuedGameMode) && _queueStartUtc.HasValue && !string.IsNullOrWhiteSpace(_currentGameMode))
            {
                _queuedGameMode = _currentGameMode;
                LogDebug($"[DiscordRPC] Snapshotted queued mode '{_queuedGameMode}' before entering Queue Arcade.");
            }
            SetQueueArcadeActive(true, cleanLine);
            _currentMatchMode = null;
            _matchStartUtc = null;
            EnsureQueueArcadeTimerAnchored();

            string previousMap = _currentGameMode;
            _currentGameMode = DetectGameModeFromLog(cleanLine, _currentGameMode);

            bool hasInlineHeroSignal =
                cleanLine.Contains("SpawnHero", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("SelectHero", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("PLAYER_SELECT", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("PLAYER_SPAWN", StringComparison.OrdinalIgnoreCase) ||
                cleanLine.Contains("Client #", StringComparison.OrdinalIgnoreCase);

            if (!hasInlineHeroSignal &&
                (!wasActive || !string.Equals(previousMap, _currentGameMode, StringComparison.OrdinalIgnoreCase)))
            {
                string cleanQueueName = CleanAccountName;
                string queueState = string.IsNullOrEmpty(cleanQueueName) ? "Finding Match..." : $"{cleanQueueName} in Queue";
                UpdateLocalPresence("In Queue", queueState, _selectedHeroAssetKey ?? "small", BuildPlayerTooltipIdentity("In Queue"));
                LogDebug($"[DiscordRPC] Queue Arcade active (Map='{_currentGameMode}', QueueStartUtc='{_queueStartUtc:O}').");
            }

            return !hasInlineHeroSignal;
        }

        return false;
    }

    public static bool IsBaseHeroAvatarToken(
        string? candidateAvatar,
        string baseAssetKey,
        string baseDisplayName,
        string? rawHeroToken = null)
    {
        if (IsPickerDummyHero(candidateAvatar))
            return true;

        string trimmed = candidateAvatar!.Trim();
        if (trimmed.Equals("Base", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("Default", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Hero_", StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(baseDisplayName) && trimmed.Equals(baseDisplayName, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(baseAssetKey) && trimmed.Equals(baseAssetKey, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(rawHeroToken) && trimmed.Equals(rawHeroToken.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (!trimmed.Contains('.') && !string.IsNullOrWhiteSpace(baseAssetKey))
        {
            (string AssetKey, string DisplayName)? resolved = ResolveHero(trimmed);
            if (resolved.HasValue &&
                resolved.Value.AssetKey.Equals(baseAssetKey, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static string? DetectMatchModeFromLog(string line, string? currentMode = null)
    {
        if (string.IsNullOrWhiteSpace(line) || IsDevConsoleLine(line)) return currentMode;

        string lower = line.ToLowerInvariant();
        if (((lower.Contains(".skin") || lower.Contains("_skin")) && !lower.Contains("map") && !lower.Contains("mode")) ||
            lower.Contains("/heroes/") ||
            ContainsNonGameplayAssetPath(lower) ||
            lower.Contains("[kongor-bots]") || lower.Contains("queue arcade") ||
            lower.Contains("[queue_arcade]") || lower.Contains("_stats_is_queue_arcade_match=true") ||
            lower.Contains("[nexus_globals]") || lower.Contains("rewardstat_ctf")) return currentMode;

        // Check for bot match indicators first (more specific than general practice)
        if (lower.Contains("botmatch") || lower.Contains("bot_match") || lower.Contains("bot match") ||
            lower.Contains("localbotsgame") || lower.Contains("mode:botmatch") || lower.Contains("mode:bot") ||
            lower.Contains("startbotmatch") || lower.Contains("bot_game") || Regex.IsMatch(lower, @"\brandombots:(?!0\|0\b)\d+\|\d+\b") ||
            lower.Contains("addfakeplayer") || lower.Contains("ui_local_bot_game"))

        {
            return "Bot Match";
        }

        // Check for practice match indicators
        if (lower.Contains("practice") || lower.Contains("startpractice") || lower.Contains("practicematch") ||
            lower.Contains("practicegame") || lower.Contains("mode:practice") || lower.Contains("startgame practice") ||
            lower.Contains("sysbar_launchpractice") || lower.Contains("launchpractice") ||
            lower.Contains("port: 11235") || lower.Contains("port, string: 11235") ||
            lower.Contains("127.0.0.1:11235") || lower.Contains("localhost:11235"))
        {
            return "Practice Match";
        }

        return currentMode;
    }

    public static string DetectGameModeFromLog(string line, string current)
    {
        if (string.IsNullOrWhiteSpace(line) || IsDevConsoleLine(line)) return current;

        string lower = line.ToLowerInvariant();
        if (((lower.Contains(".skin") || lower.Contains("_skin")) && !lower.Contains("map") && !lower.Contains("mode")) ||
            lower.Contains("/heroes/") ||
            ContainsNonGameplayAssetPath(lower) ||
            lower.Contains("[nexus_globals]") || lower.Contains("rewardstat_ctf")) return current;

        // Specific map variants before generic parent substrings (including 15-char FastBus CurrentMap[16] prefixes)
        if (lower.Contains("caldavar_hallow") || lower.Contains("forests of caldavar (halloween)"))
            return "Forests of Caldavar (Halloween)";
        if (lower.Contains("caldavar_wtf") || lower.Contains("forests of caldavar (wtf)"))
            return "Forests of Caldavar (WTF)";
        if (lower.Contains("caldavar_reborn") || lower.Contains("forests of caldavar reborn"))
            return "Forests of Caldavar Reborn";
        if (lower.Contains("caldavar_old") || lower.Contains("forests of caldavar (classic)"))
            return "Forests of Caldavar (Classic)";

        if (lower.Contains("midwars_hallowe") || lower.Contains("mid wars (halloween)"))
            return "Mid Wars (Halloween)";
        if (lower.Contains("midwars_10v10") || lower.Contains("mid wars (10v10 chaos)"))
            return "Mid Wars (10v10 Chaos)";
        if (lower.Contains("midwars_reborn") || lower.Contains("mid wars reborn"))
            return "Mid Wars Reborn";
        if (lower.Contains("mids_omg") || lower.Contains("mid wars classic"))
            return "Mid Wars Classic";

        if (lower.Contains("truedevo_10v10") || lower.Contains("true devo wars (10v10 chaos)"))
            return "True Devo Wars (10v10 Chaos)";
        if (lower.Contains("truedevo") || lower.Contains("true devo") || lower.Contains("true_devo"))
            return "True Devo Wars";
        if (lower.Contains("devowars_10v10") || lower.Contains("devo wars (10v10 chaos)"))
            return "Devo Wars (10v10 Chaos)";

        // 10 Additional Standalone Custom Maps
        if (lower.Contains("colosseumduel") || lower.Contains("colosseum_duel") || lower.Contains("colosseum duel"))
            return "Colosseum Duel";
        if (lower.Contains("empathmaze") || lower.Contains("empath_maze") || lower.Contains("empath maze"))
            return "Empath Maze";
        if (lower.Contains("nightmarishcoop") || lower.Contains("nightmarish_coop") || lower.Contains("nightmarish_coo") ||
            lower.Contains("nightmarish_004") || lower.Contains("nightmarish co-op") || lower.Contains("nightmarish coop"))
            return "Nightmarish Co-Op";
        if (lower.Contains("pebblesduel") || lower.Contains("pebbles_duel") || lower.Contains("pebbles duel"))
            return "Pebbles Duel";
        if (lower.Contains("persianpass") || lower.Contains("persian_pass") || lower.Contains("persian pass"))
            return "Persian Pass";
        if (lower.Contains("threekingdoms") || lower.Contains("three_kingdoms") || lower.Contains("three kingdoms"))
            return "Three Kingdoms";
        if (lower.Contains("valkwars") || lower.Contains("valk_wars") || lower.Contains("valkyrie wars"))
            return "Valkyrie Wars";
        if (Regex.IsMatch(lower, @"\b(?:map_archers|archers|archer_wars|archer wars)\b"))
            return "Archer Wars";
        if (Regex.IsMatch(lower, @"\b(?:map_enfos|enfos)\b") || lower.Contains("enfo's team survival") || lower.Contains("enfos team survival"))
            return "Enfo's Team Survival";
        if (!lower.Contains("garena") && (lower.Contains("hon arena") || lower.Contains("map_arena") || Regex.IsMatch(lower, @"^(?:arena)$|\b(?:map|mode)[:=\s""']+arena\b")))
            return "HoN Arena";

        if (Regex.IsMatch(lower, @"\b(?:capturetheflag|capture_the_flag|capture the flag|ctf)\b"))
            return "Capture the Flag";
        if (lower.Contains("midwars") || lower.Contains("mid wars") || lower.Contains("mid_wars")) return "Mid Wars";
        if (lower.Contains("caldavar") || lower.Contains("forests of caldavar") || lower.Contains("ranked"))
            return "Forests of Caldavar";
        if (lower.Contains("darkwoodvale") || lower.Contains("darkwood vale")) return "Darkwood Vale";
        if (lower.Contains("devowars") || lower.Contains("devo wars") || lower.Contains("devo_wars"))
            return "Devo Wars";
        if (lower.Contains("grimmscrossing") || lower.Contains("grimm's crossing") || lower.Contains("grimrnscrossing"))
            return "Grimm's Crossing";
        if (lower.Contains("prophets") && !lower.Contains("hero_prophet")) return "Prophets";
        if (lower.Contains("riftwars") || lower.Contains("rift wars")) return "Rift Wars";
        if (Regex.IsMatch(lower,
                @"\b(?:soccer|soccer_mode|map_strikers|game_strikers|strikers_match|playing strikers|strikers)\b") &&
            !lower.Contains("skin")) return "Strikers";
        if (lower.Contains("thegrimmhunt") || lower.Contains("grimm hunt")) return "The Grimm Hunt";
        if (Regex.IsMatch(lower, @"\b(?:team_deathmatch|teamdeathmatch|team deathmatch|team death match|deathmatch|tdm)\b"))
            return "Team Deathmatch";
        if (lower.Contains("spotlight")) return "Spotlight";
        if (lower.Contains("watchtower")) return "Watchtower";
        if (lower.Contains("tutorial")) return "Tutorial";

        if (string.IsNullOrEmpty(current) &&
            (lower.Contains("practice") || lower.Contains("botmatch") || lower.Contains("bot_match") || lower.Contains("bot match")))
            return "Forests of Caldavar";

        return current;
    }

    private async Task PollForAccountIdAsync(CancellationToken token)
    {
        string myDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string currentDir = Directory.GetCurrentDirectory();
        string? gameClientDir = LocationGuard.ResolveGameClientDirectory(currentDir)
                             ?? LocationGuard.ResolveGameClientDirectory(baseDir)
                             ?? LocationGuard.ResolveGameClientDirectory();

        List<string> searchRoots = [];
        if (!string.IsNullOrEmpty(gameClientDir))
        {
            searchRoots.Add(Path.Combine(gameClientDir, "userdata", "Heroes of Newerth x64", "KONGOR", "extensions"));
            searchRoots.Add(Path.Combine(gameClientDir, "userdata", "Heroes of Newerth x64", "game"));
            searchRoots.Add(Path.Combine(gameClientDir, "userdata", "Heroes of Newerth x64", "base"));
            searchRoots.Add(Path.Combine(gameClientDir, "userdata", "Heroes of Newerth x64"));
            searchRoots.Add(Path.Combine(gameClientDir, "userdata", ".Heroes of Newerth x64"));
            searchRoots.Add(Path.Combine(gameClientDir, "userdata", ".Heroes of Newerth"));
            searchRoots.Add(Path.Combine(gameClientDir, "userdata", "Library", "Application Support", "Heroes of Newerth"));
            searchRoots.Add(Path.Combine(gameClientDir, "userdata"));
            searchRoots.Add(gameClientDir);
        }

        searchRoots.AddRange(
        [
            Path.Combine(currentDir, "games", "client", "userdata", "Heroes of Newerth x64"),
            Path.Combine(currentDir, "games", "client", "userdata"),
            Path.Combine(currentDir, "userdata"),
            Path.Combine(baseDir, "userdata"),
            Path.Combine(currentDir, "games", "client"),
            Path.Combine(currentDir, "games", "shared"),
            baseDir,
            Path.Combine(userProfile, ".Heroes of Newerth x64"),
            Path.Combine(userProfile, ".Heroes of Newerth"),
            Path.Combine(userProfile, "Library", "Application Support", "Heroes of Newerth"),
            Path.Combine(userProfile, "OneDrive", "Documents", "Heroes of Newerth x64"),
            Path.Combine(myDocuments, "Heroes of Newerth x64"),
            Path.Combine(userProfile, "Documents", "Heroes of Newerth x64"),
            Path.Combine(appData, "Heroes of Newerth x64"),
            Path.Combine(localAppData, "Heroes of Newerth x64")
        ]);

        foreach (string root in searchRoots)
            if (Directory.Exists(root))
                TryDiscoverAccountNameFromConfig(root);

        Dictionary<string, long> fileOffsets = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, Encoding> fileEncodings = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> serverHostLogs = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> loggedWatchPaths = new(StringComparer.OrdinalIgnoreCase);
        bool initialIdentitySeeded = false;

        while (!token.IsCancellationRequested)
        {
            try
            {
                List<string> candidateFiles = [];

                foreach (string root in searchRoots)
                {
                    if (!Directory.Exists(root))
                        continue;

                    // Direct client console logs
                    string[] directLogs =
                    [
                        Path.Combine(root, "userdata", "Heroes of Newerth x64", "KONGOR", "extensions", "console.log"),
                        Path.Combine(root, "userdata", "Heroes of Newerth x64", "game", "console.log"),
                        Path.Combine(root, "userdata", "Heroes of Newerth x64", "base", "console.log"),
                        Path.Combine(root, "Heroes of Newerth x64", "KONGOR", "extensions", "console.log"),
                        Path.Combine(root, "Heroes of Newerth x64", "game", "console.log"),
                        Path.Combine(root, "Heroes of Newerth x64", "base", "console.log"),
                        Path.Combine(root, "userdata", "game", "console.log"),
                        Path.Combine(root, "userdata", "game", "logs", "console.log"),
                        Path.Combine(root, "userdata", "console.log"),
                        Path.Combine(root, "game", "console.log"),
                        Path.Combine(root, "game", "logs", "console.log"),
                        Path.Combine(root, "KONGOR", "extensions", "console.log"),
                        Path.Combine(root, "KONGOR", "configuration", "console.log"),
                        Path.Combine(root, "override", "console.log"),
                        Path.Combine(root, "client", "console.log"),
                        Path.Combine(root, "console.log")
                    ];

                    foreach (string directLog in directLogs)
                        if (File.Exists(directLog))
                            candidateFiles.Add(directLog);
                }

                // Dynamically discover all active console.log files under client userdata directory
                if (!string.IsNullOrEmpty(gameClientDir))
                {
                    string clientUserdata = Path.Combine(gameClientDir, "userdata");
                    if (Directory.Exists(clientUserdata))
                    {
                        try
                        {
                            foreach (string foundLog in Directory.GetFiles(clientUserdata, "console.log", SearchOption.AllDirectories))
                            {
                                candidateFiles.Add(foundLog);
                            }
                        }
                        catch { }
                    }
                }

                string legacyDocs = Path.Combine(myDocuments, "Heroes of Newerth x64");
                string legacyOneDrive = Path.Combine(userProfile, "OneDrive", "Documents", "Heroes of Newerth x64");
                string legacyDocsUser = Path.Combine(userProfile, "Documents", "Heroes of Newerth x64");

                // Process all active candidates with local userdata and recent files prioritized over legacy paths
                DateTime recentCutoff = TimeProvider.System.GetUtcNow().UtcDateTime.AddHours(-2);
                IEnumerable<string> orderedCandidates = candidateFiles
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(f => !f.StartsWith(legacyDocs, StringComparison.OrdinalIgnoreCase) &&
                                            !f.StartsWith(legacyOneDrive, StringComparison.OrdinalIgnoreCase) &&
                                            !f.StartsWith(legacyDocsUser, StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(f => f.Contains("userdata", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(f => File.GetLastWriteTimeUtc(f) > recentCutoff)
                    .ThenByDescending(File.GetLastWriteTimeUtc);

                foreach (string filePath in orderedCandidates)
                {
                    string fileName = Path.GetFileName(filePath);
                    if (fileName.StartsWith("Slave", StringComparison.OrdinalIgnoreCase) ||
                        (fileName.StartsWith("M", StringComparison.OrdinalIgnoreCase) &&
                         fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) ||
                        fileName.EndsWith(".clog", StringComparison.OrdinalIgnoreCase) ||
                        fileName.Equals("COMPEL.log", StringComparison.OrdinalIgnoreCase) ||
                        serverHostLogs.Contains(filePath))
                        continue;

                    long currentLength = new FileInfo(filePath).Length;
                    bool isInitialRead = !fileOffsets.TryGetValue(filePath, out long lastOffset);
                    if (isInitialRead)
                    {
                        lastOffset = 0;
                        fileOffsets[filePath] = 0;
                    }

                    if (currentLength < lastOffset)
                    {
                        lastOffset = 0;
                        fileEncodings.Remove(filePath);
                    }

                    if (currentLength > lastOffset)
                    {
                        List<string> linesRead = await ReadNewLogLinesAsync(filePath, lastOffset, fileEncodings, token);
                        fileOffsets[filePath] = currentLength;

                        bool allowInitialIdentityReplay = isInitialRead &&
                                                          !initialIdentitySeeded &&
                                                          File.GetLastWriteTimeUtc(filePath) > TimeProvider.System.GetUtcNow().UtcDateTime.AddHours(-24);
                        bool matchedClientIdentityInInitialRead = false;

                        BeginLogBatch();
                        try
                        {
                            foreach (string l in linesRead)
                            {
                                if (l.Contains("Starting Dedicated Server", StringComparison.OrdinalIgnoreCase) ||
                                    l.Contains("MatchServerManager", StringComparison.OrdinalIgnoreCase) ||
                                    l.Contains("COMPEL", StringComparison.OrdinalIgnoreCase))
                                {
                                    serverHostLogs.Add(filePath);
                                    break;
                                }

                                if (loggedWatchPaths.Add(filePath))
                                {
                                    LogDebug($"[DiscordRPC] Actively streaming client console log: {filePath}");
                                }

                                if (isInitialRead)
                                {
                                    if (!allowInitialIdentityReplay)
                                        continue;

                                    // On initial attach to the single newest active client console.log, only extract account identity
                                    // so stale match/hero/account lines from older secondary logs do not overwrite launch presence.
                                    if (l.Contains("Login success:", StringComparison.OrdinalIgnoreCase) ||
                                        l.Contains("login_name", StringComparison.OrdinalIgnoreCase) ||
                                        l.Contains("svr_login", StringComparison.OrdinalIgnoreCase) ||
                                        l.Contains("nickname", StringComparison.OrdinalIgnoreCase) ||
                                        l.Contains("account_id", StringComparison.OrdinalIgnoreCase) ||
                                        l.Contains("[HOMEPAGE_LUA]", StringComparison.OrdinalIgnoreCase) ||
                                        Regex.IsMatch(l, @"^g\d+\|", RegexOptions.IgnoreCase))
                                    {
                                        ProcessConsoleLogLine(l);
                                        matchedClientIdentityInInitialRead = !string.IsNullOrEmpty(_accountName);
                                    }
                                }
                                else
                                {
                                    ProcessConsoleLogLine(l);
                                }
                            }
                        }
                        finally
                        {
                            EndLogBatch();
                        }

                        if (allowInitialIdentityReplay && !serverHostLogs.Contains(filePath) && matchedClientIdentityInInitialRead)
                        {
                            initialIdentitySeeded = true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogDebug($"[DiscordRPC] PollForAccountIdAsync exception: {ex.Message}");
            }

            await Task.Delay(500, token);
        }
    }

    private void TryDiscoverAccountNameFromConfig(string root)
    {
        try
        {
            if (!string.IsNullOrEmpty(_accountName) &&
                !_accountName.Equals("UnnamedNewbie", StringComparison.OrdinalIgnoreCase))
                return;

            string[] candidateConfigs =
            [
                Path.Combine(root, "userdata", "Heroes of Newerth x64", "KONGOR", "extensions", "startup.cfg"),
                Path.Combine(root, "userdata", "Heroes of Newerth x64", "KONGOR", "extensions", "login.cfg"),
                Path.Combine(root, "userdata", "Heroes of Newerth x64", "base", "startup.cfg"),
                Path.Combine(root, "userdata", "Heroes of Newerth x64", "base", "login.cfg"),
                Path.Combine(root, "userdata", "Heroes of Newerth x64", "game", "startup.cfg"),
                Path.Combine(root, "userdata", "Heroes of Newerth x64", "game", "login.cfg"),
                Path.Combine(root, "Heroes of Newerth x64", "KONGOR", "extensions", "startup.cfg"),
                Path.Combine(root, "Heroes of Newerth x64", "KONGOR", "extensions", "login.cfg"),
                Path.Combine(root, "Heroes of Newerth x64", "base", "startup.cfg"),
                Path.Combine(root, "Heroes of Newerth x64", "base", "login.cfg"),
                Path.Combine(root, "Heroes of Newerth x64", "game", "startup.cfg"),
                Path.Combine(root, "Heroes of Newerth x64", "game", "login.cfg"),
                Path.Combine(root, "userdata", "game", "startup.cfg"),
                Path.Combine(root, "userdata", "game", "login.cfg"),
                Path.Combine(root, "userdata", "startup.cfg"),
                Path.Combine(root, "userdata", "login.cfg"),
                Path.Combine(root, "game", "startup.cfg"),
                Path.Combine(root, "game", "login.cfg"),
                Path.Combine(root, "KONGOR", "extensions", "startup.cfg"),
                Path.Combine(root, "startup.cfg"),
                Path.Combine(root, "login.cfg"),
                Path.Combine(root, "configuration", "startup.cfg"),
                Path.Combine(root, "configuration", "login.cfg")
            ];

            foreach (string cfg in candidateConfigs)
                if (File.Exists(cfg))
                {
                    string text = ReadConfigFileText(cfg);
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    Match m = Regex.Match(text,
                        @"(?:SetSave|Set)?\s*""?(?:login_name|svr_login)""?\s+""?([^""\r\n\s]+)""?",
                        RegexOptions.IgnoreCase);
                    if (m.Success && !string.IsNullOrWhiteSpace(m.Groups[1].Value))
                    {
                        string candidate = m.Groups[1].Value.Trim();
                        if (IsDisallowedPreloadedConfigName(candidate)) continue;
                        SetAccountName(candidate, isAuthoritative: false);
                        LogDebug($"[DiscordRPC] Discovered initial login name from config: '{_accountName}'");
                        break;
                    }
                }
        }
        catch
        {
        }
    }

    public static (Encoding Encoding, int BomLength) DetectLogStreamEncoding(Stream stream)
    {
        long savedPos = stream.Position;
        stream.Seek(0, SeekOrigin.Begin);
        Span<byte> header = stackalloc byte[4];
        int read = stream.Read(header);
        stream.Seek(savedPos, SeekOrigin.Begin);

        if (read >= 2 && header[0] == 0xFF && header[1] == 0xFE)
            return (new UnicodeEncoding(bigEndian: false, byteOrderMark: false), 2);
        if (read >= 2 && header[0] == 0xFE && header[1] == 0xFF)
            return (new UnicodeEncoding(bigEndian: true, byteOrderMark: false), 2);
        if (read >= 3 && header[0] == 0xEF && header[1] == 0xBB && header[2] == 0xBF)
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 3);
        if (read >= 4 && header[1] == 0x00 && header[3] == 0x00)
            return (new UnicodeEncoding(bigEndian: false, byteOrderMark: false), 0);

        return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 0);
    }

    public static async Task<List<string>> ReadNewLogLinesAsync(
        string filePath,
        long startOffset,
        Dictionary<string, Encoding> fileEncodings,
        CancellationToken token = default)
    {
        List<string> lines = [];
        using FileStream fs = new(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (!fileEncodings.TryGetValue(filePath, out Encoding? encoding))
        {
            (Encoding detected, int bomLen) = DetectLogStreamEncoding(fs);
            encoding = detected;
            fileEncodings[filePath] = encoding;
            if (startOffset == 0 && bomLen > 0)
            {
                startOffset = bomLen;
            }
        }

        if (encoding is UnicodeEncoding && (startOffset & 1) != 0)
        {
            startOffset = Math.Max(0, startOffset - 1);
        }

        if (startOffset >= fs.Length)
        {
            return lines;
        }

        fs.Seek(startOffset, SeekOrigin.Begin);
        using StreamReader reader = new(fs, encoding, detectEncodingFromByteOrderMarks: false);
        while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }

    private void ProcessNexusTelemetry(string line)
    {
        try
        {
            int idx = line.IndexOf("[NEXUS_TELEMETRY]", StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                // [NEXUS_EVENT] carries non-DRP UI telemetry; ignore so it never mutates hero or lobby state.
                return;
            }

            string payload = line[(idx + "[NEXUS_TELEMETRY]".Length)..].Trim();
            int pipeIdx = payload.IndexOf('|');
            if (pipeIdx >= 0 && (!payload.StartsWith('{') || pipeIdx < payload.IndexOf('{')))
            {
                payload = payload[(pipeIdx + 1)..].Trim();
            }

            Dictionary<string, string> dict = new(StringComparer.OrdinalIgnoreCase);
            if (payload.StartsWith('{'))
            {
                using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(payload);
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    foreach (System.Text.Json.JsonProperty prop in doc.RootElement.EnumerateObject())
                    {
                        string val = prop.Value.ValueKind == System.Text.Json.JsonValueKind.String
                            ? prop.Value.GetString() ?? string.Empty
                            : prop.Value.ToString();
                        dict[prop.Name.Trim()] = val.Trim();
                    }
                }
            }
            else
            {
                string[] pairs = payload.Split(';', StringSplitOptions.RemoveEmptyEntries);
                foreach (string pair in pairs)
                {
                    string[] kv = pair.Split('=', 2);
                    if (kv.Length == 2) dict[kv[0].Trim()] = kv[1].Trim();
                }
            }

            if (dict.TryGetValue("player", out string? playerNick) && !string.IsNullOrWhiteSpace(playerNick))
            {
                if (!string.IsNullOrEmpty(_accountName) &&
                    !IsPlaceholderPlayerName(playerNick) &&
                    !IsLocalPlayerName(playerNick))
                {
                    LogDebug($"[DiscordRPC] Ignored foreign [NEXUS_TELEMETRY] packet for player '{playerNick}' (local: '{_accountName}').");
                    return;
                }

                SetAccountName(playerNick);
            }

            dict.TryGetValue("status", out string? earlyStatus);
            if (_hasLiveFastBus && _fastBusGamePhase == 0 && !string.IsNullOrWhiteSpace(earlyStatus) &&
                (earlyStatus.Equals("InGame", StringComparison.OrdinalIgnoreCase) ||
                 earlyStatus.Equals("Playing", StringComparison.OrdinalIgnoreCase) ||
                 earlyStatus.Equals("HeroSelect", StringComparison.OrdinalIgnoreCase) ||
                 earlyStatus.Equals("PickingPhase", StringComparison.OrdinalIgnoreCase) ||
                 earlyStatus.Equals("BanningPhase", StringComparison.OrdinalIgnoreCase) ||
                 earlyStatus.Equals("Banning", StringComparison.OrdinalIgnoreCase)))
            {
                LogDebug($"[DiscordRPC] Ignored stale post-match telemetry status='{earlyStatus}' while FastBus phase=0 (Main Menu).");
                return;
            }

            bool isHostedLobbyStatus = !string.IsNullOrWhiteSpace(earlyStatus) &&
                                       (earlyStatus.Equals("HostedLobby", StringComparison.OrdinalIgnoreCase) ||
                                        earlyStatus.Equals("CustomLobby", StringComparison.OrdinalIgnoreCase));

            if (dict.TryGetValue("slots", out string? slotsRatioStr) && !string.IsNullOrWhiteSpace(slotsRatioStr))
            {
                string[] slotParts = slotsRatioStr.Split('/', 2, StringSplitOptions.TrimEntries);
                if (int.TryParse(slotParts[0], out int sCur))
                {
                    int sMax = slotParts.Length == 2 && int.TryParse(slotParts[1], out int parsedSlotMax)
                        ? parsedSlotMax
                        : 10;
                    UpdateHostedLobbySlots(sCur, sMax);
                }
            }

            if (dict.TryGetValue("party", out string? partyRatioStr) && !string.IsNullOrWhiteSpace(partyRatioStr))
            {
                string[] partyParts = partyRatioStr.Split('/', 2, StringSplitOptions.TrimEntries);
                if (int.TryParse(partyParts[0], out int pSizeRatio))
                {
                    int pMaxRatio = partyParts.Length == 2 && int.TryParse(partyParts[1], out int parsedMax)
                        ? parsedMax
                        : 5;
                    if (isHostedLobbyStatus)
                        UpdateHostedLobbySlots(pSizeRatio, pMaxRatio);
                    else
                        UpdateParty(pSizeRatio, pMaxRatio);
                }
            }
            else if (dict.TryGetValue("partysize", out string? pSizeStr) || dict.TryGetValue("party_size", out pSizeStr))
            {
                if (int.TryParse(pSizeStr, out int pSize))
                {
                    int pMax = 5;
                    if (dict.TryGetValue("partymax", out string? pMaxStr) || dict.TryGetValue("party_max", out pMaxStr))
                    {
                        int.TryParse(pMaxStr, out pMax);
                    }
                    if (isHostedLobbyStatus)
                        UpdateHostedLobbySlots(pSize, pMax);
                    else
                        UpdateParty(pSize, pMax);
                }
            }

            if (dict.TryGetValue("arcade", out string? arcadeVal) && !string.IsNullOrWhiteSpace(arcadeVal))
            {
                _hasSeenStructuredArcadeTelemetry = true;
                bool arcadeAllowedForStatus = string.IsNullOrWhiteSpace(earlyStatus) ||
                                              earlyStatus.Equals("Loading", StringComparison.OrdinalIgnoreCase) ||
                                              earlyStatus.Equals("Connecting", StringComparison.OrdinalIgnoreCase) ||
                                              earlyStatus.Equals("BanningPhase", StringComparison.OrdinalIgnoreCase) ||
                                              earlyStatus.Equals("Banning", StringComparison.OrdinalIgnoreCase) ||
                                              earlyStatus.Equals("HeroSelect", StringComparison.OrdinalIgnoreCase) ||
                                              earlyStatus.Equals("PickingPhase", StringComparison.OrdinalIgnoreCase) ||
                                              earlyStatus.Equals("InGame", StringComparison.OrdinalIgnoreCase) ||
                                              earlyStatus.Equals("Playing", StringComparison.OrdinalIgnoreCase);

                if (arcadeAllowedForStatus &&
                    (arcadeVal.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                     arcadeVal.Equals("true", StringComparison.OrdinalIgnoreCase)))
                {
                    SetQueueArcadeActive(true, $"[NEXUS_TELEMETRY] status={earlyStatus ?? "none"};arcade={arcadeVal}");
                    _currentMatchMode = null;
                    _matchStartUtc = null;
                    EnsureQueueArcadeTimerAnchored();
                }
                else if (arcadeVal.Equals("0", StringComparison.OrdinalIgnoreCase) ||
                         arcadeVal.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                         !arcadeAllowedForStatus)
                {
                    SetQueueArcadeActive(false, $"[NEXUS_TELEMETRY] status={earlyStatus ?? "none"};arcade={arcadeVal}");
                }
            }

            if (dict.TryGetValue("repick", out string? repickVal) &&
                (repickVal.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                 repickVal.Equals("true", StringComparison.OrdinalIgnoreCase)))
            {
                ClearHeroSelectionState();
                LogDebug($"[DiscordRPC] Telemetry repick detected for '{_accountName}' -> cleared active hero selection.");
            }

            string? explicitAvatar = dict.TryGetValue("avatar", out string? rawAvatar) && !string.IsNullOrWhiteSpace(rawAvatar)
                ? rawAvatar.Trim()
                : null;

            if (dict.TryGetValue("hero", out string? heroName) && !string.IsNullOrWhiteSpace(heroName) && !IsPickerDummyHero(heroName))
            {
                bool isLocalPlayer = !dict.TryGetValue("player", out string? pName) ||
                                     string.IsNullOrWhiteSpace(pName) ||
                                     IsLocalPlayerName(pName);

                if (isLocalPlayer)
                {
                    (string AssetKey, string DisplayName)? hero = ResolveHero(heroName);
                    if (hero.HasValue)
                    {
                        bool switchedBaseHero = !string.Equals(_selectedHeroAssetKey, hero.Value.AssetKey, StringComparison.OrdinalIgnoreCase);
                        _selectedHeroAssetKey = hero.Value.AssetKey;
                        _selectedHeroDisplayName = hero.Value.DisplayName;

                        if (!string.IsNullOrWhiteSpace(explicitAvatar) &&
                            !IsBaseHeroAvatarToken(explicitAvatar, _selectedHeroAssetKey, _selectedHeroDisplayName, heroName))
                        {
                            _selectedAvatarDisplayName = explicitAvatar;
                        }
                        else if (switchedBaseHero ||
                                 (!string.IsNullOrWhiteSpace(explicitAvatar) &&
                                  IsBaseHeroAvatarToken(explicitAvatar, _selectedHeroAssetKey, _selectedHeroDisplayName, heroName)) ||
                                 (dict.TryGetValue("status", out string? heroStatus) &&
                                  heroStatus.Equals("HeroSelect", StringComparison.OrdinalIgnoreCase) &&
                                  !heroName.Contains('.') &&
                                  string.IsNullOrWhiteSpace(explicitAvatar)))
                        {
                            _selectedAvatarDisplayName = null;
                        }
                        else if (!switchedBaseHero &&
                                 string.IsNullOrWhiteSpace(explicitAvatar) &&
                                 !string.IsNullOrEmpty(_selectedAvatarDisplayName))
                        {
                            LogDebug($"[DiscordRPC] Preserved existing Alt Avatar '{_selectedAvatarDisplayName}' across base-hero InGame telemetry ('{heroName}').");
                        }
                    }
                    else
                    {
                        // Guard against GetViewHeroName() emitting an Alt Avatar's localized display name (e.g. "Abyssal Bubbles" or "Pimp Slayer")
                        // when we already locked onto a valid base hero portrait ("hero_*") during pick/spawn!
                        bool hasExistingValidHeroPortrait = !string.IsNullOrEmpty(_selectedHeroAssetKey) &&
                                                            _selectedHeroAssetKey.StartsWith("hero_", StringComparison.OrdinalIgnoreCase) &&
                                                            ValidAssetKeys.Contains(_selectedHeroAssetKey);
                        if (hasExistingValidHeroPortrait)
                        {
                            if (!IsBaseHeroAvatarToken(heroName, _selectedHeroAssetKey!, _selectedHeroDisplayName ?? string.Empty))
                            {
                                _selectedAvatarDisplayName = heroName.Trim();
                            }
                        }
                        else
                        {
                            _selectedHeroAssetKey = heroName.ToLowerInvariant();
                            _selectedHeroDisplayName = heroName;
                        }
                    }

                    LogDebug(
                        $"[DiscordRPC] Telemetry Hero Resolved for Local Player '{_accountName}': '{_selectedHeroDisplayName}' (Key='{_selectedHeroAssetKey}', Avatar='{_selectedAvatarDisplayName ?? "Base"}')");
                }
            }
            else if (!string.IsNullOrWhiteSpace(explicitAvatar))
            {
                if (IsBaseHeroAvatarToken(explicitAvatar, _selectedHeroAssetKey ?? string.Empty, _selectedHeroDisplayName ?? string.Empty))
                {
                    _selectedAvatarDisplayName = null;
                }
                else
                {
                    _selectedAvatarDisplayName = explicitAvatar;
                }
            }

            bool isQueueStatusPacket = !string.IsNullOrWhiteSpace(earlyStatus) &&
                                       (earlyStatus.Equals("Queue", StringComparison.OrdinalIgnoreCase) ||
                                        earlyStatus.Equals("Matchmaking", StringComparison.OrdinalIgnoreCase));

            if (dict.TryGetValue("mode", out string? modeVal) && !string.IsNullOrWhiteSpace(modeVal))
            {
                if (!_isQueueArcadeActive)
                {
                    _currentMatchMode = DetectMatchModeFromLog(modeVal, _currentMatchMode);
                }

                if (isQueueStatusPacket && _isQueueArcadeActive)
                {
                    string parsedQueueMode = DetectGameModeFromLog(modeVal, _queuedGameMode);
                    if (!string.IsNullOrWhiteSpace(parsedQueueMode))
                        _queuedGameMode = parsedQueueMode;
                }
                else
                {
                    _currentGameMode = DetectGameModeFromLog(modeVal, _currentGameMode);
                    if (string.IsNullOrEmpty(_currentGameMode) && !string.IsNullOrEmpty(_currentMatchMode))
                        _currentGameMode = "Forests of Caldavar";
                    if (isQueueStatusPacket && !_isQueueArcadeActive && !string.IsNullOrWhiteSpace(_currentGameMode))
                        _queuedGameMode = _currentGameMode;
                }
            }

            if (dict.TryGetValue("map", out string? mapVal) && !string.IsNullOrWhiteSpace(mapVal))
            {
                if (isQueueStatusPacket && _isQueueArcadeActive)
                {
                    string parsedQueueMap = DetectGameModeFromLog(mapVal, _queuedGameMode);
                    if (!string.IsNullOrWhiteSpace(parsedQueueMap))
                        _queuedGameMode = parsedQueueMap;
                }
                else
                {
                    _currentGameMode = DetectGameModeFromLog(mapVal, _currentGameMode);
                    if (isQueueStatusPacket && !_isQueueArcadeActive && !string.IsNullOrWhiteSpace(_currentGameMode))
                        _queuedGameMode = _currentGameMode;
                }
            }

            string displayPlayer = string.IsNullOrEmpty(_accountName) ? "Player" : _accountName;
            string cleanDisplayPlayer = string.IsNullOrEmpty(CleanAccountName) ? "Player" : CleanAccountName;

            if (dict.TryGetValue("status", out string? status) && !string.IsNullOrWhiteSpace(status))
            {
                if (!_isQueueArcadeActive)
                {
                    _currentMatchMode = DetectMatchModeFromLog(status, _currentMatchMode);
                }

                if (string.IsNullOrEmpty(_currentGameMode) && !string.IsNullOrEmpty(_currentMatchMode))
                    _currentGameMode = "Forests of Caldavar";

                if (status.Equals("Lobby", StringComparison.OrdinalIgnoreCase) ||
                    status.Equals("MainMenu", StringComparison.OrdinalIgnoreCase) ||
                    status.Equals("Party", StringComparison.OrdinalIgnoreCase))
                {
                    ClearHostedLobbyState($"[NEXUS_TELEMETRY] status={status}");
                    SetQueueArcadeActive(false, $"[NEXUS_TELEMETRY] status={status}");
                    ClearQueueState($"[NEXUS_TELEMETRY] status={status}");
                    _currentMatchMode = null;
                    _currentGameMode = string.Empty;
                    ClearHeroSelectionState();
                    _localPlayerSlot = null;
                    _matchStartUtc = null;
                    bool inParty = _partySize > 1;
                    string lobbySuffix = inParty ? "In Party" : "In Lobby";
                    string lobbyState = string.IsNullOrEmpty(CleanAccountName) ? lobbySuffix : $"{CleanAccountName} ({lobbySuffix})";
                    UpdateLocalPresence("In Main Menu", lobbyState, "small", displayPlayer);
                    LogDebug($"[DiscordRPC] Direct Telemetry Transition to Lobby: {lobbyState}");
                    return;
                }

                if (status.Equals("HostedLobby", StringComparison.OrdinalIgnoreCase) ||
                    status.Equals("CustomLobby", StringComparison.OrdinalIgnoreCase))
                {
                    SetQueueArcadeActive(false, $"[NEXUS_TELEMETRY] status={status}");
                    ClearQueueState($"[NEXUS_TELEMETRY] status={status}");
                    _matchStartUtc = null;
                    ClearHeroSelectionState();
                    string hostedState = string.IsNullOrEmpty(CleanAccountName)
                        ? "Waiting for Players"
                        : $"{CleanAccountName} (Waiting for Players)";
                    string hostedDetails = !string.IsNullOrEmpty(_currentGameMode)
                        ? $"Custom Lobby ({_currentGameMode})"
                        : "Custom Lobby";
                    UpdateLocalPresence(hostedDetails, hostedState, "small", displayPlayer);
                    LogDebug($"[DiscordRPC] Direct Telemetry Transition to Hosted Lobby: {hostedDetails} | {hostedState}");
                    return;
                }

                if (status.Equals("Spectating", StringComparison.OrdinalIgnoreCase) ||
                    status.Equals("Replay", StringComparison.OrdinalIgnoreCase))
                {
                    ClearHostedLobbyState($"[NEXUS_TELEMETRY] status={status}");
                    SetQueueArcadeActive(false, $"[NEXUS_TELEMETRY] status={status}");
                    ClearQueueState($"[NEXUS_TELEMETRY] status={status}");
                    _matchStartUtc ??= TimeProvider.System.GetUtcNow().UtcDateTime;
                    string specState = string.IsNullOrEmpty(CleanAccountName)
                        ? "Watching Match"
                        : $"{CleanAccountName} (Watching Match)";
                    string specDetails = !string.IsNullOrEmpty(_currentGameMode)
                        ? $"Spectating {_currentGameMode}"
                        : "Spectating Match";
                    UpdateLocalPresence(specDetails, specState, "small", displayPlayer);
                    LogDebug($"[DiscordRPC] Direct Telemetry Transition to Spectating: {specDetails} | {specState}");
                    return;
                }

                if (status.Equals("Queue", StringComparison.OrdinalIgnoreCase) ||
                    status.Equals("Matchmaking", StringComparison.OrdinalIgnoreCase))
                {
                    if (_isQueueArcadeActive)
                    {
                        LogDebug($"[DiscordRPC] Preserving active Queue Arcade while syncing queued mode '{_queuedGameMode}'.");
                        return;
                    }
                    ClearHostedLobbyState($"[NEXUS_TELEMETRY] status={status}");
                    _currentMatchMode = null;
                    ClearHeroSelectionState();
                    _localPlayerSlot = null;
                    _matchStartUtc = null;
                    _queueStartUtc ??= TimeProvider.System.GetUtcNow().UtcDateTime;
                    if (string.IsNullOrWhiteSpace(_currentGameMode) && !string.IsNullOrWhiteSpace(_queuedGameMode))
                    {
                        _currentGameMode = _queuedGameMode;
                    }
                    else if (!string.IsNullOrWhiteSpace(_currentGameMode))
                    {
                        _queuedGameMode = _currentGameMode;
                    }
                    string queueState = string.IsNullOrEmpty(CleanAccountName)
                        ? "Finding Match..."
                        : $"{CleanAccountName} in Queue";
                    string queueDetails = !string.IsNullOrEmpty(_currentGameMode)
                        ? $"In Matchmaking ({_currentGameMode})"
                        : "In Matchmaking";
                    UpdateLocalPresence(queueDetails, queueState, "small", displayPlayer);
                    LogDebug($"[DiscordRPC] Direct Telemetry Transition to Queue: {queueDetails} | {queueState}");
                    return;
                }

                if (status.Equals("BanningPhase", StringComparison.OrdinalIgnoreCase) ||
                    status.Equals("Banning", StringComparison.OrdinalIgnoreCase))
                {
                    ClearHostedLobbyState($"[NEXUS_TELEMETRY] status={status}");
                    if (!_isQueueArcadeActive) ClearQueueState($"[NEXUS_TELEMETRY] status={status}");
                    ClearHeroSelectionState();
                    string banState = string.IsNullOrEmpty(CleanAccountName)
                        ? "Banning Phase"
                        : $"{CleanAccountName} (Banning Phase)";
                    string banDetails = !string.IsNullOrEmpty(_currentGameMode)
                        ? $"Banning Phase ({_currentGameMode})"
                        : "Banning Phase";
                    UpdateLocalPresence(banDetails, banState, "small", displayPlayer);
                    LogDebug($"[DiscordRPC] Direct Telemetry Transition to Banning Phase: {banState}");
                    return;
                }

                if (status.Equals("Loading", StringComparison.OrdinalIgnoreCase) ||
                    status.Equals("Connecting", StringComparison.OrdinalIgnoreCase))
                {
                    ClearHostedLobbyState($"[NEXUS_TELEMETRY] status={status}");
                    if (!_isQueueArcadeActive)
                    {
                        if (_queueStartUtc.HasValue)
                        {
                            _recentArcadeQueueStartUtc ??= _queueStartUtc;
                            _recentArcadeDisconnectUtc = TimeProvider.System.GetUtcNow().UtcDateTime;
                        }
                        _queueStartUtc = null;
                    }
                    string joinState = string.IsNullOrEmpty(CleanAccountName)
                        ? "Connecting to Server"
                        : $"{CleanAccountName} (Connecting)";
                    string joinDetails = !string.IsNullOrEmpty(_currentGameMode)
                        ? $"Joining Game ({_currentGameMode})..."
                        : "Joining Game...";
                    UpdateLocalPresence(joinDetails, joinState, "small", displayPlayer);
                    LogDebug($"[DiscordRPC] Direct Telemetry Transition to Joining Game: {joinState}");
                    return;
                }

                if (status.Equals("HeroSelect", StringComparison.OrdinalIgnoreCase) ||
                    status.Equals("PickingPhase", StringComparison.OrdinalIgnoreCase))
                {
                    ClearHostedLobbyState($"[NEXUS_TELEMETRY] status={status}");
                    if (!_isQueueArcadeActive) ClearQueueState($"[NEXUS_TELEMETRY] status={status}");
                    if (!string.IsNullOrEmpty(_selectedHeroDisplayName))
                    {
                        string pickState = string.IsNullOrEmpty(CleanAccountName)
                            ? $"Playing as {_selectedHeroDisplayName}"
                            : $"{CleanAccountName} as {_selectedHeroDisplayName}";
                        string heroTip = BuildHeroSmallTooltip(displayPlayer);
                        string pickDetails = !string.IsNullOrEmpty(_currentGameMode)
                            ? $"Picking Hero ({_currentGameMode})"
                            : "Picking Hero";
                        UpdateLocalPresence(pickDetails, pickState, _selectedHeroAssetKey ?? "small", heroTip);
                        LogDebug($"[DiscordRPC] Direct Telemetry Hero Selected: {pickState}");
                    }
                    else
                    {
                        string pickingState = string.IsNullOrEmpty(CleanAccountName)
                            ? "Hero Selection"
                            : $"{CleanAccountName} (Hero Selection)";
                        string pickDetails = !string.IsNullOrEmpty(_currentGameMode)
                            ? $"Picking Hero ({_currentGameMode})"
                            : "Picking Hero";
                        UpdateLocalPresence(pickDetails, pickingState, "small", displayPlayer);
                        LogDebug($"[DiscordRPC] Direct Telemetry Transition to Picking Hero: {pickingState}");
                    }

                    return;
                }

                if (status.Equals("InGame", StringComparison.OrdinalIgnoreCase) ||
                    status.Equals("Playing", StringComparison.OrdinalIgnoreCase))
                {
                    ClearHostedLobbyState($"[NEXUS_TELEMETRY] status={status}");
                    if (!_isQueueArcadeActive)
                    {
                        ClearQueueState($"[NEXUS_TELEMETRY] status={status}");
                        bool syncedFromGameClock = false;
                        if (dict.TryGetValue("matchsec", out string? matchSecStr) &&
                            double.TryParse(matchSecStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double matchSec) &&
                            matchSec >= 1.0)
                        {
                            bool isPreMatch = dict.TryGetValue("prematch", out string? preMatchStr) &&
                                              (preMatchStr.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                                               preMatchStr.Equals("true", StringComparison.OrdinalIgnoreCase));
                            if (!isPreMatch)
                            {
                                DateTime expectedStartUtc = TimeProvider.System.GetUtcNow().UtcDateTime - TimeSpan.FromSeconds(matchSec);
                                if (!_matchStartUtc.HasValue ||
                                    Math.Abs((_matchStartUtc.Value - expectedStartUtc).TotalSeconds) > 10.0)
                                {
                                    _matchStartUtc = expectedStartUtc;
                                    LogDebug($"[DiscordRPC] Synced match timer to real game clock (matchsec={matchSec:F0}s -> Start={_matchStartUtc:O}).");
                                }
                                syncedFromGameClock = true;
                            }
                        }

                        if (!syncedFromGameClock)
                        {
                            _matchStartUtc ??= TimeProvider.System.GetUtcNow().UtcDateTime;
                        }
                    }

                    bool hasDetailedStats = dict.ContainsKey("k") || dict.ContainsKey("d") || dict.ContainsKey("a") || dict.ContainsKey("level");
                    if (!hasDetailedStats)
                    {
                        string smallTip = !string.IsNullOrEmpty(_selectedHeroDisplayName)
                            ? BuildHeroSmallTooltip(displayPlayer)
                            : displayPlayer;
                        string playState = !string.IsNullOrEmpty(_selectedHeroDisplayName)
                            ? string.IsNullOrEmpty(CleanAccountName)
                                ? $"Playing as {_selectedHeroDisplayName}"
                                : $"{CleanAccountName} as {_selectedHeroDisplayName}"
                            : string.IsNullOrEmpty(CleanAccountName)
                                ? "In Match"
                                : $"{CleanAccountName} (In Match)";
                        string playDetails = !string.IsNullOrEmpty(_currentGameMode)
                            ? $"Playing {_currentGameMode}"
                            : "Playing in Match";
                        string heroKey = _selectedHeroAssetKey ?? "small";
                        UpdateLocalPresence(playDetails, playState, heroKey, smallTip);
                        LogDebug($"[DiscordRPC] Direct Telemetry Transition to In-Game: {playState}");
                        return;
                    }
                }
            }

            string levelStr = dict.TryGetValue("level", out string? lvl) && !string.IsNullOrWhiteSpace(lvl)
                ? $"Lvl {lvl}"
                : string.Empty;
            string kdaStr = dict.TryGetValue("k", out string? k) && dict.TryGetValue("d", out string? d) &&
                            dict.TryGetValue("a", out string? a)
                ? $"{k}/{d}/{a}"
                : string.Empty;

            if (!string.IsNullOrEmpty(levelStr))
                _lastTelemetryLevel = levelStr;
            if (!string.IsNullOrEmpty(kdaStr))
                _lastTelemetryKda = kdaStr;

            string stateMsg = !string.IsNullOrEmpty(_selectedHeroDisplayName)
                ? string.IsNullOrEmpty(CleanAccountName)
                    ? $"Playing as {_selectedHeroDisplayName}"
                    : $"{CleanAccountName} as {_selectedHeroDisplayName}"
                : string.IsNullOrEmpty(CleanAccountName)
                    ? "In Match"
                    : $"{CleanAccountName} (In Match)";

            if (!string.IsNullOrEmpty(levelStr)) stateMsg += $" ({levelStr})";

            string detailsMsg = !string.IsNullOrEmpty(_currentGameMode)
                ? $"Playing {_currentGameMode}"
                : "Playing in Match";
            if (!string.IsNullOrEmpty(kdaStr)) detailsMsg += $" • {kdaStr}";

            string finalSmallTip = !string.IsNullOrEmpty(_selectedHeroDisplayName)
                ? BuildHeroSmallTooltip(displayPlayer)
                : $"{cleanDisplayPlayer} ({displayPlayer})";

            UpdateLocalPresence(detailsMsg, stateMsg, _selectedHeroAssetKey ?? "small", finalSmallTip);
            LogDebug($"[DiscordRPC] Live Telemetry Processed: {detailsMsg} | {stateMsg}");
        }
        catch (Exception ex)
        {
            LogDebug($"[DiscordRPC] Failed to parse Nexus Telemetry: {ex.Message}");
        }
    }
}

public class PresenceDto
{
    public long AccountId { get; set; }
    public string State { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public string LargeImageKey { get; set; } = string.Empty;
    public string LargeImageText { get; set; } = string.Empty;
    public string SmallImageKey { get; set; } = string.Empty;
    public string SmallImageText { get; set; } = string.Empty;
    public string PartyId { get; set; } = string.Empty;
    public int? PartySize { get; set; }
    public int? PartyMax { get; set; }
    public long? StartTimestamp { get; set; }
}

[JsonSerializable(typeof(PresenceDto))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
public partial class PresenceJsonContext : JsonSerializerContext
{
}

public class CustomDiscordLogger(Action<string> logAction) : ILogger
{
    private readonly Action<string> _logAction = logAction;
    public LogLevel Level { get; set; } = LogLevel.Trace;

    public void Trace(string message, params object[] args)
    {
        if (Level <= LogLevel.Trace)
            _logAction($"[DiscordRPC Internal TRACE] {(args.Length > 0 ? string.Format(message, args) : message)}");
    }

    public void Info(string message, params object[] args)
    {
        if (Level <= LogLevel.Info)
            _logAction($"[DiscordRPC Internal INFO] {(args.Length > 0 ? string.Format(message, args) : message)}");
    }

    public void Warning(string message, params object[] args)
    {
        if (Level <= LogLevel.Warning)
            _logAction($"[DiscordRPC Internal WARN] {(args.Length > 0 ? string.Format(message, args) : message)}");
    }

    public void Error(string message, params object[] args)
    {
        if (Level <= LogLevel.Error)
            _logAction($"[DiscordRPC Internal ERROR] {(args.Length > 0 ? string.Format(message, args) : message)}");
    }
}
