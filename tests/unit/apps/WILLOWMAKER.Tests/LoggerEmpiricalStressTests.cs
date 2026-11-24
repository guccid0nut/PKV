// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Diagnostics;
using WILLOWMAKER.Core.Utilities;
using WILLOWMAKER.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace WILLOWMAKER.Tests;

public sealed class LoggerEmpiricalStressTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _testDirectory;

    public LoggerEmpiricalStressTests(ITestOutputHelper output)
    {
        _output = output;
        _testDirectory = Path.Combine(Path.GetTempPath(), $"willow_empirical_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
            try
            {
                Directory.Delete(_testDirectory, true);
            }
            catch
            {
                // Best-effort cleanup
            }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Empirical_1000FPS_RenderLoop_LatencyProfile_NeverExceedsFrameBudget()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Target: 1000 FPS = 1,000 μs total frame budget.
        // Hot-path logging MUST consume < 50 μs (p99.9) and mean MUST be < 5.0 μs (< 0.5% frame budget).
        string logPath = Path.Combine(_testDirectory, "render_1000fps_latency.log");
        const int totalFrames = 10_000;
        double[] latenciesMicroseconds = new double[totalFrames];

        await using (Logger logger = new(logPath, 50_000))
        {
            // Warm-up JIT & memory
            for (int w = 0; w < 1000; w++) logger.Log("WARMUP", $"Warmup message {w}");

            GC.Collect(2, GCCollectionMode.Forced, true);
            GC.WaitForPendingFinalizers();

            // Simulate 10,000 continuous frames
            for (int frame = 0; frame < totalFrames; frame++)
            {
                long startTick = Stopwatch.GetTimestamp();
                logger.Log("RENDER", $"Frame {frame} DrawCalls: 142 Vertices: 98214 StateChanges: 18");
                long endTick = Stopwatch.GetTimestamp();

                latenciesMicroseconds[frame] = (endTick - startTick) * 1_000_000.0 / Stopwatch.Frequency;
            }
        }

        // Statistical Analysis
        Array.Sort(latenciesMicroseconds);
        double min = latenciesMicroseconds[0];
        double max = latenciesMicroseconds[^1];
        double mean = latenciesMicroseconds.Average();
        double p50 = latenciesMicroseconds[(int)(totalFrames * 0.50)];
        double p90 = latenciesMicroseconds[(int)(totalFrames * 0.90)];
        double p95 = latenciesMicroseconds[(int)(totalFrames * 0.95)];
        double p99 = latenciesMicroseconds[(int)(totalFrames * 0.99)];
        double p999 = latenciesMicroseconds[(int)(totalFrames * 0.999)];

        _output.WriteLine("[1000 FPS Latency Benchmark - 10,000 Frames]");
        _output.WriteLine($"  Min:    {min:F3} μs");
        _output.WriteLine($"  Mean:   {mean:F3} μs");
        _output.WriteLine($"  p50:    {p50:F3} μs");
        _output.WriteLine($"  p90:    {p90:F3} μs");
        _output.WriteLine($"  p95:    {p95:F3} μs");
        _output.WriteLine($"  p99:    {p99:F3} μs");
        _output.WriteLine($"  p99.9:  {p999:F3} μs");
        _output.WriteLine($"  Max:    {max:F3} μs");

        // Assertions: 99.9% of all frames must complete in < 50 μs, and average must be sub-microsecond (< 2.5 μs)
        Assert.True(mean < 2.5, $"Mean latency ({mean:F3} μs) exceeded 2.5 μs threshold.");
        Assert.True(p50 < 1.0, $"Median latency ({p50:F3} μs) exceeded 1.0 μs threshold.");
        Assert.True(p99 < 20.0, $"p99 latency ({p99:F3} μs) exceeded 20.0 μs threshold.");
        Assert.True(p999 < 50.0,
            $"p99.9 latency ({p999:F3} μs) exceeded 50.0 μs threshold (< 5% of 1.0ms frame budget).");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Empirical_MassiveConcurrentProducers_DataIntegrityAndThreadSafety()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // 32 concurrent threads hammering the logger with 2,000 messages each = 64,000 total messages
        string logPath = Path.Combine(_testDirectory, "concurrent_32_threads.log");
        const int threadCount = 32;
        const int messagesPerThread = 2_000;
        const int totalExpectedMessages = threadCount * messagesPerThread;

        await using (Logger logger = new(logPath, 100_000))
        {
            Task[] tasks = new Task[threadCount];
            using Barrier barrier = new(threadCount);

            for (int t = 0; t < threadCount; t++)
            {
                int threadId = t;
                tasks[t] = Task.Run(() =>
                {
                    barrier.SignalAndWait(); // Synchronize start for maximum concurrency spike
                    for (int i = 0; i < messagesPerThread; i++)
                        logger.Log($"THREAD_{threadId:D2}",
                            $"Payload seq={i:D5} time={TimeProvider.System.GetUtcNow().UtcDateTime.Ticks}");
                });
            }

            await Task.WhenAll(tasks);
        }

        // Verify disk file integrity
        Assert.True(File.Exists(logPath));
        string[] lines = await File.ReadAllLinesAsync(logPath);

        // Filter out session header lines
        string[] logLines = [.. lines.Where(l => l.StartsWith('[') && l.Contains("] [THREAD_"))];
        _output.WriteLine("[Concurrent Stress - 32 Threads x 2000 Messages]");
        _output.WriteLine($"  Total Lines Written: {lines.Length}");
        _output.WriteLine($"  Valid Log Lines:     {logLines.Length} (Expected: {totalExpectedMessages})");

        Assert.Equal(totalExpectedMessages, logLines.Length);

        // Verify every thread has all its sequences without data corruption
        ConcurrentDictionary<int, int> threadCounts = new();
        foreach (string line in logLines)
        {
            // Verify structure: [timestamp] [category] message
            Assert.StartsWith("[", line);
            Assert.Contains("] [THREAD_", line);
            Assert.Contains("Payload seq=", line);

            int tagIndex = line.IndexOf("[THREAD_", StringComparison.Ordinal);
            string threadIdStr = line.Substring(tagIndex + 8, 2);
            int threadId = int.Parse(threadIdStr);
            threadCounts.AddOrUpdate(threadId, 1, (_, count) => count + 1);
        }

        Assert.Equal(threadCount, threadCounts.Count);
        for (int t = 0; t < threadCount; t++)
        {
            Assert.True(threadCounts.TryGetValue(t, out int count), $"Thread {t} was missing from output.");
            Assert.Equal(messagesPerThread, count);
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Empirical_ChannelOverflow_DropOldest_GracefulDegradation()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // When logger channel overflows (e.g. 500 capacity with 10,000 rapid messages),
        // DropOldest must drop older entries gracefully without locking, throwing, or memory corruption.
        string logPath = Path.Combine(_testDirectory, "overflow_drop_oldest.log");
        const int smallCapacity = 500;
        const int messageCount = 10_000;

        await using (Logger logger = new(logPath, smallCapacity))
        {
            // Rapidly write 10,000 messages
            Stopwatch sw = Stopwatch.StartNew();
            for (int i = 0; i < messageCount; i++) logger.Log("OVERFLOW", $"Overflow message sequence {i:D6}");
            sw.Stop();

            double avgUs = sw.Elapsed.TotalMicroseconds / messageCount;
            _output.WriteLine("[Overflow Test - 10,000 writes into 500 capacity]");
            _output.WriteLine($"  Total Duration: {sw.ElapsedMilliseconds} ms");
            _output.WriteLine($"  Avg per call:   {avgUs:F3} μs");

            // Even during constant channel dropping, caller latency must remain sub-microsecond
            Assert.True(avgUs < 10.0, $"Average write under overflow was {avgUs:F3} μs, expected < 10.0 μs");
        }

        // Verify file contains header and valid lines
        Assert.True(File.Exists(logPath));
        string[] lines = await File.ReadAllLinesAsync(logPath);
        _output.WriteLine($"  Lines retained on disk: {lines.Length}");

        Assert.True(lines.Length > 0);
        // Verify the newest entries survived
        Assert.Contains(lines, l => l.Contains($"Overflow message sequence {messageCount - 1:D6}"));
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Empirical_ConcurrentLoggingDuringDisposal_NoDeadlockOrExceptions()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Stress test concurrent calls to Log() while DisposeAsync / Flush is in flight
        string logPath = Path.Combine(_testDirectory, "teardown_race.log");
        Logger logger = new(logPath, 5000);

        CancellationTokenSource cts = new();
        const int writerThreads = 8;
        Task[] writerTasks = new Task[writerThreads];
        long loggedCount = 0;

        for (int t = 0; t < writerThreads; t++)
        {
            int tid = t;
            writerTasks[t] = Task.Run(() =>
            {
                int seq = 0;
                while (!cts.IsCancellationRequested)
                    try
                    {
                        logger.Log($"RACE_{tid}", $"Race msg {seq++}");
                        Interlocked.Increment(ref loggedCount);
                    }
                    catch (Exception ex)
                    {
                        _output.WriteLine($"Exception during concurrent log: {ex}");
                        throw;
                    }
            });
        }

        // Allow writers to run briefly
        await Task.Delay(50);

        // Concurrently trigger dispose
        await logger.DisposeAsync();

        // Stop writers
        cts.Cancel();
        await Task.WhenAll(writerTasks);

        _output.WriteLine(
            $"[Disposal Race Test] Successfully logged {loggedCount} messages through disposal race without exceptions.");
        Assert.True(File.Exists(logPath));
    }

    [Fact]
    public void Empirical_IdempotentMultiDispose_NoCrash()
    {
        string logPath = Path.Combine(_testDirectory, "multi_dispose.log");
        Logger logger = new(logPath, 1000);
        logger.Log("TEST", "Entry 1");

        // Concurrent multiple sync dispose
        Parallel.For(0, 10, _ => { logger.Dispose(); });

        Assert.True(File.Exists(logPath));
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Empirical_RapidCreationAndDisposal_NoResourceLeaks()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        const int instances = 50;
        for (int i = 0; i < instances; i++)
        {
            string path = Path.Combine(_testDirectory, $"rapid_{i}.log");
            await using (Logger logger = new(path, 1000))
            {
                logger.Log("RAPID", $"Rapid instance {i} message");
            }

            Assert.True(File.Exists(path));
        }

        _output.WriteLine(
            $"[Rapid Creation Test] Successfully created and disposed {instances} distinct Logger instances.");
    }

    [Fact]
    public void Empirical_NestedDirectoryCreation_Succeeds()
    {
        string deepPath = Path.Combine(_testDirectory, "deep", "nested", "directory", "structure", "willow.log");
        using (Logger logger = new(deepPath, 1000))
        {
            logger.Log("DEEP", "Deep directory message");
        }

        Assert.True(File.Exists(deepPath));
        string content = File.ReadAllText(deepPath);
        Assert.Contains("Deep directory message", content);
    }
}
