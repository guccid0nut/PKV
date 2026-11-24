// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Tooling.Lua;
using FluentAssertions;
using NLua;
using Xunit;

namespace Kongor.Core.Tests.Tooling;

public class LuaStatePoolTests
{
    [Fact]
    public void GetLease_ReturnsNonNullState_AndExecutesBasicLua()
    {
        // Arrange
        using LuaStatePool pool = new(new LuaPoolOptions { MaximumRetained = 4, PrewarmCount = 2 });

        // Act
        using PooledLuaLease lease = pool.GetLease();
        object[] result = lease.Lua.DoString("return 10 + 25");

        // Assert
        result.Should().NotBeNull();
        result.Should().ContainSingle();
        Convert.ToInt32(result[0]).Should().Be(35);
    }

    [Fact]
    public void Return_SanitizesGlobalTables_PreventsStateContamination()
    {
        // Arrange
        using LuaStatePool pool = new(new LuaPoolOptions { MaximumRetained = 1, PrewarmCount = 1 });

        // Act 1: Lease 1 mutates test tables
        using (PooledLuaLease lease1 = pool.GetLease())
        {
            lease1.Lua.DoString(@"
                _matchList = { { id = 'leaked_match_id' } }
                _seasonStatsNormal = { cam_wins = 999 }
                base64Payload = 'leaked_payload'
            ");
            object[] test1 = lease1.Lua.DoString("return _matchList[1].id");
            test1[0].ToString().Should().Be("leaked_match_id");
        } // Returned to pool, triggering sanitization policy

        // Act 2: Lease 2 (recycled state) must have clean globals
        using (PooledLuaLease lease2 = pool.GetLease())
        {
            object[] checkMatchList = lease2.Lua.DoString("return _matchList[1] == nil and 1 or 0");
            Convert.ToInt32(checkMatchList[0]).Should().Be(1, "Recycled Lua state must have cleared _matchList table");

            object[] checkPayload = lease2.Lua.DoString("return base64Payload == nil and 1 or 0");
            Convert.ToInt32(checkPayload[0]).Should().Be(1, "Recycled Lua state must have cleared base64Payload");
        }
    }

    [Fact]
    public void ConcurrentLeases_ExecuteInParallel_WithoutCorruption()
    {
        // Arrange
        using LuaStatePool pool = new(new LuaPoolOptions
        {
            MaximumRetained = 8,
            PrewarmCount = 4,
            AutoCollectGarbageOnReturn = true
        });

        const int iterations = 50;
        List<Exception> exceptions = [];
        object lockObj = new();

        // Act
        Parallel.For(0, iterations, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            try
            {
                using PooledLuaLease lease = pool.GetLease();
                lease.Lua["thread_idx"] = i;
                object[] res = lease.Lua.DoString($"return (thread_idx * 2) + {i}");
                int expected = (i * 2) + i;
                int actual = Convert.ToInt32(res[0]);
                if (actual != expected)
                {
                    lock (lockObj) exceptions.Add(new InvalidOperationException($"Mismatch: expected {expected}, got {actual}"));
                }
            }
            catch (Exception ex)
            {
                lock (lockObj) exceptions.Add(ex);
            }
        });

        // Assert
        exceptions.Should().BeEmpty("Concurrent executions across pooled Lua states must not corrupt stack or memory");
    }

    [Fact]
    public void Execute_HelperMethod_ReturnsExpectedResult()
    {
        // Arrange
        using LuaStatePool pool = new(new LuaPoolOptions { MaximumRetained = 2 });

        // Act
        string result = pool.Execute(lua =>
        {
            object[] res = lua.DoString("return string.upper('kongor_tooling')");
            return res[0].ToString()!;
        });

        // Assert
        result.Should().Be("KONGOR_TOOLING");
    }

    [Fact]
    public async Task ExecuteAsync_HelperMethod_ReturnsExpectedResult()
    {
        // Arrange
        using LuaStatePool pool = new(new LuaPoolOptions { MaximumRetained = 2 });

        // Act
        int result = await pool.ExecuteAsync(async lua =>
        {
            await Task.Yield();
            object[] res = lua.DoString("return 7 * 7");
            return Convert.ToInt32(res[0]);
        });

        // Assert
        result.Should().Be(49);
    }

    [Fact]
    public void LuaStatePool_ActiveLeaseCount_AccuratelyTracksCheckoutsAndReturns()
    {
        // Arrange
        using LuaStatePool pool = new(new LuaPoolOptions { MaximumRetained = 4 });
        pool.ActiveLeaseCount.Should().Be(0);

        // Act & Assert
        using (PooledLuaLease lease1 = pool.GetLease())
        {
            pool.ActiveLeaseCount.Should().Be(1);
            using (PooledLuaLease lease2 = pool.GetLease())
            {
                pool.ActiveLeaseCount.Should().Be(2);
            }
            pool.ActiveLeaseCount.Should().Be(1);
        }
        pool.ActiveLeaseCount.Should().Be(0);
    }
}
