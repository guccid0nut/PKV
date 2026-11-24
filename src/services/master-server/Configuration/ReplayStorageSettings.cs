// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace KONGOR.MasterServer.Configuration;

public class ReplayStorageSettings
{
    public string Provider { get; set; } = "Local"; // Local, Supabase, Cloud, S3
    public string Domain { get; set; } = "http://newbs.io";
    public SupabaseStorageSettings Supabase { get; set; } = new();
    public CloudStorageSettings Cloud { get; set; } = new();
    public LocalDiskSettings LocalDisk { get; set; } = new();
}

public class SupabaseStorageSettings
{
    public string ProjectUrl { get; set; } = string.Empty;
    public string ServiceRoleKey { get; set; } = string.Empty;
    public string AnonKey { get; set; } = string.Empty;
    public string BucketName { get; set; } = "replays";
}

public class CloudStorageSettings
{
    public string BucketName { get; set; } = string.Empty;
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string ServiceUrl { get; set; } = string.Empty;
    public bool UsePublicReadAcl { get; set; }
}

public class LocalDiskSettings
{
    public string UploadDirectory { get; set; } = string.Empty;
}
