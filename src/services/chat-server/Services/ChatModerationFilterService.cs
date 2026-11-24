// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace TRANSMUTANSTEIN.ChatServer.Services;

public enum ChatModerationAction
{
    Allow,
    MaskAndAllow,
    RejectSpam,
    RejectDuplicateSpam,
    RejectProfanity
}

public readonly record struct ChatModerationResult(
    ChatModerationAction Action,
    string ProcessedMessage,
    string? Reason,
    string? MatchedRule)
{
    public bool IsBlocked => Action is ChatModerationAction.RejectSpam
        or ChatModerationAction.RejectDuplicateSpam
        or ChatModerationAction.RejectProfanity;
}

public sealed class ChatModerationOptions
{
    public bool ChatFilterEnabled { get; set; } = true;
    public int SlidingWindowSeconds { get; set; } = 10;
    public int MaxMessagesPerWindow { get; set; } = 6;
    public int MaxDuplicateMessagesPerWindow { get; set; } = 3;
    public bool MaskBlockedTerms { get; set; } = true;
    public string[] BlockedTerms { get; set; } = [];
}

/// <summary>
/// Config-driven sliding-window burst/duplicate spam throttle and word-boundary profanity/slur filter
/// for public chat channels in TRANSMUTANSTEIN.
/// </summary>
public sealed class ChatModerationFilterService
{
    public const int MaxTrackedAccounts = 16384;

    private static readonly string[] DefaultBlockedTerms =
    [
        "free-gold-hack",
        "account-stealer",
        "phishing-login",
        "kys",
        "slur_placeholder_1"
    ];

    private sealed class AccountSpamState
    {
        public readonly Queue<(long Ticks, string NormalizedMessage)> History = new();
        public long LastSeenTicks;
    }

    private readonly ConcurrentDictionary<int, AccountSpamState> _accountStates = new();
    private readonly ChatModerationOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ChatModerationFilterService>? _logger;
    private readonly List<(string Term, Regex Pattern)> _compiledPatterns = [];
    private int _pruneCounter;

    public ChatModerationFilterService(
        IConfiguration? configuration = null,
        TimeProvider? timeProvider = null,
        ILogger<ChatModerationFilterService>? logger = null,
        ChatModerationOptions? optionsOverride = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;

        ChatModerationOptions opts = optionsOverride ?? new ChatModerationOptions();
        if (optionsOverride is null && configuration is not null)
        {
            IConfigurationSection section = configuration.GetSection("Moderation");
            if (section.Exists())
            {
                opts.ChatFilterEnabled = section.GetValue("ChatFilterEnabled", opts.ChatFilterEnabled);
                opts.SlidingWindowSeconds = Math.Clamp(section.GetValue("SlidingWindowSeconds", opts.SlidingWindowSeconds), 1, 300);
                opts.MaxMessagesPerWindow = Math.Clamp(section.GetValue("MaxMessagesPerWindow", opts.MaxMessagesPerWindow), 1, 100);
                opts.MaxDuplicateMessagesPerWindow = Math.Clamp(section.GetValue("MaxDuplicateMessagesPerWindow", opts.MaxDuplicateMessagesPerWindow), 1, 50);
                opts.MaskBlockedTerms = section.GetValue("MaskBlockedTerms", opts.MaskBlockedTerms);
                string[]? configuredTerms = section.GetSection("BlockedTerms").Get<string[]>();
                if (configuredTerms is { Length: > 0 })
                {
                    opts.BlockedTerms = configuredTerms;
                }
            }
        }

        _options = opts;
        BuildCompiledPatterns();
    }

    public bool IsEnabled => _options.ChatFilterEnabled;

    public ChatModerationResult EvaluateMessage(int accountId, AccountType accountType, string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return new ChatModerationResult(ChatModerationAction.Allow, string.Empty, null, null);
        }

        if (!_options.ChatFilterEnabled)
        {
            return new ChatModerationResult(ChatModerationAction.Allow, message, null, null);
        }

        // Staff / GameMaster / MatchModerator accounts are exempt from automated chat throttling & filtering.
        if (accountType is AccountType.Staff or AccountType.GameMaster or AccountType.MatchModerator)
        {
            return new ChatModerationResult(ChatModerationAction.Allow, message, null, null);
        }

        // 1. Sliding-window burst & duplicate spam check
        long nowTicks = _timeProvider.GetUtcNow().UtcTicks;
        TimeSpan window = TimeSpan.FromSeconds(Math.Clamp(_options.SlidingWindowSeconds, 1, 300));
        long cutoffTicks = nowTicks - window.Ticks;

        if (Interlocked.Increment(ref _pruneCounter) % 256 == 0 || _accountStates.Count >= MaxTrackedAccounts)
        {
            PruneStaleAccounts(cutoffTicks);
        }

