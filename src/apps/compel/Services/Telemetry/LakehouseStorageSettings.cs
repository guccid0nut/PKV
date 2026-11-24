// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: LakehouseStorageSettings.cs
// System: COMPEL Supervision - Dedicated Server Match Parquet Lakehouse Settings
// Policy: Strict Explicit Typing (NO 'var'), Native AOT Trim-Safe (NO Reflection)
// ============================================================================

using Microsoft.Extensions.Configuration;

namespace COMPEL.Services.Telemetry;

/// <summary>
///     S3 connection settings for the match lakehouse. Resolved from configuration (the user-secrets vault written by
///     the NEXUS Control Panel "Secrets &amp; Configs" page) with legacy environment-variable names as fallbacks.
///     There are intentionally NO baked-in credentials: an unconfigured uploader is disabled.
/// </summary>
public sealed record LakehouseStorageSettings(
    string Endpoint,
    string Region,
    string Bucket,
    string AccessKey,
    string SecretKey)
{
    public const string DefaultBucket = "match-analytics";
    public const string DefaultRegion = "us-east-1";

    public static LakehouseStorageSettings Unconfigured { get; } = new(string.Empty, DefaultRegion, DefaultBucket, string.Empty, string.Empty);

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Endpoint) &&
        !string.IsNullOrWhiteSpace(AccessKey) &&
        !string.IsNullOrWhiteSpace(SecretKey) &&
        Uri.TryCreate(Endpoint, UriKind.Absolute, out _);

    /// <summary>Names of the missing settings (never values) for diagnostics.</summary>
    public IReadOnlyList<string> MissingSettings()
    {
        List<string> missing = [];
        if (string.IsNullOrWhiteSpace(Endpoint)) missing.Add("Supabase:S3Endpoint (or Supabase:Url)");
        if (string.IsNullOrWhiteSpace(AccessKey)) missing.Add("Supabase:S3AccessKey");
        if (string.IsNullOrWhiteSpace(SecretKey)) missing.Add("Supabase:S3SecretKey");
        return missing;
    }

    /// <summary>
    ///     Resolves settings from <paramref name="configuration" /> (vault / appsettings / env provider) and then
    ///     from raw process environment variables. Precedence: canonical key → panel compatibility key → legacy names.
    /// </summary>
    public static LakehouseStorageSettings Resolve(IConfiguration? configuration)
    {
        string? Read(params string[] keys)
        {
            foreach (string key in keys)
            {
                string? value = configuration?[key];
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }

            foreach (string key in keys)
            {
                string? value = Environment.GetEnvironmentVariable(key);
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }

            return null;
        }

        string? endpoint = Read("Supabase:S3Endpoint", "SUPABASE_S3_ENDPOINT", "KONGOR_LAKEHOUSE_S3_ENDPOINT", "SUPABASE_STORAGE_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            string? projectUrl = Read("Supabase:Url", "SUPABASE_URL");
            if (!string.IsNullOrWhiteSpace(projectUrl)) endpoint = projectUrl.TrimEnd('/') + "/storage/v1/s3";
        }

        return new LakehouseStorageSettings(
            endpoint ?? string.Empty,
            Read("Supabase:S3Region", "SUPABASE_S3_REGION", "KONGOR_LAKEHOUSE_S3_REGION", "SUPABASE_STORAGE_REGION") ?? DefaultRegion,
            Read("Supabase:LakehouseBucket", "SUPABASE_LAKEHOUSE_BUCKET", "KONGOR_LAKEHOUSE_S3_BUCKET") ?? DefaultBucket,
            Read("Supabase:S3AccessKey", "SUPABASE_S3_ACCESS_KEY", "KONGOR_LAKEHOUSE_S3_ACCESS_KEY", "SUPABASE_STORAGE_KEY_ID") ?? string.Empty,
            Read("Supabase:S3SecretKey", "SUPABASE_S3_SECRET_KEY", "KONGOR_LAKEHOUSE_S3_SECRET_KEY", "SUPABASE_STORAGE_SECRET") ?? string.Empty);
    }

    /// <summary>Never print the secret: only show whether it is present and the first 4 chars of the key id.</summary>
    public override string ToString()
    {
        string keyHint = AccessKey.Length >= 4 ? AccessKey[..4] + "…" : (AccessKey.Length > 0 ? "set" : "missing");
        return $"Endpoint={Endpoint}, Region={Region}, Bucket={Bucket}, AccessKey={keyHint}, SecretKey={(SecretKey.Length > 0 ? "set" : "missing")}";
    }
}
