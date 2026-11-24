# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

param(
    [string]$Hero = "Hero_Pyromancer",
    [string]$Map = "caldavar",
    [int]$Width = 1920,
    [int]$Height = 1080,
    [string]$WindowMode = "offscreen",
    [switch]$NoWait,
    [switch]$UseWmi,
    [switch]$DebugUiScript,
    [string]$DebugUiScriptInterface = "",
    [string]$DebugUiScriptWidget = "",
    [switch]$DebugHoverWidget,
    [switch]$DynamicResReload
)

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$ClientDir = Join-Path $RepoRoot "games\client"
$ExePath = Join-Path $ClientDir "hon_x64.exe"

# Singleton Game-Worker Mutex Guard: block unauthorized offline workers or conflicting game-worker locks
$workerLockScript = Join-Path $PSScriptRoot "kongor_worker_lock.ps1"
if (Test-Path -LiteralPath $workerLockScript) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File $workerLockScript -Action assert
    if ($LASTEXITCODE -ne 0) {
        Write-Error "[WORKER-LOCK BLOCKED] Cannot launch practice client while game-worker mutex check failed. Run: .\scripts\kongor_worker_lock.ps1 -Action status"
        exit 1
    }
}

# Graceful Quit-Before-Kill & 1-Thread Windowless Kernel Zombie Guard (Rec #9 / FN-008)
$stopCleanHelper = Join-Path $PSScriptRoot "lib\Stop-KongorGameCleanly.ps1"
if (Test-Path -LiteralPath $stopCleanHelper) {
    . $stopCleanHelper
    $existingHon = Get-Process -Name "hon_x64" -ErrorAction SilentlyContinue
    if ($existingHon) {
        Write-Host "[LAUNCH-GUARD] Existing hon_x64 process detected (PID: $($existingHon.Id -join ', ')); executing graceful quit-before-kill (FN-008)..." -ForegroundColor Cyan
        [void](Stop-KongorGameCleanly -ProcessName "hon_x64" -GracefulTimeoutMs 1500)
    }
}

if ($WindowMode -eq "offscreen") {
    $env:KONGOR_HEADLESS = "1"
    $overrideCfgDir = Join-Path $ClientDir "userdata\Heroes of Newerth x64\override"
    if (-not (Test-Path -LiteralPath $overrideCfgDir)) {
        New-Item -ItemType Directory -Path $overrideCfgDir -Force | Out-Null
    }
    $headlessCfgPath = Join-Path $overrideCfgDir "startup_headless.cfg"
    $foregroundCfgPath = Join-Path $overrideCfgDir "startup.cfg"
    if ((-not (Test-Path -LiteralPath $headlessCfgPath)) -and (Test-Path -LiteralPath $foregroundCfgPath)) {
        Copy-Item -LiteralPath $foregroundCfgPath -Destination $headlessCfgPath -Force
        Write-Host "[HEADLESS-CFG] Seeded isolated startup_headless.cfg from startup.cfg" -ForegroundColor Cyan
    }
} else {
    $env:KONGOR_HEADLESS = "0"
}

# Set Vulkan layer environment variables
$env:VK_LAYER_PATH = "$ClientDir"
$env:VK_INSTANCE_LAYERS = "VK_LAYER_HON_replay_capture"
$env:VK_LOADER_LAYERS_ENABLE = "*replay_capture*,VK_LAYER_HON_replay_capture"
$env:HON_DEVTOOLS = "1"
$env:HON_MODERN_HUD = "1"
$env:HON_CAPTURE_ENABLE = "0"
$env:HON_CAPTURE_SLOTS = "3"

# Synchronize HUD assets
if (Test-Path "$RepoRoot\assets\hud") {
    Copy-Item -Path "$RepoRoot\assets\hud\*" -Destination "$ClientDir\assets\hud\" -Force -Recurse -ErrorAction SilentlyContinue
}

# Synchronize Replays to engine canonical directory (userdata/Heroes of Newerth x64/game/replays)
$canonicalReplayDir = Join-Path $ClientDir "userdata\Heroes of Newerth x64\game\replays"
if (-not (Test-Path $canonicalReplayDir)) {
    New-Item -ItemType Directory -Path $canonicalReplayDir -Force | Out-Null
    Write-Host "[REPLAY-SYNC] Created canonical replay directory: $canonicalReplayDir" -ForegroundColor Cyan
}

