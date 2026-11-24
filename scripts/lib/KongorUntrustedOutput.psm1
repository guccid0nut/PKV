# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Structural <UNTRUSTED_TOOL_OUTPUT> spotlighting, special-token defanging, ANSI/Unicode sanitizer,
    and 12 KB bounded disk-spill helper for Project Kongor (#38, #44, #52, #112).

.DESCRIPTION
    Protects AI agent contexts against indirect prompt injection from game logs (console.log,
    client_lua_debug.log), replay chat/player names, and extracted binary strings:
    - Remove-KongorAnsiAndInvisibleChars: Strips ANSI escape sequences and Unicode invisible tag
      characters (U+E0000..U+E007F) and zero-width formatting characters.
    - ConvertTo-KongorDefangedTokens: Neutralizes chat-template special tokens (<|im_start|>,
      <|endoftext|>, [INST], </UNTRUSTED_TOOL_OUTPUT>).
    - Test-KongorPromptInjection: 5-category prompt-injection detector (role_override,
      system_tag_spoof, tool_call_spoof, destructive_cmd_lure, secret_exfil_lure).
    - Format-KongorUntrustedOutput: Wraps sanitized text inside <UNTRUSTED_TOOL_OUTPUT> with
      telemetry attributes and optional 12 KB head/tail disk spill (#44).
#>

$script:KongorPromptInjectionRules = @(
    @{ Category = 'role_override';        Pattern = '(?i)\b(?:ignore|disregard|forget)\s+(?:all\s+)?(?:previous|prior|above|system)\s+(?:instructions|prompts|rules|directives)\b|\byou\s+are\s+now\s+(?:a|an|in)\s+[a-z0-9_-]+\s+mode\b' },
    @{ Category = 'system_tag_spoof';     Pattern = '(?i)<\|\s*(?:im_start|im_end|endoftext|system|assistant|user|tool_call)\s*\|>|\[/?INST\]|<<SYS>>|<\|begin_of_text\|>|<\s*/?\s*UNTRUSTED_TOOL_OUTPUT\b' },
    @{ Category = 'tool_call_spoof';      Pattern = '(?i)\b(?:run_command|write_to_file|replace_file_content|call_mcp_tool|invoke_subagent)\s*\(|\{"tool_calls"\s*:|<tool_call>' },
    @{ Category = 'destructive_cmd_lure'; Pattern = '(?i)\b(?:git\s+reset\s+--hard|Remove-Item\s+[^\r\n]*-Recurse[^\r\n]*-Force|Format-Volume|Clear-Disk|rm\s+-rf\s+/)\b' },
    @{ Category = 'secret_exfil_lure';    Pattern = '(?i)!\[[^\]]*\]\(https?://[^)\s]+\?(?:token|key|secret|cred|data)=|\b(?:curl|Invoke-WebRequest|wget)\b[^\r\n]*(?:\$env:|api_key|password|token)' }
)

function Remove-KongorAnsiAndInvisibleChars {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Text
    )

    if ([string]::IsNullOrEmpty($Text)) {
        return ""
    }

    # 1. Strip ANSI CSI/OSC escape sequences (\x1b\[[0-9;?]*[a-zA-Z] and \x1b\][^\x07]*\x07)
    $clean = [regex]::Replace($Text, '\x1B\[[0-9;?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)', '')

    # 2. Strip Unicode Tag Block (U+E0000..U+E007F -> surrogate pairs \uDB40[\uDC00-\uDC7F]) and zero-width chars
    $clean = [regex]::Replace($clean, '\uDB40[\uDC00-\uDC7F]|[\u200B-\u200F\u202A-\u202E\u2060-\u206F\uFEFF]', '')

    return $clean
}

function ConvertTo-KongorDefangedTokens {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Text
    )

    if ([string]::IsNullOrEmpty($Text)) {
        return ""
    }

    $defanged = Remove-KongorAnsiAndInvisibleChars -Text $Text

    # Defang LLM special chat/control tokens (#52 special-token-defanging)
    $defanged = [regex]::Replace($defanged, '<\|([a-zA-Z0-9_:-]+)\|>', '<|$1_DEFANGED|>')
    $defanged = [regex]::Replace($defanged, '(?i)\[(/?INST)\]', '[$1_DEFANGED]')
    $defanged = [regex]::Replace($defanged, '(?i)<<(/?SYS)>>', '<<$1_DEFANGED>>')
    $defanged = [regex]::Replace($defanged, '(?i)<\s*(/?\s*UNTRUSTED_TOOL_OUTPUT)\b', '<$1_DEFANGED')

    return $defanged
}

