// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Avalonia.Layout;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NEXUS.ControlPanel.GUI.ViewModels;

public sealed partial class GameServerChatMessageViewModel : ObservableObject
{
    [ObservableProperty] private string _sender = "Server";
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private DateTimeOffset _timestamp = TimeProvider.System.GetUtcNow();
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsServer))]
    [NotifyPropertyChangedFor(nameof(SenderColor))]
    private bool _isOperator;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SenderDisplay))]
    [NotifyPropertyChangedFor(nameof(SenderColor))]
    private bool _isError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsServer))]
    private bool _isSystem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsServer))]
    [NotifyPropertyChangedFor(nameof(SenderColor))]
    private bool _isPlayerChat;

    public bool IsServer => !IsOperator && !IsSystem && !IsPlayerChat;
    public string SenderDisplay => IsError ? "⚠️ Server Error" : Sender;
    public string SenderColor => IsError ? "#EF4444" : (IsOperator ? "#38BDF8" : (IsPlayerChat ? "#10B981" : "#94A3B8"));
    [ObservableProperty] private string _deliveryChannel = string.Empty;
    [ObservableProperty] private string _bubbleBackground = "#0F172A";
    [ObservableProperty] private string _bubbleBorder = "#1E293B";
    [ObservableProperty] private string _textForeground = "#F8FAFC";
    [ObservableProperty] private HorizontalAlignment _alignment = HorizontalAlignment.Left;

    public string FormattedTime => Timestamp.ToLocalTime().ToString("HH:mm:ss");

    public static GameServerChatMessageViewModel CreateOperatorMessage(string command, string channel = "stdin")
    {
        return new GameServerChatMessageViewModel
        {
            Sender = "Operator",
            Message = command,
            Timestamp = TimeProvider.System.GetUtcNow(),
            IsOperator = true,
            DeliveryChannel = channel,
            BubbleBackground = "#1E1B4B",
            BubbleBorder = "#6366F1",
            TextForeground = "#38BDF8",
            Alignment = HorizontalAlignment.Right
        };
    }

    public static GameServerChatMessageViewModel CreateServerLog(string text, bool isError = false, DateTimeOffset? timestamp = null)
    {
        return new GameServerChatMessageViewModel
        {
            Sender = "Server",
            Message = text,
            Timestamp = timestamp ?? TimeProvider.System.GetUtcNow(),
            IsError = isError,
            BubbleBackground = isError ? "#450A0A" : "#0F172A",
            BubbleBorder = isError ? "#EF4444" : "#1E293B",
            TextForeground = isError ? "#FCA5A5" : "#E2E8F0",
            Alignment = HorizontalAlignment.Left
        };
    }

    public static GameServerChatMessageViewModel CreateSystemMessage(string text, DateTimeOffset? timestamp = null)
    {
        return new GameServerChatMessageViewModel
        {
            Sender = "System",
            Message = text,
            Timestamp = timestamp ?? TimeProvider.System.GetUtcNow(),
            IsSystem = true,
            BubbleBackground = "#1E293B",
            BubbleBorder = "#334155",
            TextForeground = "#94A3B8",
            Alignment = HorizontalAlignment.Center
        };
    }

    public static GameServerChatMessageViewModel CreatePlayerChat(
        string sender,
        string message,
        bool isAllChat = true,
        DateTimeOffset? timestamp = null)
    {
        return new GameServerChatMessageViewModel
        {
            Sender = sender,
            Message = message,
            Timestamp = timestamp ?? TimeProvider.System.GetUtcNow(),
            IsPlayerChat = true,
            BubbleBackground = isAllChat ? "#064E3B" : "#1E3A5F",
            BubbleBorder = isAllChat ? "#10B981" : "#38BDF8",
            TextForeground = "#F8FAFC",
            Alignment = HorizontalAlignment.Left
        };
    }
}
