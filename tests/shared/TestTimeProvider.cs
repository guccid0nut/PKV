// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: TestTimeProvider.cs
// System: ProjectKongor - Shared Test Infrastructure
// Policy: Strict Explicit Typing (NO 'var') - Deterministic Clock Manipulation
// ============================================================================

using System;

namespace ASPIRE.Tests.Infrastructure;

/// <summary>
///     Deterministic, controllable TimeProvider implementation for sub-second automated test runs.
/// </summary>
public sealed class TestTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    public TestTimeProvider(DateTimeOffset initialUtcNow)
    {
        _utcNow = initialUtcNow;
    }

    public TestTimeProvider() : this(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero))
    {
    }

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan delta)
    {
        _utcNow = _utcNow.Add(delta);
    }

    public void SetUtcNow(DateTimeOffset utcNow)
    {
        _utcNow = utcNow;
    }
}
