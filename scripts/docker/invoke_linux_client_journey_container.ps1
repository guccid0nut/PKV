# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors

<#
.SYNOPSIS
    Isolated Headless Linux HoN Client (hon-x86_64 + Xvfb :99 + Mesa llvmpipe + NativeGuard Unix IPC)
    Container Controller for Cross-Platform Self-Healing Journeys.

.DESCRIPTION
    Runs games/client/linux-x64/hon-x86_64 strictly offscreen inside Docker ('kongor-linux-client-journey')
    with read-only host asset mounts, writable container-local userdata, socat TCP/UDP loopback bridges to
    host MasterServer/ChatServer/COMPEL/QueueArcade, Unix socket Lua evaluation, and X11 PNG frame capture.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("provision-account", "start", "login", "enter-offline-hub", "status", "eval", "eval-lua", "cmd", "capture", "tail-log", "stop")]
    [string]$Action,

    [string]$ContainerName = "kongor-linux-client-journey",
    [string]$ImageTag = "projectkongor/linux-client-xvfb:latest",
    [Alias("LuaCode")]
    [string]$Lua = "",
    [string]$Command = "",
    [Alias("CapturePath")]
    [string]$OutputPath = "",
    [int]$Lines = 60,
    [string]$Filter = "",
    [string]$InviteCode = "KONGOR-1111",
    [Alias("Account", "IGN")]
    [string]$Username = "",
    [string]$Password = "",
    [int]$TimeoutSeconds = 150,
    [switch]$FullRes,
    [switch]$ForwardLocalHostServer
)

$ErrorActionPreference = "Stop"
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$GamesDir = Join-Path $RepoRoot "games"
$AccountStateFile = Join-Path $env:TEMP "kongor_linux_journey_account.json"

function Write-LinuxHarnessLog {
    param([string]$Message)
    $ts = [DateTime]::Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
    Write-Host "[$ts] [LINUX-JOURNEY] $Message"
}

function Invoke-DockerQuiet {
    param([Parameter(Mandatory = $true)][string[]]$ArgsList)
    $prev = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = & docker @ArgsList 2>&1
        return [pscustomobject]@{
            ExitCode = $LASTEXITCODE
            Output   = ($output -join "`n")
            Lines    = @($output)
        }
    }
    finally {
        $ErrorActionPreference = $prev
    }
}

