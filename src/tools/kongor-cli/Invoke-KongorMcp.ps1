# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

function Invoke-KongorMcp {
    param(
        [Parameter(Mandatory=$true)]
        [string]$ToolName,
        [hashtable]$Arguments = @{}
    )

    $rpcPayload = @{
        jsonrpc = "2.0"
        id = [Guid]::NewGuid().ToString("N")
        method = "tools/call"
        params = @{
            name = $ToolName
            arguments = $Arguments
        }
    } | ConvertTo-Json -Depth 10 -Compress

    $initPayload = @{
        jsonrpc = "2.0"
        id = "init"
        method = "initialize"
        params = @{
            protocolVersion = "2024-11-05"
            capabilities = @{}
            clientInfo = @{
                name = "Antigravity"
                version = "1.0"
            }
        }
    } | ConvertTo-Json -Depth 5 -Compress

    $repoRoot = (Resolve-Path "$PSScriptRoot\..\..\..").Path
    $pyPath = Join-Path $repoRoot "src\tools\comfyui\.venv\Scripts\python.exe"
    if (-not (Test-Path $pyPath)) {
        $pyPath = "python.exe"
    }
    $mcpPath = Join-Path $repoRoot "src\tools\kongor\mcp_server.py"

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $pyPath
    $psi.Arguments = $mcpPath
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $false
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true

    $proc = [System.Diagnostics.Process]::Start($psi)
    $proc.StandardInput.WriteLine($initPayload)
    $proc.StandardInput.Flush()
    $null = $proc.StandardOutput.ReadLine()

    $proc.StandardInput.WriteLine($rpcPayload)
    $proc.StandardInput.Flush()
    $responseJson = $proc.StandardOutput.ReadLine()

    $proc.StandardInput.Close()
    $proc.WaitForExit(3000)

    if ($responseJson) {
        $parsed = $responseJson | ConvertFrom-Json
        if ($parsed.result.content -and $parsed.result.content.Count -gt 0) {
            $parsedText = $parsed.result.content[0].text
            try {
                return ($parsedText | ConvertFrom-Json)
            } catch {
                return $parsedText
            }
        }
        return $parsed
    }
    return $null
}
