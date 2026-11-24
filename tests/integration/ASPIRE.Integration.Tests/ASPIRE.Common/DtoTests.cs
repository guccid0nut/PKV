// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.Tests.ASPIRE.Common;

public class DtoTests
{
    [Fact]
    public void RegisterDirectUserDTO_Constructor_SetsProperties()
    {
        // Arrange & Act
        RegisterDirectUserDTO dto = new("test@example.com", "Username123", "Password123!");

        // Assert
        Assert.Equal("test@example.com", dto.Email);
        Assert.Equal("Password123!", dto.Password);
        Assert.Equal("Username123", dto.Name);
    }

    [Fact]
    public void RegisterUserWithTokenDTO_Constructor_SetsProperties()
    {
        // Arrange & Act
        RegisterUserWithTokenDTO dto = new("Token123", "Username123", "Password123!");

        // Assert
        Assert.Equal("Token123", dto.Token);
        Assert.Equal("Password123!", dto.Password);
        Assert.Equal("Username123", dto.Name);
    }
}
