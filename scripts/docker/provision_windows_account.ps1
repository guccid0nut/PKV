# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors

param(
    [string]$InviteCode = "KONGOR-1111"
)

$ErrorActionPreference = "Stop"
$suffix = (Get-Random -Minimum 1000 -Maximum 9999)
$ign = "WinJrny$suffix"
$secret = "Kj9!" + [Guid]::NewGuid().ToString("N").Substring(0, 12) + "aA1"

Write-Host "Provisioning ephemeral Windows playtest account IGN='$ign' via DAWNBRINGER (:5266)..."
$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$regPage = Invoke-WebRequest -Uri "http://127.0.0.1:5266/Account/Register?invite=$InviteCode" -WebSession $session -UseBasicParsing -TimeoutSec 10
if ($regPage.Content -notmatch 'name="IGN"') {
    throw "Registration form not rendered at http://127.0.0.1:5266/Account/Register (ensure KONGOR_STANDALONE=true is set on web-ui and web-api)."
}
if ($regPage.Content -notmatch 'name="__RequestVerificationToken"[^>]*value="([^"]+)"') {
    throw "Could not extract anti-forgery token from http://127.0.0.1:5266/Account/Register."
}
$csrf = $Matches[1]
$form = @{
    "__RequestVerificationToken" = $csrf
    "IGN" = $ign
    "Password" = $secret
    "ConfirmPassword" = $secret
    "InviteCode" = $InviteCode
}
$postResp = Invoke-WebRequest -Method Post -Uri "http://127.0.0.1:5266/Account/CompleteRegistration" -Body $form -WebSession $session -UseBasicParsing -MaximumRedirection 5 -TimeoutSec 15
if ($postResp.Content -match 'field-validation-error|validation-summary-errors' -or $postResp.Content -notmatch [regex]::Escape($ign)) {
    throw "Account registration did not complete on :5266 for IGN='$ign'. Verify invite '$InviteCode', KONGOR_STANDALONE=true, and 'web-portal-api:5200'."
}

$AccountFile = Join-Path $env:TEMP "kongor_windows_journey_account.json"
@{ ign = $ign; secret = $secret; createdUtc = [DateTime]::UtcNow.ToString("O") } |
    ConvertTo-Json -Compress |
    Set-Content -Path $AccountFile -Encoding UTF8 -NoNewline

Write-Host "Successfully provisioned Windows account: $ign (saved to $AccountFile)"
Write-Output $ign
