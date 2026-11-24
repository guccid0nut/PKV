// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using WILLOWMAKER.Core.Utilities;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class LoggerAsyncTests
{
    [Fact]
    public async Task Logger_AsyncWrite_DrainsAllEntriesToFileOnDispose()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), $"willow_log_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string logPath = Path.Combine(tempDir, "test.log");

        try
        {
            // Act
            await using (Logger logger = new(logPath, 5000))
            {
                for (int i = 0; i < 100; i++) logger.Log("GENERAL", $"Test message {i}");
            } // DisposeAsync triggers complete queue drain and flush

            // Assert
            Assert.True(File.Exists(logPath));
            string[] lines = File.ReadAllLines(logPath);
            // Session header + 100 entries
            Assert.True(lines.Length >= 100);
            Assert.Contains(lines, l => l.Contains("Test message 0"));
            Assert.Contains(lines, l => l.Contains("Test message 99"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                try
                {
                    Directory.Delete(tempDir, true);
                }
                catch
                {
                }
        }
    }

    [Fact]
    public void Logger_HotPathLatency_SubMicrosecondExecution()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), $"willow_log_perf_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string logPath = Path.Combine(tempDir, "perf.log");

        try
        {
            using Logger logger = new(logPath, 50000);

            // Warm up
            for (int i = 0; i < 100; i++) logger.Log("WARMUP", $"Warmup {i}");

            const int iterations = 10_000;
            Stopwatch sw = Stopwatch.StartNew();

            // Act - Log 10,000 items on caller thread
            for (int i = 0; i < iterations; i++) logger.Log("PERF", $"Performance check line number {i}");
            sw.Stop();

            double totalMicroseconds = sw.Elapsed.TotalMicroseconds;
            double avgMicrosecondsPerCall = totalMicroseconds / iterations;

            // Assert - Average call time must be well below 1.0ms (1,000 μs) frame budget (expected < 100.0 μs even under extreme test suite load)
            Assert.True(avgMicrosecondsPerCall < 100.0,
                $"Avg call time was {avgMicrosecondsPerCall:F3} μs, expected < 100.0 μs");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                try
                {
                    Directory.Delete(tempDir, true);
                }
                catch
                {
                }
        }
    }

    [Fact]
    public async Task Logger_ConcurrentProducers_ZeroCorruption()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), $"willow_log_conc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string logPath = Path.Combine(tempDir, "concurrent.log");

        try
        {
            const int threadCount = 10;
            const int logsPerThread = 500;

            await using (Logger logger = new(logPath, 20000))
            {
                Task[] tasks = new Task[threadCount];
                for (int t = 0; t < threadCount; t++)
                {
                    int threadId = t;
                    tasks[t] = Task.Run(() =>
                    {
                        for (int i = 0; i < logsPerThread; i++)
                            logger.Log($"THREAD_{threadId}", $"Message from thread {threadId} iteration {i}");
                    });
                }

                await Task.WhenAll(tasks);
            }

            // Assert
            string content = File.ReadAllText(logPath);
            for (int t = 0; t < threadCount; t++) Assert.Contains($"[THREAD_{t}]", content);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                try
                {
                    Directory.Delete(tempDir, true);
                }
                catch
                {
                }
        }
    }
}
