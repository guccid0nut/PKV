// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Threading.Channels;
using COMPEL.Endpoints;
using COMPEL.Services.Supervision;

namespace COMPEL.Tests;

public sealed class DedicatedServerStreamChannelTests
{
    [Fact]
    public void DedicatedServerStreamChannel_DedicatedServerLogEntry_StoresPropertiesCorrectly()
    {
        DedicatedServerLogEntry entry = new(11235, "Sv: [12:00:00] Server ready", false);
        Assert.Equal(11235, entry.Port);
        Assert.Equal("Sv: [12:00:00] Server ready", entry.Text);
        Assert.False(entry.IsError);

        DedicatedServerLogEntry errEntry = new(11235, "Error loading asset", true);
        Assert.True(errEntry.IsError);
    }

    [Fact]
    public void Channel_WhenExecuted_AbsorbsHighThroughputBurstWithoutBlocking()
    {
        Channel<DedicatedServerLogEntry> channel = Channel.CreateBounded<DedicatedServerLogEntry>(
            new BoundedChannelOptions(10_000)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleWriter = false,
                SingleReader = true
            });

        Stopwatch sw = Stopwatch.StartNew();
        const int burstCount = 5_000;

        for (int i = 0; i < burstCount; i++)
        {
            bool written = channel.Writer.TryWrite(new DedicatedServerLogEntry(11235, $"Log line #{i}", false));
            Assert.True(written);
        }
        sw.Stop();