$replaySources = @(
    (Join-Path $ClientDir "userdata\game\replays"),
    (Join-Path $ClientDir "userdata\replays"),
    (Join-Path $ClientDir "replays"),
    (Join-Path $ClientDir "game\replays")
)

foreach ($rSource in $replaySources) {
    if (Test-Path $rSource) {
        $replays = Get-ChildItem -Path $rSource -Filter "*.honreplay" -File -ErrorAction SilentlyContinue
        foreach ($r in $replays) {
            $dest = Join-Path $canonicalReplayDir $r.Name
            if (-not (Test-Path $dest)) {
                Copy-Item -Path $r.FullName -Destination $dest -Force
                Write-Host "[REPLAY-SYNC] Synchronized replay $($r.Name) from $rSource to $dest" -ForegroundColor Green
            }
        }
    }
}

# Do not overwrite autoexec.cfg - preserve engine and telemetry configurations

$vfsConfigPath = Join-Path $RepoRoot "config\vfs_mounts.json"
$canonicalModString = if (Test-Path $vfsConfigPath) {
    $vfsJson = Get-Content $vfsConfigPath -Raw | ConvertFrom-Json
    "-mod " + $vfsJson.mod_param_string
} else {
    "-mod base;game;ClientPackageFiles;KONGOR/configuration;KONGOR/updates;KONGOR/extensions;override"
}

$chatListening = $false
try {
    $tcp = New-Object System.Net.Sockets.TcpClient
    $iar = $tcp.BeginConnect("127.0.0.1", 11031, $null, $null)
    $chatListening = $iar.AsyncWaitHandle.WaitOne(50, $false)
    if ($chatListening) { $tcp.EndConnect($iar) }
    $tcp.Close()
} catch {
    Write-Verbose -Message "Chat server port 11031 probe skipped: $($_.Exception.Message)"
}

$chatArgs = if ($chatListening) {
    @(
        "-chatserver 127.0.0.1:11031",
        "+Set chat_serverAddrOverride 127.0.0.1",
        "+Set chat_serverPortOverride 11031"
    )
} else {
    @(
        "+Set chat_serverAddrOverride 127.0.0.1",
        "+Set chat_serverPortOverride 11031",
        "+Set chat_connectTimeout 1",
        "+Set chat_maxReconnectAttempts 0"
    )
}

$dynamicReloadFlag = if ($DynamicResReload) { "true" } else { "false" }
$telemetryArgs = @()
if ($DynamicResReload) {
    Write-Host -Object "[TELEMETRY] Enabled host_dynamicResReload=true for live .entity/resource hot-reload." -ForegroundColor Cyan
}
$enableScriptTrace = $DebugUiScript -or (-not [string]::IsNullOrWhiteSpace($DebugUiScriptInterface)) -or (-not [string]::IsNullOrWhiteSpace($DebugUiScriptWidget))
if ($enableScriptTrace) {
    $telemetryArgs += "+Set ui_debugScript true"
    if (-not [string]::IsNullOrWhiteSpace($DebugUiScriptInterface)) {
        $telemetryArgs += "+Set ui_debugScriptInterface `"$DebugUiScriptInterface`""
    }
    if (-not [string]::IsNullOrWhiteSpace($DebugUiScriptWidget)) {
        $telemetryArgs += "+Set ui_debugScriptWidget `"$DebugUiScriptWidget`""
    }
    if ([string]::IsNullOrWhiteSpace($DebugUiScriptInterface) -and [string]::IsNullOrWhiteSpace($DebugUiScriptWidget)) {
        Write-Warning -Message "[TELEMETRY] ui_debugScript=true enabled globally without -DebugUiScriptInterface or -DebugUiScriptWidget filter; console.log volume will be high."
    } else {
        Write-Host -Object "[TELEMETRY] Enabled scoped ui_debugScript=true (interface='$DebugUiScriptInterface', widget='$DebugUiScriptWidget')." -ForegroundColor Cyan
    }
}
if ($DebugHoverWidget) {
    $telemetryArgs += "+Set ui_debugHoverWidget true"
    Write-Host -Object "[TELEMETRY] Enabled ui_debugHoverWidget=true for live cursor widget inspection." -ForegroundColor Cyan
}

