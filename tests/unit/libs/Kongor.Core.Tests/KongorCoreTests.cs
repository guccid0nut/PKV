// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using Xunit;

namespace Kongor.Core.Tests;

public sealed class KongorCoreTests
{
    [Fact]
    public void KongorCoreAssembly_WhenLoaded_ShouldLoadSuccessfully()
    {
        typeof(KongorCoreTests).Assembly.Should().NotBeNull();
    }
}
