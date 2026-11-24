// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: DiagnosticRedactor.cs
// System: WILLOWMAKER - Diagnostics & Privacy Firewall
// Subsystem: Native-AOT Privacy & Credential Scrubber for Exported Reports
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WILLOWMAKER.Core.Services.Diagnostics;

public sealed record RedactionAuditCounts(
    int SecretsRedacted,
    int UserIdentifiersRedacted,
    int PeerIpsRedacted)
{
    public int TotalRedactions => SecretsRedacted + UserIdentifiersRedacted + PeerIpsRedacted;
}

/// <summary>
/// Per-bundle ephemeral privacy context holding a single-use CSPRNG salt (discarded after zip export),
/// canonical directory root mappings, and discovered local session identifiers/secrets.
/// Ensures consistent [PLAYER_n], [UUID_n], and third_party_module_n.dll tokens within one report
/// with zero cross-bundle linkability.
/// </summary>
public sealed class PrivacyScrubContext
{
    private readonly byte[] _ephemeralSalt = new byte[16];
    private readonly Dictionary<string, string> _playerTokensByHmac = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _uuidTokensByHmac = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _thirdPartyModuleTokensByHmac = new(StringComparer.Ordinal);
    private readonly List<(string PathPrefix, string Token)> _canonicalRoots = new();
    private readonly HashSet<string> _knownPlayerNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _knownSecrets = new(StringComparer.Ordinal);
    private readonly HashSet<string> _knownUserLiterals = new(StringComparer.OrdinalIgnoreCase);

    public PrivacyScrubContext(
        string? clientRoot = null,
        string? outputDir = null,
        string? faultingModuleName = null,
        bool strictZeroPiiBundleMode = true)
    {
        RandomNumberGenerator.Fill(_ephemeralSalt);
        StrictZeroPiiBundleMode = strictZeroPiiBundleMode;
        FaultingModuleName = faultingModuleName;

        AddCanonicalRoot(clientRoot, "[GAME_DIR]");
        AddCanonicalRoot(AppContext.BaseDirectory, "[LAUNCHER_DIR]");
        AddCanonicalRoot(outputDir, "[DIAG_OUT_DIR]");
        AddCanonicalRoot(SafeGetFolderPath(Environment.SpecialFolder.LocalApplicationData), "[LOCAL_APPDATA]");
        AddCanonicalRoot(SafeGetFolderPath(Environment.SpecialFolder.ApplicationData), "[APPDATA]");
        AddCanonicalRoot(Path.GetTempPath(), "[TEMP_DIR]");

        string userProfile = SafeGetFolderPath(Environment.SpecialFolder.UserProfile);
        AddCanonicalRoot(userProfile, "[USER_DIR]");
        AddCanonicalRoot(Environment.GetEnvironmentVariable("USERPROFILE"), "[USER_DIR]");
        AddCanonicalRoot(Environment.GetEnvironmentVariable("HOME"), "[USER_DIR]");

        // Sort longest-prefix-first so [GAME_DIR] wins before [TEMP_DIR] or [USER_DIR]
        _canonicalRoots.Sort((a, b) => b.PathPrefix.Length.CompareTo(a.PathPrefix.Length));

        AddKnownUserLiteral(Environment.UserName);
        AddKnownUserLiteral(ExtractLeafDirectoryName(userProfile));
        AddKnownUserLiteral(ExtractLeafDirectoryName(Environment.GetEnvironmentVariable("USERPROFILE")));
        AddKnownUserLiteral(ExtractLeafDirectoryName(Environment.GetEnvironmentVariable("HOME")));
        HarvestUserPathLeafFromPath(clientRoot);
    }

    public bool StrictZeroPiiBundleMode { get; }
    public string? FaultingModuleName { get; set; }
    public IReadOnlyList<(string PathPrefix, string Token)> CanonicalRoots => _canonicalRoots;
    public IReadOnlyCollection<string> KnownPlayerNames => _knownPlayerNames;
    public IReadOnlyCollection<string> KnownSecrets => _knownSecrets;
    public IReadOnlyCollection<string> KnownUserLiterals => _knownUserLiterals;

    public void AddKnownPlayerName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        string trimmed = name.Trim().Trim('"', '\'');
        if (trimmed.Length < 3 || IsRedactionPlaceholderToken(trimmed))
        {
            return;
        }

        if (!IsGenericDirectoryWord(trimmed) && !IsCommonLogKeyword(trimmed))
        {
            _knownPlayerNames.Add(trimmed);
        }

