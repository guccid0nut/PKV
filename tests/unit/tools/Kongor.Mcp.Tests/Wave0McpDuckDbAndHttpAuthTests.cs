// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Net;
using System.Net.Sockets;
using DuckDB.NET.Data;
using FluentAssertions;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace Kongor.Mcp.Tests;

[Collection("McpHttpAuthEnvCollection")]
public sealed class Wave0McpDuckDbAndHttpAuthTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _validParquetPath;
    private readonly string _secretFilePath;
    private readonly DuckDbMatchAnalyticsService _analytics;

    public Wave0McpDuckDbAndHttpAuthTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"kongor_mcp_w0_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _validParquetPath = Path.Combine(_tempDir, "match_9001.parquet");
        _secretFilePath = Path.Combine(_tempDir, "host_secret.txt");
        File.WriteAllText(_secretFilePath, "TOP_SECRET_CREDENTIAL_DO_NOT_EXFILTRATE");

        using (DuckDBConnection conn = new("Data Source=:memory:"))
        {
            conn.Open();
            using DuckDBCommand cmd = conn.CreateCommand();
            string norm = _validParquetPath.Replace('\\', '/');
            cmd.CommandText = $"COPY (SELECT 9001::BIGINT AS match_id, 1::TINYINT AS event_type, 500.0::FLOAT AS damage) TO '{norm}' (FORMAT PARQUET);";
            cmd.ExecuteNonQuery();
        }

        _analytics = new DuckDbMatchAnalyticsService(_ => { });
    }

    [Theory]
    [InlineData("SELECT * FROM read_text('{SECRET}')")]
    [InlineData("SELECT * FROM read_csv_auto('{SECRET}')")]
    [InlineData("SELECT * FROM glob('*')")]
    [InlineData("INSTALL httpfs; LOAD httpfs; SELECT 1")]
    [InlineData("SET enable_external_access = true; SELECT 1")]
    [InlineData("PRAGMA database_list")]
    [InlineData("COPY (SELECT 1) TO 'out.parquet'")]
    public async Task DuckDbMatchAnalytics_BlocksArbitraryFileReadExtensionAndMultiStatementQueries(string sqlTemplate)
    {
        // protects: TAO MCP DuckDB analytics engine against arbitrary local file read / SSRF / extension loading via SQL injection.
        // fails_when: User SQL can invoke read_text/read_csv/glob, SET enable_external_access, PRAGMA, INSTALL/LOAD, or multi-statement chains.
        // seam: DuckDbMatchAnalyticsService.ExecuteQueryAsync + EnsureViewRegisteredAsync (TEMP TABLE materialization + external access lock).

        string sql = sqlTemplate.Replace("{SECRET}", _secretFilePath.Replace('\\', '/'));
        MatchAnalyticsQueryResult res = await _analytics.ExecuteQueryAsync(sql, _validParquetPath);

        res.Success.Should().BeFalse();
        res.Error.Should().Contain("Security Violation");

        // Legitimate analytical query over materialized match_combat_events still succeeds!
        MatchAnalyticsQueryResult validRes = await _analytics.ExecuteQueryAsync(
            "SELECT match_id, SUM(damage) AS total_dmg FROM match_combat_events GROUP BY match_id",
            _validParquetPath);
        validRes.Success.Should().BeTrue();
        validRes.RowCount.Should().Be(1);
    }

    [Fact]
    public async Task DuckDbMatchAnalytics_RejectsNonParquetFilePath()
    {
        // protects: EnsureViewRegisteredAsync path extension gate so non-.parquet files cannot be passed into read_parquet.
        // fails_when: Non-.parquet path is accepted instead of failing closed.
        // seam: DuckDbMatchAnalyticsService.ExecuteQueryAsync -> EnsureViewRegisteredAsync.

        MatchAnalyticsQueryResult res = await _analytics.ExecuteQueryAsync("SELECT 1", _secretFilePath);
        res.Success.Should().BeFalse();
        res.Error.Should().Contain(".parquet");
    }

    [Fact]
    public void OfficialMcpHttpHost_BuildHttpApp_ThrowsWhenRequireAuthTrueAndTokenMissing()
    {
        // protects: MCP HTTP server fail-closed authentication contract when requireAuth=true.
        // fails_when: BuildHttpApp silently disables authentication when requiredBearerToken and KONGOR_MCP_AUTH_TOKEN are both unset.
        // seam: OfficialMcpHttpHost.BuildHttpApp guard.

        string? prevToken = Environment.GetEnvironmentVariable("KONGOR_MCP_AUTH_TOKEN");
        string? prevIssuer = Environment.GetEnvironmentVariable("KONGOR_MCP_OAUTH_ISSUER");
        try
        {
            Environment.SetEnvironmentVariable("KONGOR_MCP_AUTH_TOKEN", null);
            Environment.SetEnvironmentVariable("KONGOR_MCP_OAUTH_ISSUER", null);
            McpToolRegistry registry = new(registerDefaults: false);
            using McpServer server = new(registry, TextWriter.Null);

            Action act = () => OfficialMcpHttpHost.BuildHttpApp(
                server,
                host: "127.0.0.1",
                port: 19599,
                requiredBearerToken: null,
                requireAuth: true);

            act.Should().Throw<InvalidOperationException>().WithMessage("*SECURITY-ALERT*");
        }
        finally
        {
            Environment.SetEnvironmentVariable("KONGOR_MCP_AUTH_TOKEN", prevToken);
            Environment.SetEnvironmentVariable("KONGOR_MCP_OAUTH_ISSUER", prevIssuer);
        }
    }

    [Fact]
    public async Task OfficialMcpHttpHost_WhenRemoteHttpEnabled_RequiresBearerTokenOnHealthz()
    {
        // protects: /healthz metadata endpoint from unauthenticated network reconnaissance when KONGOR_MCP_ALLOW_REMOTE_HTTP=1.
        // fails_when: /healthz returns 200 OK without Bearer token while remote HTTP is enabled.
        // seam: OfficialMcpHttpHost middleware + /healthz endpoint.

        int port = GetEphemeralPort();
        string bearerVal = "synthetic-bearer-" + Guid.NewGuid().ToString("N")[..12];

        McpToolRegistry registry = new(registerDefaults: false);
        using McpServer server = new(registry, TextWriter.Null);
        await using WebApplication app = OfficialMcpHttpHost.BuildHttpApp(
            server,
            host: "127.0.0.1",
            port: port,
            requiredBearerToken: bearerVal,
            requireAuth: true,
            allowRemoteHttp: true);
        await app.StartAsync();

        try
        {
            using HttpClient client = new();
            HttpResponseMessage unauthResp = await client.GetAsync($"http://127.0.0.1:{port}/healthz");
            unauthResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            using HttpRequestMessage authReq = new(HttpMethod.Get, $"http://127.0.0.1:{port}/healthz");
            authReq.Headers.Add("Authorization", $"Bearer {bearerVal}");
            HttpResponseMessage authResp = await client.SendAsync(authReq);
            authResp.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static int GetEphemeralPort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public void Dispose()
    {
        _analytics.Dispose();
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }
}
