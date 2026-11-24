// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Kongor.Core.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Kongor.Cli.Commands;

internal class StandaloneBundleSettings : CommandSettings
{
    [CommandOption("--bundle-root <DIR>")]
    [Description("Optional bundle root path override for testing")]
    public string? BundleRoot { get; init; }
}

internal sealed class AccountCreateSettings : StandaloneBundleSettings
{
    [CommandOption("-n|-u|--name|--username <IGN>")]
    [Description("In-game name for the player account")]
    public string? Name { get; init; }

    [CommandOption("-p|--password <PW>")]
    [Description("Account password")]
    public string? Password { get; init; }

    [CommandOption("-i|--invite <CODE>")]
    [Description("Optional single-use registration invite code")]
    public string? Invite { get; init; }
}

internal sealed class InviteCreateSettings : StandaloneBundleSettings
{
    [CommandOption("-c|--count <N>")]
    [Description("Number of invite codes to generate (default: 1)")]
    [DefaultValue(1)]
    public int Count { get; init; } = 1;
}

internal static class StandaloneCommandHelpers
{
    public static bool TryResolveBundledLayout(string? bundleRoot, [MaybeNullWhen(false)] out BundledLayout layout)
    {
        if (!string.IsNullOrWhiteSpace(bundleRoot) && BundledLayoutResolver.TryResolve(out layout, bundleRoot))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(bundleRoot) &&
            Infrastructure.CliRuntimeContext.TryResolveWorkspace(out var ws) &&
            ws is { IsBundledInstall: true, BundledLayout: not null })
        {
            layout = ws.BundledLayout;
            return true;
        }

        if (BundledLayoutResolver.TryResolve(out layout, bundleRoot))
        {
            return true;
        }

        AnsiConsole.MarkupLine("[bold red]This command is only available inside a standalone ProjectKongor bundle (playtest_policy.json + games/client not found).[/]");
        return false;
    }

    public static string? TryReadInternalServiceKey(BundledLayout layout)
    {
        if (!File.Exists(layout.SecretsJson))
        {
            AnsiConsole.MarkupLine("[bold red]Standalone secrets not initialized yet. Start the cluster once via NEXUS Control Panel first.[/]");
            return null;
        }

        try
        {
            using var stream = File.OpenRead(layout.SecretsJson);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            if (root.TryGetProperty("InternalServiceKey", out var elem) &&
                elem.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(elem.GetString()))
            {
                return elem.GetString();
            }

            if (root.TryGetProperty("Security:InternalServiceKey", out var elem2) &&
                elem2.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(elem2.GetString()))
            {
                return elem2.GetString();
            }

            if (root.TryGetProperty("Security", out var secElem) &&
                secElem.ValueKind == JsonValueKind.Object &&
                secElem.TryGetProperty("InternalServiceKey", out var elem3) &&
                elem3.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(elem3.GetString()))
            {
                return elem3.GetString();
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[bold red]Failed to read secrets file '{Markup.Escape(layout.SecretsJson)}': {Markup.Escape(ex.Message)}[/]");
            return null;
        }

        AnsiConsole.MarkupLine("[bold red]Standalone secrets not initialized yet. Start the cluster once via NEXUS Control Panel first.[/]");
        return null;
    }
}

