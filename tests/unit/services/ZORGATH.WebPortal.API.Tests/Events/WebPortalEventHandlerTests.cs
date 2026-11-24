// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Events;
using Xunit;
using ZORGATH.WebPortal.API.Services.Events;

namespace ZORGATH.WebPortal.API.Tests.Events;

public sealed class WebPortalEventHandlerTests
{
    [Fact]
    public async Task WebPortalEventHandler_WebPortalMatchCompletedEventHandler_ShouldHandleMatchCompletion()
    {
        // Arrange
        WebPortalMatchCompletedEventHandler handler = new(
            NullLogger<WebPortalMatchCompletedEventHandler>.Instance);

        MatchCompletedEvent matchEvent = new(
            99991,
            [1, 2, 3, 4, 5, 6, 7, 8, 9, 10],
            2,
            TimeSpan.FromMinutes(45),
            "ranked"
        );

        // Act
        Func<Task> act = async () => await handler.HandleAsync(matchEvent);

        // Assert
        await act.Should().NotThrowAsync();
    }
}
