// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using KONGOR.MasterServer.Handlers.ClientRequester;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Moq;
using Xunit.Abstractions;

namespace ASPIRE.Tests.KONGOR.MasterServer.Tests;

public sealed class ProductsHandlerPerformanceTests(ITestOutputHelper testOutputHelper)

{
    [Trait("Category", "Manual")]
    [Fact]
    public async Task ProductsHandler_HighLoadExecution_MeasuresPerformanceWithinSla()

    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;


        Mock<IDatabase> mockCache = new();


        ProductsHandler handler = new(mockCache.Object);


        DefaultHttpContext context = new()

        {
            Request =

            {
                Form = new FormCollection(new Dictionary<string, StringValues> { { "cookie", "test-cookie" } })
            },

            Items = { ["SessionAccountName"] = "TestUser" }
        };


        // Warmup

        await handler.HandleRequestAsync(context);


        long startAllocated = GC.GetTotalAllocatedBytes(true);

        Stopwatch sw = Stopwatch.StartNew();

        const int iterations = 100;

        for (int i = 0; i < iterations; i++) await handler.HandleRequestAsync(context);


        sw.Stop();

        long endAllocated = GC.GetTotalAllocatedBytes(true);

        long allocatedBytes = endAllocated - startAllocated;


        testOutputHelper.WriteLine("[ProductsHandler Performance Benchmark]");

        testOutputHelper.WriteLine($"Total time for {iterations} iterations: {sw.ElapsedMilliseconds} ms");

        testOutputHelper.WriteLine($"Average time per request: {sw.Elapsed.TotalMilliseconds / iterations:F4} ms");

        testOutputHelper.WriteLine($"Total Heap Allocations: {allocatedBytes:N0} bytes");

        testOutputHelper.WriteLine($"PRODUCTS_PERF_MS={sw.ElapsedMilliseconds}");
    }
}
