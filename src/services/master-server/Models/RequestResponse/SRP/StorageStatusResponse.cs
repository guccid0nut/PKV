// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace KONGOR.MasterServer.Models.RequestResponse.SRP;

public partial class StorageStatusResponse
{
    [PHPProperty("success")] public bool Success { get; set; } = true;

    [PHPProperty("data")] public string? Data { get; set; } = null;

    [PHPProperty("cloud_storage_info")] public required CloudStorageInformation CloudStorageInformation { get; set; }

    [PHPProperty("messages")] public string Messages { get; set; } = string.Empty;
}
