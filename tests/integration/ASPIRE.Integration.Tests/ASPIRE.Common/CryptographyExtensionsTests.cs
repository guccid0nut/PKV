// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Extensions.Cryptography;

namespace ASPIRE.Tests.ASPIRE.Common;

public class CryptographyExtensionsTests
{
    [Fact]
    public void CryptographyExtensions_GetDeterministicUInt32Hash_ReturnsConsistentHash()
    {
        // Arrange
        const string input = "test_string_123";

        // Act
        uint hash1 = input.GetDeterministicUInt32Hash();
        uint hash2 = input.GetDeterministicUInt32Hash();
        uint hash3 = "different_string".GetDeterministicUInt32Hash();

        // Assert
        Assert.Equal(hash1, hash2);
        Assert.NotEqual(hash1, hash3);
    }
}