        string normalizedMessage = NormalizeForDuplicateCheck(message);
        AccountSpamState state = _accountStates.GetOrAdd(accountId, _ => new AccountSpamState());
        lock (state)
        {
            while (state.History.Count > 0 && state.History.Peek().Ticks <= cutoffTicks)
            {
                state.History.Dequeue();
            }

            state.LastSeenTicks = nowTicks;

            int duplicateCount = 0;
            foreach ((long _, string prev) in state.History)
            {
                if (string.Equals(prev, normalizedMessage, StringComparison.Ordinal))
                {
                    duplicateCount++;
                }
            }

            if (duplicateCount >= _options.MaxDuplicateMessagesPerWindow)
            {
                _logger?.LogWarning(
                    "[MODERATION] Blocked duplicate chat spam from AccountId={AccountId} ({Duplicates} identical msgs within {WindowSec}s).",
                    accountId,
                    duplicateCount + 1,
                    (int)window.TotalSeconds);

                return new ChatModerationResult(
                    ChatModerationAction.RejectDuplicateSpam,
                    string.Empty,
                    $"Please do not repeat the same message rapidly (limit {_options.MaxDuplicateMessagesPerWindow} per {(int)window.TotalSeconds}s).",
                    "duplicate_spam");
            }

            if (state.History.Count >= _options.MaxMessagesPerWindow)
            {
                _logger?.LogWarning(
                    "[MODERATION] Blocked burst chat spam from AccountId={AccountId} ({Count}/{Limit} msgs within {WindowSec}s).",
                    accountId,
                    state.History.Count + 1,
                    _options.MaxMessagesPerWindow,
                    (int)window.TotalSeconds);

                return new ChatModerationResult(
                    ChatModerationAction.RejectSpam,
                    string.Empty,
                    $"You are sending messages too quickly (limit {_options.MaxMessagesPerWindow} per {(int)window.TotalSeconds}s).",
                    "sliding_window_burst");
            }

            state.History.Enqueue((nowTicks, normalizedMessage));
        }

        // 2. Word-boundary profanity / blocked-term check
        string current = message;
        string? firstMatchedTerm = null;

        foreach ((string term, Regex regex) in _compiledPatterns)
        {
            try
            {
                if (regex.IsMatch(current))
                {
                    firstMatchedTerm ??= term;
                    if (!_options.MaskBlockedTerms)
                    {
                        _logger?.LogWarning(
                            "[MODERATION] Rejected blocked chat term from AccountId={AccountId} (rule='{Rule}').",
                            accountId,
                            term);

                        return new ChatModerationResult(
                            ChatModerationAction.RejectProfanity,
                            string.Empty,
                            "Your message was blocked by the chat moderation filter.",
                            term);
                    }

                    current = regex.Replace(current, "***");
                }
            }
            catch (RegexMatchTimeoutException)
            {
                _logger?.LogWarning(
                    "[MODERATION] Regex evaluation timed out for AccountId={AccountId} on rule='{Rule}'; skipping pattern safely.",
                    accountId,
                    term);
            }
        }

        if (firstMatchedTerm is not null)
        {
            _logger?.LogInformation(
                "[MODERATION] Masked blocked term in chat message from AccountId={AccountId} (rule='{Rule}').",
                accountId,
                firstMatchedTerm);

            return new ChatModerationResult(
                ChatModerationAction.MaskAndAllow,
                current,
                null,
                firstMatchedTerm);
        }

        return new ChatModerationResult(ChatModerationAction.Allow, message, null, null);
    }

    private void BuildCompiledPatterns()
    {
        HashSet<string> mergedTerms = new(StringComparer.OrdinalIgnoreCase);
        foreach (string t in DefaultBlockedTerms)
        {
            if (!string.IsNullOrWhiteSpace(t)) mergedTerms.Add(t.Trim());
        }

        if (_options.BlockedTerms is { Length: > 0 })
        {
            foreach (string t in _options.BlockedTerms)
            {
                if (!string.IsNullOrWhiteSpace(t)) mergedTerms.Add(t.Trim());
            }
        }

        foreach (string term in mergedTerms)
        {
            string escaped = Regex.Escape(term);
            string pattern = $@"(?<![A-Za-z0-9]){escaped}(?![A-Za-z0-9])";
            _compiledPatterns.Add((term, new Regex(
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
                TimeSpan.FromMilliseconds(50))));
        }
    }

    private void PruneStaleAccounts(long cutoffTicks)
    {
        foreach (KeyValuePair<int, AccountSpamState> kvp in _accountStates)
        {
            bool remove = false;
            lock (kvp.Value)
            {
                if (kvp.Value.LastSeenTicks <= cutoffTicks)
                {
                    remove = true;
                }
            }

            if (remove)
            {
                _accountStates.TryRemove(kvp.Key, out _);
            }
        }
    }

    private static string NormalizeForDuplicateCheck(string raw)
    {
        // Collapse repeated whitespace and normalize case so trivial spacing tweaks don't bypass duplicate spam detection.
        string trimmed = raw.Trim().ToLowerInvariant();
        return Regex.Replace(trimmed, @"\s+", " ", RegexOptions.None, TimeSpan.FromMilliseconds(50));
    }
}