        // If handle carries a leading [CLAN] tag (e.g. "[CLAN]ExamplePlayer"), also register the bare handle
        // when >= 4 chars and non-generic so untagged mentions elsewhere in the bundle are scrubbed too.
        if (trimmed.StartsWith('[') &&
            trimmed.IndexOf(']') is int closeBracket &&
            closeBracket > 1 &&
            closeBracket + 1 < trimmed.Length)
        {
            string bareHandle = trimmed[(closeBracket + 1)..].Trim();
            if (bareHandle.Length >= 4 && !IsGenericDirectoryWord(bareHandle) && !IsCommonLogKeyword(bareHandle))
            {
                _knownPlayerNames.Add(bareHandle);
            }
        }
    }

    public void AddKnownSecret(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) return;
        string trimmed = secret.Trim();
        if (trimmed.Length >= 4 && !trimmed.StartsWith("[REDACTED", StringComparison.OrdinalIgnoreCase))
        {
            _knownSecrets.Add(trimmed);
        }
    }

    public void AddKnownUserLiteral(string? literal)
    {
        if (string.IsNullOrWhiteSpace(literal)) return;
        string trimmed = literal.Trim();
        if (trimmed.Length >= 3 && !IsGenericDirectoryWord(trimmed))
        {
            _knownUserLiterals.Add(trimmed);
        }
    }

    public string MapPlayerName(string rawName)
    {
        string normalized = rawName.Trim().Trim('"', '\'');
        if (string.IsNullOrWhiteSpace(normalized) || IsRedactionPlaceholderToken(normalized))
        {
            return normalized;
        }

        AddKnownPlayerName(normalized);

        string key = ComputeSaltedKey("player", normalized.ToLowerInvariant());
        if (!_playerTokensByHmac.TryGetValue(key, out string? badge))
        {
            badge = $"[PLAYER_{_playerTokensByHmac.Count + 1}]";
            _playerTokensByHmac[key] = badge;
        }
        return badge;
    }

    public string MapUuid(string rawUuid)
    {
        string normalized = rawUuid.Trim().ToLowerInvariant();
        string key = ComputeSaltedKey("uuid", normalized);
        if (!_uuidTokensByHmac.TryGetValue(key, out string? token))
        {
            token = $"[UUID_{_uuidTokensByHmac.Count + 1}]";
            _uuidTokensByHmac[key] = token;
        }
        return token;
    }

    public string MapThirdPartyModuleName(string baseName)
    {
        string normalized = baseName.Trim().ToLowerInvariant();
        string key = ComputeSaltedKey("mod3p", normalized);
        if (!_thirdPartyModuleTokensByHmac.TryGetValue(key, out string? token))
        {
            token = $"third_party_module_{_thirdPartyModuleTokensByHmac.Count + 1}.dll";
            _thirdPartyModuleTokensByHmac[key] = token;
        }
        return token;
    }

    public List<string> BuildBinaryScrubVocabulary()
    {
        HashSet<string> all = new(StringComparer.Ordinal);
        foreach (string u in _knownUserLiterals) all.Add(u);
        if (!string.IsNullOrWhiteSpace(Environment.MachineName) && Environment.MachineName.Length >= 3)
        {
            all.Add(Environment.MachineName);
        }
        foreach (string p in _knownPlayerNames) all.Add(p);
        foreach (string s in _knownSecrets) all.Add(s);
        return all.ToList();
    }

    private string ComputeSaltedKey(string domain, string normalizedInput)
    {
        byte[] bytes = Encoding.UTF8.GetBytes($"{domain}:{normalizedInput}");
        byte[] mac = HMACSHA256.HashData(_ephemeralSalt, bytes);
        return Convert.ToHexString(mac, 0, 12);
    }

    private void AddCanonicalRoot(string? rawPath, string token)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return;
        try
        {
            string full = Path.GetFullPath(rawPath).TrimEnd('\\', '/');
            if (full.Length < 4) return; // Never replace bare drive roots like "C:"

            string winForm = full.Replace('/', '\\');
            string posixForm = full.Replace('\\', '/');
            string jsonEscapedWinForm = winForm.Replace("\\", "\\\\");

            if (!_canonicalRoots.Any(r => string.Equals(r.PathPrefix, winForm, StringComparison.OrdinalIgnoreCase)))
            {
                _canonicalRoots.Add((winForm, token));
            }
            if (!string.Equals(winForm, jsonEscapedWinForm, StringComparison.Ordinal) &&
                !_canonicalRoots.Any(r => string.Equals(r.PathPrefix, jsonEscapedWinForm, StringComparison.OrdinalIgnoreCase)))
            {
                _canonicalRoots.Add((jsonEscapedWinForm, token));
            }
            if (!string.Equals(winForm, posixForm, StringComparison.Ordinal) &&
                !_canonicalRoots.Any(r => string.Equals(r.PathPrefix, posixForm, StringComparison.OrdinalIgnoreCase)))
            {
                _canonicalRoots.Add((posixForm, token));
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DIAGNOSTICS] Canonical root normalization notice: {ex.Message}");
        }
    }

    private void HarvestUserPathLeafFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        string[] parts = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            if ((string.Equals(parts[i], "Users", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(parts[i], "home", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < parts.Length)
            {
                AddKnownUserLiteral(parts[i + 1]);
            }
            else if (parts[i].EndsWith("_Profile", StringComparison.OrdinalIgnoreCase))
            {
                AddKnownUserLiteral(parts[i]);
            }
        }
    }

    private static string SafeGetFolderPath(Environment.SpecialFolder folder)
    {
        try { return Environment.GetFolderPath(folder); }
        catch { return string.Empty; }
    }

    private static string? ExtractLeafDirectoryName(string? dirPath)
    {
        if (string.IsNullOrWhiteSpace(dirPath)) return null;
        string trimmed = dirPath.TrimEnd('\\', '/');
        int idx = Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/'));
        return idx >= 0 && idx + 1 < trimmed.Length ? trimmed[(idx + 1)..] : null;
    }

    private static bool IsRedactionPlaceholderToken(string value) =>
        (value.StartsWith('[') && value.EndsWith(']')) ||
        value.StartsWith("[PLAYER_", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("[REDACTED", StringComparison.OrdinalIgnoreCase);

    private static bool IsCommonLogKeyword(string word) =>
        word.Equals("info", StringComparison.OrdinalIgnoreCase) ||
        word.Equals("warn", StringComparison.OrdinalIgnoreCase) ||
        word.Equals("error", StringComparison.OrdinalIgnoreCase) ||
        word.Equals("debug", StringComparison.OrdinalIgnoreCase) ||
        word.Equals("trace", StringComparison.OrdinalIgnoreCase) ||
        word.Equals("main", StringComparison.OrdinalIgnoreCase) ||
        word.Equals("game", StringComparison.OrdinalIgnoreCase) ||
        word.Equals("sync", StringComparison.OrdinalIgnoreCase) ||
        word.Equals("boot", StringComparison.OrdinalIgnoreCase) ||
        word.Equals("host", StringComparison.OrdinalIgnoreCase) ||
        word.Equals("init", StringComparison.OrdinalIgnoreCase) ||
        word.Equals("none", StringComparison.OrdinalIgnoreCase) ||
        word.Equals("null", StringComparison.OrdinalIgnoreCase) ||
        word.Equals("true", StringComparison.OrdinalIgnoreCase) ||
        word.Equals("false", StringComparison.OrdinalIgnoreCase) ||
        word.Equals("normal", StringComparison.OrdinalIgnoreCase) ||
        word.Equals("lobby", StringComparison.OrdinalIgnoreCase) ||
        word.Equals("match", StringComparison.OrdinalIgnoreCase) ||
        word.Equals("queue", StringComparison.OrdinalIgnoreCase);

    private static bool IsGenericDirectoryWord(string word) =>
        word.Equals("Users", StringComparison.OrdinalIgnoreCase) ||
        word.Equals("home", StringComparison.OrdinalIgnoreCase) ||
        word.Equals("root", StringComparison.OrdinalIgnoreCase) ||
        word.Equals("Public", StringComparison.OrdinalIgnoreCase) ||
        word.Equals("Default", StringComparison.OrdinalIgnoreCase) ||
        word.Equals("Temp", StringComparison.OrdinalIgnoreCase) ||
        word.Equals("tmp", StringComparison.OrdinalIgnoreCase) ||
        word.Equals("client", StringComparison.OrdinalIgnoreCase) ||
        word.Equals("games", StringComparison.OrdinalIgnoreCase) ||
        word.Equals("runner", StringComparison.OrdinalIgnoreCase) ||
        word.Equals("Kongor", StringComparison.OrdinalIgnoreCase) ||
        word.Equals("WILLOWMAKER", StringComparison.OrdinalIgnoreCase) ||
        word.Equals("Newerth", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// High-performance, zero-reflection, Native AOT-safe privacy scrubber.
/// Sanitizes credentials, passwords, session tokens, connection-string URIs, user &amp; custom-drive paths,
/// multi-player nicknames, UUIDs, hardware IDs, audio endpoints, and peer IPv4/IPv6 addresses.
/// </summary>
public static partial class DiagnosticRedactor
{
    [GeneratedRegex(@"(?i)(SetSave|Set)\s+[""']?([a-zA-Z0-9_]*(?:password|cookie|token|secret)[a-zA-Z0-9_]*)[""']?\s+[""']?([^""'\r\n]+)[""']?", RegexOptions.Compiled)]
    private static partial Regex K2SecretCvarRegex();

    [GeneratedRegex(@"(?i)(SetSave|Set)\s+[""']?(login_name|p2p_nickname|host_cloudLastModifiedUser|man_masterLogin)[""']?\s+[""']([^""'\r\n]+)[""']", RegexOptions.Compiled)]
    private static partial Regex K2LoginNameCvarRegex();

    [GeneratedRegex(@"(?i)(SetSave|Set)\s+[""']?(sound_output|sound_record_driver)[""']?\s+[""']([^""'\r\n]+)[""']", RegexOptions.Compiled)]
    private static partial Regex K2AudioDriverCvarRegex();

    [GeneratedRegex(@"\b([a-zA-Z][a-zA-Z0-9+\-.]*://)([^/\s:@""']+:[^/\s@""']+)@", RegexOptions.Compiled)]
    private static partial Regex UriUserInfoCredentialsRegex();

    [GeneratedRegex(@"(?i)Bearer\s+[A-Za-z0-9\-_\.\~+/]+=*", RegexOptions.Compiled)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(@"(?i)([""'])([a-zA-Z0-9_\-]*(?:password|token|session|auth|cookie|pass|secret|apikey|api_key|jwt)[a-zA-Z0-9_\-]*)\1\s*:\s*([""'])((?:\\.|(?!\3)[^\r\n\\])+)\3", RegexOptions.Compiled)]
    private static partial Regex JsonQuotedSecretPropertyRegex();

    [GeneratedRegex(@"(?i)([""'])(LastPlayerNickname|PlayerNickname|login_name)[""']\s*:\s*([""'])((?:\\.|(?!\3)[^\r\n\\])+)\3", RegexOptions.Compiled)]
    private static partial Regex JsonPlayerNicknamePropertyRegex();

    [GeneratedRegex(@"(?i)\b([a-zA-Z0-9_\-]*(?:password|token|session|auth|cookie|pass|secret)[a-zA-Z0-9_\-]*)\s*(?:=\s*[""']?([A-Za-z0-9\-_\.\~+/]{6,})[""']?|:\s*[""']?([A-Za-z0-9\-_\.\~+/]{6,})(?![\w\-]*=)[""']?)", RegexOptions.Compiled)]
    private static partial Regex KeyValueSecretRegex();

    [GeneratedRegex(@"(?i)([a-zA-Z]:[\\/]+(?:Users|Documents and Settings)[\\/]+[^\\/\r\n""'\s]+)", RegexOptions.Compiled)]
    private static partial Regex WindowsUserPathRegex();

    [GeneratedRegex(@"(?i)((?<![A-Za-z0-9_:])\\+(?:Users|Documents and Settings)\\+[^\\/\r\n""'\s]+)", RegexOptions.Compiled)]
    private static partial Regex DrivelessWindowsUserPathRegex();

    [GeneratedRegex(@"(?i)(\\Device\\HarddiskVolume\d+\\+Users\\+[^\\/\r\n""'\s]+)", RegexOptions.Compiled)]
    private static partial Regex NtDeviceUserPathRegex();

    [GeneratedRegex(@"(?i)((?<![A-Za-z0-9_:])\/(?:Users|home)[\\/][^\\/\r\n""'\s]+)", RegexOptions.Compiled)]
    private static partial Regex PosixUserPathRegex();

    [GeneratedRegex(@"(\[(?:USER_DIR|TEMP_DIR|LOCAL_APPDATA|APPDATA)\])(?:[\\/]+[^\\/\r\n""':*?<>|]+)+[\\/]+([^\\/\r\n""'\s:*?<>|]+)", RegexOptions.Compiled)]
    private static partial Regex TokenizedSubpathCollapseRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])([A-Za-z]:[\\/]+[^\r\n""'<>|*?;]+?\.[A-Za-z0-9_]{1,8})(?=$|[\s""',;)\]])", RegexOptions.Compiled)]
    private static partial Regex ArbitraryWindowsFileWithExtRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])([A-Za-z]:[\\/]+[^\r\n""'\s<>|*?]+)", RegexOptions.Compiled)]
    private static partial Regex ArbitraryWindowsBarePathRegex();

    [GeneratedRegex(@"\[(?:PLAYER_\d+|UUID_\d+|REDACTED_[A-Z0-9_]+|USER_DIR|GAME_DIR|LAUNCHER_DIR|DIAG_OUT_DIR|LOCAL_APPDATA|APPDATA|TEMP_DIR|SYSTEM_DIR|EXTERNAL_DIR|AUDIO_DEVICE|CUSTOM_MOD(?:_\d+)?|LOCAL_CRASH_DIR|LINE_REDACTED_BY_ZERO_PII_CANARY:[^\r\n\]]+)\]", RegexOptions.Compiled)]
    private static partial Regex CanonicalRedactionTokenStripRegex();

    // Requires >= 5 printable path chars after X:\ so random x64 binary pointers/floats never false-positive (~1 in 1.2e9 bytes)
    [GeneratedRegex(@"[A-Za-z]:[\\/][A-Za-z0-9_.\-\\/ ]{5,}", RegexOptions.Compiled)]
    private static partial Regex BinaryMinidumpDrivePathCanaryRegex();

    [GeneratedRegex(@"(?i)[""']?\b(crashReporterKey|bootSessionUUID|incident_id|slice_uuid|anonId|udid)\b[""']?\s*[:=]\s*[""']?([A-Fa-f0-9\-]{8,})[""']?", RegexOptions.Compiled)]
    private static partial Regex MacIpsIdentifierRegex();

    [GeneratedRegex(@"(?i)\b(deviceUUID|driverUUID|pipelineCacheUUID|deviceLUID|MonitorSerial|EDID)\b(\s*[:=]\s*)([0-9A-Fa-f\-:]{8,})", RegexOptions.Compiled)]
    private static partial Regex HardwareFingerprintIdRegex();

    // Matches labeled hardware MAC addresses (e.g. "MAC: c8-7f-54-ec-16-eb", "MAC=C8:7F:54:EC:16:EB") without matching 8-4-4-4-12 UUIDs.
    [GeneratedRegex(@"(?i)\bMAC(?:\s+address)?\s*[:=]\s*([""']?)(?:[0-9a-f]{2}(?:-[0-9a-f]{2}){5}|[0-9a-f]{2}(?::[0-9a-f]{2}){5})(?![0-9a-f:-])\1", RegexOptions.Compiled)]
    private static partial Regex HardwareMacLabeledRegex();

    // Matches lines mentioning MAC so standalone 6-octet MAC literals on MAC lines are scrubbed while avoiding UUID/unrelated hex false positives.
    // Uses (?=\r?$) so Windows CRLF (\r\n) log lines match cleanly (.NET multiline '$' only matches before '\n', not '\r').
    [GeneratedRegex(@"(?im)^[^\r\n]*\bMAC\b[^\r\n]*(?=\r?$)", RegexOptions.Compiled)]
    private static partial Regex HardwareMacContextLineRegex();

    // Matches exact 6-octet colon- or hyphen-delimited hardware MAC literals (never 8-4-4-4-12 UUIDs or longer hex sequences).
    [GeneratedRegex(@"(?<![0-9A-Fa-f:-])(?:[0-9A-Fa-f]{2}(?:-[0-9A-Fa-f]{2}){5}|[0-9A-Fa-f]{2}(?::[0-9A-Fa-f]{2}){5})(?![0-9A-Fa-f:-])", RegexOptions.Compiled)]
    private static partial Regex StandaloneHardwareMacRegex();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}\b", RegexOptions.Compiled)]
    private static partial Regex GenericUuidRegex();

    [GeneratedRegex(@"(?i)\b(player\s*=\s*)([^;\r\n""'\s,]+)", RegexOptions.Compiled)]
    private static partial Regex TelemetryPlayerKeyValueRegex();

    [GeneratedRegex(@"(?i)\b(accountId\s*:\s*)\d+", RegexOptions.Compiled)]
    private static partial Regex LuaAccountIdRegex();

    [GeneratedRegex(@"(?i)\b(nickname\s*:\s*)([^\r\n,;|""']+)", RegexOptions.Compiled)]
    private static partial Regex LuaNicknameRegex();

    [GeneratedRegex(@"(\^[a-zA-Z])(\d+)\|([^|\r\n]+)\|(\d+)", RegexOptions.Compiled)]
    private static partial Regex K2ColorPipeAccountTupleRegex();

    [GeneratedRegex(@"(?i)(Offline/P2P nickname\s*['""])([^'""\r\n]+)(['""])", RegexOptions.Compiled)]
    private static partial Regex NativeGuardOfflineNickRegex();

    [GeneratedRegex(@"(?i)(\bprev\s*=\s*['""])([^'""\r\n]+)(['""])", RegexOptions.Compiled)]
    private static partial Regex NativeGuardPrevNickRegex();

    [GeneratedRegex(@"(?i)\b(Identified active player:\s*)([^\r\n]+)", RegexOptions.Compiled)]
    private static partial Regex LauncherIdentifiedActivePlayerRegex();

    [GeneratedRegex(@"(?i)\b(Successfully authenticated as\s*['""])([^'""\r\n]+)(['""]\s*\(Account:\s*['""])([^'""\r\n]+)(['""]\))", RegexOptions.Compiled)]
    private static partial Regex DiscordOAuthAuthenticatedRegex();

    [GeneratedRegex(@"(?i)(\[Discord\s*RPC\]\s*(?:Preloaded Account Name|Discovered initial login name from config:|Login Name detected from config:|Nickname detected:|Telemetry Hero Resolved for Local Player|Ignored foreign PresenceDto for|Ignored foreign \[NEXUS_TELEMETRY\] packet for player)\s*['""])([^'""\r\n]+)(['""])", RegexOptions.Compiled)]
    private static partial Regex DiscordRpcQuotedIdentityRegex();

    [GeneratedRegex(@"(?i)(\(local:\s*['""])([^'""\r\n]+)(['""]\))", RegexOptions.Compiled)]
    private static partial Regex DiscordRpcLocalPlayerSuffixRegex();

    [GeneratedRegex(@"(?i)(\bAccount(?:\s+ID)?\s+)(\d+)(,\s*Name\s*['""])([^'""\r\n]+)(['""])", RegexOptions.Compiled)]
    private static partial Regex DiscordRpcAccountAndNameRegex();

    [GeneratedRegex(@"(?i)(\[Discord\s*RPC\]\s+(?:Direct Telemetry (?:Transition to [^:\r\n]+|Hero Selected):\s+)?)(?!Playing\s+as\b)([^\r\n|]+?)(\s+(?:\((?:In Lobby|In Match|Banning Phase|Connecting|Hero Selection)\)|in Queue\b|as\s+[A-Za-z0-9_ '\-]+(?:\s*\(Lvl\s*\d+\))?))(?=\s*(?:\||[\r\n]|$))", RegexOptions.Compiled)]
    private static partial Regex DiscordRpcPresenceDetailsPlayerRegex();

    [GeneratedRegex(@"(?i)\b(Login\s+success:\s*#)(\d+)\s+([^\s\[\r\n]+)(\s*\[([0-9a-fA-F:]+)\])?", RegexOptions.Compiled)]
    private static partial Regex K2LoginSuccessRegex();

    [GeneratedRegex(@"(?i)(Add\s+subaccount\s+)([^\s\r\n]+)(\s+index\d+)", RegexOptions.Compiled)]
    private static partial Regex K2AddSubaccountRegex();

    [GeneratedRegex(@"(?i)(\bself\s+row\s*\()([^:\r\n()]+)(:\s*rank=)", RegexOptions.Compiled)]
    private static partial Regex K2LadderSelfRowRegex();

    [GeneratedRegex(@"(?i)(/ui/fe2/avatars/)([^/\s\r\n""'()]+?)(\.(?:png|tga|jpg|dds)\b)", RegexOptions.Compiled)]
    private static partial Regex K2Fe2AvatarPathRegex();

    [GeneratedRegex(@"(?i)\b(Using driver\s+\d+\s*:\s*)""[^""\r\n]+""", RegexOptions.Compiled)]
    private static partial Regex FmodUsingDriverLineRegex();

    [GeneratedRegex(@"(?i)\b(Speakers|Microphone|Headphones|Headset|Earbuds|Digital\s+Audio|Line\s+In|Line\s+Out)\s*\([^)\r\n]{1,96}\)", RegexOptions.Compiled)]
    private static partial Regex AudioEndpointLabelRegex();

    [GeneratedRegex(@"\b(\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:\.\d+)?)\s*([+-]\d{2}:?\d{2})\b", RegexOptions.Compiled)]
    private static partial Regex IsoLocalOffsetTimestampRegex();

    [GeneratedRegex(@"\b(?!(?:127\.0\.0\.1|0\.0\.0\.0|255\.255\.255\.255)\b)(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\b", RegexOptions.Compiled)]
    private static partial Regex GenericIpv4Regex();

    // Non-identifier boundaries (?<![0-9A-Za-z_:]) and (?![0-9A-Za-z_:]) prevent C++ Class::Method symbols
    // (such as IWidget::ExecuteLuaCallback, CTexture::Load, CVid::StartDriver) from matching as compressed IPv6.
    [GeneratedRegex(@"(?<![0-9A-Za-z_:])(?:(?:[0-9A-Fa-f]{1,4}:){7}[0-9A-Fa-f]{1,4}|(?:[0-9A-Fa-f]{1,4}:){1,7}:|(?:[0-9A-Fa-f]{1,4}:){1,6}:[0-9A-Fa-f]{1,4}|(?:[0-9A-Fa-f]{1,4}:){1,5}(?::[0-9A-Fa-f]{1,4}){1,2}|(?:[0-9A-Fa-f]{1,4}:){1,4}(?::[0-9A-Fa-f]{1,4}){1,3}|(?:[0-9A-Fa-f]{1,4}:){1,3}(?::[0-9A-Fa-f]{1,4}){1,4}|(?:[0-9A-Fa-f]{1,4}:){1,2}(?::[0-9A-Fa-f]{1,4}){1,5}|[0-9A-Fa-f]{1,4}:(?::[0-9A-Fa-f]{1,4}){1,6}|:(?::[0-9A-Fa-f]{1,4}){1,7})(?![0-9A-Za-z_:])", RegexOptions.Compiled)]
    private static partial Regex CandidateIpv6Regex();

    /// <summary>
    /// Scrubs a text block and returns the sanitized string along with the counts of redacted items.
    /// </summary>
    public static (string ScrubbedText, RedactionAuditCounts Counts) ScrubText(
        string rawText,
        string? allowedMasterServerHost = null,
        string? allowedCdnHost = null,
        string? userNameOverride = null,
        string? machineNameOverride = null,
        string? playerNickname = null,
        PrivacyScrubContext? context = null)
    {
        if (string.IsNullOrEmpty(rawText))
        {
            return (string.Empty, new RedactionAuditCounts(0, 0, 0));
        }

        int secrets = 0;
        int userIds = 0;
        int peerIps = 0;

        string current = rawText;

        string FormatPlayer(string raw) => context != null ? context.MapPlayerName(raw) : "[REDACTED_PLAYER]";
        string FormatUuid(string raw) => context != null ? context.MapUuid(raw) : "[REDACTED_UUID]";

        // 0. Canonical Runtime Root Tokenization (Bundle Mode: longest-first so [GAME_DIR] wins before [USER_DIR])
        if (context != null)
        {
            foreach ((string prefix, string token) in context.CanonicalRoots)
            {
                if (current.Contains(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    current = ReplaceOrdinalIgnoreCase(current, prefix, token, ref userIds);
                }
            }
        }

        // 1. K2 Secret CVars, Login/P2P Name CVars, & Audio Driver CVars
        current = K2SecretCvarRegex().Replace(current, m =>
        {
            string val = m.Groups[3].Value.Trim();
            if (IsPurelyNumericOrBooleanLiteral(val) || val.StartsWith("[REDACTED", StringComparison.Ordinal))
            {
                return m.Value;
            }

            secrets++;
            return $"{m.Groups[1].Value} \"{m.Groups[2].Value}\" \"[REDACTED_SECRET]\"";
        });

        current = K2LoginNameCvarRegex().Replace(current, m =>
        {
            string val = m.Groups[3].Value;
            if (string.IsNullOrWhiteSpace(val) || val.StartsWith("[REDACTED", StringComparison.Ordinal) || val.StartsWith("[PLAYER_", StringComparison.Ordinal))
            {
                return m.Value;
            }
            userIds++;
            return $"{m.Groups[1].Value} \"{m.Groups[2].Value}\" \"{FormatPlayer(val)}\"";
        });

        current = K2AudioDriverCvarRegex().Replace(current, m =>
        {
            string val = m.Groups[3].Value;
            if (string.IsNullOrWhiteSpace(val) || val.StartsWith("[AUDIO_DEVICE]", StringComparison.Ordinal))
            {
                return m.Value;
            }
            userIds++;
            return $"{m.Groups[1].Value} \"{m.Groups[2].Value}\" \"[AUDIO_DEVICE]\"";
        });

        // 2. Connection-string URI credentials (scheme://user:password@host)
        current = UriUserInfoCredentialsRegex().Replace(current, m =>
        {
            secrets++;
            return $"{m.Groups[1].Value}[REDACTED_CREDENTIALS]@";
        });

        // 3. Bearer Tokens
        current = BearerTokenRegex().Replace(current, _ =>
        {
            secrets++;
            return "Bearer [REDACTED_TOKEN]";
        });

        // 4. macOS .ips hardware/device UUIDs, Vulkan/DXVK Hardware UUIDs, & Network Hardware MAC Addresses
        current = MacIpsIdentifierRegex().Replace(current, m =>
        {
            userIds++;
            return $"{m.Groups[1].Value}: \"[REDACTED_UUID]\"";
        });

        current = HardwareFingerprintIdRegex().Replace(current, m =>
        {
            userIds++;
            return $"{m.Groups[1].Value}{m.Groups[2].Value}[REDACTED_HW_ID]";
        });

        if (current.Contains("mac", StringComparison.OrdinalIgnoreCase))
        {
            current = HardwareMacLabeledRegex().Replace(current, _ =>
            {
                userIds++;
                return "MAC: [REDACTED_MAC]";
            });

            current = HardwareMacContextLineRegex().Replace(current, lineMatch =>
                StandaloneHardwareMacRegex().Replace(lineMatch.Value, _ =>
                {
                    userIds++;
                    return "[REDACTED_MAC]";
                }));
        }

        // 5. JSON / Quoted Key-Value Secrets (handles spaces & special symbols in passwords)
        current = JsonQuotedSecretPropertyRegex().Replace(current, m =>
        {
            string key = m.Groups[2].Value;
            string val = m.Groups[4].Value;
            if (val.StartsWith("[REDACTED", StringComparison.Ordinal) ||
                IsPurelyNumericOrBooleanLiteral(val) ||
                string.Equals(val, "null", StringComparison.OrdinalIgnoreCase))
            {
                return m.Value;
            }

            secrets++;
            return $"\"{key}\": \"[REDACTED_SECRET]\"";
        });

        // 6. JSON Player Nickname Properties ("LastPlayerNickname": "...")
        current = JsonPlayerNicknamePropertyRegex().Replace(current, m =>
        {
            string key = m.Groups[2].Value;
            string val = m.Groups[4].Value;
            if (string.IsNullOrWhiteSpace(val) || val.StartsWith("[REDACTED", StringComparison.Ordinal) || val.StartsWith("[PLAYER_", StringComparison.Ordinal))
            {
                return m.Value;
            }

            userIds++;
            return $"\"{key}\": \"{FormatPlayer(val)}\"";
        });

        // 7. Generic Key=Value Auth Tokens (excluding .NET PublicKeyToken and boolean/numeric timeouts)
        current = KeyValueSecretRegex().Replace(current, m =>
        {
            string key = m.Groups[1].Value;
            string val = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            if (val.StartsWith("REDACTED", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "PublicKeyToken", StringComparison.OrdinalIgnoreCase) ||
                IsPurelyNumericOrBooleanLiteral(val))
            {
                return m.Value;
            }

            secrets++;
            bool isColon = m.Value.Contains(':');
            return isColon
                ? $"{key}: [REDACTED_TOKEN]"
                : $"{key}=\"[REDACTED_TOKEN]\"";
        });

        // 8. Structural Multi-Player Nickname, Account ID, Audio Endpoint, & Generic UUID Scrubbing
        current = TelemetryPlayerKeyValueRegex().Replace(current, m =>
        {
            string val = m.Groups[2].Value;
            if (val.StartsWith("[PLAYER_", StringComparison.Ordinal) || val.StartsWith("[REDACTED", StringComparison.Ordinal))
            {
                return m.Value;
            }
            userIds++;
            return $"{m.Groups[1].Value}{FormatPlayer(val)}";
        });

        current = LuaAccountIdRegex().Replace(current, m =>
        {
            userIds++;
            return $"{m.Groups[1].Value}[REDACTED_ID]";
        });

        current = LuaNicknameRegex().Replace(current, m =>
        {
            string val = m.Groups[2].Value.Trim();
            if (string.IsNullOrWhiteSpace(val) || val.StartsWith("[PLAYER_", StringComparison.Ordinal) || val.StartsWith("[REDACTED", StringComparison.Ordinal))
            {
                return m.Value;
            }
            userIds++;
            return $"{m.Groups[1].Value}{FormatPlayer(val)}";
        });

        current = K2ColorPipeAccountTupleRegex().Replace(current, m =>
        {
            string nick = m.Groups[3].Value;
            userIds++;
            return $"{m.Groups[1].Value}[REDACTED_ID]|{FormatPlayer(nick)}|{m.Groups[4].Value}";
        });

        current = NativeGuardOfflineNickRegex().Replace(current, m =>
        {
            string nick = m.Groups[2].Value;
            if (nick.Equals("<empty>", StringComparison.OrdinalIgnoreCase)) return m.Value;
            userIds++;
            return $"{m.Groups[1].Value}{FormatPlayer(nick)}{m.Groups[3].Value}";
        });

        current = NativeGuardPrevNickRegex().Replace(current, m =>
        {
            string nick = m.Groups[2].Value;
            if (nick.Equals("<empty>", StringComparison.OrdinalIgnoreCase)) return m.Value;
            userIds++;
            return $"{m.Groups[1].Value}{FormatPlayer(nick)}{m.Groups[3].Value}";
        });

        current = LauncherIdentifiedActivePlayerRegex().Replace(current, m =>
        {
            string nick = m.Groups[2].Value.Trim();
            if (string.IsNullOrWhiteSpace(nick) || nick.StartsWith("[PLAYER_", StringComparison.Ordinal) || nick.StartsWith("[REDACTED", StringComparison.Ordinal))
            {
                return m.Value;
            }
            userIds++;
            return $"{m.Groups[1].Value}{FormatPlayer(nick)}";
        });

        current = DiscordOAuthAuthenticatedRegex().Replace(current, m =>
        {
            string discordUser = m.Groups[2].Value.Trim();
            string accountName = m.Groups[4].Value.Trim();
            userIds += 2;
            return $"{m.Groups[1].Value}{FormatPlayer(discordUser)}{m.Groups[3].Value}{FormatPlayer(accountName)}{m.Groups[5].Value}";
        });

        current = DiscordRpcQuotedIdentityRegex().Replace(current, m =>
        {
            string nick = m.Groups[2].Value.Trim();
            if (string.IsNullOrWhiteSpace(nick) || nick.Equals("<empty>", StringComparison.OrdinalIgnoreCase) || nick.StartsWith("[PLAYER_", StringComparison.Ordinal))
            {
                return m.Value;
            }
            userIds++;
            return $"{m.Groups[1].Value}{FormatPlayer(nick)}{m.Groups[3].Value}";
        });

        current = DiscordRpcLocalPlayerSuffixRegex().Replace(current, m =>
        {
            string nick = m.Groups[2].Value.Trim();
            if (string.IsNullOrWhiteSpace(nick) || nick.StartsWith("[PLAYER_", StringComparison.Ordinal))
            {
                return m.Value;
            }
            userIds++;
            return $"{m.Groups[1].Value}{FormatPlayer(nick)}{m.Groups[3].Value}";
        });

        current = DiscordRpcAccountAndNameRegex().Replace(current, m =>
        {
            string nick = m.Groups[4].Value.Trim();
            userIds += 2;
            return $"{m.Groups[1].Value}[REDACTED_ID]{m.Groups[3].Value}{FormatPlayer(nick)}{m.Groups[5].Value}";
        });

        current = DiscordRpcPresenceDetailsPlayerRegex().Replace(current, m =>
        {
            string nick = m.Groups[2].Value.Trim();
            if (string.IsNullOrWhiteSpace(nick) || nick.StartsWith("[PLAYER_", StringComparison.Ordinal) || nick.StartsWith("[REDACTED", StringComparison.Ordinal))
            {
                return m.Value;
            }
            userIds++;
            return $"{m.Groups[1].Value}{FormatPlayer(nick)}{m.Groups[3].Value}";
        });

        current = K2LoginSuccessRegex().Replace(current, m =>
        {
            string nick = m.Groups[3].Value.Trim();
            userIds += 2;
            if (m.Groups[4].Success)
            {
                secrets++;
                return $"{m.Groups[1].Value}[REDACTED_ID] {FormatPlayer(nick)} [REDACTED_TOKEN]";
            }
            return $"{m.Groups[1].Value}[REDACTED_ID] {FormatPlayer(nick)}";
        });

        current = K2AddSubaccountRegex().Replace(current, m =>
        {
            string nick = m.Groups[2].Value.Trim();
            if (string.IsNullOrWhiteSpace(nick) || nick.StartsWith("[PLAYER_", StringComparison.Ordinal) || nick.StartsWith("[REDACTED", StringComparison.Ordinal))
            {
                return m.Value;
            }
            userIds++;
            return $"{m.Groups[1].Value}{FormatPlayer(nick)}{m.Groups[3].Value}";
        });

        current = K2LadderSelfRowRegex().Replace(current, m =>
        {
            string nick = m.Groups[2].Value.Trim();
            if (string.IsNullOrWhiteSpace(nick) || nick.StartsWith("[PLAYER_", StringComparison.Ordinal) || nick.StartsWith("[REDACTED", StringComparison.Ordinal))
            {
                return m.Value;
            }
            userIds++;
            return $"{m.Groups[1].Value}{FormatPlayer(nick)}{m.Groups[3].Value}";
        });

        current = K2Fe2AvatarPathRegex().Replace(current, m =>
        {
            string avatarStem = m.Groups[2].Value.Trim();
            if (string.IsNullOrWhiteSpace(avatarStem) ||
                avatarStem.StartsWith("[PLAYER_", StringComparison.Ordinal) ||
                avatarStem.StartsWith("[REDACTED", StringComparison.Ordinal) ||
                avatarStem.Equals("default", StringComparison.OrdinalIgnoreCase) ||
                avatarStem.Equals("blank", StringComparison.OrdinalIgnoreCase) ||
                avatarStem.Equals("empty", StringComparison.OrdinalIgnoreCase) ||
                avatarStem.Equals("none", StringComparison.OrdinalIgnoreCase) ||
                avatarStem.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            {
                return m.Value;
            }
            userIds++;
            return $"{m.Groups[1].Value}{FormatPlayer(avatarStem)}{m.Groups[3].Value}";
        });

        current = FmodUsingDriverLineRegex().Replace(current, m =>
        {
            userIds++;
            return $"{m.Groups[1].Value}\"[AUDIO_DEVICE]\"";
        });

        current = AudioEndpointLabelRegex().Replace(current, m =>
        {
            if (m.Value.Contains("[AUDIO_DEVICE]", StringComparison.Ordinal)) return m.Value;
            userIds++;
            return $"{m.Groups[1].Value} ([AUDIO_DEVICE])";
        });

        current = GenericUuidRegex().Replace(current, m =>
        {
            userIds++;
            return FormatUuid(m.Value);
        });

        // 9. User profile directories & arbitrary non-system drive paths
        current = NtDeviceUserPathRegex().Replace(current, _ =>
        {
            userIds++;
            return "[USER_DIR]";
        });
        current = WindowsUserPathRegex().Replace(current, _ =>
        {
            userIds++;
            return "[USER_DIR]";
        });
        current = DrivelessWindowsUserPathRegex().Replace(current, _ =>
        {
            userIds++;
            return "[USER_DIR]";
        });
        current = PosixUserPathRegex().Replace(current, _ =>
        {
            userIds++;
            return "[USER_DIR]";
        });

        if (context is { StrictZeroPiiBundleMode: true })
        {
            // Collapse intermediate personal folders under [USER_DIR], [TEMP_DIR], [LOCAL_APPDATA], [APPDATA]
            current = TokenizedSubpathCollapseRegex().Replace(current, m =>
            {
                string rootToken = m.Groups[1].Value;
                string leaf = SanitizeExternalLeafFileName(m.Groups[2].Value, m.Value, context);
                return $"{rootToken}/.../{leaf}";
            });

            // Collapse arbitrary Windows drive paths with file extensions (including spaces like C:\Program Files\...)
            current = ArbitraryWindowsFileWithExtRegex().Replace(current, m =>
            {
                string rawPath = m.Groups[1].Value;
                userIds++;
                if (IsWindowsSystemPath(rawPath))
                {
                    return $"[SYSTEM_DIR]/{MinidumpPrivacySanitizer.ExtractCrossPlatformFileName(rawPath)}";
                }

                string leaf = MinidumpPrivacySanitizer.ExtractCrossPlatformFileName(rawPath);
                string safeLeaf = SanitizeExternalLeafFileName(leaf, rawPath, context);
                return $"[EXTERNAL_DIR]/{safeLeaf}";
            });

            // Catch any remaining bare drive directory paths without file extensions (e.g. D:\CustomUser\SecretFolder)
            current = ArbitraryWindowsBarePathRegex().Replace(current, m =>
            {
                string rawPath = m.Groups[1].Value;
                userIds++;
                return IsWindowsSystemPath(rawPath) ? "[SYSTEM_DIR]" : "[EXTERNAL_DIR]";
            });

            // Normalize ISO-8601 local timezone offsets (e.g. 2026-11-08T14:22:01.123-06:00 -> 2026-11-08T20:22:01.123Z)
            current = IsoLocalOffsetTimestampRegex().Replace(current, m =>
            {
                if (DateTimeOffset.TryParse(m.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset dto))
                {
                    return dto.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
                }
                return $"{m.Groups[1].Value}Z";
            });
        }

        // 10. Literal Environment.UserName, UserProfile leaf, Environment.MachineName, known nicknames, & config secrets
        string userName = userNameOverride ?? Environment.UserName;
        if (!string.IsNullOrWhiteSpace(userName) && userName.Length >= 3)
        {
            current = ScrubBoundedIdentifier(current, userName, "[REDACTED_USER]", ref userIds);
        }

        if (context != null)
        {
            foreach (string userLit in context.KnownUserLiterals)
            {
                current = ScrubBoundedIdentifier(current, userLit, "[REDACTED_USER]", ref userIds);
            }
        }

        string machineName = machineNameOverride ?? Environment.MachineName;
        if (!string.IsNullOrWhiteSpace(machineName) && machineName.Length >= 3)
        {
            current = ScrubBoundedIdentifier(current, machineName, "[REDACTED_HOST]", ref userIds);
        }

        if (!string.IsNullOrWhiteSpace(playerNickname) && playerNickname.Trim().Length >= 3)
        {
            string cleanNick = playerNickname.Trim();
            current = ScrubBoundedIdentifier(current, cleanNick, FormatPlayer(cleanNick), ref userIds);
        }

        if (context != null)
        {
            foreach (string knownPlayer in context.KnownPlayerNames.OrderByDescending(static s => s.Length))
            {
                if (current.Contains(knownPlayer, StringComparison.OrdinalIgnoreCase))
                {
                    current = ScrubBoundedIdentifier(current, knownPlayer, FormatPlayer(knownPlayer), ref userIds);
                }
            }

            foreach (string secret in context.KnownSecrets)
            {
                current = ReplaceOrdinal(current, secret, "[REDACTED_SECRET]", ref secrets);
            }
        }

        // 11. Generic Peer IPv4 & IPv6 addresses (preserving loopback, allowed endpoints, assembly versions, & C++ :: symbols)
        HashSet<string> allowedIps = new(StringComparer.OrdinalIgnoreCase)
        {
            "127.0.0.1",
            "0.0.0.0",
            "::1",
            "::"
        };
        AddAllowedHostIp(allowedIps, allowedMasterServerHost);
        AddAllowedHostIp(allowedIps, allowedCdnHost);

        string ipv4Snapshot = current;
        current = GenericIpv4Regex().Replace(current, m =>
        {
            string ip = m.Value;
            if (allowedIps.Contains(ip) || IsPrecededByVersionKeyword(ipv4Snapshot, m.Index))
            {
                return ip;
            }
            peerIps++;
            return "[REDACTED_IP]";
        });

        current = CandidateIpv6Regex().Replace(current, m =>
        {
            string candidate = m.Value;
            if (allowedIps.Contains(candidate))
            {
                return candidate;
            }

            // Guard against pure-letter C++ namespaces composed solely of hex letters (e.g. fb::cf)
            if (candidate.Contains("::", StringComparison.Ordinal) &&
                candidate.Count(c => c == ':') == 2 &&
                candidate.All(c => c == ':' || char.IsLetter(c)))
            {
                return candidate;
            }

            if (!IPAddress.TryParse(candidate, out IPAddress? parsed) ||
                parsed.AddressFamily != AddressFamily.InterNetworkV6)
            {
                return candidate;
            }

            if (IPAddress.IsLoopback(parsed) ||
                parsed.Equals(IPAddress.IPv6Any) ||
                parsed.Equals(IPAddress.IPv6None))
            {
                return candidate;
            }

            peerIps++;
            return "[REDACTED_IPV6]";
        });

        return (current, new RedactionAuditCounts(secrets, userIds, peerIps));
    }

    /// <summary>
    /// Pre-harvests all player/OAuth/DiscordRPC handles observed in WILLOWMAKER.log into <paramref name="context"/>
    /// before binary minidump sanitization and cross-log scrubbing execute, without pre-allocating [PLAYER_n] indices.
    /// </summary>
    public static void HarvestKnownPlayerNamesFromLauncherLog(string? rawLogText, PrivacyScrubContext context)
    {
        if (string.IsNullOrWhiteSpace(rawLogText))
        {
            return;
        }

        foreach (Match m in LauncherIdentifiedActivePlayerRegex().Matches(rawLogText))
        {
            context.AddKnownPlayerName(m.Groups[2].Value);
        }

        foreach (Match m in DiscordOAuthAuthenticatedRegex().Matches(rawLogText))
        {
            context.AddKnownPlayerName(m.Groups[2].Value);
            context.AddKnownPlayerName(m.Groups[4].Value);
        }

        foreach (Match m in DiscordRpcQuotedIdentityRegex().Matches(rawLogText))
        {
            string val = m.Groups[2].Value;
            if (!val.Equals("<empty>", StringComparison.OrdinalIgnoreCase))
            {
                context.AddKnownPlayerName(val);
            }
        }

        foreach (Match m in DiscordRpcAccountAndNameRegex().Matches(rawLogText))
        {
            context.AddKnownPlayerName(m.Groups[4].Value);
        }

        foreach (Match m in DiscordRpcPresenceDetailsPlayerRegex().Matches(rawLogText))
        {
            context.AddKnownPlayerName(m.Groups[2].Value);
        }

        foreach (Match m in NativeGuardOfflineNickRegex().Matches(rawLogText))
        {
            string val = m.Groups[2].Value;
            if (!val.Equals("<empty>", StringComparison.OrdinalIgnoreCase))
            {
                context.AddKnownPlayerName(val);
            }
        }

        foreach (Match m in NativeGuardPrevNickRegex().Matches(rawLogText))
        {
            string val = m.Groups[2].Value;
            if (!val.Equals("<empty>", StringComparison.OrdinalIgnoreCase))
            {
                context.AddKnownPlayerName(val);
            }
        }
    }

    /// <summary>
    /// Pre-harvests all player/subaccount handles and session login tokens observed in K2 console.log
    /// into <paramref name="context"/> before binary minidump sanitization and cross-log scrubbing execute.
    /// </summary>
    public static void HarvestKnownPlayerNamesFromConsoleLog(string? rawConsoleText, PrivacyScrubContext context)
    {
        if (string.IsNullOrWhiteSpace(rawConsoleText))
        {
            return;
        }

        foreach (Match m in K2LoginSuccessRegex().Matches(rawConsoleText))
        {
            context.AddKnownPlayerName(m.Groups[3].Value);
            if (m.Groups[5].Success)
            {
                string tokenPair = m.Groups[5].Value.Trim();
                context.AddKnownSecret(tokenPair);
                foreach (string part in tokenPair.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (part.Length >= 8)
                    {
                        context.AddKnownSecret(part);
                    }
                }
            }
        }

        foreach (Match m in K2AddSubaccountRegex().Matches(rawConsoleText))
        {
            context.AddKnownPlayerName(m.Groups[2].Value);
        }

        foreach (Match m in LuaNicknameRegex().Matches(rawConsoleText))
        {
            context.AddKnownPlayerName(m.Groups[2].Value);
        }

        foreach (Match m in K2ColorPipeAccountTupleRegex().Matches(rawConsoleText))
        {
            context.AddKnownPlayerName(m.Groups[3].Value);
        }

        foreach (Match m in K2LadderSelfRowRegex().Matches(rawConsoleText))
        {
            context.AddKnownPlayerName(m.Groups[2].Value);
        }

        foreach (Match m in TelemetryPlayerKeyValueRegex().Matches(rawConsoleText))
        {
            context.AddKnownPlayerName(m.Groups[2].Value);
        }
    }

    /// <summary>
    /// Structural Zero-PII Canary verification pass executed on every zip entry right before writing to disk.
    /// Checks UTF-8 (and UTF-16LE for binary minidumps) for raw drive paths, user directories, raw UUIDs, or known session PII.
    /// Returns null if clean, or an actionable diagnostic reason string if a violation is detected.
    /// </summary>
    public static string? FindZeroPiiCanaryViolation(
        string entryName,
        ReadOnlySpan<byte> entryBytes,
        PrivacyScrubContext context,
        bool isBinaryMinidump)
    {
        string utf8 = Encoding.UTF8.GetString(entryBytes);
        string utf8Inspect = isBinaryMinidump
            ? utf8
            : CanonicalRedactionTokenStripRegex().Replace(utf8, " ");
        string? utf16 = isBinaryMinidump ? Encoding.Unicode.GetString(entryBytes) : null;

        Regex drivePathRegex = isBinaryMinidump ? BinaryMinidumpDrivePathCanaryRegex() : ArbitraryWindowsBarePathRegex();
        if (drivePathRegex.IsMatch(utf8Inspect) || (utf16 != null && drivePathRegex.IsMatch(utf16)))
        {
            return $"Unredacted drive path detected in '{entryName}'.";
        }

        bool hasUserProfileMarker = isBinaryMinidump
            ? (utf8.Contains(@"\Users\", StringComparison.OrdinalIgnoreCase) ||
               utf8.Contains("/Users/", StringComparison.OrdinalIgnoreCase) ||
               (utf16 != null && (utf16.Contains(@"\Users\", StringComparison.OrdinalIgnoreCase) ||
                                  utf16.Contains("/Users/", StringComparison.OrdinalIgnoreCase))))
            : (utf8Inspect.Contains(@"\Users\", StringComparison.OrdinalIgnoreCase) ||
               PosixUserPathRegex().IsMatch(utf8Inspect));

        if (hasUserProfileMarker)
        {
            return $"Unredacted user profile directory marker detected in '{entryName}'.";
        }

        if (!isBinaryMinidump && GenericUuidRegex().IsMatch(utf8Inspect))
        {
            return $"Unredacted raw UUID detected in text entry '{entryName}'.";
        }

        if (ContainsRawHardwareMacAddress(utf8Inspect, requireLabeledOnly: isBinaryMinidump) ||
            (utf16 != null && ContainsRawHardwareMacAddress(utf16, requireLabeledOnly: isBinaryMinidump)))
        {
            return $"Unredacted hardware MAC address detected in '{entryName}'.";
        }

        foreach (string secret in context.KnownSecrets)
        {
            if (utf8Inspect.Contains(secret, StringComparison.Ordinal) ||
                (utf16 != null && utf16.Contains(secret, StringComparison.Ordinal)))
            {
                return $"Known session secret survived scrubbing in '{entryName}'.";
            }
        }

        foreach (string player in context.KnownPlayerNames)
        {
            if (player.Length >= 4 &&
                (ContainsBoundedIdentifier(utf8Inspect, player) ||
                 (utf16 != null && ContainsBoundedIdentifier(utf16, player))))
            {
                return $"Known player nickname survived scrubbing in '{entryName}'.";
            }
        }

        foreach (string userLit in context.KnownUserLiterals)
        {
            if (userLit.Length >= 4 &&
                (ContainsBoundedIdentifier(utf8Inspect, userLit) ||
                 (utf16 != null && ContainsBoundedIdentifier(utf16, userLit))))
            {
                return $"Known OS user/profile identifier survived scrubbing in '{entryName}'.";
            }
        }

        return null;
    }

    public static string ClassifyZeroPiiViolationKind(string? violation)
    {
        if (string.IsNullOrWhiteSpace(violation))
        {
            return "pii_canary";
        }

        if (violation.Contains("drive path", StringComparison.OrdinalIgnoreCase))
        {
            return "drive_path";
        }

        if (violation.Contains("user profile", StringComparison.OrdinalIgnoreCase))
        {
            return "user_profile_path";
        }

        if (violation.Contains("UUID", StringComparison.OrdinalIgnoreCase))
        {
            return "raw_uuid";
        }

        if (violation.Contains("secret", StringComparison.OrdinalIgnoreCase))
        {
            return "session_secret";
        }

        if (violation.Contains("player nickname", StringComparison.OrdinalIgnoreCase))
        {
            return "player_nickname";
        }

        if (violation.Contains("OS user", StringComparison.OrdinalIgnoreCase))
        {
            return "os_user_identifier";
        }

        if (violation.Contains("MAC address", StringComparison.OrdinalIgnoreCase))
        {
            return "hardware_mac_address";
        }

        return "pii_canary";
    }

    public static bool ContainsRawHardwareMacAddress(string text, bool requireLabeledOnly = false)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("mac", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (HardwareMacLabeledRegex().IsMatch(text))
        {
            return true;
        }

        if (!requireLabeledOnly)
        {
            foreach (Match lineMatch in HardwareMacContextLineRegex().Matches(text))
            {
                if (StandaloneHardwareMacRegex().IsMatch(lineMatch.Value))
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal static bool ContainsBoundedIdentifier(string text, string identifier)
    {
        int idx = text.IndexOf(identifier, StringComparison.OrdinalIgnoreCase);
        while (idx >= 0)
        {
            bool leftBound = idx == 0 || !char.IsLetterOrDigit(text[idx - 1]);
            int end = idx + identifier.Length;
            bool rightBound = end >= text.Length || !char.IsLetterOrDigit(text[end]);
            if (leftBound && rightBound)
            {
                return true;
            }
            idx = text.IndexOf(identifier, idx + 1, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static bool IsPurelyNumericOrBooleanLiteral(string value)
    {
        if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    }

    private static bool IsPrecededByVersionKeyword(string text, int matchIndex)
    {
        if (matchIndex <= 0) return false;
        int lookbackStart = Math.Max(0, matchIndex - 20);
        ReadOnlySpan<char> prefix = text.AsSpan(lookbackStart, matchIndex - lookbackStart).TrimEnd();
        if (prefix.Length == 0) return false;

        if ((prefix[^1] == 'v' || prefix[^1] == 'V') &&
            (prefix.Length == 1 || !char.IsLetterOrDigit(prefix[^2])))
        {
            return true;
        }

        return prefix.EndsWith("Version=", StringComparison.OrdinalIgnoreCase) ||
               prefix.EndsWith("Version:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWindowsSystemPath(string rawPath)
    {
        string normalized = rawPath.Replace('/', '\\');
        return normalized.StartsWith(@"C:\Windows\", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, @"C:\Windows", StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeExternalLeafFileName(string leaf, string fullPath, PrivacyScrubContext context)
    {
        if (leaf.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(leaf, context.FaultingModuleName, StringComparison.OrdinalIgnoreCase) &&
            !MinidumpPrivacySanitizer.IsSafeEngineOrSystemModule(fullPath, leaf))
        {
            return context.MapThirdPartyModuleName(leaf);
        }

        return leaf;
    }

    private static string ScrubBoundedIdentifier(string input, string identifier, string replacement, ref int counter)
    {
        if (string.IsNullOrWhiteSpace(identifier) || identifier.Length < 3)
        {
            return input;
        }

        string pattern = $"(?<![A-Za-z0-9]){Regex.Escape(identifier)}(?![A-Za-z0-9])";
        int localHits = 0;
        string updated = Regex.Replace(input, pattern, m =>
        {
            if (IsInsideCanonicalRedactionBracket(input, m.Index, m.Length))
            {
                return m.Value;
            }

            localHits++;
            return replacement;
        }, RegexOptions.IgnoreCase);
        counter += localHits;
        return updated;
    }

    private static bool IsInsideCanonicalRedactionBracket(string text, int index, int length)
    {
        int openIdx = -1;
        int minScan = Math.Max(0, index - 96);
        for (int i = index - 1; i >= minScan; i--)
        {
            char c = text[i];
            if (c is '\r' or '\n' or ']')
            {
                break;
            }

            if (c == '[')
            {
                openIdx = i;
                break;
            }
        }

        if (openIdx < 0)
        {
            return false;
        }

        int closeIdx = -1;
        int maxScan = Math.Min(text.Length - 1, index + length + 96);
        for (int i = index + length; i <= maxScan; i++)
        {
            char c = text[i];
            if (c is '\r' or '\n' or '[')
            {
                break;
            }

            if (c == ']')
            {
                closeIdx = i;
                break;
            }
        }

        if (closeIdx < 0)
        {
            return false;
        }

        string candidateToken = text.Substring(openIdx, closeIdx - openIdx + 1);
        Match match = CanonicalRedactionTokenStripRegex().Match(candidateToken);
        return match.Success && match.Index == 0 && match.Length == candidateToken.Length;
    }

    private static string ReplaceOrdinalIgnoreCase(string input, string oldValue, string newValue, ref int counter)
    {
        if (string.IsNullOrEmpty(oldValue)) return input;
        int idx = input.IndexOf(oldValue, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return input;

        StringBuilder sb = new(input.Length);
        int pos = 0;
        while (idx >= 0)
        {
            sb.Append(input, pos, idx - pos);
            sb.Append(newValue);
            counter++;
            pos = idx + oldValue.Length;
            idx = input.IndexOf(oldValue, pos, StringComparison.OrdinalIgnoreCase);
        }
        sb.Append(input, pos, input.Length - pos);
        return sb.ToString();
    }

    private static string ReplaceOrdinal(string input, string oldValue, string newValue, ref int counter)
    {
        if (string.IsNullOrEmpty(oldValue)) return input;
        int idx = input.IndexOf(oldValue, StringComparison.Ordinal);
        if (idx < 0) return input;

        StringBuilder sb = new(input.Length);
        int pos = 0;
        while (idx >= 0)
        {
            sb.Append(input, pos, idx - pos);
            sb.Append(newValue);
            counter++;
            pos = idx + oldValue.Length;
            idx = input.IndexOf(oldValue, pos, StringComparison.Ordinal);
        }
        sb.Append(input, pos, input.Length - pos);
        return sb.ToString();
    }

    private static void AddAllowedHostIp(HashSet<string> allowedIps, string? hostOrUrl)
    {
        if (string.IsNullOrWhiteSpace(hostOrUrl))
        {
            return;
        }

        string trimmed = hostOrUrl.Trim();
        allowedIps.Add(trimmed);

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? parsedUri) && !string.IsNullOrWhiteSpace(parsedUri.Host))
        {
            allowedIps.Add(parsedUri.Host.Trim('[', ']'));
            return;
        }

        if (Uri.TryCreate($"http://{trimmed}", UriKind.Absolute, out Uri? hostUri) && !string.IsNullOrWhiteSpace(hostUri.Host))
        {
            allowedIps.Add(hostUri.Host.Trim('[', ']'));
        }
    }

    /// <summary>
    /// In-place byte scrubbing of a minidump buffer.
    /// Overwrites occurrences of sensitive strings with 'X' in ASCII and UTF-16LE,
    /// preserving identical byte lengths and all internal MDMP RVA stream offsets.
    /// Short identifiers (&lt; 6 chars, e.g. "manu", "max", "win") are scrubbed only inside path delimiters
    /// so module names like win32u.dll or CVar names like maxFrameRate are never corrupted.
    /// </summary>
    public static int ScrubBinaryMinidump(byte[] dmpBytes, IEnumerable<string?> sensitiveStrings)
    {
        ArgumentNullException.ThrowIfNull(dmpBytes);
        if (sensitiveStrings == null || dmpBytes.Length == 0) return 0;

        int totalReplacements = 0;
        foreach (string? raw in sensitiveStrings)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.Length < 3) continue;

            if (raw.Length < 6)
            {
                ReadOnlySpan<(char Lead, char Trail)> delimiters =
                [
                    ('/', '/'),
                    ('\\', '\\'),
                    ('/', '\\'),
                    ('\\', '/')
                ];

                foreach ((char lead, char trail) in delimiters)
                {
                    string bounded = $"{lead}{raw}{trail}";
                    string boundedMask = $"{lead}{new string('X', raw.Length)}{trail}";

                    totalReplacements += ReplaceBytesInPlace(
                        dmpBytes,
                        Encoding.ASCII.GetBytes(bounded),
                        Encoding.ASCII.GetBytes(boundedMask));

                    totalReplacements += ReplaceBytesInPlace(
                        dmpBytes,
                        Encoding.Unicode.GetBytes(bounded),
                        Encoding.Unicode.GetBytes(boundedMask));
                }

                continue;
            }

            // ASCII pattern
            byte[] asciiPattern = Encoding.ASCII.GetBytes(raw);
            byte[] asciiReplacement = new byte[asciiPattern.Length];
            Array.Fill(asciiReplacement, (byte)'X');
            totalReplacements += ReplaceBytesInPlace(dmpBytes, asciiPattern, asciiReplacement);

            // UTF-16LE pattern
            byte[] unicodePattern = Encoding.Unicode.GetBytes(raw);
            byte[] unicodeReplacement = new byte[unicodePattern.Length];
            for (int i = 0; i < unicodeReplacement.Length; i += 2)
            {
                unicodeReplacement[i] = (byte)'X';
                if (i + 1 < unicodeReplacement.Length) unicodeReplacement[i + 1] = 0x00;
            }
            totalReplacements += ReplaceBytesInPlace(dmpBytes, unicodePattern, unicodeReplacement);
        }

        return totalReplacements;
    }

    private static int ReplaceBytesInPlace(byte[] buffer, byte[] pattern, byte[] replacement)
    {
        if (pattern.Length == 0 || pattern.Length != replacement.Length || buffer.Length < pattern.Length)
        {
            return 0;
        }

        int count = 0;
        for (int i = 0; i <= buffer.Length - pattern.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (buffer[i + j] != pattern[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                Array.Copy(replacement, 0, buffer, i, replacement.Length);
                count++;
                i += pattern.Length - 1;
            }
        }

        return count;
    }
}
