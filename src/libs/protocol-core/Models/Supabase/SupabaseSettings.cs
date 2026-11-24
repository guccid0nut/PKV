// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.Common.Models.Supabase;

public sealed class SupabaseSettings
{
    public string Url { get; set; } = string.Empty;

    public string AnonKey { get; set; } = string.Empty;

    public string ServiceRoleKey { get; set; } = string.Empty;

    public string DatabaseConnectionString { get; set; } = string.Empty;

    public string StorageBucket { get; set; } = "replays";

    public string S3AccessKey { get; set; } = string.Empty;

    public string S3SecretKey { get; set; } = string.Empty;

    public string S3Region { get; set; } = "us-east-1";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Url);

    public bool HasServiceRoleKey => !string.IsNullOrWhiteSpace(ServiceRoleKey);

    public bool HasDatabaseConnectionString => !string.IsNullOrWhiteSpace(DatabaseConnectionString);

    public bool HasS3Credentials => !string.IsNullOrWhiteSpace(S3AccessKey) && !string.IsNullOrWhiteSpace(S3SecretKey);

    public string GetS3Endpoint()
    {
        return string.IsNullOrWhiteSpace(Url) ? string.Empty : $"{Url.TrimEnd('/')}/storage/v1/s3";
    }

    public string GetRestStorageEndpoint(string? bucket = null)
    {
        if (string.IsNullOrWhiteSpace(Url)) return string.Empty;

        string targetBucket = !string.IsNullOrWhiteSpace(bucket) ? bucket : StorageBucket;
        return $"{Url.TrimEnd('/')}/storage/v1/object/{targetBucket}";
    }

    public string GetPublicObjectUrl(string objectPath, string? bucket = null)
    {
        if (string.IsNullOrWhiteSpace(Url)) return string.Empty;

        string targetBucket = !string.IsNullOrWhiteSpace(bucket) ? bucket : StorageBucket;
        string safePath = objectPath.TrimStart('/');
        return $"{Url.TrimEnd('/')}/storage/v1/object/public/{targetBucket}/{safePath}";
    }
}
