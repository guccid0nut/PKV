// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using ASPIRE.Common.Serialization.PHP;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace ASPIRE.Load.Tests;

public class PhpResultStreamingLoadTests
{
    private sealed class MockPayloadDto : IPhpSerializable
    {
        public int Id { get; set; } = 12345;
        public string Name { get; set; } = "PlayerOne";
        public double Rating { get; set; } = 1750.55;
        public int Matches { get; set; } = 450;
        public int Wins { get; set; } = 280;
        public string GuildTag { get; set; } = "[HON]";
        public string Status { get; set; } = "InLobby";

        public void SerializePhp(ref Utf8PhpWriter writer)
        {
            writer.WriteArrayHeader(7);
            writer.WriteKey("id");
            writer.WriteInt(Id);
            writer.WriteKey("name");
            writer.WriteString(Name);
            writer.WriteKey("rating");
            writer.WriteDouble(Rating);
            writer.WriteKey("matches");
            writer.WriteInt(Matches);
            writer.WriteKey("wins");
            writer.WriteInt(Wins);
            writer.WriteKey("tag");
            writer.WriteString(GuildTag);
            writer.WriteKey("status");
            writer.WriteString(Status);
            writer.WriteArrayFooter();
        }
    }

    [Fact]
    public async Task PhpResult_ConcurrentStreamingLoad_ZeroLohAllocationsAndLowLatency()
    {
        const int concurrentRequests = 1_000;
        MockPayloadDto testPayload = new();

        // 1. Pre-warm JIT compilation, static type caches, ArrayPool buckets, and HttpContext infrastructure
        {
            DefaultHttpContext warmContext = new();
            warmContext.Response.Body = new MemoryStream();
            PhpResult warmResult = new(testPayload);
            await warmResult.ExecuteAsync(warmContext);
        }

        // 2. Force full clean garbage collection to establish a quiescent baseline before measurement
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect();

        int initialGen2Count = GC.CollectionCount(2);
        long initialLohSize = GC.GetGCMemoryInfo().GenerationInfo[3].SizeAfterBytes;

        Stopwatch sw = Stopwatch.StartNew();

        Task[] tasks = new Task[concurrentRequests];
        for (int i = 0; i < concurrentRequests; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                DefaultHttpContext context = new();
                context.Response.Body = new MemoryStream();

                PhpResult result = new(testPayload);
                await result.ExecuteAsync(context);

                context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
                context.Response.ContentLength.Should().BeGreaterThan(0);
                context.Response.ContentType.Should().Be("text/plain; charset=utf-8");

                context.Response.Body.Length.Should().Be(context.Response.ContentLength!.Value);
            });
        }

        await Task.WhenAll(tasks);
        sw.Stop();

        int finalGen2Count = GC.CollectionCount(2);
        int gen2Collections = finalGen2Count - initialGen2Count;

        // Hardened assertion: allow at most 1 Gen 2 collection due to process-wide ThreadPool/HttpContext allocation pressure across 1,000 parallel tasks
        gen2Collections.Should().BeLessThanOrEqualTo(1, "Zero-copy pooled serialization minimizes GC pressure; at most 1 background GC collection may occur from ThreadPool context allocations across 1,000 concurrent tasks");

        // Verify Large Object Heap (LOH) size did not expand (zero LOH allocations)
        long finalLohSize = GC.GetGCMemoryInfo().GenerationInfo[3].SizeAfterBytes;
        finalLohSize.Should().BeLessThanOrEqualTo(initialLohSize, "Zero-copy pooled serialization must produce zero Large Object Heap (LOH) expansions");

        sw.ElapsedMilliseconds.Should().BeLessThan(3000, "1,000 concurrent PhpResult serializations must complete in under 3 seconds");
    }

    [Fact]
    public async Task PhpResult_StringPayload_StreamsWithExactContentLength()
    {
        DefaultHttpContext context = new();
        context.Response.Body = new MemoryStream();

        string sampleResponse = "a:2:{s:6:\"status\";s:7:\"success\";s:4:\"code\";i:200;}";
        PhpResult result = new(sampleResponse);
        await result.ExecuteAsync(context);

        context.Response.ContentLength.Should().Be(sampleResponse.Length);
        context.Response.Body.Position = 0;
        using StreamReader reader = new(context.Response.Body);
        string writtenText = await reader.ReadToEndAsync();
        writtenText.Should().Be(sampleResponse);
    }
}
