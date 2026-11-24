// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace KONGOR.MasterServer.Models.RequestResponse.Cloud;

public partial class EnableConfigurationBackupResponse
{
    [PHPProperty("data")] public required CloudStorageInformation Data { get; set; }
}
