// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Compatibility;

namespace ASPIRE.Tests.ASPIRE.Common;

public class CompatibilityTests
{
    [Fact]
    public void Client_GetVersion_ReturnsValidVersionString()
    {
        // Act
        string version = Client.GetVersion();

        // Assert
        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.Contains(".", version);

        // Verify format depending on whether hotfix is 0
        string expectedClientVersion = $"{Client.MAJOR_VERSION}.{Client.MINOR_VERSION}.{Client.MICRO_VERSION}";
        Assert.Equal(expectedClientVersion, version);
    }

    [Fact]
    public void Server_GetVersion_ReturnsValidVersionString()
    {
        // Act
        string version = Server.GetVersion();

        // Assert
        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.Contains(".", version);

        // Verify format depending on whether hotfix is 0
        string expectedServerVersion = $"{Server.MAJOR_VERSION}.{Server.MINOR_VERSION}.{Server.MICRO_VERSION}";
        Assert.Equal(expectedServerVersion, version);
    }
}
