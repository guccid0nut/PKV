// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;

namespace ASPIRE.Tests.Performance;

public class WaitBenchmark
{
    private const int Iterations = 1_000_000;

    public static void Main()
    {
        Console.WriteLine($"Running benchmark with {Iterations} iterations...");

        Task<int> completedTask = Task.FromResult(42);

        // Warmup
        for (int i = 0; i < 100_000; i++)
        {
            int r = completedTask.Result;
            int a = completedTask.GetAwaiter().GetResult();
            bool w = completedTask.Wait(0);
            bool c = completedTask.IsCompleted;
        }

        // Baseline: task.Result
        Stopwatch sw = Stopwatch.StartNew();
        long sum = 0;
        for (int i = 0; i < Iterations; i++) sum += completedTask.Result;

        sw.Stop();
        Console.WriteLine($"Baseline (task.Result): {sw.ElapsedMilliseconds}ms (Sum: {sum})");

        // Optimized: task.GetAwaiter().GetResult()
        sw.Restart();
        sum = 0;
        for (int i = 0; i < Iterations; i++) sum += completedTask.GetAwaiter().GetResult();

        sw.Stop();
        Console.WriteLine($"Optimized (GetAwaiter().GetResult()): {sw.ElapsedMilliseconds}ms (Sum: {sum})");

        // TryWait Baseline: task.Wait(0)
        sw.Restart();
        int count = 0;
        for (int i = 0; i < Iterations; i++)
            if (completedTask.Wait(0))
                count++;

        sw.Stop();
        Console.WriteLine($"TryWait Baseline (task.Wait(0)): {sw.ElapsedMilliseconds}ms (Count: {count})");

        // TryWait Optimized: task.IsCompleted
        sw.Restart();
        count = 0;
        for (int i = 0; i < Iterations; i++)
            if (completedTask.IsCompleted)
                count++;

        sw.Stop();
        Console.WriteLine($"TryWait Optimized (task.IsCompleted): {sw.ElapsedMilliseconds}ms (Count: {count})");
    }
}
