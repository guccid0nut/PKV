// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using Xunit;

namespace TRANSMUTANSTEIN.ChatServer.Tests.Domain;

public sealed class PipeSocketHandlerTests
{
    private static async Task<(Socket Client, Socket Server)> CreateConnectedSocketPairAsync()
    {
        using Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);

        Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        Task<Socket> acceptTask = listener.AcceptAsync();
        await client.ConnectAsync(listener.LocalEndPoint!);
        Socket server = await acceptTask;

        return (client, server);
    }

    [Fact]
    public async Task Dispose_Synchronous_DoesNotBlockOrDeadlock()
    {
        var (client, server) = await CreateConnectedSocketPairAsync();
        using (client)
        using (server)
        {
            PipeSocketHandler handler = new(client);
            handler.Start();

            // Synchronous dispose must return immediately without blocking on background tasks
            Stopwatch sw = Stopwatch.StartNew();
            handler.Dispose();
            sw.Stop();

            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task DisposeAsync_Asynchronous_CompletesCleanly()
    {
        var (client, server) = await CreateConnectedSocketPairAsync();
        using (client)
        using (server)
        {
            PipeSocketHandler handler = new(client);
            handler.Start();

            Func<Task> act = async () => await handler.DisposeAsync();
            await act.Should().NotThrowAsync();
        }
    }

    [Fact]
    public async Task PipeSocketHandler_DoubleDispose_IsIdempotent()
    {
        var (client, server) = await CreateConnectedSocketPairAsync();
        using (client)
        using (server)
        {
            PipeSocketHandler handler = new(client);
            handler.Start();

            handler.Dispose();
            handler.Dispose(); // second call should be no-op

            Func<Task> act = async () => await handler.DisposeAsync();
            await act.Should().NotThrowAsync();
        }
    }

    [Fact]
    public async Task FlushOutputPipeAsync_MultipleChunks_TransmitsEntirePayloadWithoutTruncation()
    {
        var (client, server) = await CreateConnectedSocketPairAsync();
        using (client)
        using (server)
        {
            PipeSocketHandler handler = new(client);
            handler.Start();

            byte[] payload = new byte[8192];
            Random.Shared.NextBytes(payload);

            await handler.WriteAsync(payload);
            await handler.FlushAsync();

            byte[] received = new byte[payload.Length];
            int totalReceived = 0;
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(3));

            while (totalReceived < payload.Length)
            {
                int read = await server.ReceiveAsync(received.AsMemory(totalReceived, payload.Length - totalReceived), SocketFlags.None, cts.Token);
                if (read == 0) break;
                totalReceived += read;
            }

            totalReceived.Should().Be(payload.Length);
            received.Should().BeEquivalentTo(payload);

            await handler.DisposeAsync();
        }
    }
}