function Test-KongorPromptInjection {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Text
    )

    if ([string]::IsNullOrEmpty($Text)) {
        return [PSCustomObject]@{
            Detected   = $false
            Risk       = "low"
            Categories = @()
        }
    }

    $matchedCategories = New-Object System.Collections.Generic.List[string]

    # Check for invisible Unicode tag block characters before stripping
    if ([regex]::IsMatch($Text, '\uDB40[\uDC00-\uDC7F]|[\u202A-\u202E\u2066-\u2069]')) {
        $matchedCategories.Add("invisible_unicode_tag")
    }

    foreach ($rule in $script:KongorPromptInjectionRules) {
        if ([regex]::IsMatch($Text, $rule.Pattern)) {
            if (-not $matchedCategories.Contains($rule.Category)) {
                $matchedCategories.Add($rule.Category)
            }
        }
    }

    $riskLevel = if ($matchedCategories.Count -ge 2 -or
                     $matchedCategories.Contains("role_override") -or
                     $matchedCategories.Contains("system_tag_spoof") -or
                     $matchedCategories.Contains("destructive_cmd_lure")) {
        "high"
    } elseif ($matchedCategories.Count -eq 1) {
        "medium"
    } else {
        "low"
    }

    return [PSCustomObject]@{
        Detected   = ($matchedCategories.Count -gt 0)
        Risk       = $riskLevel
        Categories = $matchedCategories.ToArray()
    }
}

function Format-KongorBoundedOutput {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Text,

        [Parameter(Mandatory = $false)]
        [int]$MaxBytes = 12288,

        [Parameter(Mandatory = $false)]
        [string]$ToolName = "kongor_log",

        [Parameter(Mandatory = $false)]
        [string]$SpillDir = ""
    )

    if ([string]::IsNullOrEmpty($Text)) {
        return [PSCustomObject]@{
            Text        = ""
            Truncated   = $false
            SpillPath   = ""
            TotalBytes  = 0
        }
    }

    $utf8 = New-Object System.Text.UTF8Encoding($false)
    $bytes = $utf8.GetBytes($Text)
    if ($bytes.Length -le $MaxBytes) {
        return [PSCustomObject]@{
            Text        = $Text
            Truncated   = $false
            SpillPath   = ""
            TotalBytes  = $bytes.Length
        }
    }

    if ([string]::IsNullOrWhiteSpace($SpillDir)) {
        $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
        $SpillDir = Join-Path $repoRoot "logs\spill"
    }
    if (-not (Test-Path -LiteralPath $SpillDir)) {
        New-Item -ItemType Directory -Path $SpillDir -Force | Out-Null
    }

    $sha1 = [System.Security.Cryptography.SHA1]::Create()
    $hashHex = ([System.BitConverter]::ToString($sha1.ComputeHash($bytes))).Replace("-", "").ToLowerInvariant().Substring(0, 12)
    $sha1.Dispose()

    $safeTool = ($ToolName -replace '[^a-zA-Z0-9_-]', '_')
    $spillFile = Join-Path $SpillDir "${safeTool}-${hashHex}.log"
    [System.IO.File]::WriteAllBytes($spillFile, $bytes)

    $lines = $Text -split "`r?`n"
    $halfChars = [Math]::Max(256, [int]($MaxBytes / 2) - 128)
    $headText = if ($Text.Length -gt $halfChars) { $Text.Substring(0, $halfChars) } else { $Text }
    $tailText = if ($Text.Length -gt $halfChars) { $Text.Substring($Text.Length - $halfChars) } else { "" }

    $bounded = @(
        "=== HEAD (bounded to $MaxBytes bytes; total=$($bytes.Length) bytes, lines=$($lines.Count)) ==="
        $headText
        "... [OUTPUT BOUNDED: $($bytes.Length - $MaxBytes) bytes spilled to '$spillFile'] ..."
        "=== TAIL ==="
        $tailText
    ) -join "`n"

    return [PSCustomObject]@{
        Text        = $bounded
        Truncated   = $true
        SpillPath   = $spillFile
        TotalBytes  = $bytes.Length
    }
}

function Format-KongorUntrustedOutput {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Text,

        [Parameter(Mandatory = $false)]
        [string]$Origin = "game_log",

        [Parameter(Mandatory = $false)]
        [int]$MaxBytes = 12288,

        [Parameter(Mandatory = $false)]
        [string]$SpillDir = ""
    )

    $scan = Test-KongorPromptInjection -Text $Text
    $defanged = ConvertTo-KongorDefangedTokens -Text $Text
    $bounded = Format-KongorBoundedOutput -Text $defanged -MaxBytes $MaxBytes -ToolName $Origin -SpillDir $SpillDir

    $catAttr = if ($scan.Categories.Count -gt 0) { $scan.Categories -join "," } else { "none" }
    $spillAttr = if ($bounded.Truncated) { " spill_path=`"$($bounded.SpillPath)`"" } else { "" }

    $envelope = "<UNTRUSTED_TOOL_OUTPUT origin=`"$Origin`" risk=`"$($scan.Risk)`" categories=`"$catAttr`" truncated=`"$($bounded.Truncated.ToString().ToLowerInvariant())`"$spillAttr>`n$($bounded.Text)`n</UNTRUSTED_TOOL_OUTPUT>"

    return [PSCustomObject]@{
        Envelope   = $envelope
        Sanitized  = $bounded.Text
        Detected   = $scan.Detected
        Risk       = $scan.Risk
        Categories = $scan.Categories
        Truncated  = $bounded.Truncated
        SpillPath  = $bounded.SpillPath
    }
}

Export-ModuleMember -Function @(
    'Remove-KongorAnsiAndInvisibleChars',
    'ConvertTo-KongorDefangedTokens',
    'Test-KongorPromptInjection',
    'Format-KongorBoundedOutput',
    'Format-KongorUntrustedOutput'
)
