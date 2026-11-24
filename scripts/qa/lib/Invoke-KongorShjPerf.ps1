# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Runtime Fingerprint Drift Audit (I7) and Adaptive Competitive High-Hz Perf Canary (I5)
    library for scripts/qa/kongor_shj.ps1.
#>

Set-StrictMode -Version Latest

if (-not ("KongorShjWin32V3" -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public sealed class KongorWindowProbeResultV3
{
    public IntPtr Hwnd;
    public string DesktopName;
    public string ClassName;
    public string Title;
    public string Method;
    public int CandidateCount;
    public string CurrentThreadDesktop;
    public bool DefaultDesktopAccessible;
    public bool GotRect;
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
    public bool IsVisible;
    public bool IsForeground;
}

public static class KongorShjWin32V3
{
    private const uint DESKTOP_ALL_READ = 0x01FF;
    private const uint DESKTOP_READOBJECTS = 0x0001;
    private const int UOI_NAME = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern IntPtr GetThreadDesktop(uint dwThreadId);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr OpenDesktop(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetThreadDesktop(IntPtr hDesktop);

    [DllImport("user32.dll")]
    public static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetUserObjectInformation(IntPtr hObj, int nIndex, StringBuilder pvInfo, int nLength, out int lpnLengthNeeded);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumThreadWindows(uint dwThreadId, EnumWindowsProc lpfn, IntPtr lParam);

    public static string GetDesktopName(IntPtr hDesk)
    {
        if (hDesk == IntPtr.Zero) return "unknown";
        StringBuilder sb = new StringBuilder(256);
        int needed = 0;
        if (GetUserObjectInformation(hDesk, UOI_NAME, sb, sb.Capacity, out needed))
        {
            return sb.ToString();
        }
        return "unknown";
    }

    public static string GetCurrentDesktopName()
    {
        return GetDesktopName(GetThreadDesktop(GetCurrentThreadId()));
    }

    public static string ReadClassName(IntPtr hWnd)
    {
        StringBuilder sb = new StringBuilder(256);
        int len = GetClassName(hWnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString(0, len) : string.Empty;
    }

    public static string ReadTitle(IntPtr hWnd)
    {
        StringBuilder sb = new StringBuilder(256);
        int len = GetWindowText(hWnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString(0, len) : string.Empty;
    }

    private static bool IsDummyWindow(string cls, string title)
    {
        if (!string.IsNullOrEmpty(title))
        {
            if (title.IndexOf("dummy", StringComparison.OrdinalIgnoreCase) >= 0 ||
                title.IndexOf("fodder", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }
        if (!string.IsNullOrEmpty(cls) && cls.IndexOf("dummy", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }
        return false;
    }

    private static int ScoreWindow(IntPtr hWnd, string cls, string title, out RECT outRect, out bool gotRect)
    {
        outRect = new RECT();
        gotRect = GetWindowRect(hWnd, out outRect);
        if (IsDummyWindow(cls, title)) return -1;
        bool isK2Class = !string.IsNullOrEmpty(cls) && cls.StartsWith("K2_", StringComparison.OrdinalIgnoreCase);
        bool isHonTitle = !string.IsNullOrEmpty(title) && title.IndexOf("Heroes of Newerth", StringComparison.OrdinalIgnoreCase) >= 0;
        int area = 0;
        if (gotRect)
        {
            int w = Math.Max(0, outRect.Right - outRect.Left);
            int h = Math.Max(0, outRect.Bottom - outRect.Top);
            area = w * h;
        }
        if (isK2Class) return 100000000 + Math.Min(area, 50000000);
        if (isHonTitle) return 50000000 + Math.Min(area, 40000000);
        if (area > 10000) return Math.Min(area, 40000000);
        return 0;
    }

    private static KongorWindowProbeResultV3 ProbeOnCurrentThreadDesktop(uint targetPid, string deskLabel, string methodPrefix)
    {
        KongorWindowProbeResultV3 res = new KongorWindowProbeResultV3();
        res.Hwnd = IntPtr.Zero;
        res.DesktopName = deskLabel;
        res.ClassName = string.Empty;
        res.Title = string.Empty;
        res.Method = "none";
        res.CandidateCount = 0;

        int bestScore = -1;
        HashSet<long> seen = new HashSet<long>();

        Action<IntPtr, string> inspect = delegate(IntPtr hWnd, string method)
        {
            if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return;
            uint wPid = 0;
            GetWindowThreadProcessId(hWnd, out wPid);
            if (wPid != targetPid) return;
            if (!seen.Add(hWnd.ToInt64())) return;

            res.CandidateCount++;
            string cls = ReadClassName(hWnd);
            string title = ReadTitle(hWnd);
            RECT r;
            bool gotRect;
            int score = ScoreWindow(hWnd, cls, title, out r, out gotRect);
            if (score > bestScore)
            {
                bestScore = score;
                res.Hwnd = hWnd;
                res.ClassName = cls;
                res.Title = title;
                res.Method = method;
                res.GotRect = gotRect;
                res.Left = r.Left;
                res.Top = r.Top;
                res.Right = r.Right;
                res.Bottom = r.Bottom;
                res.IsVisible = IsWindowVisible(hWnd);
                IntPtr fg = GetForegroundWindow();
                uint fgPid = 0;
                if (fg != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(fg, out fgPid);
                }
                res.IsForeground = (fgPid == targetPid);
            }
        };

        EnumWindowsProc cb = delegate(IntPtr hWnd, IntPtr lParam)
        {
            inspect(hWnd, methodPrefix + ":" + deskLabel);
            return true;
        };
        EnumWindows(cb, IntPtr.Zero);
        GC.KeepAlive(cb);

        if (res.Hwnd == IntPtr.Zero || bestScore < 50000000)
        {
            try
            {
                Process proc = Process.GetProcessById((int)targetPid);
                try
                {
                    foreach (ProcessThread th in proc.Threads)
                    {
                        uint tid = (uint)th.Id;
                        EnumWindowsProc thCb = delegate(IntPtr hWnd, IntPtr lParam)
                        {
                            inspect(hWnd, "EnumThreadWindows:" + deskLabel);
                            return true;
                        };
                        EnumThreadWindows(tid, thCb, IntPtr.Zero);
                        GC.KeepAlive(thCb);
                    }
                }
                finally
                {
                    proc.Dispose();
                }
            }
            catch
            {
                // Process may exit concurrently; caller enforces fail-closed check
            }
        }

        return res;
    }

    public static KongorWindowProbeResultV3 ProbePrimaryWindowForPid(uint targetPid)
    {
        string callerDesk = GetCurrentDesktopName();
        KongorWindowProbeResultV3 defResult = null;
        bool defAccessible = false;

        // 1. Always probe the interactive "Default" desktop on a fresh worker thread via SetThreadDesktop
        //    so GetWindowRect, IsWindowVisible, and GetForegroundWindow execute in the SAME desktop context!
        if (!string.Equals(callerDesk, "Default", StringComparison.OrdinalIgnoreCase))
        {
            Thread t = new Thread(delegate()
            {
                IntPtr hDef = OpenDesktop("Default", 0, false, DESKTOP_ALL_READ);
                if (hDef == IntPtr.Zero)
                {
                    hDef = OpenDesktop("Default", 0, false, DESKTOP_READOBJECTS);
                }
                if (hDef == IntPtr.Zero) return;
                try
                {
                    if (SetThreadDesktop(hDef))
                    {
                        defAccessible = true;
                        defResult = ProbeOnCurrentThreadDesktop(targetPid, "Default", "SetThreadDesktop+EnumWindows");
                    }
                }
                finally
                {
                    CloseDesktop(hDef);
                }
            });
            t.IsBackground = true;
            t.Start();
            t.Join(3000);
        }
        else
        {
            defAccessible = true;
            defResult = ProbeOnCurrentThreadDesktop(targetPid, "Default", "EnumWindows");
        }

        if (defResult != null && defResult.Hwnd != IntPtr.Zero)
        {
            defResult.CurrentThreadDesktop = callerDesk;
            defResult.DefaultDesktopAccessible = defAccessible;
            return defResult;
        }

        // 2. Fall back to caller's own desktop (e.g. exebox-*) if target window lives there instead
        KongorWindowProbeResultV3 localResult = ProbeOnCurrentThreadDesktop(targetPid, callerDesk, "EnumWindows");
        localResult.CurrentThreadDesktop = callerDesk;
        localResult.DefaultDesktopAccessible = defAccessible;
        return localResult;
    }
}
"@
}

function ConvertTo-KongorSanitizedPath {
    param([string]$InputPath)
    if ([string]::IsNullOrWhiteSpace($InputPath)) {
        return $InputPath
    }
    $userProfile = $env:USERPROFILE
    if ([string]::IsNullOrWhiteSpace($userProfile)) {
        return $InputPath
    }
    $trimmedProfile = $userProfile.TrimEnd('\', '/')
    $fwdProfile = $trimmedProfile.Replace('\', '/')
    $sanitized = [regex]::Replace($InputPath, [regex]::Escape($trimmedProfile), '[USER_DIR]', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    $sanitized = [regex]::Replace($sanitized, [regex]::Escape($fwdProfile), '[USER_DIR]', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    return $sanitized
}

function Get-KongorFileSha256 {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return ""
    }
    try {
        return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    } catch {
        Write-Verbose "[SHJ-PERF] Failed computing SHA-256 for '$Path': $($_.Exception.Message)"
        return ""
    }
}

function Get-KongorGitHeadSha {
    param([string]$RepoRootPath)
    try {
        $sha = (& git -C $RepoRootPath rev-parse --short=12 HEAD 2>$null | Out-String).Trim()
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($sha)) {
            return $sha
        }
    } catch {
        Write-Verbose "[SHJ-PERF] Unable to resolve git HEAD SHA: $($_.Exception.Message)"
    }
    return "unknown"
}

function Get-KongorHardwareProfileKey {
    param(
        [string]$Resolution,
        [int]$TargetFps,
        [string]$WindowProfile
    )
    $gpuName = "unknown_gpu"
    try {
        $gpus = @(Get-CimInstance -ClassName Win32_VideoController -ErrorAction Stop | Where-Object { -not [string]::IsNullOrWhiteSpace($_.Name) })
        $preferred = $gpus | Where-Object { $_.Name -match '(?i)(NVIDIA|GeForce|RTX|GTX|Radeon|RX\s|Intel.*Arc)' } | Select-Object -First 1
        $chosen = if ($null -ne $preferred) { $preferred } else { $gpus | Select-Object -First 1 }
        if ($null -ne $chosen -and -not [string]::IsNullOrWhiteSpace([string]$chosen.Name)) {
            $gpuName = [string]$chosen.Name
        }
    } catch {
        Write-Verbose "[SHJ-PERF] Win32_VideoController query failed: $($_.Exception.Message)"
    }

    $slug = ($gpuName.ToLowerInvariant() -replace '[^a-z0-9]+', '_').Trim('_')
    if ([string]::IsNullOrWhiteSpace($slug)) { $slug = "unknown_gpu" }
    $resNorm = ($Resolution.ToLowerInvariant() -replace '[^0-9x]+', '')
    return [ordered]@{
        gpuName       = $gpuName
        gpuSlug       = $slug
        resolution    = $resNorm
        targetFps     = $TargetFps
        windowProfile = $WindowProfile
        hardwareKey   = ("{0}_{1}_{2}Hz_{3}" -f $slug, $resNorm, $TargetFps, $WindowProfile)
    }
}

function Get-KongorKhronosVulkanLayerAudit {
    $regSpecs = @(
        @{ Hive = "HKCU"; Kind = "ImplicitLayers"; Path = "HKCU:\SOFTWARE\Khronos\Vulkan\ImplicitLayers" },
        @{ Hive = "HKLM"; Kind = "ImplicitLayers"; Path = "HKLM:\SOFTWARE\Khronos\Vulkan\ImplicitLayers" },
        @{ Hive = "HKCU"; Kind = "ExplicitLayers"; Path = "HKCU:\SOFTWARE\Khronos\Vulkan\ExplicitLayers" },
        @{ Hive = "HKLM"; Kind = "ExplicitLayers"; Path = "HKLM:\SOFTWARE\Khronos\Vulkan\ExplicitLayers" }
    )

    $entries = New-Object System.Collections.Generic.List[object]
    $enabledImplicitThirdParty = New-Object System.Collections.Generic.List[string]
    $enabledImplicitKongorReplay = $false

    foreach ($spec in $regSpecs) {
        if (-not (Test-Path -LiteralPath $spec.Path)) {
            continue
        }
        try {
            $item = Get-Item -LiteralPath $spec.Path -ErrorAction Stop
            foreach ($valName in $item.GetValueNames()) {
                $rawVal = $item.GetValue($valName)
                $numVal = 0
                try { $numVal = [int]$rawVal } catch { $numVal = -1 }
                $isEnabled = ($numVal -eq 0)
                $entries.Add([ordered]@{
                    hive      = $spec.Hive
                    kind      = $spec.Kind
                    manifest  = (ConvertTo-KongorSanitizedPath -InputPath $valName)
                    value     = $numVal
                    isEnabled = $isEnabled
                })
                if ($spec.Kind -eq "ImplicitLayers" -and $isEnabled) {
                    if ($valName -match '(?i)VkLayer_hon_replay_capture') {
                        $enabledImplicitKongorReplay = $true
                    } else {
                        $enabledImplicitThirdParty.Add(("{0}:{1}" -f $spec.Hive, [System.IO.Path]::GetFileName($valName)))
                    }
                }
            }
        } catch {
            Write-Verbose "[SHJ-AUDIT] Registry read skipped for '$($spec.Path)': $($_.Exception.Message)"
        }
    }

    return [ordered]@{
        totalRegistryEntries        = $entries.Count
        enabledImplicitKongorReplay = $enabledImplicitKongorReplay
        enabledImplicitThirdParty   = @($enabledImplicitThirdParty.ToArray())
        entries                     = @($entries.ToArray())
    }
}

function Get-KongorDxvkConfSummary {
    param([string]$DxvkPath)
    $settings = [ordered]@{}
    $exists = Test-Path -LiteralPath $DxvkPath -PathType Leaf
    $sha256 = ""
    if ($exists) {
        $sha256 = Get-KongorFileSha256 -Path $DxvkPath
        $lines = Get-Content -LiteralPath $DxvkPath -Encoding UTF8
        foreach ($rawLine in $lines) {
            $line = $rawLine.Trim()
            if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith("#")) {
                continue
            }
            $eqIdx = $line.IndexOf("=")
            if ($eqIdx -gt 0) {
                $k = $line.Substring(0, $eqIdx).Trim()
                $v = $line.Substring($eqIdx + 1).Trim()
                $hashIdx = $v.IndexOf("#")
                if ($hashIdx -ge 0) { $v = $v.Substring(0, $hashIdx).Trim() }
                if (-not [string]::IsNullOrWhiteSpace($k)) {
                    $settings[$k] = $v
                }
            }
        }
    }

    return [ordered]@{
        path     = (ConvertTo-KongorSanitizedPath -InputPath $DxvkPath)
        exists   = $exists
        sha256   = $sha256
        settings = $settings
    }
}

function Resolve-KongorZeroWindowExposureDecision {
    param(
        [bool]$HasHandle,
        [string]$HandleHex = "0x0",
        [bool]$IsVisible = $false,
        [bool]$IsForeground = $false,
        [object]$Rect = $null,
        [bool]$RequireRenderWindow = $true,
        [string]$WindowDesktop = "unknown",
        [string]$WindowClass = "",
        [string]$WindowTitle = "",
        [string]$Method = "none",
        [int]$CandidateCount = 0,
        [string]$CurrentThreadDesktop = "unknown",
        [bool]$DefaultDesktopAccessible = $false
    )

    if (-not $HasHandle) {
        if ($RequireRenderWindow) {
            return [ordered]@{
                zeroWindowExposure       = $false
                mainWindowHandle         = "0x0"
                isVisible                = $false
                isForeground             = $false
                rect                     = $null
                windowDesktop            = $WindowDesktop
                windowClass              = $WindowClass
                windowTitle              = $WindowTitle
                enumerationMethod        = $Method
                candidateCount           = $CandidateCount
                currentThreadDesktop     = $CurrentThreadDesktop
                defaultDesktopAccessible = $DefaultDesktopAccessible
                reason                   = "k2_window_handle_not_found"
            }
        }
        return [ordered]@{
            zeroWindowExposure       = $true
            mainWindowHandle         = "0x0"
            isVisible                = $false
            isForeground             = $false
            rect                     = $null
            windowDesktop            = $WindowDesktop
            windowClass              = $WindowClass
            windowTitle              = $WindowTitle
            enumerationMethod        = $Method
            candidateCount           = $CandidateCount
            currentThreadDesktop     = $CurrentThreadDesktop
            defaultDesktopAccessible = $DefaultDesktopAccessible
            reason                   = "no_render_driver_loaded"
        }
    }

    # Fail closed if we found a window handle but GetWindowRect failed (e.g. cross-desktop handle query failure)
    if ($null -eq $Rect) {
        return [ordered]@{
            zeroWindowExposure       = $false
            mainWindowHandle         = $HandleHex
            isVisible                = $IsVisible
            isForeground             = $IsForeground
            rect                     = $null
            windowDesktop            = $WindowDesktop
            windowClass              = $WindowClass
            windowTitle              = $WindowTitle
            enumerationMethod        = $Method
            candidateCount           = $CandidateCount
            currentThreadDesktop     = $CurrentThreadDesktop
            defaultDesktopAccessible = $DefaultDesktopAccessible
            reason                   = "window_rect_unavailable"
        }
    }

    $leftVal = [int]$Rect.left
    $topVal = [int]$Rect.top
    $isOffscreen = ($leftVal -le -4000 -or $topVal -le -4000 -or $leftVal -ge 4000 -or $topVal -ge 4000)

    $zeroExp = (-not $IsForeground) -and ((-not $IsVisible) -or $isOffscreen)
    $reason = if ($zeroExp) {
        if ($isOffscreen) { "offscreen_coordinates" } else { "window_not_visible" }
    } else {
        "visible_on_desktop_or_foreground"
    }

    return [ordered]@{
        zeroWindowExposure       = $zeroExp
        mainWindowHandle         = $HandleHex
        isVisible                = $IsVisible
        isForeground             = $IsForeground
        rect                     = $Rect
        windowDesktop            = $WindowDesktop
        windowClass              = $WindowClass
        windowTitle              = $WindowTitle
        enumerationMethod        = $Method
        candidateCount           = $CandidateCount
        currentThreadDesktop     = $CurrentThreadDesktop
        defaultDesktopAccessible = $DefaultDesktopAccessible
        reason                   = $reason
    }
}

function Test-KongorZeroWindowExposure {
    param(
        [int]$ProcessId,
        [bool]$RequireRenderWindow = $true
    )
    try {
        [void](Get-Process -Id $ProcessId -ErrorAction Stop)
        $probe = [KongorShjWin32V3]::ProbePrimaryWindowForPid([uint32]$ProcessId)
        $hwnd = $probe.Hwnd
        if ($hwnd -eq [IntPtr]::Zero) {
            return (Resolve-KongorZeroWindowExposureDecision `
                -HasHandle $false `
                -RequireRenderWindow $RequireRenderWindow `
                -WindowDesktop $probe.DesktopName `
                -Method $probe.Method `
                -CandidateCount $probe.CandidateCount `
                -CurrentThreadDesktop $probe.CurrentThreadDesktop `
                -DefaultDesktopAccessible $probe.DefaultDesktopAccessible)
        }

        $rectObj = if ($probe.GotRect) {
            [ordered]@{
                left   = $probe.Left
                top    = $probe.Top
                right  = $probe.Right
                bottom = $probe.Bottom
                width  = ($probe.Right - $probe.Left)
                height = ($probe.Bottom - $probe.Top)
            }
        } else { $null }

        return (Resolve-KongorZeroWindowExposureDecision `
            -HasHandle $true `
            -HandleHex ("0x{0:X}" -f $hwnd.ToInt64()) `
            -IsVisible ([bool]$probe.IsVisible) `
            -IsForeground ([bool]$probe.IsForeground) `
            -Rect $rectObj `
            -RequireRenderWindow $RequireRenderWindow `
            -WindowDesktop $probe.DesktopName `
            -WindowClass $probe.ClassName `
            -WindowTitle $probe.Title `
            -Method $probe.Method `
            -CandidateCount $probe.CandidateCount `
            -CurrentThreadDesktop $probe.CurrentThreadDesktop `
            -DefaultDesktopAccessible $probe.DefaultDesktopAccessible)
    } catch {
        return [ordered]@{
            zeroWindowExposure       = $false
            mainWindowHandle         = "0x0"
            isVisible                = $false
            isForeground             = $false
            rect                     = $null
            windowDesktop            = "unknown"
            windowClass              = ""
            windowTitle              = ""
            enumerationMethod        = "exception"
            candidateCount           = 0
            currentThreadDesktop     = "unknown"
            defaultDesktopAccessible = $false
            reason                   = ("probe_error:{0}" -f $_.Exception.Message)
        }
    }
}

function Start-KongorShjMcpSession {
    param(
        [string]$KongorDllPath,
        [string]$RepoRootPath
    )

    $dotnetExe = "dotnet"
    $localDotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
    if (Test-Path -LiteralPath $localDotnet -PathType Leaf) {
        $dotnetExe = $localDotnet
    }

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $dotnetExe
    $psi.Arguments = "`"$KongorDllPath`" mcp"
    $psi.WorkingDirectory = $RepoRootPath
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    if (Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA "Microsoft\dotnet")) {
        $psi.EnvironmentVariables["DOTNET_ROOT"] = (Join-Path $env:LOCALAPPDATA "Microsoft\dotnet")
    }

    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi
    [void]$proc.Start()
    $stderrTask = $proc.StandardError.ReadToEndAsync()

    $initReq = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"kongor-shj-perf","version":"1.0"}}}'
    Send-McpJsonRpcLine -Process $proc -JsonLine $initReq
    [void](Read-McpResponsesByIds -Process $proc -ExpectedIds @(1) -TimeoutMs 20000)

    return [PSCustomObject]@{
        Process    = $proc
        StderrTask = $stderrTask
        NextId     = 10
    }
}

function Stop-KongorShjMcpSession {
    param([PSCustomObject]$Session)
    if ($null -eq $Session -or $null -eq $Session.Process) { return }
    try {
        $Session.Process.StandardInput.Close()
        [void]$Session.Process.WaitForExit(3000)
    } catch {
        Write-Verbose "[SHJ-MCP] Graceful close skipped: $($_.Exception.Message)"
    }
    if (-not $Session.Process.HasExited) {
        try { $Session.Process.Kill() } catch { Write-Verbose "[SHJ-MCP] Kill skipped: $($_.Exception.Message)" }
    }
    $Session.Process.Dispose()
}

function Get-KongorSafeProp {
    param(
        [object]$Obj,
        [string]$Name
    )
    if ($null -eq $Obj) { return $null }
    if ($Obj -is [System.Collections.IDictionary]) {
        if ($Obj.Contains($Name)) { return $Obj[$Name] }
        return $null
    }
    $prop = $Obj.PSObject.Properties[$Name]
    if ($null -ne $prop) { return $prop.Value }
    return $null
}

function Invoke-KongorShjMcpTool {
    param(
        [PSCustomObject]$Session,
        [string]$ToolName,
        [hashtable]$Arguments,
        [int]$TimeoutMs = 45000
    )
    $reqId = [int]$Session.NextId
    $Session.NextId = $reqId + 1

    $payload = [ordered]@{
        jsonrpc = "2.0"
        id      = $reqId
        method  = "tools/call"
        params  = [ordered]@{
            name      = $ToolName
            arguments = $Arguments
        }
    }
    $jsonLine = $payload | ConvertTo-Json -Depth 10 -Compress
    Send-McpJsonRpcLine -Process $Session.Process -JsonLine $jsonLine
    $respMap = Read-McpResponsesByIds -Process $Session.Process -ExpectedIds @($reqId) -TimeoutMs $TimeoutMs
    $resp = $respMap[$reqId]

    $isError = $false
    $errObj = Get-KongorSafeProp -Obj $resp -Name "error"
    if ($null -ne $errObj) {
        return [PSCustomObject]@{
            IsError = $true
            RawText = ($errObj | ConvertTo-Json -Depth 6 -Compress)
            Data    = $null
        }
    }
    $resObj = Get-KongorSafeProp -Obj $resp -Name "result"
    $isErrProp = Get-KongorSafeProp -Obj $resObj -Name "isError"
    if ($null -ne $isErrProp) {
        $isError = [bool]$isErrProp
    }
    $rawText = ""
    $contentArr = Get-KongorSafeProp -Obj $resObj -Name "content"
    if ($null -ne $contentArr -and @($contentArr).Count -gt 0) {
        $firstItem = @($contentArr)[0]
        $txt = Get-KongorSafeProp -Obj $firstItem -Name "text"
        if ($null -ne $txt) { $rawText = [string]$txt }
    }
    $parsed = $null
    if ($rawText.TrimStart().StartsWith("{") -or $rawText.TrimStart().StartsWith("[")) {
        try { $parsed = $rawText | ConvertFrom-Json } catch { $parsed = $null }
    }

    return [PSCustomObject]@{
        IsError = $isError
        RawText = $rawText
        Data    = $parsed
    }
}

function Read-KongorLiveCvarsViaMcp {
    param(
        [PSCustomObject]$Session,
        [string[]]$CvarNames = @(
            "host_maxFPS",
            "cg_interfaceFPS",
            "vid_vsync",
            "vid_shadowmapSize",
            "vid_antialiasing",
            "vid_reflectionMapSize",
            "cg_precacheResources",
            "vid_driver",
            "vid_resolution"
        )
    )

    $quotedList = ($CvarNames | ForEach-Object { "'$_'" }) -join ","
    $luaCode = "local names={$quotedList}; local out={}; for i=1,#names do local n=names[i]; local c=Cvar.GetCvar(n); local v=(c and c:GetString()) or '__NIL__'; out[#out+1]=n..'='..tostring(v) end; return table.concat(out,'|')"

    $res = Invoke-KongorShjMcpTool -Session $Session -ToolName "kongor_client_eval_lua" -Arguments @{ code = $luaCode } -TimeoutMs 15000
    $cvars = [ordered]@{}
    $ok = $false
    $rawResult = ""

    if (-not $res.IsError -and $null -ne $res.Data) {
        $valStr = ""
        if ($null -ne $res.Data.PSObject.Properties["result"] -and $null -ne $res.Data.result) {
            $valStr = [string]$res.Data.result
        } elseif ($null -ne $res.Data.PSObject.Properties["output"] -and $null -ne $res.Data.output) {
            $valStr = [string]$res.Data.output
        }
        $rawResult = $valStr
        if (-not [string]::IsNullOrWhiteSpace($valStr) -and $valStr.Contains("=")) {
            $ok = $true
            foreach ($pair in ($valStr -split '\|')) {
                $eq = $pair.IndexOf("=")
                if ($eq -gt 0) {
                    $k = $pair.Substring(0, $eq).Trim()
                    $v = $pair.Substring($eq + 1).Trim()
                    $cvars[$k] = $v
                }
            }
        }
    }

    return [ordered]@{
        success   = $ok
        cvars     = $cvars
        rawResult = $rawResult
        error     = if ($ok) { $null } else { $res.RawText }
    }
}

function Invoke-KongorRuntimeAudit {
    param(
        [string]$RepoRootPath,
        [ValidateSet("perf_clean", "mcp_visual_offscreen", "willowmaker")]
        [string]$Profile = "perf_clean",
        [int]$ExpectedMaxFps = 0,
        [int]$ExpectedInterfaceFps = 0,
        [int]$ExpectedMaxAntialiasing = -1,
        [int]$ExpectedVsync = -1,
        [bool]$RequireZeroWindowExposure = $false,
        [PSCustomObject]$McpSession = $null,
        [string]$KongorDllPath = "",
        [switch]$DryRun,
        [string]$OutputPath = ""
    )

    $auditSw = [System.Diagnostics.Stopwatch]::StartNew()
    $clientDir = Join-Path $RepoRootPath "games\client"
    $dxvkPath = Join-Path $clientDir "userdataKONGOR\dxvk.conf"
    $willowReceiptPath = Join-Path $RepoRootPath "logs\shj\willowmaker_last_launch_receipt.json"

    $khronosAudit = Get-KongorKhronosVulkanLayerAudit
    $dxvkSummary = Get-KongorDxvkConfSummary -DxvkPath $dxvkPath

    $willowReceipt = $null
    if (Test-Path -LiteralPath $willowReceiptPath -PathType Leaf) {
        try {
            $willowReceipt = Get-Content -LiteralPath $willowReceiptPath -Raw -Encoding UTF8 | ConvertFrom-Json
        } catch {
            Write-Verbose "[SHJ-AUDIT] Could not parse '$willowReceiptPath': $($_.Exception.Message)"
        }
    }

    $honProc = Get-Process -Name "hon_x64" -ErrorAction SilentlyContinue | Select-Object -First 1
    $liveRunning = ($null -ne $honProc)

    if ($DryRun.IsPresent -or (-not $liveRunning)) {
        $reason = if ($DryRun.IsPresent) { "dry_run" } else { "client_offline" }
        $auditSw.Stop()
        $incObj = [ordered]@{
            schemaVersion          = 1
            action                 = "runtime-audit"
            profile                = $Profile
            dryRun                 = [bool]$DryRun.IsPresent
            liveClientRunning      = $liveRunning
            verdict                = "inconclusive"
            status                 = "inconclusive"
            inconclusiveReason     = $reason
            khronosRegistry        = $khronosAudit
            dxvkConfig             = $dxvkSummary
            willowmakerReceiptSeen = ($null -ne $willowReceipt)
            findings               = @()
            durationMs             = [Math]::Round($auditSw.Elapsed.TotalMilliseconds, 2)
            timestampUtc           = [DateTimeOffset]::UtcNow.ToString("o")
        }
        if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
            $pDir = Split-Path -Parent $OutputPath
            if (-not [string]::IsNullOrWhiteSpace($pDir) -and -not (Test-Path -LiteralPath $pDir)) {
                New-Item -ItemType Directory -Path $pDir -Force | Out-Null
            }
            $incObj | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
        }
        return $incObj
    }

    $findings = New-Object System.Collections.Generic.List[string]
    $expectedVkLayerLoaded = ($Profile -eq "mcp_visual_offscreen")
    if ($Profile -eq "willowmaker" -and $null -ne $willowReceipt -and [int]$willowReceipt.gamePid -eq [int]$honProc.Id) {
        $expectedVkLayerLoaded = [bool]$willowReceipt.expectedVkLayerReplayCaptureLoaded
    }

    # 1. Live loaded module audit (Process.Modules + SHA-256 parity vs games/client)
    $deniedHooks = @(
        "RTSSHooks64.dll",
        "DiscordHook64.dll",
        "graphics-hook64.dll",
        "obs-vulkan64.dll",
        "SteamOverlayVulkanLayer64.dll",
        "SteamFossilizeVulkanLayer64.dll",
        "renderdoc.dll",
        "MediaSDKVulkanLayer64.dll",
        "MedalHook64.dll"
    )
    $keyNames = @("vid_vulkan_x64.dll", "d3d9.dll", "version.dll", "VkLayer_hon_replay_capture.dll")

    $loadedModules = @()
    try {
        $loadedModules = @($honProc.Modules | ForEach-Object {
            [PSCustomObject]@{ ModuleName = [string]$_.ModuleName; FileName = [string]$_.FileName }
        })
    } catch {
        $findings.Add("Failed enumerating loaded modules for hon_x64.exe PID $($honProc.Id): $($_.Exception.Message)")
    }

    $modSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($m in $loadedModules) { [void]$modSet.Add($m.ModuleName) }

    $vkLayerLoaded = $modSet.Contains("VkLayer_hon_replay_capture.dll")
    $vidVulkanLoaded = $modSet.Contains("vid_vulkan_x64.dll")
    $d3d9Loaded = $modSet.Contains("d3d9.dll")
    $versionLoaded = $modSet.Contains("version.dll")

    if ($vkLayerLoaded -ne $expectedVkLayerLoaded) {
        $findings.Add("VkLayer_hon_replay_capture.dll drift for profile '$Profile': loaded=$vkLayerLoaded, expected=$expectedVkLayerLoaded. Check HKCU/HKLM ImplicitLayers and VK_LOADER_LAYERS_DISABLE.")
    }

    $detectedDenied = @($deniedHooks | Where-Object { $modSet.Contains($_) })
    if ($detectedDenied.Count -gt 0) {
        $findings.Add("Denied third-party overlay/Vulkan hook(s) mapped in hon_x64.exe: $($detectedDenied -join ', '). Ensure VK_LOADER_LAYERS_DISABLE blocks ~implicit~ layers.")
    }

    $keyModuleChecks = New-Object System.Collections.Generic.List[object]
    foreach ($kName in $keyNames) {
        $loadedMod = $loadedModules | Where-Object { $_.ModuleName.Equals($kName, [System.StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
        $repoBin = Join-Path $clientDir $kName
        $repoSha = Get-KongorFileSha256 -Path $repoBin
        $loadedPath = if ($null -ne $loadedMod) { $loadedMod.FileName } else { "" }
        $loadedSha = if (-not [string]::IsNullOrWhiteSpace($loadedPath)) { Get-KongorFileSha256 -Path $loadedPath } else { "" }
        $hashMatches = if ([string]::IsNullOrWhiteSpace($loadedSha) -or [string]::IsNullOrWhiteSpace($repoSha)) { $null } else { ($loadedSha -eq $repoSha) }

        $safeLoadedPath = ConvertTo-KongorSanitizedPath -InputPath $loadedPath
        $safeRepoPath = ConvertTo-KongorSanitizedPath -InputPath $repoBin
        if ($null -ne $loadedMod -and $hashMatches -eq $false) {
            $findings.Add("Binary hash drift for loaded '$kName': loaded '$safeLoadedPath' ($loadedSha) != repo '$safeRepoPath' ($repoSha).")
        }
        $keyModuleChecks.Add([ordered]@{
            moduleName   = $kName
            loaded       = ($null -ne $loadedMod)
            loadedPath   = $safeLoadedPath
            loadedSha256 = $loadedSha
            repoPath     = $safeRepoPath
            repoSha256   = $repoSha
            hashMatches  = $hashMatches
        })
    }

    # 2. dxvk.conf High-Hz parity checks
    if ($dxvkSummary.exists) {
        $maxLat = if ($dxvkSummary.settings.Contains("d3d9.maxFrameLatency")) { [string]$dxvkSummary.settings["d3d9.maxFrameLatency"] } else { "" }
        $latSleep = if ($dxvkSummary.settings.Contains("dxvk.latencySleep")) { ([string]$dxvkSummary.settings["dxvk.latencySleep"]).ToLowerInvariant() } else { "" }
        if ($maxLat -eq "1") {
            $findings.Add("dxvk.conf regression: d3d9.maxFrameLatency = 1 causes High-Hz main-thread pacing stalls (expected >= 2).")
        }
        if ($latSleep -eq "true" -or $latSleep -eq "1") {
            $findings.Add("dxvk.conf regression: dxvk.latencySleep = $latSleep introduces micro-stutters on High-Hz displays (expected False).")
        }
        if ($Profile -eq "willowmaker" -and $null -ne $willowReceipt -and $null -ne $willowReceipt.dxvkConfig -and -not [string]::IsNullOrWhiteSpace([string]$willowReceipt.dxvkConfig.sha256)) {
            if ([string]$willowReceipt.dxvkConfig.sha256 -ne [string]$dxvkSummary.sha256) {
                $findings.Add("dxvk.conf drifted on disk since last WILLOWMAKER launch (disk=$($dxvkSummary.sha256) vs receipt=$($willowReceipt.dxvkConfig.sha256)).")
            }
        }
    }

    # 3. Window exposure check (fail-closed whenever vid_vulkan_x64.dll is loaded and headless isolation is required)
    $windowExposure = Test-KongorZeroWindowExposure -ProcessId $honProc.Id -RequireRenderWindow $vidVulkanLoaded
    if ($RequireZeroWindowExposure -and (-not [bool]$windowExposure.zeroWindowExposure)) {
        $findings.Add("Headless window isolation violated: zeroWindowExposure=false (reason=$($windowExposure.reason), desktop=$($windowExposure.windowDesktop), hwnd=$($windowExposure.mainWindowHandle), rect=$($windowExposure.rect | ConvertTo-Json -Compress)).")
    }

    # 4. Live K2 Engine CVar Readback via MCP
    $ownsSession = $false
    $sessionToUse = $McpSession
    $cvarProbe = [ordered]@{ success = $false; cvars = [ordered]@{}; rawResult = ""; error = "mcp_session_unavailable" }
    try {
        if ($null -eq $sessionToUse) {
            if ([string]::IsNullOrWhiteSpace($KongorDllPath)) {
                $KongorDllPath = Resolve-KongorCliDll -RepoRootPath $RepoRootPath
            }
            $sessionToUse = Start-KongorShjMcpSession -KongorDllPath $KongorDllPath -RepoRootPath $RepoRootPath
            $ownsSession = $true
        }
        $cvarProbe = Read-KongorLiveCvarsViaMcp -Session $sessionToUse
    } catch {
        $cvarProbe = [ordered]@{ success = $false; cvars = [ordered]@{}; rawResult = ""; error = $_.Exception.Message }
    } finally {
        if ($ownsSession -and $null -ne $sessionToUse) {
            Stop-KongorShjMcpSession -Session $sessionToUse
        }
    }

    if (-not [bool]$cvarProbe.success) {
        $findings.Add("Live K2 CVar IPC readback failed: $($cvarProbe.error)")
    } else {
        $liveCvars = $cvarProbe.cvars
        $invCulture = [System.Globalization.CultureInfo]::InvariantCulture
        if ($ExpectedMaxFps -gt 0 -and $liveCvars.Contains("host_maxFPS")) {
            $rawMaxFps = [string]$liveCvars["host_maxFPS"]
            $parsedMaxFps = 0.0
            if ($rawMaxFps -eq "__NIL__" -or (-not [double]::TryParse($rawMaxFps, [System.Globalization.NumberStyles]::Float, $invCulture, [ref]$parsedMaxFps))) {
                $findings.Add("CVar drift: host_maxFPS='$rawMaxFps' is missing/unparseable (expected $ExpectedMaxFps).")
            } elseif ([int][Math]::Round($parsedMaxFps) -ne $ExpectedMaxFps) {
                $findings.Add("CVar drift: host_maxFPS=$parsedMaxFps (expected $ExpectedMaxFps).")
            }
        }
        if ($ExpectedInterfaceFps -gt 0 -and $liveCvars.Contains("cg_interfaceFPS")) {
            $rawUiFps = [string]$liveCvars["cg_interfaceFPS"]
            $parsedUiFps = 0.0
            if ($rawUiFps -eq "__NIL__" -or (-not [double]::TryParse($rawUiFps, [System.Globalization.NumberStyles]::Float, $invCulture, [ref]$parsedUiFps))) {
                $findings.Add("CVar drift: cg_interfaceFPS='$rawUiFps' is missing/unparseable (expected $ExpectedInterfaceFps).")
            } elseif ([int][Math]::Round($parsedUiFps) -ne $ExpectedInterfaceFps) {
                $findings.Add("CVar drift: cg_interfaceFPS=$parsedUiFps (expected $ExpectedInterfaceFps).")
            }
        }
        if ($ExpectedMaxAntialiasing -ge 0 -and $liveCvars.Contains("vid_antialiasing")) {
            $rawAa = [string]$liveCvars["vid_antialiasing"]
            $parsedAa = 0.0
            if ($rawAa -eq "__NIL__" -or (-not [double]::TryParse($rawAa, [System.Globalization.NumberStyles]::Float, $invCulture, [ref]$parsedAa))) {
                $findings.Add("CVar drift: vid_antialiasing='$rawAa' is missing/unparseable (expected <= $ExpectedMaxAntialiasing).")
            } elseif ([int][Math]::Round($parsedAa) -gt $ExpectedMaxAntialiasing) {
                $findings.Add("CVar drift: vid_antialiasing=$parsedAa exceeds max allowed $ExpectedMaxAntialiasing.")
            }
        }
        if ($ExpectedVsync -ge 0 -and $liveCvars.Contains("vid_vsync")) {
            $rawVsync = ([string]$liveCvars["vid_vsync"]).ToLowerInvariant()
            if ($rawVsync -eq "__nil__") {
                $findings.Add("CVar drift: vid_vsync='__NIL__' is unavailable (expected $ExpectedVsync).")
            } else {
                $actualVsync = if ($rawVsync -eq "true" -or $rawVsync -eq "1") { 1 } else { 0 }
                if ($actualVsync -ne $ExpectedVsync) {
                    $findings.Add("CVar drift: vid_vsync=$($liveCvars['vid_vsync']) (expected $ExpectedVsync).")
                }
            }
        }
    }

    $auditSw.Stop()
    $verdict = if ($findings.Count -eq 0) { "passed" } else { "failed" }
    $wmPostBootAudit = Get-KongorSafeProp -Obj $willowReceipt -Name "postBootAudit"
    $wmPostBootPassed = if ($null -ne $wmPostBootAudit) { [bool](Get-KongorSafeProp -Obj $wmPostBootAudit -Name "passed") } else { $null }
    $result = [ordered]@{
        schemaVersion                    = 1
        action                           = "runtime-audit"
        profile                          = $Profile
        processId                        = $honProc.Id
        liveClientRunning                = $true
        expectedVkLayerReplayCapture     = $expectedVkLayerLoaded
        actualVkLayerReplayCaptureLoaded = $vkLayerLoaded
        vidVulkanLoaded                  = $vidVulkanLoaded
        d3d9Loaded                       = $d3d9Loaded
        versionDllLoaded                 = $versionLoaded
        totalLoadedModules               = $loadedModules.Count
        detectedDeniedHooks              = $detectedDenied
        keyModules                       = @($keyModuleChecks.ToArray())
        khronosRegistry                  = $khronosAudit
        dxvkConfig                       = $dxvkSummary
        windowExposure                   = $windowExposure
        liveCvars                        = $cvarProbe
        willowmakerReceipt               = if ($null -ne $willowReceipt) {
            [ordered]@{
                path                  = (ConvertTo-KongorSanitizedPath -InputPath $willowReceiptPath)
                timestampUtc          = (Get-KongorSafeProp -Obj $willowReceipt -Name "timestampUtc")
                gamePid               = (Get-KongorSafeProp -Obj $willowReceipt -Name "gamePid")
                willowmakerDllSha256  = (Get-KongorSafeProp -Obj $willowReceipt -Name "willowmakerDllSha256")
                selectedVulkanHudMode = (Get-KongorSafeProp -Obj $willowReceipt -Name "selectedVulkanHudMode")
                postBootAuditPassed   = $wmPostBootPassed
            }
        } else { $null }
        findings                         = @($findings.ToArray())
        verdict                          = $verdict
        status                           = $verdict
        durationMs                       = [Math]::Round($auditSw.Elapsed.TotalMilliseconds, 2)
        timestampUtc                     = [DateTimeOffset]::UtcNow.ToString("o")
    }

    if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
        $pDir = Split-Path -Parent $OutputPath
        if (-not [string]::IsNullOrWhiteSpace($pDir) -and -not (Test-Path -LiteralPath $pDir)) {
            New-Item -ItemType Directory -Path $pDir -Force | Out-Null
        }
        $result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
    }

    return $result
}

function Read-KongorSpyLogWindowMetrics {
    param(
        [string]$SpyLogPath,
        [int64]$StartByteOffset
    )
    if (-not (Test-Path -LiteralPath $SpyLogPath -PathType Leaf)) {
        return [ordered]@{ available = $false; linesParsed = 0; totalSamples = 0; harmonicAvgFps = 0.0; weightedAvgDraws = 0.0 }
    }

    $text = ""
    try {
        $fs = [System.IO.File]::Open($SpyLogPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        try {
            $len = $fs.Length
            $seekPos = if ($StartByteOffset -gt 0 -and $StartByteOffset -le $len) { $StartByteOffset } else { 0L }
            [void]$fs.Seek($seekPos, [System.IO.SeekOrigin]::Begin)
            $sr = New-Object System.IO.StreamReader($fs, [System.Text.Encoding]::UTF8)
            try {
                $text = $sr.ReadToEnd()
            } finally {
                $sr.Dispose()
            }
        } finally {
            $fs.Dispose()
        }
    } catch {
        return [ordered]@{ available = $false; linesParsed = 0; totalSamples = 0; harmonicAvgFps = 0.0; weightedAvgDraws = 0.0; error = $_.Exception.Message }
    }

    $regex = [regex]'\[HIGH_HZ_TELEMETRY\]\s+AvgFPS[:=]\s*([0-9.]+).*?AvgDraws[:=]\s*([0-9.]+).*?Samples[:=]\s*(\d+)'
    $matches = $regex.Matches($text)
    $totalSamples = 0.0
    $totalSeconds = 0.0
    $weightedDraws = 0.0

    foreach ($m in $matches) {
        $fps = [double]$m.Groups[1].Value
        $draws = [double]$m.Groups[2].Value
        $samples = [double]$m.Groups[3].Value
        if ($fps -gt 0.1 -and $samples -gt 0) {
            $totalSamples += $samples
            $totalSeconds += ($samples / $fps)
            $weightedDraws += ($draws * $samples)
        }
    }

    $harmonicFps = if ($totalSeconds -gt 0.001) { [Math]::Round($totalSamples / $totalSeconds, 2) } else { 0.0 }
    $avgDraws = if ($totalSamples -gt 0) { [Math]::Round($weightedDraws / $totalSamples, 1) } else { 0.0 }

    return [ordered]@{
        available        = ($matches.Count -gt 0)
        linesParsed      = $matches.Count
        totalSamples     = [int]$totalSamples
        harmonicAvgFps   = $harmonicFps
        weightedAvgDraws = $avgDraws
    }
}

function Get-KongorNumericMedian {
    param([double[]]$Values)
    if ($null -eq $Values -or $Values.Length -eq 0) { return 0.0 }
    $sorted = @($Values | Sort-Object)
    $mid = [int][Math]::Floor($sorted.Count / 2)
    if (($sorted.Count % 2) -eq 1) {
        return [Math]::Round([double]$sorted[$mid], 3)
    }
    return [Math]::Round(([double]$sorted[$mid - 1] + [double]$sorted[$mid]) / 2.0, 3)
}

function Merge-KongorPerfWindowMetrics {
    param(
        [object[]]$Windows,
        [bool]$EscalatedToThree = $false
    )
    $wArr = @($Windows)
    if ($wArr.Count -eq 0) {
        throw "[SHJ-PERF] Merge-KongorPerfWindowMetrics requires at least one scored window."
    }

    $sumDroppedFrames = [uint64]0
    $sumBudgetViolations = 0
    $maxSpyDivergence = 0.0
    $allRenderAccurate = $true
    $allFrameBudgetPassed = $true
    $anySpyAvailable = $false
    $allModuleAuditsPassed = $true

    foreach ($wItem in $wArr) {
        $sumDroppedFrames += [uint64](Get-KongorSafeProp -Obj $wItem -Name "droppedRingFrames")
        $sumBudgetViolations += [int](Get-KongorSafeProp -Obj $wItem -Name "budgetViolationsCount")
        $divVal = [double](Get-KongorSafeProp -Obj $wItem -Name "spyDivergencePct")
        if ($divVal -gt $maxSpyDivergence) {
            $maxSpyDivergence = $divVal
        }
        if (-not [bool](Get-KongorSafeProp -Obj $wItem -Name "isRenderFrameAccurate")) {
            $allRenderAccurate = $false
        }
        if (-not [bool](Get-KongorSafeProp -Obj $wItem -Name "frameBudgetPassed")) {
            $allFrameBudgetPassed = $false
        }
        if ([bool](Get-KongorSafeProp -Obj $wItem -Name "spyAvailable")) {
            $anySpyAvailable = $true
        }
        $modPassProp = Get-KongorSafeProp -Obj $wItem -Name "moduleAuditPassed"
        if ($null -ne $modPassProp -and (-not [bool]$modPassProp)) {
            $allModuleAuditsPassed = $false
        }
    }

    return [ordered]@{
        windowsRun            = $wArr.Count
        escalatedToThree      = $EscalatedToThree
        telemetrySource       = [string](Get-KongorSafeProp -Obj $wArr[0] -Name "telemetrySource")
        isRenderFrameAccurate = $allRenderAccurate
        sampledFrames         = [int](Get-KongorNumericMedian -Values @($wArr | ForEach-Object { [double](Get-KongorSafeProp -Obj $_ -Name "sampledFrames") }))
        droppedRingFrames     = $sumDroppedFrames
        averageFps            = [Math]::Round((Get-KongorNumericMedian -Values @($wArr | ForEach-Object { [double](Get-KongorSafeProp -Obj $_ -Name "averageFps") })), 2)
        medianFps             = [Math]::Round((Get-KongorNumericMedian -Values @($wArr | ForEach-Object { [double](Get-KongorSafeProp -Obj $_ -Name "medianFps") })), 2)
        p1LowFps              = [Math]::Round((Get-KongorNumericMedian -Values @($wArr | ForEach-Object { [double](Get-KongorSafeProp -Obj $_ -Name "p1LowFps") })), 2)
        p01LowFps             = [Math]::Round((Get-KongorNumericMedian -Values @($wArr | ForEach-Object { [double](Get-KongorSafeProp -Obj $_ -Name "p01LowFps") })), 2)
        avgFrameTimeMs        = [Math]::Round((Get-KongorNumericMedian -Values @($wArr | ForEach-Object { [double](Get-KongorSafeProp -Obj $_ -Name "avgFrameTimeMs") })), 3)
        p50FrameTimeMs        = [Math]::Round((Get-KongorNumericMedian -Values @($wArr | ForEach-Object { [double](Get-KongorSafeProp -Obj $_ -Name "p50FrameTimeMs") })), 3)
        p95FrameTimeMs        = [Math]::Round((Get-KongorNumericMedian -Values @($wArr | ForEach-Object { [double](Get-KongorSafeProp -Obj $_ -Name "p95FrameTimeMs") })), 3)
        p99FrameTimeMs        = [Math]::Round((Get-KongorNumericMedian -Values @($wArr | ForEach-Object { [double](Get-KongorSafeProp -Obj $_ -Name "p99FrameTimeMs") })), 3)
        p999FrameTimeMs       = [Math]::Round((Get-KongorNumericMedian -Values @($wArr | ForEach-Object { [double](Get-KongorSafeProp -Obj $_ -Name "p999FrameTimeMs") })), 3)
        maxFrameTimeMs        = [Math]::Round((Get-KongorNumericMedian -Values @($wArr | ForEach-Object { [double](Get-KongorSafeProp -Obj $_ -Name "maxFrameTimeMs") })), 3)
        avgCpuEngineMs        = [Math]::Round((Get-KongorNumericMedian -Values @($wArr | ForEach-Object { [double](Get-KongorSafeProp -Obj $_ -Name "avgCpuEngineMs") })), 3)
        avgGpuRenderMs        = [Math]::Round((Get-KongorNumericMedian -Values @($wArr | ForEach-Object { [double](Get-KongorSafeProp -Obj $_ -Name "avgGpuRenderMs") })), 3)
        avgPresentWaitMs      = [Math]::Round((Get-KongorNumericMedian -Values @($wArr | ForEach-Object { [double](Get-KongorSafeProp -Obj $_ -Name "avgPresentWaitMs") })), 3)
        avgDrawCalls          = [Math]::Round((Get-KongorNumericMedian -Values @($wArr | ForEach-Object { [double](Get-KongorSafeProp -Obj $_ -Name "avgDrawCalls") })), 1)
        gt8ms                 = [int](Get-KongorNumericMedian -Values @($wArr | ForEach-Object { [double](Get-KongorSafeProp -Obj $_ -Name "gt8ms") }))
        gt10ms                = [int](Get-KongorNumericMedian -Values @($wArr | ForEach-Object { [double](Get-KongorSafeProp -Obj $_ -Name "gt10ms") }))
        gt16_67ms             = [int](Get-KongorNumericMedian -Values @($wArr | ForEach-Object { [double](Get-KongorSafeProp -Obj $_ -Name "gt16_67ms") }))
        gt33_33ms             = [int](Get-KongorNumericMedian -Values @($wArr | ForEach-Object { [double](Get-KongorSafeProp -Obj $_ -Name "gt33_33ms") }))
        gt50ms                = [int](Get-KongorNumericMedian -Values @($wArr | ForEach-Object { [double](Get-KongorSafeProp -Obj $_ -Name "gt50ms") }))
        rateGt8msPerSec       = [Math]::Round((Get-KongorNumericMedian -Values @($wArr | ForEach-Object { [double](Get-KongorSafeProp -Obj $_ -Name "rateGt8msPerSec") })), 2)
        rateGt10msPerSec      = [Math]::Round((Get-KongorNumericMedian -Values @($wArr | ForEach-Object { [double](Get-KongorSafeProp -Obj $_ -Name "rateGt10msPerSec") })), 2)
        cpuStallCount         = [int](Get-KongorNumericMedian -Values @($wArr | ForEach-Object { [double](Get-KongorSafeProp -Obj $_ -Name "cpuStallCount") }))
        gpuStallCount         = [int](Get-KongorNumericMedian -Values @($wArr | ForEach-Object { [double](Get-KongorSafeProp -Obj $_ -Name "gpuStallCount") }))
        budgetViolationsCount = $sumBudgetViolations
        frameBudgetPassed     = $allFrameBudgetPassed
        spyAvailable          = $anySpyAvailable
        spyHarmonicAvgFps     = [Math]::Round((Get-KongorNumericMedian -Values @($wArr | ForEach-Object { [double](Get-KongorSafeProp -Obj $_ -Name "spyHarmonicAvgFps") })), 2)
        spyDivergencePct      = [Math]::Round($maxSpyDivergence, 2)
        moduleAuditPassed     = $allModuleAuditsPassed
    }
}

function Evaluate-KongorPerfWindowAgainstBudgets {
    param(
        [object]$Metrics,
        [int]$TargetFps,
        [int]$WindowSeconds,
        [object]$Baseline = $null
    )

    $failures = New-Object System.Collections.Generic.List[string]
    $borderlineReasons = New-Object System.Collections.Generic.List[string]

    # 1. Hard telemetry truth & stability invariants (always enforced)
    if (-not [bool]$Metrics.isRenderFrameAccurate) {
        $failures.Add("Telemetry source '$($Metrics.telemetrySource)' is NOT render-frame-accurate (expected vid_vulkan_render_fastbus).")
    }
    if ([uint64]$Metrics.droppedRingFrames -gt 0) {
        $failures.Add("Dropped $($Metrics.droppedRingFrames) FastBus telemetry ring frames during scored window (must be 0).")
    }
    $minExpectedFrames = [int][Math]::Floor($TargetFps * $WindowSeconds * 0.60)
    if ([int]$Metrics.sampledFrames -lt $minExpectedFrames) {
        $failures.Add("Insufficient sampled frames: $($Metrics.sampledFrames) < $minExpectedFrames minimum for ${WindowSeconds}s @ ${TargetFps}Hz.")
    }
    if ([bool]$Metrics.isRenderFrameAccurate -and (-not [bool]$Metrics.spyAvailable)) {
        $failures.Add("MCP-296 cross-check FAILED: vid_vulkan_spy.log emitted 0 parseable [HIGH_HZ_TELEMETRY] samples during scored window (expected independent cross-check against FastBus).")
    }
    if ([bool]$Metrics.spyAvailable -and [double]$Metrics.spyDivergencePct -gt 3.0) {
        $failures.Add("MCP-296 telemetry divergence gate FAILED: FastBus AvgFPS ($($Metrics.averageFps)) vs vid_vulkan_spy.log ($($Metrics.spyHarmonicAvgFps)) diverged by $($Metrics.spyDivergencePct)% (> 3.0%).")
    }
    if (-not [bool]$Metrics.frameBudgetPassed) {
        $failures.Add("Lua UI frame budget violated: $($Metrics.budgetViolationsCount) transition(s) exceeded 1.0ms.")
    }
    if ([int]$Metrics.gpuStallCount -gt 0) {
        $failures.Add("GPU render/present stalls detected: sentinelHitchFlags.gpuStall = $($Metrics.gpuStallCount) (competitive budget: 0).")
    }
    if ([int]$Metrics.gt33_33ms -gt 0) {
        $failures.Add("Severe hitch (>33.33ms) detected: count = $($Metrics.gt33_33ms) (competitive budget: 0).")
    }
    if ([int]$Metrics.gt50ms -gt 0) {
        $failures.Add("Major hitch (>50ms) detected: count = $($Metrics.gt50ms) (competitive budget: 0).")
    }
    if ([double]$Metrics.p999FrameTimeMs -gt 25.0) {
        $failures.Add("P99.9 frame time $($Metrics.p999FrameTimeMs)ms exceeds strict competitive ceiling 25.0ms.")
    } elseif ([double]$Metrics.p999FrameTimeMs -gt 21.0) {
        $borderlineReasons.Add("P99.9=$($Metrics.p999FrameTimeMs)ms approaching 25.0ms ceiling")
    }
    if ($null -ne $Metrics.moduleAuditPassed -and (-not [bool]$Metrics.moduleAuditPassed)) {
        $failures.Add("End-of-window runtime module audit FAILED (loaded DLL/layer drift detected during match).")
    }

    # 2. Strict competitive esports budgets (baseline-relative when calibrated; absolute floors otherwise)
    if ($null -ne $Baseline) {
        $minMedFps = [Math]::Round([double]$Baseline.medianFps * 0.95, 2)
        if ([double]$Metrics.medianFps -lt $minMedFps) {
            $failures.Add("MedianFPS regression: $($Metrics.medianFps) < $minMedFps (baseline $($Baseline.medianFps), max -5%).")
        } elseif ([double]$Metrics.medianFps -lt ([double]$Baseline.medianFps * 0.965)) {
            $borderlineReasons.Add("MedianFPS=$($Metrics.medianFps) within 1.5% of -5% budget")
        }

        $minAvgFps = [Math]::Round([double]$Baseline.averageFps * 0.93, 2)
        if ([double]$Metrics.averageFps -lt $minAvgFps) {
            $failures.Add("AvgFPS regression: $($Metrics.averageFps) < $minAvgFps (baseline $($Baseline.averageFps), max -7%).")
        }

        $min1Low = [Math]::Round([double]$Baseline.p1LowFps * 0.85, 2)
        if ([double]$Metrics.p1LowFps -lt $min1Low) {
            $failures.Add("1% Low FPS regression: $($Metrics.p1LowFps) < $min1Low (baseline $($Baseline.p1LowFps), max -15%).")
        } elseif ([double]$Metrics.p1LowFps -lt ([double]$Baseline.p1LowFps * 0.88)) {
            $borderlineReasons.Add("1%Low=$($Metrics.p1LowFps) near -15% floor")
        }

        $maxP99 = [Math]::Round([double]$Baseline.p99FrameTimeMs + [Math]::Max(1.5, [double]$Baseline.p99FrameTimeMs * 0.15), 2)
        if ([double]$Metrics.p99FrameTimeMs -gt $maxP99) {
            $failures.Add("P99 frame time regression: $($Metrics.p99FrameTimeMs)ms > ${maxP99}ms (baseline $($Baseline.p99FrameTimeMs)ms, max +15% or +1.5ms).")
        } elseif ([double]$Metrics.p99FrameTimeMs -gt ($maxP99 * 0.92)) {
            $borderlineReasons.Add("P99=$($Metrics.p99FrameTimeMs)ms near ${maxP99}ms ceiling")
        }

        $maxGt10Rate = [Math]::Round([double]$Baseline.rateGt10msPerSec + [Math]::Max(1.0, [double]$Baseline.rateGt10msPerSec * 0.25), 2)
        if ([double]$Metrics.rateGt10msPerSec -gt $maxGt10Rate) {
            $failures.Add(">10ms hitch rate regression: $($Metrics.rateGt10msPerSec)/s > ${maxGt10Rate}/s (baseline $($Baseline.rateGt10msPerSec)/s).")
        }
    } else {
        # Absolute competitive esports floors (calibrated from Step 0 empirical 10-hero caldavar probe)
        $absMinMed = [Math]::Round($TargetFps * 0.90, 2)
        $absMinAvg = [Math]::Round($TargetFps * 0.85, 2)
        if ([double]$Metrics.medianFps -lt $absMinMed) {
            $failures.Add("MedianFPS $($Metrics.medianFps) below absolute competitive floor $absMinMed (90% of ${TargetFps}Hz).")
        }
        if ([double]$Metrics.averageFps -lt $absMinAvg) {
            $failures.Add("AvgFPS $($Metrics.averageFps) below absolute competitive floor $absMinAvg (85% of ${TargetFps}Hz).")
        }
        if ([double]$Metrics.p1LowFps -lt 45.0) {
            $failures.Add("1% Low FPS $($Metrics.p1LowFps) below absolute competitive floor 45.0 FPS.")
        }
        if ([double]$Metrics.p99FrameTimeMs -gt 18.0) {
            $failures.Add("P99 frame time $($Metrics.p99FrameTimeMs)ms exceeds absolute competitive ceiling 18.0ms.")
        }
    }

    return [ordered]@{
        passed            = ($failures.Count -eq 0)
        isBorderline      = ($borderlineReasons.Count -gt 0)
        failures          = @($failures.ToArray())
        borderlineReasons = @($borderlineReasons.ToArray())
    }
}

function Invoke-KongorFridaAttributionPass {
    param(
        [string]$RepoRootPath,
        [int]$ProcessId,
        [int]$DurationSec = 10
    )
    $fridaPython = Join-Path $RepoRootPath "src\tools\frida\.venv\Scripts\python.exe"
    $runnerPy = Join-Path $RepoRootPath "scripts\qa\invoke_frida_perf_window.py"
    if (-not (Test-Path -LiteralPath $fridaPython -PathType Leaf)) {
        return [ordered]@{ status = "skipped"; reason = "Frida venv python not found at '$fridaPython'." }
    }
    if (-not (Test-Path -LiteralPath $runnerPy -PathType Leaf)) {
        return [ordered]@{ status = "skipped"; reason = "Frida attribution runner not found at '$runnerPy'." }
    }

    Write-Host "[SHJ-PERF][FRIDA] Running post-score ${DurationSec}s Frida stall-attribution pass on PID $ProcessId..." -ForegroundColor Magenta
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $fridaPython
    $psi.Arguments = "`"$runnerPy`" --pid $ProcessId --duration $DurationSec"
    $psi.WorkingDirectory = $RepoRootPath
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true

    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi
    try {
        [void]$proc.Start()
        $stdoutTask = $proc.StandardOutput.ReadToEndAsync()
        $stderrTask = $proc.StandardError.ReadToEndAsync()
        $exited = $proc.WaitForExit(($DurationSec + 45) * 1000)
        if (-not $exited) {
            try { $proc.Kill() } catch { Write-Verbose "[SHJ-PERF][FRIDA] Kill skipped: $($_.Exception.Message)" }
            return [ordered]@{ status = "timeout"; durationRequestedSec = $DurationSec }
        }
        $rawOut = $stdoutTask.Result.Trim()
        $rawErr = $stderrTask.Result.Trim()
        try {
            return ($rawOut | ConvertFrom-Json)
        } catch {
            return [ordered]@{ status = "error"; exitCode = $proc.ExitCode; rawOutput = $rawOut; stderr = $rawErr; parseError = $_.Exception.Message }
        }
    } finally {
        $proc.Dispose()
    }
}

function Invoke-KongorPerfCanary {
    param(
        [string]$RepoRootPath,
        [ValidateSet("offscreen_paced", "player_directflip")]
        [string]$WindowProfile = "offscreen_paced",
        [ValidateRange(1, 5)]
        [int]$Windows = 1,
        [ValidateRange(5, 30)]
        [int]$WindowSeconds = 20,
        [string]$Resolution = "2560x1440",
        [int]$TargetFps = 200,
        [switch]$Attribute,
        [switch]$CalibrateBaseline,
        [switch]$ConfirmOnScreen,
        [switch]$AllowClientRestart,
        [switch]$Force,
        [switch]$DryRun,
        [string]$OutputPath = ""
    )

    $canarySw = [System.Diagnostics.Stopwatch]::StartNew()
    $shjDir = Join-Path $RepoRootPath "logs\shj"
    if (-not (Test-Path -LiteralPath $shjDir)) {
        New-Item -ItemType Directory -Path $shjDir -Force | Out-Null
    }
    $gateDataDir = Join-Path $RepoRootPath "data\gate"
    if (-not (Test-Path -LiteralPath $gateDataDir)) {
        New-Item -ItemType Directory -Path $gateDataDir -Force | Out-Null
    }
    if ([string]::IsNullOrWhiteSpace($OutputPath)) {
        $OutputPath = Join-Path $shjDir "perf_canary_receipt.json"
    }
    $baselinesPath = Join-Path $gateDataDir "perf_baselines.json"
    $legacyBaselinesPath = Join-Path $shjDir "perf_baselines.json"
    $historyPath = Join-Path $shjDir "perf_history.jsonl"

    # Non-negotiable desktop protection gate: player_directflip MUST have explicit user permission (-ConfirmOnScreen)
    if ($WindowProfile -eq "player_directflip" -and (-not $ConfirmOnScreen.IsPresent)) {
        throw "[SHJ-PERF] BLOCKED: -WindowProfile 'player_directflip' creates a visible foreground window and requires explicit user permission (-ConfirmOnScreen). Use default '-WindowProfile offscreen_paced' for zero-window-exposure headless verification, or ask the user first and pass -ConfirmOnScreen."
    }

    $hwInfo = Get-KongorHardwareProfileKey -Resolution $Resolution -TargetFps $TargetFps -WindowProfile $WindowProfile
    $commitSha = Get-KongorGitHeadSha -RepoRootPath $RepoRootPath

    $existingClient = Get-Process -Name "hon_x64" -ErrorAction SilentlyContinue | Select-Object -First 1
    $liveClientRunning = ($null -ne $existingClient)

    if ($DryRun.IsPresent -or ((-not $liveClientRunning) -and (-not $AllowClientRestart.IsPresent))) {
        $reason = if ($DryRun.IsPresent) { "dry_run" } else { "client_offline_and_restart_not_allowed" }
        $canarySw.Stop()
        $incReceipt = [ordered]@{
            schemaVersion      = 1
            action             = "perf-canary"
            windowProfile      = $WindowProfile
            hardware           = $hwInfo
            commitSha          = $commitSha
            dryRun             = [bool]$DryRun.IsPresent
            allowClientRestart = [bool]$AllowClientRestart.IsPresent
            liveClientRunning  = $liveClientRunning
            scoredWindows      = @()
            verdict            = "inconclusive"
            status             = "inconclusive"
            inconclusiveReason = $reason
            durationMs         = [Math]::Round($canarySw.Elapsed.TotalMilliseconds, 2)
            timestampUtc       = [DateTimeOffset]::UtcNow.ToString("o")
        }
        $incReceipt | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
        return $incReceipt
    }

    # Load existing hardware-keyed baseline from tracked data/gate/perf_baselines.json (falling back to legacy logs/shj copy if present)
    $allBaselines = @{}
    $baselineReadPath = if (Test-Path -LiteralPath $baselinesPath -PathType Leaf) {
        $baselinesPath
    } elseif (Test-Path -LiteralPath $legacyBaselinesPath -PathType Leaf) {
        $legacyBaselinesPath
    } else {
        ""
    }
    if (-not [string]::IsNullOrWhiteSpace($baselineReadPath)) {
        try {
            $rawBase = Get-Content -LiteralPath $baselineReadPath -Raw -Encoding UTF8 | ConvertFrom-Json
            foreach ($prop in $rawBase.PSObject.Properties) {
                $allBaselines[$prop.Name] = $prop.Value
            }
        } catch {
            Write-Warning "[SHJ-PERF] Could not parse existing '$baselineReadPath': $($_.Exception.Message)"
        }
    }
    $activeBaseline = if ($allBaselines.ContainsKey($hwInfo.hardwareKey)) { $allBaselines[$hwInfo.hardwareKey] } else { $null }

    $previousHistoryRow = $null
    if (Test-Path -LiteralPath $historyPath -PathType Leaf) {
        $histLines = @(Get-Content -LiteralPath $historyPath -Encoding UTF8 | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        for ($hi = $histLines.Count - 1; $hi -ge 0; $hi--) {
            try {
                $cand = $histLines[$hi] | ConvertFrom-Json
                if ([string]$cand.hardwareKey -eq [string]$hwInfo.hardwareKey) {
                    $previousHistoryRow = $cand
                    break
                }
            } catch {
                Write-Verbose "[SHJ-PERF] Skipping malformed history line."
            }
        }
    }

    $kongorDll = Resolve-KongorCliDll -RepoRootPath $RepoRootPath
    $workerLockScript = Join-Path $RepoRootPath "scripts\kongor_worker_lock.ps1"
    $lockOwner = "shj-perf-canary-$PID"
    $lockAcquired = $false
    $mcpSession = $null
    $launchedDedicatedClient = $false
    $targetPid = 0

    try {
        if (Test-Path -LiteralPath $workerLockScript) {
            & $workerLockScript -Action acquire -Owner $lockOwner -Quiet
            $lockAcquired = $true
        }

        $mcpSession = Start-KongorShjMcpSession -KongorDllPath $kongorDll -RepoRootPath $RepoRootPath

        if ($AllowClientRestart.IsPresent) {
            if ($liveClientRunning) {
                if (-not $Force.IsPresent) {
                    $probeRes = Invoke-KongorShjMcpTool -Session $mcpSession -ToolName "kongor_wait_for" -Arguments @{
                        condition = "engine_ready"
                        timeout_ms = 2500
                        poll_interval_ms = 100
                    } -TimeoutMs 10000
                    $obsState = if (-not $probeRes.IsError) { Get-KongorSafeProp -Obj $probeRes.Data -Name "observedState" } else { $null }
                    $obsPhaseRaw = Get-KongorSafeProp -Obj $obsState -Name "gamePhase"
                    $obsMatchRaw = Get-KongorSafeProp -Obj $obsState -Name "activeMatchId"
                    $obsPhase = if ($null -ne $obsPhaseRaw) { [int]$obsPhaseRaw } else { -1 }
                    $obsMatch = if ($null -ne $obsMatchRaw) { [long]$obsMatchRaw } else { 0L }
                    # Fail-closed: refuse restarting a running client unless we positively confirmed it is NOT in a match/draft
                    if ($probeRes.IsError -or $obsPhase -lt 0 -or $obsPhase -ge 2 -or $obsMatch -gt 0) {
                        throw "[SHJ-PERF] BLOCKED: Could not confirm running hon_x64.exe is idle outside a match (probeError=$($probeRes.IsError), gamePhase=$obsPhase, activeMatchId=$obsMatch). Refusing -AllowClientRestart without -Force."
                    }
                }
                Write-Host "[SHJ-PERF] Stopping existing hon_x64.exe before clean perf_clean launch..." -ForegroundColor Cyan
                [void](Invoke-KongorShjMcpTool -Session $mcpSession -ToolName "kongor_game_client_manage" -Arguments @{ action = "stop" } -TimeoutMs 20000)
                Start-Sleep -Milliseconds 750
            }

            $launchWindowMode = if ($WindowProfile -eq "player_directflip") { "foreground" } else { "offscreen" }
            Write-Host "[SHJ-PERF] Launching hon_x64.exe (profile='perf_clean', windowMode='$launchWindowMode', res='$Resolution', maxFps=$TargetFps, enforceFramePacing=true, enableReplayCapture=false)..." -ForegroundColor Cyan
            $launchRes = Invoke-KongorShjMcpTool -Session $mcpSession -ToolName "kongor_game_client_manage" -Arguments @{
                action              = "launch"
                window_mode         = $launchWindowMode
                launchProfile       = "perf_clean"
                enableReplayCapture = $false
                enforceFramePacing  = $true
                resolution          = $Resolution
                maxFps              = $TargetFps
                masterServer        = "127.0.0.1:55555"
                slot                = 0
                autoLogin           = $true
            } -TimeoutMs 35000
            $launchStatus = [string](Get-KongorSafeProp -Obj $launchRes.Data -Name "status")
            if ($launchRes.IsError -or $launchStatus -eq "error") {
                throw "[SHJ-PERF] Client launch failed: $($launchRes.RawText)"
            }
            $launchedDedicatedClient = $true
            $launchedPidProp = Get-KongorSafeProp -Obj $launchRes.Data -Name "processId"
            if ($null -ne $launchedPidProp) {
                $targetPid = [int]$launchedPidProp
            }
        }

        # Wait for IPC, engine, and UI readiness
        Write-Host "[SHJ-PERF] Waiting for ipc_ready -> engine_ready -> client_ready..." -ForegroundColor Cyan
        $rIpc = Invoke-KongorShjMcpTool -Session $mcpSession -ToolName "kongor_wait_for" -Arguments @{ condition = "ipc_ready"; timeout_ms = 60000; poll_interval_ms = 100 } -TimeoutMs 65000
        if ($rIpc.IsError) { throw "[SHJ-PERF] ipc_ready failed: $($rIpc.RawText)" }

        $rEng = Invoke-KongorShjMcpTool -Session $mcpSession -ToolName "kongor_wait_for" -Arguments @{ condition = "engine_ready"; timeout_ms = 60000; poll_interval_ms = 100 } -TimeoutMs 65000
        if ($rEng.IsError) { throw "[SHJ-PERF] engine_ready failed: $($rEng.RawText)" }

        if ($targetPid -le 0) {
            $hp = Get-Process -Name "hon_x64" -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($null -ne $hp) { $targetPid = $hp.Id }
        }

        # Apply canonical competitive High-Hz CVars so stale startup_headless.cfg values cannot silently override the canary
        Write-Host "[SHJ-PERF] Enforcing competitive High-Hz CVars via native pipe (host_maxFPS=$TargetFps, cg_interfaceFPS=$TargetFps, vid_vsync=false, vid_antialiasing=2, vid_shadowmapSize=2048)..." -ForegroundColor Cyan
        $cvarCmds = @(
            "Set host_maxFPS $TargetFps",
            "Set host_maximumFPS $TargetFps",
            "Set vid_maxFPS $TargetFps",
            "Set cl_maxFPS $TargetFps",
            "Set cg_interfaceFPS $TargetFps",
            "Set vid_vsync false",
            "Set vid_antialiasing 2",
            "Set vid_shadowmapSize 2048",
            "Set vid_reflectionMapSize 1024"
        )
        foreach ($cmd in $cvarCmds) {
            [void](Invoke-KongorShjMcpTool -Session $mcpSession -ToolName "kongor_game_command" -Arguments @{ command = $cmd } -TimeoutMs 10000)
        }
        Start-Sleep -Milliseconds 800

        # Mandatory pre-score Runtime Fingerprint Audit (I7_LaunchPlanAndRuntimeParity)
        Write-Host "[SHJ-PERF] Executing mandatory Runtime Fingerprint Audit (I7)..." -ForegroundColor Cyan
        $runtimeAudit = Invoke-KongorRuntimeAudit `
            -RepoRootPath $RepoRootPath `
            -Profile "perf_clean" `
            -ExpectedMaxFps $TargetFps `
            -ExpectedInterfaceFps $TargetFps `
            -ExpectedMaxAntialiasing 2 `
            -ExpectedVsync 0 `
            -RequireZeroWindowExposure ($WindowProfile -eq "offscreen_paced") `
            -McpSession $mcpSession `
            -OutputPath (Join-Path $shjDir "runtime_audit_receipt.json")

        if ([string]$runtimeAudit.verdict -ne "passed") {
            throw "[SHJ-PERF] I7 Runtime Fingerprint Audit FAILED before scoring: $($runtimeAudit.findings -join ' | ')"
        }
        Write-Host "[SHJ-PERF] I7 Runtime Fingerprint Audit PASSED (PID=$($runtimeAudit.processId), VkLayerLoaded=$($runtimeAudit.actualVkLayerReplayCaptureLoaded), DeniedHooks=0, ZeroWindowExposure=$($runtimeAudit.windowExposure.zeroWindowExposure), Hwnd=$($runtimeAudit.windowExposure.mainWindowHandle), Desktop=$($runtimeAudit.windowExposure.windowDesktop), Method=$($runtimeAudit.windowExposure.enumerationMethod))." -ForegroundColor Green

        # Start deterministic 10-hero caldavar bot match:
        # Zero hero-select countdown (so GamePhase=3 starts in ~8s), and set sv_preMatchTime=180000 (ms = 3 min)
        # so all 3x20s calibration windows run under an identical steady-state 10-hero workload
        # without an embedded listen-server creep-wave spawn cliff hitting mid-calibration.
        Write-Host "[SHJ-PERF] Launching deterministic 10-hero caldavar bot match..." -ForegroundColor Cyan
        [void](Invoke-KongorShjMcpTool -Session $mcpSession -ToolName "kongor_game_command" -Arguments @{ command = "Set sv_heroSelectTime 0; Set sv_heroSelectInitialTime 0; Set sv_heroSelectAllReadyTime 0; Set sv_preMatchTime 180000; Set sv_gameCountdownLength 0; Set sv_gameStartCountdown 0" } -TimeoutMs 10000)
        $startMatchCmd = "StartGame practice LocalBotsGame mode:botmatch map:caldavar teamsize:5 casual:true allheroes:true bots1:AccursedBot|AlunaBot|AndromedaBot|GlaciusBot bots2:ArachnaBot|ArmadonBot|BehemothBot|HammerstormBot|PyromancerBot botdifficulty:2"
        [void](Invoke-KongorShjMcpTool -Session $mcpSession -ToolName "kongor_game_command" -Arguments @{ command = $startMatchCmd } -TimeoutMs 15000)

        # Stage A: Wait until caldavar map finishes loading via kongor_wait_for(condition='map_loaded')
        Write-Host "[SHJ-PERF] Waiting for caldavar map load (condition='map_loaded')..." -ForegroundColor Cyan
        $mapWait = Invoke-KongorShjMcpTool -Session $mcpSession -ToolName "kongor_wait_for" -Arguments @{
            condition        = "map_loaded"
            target           = "caldavar"
            timeout_ms       = 45000
            poll_interval_ms = 250
        } -TimeoutMs 50000
        if ($mapWait.IsError) {
            throw "[SHJ-PERF] Timed out waiting for caldavar map load: $($mapWait.RawText)"
        }

        $lobbyAdvanceLua = @"
if Cmd then
    Cmd('Set sv_heroSelectTime 0')
    Cmd('Set sv_heroSelectInitialTime 0')
    Cmd('Set sv_heroSelectAllReadyTime 0')
    Cmd('Set sv_preMatchTime 180000')
    Cmd('Set sv_gameCountdownLength 0')
    Cmd('Set sv_gameStartCountdown 0')
    Cmd('SelectHero Hero_Pyromancer')
    Cmd('SpawnHero Hero_Pyromancer')
    Cmd('RequestMatchStart')
    Cmd('StartMatch')
end
local main = UIManager and UIManager.GetInterface and UIManager.GetInterface('main')
if main and main.UICmd then
    pcall(main.UICmd, main, "Team(1, 0); RequestMatchStart(); SpawnHero('Hero_Pyromancer'); Ready();")
end
return 'ok'
"@
        Write-Host "[SHJ-PERF] Map 'caldavar' loaded! Fast-advancing hero select via Lua UICmd + zero countdown..." -ForegroundColor Cyan
        [void](Invoke-KongorShjMcpTool -Session $mcpSession -ToolName "kongor_client_eval_lua" -Arguments @{ code = $lobbyAdvanceLua } -TimeoutMs 10000)

        # Stage B: Wait for in-world active match (gamePhase == 3 AND avgDrawCalls >= 800 to skip Hero Select!)
        $inWorldReady = $false
        $phaseWaitSw = [System.Diagnostics.Stopwatch]::StartNew()
        $waitDeadline = [DateTimeOffset]::UtcNow.AddSeconds(75)
        $lastPickRetry = [DateTimeOffset]::UtcNow
        while ([DateTimeOffset]::UtcNow -lt $waitDeadline) {
            Start-Sleep -Milliseconds 1000
            $probe = Invoke-KongorShjMcpTool -Session $mcpSession -ToolName "kongor_wait_for" -Arguments @{ condition = "engine_ready"; timeout_ms = 2000; poll_interval_ms = 100 } -TimeoutMs 8000
            $curPhase = -1
            $probeObs = if (-not $probe.IsError) { Get-KongorSafeProp -Obj $probe.Data -Name "observedState" } else { $null }
            $probePhaseVal = Get-KongorSafeProp -Obj $probeObs -Name "gamePhase"
            if ($null -ne $probePhaseVal) {
                $curPhase = [int]$probePhaseVal
            }
            if ($curPhase -eq 3) {
                # Verify real 3D world rendering (avgDrawCalls >= 800) so we never score a loading/select overlay
                $quickCheck = Invoke-KongorShjMcpTool -Session $mcpSession -ToolName "kongor_perf_window" -Arguments @{
                    action     = "measure"
                    durationMs = 1500
                    pid        = $targetPid
                    detail     = "full"
                } -TimeoutMs 15000
                $quickDraws = 0.0
                $quickSum = if (-not $quickCheck.IsError) { Get-KongorSafeProp -Obj $quickCheck.Data -Name "summary" } else { $null }
                if ($null -ne $quickSum) {
                    $quickDraws = [double](Get-KongorSafeProp -Obj $quickSum -Name "avgDrawCalls")
                }
                Write-Host ("[SHJ-PERF] GamePhase=3 probe ({0:N1}s after map load): avgDrawCalls={1}" -f $phaseWaitSw.Elapsed.TotalSeconds, $quickDraws) -ForegroundColor DarkCyan
                if ($quickDraws -ge 800.0) {
                    $inWorldReady = $true
                    break
                }
            } elseif (([DateTimeOffset]::UtcNow - $lastPickRetry).TotalSeconds -ge 3.0) {
                Write-Host ("[SHJ-PERF] Waiting for GamePhase=3 (current={0}, elapsed={1:N1}s); re-sending Lua UICmd hero-select advance..." -f $curPhase, $phaseWaitSw.Elapsed.TotalSeconds) -ForegroundColor DarkGray
                [void](Invoke-KongorShjMcpTool -Session $mcpSession -ToolName "kongor_client_eval_lua" -Arguments @{ code = $lobbyAdvanceLua } -TimeoutMs 10000)
                $lastPickRetry = [DateTimeOffset]::UtcNow
            }
        }
        $phaseWaitSw.Stop()
        if (-not $inWorldReady) {
            throw "[SHJ-PERF] Timed out waiting for in-world 10-hero caldavar rendering (gamePhase==3 && avgDrawCalls>=800)."
        }

        Write-Host ("[SHJ-PERF] In-world 3D caldavar confirmed in {0:N1}s! Waiting 20s for shader/spawn steady-state settle (Frida OFF)..." -f $phaseWaitSw.Elapsed.TotalSeconds) -ForegroundColor Cyan
        Start-Sleep -Seconds 20

        # Execute scored windows:
        # - Adaptive mode: 1 window fast pass, auto-escalate to 3 windows on borderline/fail.
        # - Calibration mode (-CalibrateBaseline): collect $targetWindows (default 3) clean passing windows;
        #   allow at most 1 automatic warmup discard if Attempt #1 catches late post-spawn shader compilation.
        $spyLogPath = Join-Path $RepoRootPath "games\client\vid_vulkan_spy.log"
        $targetWindows = if ($CalibrateBaseline.IsPresent) { [Math]::Max(3, $Windows) } else { [Math]::Max(1, $Windows) }
        $adaptiveMode = (-not $CalibrateBaseline.IsPresent) -and ($Windows -eq 1)
        $maxCalibrationWarmupDiscards = if ($CalibrateBaseline.IsPresent) { 1 } else { 0 }
        $maxAttempts = if ($adaptiveMode) { 3 } else { $targetWindows + $maxCalibrationWarmupDiscards }

        $scoredWindows = New-Object System.Collections.Generic.List[object]
        $discardedWarmupWindows = New-Object System.Collections.Generic.List[object]
        $escalatedToThree = $false

        for ($attemptIdx = 1; $attemptIdx -le $maxAttempts; $attemptIdx++) {
            if ((-not $adaptiveMode) -and ($scoredWindows.Count -ge $targetWindows)) {
                break
            }
            $wIdx = $scoredWindows.Count + 1
            $spyOffsetBefore = if (Test-Path -LiteralPath $spyLogPath -PathType Leaf) { (Get-Item -LiteralPath $spyLogPath).Length } else { 0L }
            Write-Host ("[SHJ-PERF] Scoring Window #{0} (attempt {1}/{2}, {3}s, Frida OFF, FastBus lossless drain + vid_vulkan_spy cross-check)..." -f $wIdx, $attemptIdx, $maxAttempts, $WindowSeconds) -ForegroundColor Cyan

            $pwRes = Invoke-KongorShjMcpTool -Session $mcpSession -ToolName "kongor_perf_window" -Arguments @{
                action                = "measure"
                durationMs            = ($WindowSeconds * 1000)
                pid                   = $targetPid
                includeModuleAudit    = $true
                expectedVkLayerLoaded = $false
                detail                = "full"
            } -TimeoutMs (($WindowSeconds * 1000) + 25000)

            $sum = if (-not $pwRes.IsError) { Get-KongorSafeProp -Obj $pwRes.Data -Name "summary" } else { $null }
            if ($pwRes.IsError -or $null -eq $sum) {
                throw "[SHJ-PERF] Window #$wIdx kongor_perf_window measure failed: $($pwRes.RawText)"
            }

            Start-Sleep -Milliseconds 250
            $spyStats = Read-KongorSpyLogWindowMetrics -SpyLogPath $spyLogPath -StartByteOffset $spyOffsetBefore
            $fastBusAvgFps = [double]$sum.averageFps
            $spyDivPct = if ([bool]$spyStats.available -and [double]$spyStats.harmonicAvgFps -gt 0.1) {
                [Math]::Round(([Math]::Abs($fastBusAvgFps - [double]$spyStats.harmonicAvgFps) / [double]$spyStats.harmonicAvgFps) * 100.0, 2)
            } else { 0.0 }

            $modAuditObj = Get-KongorSafeProp -Obj $pwRes.Data -Name "moduleAudit"
            $modAuditPassed = if ($null -ne $modAuditObj) {
                [bool](Get-KongorSafeProp -Obj $modAuditObj -Name "passed")
            } else { $null }

            $winMetrics = [ordered]@{
                windowIndex           = $wIdx
                attemptIndex          = $attemptIdx
                telemetrySource       = [string]$sum.telemetrySource
                isRenderFrameAccurate = [bool]$sum.isRenderFrameAccurate
                sampledFrames         = [int]$sum.sampledFrames
                engineFramesElapsed   = [uint64]$sum.engineFramesElapsed
                droppedRingFrames     = [uint64]$sum.droppedRingFrames
                averageFps            = [double]$sum.averageFps
                medianFps             = [double]$sum.medianFps
                p1LowFps              = [double]$sum.p1LowFps
                p01LowFps             = [double]$sum.p01LowFps
                avgFrameTimeMs        = [double]$sum.avgFrameTimeMs
                p50FrameTimeMs        = [double]$sum.p50FrameTimeMs
                p95FrameTimeMs        = [double]$sum.p95FrameTimeMs
                p99FrameTimeMs        = [double]$sum.p99FrameTimeMs
                p999FrameTimeMs       = [double]$sum.p999FrameTimeMs
                maxFrameTimeMs        = [double]$sum.maxFrameTimeMs
                avgCpuEngineMs        = [double]$sum.avgCpuEngineMs
                avgGpuRenderMs        = [double]$sum.avgGpuRenderMs
                avgPresentWaitMs      = [double]$sum.avgPresentWaitMs
                avgDrawCalls          = [double]$sum.avgDrawCalls
                gt8ms                 = [int]$sum.hitches.gt8ms
                gt10ms                = [int]$sum.hitches.gt10ms
                gt16_67ms             = [int]$sum.hitches.gt16_67ms
                gt33_33ms             = [int]$sum.hitches.gt33_33ms
                gt50ms                = [int]$sum.hitches.gt50ms
                rateGt8msPerSec       = [double]$sum.hitches.rateGt8msPerSec
                rateGt10msPerSec      = [double]$sum.hitches.rateGt10msPerSec
                cpuStallCount         = [int]$sum.sentinelHitchFlags.cpuStall
                gpuStallCount         = [int]$sum.sentinelHitchFlags.gpuStall
                dwmWaitCount          = [int]$sum.sentinelHitchFlags.dwmWait
                preemptionCount       = [int]$sum.sentinelHitchFlags.preemption
                budgetViolationsCount = [int]$sum.budgetViolationsCount
                frameBudgetPassed     = [bool]$sum.frameBudgetPassed
                spyAvailable          = [bool]$spyStats.available
                spyHarmonicAvgFps     = [double]$spyStats.harmonicAvgFps
                spyWeightedAvgDraws   = [double]$spyStats.weightedAvgDraws
                spyDivergencePct      = $spyDivPct
                moduleAuditPassed     = $modAuditPassed
            }

            $winEval = Evaluate-KongorPerfWindowAgainstBudgets -Metrics $winMetrics -TargetFps $TargetFps -WindowSeconds $WindowSeconds -Baseline $(if ($CalibrateBaseline.IsPresent) { $null } else { $activeBaseline })
            $winMetrics["evaluation"] = $winEval

            Write-Host ("[SHJ-PERF] Window #{0} (attempt {1}): Avg={2} FPS (Spy={3} FPS, div={4}%), Med={5} FPS, 1%Low={6}, 0.1%Low={7}, P99={8}ms, P99.9={9}ms, Max={10}ms, >10ms={11}/s, >33ms={12}, gpuStall={13}, draws={14}, drops={15}, pass={16}" -f `
                $wIdx, $attemptIdx, $winMetrics.averageFps, $winMetrics.spyHarmonicAvgFps, $winMetrics.spyDivergencePct, $winMetrics.medianFps, `
                $winMetrics.p1LowFps, $winMetrics.p01LowFps, $winMetrics.p99FrameTimeMs, $winMetrics.p999FrameTimeMs, `
                $winMetrics.maxFrameTimeMs, $winMetrics.rateGt10msPerSec, $winMetrics.gt33_33ms, $winMetrics.gpuStallCount, `
                $winMetrics.avgDrawCalls, $winMetrics.droppedRingFrames, $winEval.passed) -ForegroundColor $(if ($winEval.passed) { "Green" } else { "Yellow" })

            if ($CalibrateBaseline.IsPresent -and (-not $winEval.passed) -and $discardedWarmupWindows.Count -lt $maxCalibrationWarmupDiscards) {
                Write-Host ("[SHJ-PERF] Calibration transient discard: Attempt #{0} failed ({1}); discarding outlier window and collecting {2} clean steady-state windows..." -f $attemptIdx, ($winEval.failures -join "; "), $targetWindows) -ForegroundColor Yellow
                $discardedWarmupWindows.Add($winMetrics)
                continue
            }

            $scoredWindows.Add($winMetrics)

            if ($adaptiveMode -and $scoredWindows.Count -eq 1) {
                if ($winEval.passed -and (-not $winEval.isBorderline)) {
                    Write-Host "[SHJ-PERF] Adaptive fast-path: Window #1 passed cleanly with comfortable headroom; skipping confirmation windows #2-#3." -ForegroundColor Green
                    break
                } else {
                    $escalatedToThree = $true
                    Write-Host ("[SHJ-PERF] Adaptive confirmation triggered ({0}); running Windows #2 & #3 for 3-window median verdict..." -f $(if (-not $winEval.passed) { "Window #1 failed: $($winEval.failures -join '; ')" } else { "Borderline: $($winEval.borderlineReasons -join '; ')" })) -ForegroundColor Yellow
                }
            }
        }

        # Aggregate across all scored windows (single window or median of 3 windows)
        $wArr = @($scoredWindows.ToArray())
        $agg = Merge-KongorPerfWindowMetrics -Windows $wArr -EscalatedToThree $escalatedToThree

        $finalEval = Evaluate-KongorPerfWindowAgainstBudgets -Metrics $agg -TargetFps $TargetFps -WindowSeconds $WindowSeconds -Baseline $(if ($CalibrateBaseline.IsPresent) { $null } else { $activeBaseline })

        # Calibration hygiene gate: refuse baseline write if ANY kept calibration window failed a hard invariant
        if ($CalibrateBaseline.IsPresent) {
            $failedCalWindows = @($wArr | Where-Object { -not [bool]$_["evaluation"]["passed"] })
            if ($wArr.Count -lt $targetWindows -or $failedCalWindows.Count -gt 0) {
                $calFailList = New-Object System.Collections.Generic.List[string]
                foreach ($f in $finalEval.failures) { $calFailList.Add($f) }
                if ($wArr.Count -lt $targetWindows) {
                    $calFailList.Add("Calibration collected only $($wArr.Count)/$targetWindows clean steady-state windows.")
                }
                if ($failedCalWindows.Count -gt 0) {
                    $calFailList.Add("Calibration rejected: $($failedCalWindows.Count)/$($wArr.Count) kept window(s) failed hard invariants; refusing to write dirty baseline.")
                }
                $finalEval = [ordered]@{
                    passed            = $false
                    isBorderline      = [bool]$finalEval.isBorderline
                    failures          = @($calFailList.ToArray())
                    borderlineReasons = @($finalEval.borderlineReasons)
                }
            }
        }

        # Post-score Frida attribution pass ONLY if -Attribute was requested OR budget failed (never during scored windows!)
        $fridaAttribution = $null
        if ($Attribute.IsPresent -or (-not $finalEval.passed)) {
            $fridaAttribution = Invoke-KongorFridaAttributionPass -RepoRootPath $RepoRootPath -ProcessId $targetPid -DurationSec 10
        }

        # Calibrate hardware-keyed baseline if requested and ALL calibration windows passed cleanly
        $baselineUpdated = $false
        if ($CalibrateBaseline.IsPresent -and $finalEval.passed) {
            $allBaselines[$hwInfo.hardwareKey] = [ordered]@{
                hardwareKey      = $hwInfo.hardwareKey
                gpuName          = $hwInfo.gpuName
                resolution       = $hwInfo.resolution
                targetFps        = $TargetFps
                windowProfile    = $WindowProfile
                calibratedAtUtc  = [DateTimeOffset]::UtcNow.ToString("o")
                commitSha        = $commitSha
                windowsAveraged  = $agg.windowsRun
                averageFps       = $agg.averageFps
                medianFps        = $agg.medianFps
                p1LowFps         = $agg.p1LowFps
                p01LowFps        = $agg.p01LowFps
                p50FrameTimeMs   = $agg.p50FrameTimeMs
                p95FrameTimeMs   = $agg.p95FrameTimeMs
                p99FrameTimeMs   = $agg.p99FrameTimeMs
                p999FrameTimeMs  = $agg.p999FrameTimeMs
                maxFrameTimeMs   = $agg.maxFrameTimeMs
                rateGt8msPerSec  = $agg.rateGt8msPerSec
                rateGt10msPerSec = $agg.rateGt10msPerSec
                avgDrawCalls     = $agg.avgDrawCalls
            }
            $allBaselines | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $baselinesPath -Encoding UTF8
            $activeBaseline = $allBaselines[$hwInfo.hardwareKey]
            $baselineUpdated = $true
            Write-Host "[SHJ-PERF] Calibrated hardware baseline '$($hwInfo.hardwareKey)' in tracked '$baselinesPath'." -ForegroundColor Green
        }

        $verdict = if ($finalEval.passed) { "passed" } else { "failed" }
        $vidMod = $runtimeAudit.keyModules | Where-Object { $_.moduleName -eq "vid_vulkan_x64.dll" } | Select-Object -First 1
        $vidSha = if ($null -ne $vidMod) { [string]$vidMod.loadedSha256 } else { "" }

        # Append rolling history JSONL entry
        $historyRow = [ordered]@{
            timestampUtc     = [DateTimeOffset]::UtcNow.ToString("o")
            commitSha        = $commitSha
            hardwareKey      = $hwInfo.hardwareKey
            windowProfile    = $WindowProfile
            verdict          = $verdict
            windowsRun       = $agg.windowsRun
            averageFps       = $agg.averageFps
            medianFps        = $agg.medianFps
            p1LowFps         = $agg.p1LowFps
            p01LowFps        = $agg.p01LowFps
            p95FrameTimeMs   = $agg.p95FrameTimeMs
            p99FrameTimeMs   = $agg.p99FrameTimeMs
            p999FrameTimeMs  = $agg.p999FrameTimeMs
            maxFrameTimeMs   = $agg.maxFrameTimeMs
            rateGt8msPerSec  = $agg.rateGt8msPerSec
            rateGt10msPerSec = $agg.rateGt10msPerSec
            gt16_67ms        = $agg.gt16_67ms
            gt33_33ms        = $agg.gt33_33ms
            gpuStall         = $agg.gpuStallCount
            cpuStall         = $agg.cpuStallCount
            spyDivergencePct = $agg.spyDivergencePct
            vkLayerLoaded    = [bool]$runtimeAudit.actualVkLayerReplayCaptureLoaded
            vidVulkanSha256  = $vidSha
        }
        Add-Content -LiteralPath $historyPath -Value ($historyRow | ConvertTo-Json -Depth 6 -Compress) -Encoding UTF8

        # Print Delta Table vs. Previous Run and Hardware Baseline
        Write-Host "`n================ [SHJ-PERF] COMPETITIVE HIGH-HZ DELTA SUMMARY ================" -ForegroundColor Cyan
        Write-Host (" Hardware Key : {0} | Commit: {1} | Verdict: {2}" -f $hwInfo.hardwareKey, $commitSha, $verdict.ToUpperInvariant()) -ForegroundColor $(if ($verdict -eq "passed") { "Green" } else { "Red" })
        $fmtDelta = {
            param([double]$Cur, [object]$RefObj, [string]$Prop, [string]$Unit)
            if ($null -eq $RefObj) { return "n/a" }
            $hasProp = $false
            $refVal = 0.0
            if ($RefObj -is [System.Collections.IDictionary]) {
                if ($RefObj.Contains($Prop)) {
                    $hasProp = $true
                    $refVal = [double]$RefObj[$Prop]
                }
            } elseif ($null -ne $RefObj.PSObject.Properties[$Prop]) {
                $hasProp = $true
                $refVal = [double]$RefObj.$Prop
            }
            if (-not $hasProp) { return "n/a" }
            $diff = [Math]::Round($Cur - $refVal, 2)
            $sign = if ($diff -ge 0) { "+" } else { "" }
            return ("{0}{1}{2}" -f $sign, $diff, $Unit)
        }
        Write-Host (" Metric         | Current    | vs Prev Run | vs Baseline") -ForegroundColor White
        Write-Host (" ---------------|------------|-------------|------------") -ForegroundColor DarkGray
        Write-Host (" Median FPS     | {0,10} | {1,11} | {2,11}" -f $agg.medianFps, (& $fmtDelta $agg.medianFps $previousHistoryRow "medianFps" ""), (& $fmtDelta $agg.medianFps $activeBaseline "medianFps" ""))
        Write-Host (" Average FPS    | {0,10} | {1,11} | {2,11}" -f $agg.averageFps, (& $fmtDelta $agg.averageFps $previousHistoryRow "averageFps" ""), (& $fmtDelta $agg.averageFps $activeBaseline "averageFps" ""))
        Write-Host (" 1% Low FPS     | {0,10} | {1,11} | {2,11}" -f $agg.p1LowFps, (& $fmtDelta $agg.p1LowFps $previousHistoryRow "p1LowFps" ""), (& $fmtDelta $agg.p1LowFps $activeBaseline "p1LowFps" ""))
        Write-Host (" 0.1% Low FPS   | {0,10} | {1,11} | {2,11}" -f $agg.p01LowFps, (& $fmtDelta $agg.p01LowFps $previousHistoryRow "p01LowFps" ""), (& $fmtDelta $agg.p01LowFps $activeBaseline "p01LowFps" ""))
        Write-Host (" P99 Frame ms   | {0,10} | {1,11} | {2,11}" -f $agg.p99FrameTimeMs, (& $fmtDelta $agg.p99FrameTimeMs $previousHistoryRow "p99FrameTimeMs" "ms"), (& $fmtDelta $agg.p99FrameTimeMs $activeBaseline "p99FrameTimeMs" "ms"))
        Write-Host (" P99.9 Frame ms | {0,10} | {1,11} | {2,11}" -f $agg.p999FrameTimeMs, (& $fmtDelta $agg.p999FrameTimeMs $previousHistoryRow "p999FrameTimeMs" "ms"), (& $fmtDelta $agg.p999FrameTimeMs $activeBaseline "p999FrameTimeMs" "ms"))
        Write-Host (" >10ms Rate/s   | {0,10} | {1,11} | {2,11}" -f $agg.rateGt10msPerSec, (& $fmtDelta $agg.rateGt10msPerSec $previousHistoryRow "rateGt10msPerSec" "/s"), (& $fmtDelta $agg.rateGt10msPerSec $activeBaseline "rateGt10msPerSec" "/s"))
        Write-Host (" Spy Divergence | {0,9}% |     <= 3.0% |  drops={1}" -f $agg.spyDivergencePct, $agg.droppedRingFrames)
        Write-Host "==============================================================================`n" -ForegroundColor Cyan

        $canarySw.Stop()
        $receipt = [ordered]@{
            schemaVersion    = 1
            action           = "perf-canary"
            windowProfile    = $WindowProfile
            hardware         = $hwInfo
            commitSha        = $commitSha
            processId        = $targetPid
            runtimeAudit     = $runtimeAudit
            aggregated             = $agg
            evaluation             = $finalEval
            scoredWindows          = $wArr
            discardedWarmupWindows = @($discardedWarmupWindows.ToArray())
            baselineUsed           = $activeBaseline
            baselineUpdated        = $baselineUpdated
            previousRun            = $previousHistoryRow
            fridaAttribution       = $fridaAttribution
            historyPath            = (ConvertTo-KongorSanitizedPath -InputPath $historyPath)
            baselinesPath          = (ConvertTo-KongorSanitizedPath -InputPath $baselinesPath)
            heroSelectWaitSeconds  = [Math]::Round($phaseWaitSw.Elapsed.TotalSeconds, 2)
            verdict                = $verdict
            status           = $verdict
            durationMs       = [Math]::Round($canarySw.Elapsed.TotalMilliseconds, 2)
            timestampUtc     = [DateTimeOffset]::UtcNow.ToString("o")
        }

        $receipt | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
        return $receipt
    } finally {
        if ($launchedDedicatedClient -and $null -ne $mcpSession) {
            try {
                Write-Host "[SHJ-PERF] Stopping dedicated perf-canary hon_x64.exe (PID=$targetPid) cleanly..." -ForegroundColor Cyan
                $stopArgs = @{ action = "stop" }
                if ($targetPid -gt 0) { $stopArgs["processId"] = $targetPid }
                [void](Invoke-KongorShjMcpTool -Session $mcpSession -ToolName "kongor_game_client_manage" -Arguments $stopArgs -TimeoutMs 15000)
            } catch {
                Write-Warning "[SHJ-PERF] Stop via MCP failed ($($_.Exception.Message)); ensuring hon_x64 PID $targetPid is terminated..."
                if ($targetPid -gt 0) {
                    Stop-Process -Id $targetPid -Force -ErrorAction SilentlyContinue
                }
            }
        }
        Stop-KongorShjMcpSession -Session $mcpSession
        if ($lockAcquired -and (Test-Path -LiteralPath $workerLockScript)) {
            try { & $workerLockScript -Action release -Owner $lockOwner -Quiet } catch { Write-Verbose "[SHJ-PERF] Lock release skipped: $($_.Exception.Message)" }
        }
    }
}