        // 5,000 writes into bounded channel must complete without blocking (< 5000ms under heavy test suite load)
        Assert.True(sw.ElapsedMilliseconds < 5000, $"Burst took {sw.ElapsedMilliseconds}ms, expected < 5000ms");
    }

    [Fact]
    public void Channel_DropOldest_PreservesMostRecentUnderSaturation()
    {
        const int capacity = 20;
        Channel<DedicatedServerLogEntry> channel = Channel.CreateBounded<DedicatedServerLogEntry>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleWriter = true,
                SingleReader = true
            });

        // Write 100 items (0..99) into capacity 20
        for (int i = 0; i < 100; i++)
        {
            channel.Writer.TryWrite(new DedicatedServerLogEntry(11235, $"Line_{i}", false));
        }

        channel.Writer.Complete();

        List<string> remaining = [];
        while (channel.Reader.TryRead(out DedicatedServerLogEntry entry))
        {
            remaining.Add(entry.Text);
        }

        // BoundedChannelFullMode.DropOldest keeps the last `capacity` items
        Assert.Equal(capacity, remaining.Count);
        Assert.Equal("Line_80", remaining[0]);
        Assert.Equal("Line_99", remaining[^1]);
    }

    [Fact]
    public async Task Worker_CleanShutdown_CompletesStreamChannelAndDisposesResources()
    {
        DedicatedServerWorker worker = new(
            instanceIndex: 1,
            port: 11235,
            voicePort: 11435,
            publicGamePort: 21235,
            publicVoicePort: 21435,
            serverName: "Test Worker #1");

        Channel<DedicatedServerLogEntry> channel = Channel.CreateBounded<DedicatedServerLogEntry>(
            new BoundedChannelOptions(100)
            {
                FullMode = BoundedChannelFullMode.DropOldest
            });

        worker.StreamChannel = channel;
        worker.StreamCts = new CancellationTokenSource();

        bool consumerFinished = false;
        Task consumerTask = Task.Run(async () =>
        {
            try
            {
                while (await channel.Reader.WaitToReadAsync(worker.StreamCts.Token))
                {
                    while (channel.Reader.TryRead(out _)) { }
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                consumerFinished = true;
            }
        });
        worker.StreamConsumerTask = consumerTask;

        // Write some entries
        channel.Writer.TryWrite(new DedicatedServerLogEntry(11235, "Startup line", false));

        // Stop the worker gracefully
        worker.Stop(TimeSpan.FromSeconds(2));

        Assert.Null(worker.StreamChannel);
        Assert.Null(worker.StreamConsumerTask);
        Assert.Null(worker.StreamCts);
        await consumerTask.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(consumerFinished);
    }

    [Fact]
    public void Worker_SubscribeLogStream_ReceivesBroadcastLogLines()
    {
        DedicatedServerWorker worker = new(
            instanceIndex: 1,
            port: 11235,
            voicePort: 11435,
            publicGamePort: 21235,
            publicVoicePort: 21435,
            serverName: "Test Worker #1");

        using DedicatedServerWorkerLogSubscription sub = worker.SubscribeLogStream();

        worker.AddLogLine("Server initialized on port 11235", false, "server");
        worker.AddLogLine("Game match started", false, "server");

        Assert.True(sub.Reader.TryRead(out DedicatedServerLogLine? line1));
        Assert.NotNull(line1);
        Assert.Equal("Server initialized on port 11235", line1.Text);
        Assert.False(line1.IsError);
        Assert.Equal("server", line1.Source);

        Assert.True(sub.Reader.TryRead(out DedicatedServerLogLine? line2));
        Assert.NotNull(line2);
        Assert.Equal("Game match started", line2.Text);
    }

    [Fact]
    public void Worker_SubscribeLogStream_MultipleSubscribers_AllReceiveBroadcast()
    {
        DedicatedServerWorker worker = new(
            instanceIndex: 1,
            port: 11235,
            voicePort: 11435,
            publicGamePort: 21235,
            publicVoicePort: 21435,
            serverName: "Test Worker #1");

        using DedicatedServerWorkerLogSubscription sub1 = worker.SubscribeLogStream();
        using DedicatedServerWorkerLogSubscription sub2 = worker.SubscribeLogStream();
        using DedicatedServerWorkerLogSubscription sub3 = worker.SubscribeLogStream();

        const int messageCount = 5;
        for (int i = 0; i < messageCount; i++)
        {
            worker.AddLogLine($"Broadcast line #{i}", false, "server");
        }

        for (int i = 0; i < messageCount; i++)
        {
            Assert.True(sub1.Reader.TryRead(out DedicatedServerLogLine? l1));
            Assert.Equal($"Broadcast line #{i}", l1?.Text);

            Assert.True(sub2.Reader.TryRead(out DedicatedServerLogLine? l2));
            Assert.Equal($"Broadcast line #{i}", l2?.Text);

            Assert.True(sub3.Reader.TryRead(out DedicatedServerLogLine? l3));
            Assert.Equal($"Broadcast line #{i}", l3?.Text);
        }
    }

    [Fact]
    public void Worker_SubscribeLogStream_DisposeRemovesSubscription()
    {
        DedicatedServerWorker worker = new(
            instanceIndex: 1,
            port: 11235,
            voicePort: 11435,
            publicGamePort: 21235,
            publicVoicePort: 21435,
            serverName: "Test Worker #1");

        DedicatedServerWorkerLogSubscription sub = worker.SubscribeLogStream();
        sub.Dispose();

        // Emitting after dispose should not throw
        worker.AddLogLine("Post-dispose line", false, "server");

        // The disposed channel writer is completed
        Assert.False(sub.Reader.TryRead(out _));
    }

    [Fact]
    public void Worker_AddLogLine_EnforcesBounded500LineDropOldestRing()
    {
        DedicatedServerWorker worker = new(
            instanceIndex: 1,
            port: 11235,
            voicePort: 11435,
            publicGamePort: 21235,
            publicVoicePort: 21435,
            serverName: "Test Worker #1");

        for (int i = 0; i < 650; i++)
        {
            worker.AddLogLine($"Log_{i}", isError: false, source: "server");
        }

        IReadOnlyList<DedicatedServerLogLine> all500 = worker.GetRecentLogs(1000);
        Assert.Equal(500, all500.Count);
        Assert.Equal("Log_150", all500[0].Text);
        Assert.Equal("Log_649", all500[^1].Text);

        IReadOnlyList<DedicatedServerLogLine> tail10 = worker.GetRecentLogs(10);
        Assert.Equal(10, tail10.Count);
        Assert.Equal("Log_640", tail10[0].Text);
        Assert.Equal("Log_649", tail10[^1].Text);

        worker.ClearLogHistory();
        Assert.Empty(worker.GetRecentLogs(100));
    }
}
