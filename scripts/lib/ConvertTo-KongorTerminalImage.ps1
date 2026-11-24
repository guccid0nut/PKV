# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Renders a bitmap/PNG image to a 24-bit ANSI TrueColor half-block terminal string (#85 f3d-ansi-terminal-image).

.DESCRIPTION
    Ported from F3D F3DConsoleOutputTools::PrintImageToConsole:
    - Packs two vertical pixels per character cell using Unicode half-blocks (\u2580 upper half,
      \u2584 lower half, \u2588 full block).
    - Deduplicates consecutive 24-bit ANSI escape sequences (\x1b[38;2;R;G;Bm and \x1b[48;2;R;G;Bm)
      so repeated colors across adjacent columns emit zero redundant escape bytes.
    - Supports alpha-thresholding against a checkered or transparent terminal background.
#>

Add-Type -AssemblyName System.Drawing

function ConvertTo-KongorTerminalImage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ImagePath,

        [Parameter(Mandatory = $false)]
        [ValidateRange(4, 240)]
        [int]$MaxWidth = 64
    )

    if (-not (Test-Path -LiteralPath $ImagePath)) {
        throw "Image file not found at '$ImagePath'. Verify the screenshot or texture preview path."
    }

    $esc = [char]27
    $upperHalf = [char]0x2580
    $lowerHalf = [char]0x2584
    $fullBlock = [char]0x2588

    $srcBmp = New-Object System.Drawing.Bitmap($ImagePath)
    try {
        $targetWidth = [Math]::Min($MaxWidth, [Math]::Max(1, $srcBmp.Width))
        $scale = [double]$targetWidth / [double]$srcBmp.Width
        $targetHeight = [Math]::Max(2, [int][Math]::Round($srcBmp.Height * $scale))
        if (($targetHeight % 2) -ne 0) {
            $targetHeight++
        }

        $resized = New-Object System.Drawing.Bitmap($srcBmp, $targetWidth, $targetHeight)
        try {
            $sb = New-Object System.Text.StringBuilder
            for ($y = 0; $y -lt $targetHeight; $y += 2) {
                $lastFg = ""
                $lastBg = ""

                for ($x = 0; $x -lt $targetWidth; $x++) {
                    $topPx = $resized.GetPixel($x, $y)
                    $botPx = if (($y + 1) -lt $targetHeight) { $resized.GetPixel($x, $y + 1) } else { [System.Drawing.Color]::FromArgb(0, 0, 0, 0) }

                    $topVisible = $topPx.A -ge 64
                    $botVisible = $botPx.A -ge 64

                    if (-not $topVisible -and -not $botVisible) {
                        if ($lastBg -ne "RESET") {
                            [void]$sb.Append("$esc[0m")
                            $lastFg = "RESET"
                            $lastBg = "RESET"
                        }
                        [void]$sb.Append(' ')
                    } elseif ($topVisible -and -not $botVisible) {
                        $fgCode = "$($topPx.R);$($topPx.G);$($topPx.B)"
                        if ($lastBg -ne "RESET") {
                            [void]$sb.Append("$esc[49m")
                            $lastBg = "RESET"
                        }
                        if ($lastFg -ne $fgCode) {
                            [void]$sb.Append("$esc[38;2;${fgCode}m")
                            $lastFg = $fgCode
                        }
                        [void]$sb.Append($upperHalf)
                    } elseif (-not $topVisible -and $botVisible) {
                        $fgCode = "$($botPx.R);$($botPx.G);$($botPx.B)"
                        if ($lastBg -ne "RESET") {
                            [void]$sb.Append("$esc[49m")
                            $lastBg = "RESET"
                        }
                        if ($lastFg -ne $fgCode) {
                            [void]$sb.Append("$esc[38;2;${fgCode}m")
                            $lastFg = $fgCode
                        }
                        [void]$sb.Append($lowerHalf)
                    } else {
                        $topCode = "$($topPx.R);$($topPx.G);$($topPx.B)"
                        $botCode = "$($botPx.R);$($botPx.G);$($botPx.B)"
                        if ($topCode -eq $botCode) {
                            if ($lastFg -ne $topCode) {
                                [void]$sb.Append("$esc[38;2;${topCode}m")
                                $lastFg = $topCode
                            }
                            [void]$sb.Append($fullBlock)
                        } else {
                            if ($lastFg -ne $topCode) {
                                [void]$sb.Append("$esc[38;2;${topCode}m")
                                $lastFg = $topCode
                            }
                            if ($lastBg -ne $botCode) {
                                [void]$sb.Append("$esc[48;2;${botCode}m")
                                $lastBg = $botCode
                            }
                            [void]$sb.Append($upperHalf)
                        }
                    }
                }
                [void]$sb.Append("$esc[0m`n")
            }
            return $sb.ToString()
        } finally {
            $resized.Dispose()
        }
    } finally {
        $srcBmp.Dispose()
    }
}
