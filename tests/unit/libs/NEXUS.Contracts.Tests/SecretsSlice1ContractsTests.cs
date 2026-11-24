// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net.Http;
using FluentAssertions;
using NEXUS.Contracts.Configuration;
using NEXUS.Contracts.Security;
using Xunit;

namespace NEXUS.Contracts.Tests;

/// <summary>
///     Slice 1 (control-panel secrets): the shared SigV4 signer and the settings registry the panel is driven by.
/// </summary>
public sealed class SecretsSlice1ContractsTests
{
    // ── AwsSigV4Signer ────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://x.supabase.co/storage/v1/s3", "match-analytics", "match_1.parquet",
        "/storage/v1/s3/match-analytics/match_1.parquet")]
    [InlineData("https://x.supabase.co/storage/v1/s3/", "match-analytics", null, "/storage/v1/s3/match-analytics")]
    [InlineData("https://r2.example.com", "replays", "a/b c.honreplay", "/replays/a/b%20c.honreplay")]
    public void BuildObjectUri_KeepsEndpointPathPrefix(string endpoint, string bucket, string? key, string expectedPath)
    {
        Uri uri = AwsSigV4Signer.BuildObjectUri(endpoint, bucket, key);

        uri.AbsolutePath.Should().Be(expectedPath);
    }

    [Fact]
    public void Sign_MatchesAwsPublishedGetObjectExample()
    {
        // Known-answer vector from the AWS S3 SigV4 documentation ("Example: GET Object").
        // The credentials are AWS's public documentation examples, not real secrets.
        using HttpRequestMessage request = new(HttpMethod.Get, "https://examplebucket.s3.amazonaws.com/test.txt");

        AwsSigV4Signer.Sign(request, AwsSigV4Signer.EmptyPayloadHash, "us-east-1",
            "AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", // kongor-security-ignore: AWS SigV4 RFC doc example
            new DateTimeOffset(2013, 5, 24, 0, 0, 0, TimeSpan.Zero),
            new Dictionary<string, string> { ["Range"] = "bytes=0-9" });

        string authorization = string.Join(",", request.Headers.GetValues("Authorization"));
        authorization.Should().Contain("Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request"); // kongor-security-ignore: AWS SigV4 RFC doc example
        authorization.Should().Contain("SignedHeaders=host;range;x-amz-content-sha256;x-amz-date");
        authorization.Should().EndWith("Signature=f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41");
    }

    [Fact]
    public void HashPayload_EmptyPayload_EqualsEmptyPayloadHashConstant()
    {
        AwsSigV4Signer.HashPayload([]).Should().Be(AwsSigV4Signer.EmptyPayloadHash);
    }

    // ── NexusSettingRegistry ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Registry_IdsAreUnique()
    {
        NexusSettingRegistry.All.Select(descriptor => descriptor.Id)
            .Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Registry_NoVaultKeyIsWrittenByTwoSettings()
    {
        // Two settings writing the same key would silently overwrite each other on Save.
        List<string> writeKeys = NexusSettingRegistry.All.SelectMany(descriptor => descriptor.WriteKeys).ToList();

        writeKeys.GroupBy(key => key, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Should().BeEmpty();
    }

    [Fact]
    public void Registry_ReadAliasesNeverCollideWithAnotherSettingsWriteKeys()
    {
        // A ReadAlias is deleted on save – it must never be a key some other setting writes.
        HashSet<string> writeKeys = new(NexusSettingRegistry.All.SelectMany(descriptor => descriptor.WriteKeys),
            StringComparer.OrdinalIgnoreCase);

        NexusSettingRegistry.All
            .SelectMany(descriptor => descriptor.ReadAliases
                .Where(alias => writeKeys.Contains(alias))
                .Select(alias => $"{descriptor.Id} -> {alias}"))
            .Should().BeEmpty();
    }

    [Fact]
    public void Registry_DotEnvNamesAreEnvironmentSafeAndUnique()
    {
        List<string> names = NexusSettingRegistry.All.SelectMany(descriptor => descriptor.DotEnvNames).ToList();

        names.Should().OnlyContain(name => NexusSettingDescriptor.IsEnvironmentSafe(name));
        names.GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Should().BeEmpty();
    }

    [Fact]
    public void Registry_ContainsNoPersonalDefaults()
    {
        List<string> forbidden = GetForbiddenTokens();

        foreach (NexusSettingDescriptor descriptor in NexusSettingRegistry.All)
        foreach (string? value in new[] { descriptor.DefaultValue, descriptor.HelpText, descriptor.Label })
        foreach (string token in forbidden)
            (value ?? string.Empty).Should().NotContainEquivalentOf(token,
                $"setting '{descriptor.Id}' must not ship personal or secret defaults");
    }

    private static List<string> GetForbiddenTokens()
    {
        List<string> forbidden = [@"C:\Users", "compel_", "canary_dev_user", "eyJ"];
        string? envDenylist = Environment.GetEnvironmentVariable("KONGOR_PII_DENYLIST");
        if (!string.IsNullOrWhiteSpace(envDenylist))
        {
            foreach (string part in envDenylist.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (part.Length >= 4 && !forbidden.Contains(part, StringComparer.OrdinalIgnoreCase))
                    forbidden.Add(part);
            }
        }

        string root = FindSolutionRoot();
        string piiConfigPath = Path.Combine(root, "config", "local_pii_guard.local.json");
        if (File.Exists(piiConfigPath))
        {
            try
            {
                System.Text.Json.Nodes.JsonNode? parsed = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(piiConfigPath));
                if (parsed?["terms"] is System.Text.Json.Nodes.JsonArray terms)
                {
                    foreach (System.Text.Json.Nodes.JsonNode? item in terms)
                    {
                        string? val = item?["value"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(val) && val.Length >= 4 && !forbidden.Contains(val, StringComparer.OrdinalIgnoreCase))
                        {
                            forbidden.Add(val);
                        }
                    }
                }
            }
            catch
            {
                // gitignored local config fallback
            }
        }

        return forbidden;
    }

    private static string FindSolutionRoot()
    {
        string current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "ProjectKongor.slnx")) ||
                File.Exists(Path.Combine(current, "NEXUS.DistributedApplication.slnx")) ||
                File.Exists(Path.Combine(current, "NEXUS.DistributedApplication.sln")))
            {
                return current;
            }

            DirectoryInfo? parent = Directory.GetParent(current);
            if (parent == null) break;
            current = parent.FullName;
        }

        return AppContext.BaseDirectory;
    }

    // Public, documented local-development defaults (docker-compose containers / dev JWT). Slice 2 adds a production
    // guard that refuses to start with these. Anything else that is a secret must never ship a default.
    internal static readonly HashSet<string> PublicDevelopmentDefaults =
        ["DatabasePassword", "DistributedCachePassword", "JwtSigningKey"];

    [Fact]
    public void Registry_SecretsHaveNoDefaultValue()
    {
        NexusSettingRegistry.All
            .Where(descriptor => descriptor.IsSecret && !string.IsNullOrEmpty(descriptor.DefaultValue))
            .Select(descriptor => descriptor.Id)
            .Where(id => !PublicDevelopmentDefaults.Contains(id))
            .Should().BeEmpty("secrets must be entered or generated in the panel, never defaulted");
    }

    [Theory]
    [InlineData("SupabaseS3AccessKey", "Supabase:S3AccessKey")]
    [InlineData("SupabaseS3SecretKey", "Supabase:S3SecretKey")]
    [InlineData("SupabaseS3Endpoint", "Supabase:S3Endpoint")]
    [InlineData("LakehouseBucket", "Supabase:LakehouseBucket")]
    [InlineData("AlertWebhookUrl", "Alerts:DiscordWebhookUrl")]
    public void Registry_LakehouseAndAlertKeysMatchWhatConsumersRead(string id, string expectedKey)
    {
        NexusSettingRegistry.Get(id).Key.Should().Be(expectedKey);
    }
}
