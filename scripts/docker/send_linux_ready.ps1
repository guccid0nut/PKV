# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors

$ErrorActionPreference = "Stop"
$code = @'
local main = UIManager and UIManager.GetInterface and UIManager.GetInterface("main")
if main and main.UICmd then
    main:UICmd("SendTMMPlayerReadyStatus(1, 2);")
    return "READY_STATUS_SENT"
end
return "FAILED_MAIN"
'@
& "$PSScriptRoot/invoke_linux_client_journey_container.ps1" -Action eval-lua -Lua $code
