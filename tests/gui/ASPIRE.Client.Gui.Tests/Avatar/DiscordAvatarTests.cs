// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Avalonia.Media.Imaging;
using NEXUS.ControlPanel.GUI.Services;
using Xunit;

namespace ASPIRE.Tests.Avatar;

public sealed class DiscordAvatarTests
{
    [Fact]
    [Trait("Category", "Manual")]
    public async Task DiscordAvatarService_WhenAvatarIsNull_HandlesNullSafelyAndReturnsDefaultAvatar()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        Bitmap? defaultBmp = DiscordAvatarService.GetDefaultAvatar();
        Bitmap? cdnBmp = await DiscordAvatarService.LoadAvatarFromCdnAsync(null, null);
        Assert.Equal(defaultBmp, cdnBmp);
    }

    [Fact]
    public void UserDetailDto_HasAvatar_ReflectsAvatarBitmapState()
    {
        UserDetailDto dto = new()
        {
            Id = 3,
            DiscordId = "100000000000000001",
            DiscordUsername = "guccid0nut",
            DiscordAvatar = "0123456789abcdef0123456789abcdef"
        };

        Assert.False(dto.HasAvatar);
        Assert.Equal("@guccid0nut", dto.DisplayName);
    }

    [Fact]
    public void AccountItemDto_IconProperties_ReflectsEquippedCustomIcon()
    {
        AccountItemDto account = new()
        {
            Id = 1,
            Name = "guccidonut",
            IsMain = true,
            Icon = "custom_icon:1"
        };

        Assert.True(account.HasCustomIcon);
        Assert.Equal("🏷️ custom_icon:1", account.IconDisplay);
        Assert.False(account.HasAvatar);

        AccountItemDto hostAccount = new()
        {
            Id = 2,
            Name = "HOST",
            Icon = null
        };

        Assert.False(hostAccount.HasCustomIcon);
        Assert.Equal(string.Empty, hostAccount.IconDisplay);
    }

    [Fact]
    public void DiscordAvatarService_ClearCache_ExecutesWithoutError()
    {
        DiscordAvatarService.ClearCache();
    }
}
