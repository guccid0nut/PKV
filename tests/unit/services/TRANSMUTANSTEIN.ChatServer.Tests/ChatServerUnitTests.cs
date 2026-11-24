// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using Xunit;

namespace TRANSMUTANSTEIN.ChatServer.Tests;

public sealed class ChatServerUnitTests
{
    [Fact]
    public void ChatServerUnit_ChatServerAssembly_ShouldLoadSuccessfully()
    {
        typeof(ChatServerUnitTests).Assembly.Should().NotBeNull();
    }
}
