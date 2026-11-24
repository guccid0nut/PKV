// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using NEXUS.Contracts.Domain;
using TRANSMUTANSTEIN.ChatServer.Services;
using Xunit;

namespace TRANSMUTANSTEIN.ChatServer.Tests;

public sealed class Wave2ChatModerationFilterTests
{
    private sealed class MutableFakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }

    [Fact]
    public void ChatModerationFilter_BlocksBurstAndDuplicateSpam_AndRecoversAfterSlidingWindow()
    {
        // protects: Public channel stability against rapid burst flooding and copy-paste spam.
        // fails_when: Excess burst or duplicate messages slip past the sliding window or fail to recover after expiry.
        // seam: ChatModerationFilterService.EvaluateMessage with deterministic TimeProvider.
        MutableFakeTimeProvider clock = new();
        ChatModerationFilterService filter = new(
            timeProvider: clock,
            optionsOverride: new ChatModerationOptions
            {
                ChatFilterEnabled = true,
                SlidingWindowSeconds = 10,
                MaxMessagesPerWindow = 4,
                MaxDuplicateMessagesPerWindow = 2
            });

        // Duplicate spam check: 2 identical messages allowed, 3rd rejected
        filter.EvaluateMessage(100, AccountType.Normal, "gg mid").Action.Should().Be(ChatModerationAction.Allow);
        filter.EvaluateMessage(100, AccountType.Normal, "  GG   mid ").Action.Should().Be(ChatModerationAction.Allow);
        ChatModerationResult dupBlocked = filter.EvaluateMessage(100, AccountType.Normal, "gg mid");
        dupBlocked.IsBlocked.Should().BeTrue();
        dupBlocked.Action.Should().Be(ChatModerationAction.RejectDuplicateSpam);

        // Burst spam check on a different account: 4 distinct messages allowed, 5th rejected
        for (int i = 1; i <= 4; i++)
        {
            filter.EvaluateMessage(200, AccountType.Normal, $"unique message {i}").Action.Should().Be(ChatModerationAction.Allow);
        }

        ChatModerationResult burstBlocked = filter.EvaluateMessage(200, AccountType.Normal, "unique message 5");
        burstBlocked.IsBlocked.Should().BeTrue();
        burstBlocked.Action.Should().Be(ChatModerationAction.RejectSpam);

        // Advance past sliding window -> allowed again
        clock.Advance(TimeSpan.FromSeconds(11));
        filter.EvaluateMessage(200, AccountType.Normal, "recovered after window").Action.Should().Be(ChatModerationAction.Allow);
    }

    [Fact]
    public void ChatModerationFilter_MasksOrRejectsBlockedTerms_WithWordBoundaryAccuracy_AndExemptsStaff()
    {
        // protects: Configurable profanity/scam filtering without substring false positives or staff interference.
        // fails_when: Blocked terms pass unmasked, safe words containing substrings are corrupted, or Staff/kill-switch are ignored.
        // seam: ChatModerationFilterService.EvaluateMessage across Mask, Reject, Staff, and Disabled modes.
        ChatModerationFilterService maskFilter = new(
            optionsOverride: new ChatModerationOptions
            {
                ChatFilterEnabled = true,
                MaskBlockedTerms = true,
                BlockedTerms = ["badword"]
            });

        ChatModerationResult masked = maskFilter.EvaluateMessage(301, AccountType.Normal, "you are a BADWORD player");
        masked.Action.Should().Be(ChatModerationAction.MaskAndAllow);
        masked.IsBlocked.Should().BeFalse();
        masked.ProcessedMessage.Should().Be("you are a *** player");

        // Substring inside a benign word MUST NOT trigger word-boundary match
        ChatModerationResult benign = maskFilter.EvaluateMessage(302, AccountType.Normal, "superbadwordish");
        benign.Action.Should().Be(ChatModerationAction.Allow);
        benign.ProcessedMessage.Should().Be("superbadwordish");

        // Staff exemption
        ChatModerationResult staffMsg = maskFilter.EvaluateMessage(303, AccountType.Staff, "testing badword rule");
        staffMsg.Action.Should().Be(ChatModerationAction.Allow);
        staffMsg.ProcessedMessage.Should().Be("testing badword rule");

        // Reject mode (MaskBlockedTerms = false)
        ChatModerationFilterService rejectFilter = new(
            optionsOverride: new ChatModerationOptions
            {
                ChatFilterEnabled = true,
                MaskBlockedTerms = false,
                BlockedTerms = ["badword"]
            });
        ChatModerationResult rejected = rejectFilter.EvaluateMessage(304, AccountType.Normal, "badword!");
        rejected.IsBlocked.Should().BeTrue();
        rejected.Action.Should().Be(ChatModerationAction.RejectProfanity);

        // Config kill-switch (ChatFilterEnabled = false)
        ChatModerationFilterService disabledFilter = new(
            optionsOverride: new ChatModerationOptions
            {
                ChatFilterEnabled = false,
                BlockedTerms = ["badword"]
            });
        disabledFilter.EvaluateMessage(305, AccountType.Normal, "badword").Action.Should().Be(ChatModerationAction.Allow);
    }
}
