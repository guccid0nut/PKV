// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Communication;

namespace ASPIRE.Tests.ASPIRE.Common;

public class ChatChannelsTests
{
    [Fact]
    public void ChatChannels_AllDefaultChannels_ContainsExpectedChannels()
    {
        // Act
        string[] channels = ChatChannels.AllDefaultChannels;

        // Assert
        Assert.Contains(ChatChannels.GeneralChannel, channels);
        Assert.Contains(ChatChannels.GameMastersChannel, channels);
        Assert.Contains(ChatChannels.GuestsChannel, channels);
        Assert.Contains(ChatChannels.ServerHostsChannel, channels);
        Assert.Contains(ChatChannels.StreamersChannel, channels);
        Assert.Contains(ChatChannels.VIPChannel, channels);
        Assert.Contains(ChatChannels.StaffChannel, channels);
        Assert.Equal(7, channels.Length);
    }
}