internal sealed class AccountCreateCommand : AsyncCommand<AccountCreateSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, AccountCreateSettings settings)
    {
        if (!StandaloneCommandHelpers.TryResolveBundledLayout(settings.BundleRoot, out var layout))
        {
            return 1;
        }

        string? internalServiceKey = StandaloneCommandHelpers.TryReadInternalServiceKey(layout);
        if (string.IsNullOrWhiteSpace(internalServiceKey))
        {
            return 1;
        }

        if (string.IsNullOrWhiteSpace(settings.Name))
        {
            AnsiConsole.MarkupLine("[bold red]Account name is required. Specify --name <ign>.[/]");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(settings.Password))
        {
            AnsiConsole.MarkupLine("[bold red]Account password is required. Provide a non-empty value via -p.[/]");
            return 1;
        }

        string ign = settings.Name.Trim();
        string? inviteCode = string.IsNullOrWhiteSpace(settings.Invite) ? null : settings.Invite.Trim();

        using var httpClient = ASPIRE.Common.Http.SharedSocketsHttpClientFactory.Create(TimeSpan.FromSeconds(15));
        if (string.IsNullOrWhiteSpace(inviteCode))
        {
            try
            {
                using var mintReq = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:5200/api/admin/invites/bootstrap?count=1");
                mintReq.Headers.Add("X-Internal-Service-Key", internalServiceKey.Trim());
                mintReq.Content = new StringContent("{\"count\":1}", Encoding.UTF8, "application/json");
                using var mintResp = await httpClient.SendAsync(mintReq);
                if (mintResp.IsSuccessStatusCode)
                {
                    using var mintDoc = JsonDocument.Parse(await mintResp.Content.ReadAsStringAsync());
                    if (mintDoc.RootElement.TryGetProperty("codes", out var codesArr) &&
                        codesArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in codesArr.EnumerateArray())
                        {
                            string? c = item.GetString();
                            if (!string.IsNullOrWhiteSpace(c))
                            {
                                inviteCode = c.Trim();
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]Notice: Auto-invite mint skipped ({Markup.Escape(ex.Message)}); proceeding without invite code.[/]");
            }
        }

        var payload = new
        {
            Email = $"{ign.ToLowerInvariant()}@players.kongor.local",
            Name = ign,
            Password = settings.Password,
            DiscordId = (string?)null,
            DiscordUsername = (string?)null,
            DiscordAvatar = (string?)null,
            InviteCode = inviteCode
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:5200/Registration/Register");
        request.Headers.Add("X-Internal-Service-Key", internalServiceKey.Trim());
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[bold red]Failed to connect to ZORGATH: {Markup.Escape(ex.Message)}[/]");
            AnsiConsole.MarkupLine("[yellow]Ensure the standalone cluster is running on 127.0.0.1:5200[/]");
            return 1;
        }

        if (response.IsSuccessStatusCode)
        {
            AnsiConsole.MarkupLine($"[bold green]Created standalone account '{Markup.Escape(ign)}'[/]");
            return 0;
        }

        string errorBody;
        try
        {
            errorBody = await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            errorBody = ex.Message;
        }

        AnsiConsole.MarkupLine($"[bold red]Registration failed with status {(int)response.StatusCode} ({response.StatusCode}): {Markup.Escape(errorBody)}[/]");
        AnsiConsole.MarkupLine("[yellow]Ensure the standalone cluster is running on 127.0.0.1:5200[/]");
        return 1;
    }
}

internal sealed class InviteCreateCommand : AsyncCommand<InviteCreateSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, InviteCreateSettings settings)
    {
        if (!StandaloneCommandHelpers.TryResolveBundledLayout(settings.BundleRoot, out var layout))
        {
            return 1;
        }

        string? internalServiceKey = StandaloneCommandHelpers.TryReadInternalServiceKey(layout);
        if (string.IsNullOrWhiteSpace(internalServiceKey))
        {
            return 1;
        }

        int count = Math.Clamp(settings.Count, 1, 100);

        using var httpClient = ASPIRE.Common.Http.SharedSocketsHttpClientFactory.Create(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:5200/api/admin/invites/bootstrap?count={count}");
        request.Headers.Add("X-Internal-Service-Key", internalServiceKey.Trim());
        request.Content = new StringContent(JsonSerializer.Serialize(new { count }), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[bold red]Failed to connect to ZORGATH: {Markup.Escape(ex.Message)}[/]");
            AnsiConsole.MarkupLine("[yellow]Ensure the standalone cluster is running on 127.0.0.1:5200[/]");
            return 1;
        }

        if (!response.IsSuccessStatusCode)
        {
            string errorBody;
            try
            {
                errorBody = await response.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                errorBody = ex.Message;
            }

            AnsiConsole.MarkupLine($"[bold red]Invite generation failed with status {(int)response.StatusCode} ({response.StatusCode}): {Markup.Escape(errorBody)}[/]");
            AnsiConsole.MarkupLine("[yellow]Ensure the standalone cluster is running on 127.0.0.1:5200[/]");
            return 1;
        }

        string json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        List<string> codes = new();
        if (root.TryGetProperty("codes", out var codesElem) && codesElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in codesElem.EnumerateArray())
            {
                string? c = item.GetString();
                if (!string.IsNullOrWhiteSpace(c))
                {
                    codes.Add(c);
                }
            }
        }

        if (codes.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No invite codes returned by server.[/]");
            return 0;
        }

        try
        {
            string? dir = Path.GetDirectoryName(layout.InviteCodes);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.AppendAllLines(layout.InviteCodes, codes);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Warning: Failed to append codes to '{Markup.Escape(layout.InviteCodes)}': {Markup.Escape(ex.Message)}[/]");
        }

        AnsiConsole.MarkupLine($"[bold green]Generated {codes.Count} invite code(s):[/]");
        foreach (var code in codes)
        {
            Console.WriteLine(code);
        }
        return 0;
    }
}
