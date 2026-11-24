// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using Xunit;
using Xunit.Abstractions;

namespace Kongor.Mcp.E2E.Tests.Client;

public class McpTestBase : IAsyncLifetime
{
    protected readonly ITestOutputHelper Output;
    protected McpProcessClient Client { get; private set; } = null!;

    public McpTestBase(ITestOutputHelper output)
    {
        Output = output;
    }

    public virtual async Task InitializeAsync()
    {
        Client = new McpProcessClient(msg => Output.WriteLine(msg));
        await Client.StartAsync();
        await Client.InitializeAsync();
    }

    public virtual async Task DisposeAsync()
    {
        if (Client != null)
        {
            await Client.DisposeAsync();
        }
    }
}