$rawArgs = @(
    "-masterserver 127.0.0.1:55555",
    "-webserver 127.0.0.1:55555",
    "-messageserver 127.0.0.1:55555"
) + $chatArgs + @(
    $canonicalModString,
    "+Set host_dynamicResReload $dynamicReloadFlag",
    "-writeconsole",
    "-logfile console.log",
    "+Set log_flush false",
    "+Set con_flushLog false",
    "+Set sys_flushLogs false",
    "+Set con_log 1",
    "+Set host_writeConsole false",
    "-pipe willowmaker-ipc",
    "+Set host_maxFPS 1000",
    "+Set host_maximumFPS 1000",
    "+Set host_sleep 0",
    "+Set host_sleepMS 0",
    "+Set cl_maxFPS 1000",
    "+Set vid_maxFPS 1000",
    "+Set sys_sleepTime 0",
    "+Set d3d_flush 0",
    "+Set d3d_presentInterval 0",
    "+Set vid_vsync false",
    "+Set gl_swapInterval 0",
    "+Set d3d_tripleBuffering false",
    "+Set d3d_pure true",
    "+Set vid_driver vid_vulkan",
    "+Set vid_textureCompression true",
    "+Set sound_numChannels 64",
    "+Set sound_bufferLength 256",
    "+Set sound_mixRate 48000",
    "+Set sound_recording_mixRate 48000",
    "+Set releaseStage_dev true",
    "+Set releaseStage_test true",
    "+Set releaseStage_stable false",
    "+Set ui_dev true",
    "+Set ui_dev_button true",
    "+Set ui_showQuickStart true",
    "+Set ui_canCreateGame true",
    "+Set ui_background 2",
    "+Set cg_menuTransitions true",
    "+Set dev true",
    "+Set cheats true",
    "+Set vid_resolution `"$($Width),$($Height)`"",
    "+Set vid_width $Width",
    "+Set vid_height $Height",
    "+Set vid_bpp 32",
    "+Set vid_refreshRate 144",
    "+Set vid_fullscreen false",
    "+Set vid_borderless true",
    "+Set d3d_exclusive false",
    $(if ($WindowMode -eq "offscreen") { "-x -32000 -y -32000 +Set vid_pos -32000 -32000" } else { "+Set vid_pos 0 0" }),
    $(if ($WindowMode -eq "offscreen") { "+Set in_mouseRaw false" } else { "+Set in_mouseRaw true" }),
    $(if ($WindowMode -eq "offscreen") { "+Set host_startupCfg `"~/startup_headless.cfg`"" } else { "+Set cg_constrainCursor true" }),
    "+Set vid_textureDownsize 0",
    "+Set vid_sceneBufferMipmap false",
    "+Set vid_postEffectMipmaps false",
    "+Set map $Map",
    "+Set default_hero $Hero",
    "+Set sv_heroSelectTime 3600",
    "+Set sv_heroSelectInitialTime 3600",
    "+Set sv_preMatchTime 3600",
    "+Set sv_matchStartAnnounceTime 3600",
    "+Set sv_gameStartCountdown 3600",
    "+Set sv_gameCountdownLength 3600",
    '+Set svr_login ""',
    "+Set svr_broadcastLocal false",
    "+Set svr_port 11580",
    "+Set svr_voicePortStart 12080",
    "+Set svr_voicePortEnd 12080",
    "+Set svr_proxyPort 0",
    "+Set man_startServerPort 0",
    "+Set svr_gameFPS 20",
    "+Set svr_maxFramesPerHostFrame 5",
    "+Set sv_precacheEntities false",
    "+Set ui_quickStart true"
) + $telemetryArgs + @(
    "+StartGame practice PracticeGame `"map:$Map allheroes:true`""
)

$joinedArgs = $rawArgs -join " "

Write-Host "Launching $ExePath on WinSta0\Default with Vulkan RmlUi DevTools overlay ($Width x $Height)..."

Add-Type @'
using System;
using System.Runtime.InteropServices;

public class WinStaLauncher {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CreateProcess(
        string lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string lpCurrentDirectory,
        [In] ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll")]
    public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    public static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool IsProcessInJob(IntPtr ProcessHandle, IntPtr JobHandle, out bool Result);

    public static int LaunchOnDefaultDesktop(string exePath, string cmdLine, string workDir, bool wait, out bool isTrappedInJob) {
        isTrappedInJob = false;
        STARTUPINFO si = new STARTUPINFO();
        si.cb = Marshal.SizeOf(si);
        si.lpDesktop = @"WinSta0\Default";
        si.dwFlags = 1; // STARTF_USESHOWWINDOW
        si.wShowWindow = 1; // SW_SHOWNORMAL

        PROCESS_INFORMATION pi;
        string fullCmd = "\"" + exePath + "\" " + cmdLine;
        uint flags = 0x01000000; // CREATE_BREAKAWAY_FROM_JOB
        bool ok = CreateProcess(null, fullCmd, IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero, workDir, ref si, out pi);
        if (!ok) {
            ok = CreateProcess(null, fullCmd, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, workDir, ref si, out pi);
        }
        if (!ok) {
            int err = Marshal.GetLastWin32Error();
            Console.WriteLine("CreateProcess failed with error: " + err);
            return -1;
        }

        try {
            IsProcessInJob(pi.hProcess, IntPtr.Zero, out isTrappedInJob);
        } catch {}

        Console.WriteLine("Spawned hon_x64 PID " + pi.dwProcessId + " on WinSta0\\Default (TrappedInJob: " + isTrappedInJob + ").");
        if (!wait) {
            CloseHandle(pi.hProcess);
            CloseHandle(pi.hThread);
            return (int)pi.dwProcessId;
        }
        WaitForSingleObject(pi.hProcess, 0xFFFFFFFF);
        uint exitCode = 0;
        GetExitCodeProcess(pi.hProcess, out exitCode);
        CloseHandle(pi.hProcess);
        CloseHandle(pi.hThread);
        return (int)exitCode;
    }
}
'@

if ($UseWmi) {
    Write-Host "[LAUNCH] Spawning hon_x64 via WMI (Win32_Process) on WinSta0\Default..." -ForegroundColor Cyan
    $wmiRes = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
        CommandLine = "`"$ExePath`" $joinedArgs"
        CurrentDirectory = $ClientDir
    }
    if ($wmiRes.ReturnValue -eq 0) {
        $exitCode = [int]$wmiRes.ProcessId
        Write-Host "Game Client spawned via WMI (PID: $exitCode)." -ForegroundColor Green
    } else {
        Write-Error "WMI Process Create failed with return value: $($wmiRes.ReturnValue)"
    }
} else {
    [bool]$isTrappedInJob = $false
    $exitCode = [WinStaLauncher]::LaunchOnDefaultDesktop($ExePath, $joinedArgs, $ClientDir, (-not $NoWait), [ref]$isTrappedInJob)

    if ($NoWait) {
        if ($isTrappedInJob) {
            Write-Warning "[JOB_GUARD] Process $exitCode is trapped in caller's Job Object and would be terminated on exit. Escaping to WMI (Win32_Process)..."
            Stop-Process -Id $exitCode -Force -ErrorAction SilentlyContinue
            Start-Sleep -Milliseconds 400

            $wmiRes = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
                CommandLine = "`"$ExePath`" $joinedArgs"
                CurrentDirectory = $ClientDir
            }
            if ($wmiRes.ReturnValue -eq 0) {
                $exitCode = [int]$wmiRes.ProcessId
                Write-Host "[WMI_LAUNCH] Game Client successfully spawned outside Job Object (PID: $exitCode)." -ForegroundColor Green
            } else {
                Write-Error "[WMI_LAUNCH] Failed to spawn via WMI. ReturnValue: $($wmiRes.ReturnValue). Run 'schtasks /run' or a long-lived host."
            }
        } else {
            Write-Host "Game Client launched in background (PID: $exitCode)." -ForegroundColor Green
        }
    } else {
        Start-Sleep -Milliseconds 1500
        $runningHon = Get-Process hon_x64 -ErrorAction SilentlyContinue
        if ($runningHon) {
            Write-Host "Tracking active hon_x64 instance (PID: $($runningHon.Id -join ', '))..."
            $runningHon | Wait-Process
        }
        Write-Host "Game Client finished session."
    }
}

