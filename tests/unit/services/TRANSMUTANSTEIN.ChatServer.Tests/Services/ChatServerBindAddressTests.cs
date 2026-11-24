// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TRANSMUTANSTEIN.ChatServer.Services;
using ChatServerCore = TRANSMUTANSTEIN.ChatServer.Domain.Core.ChatServer;
using Log = TRANSMUTANSTEIN.ChatServer.Utilities.Log;
using Xunit;

namespace TRANSMUTANSTEIN.ChatServer.Tests.Services;

public class ChatServerBindAddressTests
{
    private static IServiceProvider CreateServiceProvider(IConfiguration? configuration = null)
    {
        ServiceCollection services = new();
        Mock<IHostEnvironment> mockEnv = new();
        mockEnv.SetupGet(e => e.EnvironmentName).Returns("Development");
        services.AddSingleton(mockEnv.Object);
        services.AddSingleton<ILogger<Log>>(NullLogger<Log>.Instance);
        services.AddSingleton<ILogger<ChatService>>(NullLogger<ChatService>.Instance);

        IConfiguration config = configuration ?? new ConfigurationBuilder().Build();
        services.AddSingleton(config);

        return services.BuildServiceProvider();
    }

    [Fact]
    public void ChatServer_SixParameterConstructor_BindsClientAndInternalAddressesSeparately()
    {
        // Arrange
        IServiceProvider provider = CreateServiceProvider();
        IPAddress clientAddress = IPAddress.Parse("0.0.0.0");
        IPAddress internalAddress = IPAddress.Parse("127.0.0.1");

        // Act
        ChatServerCore chatServer = new(provider, clientAddress, internalAddress, 11031, 11032, 11033);

        try
        {
            // Assert
            chatServer.ClientServer.Address.Should().Be("0.0.0.0");
            ((IPEndPoint)chatServer.ClientServer.Endpoint).Address.Should().Be(clientAddress);
            chatServer.ClientServer.Port.Should().Be(11031);

            chatServer.MatchServer.Address.Should().Be("127.0.0.1");
            ((IPEndPoint)chatServer.MatchServer.Endpoint).Address.Should().Be(internalAddress);
            chatServer.MatchServer.Port.Should().Be(11032);

            chatServer.MatchServerManagerServer.Address.Should().Be("127.0.0.1");
            ((IPEndPoint)chatServer.MatchServerManagerServer.Endpoint).Address.Should().Be(internalAddress);
            chatServer.MatchServerManagerServer.Port.Should().Be(11033);
        }
        finally
        {
            chatServer.Dispose();
        }
    }

    [Fact]
    public void ChatServer_FiveParameterConstructor_ForwardsToSixParameterConstructorWithSameAddress()
    {
        // Arrange
        IServiceProvider provider = CreateServiceProvider();
        IPAddress address = IPAddress.Parse("192.168.1.50");

        // Act
        ChatServerCore chatServer = new(provider, address, 11031, 11032, 11033);

        try
        {
            // Assert
            chatServer.ClientServer.Address.Should().Be("192.168.1.50");
            ((IPEndPoint)chatServer.ClientServer.Endpoint).Address.Should().Be(address);

            chatServer.MatchServer.Address.Should().Be("192.168.1.50");
            ((IPEndPoint)chatServer.MatchServer.Endpoint).Address.Should().Be(address);

            chatServer.MatchServerManagerServer.Address.Should().Be("192.168.1.50");
            ((IPEndPoint)chatServer.MatchServerManagerServer.Endpoint).Address.Should().Be(address);
        }
        finally
        {
            chatServer.Dispose();
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveBindAddress_WhenNullOrWhitespace_DefaultsToAny(string? input)
    {
        IPAddress result = ChatService.ResolveBindAddress(input, "TEST_KEY");
        result.Should().Be(IPAddress.Any);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("192.168.1.200")]
    [InlineData("10.0.0.1")]
    public void ResolveBindAddress_WhenValidIPv4_ParsesCorrectly(string input)
    {
        IPAddress result = ChatService.ResolveBindAddress(input, "TEST_KEY");
        result.Should().Be(IPAddress.Parse(input));
    }

    [Theory]
    [InlineData("invalid-ip")]
    [InlineData("256.1.1.1")]
    [InlineData("http://127.0.0.1")]
    [InlineData("127.0.0.1:11031")]
    public void ResolveBindAddress_WhenInvalidAddress_LogsAndFallsBackToAny(string input)
    {
        IPAddress result = ChatService.ResolveBindAddress(input, "TEST_KEY");
        result.Should().Be(IPAddress.Any);
    }

    [Fact]
    public async Task ChatService_StartAsync_ConfiguresPerListenerBindAddresses()
    {
        // Arrange
        Dictionary<string, string?> inMemorySettings = new()
        {
            ["IS_TEST_ENVIRONMENT"] = "true",
            ["CHAT_SERVER_CLIENT_BIND_ADDRESS"] = "0.0.0.0",
            ["CHAT_SERVER_INTERNAL_BIND_ADDRESS"] = "127.0.0.1",
            ["CHAT_SERVER_PORT_CLIENT"] = "0",
            ["CHAT_SERVER_PORT_MATCH_SERVER"] = "0",
            ["CHAT_SERVER_PORT_MATCH_SERVER_MANAGER"] = "0"
        };
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        IServiceProvider provider = CreateServiceProvider(config);
        using ChatService service = new(provider);

        // Act
        await service.StartAsync(CancellationToken.None);

        try
        {
            // Assert
            service.ChatServer.Should().NotBeNull();
            service.ChatServer!.ClientServer.Address.Should().Be("0.0.0.0");
            service.ChatServer.MatchServer.Address.Should().Be("127.0.0.1");
            service.ChatServer.MatchServerManagerServer.Address.Should().Be("127.0.0.1");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ChatService_StartAsync_WhenBindAddressesInvalid_FallsBackToAny()
    {
        // Arrange
        Dictionary<string, string?> inMemorySettings = new()
        {
            ["IS_TEST_ENVIRONMENT"] = "true",
            ["CHAT_SERVER_CLIENT_BIND_ADDRESS"] = "not_an_ip_client",
            ["CHAT_SERVER_INTERNAL_BIND_ADDRESS"] = "not_an_ip_internal",
            ["CHAT_SERVER_PORT_CLIENT"] = "0",
            ["CHAT_SERVER_PORT_MATCH_SERVER"] = "0",
            ["CHAT_SERVER_PORT_MATCH_SERVER_MANAGER"] = "0"
        };
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        IServiceProvider provider = CreateServiceProvider(config);
        using ChatService service = new(provider);

        // Act
        await service.StartAsync(CancellationToken.None);

        try
        {
            // Assert
            service.ChatServer.Should().NotBeNull();
            service.ChatServer!.ClientServer.Address.Should().Be(IPAddress.Any.ToString());
            service.ChatServer.MatchServer.Address.Should().Be(IPAddress.Any.ToString());
            service.ChatServer.MatchServerManagerServer.Address.Should().Be(IPAddress.Any.ToString());
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }
}
