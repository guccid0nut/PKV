// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace ASPIRE.Tests.DAWNBRINGER.WebPortal.UI.Tests;

[Collection("DawnbringerE2E")]
public class DawnbringerHeadlessCaptureTests(DawnbringerE2ETestFixture fixture, ITestOutputHelper output) : IAsyncLifetime
{
    private readonly DawnbringerE2ETestFixture _fixture = fixture;
    private readonly ITestOutputHelper _output = output;
    private readonly HttpListener _listener = new();
    private CancellationTokenSource? _cts;
    private Task? _listenerTask;
    private string _baseUrl = "http://127.0.0.1:5266";
    private readonly string _edgePath = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
    private string? _authenticatedCookie;

    public async Task InitializeAsync()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // 1. Establish authenticated session cookie from fixture
        HttpClient authClient = _fixture.CreateNoRedirectClient();
        string callbackUrl = "/Account/DirectDiscordCallback?discordId=test-discord-guccidonut&discordUsername=guccidonut";
        HttpResponseMessage authResp = await authClient.GetAsync(callbackUrl);
        if (authResp.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            _authenticatedCookie = string.Join("; ", cookies.Select(c => c.Split(';')[0]));
            _output.WriteLine($"[Capture Init] Captured Auth Cookie: {_authenticatedCookie}");
        }

        // 2. Start native HTTP listener on an available port
        int port = GetAvailablePort();
        _baseUrl = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add($"{_baseUrl}/");
        _listener.Start();
        _cts = new CancellationTokenSource();