function Ensure-LinuxClientImage {
    $dockerfileBody = @'
FROM ubuntu:24.04
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends \
    xvfb \
    libgl1 \
    libgl1-mesa-dri \
    libglx-mesa0 \
    libglu1-mesa \
    libegl1 \
    libx11-6 \
    libxext6 \
    libxrandr2 \
    libxcursor1 \
    libxinerama1 \
    libxi6 \
    libxxf86vm1 \
    libxrender1 \
    libxfixes3 \
    libfreetype6 \
    libfontconfig1 \
    libasound2t64 \
    libudev1 \
    libxml2 \
    libncurses6 \
    libstdc++6 \
    libgcc-s1 \
    iproute2 \
    socat \
    python3 \
    ffmpeg \
    procps \
    ca-certificates \
    curl \
    && rm -rf /var/lib/apt/lists/*
'@
    $normalizedBody = ($dockerfileBody -replace "`r`n", "`n").Trim()
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hashBytes = $sha256.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($normalizedBody))
        $specHash = ([BitConverter]::ToString($hashBytes)).Replace("-", "").Substring(0, 12).ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }

    $probe = Invoke-DockerQuiet -ArgsList @("image", "inspect", "-f", "{{json .Config.Labels}}", $ImageTag)
    $cachedLabel = ""
    if ($probe.ExitCode -eq 0 -and -not [string]::IsNullOrWhiteSpace($probe.Output) -and $probe.Output.Trim() -ne "null") {
        try {
            $labelsObj = $probe.Output.Trim() | ConvertFrom-Json
            if ($labelsObj -and $labelsObj.PSObject.Properties["io.projectkongor.linux-harness-spec"]) {
                $cachedLabel = [string]$labelsObj."io.projectkongor.linux-harness-spec"
            }
        } catch { }
    }
    if ($probe.ExitCode -eq 0 -and $cachedLabel -eq $specHash) {
        Write-LinuxHarnessLog "Verified cached headless Linux client image '$ImageTag' (spec=$specHash, iproute2=present)."
        return
    }

    $cachedSpec = if ($probe.ExitCode -eq 0 -and -not [string]::IsNullOrWhiteSpace($cachedLabel)) { $cachedLabel } elseif ($probe.ExitCode -eq 0) { "legacy-unlabeled" } else { "missing" }
    Write-LinuxHarnessLog "Building headless Linux client image '$ImageTag' (cachedSpec='$cachedSpec' -> targetSpec='$specHash', ensuring iproute2 + Mesa llvmpipe)..."
    $buildCtx = Join-Path $env:TEMP "kongor_linux_xvfb_ctx"
    if (-not (Test-Path $buildCtx)) {
        [void](New-Item -ItemType Directory -Path $buildCtx -Force)
    }

    $dockerfile = $normalizedBody + "`nLABEL io.projectkongor.linux-harness-spec=`"$specHash`"`n"
    Set-Content -Path (Join-Path $buildCtx "Dockerfile") -Value $dockerfile -Encoding UTF8 -NoNewline
    $buildRes = Invoke-DockerQuiet -ArgsList @("build", "-t", $ImageTag, $buildCtx)
    if ($buildRes.ExitCode -ne 0) {
        throw "Docker build failed for '$ImageTag': $($buildRes.Output). Inspect Docker Desktop status and retry."
    }
    Write-LinuxHarnessLog "Built headless Linux client image '$ImageTag' cleanly (spec=$specHash)."
}

function Invoke-LinuxSocketFrame {
    param(
        [int]$OpCode,
        [string]$Payload
    )

    $b64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($Payload))
    $py = @"
import base64, os, socket, struct, sys
sock_path = '/tmp/kongor-0/kongor-linux-client.sock'
if not os.path.exists(sock_path):
    print('{"success":false,"error":"IPC socket missing at ' + sock_path + '"}')
    sys.exit(2)
payload = base64.b64decode('$b64')
frame = struct.pack('<2sHIi', b'MW', $OpCode, len(payload), 1) + payload
s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
s.settimeout(4.0)
s.connect(sock_path)
s.sendall(frame)
hdr = b''
while len(hdr) < 12:
    chunk = s.recv(12 - len(hdr))
    if not chunk:
        break
    hdr += chunk
if len(hdr) < 12:
    print('{"success":false,"error":"Short IPC header"}')
    sys.exit(3)
magic, resp_op, resp_len, req_id = struct.unpack('<2sHIi', hdr)
body = b''
while len(body) < resp_len:
    chunk = s.recv(resp_len - len(body))
    if not chunk:
        break
    body += chunk
s.close()
sys.stdout.write(body.decode('utf-8', errors='replace'))
"@
    $pyB64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes(($py -replace "`r`n", "`n")))
    $res = Invoke-DockerQuiet -ArgsList @("exec", $ContainerName, "python3", "-c", "import base64; exec(base64.b64decode('$pyB64').decode('utf-8'))")
    if ($res.ExitCode -ne 0) {
        throw "Linux Unix socket IPC failed (exitCode=$($res.ExitCode)): $($res.Output). Run 'scripts/docker/invoke_linux_client_journey_container.ps1 -Action tail-log' to diagnose."
    }
    return $res.Output
}

function Invoke-LinuxEvalLuaInternal {
    param(
        [string]$LuaCode,
        [int]$WaitMs = 5000
    )

    $nonce = [Guid]::NewGuid().ToString("N").Substring(0, 10)
    $runnerLua = @"
local m = '[LINUX_IPC:' .. '$nonce' .. '] '
local fn, ce = loadfile('ipc/kongor_eval.lua')
if not fn then
    Echo(m .. 'ERR|' .. tostring(ce))
else
    local ok, rv = pcall(fn)
    Echo(m .. (ok and 'OK|' or 'ERR|') .. tostring(rv))
end
if Cmd then Cmd('FlushLogs') end
"@

    $evalB64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes(($LuaCode -replace "`r`n", "`n")))
    $runnerB64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes(($runnerLua -replace "`r`n", "`n")))
    $writePy = @"
import base64, os
ud = '/app/games/client/linux-x64/userdata/.Heroes of Newerth/override'
os.makedirs(ud, exist_ok=True)
sym = '/app/games/client/linux-x64/ipc'
if not os.path.exists(sym):
    try: os.symlink(ud, sym)
    except Exception: pass
with open(os.path.join(ud, 'kongor_eval.lua'), 'wb') as f:
    f.write(base64.b64decode('$evalB64'))
with open(os.path.join(ud, 'kongor_runner.lua'), 'wb') as f:
    f.write(base64.b64decode('$runnerB64'))
"@
    $writePyB64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes(($writePy -replace "`r`n", "`n")))
    [void](Invoke-DockerQuiet -ArgsList @("exec", $ContainerName, "python3", "-c", "import base64; exec(base64.b64decode('$writePyB64').decode('utf-8'))"))

    $wrappedCmd = "Script dofile('ipc/kongor_runner.lua')"
    [void](Invoke-LinuxSocketFrame -OpCode 2 -Payload $wrappedCmd)

    $deadline = [DateTime]::UtcNow.AddMilliseconds($WaitMs)
    $marker = "[LINUX_IPC:$nonce]"
    $probePy = @"
import os, sys
marker = '$marker'
candidates = [
    '/app/games/client/linux-x64/userdata/.Heroes of Newerth/override/console.log',
    '/app/games/client/linux-x64/userdata/.Heroes of Newerth/game/console.log',
    '/tmp/hon_stdout.log'
]
try:
    for root, dirs, files in os.walk('/app/games/client/linux-x64/userdata'):
        for file in files:
            if file.endswith('console.log'):
                p = os.path.join(root, file)
                if p not in candidates:
                    candidates.append(p)
except Exception:
    pass
for p in candidates:
    if not os.path.isfile(p):
        continue
    try:
        with open(p, 'rb') as f:
            f.seek(0, os.SEEK_END)
            sz = f.tell()
            f.seek(max(0, sz - 65536))
            tail = f.read().decode('utf-8', errors='replace')
        for line in reversed(tail.splitlines()):
            idx = line.find(marker)
            if idx >= 0:
                val = line[idx + len(marker):].strip()
                if val.startswith('OK|') or val.startswith('ERR|'):
                    print(val)
                    sys.exit(0)
    except Exception:
        pass
sys.exit(1)
"@

    $probeB64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes(($probePy -replace "`r`n", "`n")))
    while ([DateTime]::UtcNow -lt $deadline) {
        $probe = Invoke-DockerQuiet -ArgsList @("exec", $ContainerName, "python3", "-c", "import base64; exec(base64.b64decode('$probeB64').decode('utf-8'))")
        if ($probe.ExitCode -eq 0 -and -not [string]::IsNullOrWhiteSpace($probe.Output)) {
            return $probe.Output.Trim()
        }
        Start-Sleep -Milliseconds 120
    }

    throw "Timed out after ${WaitMs}ms waiting for Lua marker $marker in Linux client logs. Check 'scripts/docker/invoke_linux_client_journey_container.ps1 -Action tail-log'."
}

function Resolve-LinuxLocalDevAccount {
    # Matches Kongor.Cli.Mcp.Services.ClientCredentialManager local dev defaults (KongorPlayer)
    $resolvedIgn = if (-not [string]::IsNullOrWhiteSpace($Username)) {
        $Username.Trim()
    }
    elseif (-not [string]::IsNullOrWhiteSpace($env:KONGOR_SHJ_USER)) {
        $env:KONGOR_SHJ_USER.Trim()
    }
    elseif (-not [string]::IsNullOrWhiteSpace($env:KONGOR_TEST_USER)) {
        $env:KONGOR_TEST_USER.Trim()
    }
    else {
        "KongorPlayer"
    }

    $credSource = "local-dev-default"
    $resolvedCred = "KONGOR"
    if (-not [string]::IsNullOrWhiteSpace($Password)) {
        $resolvedCred = $Password.Trim()
        $credSource = "explicit-parameter"
    }
    elseif (-not [string]::IsNullOrWhiteSpace($env:KONGOR_SHJ_PASS_MD5)) {
        $resolvedCred = $env:KONGOR_SHJ_PASS_MD5.Trim().ToLowerInvariant()
        $credSource = "env-kongor-shj-pass-md5"
    }
    elseif (-not [string]::IsNullOrWhiteSpace($env:KONGOR_TEST_PASSWORD)) {
        $resolvedCred = $env:KONGOR_TEST_PASSWORD.Trim()
        $credSource = "env-kongor-test-password"
    }
    elseif (-not [string]::IsNullOrWhiteSpace($env:KONGOR_TEST_PASSWORD_MD5)) {
        $resolvedCred = $env:KONGOR_TEST_PASSWORD_MD5.Trim().ToLowerInvariant()
        $credSource = "env-kongor-test-password-md5"
    }

    return [pscustomobject]@{
        Ign    = $resolvedIgn
        Cred   = $resolvedCred
        Source = $credSource
    }
}

function ConvertTo-K2PasswordMd5 {
    param([string]$InputCredential)
    if ([string]::IsNullOrWhiteSpace($InputCredential)) {
        $InputCredential = "KONGOR"
    }
    $trimmed = $InputCredential.Trim()
    if ($trimmed -match '^[0-9a-fA-F]{32}$') {
        return $trimmed.ToLowerInvariant()
    }
    $md5 = [System.Security.Cryptography.MD5]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($trimmed)
        return ([BitConverter]::ToString($md5.ComputeHash($bytes))).Replace("-", "").ToLowerInvariant()
    }
    finally {
        $md5.Dispose()
    }
}

function Invoke-LinuxDismissOnboardingModal {
    param([string]$AccountLabel = "player")
    $dismissLua = @"
if _G.KongorOnboarding and type(_G.KongorOnboarding.Skip) == 'function' then
    pcall(_G.KongorOnboarding.Skip)
elseif _G.KongorOnboarding and type(_G.KongorOnboarding.SetCompleted) == 'function' then
    pcall(_G.KongorOnboarding.SetCompleted, true)
end
if type(SetSave) == 'function' then
    pcall(SetSave, 'cg_onboardingCompleted', '1', 'string')
end
local root = GetWidget and (GetWidget('onboarding_presets_root', 'main') or GetWidget('onboarding_presets_root', 'main_popup') or GetWidget('onboarding_presets_root'))
if root and root.SetVisible then
    pcall(root.SetVisible, root, false)
end
return 'ONBOARDING_PRESET_DISMISSED'
"@
    try {
        [void](Invoke-LinuxEvalLuaInternal -LuaCode $dismissLua -WaitMs 4000)
        Write-LinuxHarnessLog "Auto-dismissed playstyle preset onboarding modal for '$AccountLabel'."
    }
    catch {
        Write-LinuxHarnessLog "Notice: Onboarding preset dismissal probe caught non-fatal exception: $_"
    }
}

switch ($Action) {
    "provision-account" {
        if (-not [string]::IsNullOrWhiteSpace($Username) -or -not [string]::IsNullOrWhiteSpace($Password)) {
            $explicitAcct = Resolve-LinuxLocalDevAccount
            @{
                ign              = $explicitAcct.Ign
                secret           = $explicitAcct.Cred
                fallback         = $false
                authMode         = "explicit-dev-account"
                credentialSource = $explicitAcct.Source
                createdUtc       = [DateTime]::UtcNow.ToString("O")
            } | ConvertTo-Json -Compress | Set-Content -Path $AccountStateFile -Encoding UTF8 -NoNewline

            Write-LinuxHarnessLog "Configured explicit Linux journey account IGN='$($explicitAcct.Ign)' (credentialSource=$($explicitAcct.Source); stored only in temp session file)."
            Write-Output (@{
                ign         = $explicitAcct.Ign
                provisioned = $true
                reused      = $false
                fallback    = $false
                authMode    = "explicit-dev-account"
            } | ConvertTo-Json -Compress)
            break
        }

        if (Test-Path $AccountStateFile) {
            try {
                $existing = Get-Content -Raw -Path $AccountStateFile | ConvertFrom-Json
                if ($existing.ign -and $existing.secret) {
                    $cachedMode = if ($existing.PSObject.Properties['authMode'] -and $existing.authMode) { [string]$existing.authMode } else { "cached" }
                    $cachedFallback = [bool]($existing.PSObject.Properties['fallback'] -and $existing.fallback)
                    Write-LinuxHarnessLog "Reusing existing Linux journey account IGN='$($existing.ign)' (authMode=$cachedMode, fallback=$cachedFallback)."
                    Write-Output (@{
                        ign         = $existing.ign
                        provisioned = $true
                        reused      = $true
                        fallback    = $cachedFallback
                        authMode    = $cachedMode
                    } | ConvertTo-Json -Compress)
                    break
                }
            }
            catch {
                Write-LinuxHarnessLog "Warning: Failed to parse existing account state file '$AccountStateFile'; reprovisioning ($($_.Exception.Message))."
            }
        }

        $suffix = (Get-Random -Minimum 1000 -Maximum 9999)
        $ign = "LinJrny$suffix"
        $ephemeralCred = "Kj9!" + [Guid]::NewGuid().ToString("N").Substring(0, 12) + "aA1"

        Write-LinuxHarnessLog "Probing DAWNBRINGER (:5266/Account/Register) to provision ephemeral playtest account IGN='$ign'..."
        $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
        $regPage = $null
        $regProbeError = ""
        try {
            $regPage = Invoke-WebRequest -Uri "http://127.0.0.1:5266/Account/Register?invite=$InviteCode" -WebSession $session -UseBasicParsing -TimeoutSec 10
        }
        catch {
            $regProbeError = $_.Exception.Message
        }

        $standaloneRegistered = $false
        $fallbackReason = ""

        if ($null -eq $regPage) {
            $fallbackReason = "web-register-unreachable"
            Write-LinuxHarnessLog "FALLBACK NOTICE [P1-06]: DAWNBRINGER http://127.0.0.1:5266/Account/Register was unreachable ($regProbeError)."
        }
        elseif ($regPage.Content -notmatch 'name="IGN"') {
            $fallbackReason = "discord-only-auth-active"
            Write-LinuxHarnessLog "FALLBACK NOTICE [P1-06]: Standalone password registration form not rendered at http://127.0.0.1:5266/Account/Register (Discord-only auth active / KONGOR_STANDALONE!=true)."
        }
        elseif ($regPage.Content -notmatch 'name="__RequestVerificationToken"[^>]*value="([^"]+)"') {
            $fallbackReason = "missing-antiforgery-token"
            Write-LinuxHarnessLog "FALLBACK NOTICE [P1-06]: Anti-forgery token missing on http://127.0.0.1:5266/Account/Register."
        }
        else {
            $csrf = $Matches[1]
            $form = @{
                "__RequestVerificationToken" = $csrf
                "IGN"                        = $ign
                "Password"                   = $ephemeralCred
                "ConfirmPassword"            = $ephemeralCred
                "InviteCode"                 = $InviteCode
            }
            try {
                $postResp = Invoke-WebRequest -Method Post -Uri "http://127.0.0.1:5266/Account/CompleteRegistration" -Body $form -WebSession $session -UseBasicParsing -MaximumRedirection 5 -TimeoutSec 15
                if ($postResp.Content -notmatch 'field-validation-error|validation-summary-errors' -and $postResp.Content -match [regex]::Escape($ign)) {
                    $standaloneRegistered = $true
                }
                else {
                    $fallbackReason = "standalone-register-validation-rejected"
                    Write-LinuxHarnessLog "FALLBACK NOTICE [P1-06]: Standalone registration POST did not confirm IGN='$ign' (invite='$InviteCode')."
                }
            }
            catch {
                $fallbackReason = "standalone-register-post-failed"
                Write-LinuxHarnessLog "FALLBACK NOTICE [P1-06]: Standalone registration POST failed ($($_.Exception.Message))."
            }
        }

        if ($standaloneRegistered) {
            @{
                ign              = $ign
                secret           = $ephemeralCred
                fallback         = $false
                authMode         = "standalone-web-register"
                credentialSource = "ephemeral-generated"
                createdUtc       = [DateTime]::UtcNow.ToString("O")
            } | ConvertTo-Json -Compress | Set-Content -Path $AccountStateFile -Encoding UTF8 -NoNewline

            Write-LinuxHarnessLog "Provisioned ephemeral Linux journey account IGN='$ign' via DAWNBRINGER (:5266) (credentials stored only in temp session file)."
            Write-Output (@{
                ign         = $ign
                provisioned = $true
                reused      = $false
                fallback    = $false
                authMode    = "standalone-web-register"
            } | ConvertTo-Json -Compress)
        }
        else {
            $devAcct = Resolve-LinuxLocalDevAccount
            @{
                ign              = $devAcct.Ign
                secret           = $devAcct.Cred
                fallback         = $true
                fallbackReason   = $fallbackReason
                authMode         = "local-dev-fallback"
                credentialSource = $devAcct.Source
                createdUtc       = [DateTime]::UtcNow.ToString("O")
            } | ConvertTo-Json -Compress | Set-Content -Path $AccountStateFile -Encoding UTF8 -NoNewline

            Write-LinuxHarnessLog "Falling back cleanly to local dev test account IGN='$($devAcct.Ign)' (reason=$fallbackReason, credentialSource=$($devAcct.Source)). Pass -Username/-Password to override, or run '-Action enter-offline-hub' for zero-backend UI journeys."
            Write-Output (@{
                ign            = $devAcct.Ign
                provisioned    = $true
                reused         = $false
                fallback       = $true
                fallbackReason = $fallbackReason
                authMode       = "local-dev-fallback"
            } | ConvertTo-Json -Compress)
        }
    }

    "start" {
        Ensure-LinuxClientImage
        [void](Invoke-DockerQuiet -ArgsList @("rm", "-f", $ContainerName))

        $bootScript = @'
#!/bin/bash
set -euo pipefail

mkdir -p \
  /app/games/client/linux-x64/userdata/".Heroes of Newerth"/override \
  /app/games/client/linux-x64/userdata/".Heroes of Newerth"/game \
  /app/games/client/linux-x64/game \
  /tmp/kongor-0
chmod 0700 /tmp/kongor-0

ln -sfn /opt/kongor-src/games/shared /app/games/shared
ln -sfn /opt/kongor-src/games/server /app/games/server

for tier in base game ClientPackageFiles KONGOR override; do
  if [ -d "/opt/kongor-src/games/client/${tier}" ]; then
    ln -sfn "/opt/kongor-src/games/client/${tier}" "/app/games/client/${tier}"
  fi
done

for entry in /opt/kongor-src/games/client/linux-x64/*; do
  bn="$(basename "$entry")"
  case "$bn" in
    manifest.xml*|userdata|libs-runtime|hon.sh|hon-x86_64|base|ClientPackageFiles|KONGOR|override|game|tos*.txt|privacy*.txt|change_log*.txt|honicon_*.png)
      continue
      ;;
    *)
      ln -sfn "$entry" "/app/games/client/linux-x64/$bn"
      ;;
  esac
done

# Symlink root legal/changelog/icon assets (tos*.txt, privacy_en.txt, change_log_header.txt, honicon_*.png)
# into both /app/games/client/ and /app/games/client/linux-x64/ so :/ VFS paths resolve without 'Invalid file type' errors
for root_asset in \
  /opt/kongor-src/games/client/tos*.txt \
  /opt/kongor-src/games/client/privacy_en.txt \
  /opt/kongor-src/games/client/privacy*.txt \
  /opt/kongor-src/games/client/change_log_header.txt \
  /opt/kongor-src/games/client/change_log*.txt \
  /opt/kongor-src/games/client/honicon_*.png; do
  [ -f "$root_asset" ] || continue
  asset_bn="$(basename "$root_asset")"
  ln -sfn "$root_asset" "/app/games/client/$asset_bn"
  ln -sfn "$root_asset" "/app/games/client/linux-x64/$asset_bn"
done

# Synthesize container-local fallback honicon_{16x16,32x32,128x128}.png when Windows-sourced games/client/ lacks Linux X11 window icons
python3 - <<'PYICON'
import os, struct, zlib

def write_rgba_png(path, w, h):
    raw = (b'\x00' + (b'\x18\x22\x30\xff' * w)) * h
    def chunk(tag, data):
        return struct.pack('>I', len(data)) + tag + data + struct.pack('>I', zlib.crc32(tag + data) & 0xffffffff)
    ihdr = struct.pack('>IIBBBBB', w, h, 8, 6, 0, 0, 0)
    with open(path, 'wb') as f:
        f.write(b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', ihdr) + chunk(b'IDAT', zlib.compress(raw)) + chunk(b'IEND', b''))

synthesized = []
for w, h in ((16, 16), (32, 32), (128, 128)):
    name = f'honicon_{w}x{h}.png'
    root_png = f'/app/games/client/{name}'
    lin_png = f'/app/games/client/linux-x64/{name}'
    if not os.path.isfile(root_png):
        write_rgba_png(root_png, w, h)
        synthesized.append(name)
    if not os.path.exists(lin_png):
        if os.path.lexists(lin_png):
            os.unlink(lin_png)
        os.symlink(root_png, lin_png)
if synthesized:
    print(f"[boot] Synthesized container-local X11 window icon PNG fallbacks: {', '.join(synthesized)}")
PYICON

# Populate Linux client game/*.so before hon.sh merges ../../game/*.s2z into /app/games/client/linux-x64/game/
if [ -d "/opt/kongor-src/games/client/linux-x64/game" ]; then
  for gso in /opt/kongor-src/games/client/linux-x64/game/*; do
    [ -e "$gso" ] || continue
    ln -sfn "$gso" "/app/games/client/linux-x64/game/$(basename "$gso")"
  done
fi

# Copy hon-x86_64 (245 KB) instead of symlinking so readlink("/proc/self/exe") resolves inside /app/games/client/linux-x64
cp /opt/kongor-src/games/client/linux-x64/hon-x86_64 /app/games/client/linux-x64/hon-x86_64
cp /opt/kongor-src/games/client/linux-x64/hon.sh /app/games/client/linux-x64/hon.sh
chmod +x /app/games/client/linux-x64/hon-x86_64 /app/games/client/linux-x64/hon.sh

for cfg_path in \
  /app/games/client/linux-x64/userdata/".Heroes of Newerth"/startup.cfg \
  /app/games/client/linux-x64/userdata/".Heroes of Newerth"/game/startup.cfg \
  /app/games/client/linux-x64/userdata/".Heroes of Newerth"/override/startup.cfg; do
  cat > "$cfg_path" <<'EOF'
SetSave "vid_driver" "vid_gl2"
SetSave "host_vidDriver" "vid_gl2"
SetSave "vid_resolution" "1280,720"
SetSave "vid_fullscreen" "false"
SetSave "vid_borderless" "true"
SetSave "vid_textureDownsize" "0"
SetSave "vid_textureMaxSize" "2048"
SetSave "vid_textureCompression" "true"
SetSave "vid_sceneBuffer" "true"
SetSave "vid_shadows" "false"
SetSave "vid_postEffects" "false"
SetSave "vid_dynamicLights" "false"
SetSave "sound_disable" "true"
SetSave "login_useSRP" "true"
SetSave "upd_checkForUpdates" "false"
SetSave "upd_checkAtStartup" "false"
SetSave "con_writeLog" "true"
SetSave "con_flushLog" "true"
SetSave "sys_flushLogs" "true"
SetSave "host_masterServerAddress" "127.0.0.1:55555"
SetSave "masterServer" "127.0.0.1:55555"
SetSave "cg_newMainInterface" "true"
EOF
done

# Alias CHAT_SERVER_LAN_HOST on lo:0 if configured so SplitHorizonEndpointResolver LAN endpoints route locally
if [ -n "${KONGOR_CHAT_LAN_HOST:-}" ] && [ "${KONGOR_CHAT_LAN_HOST}" != "127.0.0.1" ] && [ "${KONGOR_CHAT_LAN_HOST}" != "localhost" ]; then
  python3 - "${KONGOR_CHAT_LAN_HOST}" <<'PYEOF' || true
import fcntl, socket, struct, sys
ip = sys.argv[1]
s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
ifreq = struct.pack('16sH2s4s8s', b'lo:0', socket.AF_INET, b'\x00\x00', socket.inet_aton(ip), b'\x00'*8)
fcntl.ioctl(s.fileno(), 0x8916, ifreq)
netmask = struct.pack('16sH2s4s8s', b'lo:0', socket.AF_INET, b'\x00\x00', socket.inet_aton('255.255.255.255'), b'\x00'*8)
fcntl.ioctl(s.fileno(), 0x891c, netmask)
flags = struct.pack('16sH14s', b'lo:0', 0x1 | 0x40, b'\x00'*14)
fcntl.ioctl(s.fileno(), 0x8914, flags)
print(f"[boot] Aliased {ip}/32 on lo:0 for container-local SplitHorizon UDP routing")
PYEOF
fi

# Start loopback TCP/UDP forwarders to host or Docker stack services
for tcp_port in 55555 5554 11031 5200 5266; do
  socat TCP4-LISTEN:${tcp_port},fork,reuseaddr TCP4:host.docker.internal:${tcp_port} >/tmp/socat_tcp_${tcp_port}.log 2>&1 &
done

DEDICATED_TARGET="host.docker.internal"
ARCADE_TARGET="host.docker.internal"
if python3 -c "import socket; socket.gethostbyname('kongor-stack-compel-dedicated')" >/dev/null 2>&1; then
  DEDICATED_TARGET="kongor-stack-compel-dedicated"
fi
if python3 -c "import socket; socket.gethostbyname('kongor-stack-compel-arcade')" >/dev/null 2>&1; then
  ARCADE_TARGET="kongor-stack-compel-arcade"
fi

# Local host dedicated servers (11234-11236, 21234-21236), Ranked / Dedicated worker servers (11334-11342, 11835-11836, 21334-21342), and Queue Arcade worker servers (11484-11492, 11985-11986, 21484-21492)
# SHJ-009-B21: Linux client hon-x86_64 (4.10.1.0) unconditionally prepends/expects a 40-byte S2 watermark (0xAAAA at bytes 38..40) and sends "4.10.1.0" in 0xC0 NETCMD_CONNECT, whereas 5.0.0.0 dedicated servers run with net_enableWatermark=false.
cat > /tmp/udp_proxy.py <<'PYEOF'
import os, select, socket

ded_host = os.environ.get('DEDICATED_TARGET', 'host.docker.internal')
arc_host = os.environ.get('ARCADE_TARGET', 'host.docker.internal')
lan_ip = os.environ.get('KONGOR_CHAT_LAN_HOST', '').strip()

try:
    ded_ip = socket.gethostbyname(ded_host)
except Exception:
    ded_ip = ded_host
try:
    arc_ip = socket.gethostbyname(arc_host)
except Exception:
    arc_ip = arc_host
try:
    host_ip = socket.gethostbyname('host.docker.internal')
except Exception:
    host_ip = '127.0.0.1'

bind_ips = ['127.0.0.1']
if lan_ip and lan_ip not in ('127.0.0.1', 'localhost') and lan_ip not in bind_ips:
    bind_ips.append(lan_ip)

targets = {}
forward_local_11235 = os.environ.get('KONGOR_FORWARD_LOCAL_11235', '0') == '1'
if forward_local_11235:
    for p in [11234, 11235, 11236, 21234, 21235, 21236]:
        targets[p] = (host_ip, p)
for p in [11334, 11335, 11336, 11337, 11338, 11339, 11340, 11341, 11342, 11835, 11836, 21334, 21335, 21336, 21337, 21338, 21339, 21340, 21341, 21342]:
    targets[p] = (ded_ip, p)
for p in [11484, 11485, 11486, 11487, 11488, 11489, 11490, 11491, 11492, 11985, 11986, 21484, 21485, 21486, 21487, 21488, 21489, 21490, 21491, 21492]:
    targets[p] = (arc_ip, p)

listen_socks = {}
for p, t in targets.items():
    for bip in bind_ips:
        try:
            s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            s.bind((bip, p))
            s.setblocking(False)
            listen_socks[s] = (bip, p, t)
        except Exception as e:
            print(f'[udp_proxy] Warn bind {bip}:{p}: {e}', flush=True)

client_map = {}
up_socks = {}
print(f'[udp_proxy] Bound {len(listen_socks)} UDP bridge sockets across {bind_ips} (ded={ded_ip}, arc={arc_ip})', flush=True)

while True:
    rlist = list(listen_socks.keys()) + list(up_socks.keys())
    readable, _, _ = select.select(rlist, [], [], 5.0)
    for s in readable:
        if s in listen_socks:
            bip, p, (thost, tport) = listen_socks[s]
            try:
                data, caddr = s.recvfrom(65535)
            except Exception:
                continue
            if len(data) > 40 and data[38:40] == b'\xaa\xaa':
                data = data[40:]
            if b'Heroes of Newerth\x004.10.1.0\x00' in data:
                data = data.replace(b'Heroes of Newerth\x004.10.1.0\x00', b'Heroes of Newerth\x005.0.0.0\x00')
            key = (s, caddr)
            usock = client_map.get(key)
            if usock is None:
                usock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
                usock.setblocking(False)
                client_map[key] = usock
                up_socks[usock] = (s, caddr, f'{bip}:{p}->{thost}:{tport}')
                print(f'[udp_proxy] [NEW_FLOW] client={caddr} via {bip}:{p} -> {thost}:{tport} ({len(data)}B)', flush=True)
            try:
                usock.sendto(data, (thost, tport))
            except Exception as e:
                print(f'[udp_proxy] [UP_ERR] {bip}:{p} -> {thost}:{tport}: {e}', flush=True)
        elif s in up_socks:
            lsock, caddr, label = up_socks[s]
            try:
                data, _ = s.recvfrom(65535)
                lsock.sendto((b'\x00' * 38) + b'\xaa\xaa' + data, caddr)
            except Exception as e:
                print(f'[udp_proxy] [DOWN_ERR] {label}: {e}', flush=True)
PYEOF
DEDICATED_TARGET="${DEDICATED_TARGET}" ARCADE_TARGET="${ARCADE_TARGET}" python3 /tmp/udp_proxy.py >/tmp/udp_proxy.log 2>&1 &
echo "[boot] Started socat TCP forwarders and Python 4.10.1.0<->5.0.0.0 UDP watermark bridge (dedicated=${DEDICATED_TARGET}, arcade=${ARCADE_TARGET})"

# Start virtual X11 framebuffer (100% offscreen; Xvfb uses Mesa llvmpipe automatically without LIBGL_ALWAYS_SOFTWARE=1)
Xvfb :99 -screen 0 1280x720x24 -ac +extension GLX +render -noreset >/tmp/xvfb.log 2>&1 &
sleep 1

export DISPLAY=:99
export GALLIUM_DRIVER=llvmpipe
export LP_NUM_THREADS=2
export KONGOR_FORCE_FULLRES=0
export KONGOR_IPC_SOCKET_DIR=/tmp/kongor-0
export KONGOR_NATIVE_PIPE=kongor-linux-client

LOCAL_SVR_PORT=11235
if [ "${KONGOR_FORWARD_LOCAL_11235:-0}" = "1" ]; then
  LOCAL_SVR_PORT=11399
fi

ExecArgs=(
  -writeconsole
  -masterserver "127.0.0.1:55555"
  -execute "Set net_enableWatermark false; Set login_useSRP true; Set con_writeLog true; Set con_flushLog true; Set svr_port ${LOCAL_SVR_PORT}; Set vid_textureDownsize ${KONGOR_LINUX_TEX_DOWNSIZE:-2}; Set vid_textureMaxSize 2048; Set vid_textureCompression true; Set vid_sceneBuffer true; Set vid_shadows false; Set vid_postEffects false; Set vid_dynamicLights false; Set host_maxFPS 20; Set vid_maxFPS 20"
)

/app/games/client/linux-x64/hon.sh "${ExecArgs[@]}" >/tmp/hon_stdout.log 2>&1 &
HON_PID=$!
echo "$HON_PID" >/tmp/hon.pid
wait "$HON_PID"
'@

        $bootScriptUnix = $bootScript -replace "`r`n", "`n"
        $bootFile = Join-Path $env:TEMP "kongor_linux_journey_boot.sh"
        [System.IO.File]::WriteAllBytes($bootFile, [System.Text.Encoding]::UTF8.GetBytes($bootScriptUnix))

        $chatLanHost = ""
        $envInspect = Invoke-DockerQuiet -ArgsList @("inspect", "-f", "{{range .Config.Env}}{{println .}}{{end}}", "kongor-stack-chat-server")
        if ($envInspect.ExitCode -eq 0 -and $envInspect.Output -match '(?m)^CHAT_SERVER_LAN_HOST=(.+)$') {
            $chatLanHost = $Matches[1].Trim()
        }
        $texDownsize = if ($FullRes) { "0" } else { "2" }
        $netCheck = Invoke-DockerQuiet -ArgsList @("network", "inspect", "kongor-stack_default")
        $forwardLocal11235 = if ($ForwardLocalHostServer -or ($netCheck.ExitCode -ne 0)) { "1" } else { "0" }
        $dockerRunArgs = @(
            "run", "-d",
            "--name", $ContainerName,
            "--cap-add=NET_ADMIN",
            "--cpus=2.0",
            "--memory=4096m",
            "--memory-swap=4600m",
            "--shm-size=256m",
            "-e", "KONGOR_LINUX_TEX_DOWNSIZE=$texDownsize",
            "-e", "KONGOR_FORWARD_LOCAL_11235=$forwardLocal11235",
            "--add-host=host.docker.internal:host-gateway"
        )
        if ($netCheck.ExitCode -eq 0) {
            $dockerRunArgs += @("--network", "kongor-stack_default")
        }
        if (-not [string]::IsNullOrWhiteSpace($chatLanHost)) {
            $dockerRunArgs += @("-e", "KONGOR_CHAT_LAN_HOST=$chatLanHost")
        }
        $dockerRunArgs += @(
            "-v", "${GamesDir}:/opt/kongor-src/games:ro",
            "-v", "${bootFile}:/opt/boot_journey.sh:ro",
            $ImageTag,
            "/bin/bash", "/opt/boot_journey.sh"
        )

        Write-LinuxHarnessLog "Starting isolated headless Linux client container '$ContainerName' (Xvfb :99 1280x720x24, lanAlias='$chatLanHost')..."
        $runRes = Invoke-DockerQuiet -ArgsList $dockerRunArgs

        if ($runRes.ExitCode -ne 0) {
            throw "Failed to launch Docker container '$ContainerName': $($runRes.Output)"
        }

        $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
        $ready = $false
        while ([DateTime]::UtcNow -lt $deadline) {
            $check = Invoke-DockerQuiet -ArgsList @("exec", $ContainerName, "bash", "-c", "test -S /tmp/kongor-0/kongor-linux-client.sock && grep -q -E 'Created new client instance #0|autoexec\.cfg|cg_newMainInterface' /tmp/hon_stdout.log '/app/games/client/linux-x64/userdata/.Heroes of Newerth/override/console.log' 2>/dev/null && echo READY")
            if ($check.ExitCode -eq 0 -and $check.Output.Trim() -eq "READY") {
                $ready = $true
                break
            }
            $alive = Invoke-DockerQuiet -ArgsList @("inspect", "-f", "{{.State.Running}}", $ContainerName)
            if ($alive.Output.Trim() -ne "true") {
                $logs = Invoke-DockerQuiet -ArgsList @("logs", "--tail", "60", $ContainerName)
                throw "Container '$ContainerName' exited prematurely during boot: $($logs.Output)"
            }
            Start-Sleep -Seconds 2
        }

        if (-not $ready) {
            $stdoutTail = Invoke-DockerQuiet -ArgsList @("exec", $ContainerName, "bash", "-c", "tail -n 50 '/app/games/client/linux-x64/userdata/.Heroes of Newerth/override/console.log' /tmp/hon_stdout.log 2>/dev/null || true")
            throw "Linux client did not reach Main UI + IPC socket readiness within ${TimeoutSeconds}s. Recent console.log:`n$($stdoutTail.Output)"
        }

        $evalCheck = Invoke-LinuxEvalLuaInternal -LuaCode "return tostring(GetCvarString('host_version')) .. '|phase=' .. tostring(GetGamePhase and GetGamePhase() or 0)" -WaitMs 6000
        Write-LinuxHarnessLog "Linux hon-x86_64 is LIVE and IPC-ready ($evalCheck)."
    }

    "login" {
        $ign = ""
        $sec = ""
        if (-not [string]::IsNullOrWhiteSpace($Username) -or -not [string]::IsNullOrWhiteSpace($Password)) {
            $overrideAcct = Resolve-LinuxLocalDevAccount
            $ign = $overrideAcct.Ign
            $sec = $overrideAcct.Cred
            Write-LinuxHarnessLog "Using explicit login account override IGN='$ign' (credentialSource=$($overrideAcct.Source))."
        }
        elseif (Test-Path $AccountStateFile) {
            $acct = Get-Content -Raw -Path $AccountStateFile | ConvertFrom-Json
            $ign = [string]$acct.ign
            $sec = [string]$acct.secret
        }
        else {
            throw "No provisioned Linux journey account found at '$AccountStateFile'. Run 'scripts/docker/invoke_linux_client_journey_container.ps1 -Action provision-account' first, pass -Username/-Password, or run '-Action enter-offline-hub' for zero-backend UI journeys."
        }

        Write-LinuxHarnessLog "Authenticating Linux hon-x86_64 as '$ign' via SRP..."
        $safeIgn = $ign.Replace("\", "\\").Replace("'", "\'")
        $md5Sec = ConvertTo-K2PasswordMd5 -InputCredential $sec
        $loginLua = @"
SetSave('login_useSRP', 'true', 'bool')
Set('login_name', '$safeIgn', 'string')
Set('login_password', '$md5Sec', 'string')
if type(Cmd) == 'function' then
    Cmd('Login')
elseif type(Login) == 'function' then
    Login('$safeIgn', '$md5Sec')
end
return 'LOGIN_DISPATCHED:' .. '$safeIgn'
"@
        [void](Invoke-LinuxEvalLuaInternal -LuaCode $loginLua -WaitMs 6000)

        $deadline = [DateTime]::UtcNow.AddSeconds(35)
        $loggedIn = $false
        $lastProbe = ""
        while ([DateTime]::UtcNow -lt $deadline) {
            $lastProbe = Invoke-LinuxEvalLuaInternal -LuaCode @"
local topBar = GetWidget and GetWidget('main_top_bar', 'main')
local topVis = topBar and topBar:IsVisible() or false
local me = GetIdentID and tostring(GetIdentID()) or ''
local logged = (type(IsLoggedIn) == 'function' and IsLoggedIn()) or false
local chatConn = (ChatClient and type(ChatClient.IsConnected) == 'function' and ChatClient.IsConnected()) or false
return 'logged=' .. tostring(logged) .. '|chat=' .. tostring(chatConn) .. '|topVis=' .. tostring(topVis) .. '|ident=' .. me
"@ -WaitMs 5000
            if ($lastProbe -match "OK\|logged=true") {
                $loggedIn = $true
                break
            }
            Start-Sleep -Seconds 2
        }

        if (-not $loggedIn) {
            throw "Linux client failed to complete SRP + ChatServer login for '$ign' (lastProbe=$lastProbe). Check 'scripts/docker/invoke_linux_client_journey_container.ps1 -Action tail-log' or use '-Action enter-offline-hub' for offline UI journeys."
        }

        Write-LinuxHarnessLog "Authenticated '$ign' cleanly ($lastProbe)."
        Invoke-LinuxDismissOnboardingModal -AccountLabel $ign
        Write-Output $lastProbe
    }

    "enter-offline-hub" {
        $offlineNick = if (-not [string]::IsNullOrWhiteSpace($Username)) {
            $Username.Trim()
        }
        elseif (Test-Path $AccountStateFile) {
            try {
                $cachedAcct = Get-Content -Raw -Path $AccountStateFile | ConvertFrom-Json
                if ($cachedAcct.ign) { [string]$cachedAcct.ign } else { "KongorPlayer" }
            }
            catch {
                "KongorPlayer"
            }
        }
        elseif (-not [string]::IsNullOrWhiteSpace($env:KONGOR_TEST_USER)) {
            $env:KONGOR_TEST_USER.Trim()
        }
        else {
            "KongorPlayer"
        }

        $safeOfflineNick = $offlineNick.Replace("\", "\\").Replace("'", "\'")
        Write-LinuxHarnessLog "Entering Offline Main Menu Hub as '$offlineNick' via HoN_Main:EnterOfflineHub() (zero backend dependencies)..."

        $enterHubLua = @"
Set('ui_suppressAutoLogin', 'true', 'bool')
Set('login_name', '$safeOfflineNick', 'string')
Set('p2p_nickname', '$safeOfflineNick', 'string')
if HoN_Main and type(HoN_Main.EnterOfflineHub) == 'function' then
    HoN_Main:EnterOfflineHub()
elseif type(_G.EnterOfflineHub) == 'function' then
    _G.EnterOfflineHub('linux_journey_ipc')
else
    error('HoN_Main:EnterOfflineHub is not loaded in the current K2 UI context')
end
return 'OFFLINE_HUB_DISPATCHED:' .. '$safeOfflineNick'
"@
        $dispatchRes = Invoke-LinuxEvalLuaInternal -LuaCode $enterHubLua -WaitMs 6000
        if ($dispatchRes -match '^ERR\|') {
            throw "Failed to dispatch HoN_Main:EnterOfflineHub() inside '$ContainerName' ($dispatchRes). Run 'scripts/docker/invoke_linux_client_journey_container.ps1 -Action tail-log' to inspect UI load state."
        }

        $hubDeadline = [DateTime]::UtcNow.AddSeconds(20)
        $hubReady = $false
        $lastHubProbe = ""
        while ([DateTime]::UtcNow -lt $hubDeadline) {
            $lastHubProbe = Invoke-LinuxEvalLuaInternal -LuaCode @"
local topBar = GetWidget and GetWidget('main_top_bar', 'main')
local topVis = topBar and topBar:IsVisible() or false
local offCvar = (type(GetCvarBool) == 'function' and GetCvarBool('kongor_offline_hub')) or false
local uiLogged = (_G.isLoggedIn == true) or (HoN_Main and HoN_Main.isFullyLoggedIn == true) or false
local errDlg = GetWidget and GetWidget('login_error_dialog', 'main')
local errVis = errDlg and errDlg:IsVisible() or false
local nick = (type(GetCvarString) == 'function' and GetCvarString('p2p_nickname')) or ''
return string.format('offlineHub=%s|uiLogged=%s|topVis=%s|errVis=%s|nick=%s', tostring(offCvar), tostring(uiLogged), tostring(topVis), tostring(errVis), tostring(nick))
"@ -WaitMs 5000
            if ($lastHubProbe -match "OK\|offlineHub=true\|uiLogged=true") {
                $hubReady = $true
                break
            }
            Start-Sleep -Milliseconds 500
        }

        if (-not $hubReady) {
            throw "Linux client did not settle into Offline Main Menu Hub within 20s (lastProbe=$lastHubProbe). Run 'scripts/docker/invoke_linux_client_journey_container.ps1 -Action tail-log' to diagnose."
        }

        Write-LinuxHarnessLog "Entered Offline Main Menu Hub cleanly ($lastHubProbe)."
        Invoke-LinuxDismissOnboardingModal -AccountLabel $offlineNick
        Write-Output $lastHubProbe
    }

    "status" {
        $rawState = Invoke-LinuxSocketFrame -OpCode 5 -Payload ""
        $luaState = Invoke-LinuxEvalLuaInternal -LuaCode @"
local gp = (type(GetGamePhase) == 'function' and GetGamePhase()) or -1
local logged = (type(IsLoggedIn) == 'function' and IsLoggedIn()) or false
local offHub = (type(GetCvarBool) == 'function' and GetCvarBool('kongor_offline_hub')) or false
local inQ = (_G.TacticalHud and _G.TacticalHud.CheckIsInQueue and _G.TacticalHud.CheckIsInQueue()) or (type(IsInQueue) == 'function' and IsInQueue()) or false
local mid = (type(GetShowStatsMatchID) == 'function' and tostring(GetShowStatsMatchID())) or '0'
local accDlg = GetWidget and GetWidget('ingame_matchmaker_accept_game_dialog', 'game')
local accVis = accDlg and accDlg:IsVisible() or false
local rcDlg = GetWidget and GetWidget('ready_check_root', 'main_popup')
local rcVis = rcDlg and rcDlg:IsVisible() or false
return string.format('phase=%s|logged=%s|inQueue=%s|matchId=%s|ingameAcceptVis=%s|feReadyVis=%s|offlineHub=%s', tostring(gp), tostring(logged), tostring(inQ), mid, tostring(accVis), tostring(rcVis), tostring(offHub))
"@ -WaitMs 5000
        Write-Output (@{ native = ($rawState | ConvertFrom-Json); lua = $luaState } | ConvertTo-Json -Compress -Depth 5)
    }

    { $_ -in @("eval", "eval-lua") } {
        if ([string]::IsNullOrWhiteSpace($Lua)) {
            throw "Parameter -Lua is required for -Action eval-lua."
        }
        $res = Invoke-LinuxEvalLuaInternal -LuaCode $Lua -WaitMs ($TimeoutSeconds * 1000)
        Write-Output $res
    }

    "cmd" {
        if ([string]::IsNullOrWhiteSpace($Command)) {
            throw "Parameter -Command is required for -Action cmd."
        }
        $res = Invoke-LinuxSocketFrame -OpCode 2 -Payload $Command
        Write-Output $res
    }

    "capture" {
        if ([string]::IsNullOrWhiteSpace($OutputPath)) {
            throw "Parameter -OutputPath is required for -Action capture."
        }
        $parent = Split-Path -Parent $OutputPath
        if ($parent -and -not (Test-Path $parent)) {
            [void](New-Item -ItemType Directory -Path $parent -Force)
        }
        $capRes = Invoke-DockerQuiet -ArgsList @("exec", $ContainerName, "bash", "-c", "ffmpeg -loglevel error -y -f x11grab -video_size 1280x720 -i :99 -frames:v 1 /tmp/linux_capture.png")
        if ($capRes.ExitCode -ne 0) {
            throw "ffmpeg x11grab failed inside '$ContainerName': $($capRes.Output). Verify Xvfb :99 is running."
        }
        $cpRes = Invoke-DockerQuiet -ArgsList @("cp", "${ContainerName}:/tmp/linux_capture.png", $OutputPath)
        if ($cpRes.ExitCode -ne 0 -or -not (Test-Path $OutputPath)) {
            throw "Expected screenshot file was not written at '$OutputPath' ($($cpRes.Output))."
        }
        Write-LinuxHarnessLog "Captured offscreen Xvfb :99 screenshot -> '$OutputPath'."
    }

    "tail-log" {
        $tailCmd = "if [ -f '/app/games/client/linux-x64/userdata/.Heroes of Newerth/override/console.log' ]; then tail -n $Lines '/app/games/client/linux-x64/userdata/.Heroes of Newerth/override/console.log' 2>/dev/null; else tail -n $Lines /tmp/hon_stdout.log 2>/dev/null; fi || true"
        $tailRes = Invoke-DockerQuiet -ArgsList @("exec", $ContainerName, "bash", "-c", $tailCmd)
        $rawLines = $tailRes.Lines
        if (-not [string]::IsNullOrWhiteSpace($Filter)) {
            $rawLines = $rawLines | Where-Object { "$_" -match $Filter }
        }
        $rawLines | ForEach-Object { Write-Output "$_" }
    }

    "stop" {
        Write-LinuxHarnessLog "Stopping headless Linux client container '$ContainerName'..."
        [void](Invoke-DockerQuiet -ArgsList @("rm", "-f", $ContainerName))
        if (Test-Path $AccountStateFile) {
            Remove-Item -Path $AccountStateFile -Force -ErrorAction SilentlyContinue
        }
        Write-LinuxHarnessLog "Container '$ContainerName' stopped and ephemeral credentials cleaned."
    }
}
