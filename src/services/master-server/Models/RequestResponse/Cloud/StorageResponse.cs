// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace KONGOR.MasterServer.Models.RequestResponse.Cloud;

[Serializable]
public partial class StorageResponse
{
    [PHPProperty("success")] public bool Success { get; set; }

    [PHPProperty("messages")] public string Messages { get; set; } = "";
}