        // Locate web-ui wwwroot directory
        string wwwRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "src", "services", "web-ui", "wwwroot"));
        if (!Directory.Exists(wwwRoot))
        {
            wwwRoot = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "src", "services", "web-ui", "wwwroot"));
        }

        _listenerTask = Task.Run(async () =>
        {
            HttpClient client = _fixture.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = false
            });

            while (!_cts.Token.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch
                {
                    break;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        string path = ctx.Request.Url?.AbsolutePath ?? "/";

                        // Static file check in wwwroot
                        string localPath = Path.Combine(wwwRoot, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(localPath))
                        {
                            byte[] fileBytes = await File.ReadAllBytesAsync(localPath);
                            ctx.Response.StatusCode = 200;
                            ctx.Response.ContentType = GetMimeType(localPath);
                            ctx.Response.ContentLength64 = fileBytes.Length;
                            await ctx.Response.OutputStream.WriteAsync(fileBytes);
                            ctx.Response.Close();
                            return;
                        }

                        // Forward request to in-memory WebApplicationFactory
                        HttpMethod method = new(ctx.Request.HttpMethod);
                        using HttpRequestMessage req = new(method, ctx.Request.RawUrl);

                        bool hasCookieHeader = false;
                        foreach (string? headerName in ctx.Request.Headers.AllKeys)
                        {
                            if (headerName == null) continue;
                            if (string.Equals(headerName, "Host", StringComparison.OrdinalIgnoreCase)) continue;
                            if (string.Equals(headerName, "Connection", StringComparison.OrdinalIgnoreCase)) continue;

                            if (string.Equals(headerName, "Cookie", StringComparison.OrdinalIgnoreCase))
                            {
                                hasCookieHeader = true;
                            }

                            string[] values = ctx.Request.Headers.GetValues(headerName) ?? [];
                            req.Headers.TryAddWithoutValidation(headerName, values);
                        }

                        // Ensure authenticated session cookie is attached if not present
                        if (!hasCookieHeader && !string.IsNullOrEmpty(_authenticatedCookie))
                        {
                            req.Headers.TryAddWithoutValidation("Cookie", _authenticatedCookie);
                        }

                        if (ctx.Request.HasEntityBody)
                        {
                            using MemoryStream ms = new();
                            await ctx.Request.InputStream.CopyToAsync(ms);
                            req.Content = new ByteArrayContent(ms.ToArray());
                            if (!string.IsNullOrEmpty(ctx.Request.ContentType))
                            {
                                req.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(ctx.Request.ContentType);
                            }
                        }

                        using HttpResponseMessage res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                        ctx.Response.StatusCode = (int)res.StatusCode;

                        foreach (var header in res.Headers)
                        {
                            if (string.Equals(header.Key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
                            if (string.Equals(header.Key, "Set-Cookie", StringComparison.OrdinalIgnoreCase))
                            {
                                foreach (string cookieVal in header.Value)
                                {
                                    ctx.Response.AppendHeader("Set-Cookie", cookieVal);
                                }
                            }
                            else if (string.Equals(header.Key, "Location", StringComparison.OrdinalIgnoreCase))
                            {
                                string? loc = header.Value.FirstOrDefault();
                                if (!string.IsNullOrEmpty(loc))
                                {
                                    if (loc.StartsWith('/')) loc = $"{_baseUrl}{loc}";
                                    ctx.Response.RedirectLocation = loc;
                                }
                            }
                            else
                            {
                                foreach (string val in header.Value)
                                {
                                    ctx.Response.Headers.Add(header.Key, val);
                                }
                            }
                        }

                        foreach (var header in res.Content.Headers)
                        {
                            if (string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                            foreach (string val in header.Value)
                            {
                                ctx.Response.Headers.Add(header.Key, val);
                            }
                        }

                        byte[] bodyBytes = await res.Content.ReadAsByteArrayAsync();
                        ctx.Response.ContentLength64 = bodyBytes.Length;
                        await ctx.Response.OutputStream.WriteAsync(bodyBytes);
                        ctx.Response.Close();
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            ctx.Response.StatusCode = 500;
                            byte[] err = Encoding.UTF8.GetBytes(ex.ToString());
                            ctx.Response.OutputStream.Write(err, 0, err.Length);
                            ctx.Response.Close();
                        }
                        catch { }
                    }
                });
            }
        });
    }

    public Task DisposeAsync()
    {
        try
        {
            _cts?.Cancel();
            if (_listener.IsListening)
            {
                _listener.Stop();
                _listener.Close();
            }
        }
        catch { }

        return Task.CompletedTask;
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task CaptureAll15Routes_HeadlessScreenshots_And_VerifyIntegrity()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // 1. Ensure target screenshot directory exists
        string screenshotDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "docs", "screenshots"));
        Directory.CreateDirectory(screenshotDir);

        string profileDir = Path.Combine(Path.GetTempPath(), "dawnbringer_edge_profile_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profileDir);

        try
        {
            // 2. Pre-authenticate session using Discord OAuth Direct Callback
            string authCallbackUrl = $"{_baseUrl}/Account/DirectDiscordCallback?discordId=test-discord-guccidonut&discordUsername=guccidonut";
            _output.WriteLine($"[Capture] Authenticating session via: {authCallbackUrl}");
            await RunEdgeAsync(profileDir, authCallbackUrl, null);

            // 3. Define all 15 required routes
            var routes = new (int Id, string Route, string FileName, string Description)[]
            {
                (1, "/", "01_home.png", "Main Command Center / Homepage"),
                (2, "/matches", "02_matches.png", "Match History Dashboard"),
                (3, "/stats?accountName=guccidonut", "03_stats.png", "Player Combat Statistics & MMR Telemetry"),
                (4, "/leaderboard", "04_leaderboard.png", "Global Player Leaderboard"),
                (5, "/codex", "05_codex.png", "Hero Codex Roster (139 Champions)"),
                (6, "/codex/Hero_Vindicator", "06_hero_detail.png", "Hero Detail Profile (Vindicator Abilities & Lore)"),
                (7, "/items", "07_items.png", "Item Compendium Catalog (174 Items)"),
                (8, "/guides", "08_guides.png", "Community Strategy Guides Directory"),
                (9, "/guides/1", "09_guide_detail.png", "Guide Detail View (Build Order & Items)"),
                (10, "/armory?category=couriers", "10_armory.png", "Armory Showcase (Couriers & Wards Catalog)"),
                (11, "/store", "11_store.png", "Storefront Cosmetic & Alt Avatar Catalog"),
                (12, "/inventory", "12_inventory.png", "Player Inventory Management"),
                (13, "/plinko", "13_plinko.png", "Plinko Drop Board & Ticket Shop"),
                (14, "/setup", "14_setup.png", "Client Setup & Installation Guide"),
                (15, "/staff", "15_staff.png", "Staff Administration & Moderation Portal")
            };

            // Test each route with authenticated client
            HttpClient testClient = await _fixture.CreateAuthenticatedClientAsync("test-discord-guccidonut", "guccidonut");

            foreach (var item in routes)
            {
                string targetUrl = $"{_baseUrl}{item.Route}";
                string screenshotPath = Path.Combine(screenshotDir, item.FileName);

                _output.WriteLine($"[Capture {item.Id:D2}] Capturing {item.Route} -> {item.FileName}");

                // HTTP verification
                HttpResponseMessage httpResp = await testClient.GetAsync(item.Route);
                httpResp.StatusCode.Should().Be(HttpStatusCode.OK, $"Route {item.Route} must return HTTP 200 OK");
                string markup = await httpResp.Content.ReadAsStringAsync();
                markup.Should().NotContain("500 Internal Server Error");
                if (item.Route.Contains("codex"))
                {
                    markup.Should().NotContain("0 Champions Synchronized");
                }

                // Browser headless capture
                await RunEdgeAsync(profileDir, targetUrl, screenshotPath);

                // File validation
                File.Exists(screenshotPath).Should().BeTrue($"Screenshot {item.FileName} must exist");
                FileInfo fi = new(screenshotPath);
                fi.Length.Should().BeGreaterThan(10 * 1024, $"Screenshot {item.FileName} must be non-empty (> 10 KB), was {fi.Length} bytes");

                _output.WriteLine($"[Verified {item.Id:D2}] {item.FileName} captured successfully ({fi.Length:N0} bytes)");
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(profileDir))
                {
                    Directory.Delete(profileDir, true);
                }
            }
            catch { }
        }
    }

    private async Task RunEdgeAsync(string profileDir, string url, string? screenshotPath)
    {
        List<string> args =
        [
            "--headless",
            "--enable-unsafe-swiftshader",
            "--use-gl=angle",
            "--use-angle=swiftshader",
            "--virtual-time-budget=3500",
            "--hide-scrollbars",
            "--window-size=1920,1080",
            $"--user-data-dir={profileDir}"
        ];

        if (!string.IsNullOrEmpty(screenshotPath))
        {
            args.Add($"--screenshot={screenshotPath}");
        }
        else
        {
            string authThumbPath = Path.Combine(profileDir, "_auth_warmup.png");
            args.Add($"--screenshot={authThumbPath}");
        }

        args.Add(url);

        ProcessStartInfo psi = new()
        {
            FileName = _edgePath,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using Process? proc = Process.Start(psi);
        if (proc != null)
        {
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                proc.Kill(entireProcessTree: true);
                throw;
            }
        }
    }

    private static string GetMimeType(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".css" => "text/css",
            ".js" => "application/javascript",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".ico" => "image/x-icon",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            ".ttf" => "font/ttf",
            ".json" => "application/json",
            ".html" => "text/html",
            _ => "application/octet-stream"
        };
    }

    private static int GetAvailablePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
