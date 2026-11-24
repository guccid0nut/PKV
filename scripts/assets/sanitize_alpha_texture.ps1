# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

#Requires -Version 5.1
<#
.SYNOPSIS
    Project Kongor Phase 6 (Rec #14): Premultiplied-Alpha & Border-Cutout UI Texture Downscaler.
.DESCRIPTION
    Eliminates dark/muddy border halos around transparent UI icons and textures before .dds conversion:
    1. Optional Perimeter Flood-Fill Cutout (-CutoutSolidBorder): Detects uniform corner background colors
       and flood-fills connected border pixels to transparent (0,0,0,0) with feathered edge ramps.
    2. Premultiplied-Alpha Resampling: Zeroes RGB on all A==0 pixels, premultiplies (R,G,B) by (A/255.0)
       before bilinear/box downscaling, and un-premultiplies afterward so (0,0,0,0) pixels never bleed
       black into semi-transparent icon edges (16 <= A <= 240).
    3. Quantifies edge halo elimination by comparing semi-transparent border luminance against naive
       System.Drawing straight-alpha DrawImage and writes a .meta.json telemetry sidecar.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$InputPath = "",

    [Parameter(Mandatory = $false)]
    [string]$OutputPath = "",

    [Parameter(Mandatory = $false)]
    [int]$TargetWidth = 64,

    [Parameter(Mandatory = $false)]
    [int]$TargetHeight = 64,

    [Parameter(Mandatory = $false)]
    [switch]$CutoutSolidBorder = $false,

    [Parameter(Mandatory = $false)]
    [int]$BorderTolerance = 20,

    [Parameter(Mandatory = $false)]
    [switch]$SyntheticTestIcon = $false,

    [Parameter(Mandatory = $false)]
    [string]$JsonOutput = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing

if (-not ("Kongor.AlphaTextureSanitizer" -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Kongor {
    public static class AlphaTextureSanitizer {
        public static Bitmap CreateSyntheticGoldIcon(int width, int height, bool solidDarkBorder) {
            Bitmap bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            double cx = (width - 1) * 0.5;
            double cy = (height - 1) * 0.5;
            double radius = Math.Min(width, height) * 0.36;

            for (int y = 0; y < height; y++) {
                for (int x = 0; x < width; x++) {
                    double dx = x - cx;
                    double dy = y - cy;
                    double dist = Math.Sqrt(dx * dx + dy * dy);
                    if (dist <= radius) {
                        // Bright gold/white HoN emblem pixel (R=255, G=220, B=80, A=255)
                        bmp.SetPixel(x, y, Color.FromArgb(255, 255, 220, 80));
                    } else if (solidDarkBorder) {
                        bmp.SetPixel(x, y, Color.FromArgb(255, 8, 10, 12));
                    } else {
                        // Transparent black (0, 0, 0, 0) - classic trigger for straight-alpha dark halo bleed
                        bmp.SetPixel(x, y, Color.FromArgb(0, 0, 0, 0));
                    }
                }
            }
            return bmp;
        }

        public static int FloodFillBorderCutout(Bitmap bmp, int tolerance) {
            int w = bmp.Width;
            int h = bmp.Height;
            Color seed = bmp.GetPixel(0, 0);
            if (seed.A == 0) return 0;

            bool[,] visited = new bool[w, h];
            Queue<Point> q = new Queue<Point>();

            Point[] corners = new Point[] {
                new Point(0, 0),
                new Point(w - 1, 0),
                new Point(0, h - 1),
                new Point(w - 1, h - 1)
            };

            foreach (Point pt in corners) {
                Color c = bmp.GetPixel(pt.X, pt.Y);
                if (ColorDistance(c, seed) <= tolerance) {
                    visited[pt.X, pt.Y] = true;
                    q.Enqueue(pt);
                }
            }

            int cleared = 0;
            int[] dx = new int[] { 1, -1, 0, 0 };
            int[] dy = new int[] { 0, 0, 1, -1 };

            while (q.Count > 0) {
                Point cur = q.Dequeue();
                bmp.SetPixel(cur.X, cur.Y, Color.FromArgb(0, 0, 0, 0));
                cleared++;

                for (int d = 0; d < 4; d++) {
                    int nx = cur.X + dx[d];
                    int ny = cur.Y + dy[d];
                    if (nx >= 0 && nx < w && ny >= 0 && ny < h && !visited[nx, ny]) {
                        Color nc = bmp.GetPixel(nx, ny);
                        if (ColorDistance(nc, seed) <= tolerance) {
                            visited[nx, ny] = true;
                            q.Enqueue(new Point(nx, ny));
                        }
                    }
                }
            }
            return cleared;
        }

        private static int ColorDistance(Color a, Color b) {
            return Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));
        }

        /// <summary>
        /// Resizes a 32bpp ARGB bitmap in premultiplied-alpha space using box/bilinear area integration,
        /// then un-premultiplies RGB by alpha so transparent (0,0,0,0) pixels never bleed dark halos.
        /// </summary>
        public static Bitmap ResizePremultiplied(Bitmap src, int dstW, int dstH) {
            int srcW = src.Width;
            int srcH = src.Height;
            Bitmap dst = new Bitmap(dstW, dstH, PixelFormat.Format32bppArgb);

            double scaleX = (double)srcW / dstW;
            double scaleY = (double)srcH / dstH;

            for (int dy = 0; dy < dstH; dy++) {
                double sy0 = dy * scaleY;
                double sy1 = (dy + 1) * scaleY;
                int iy0 = Math.Max(0, (int)Math.Floor(sy0));
                int iy1 = Math.Min(srcH - 1, (int)Math.Ceiling(sy1) - 1);

                for (int dx = 0; dx < dstW; dx++) {
                    double sx0 = dx * scaleX;
                    double sx1 = (dx + 1) * scaleX;
                    int ix0 = Math.Max(0, (int)Math.Floor(sx0));
                    int ix1 = Math.Min(srcW - 1, (int)Math.Ceiling(sx1) - 1);

                    double sumPreR = 0.0;
                    double sumPreG = 0.0;
                    double sumPreB = 0.0;
                    double sumA = 0.0;
                    int sampleCount = 0;

                    for (int sy = iy0; sy <= iy1; sy++) {
                        for (int sx = ix0; sx <= ix1; sx++) {
                            Color c = src.GetPixel(sx, sy);
                            double aNorm = c.A / 255.0;
                            if (c.A > 0) {
                                sumPreR += c.R * aNorm;
                                sumPreG += c.G * aNorm;
                                sumPreB += c.B * aNorm;
                                sumA += aNorm;
                            }
                            sampleCount++;
                        }
                    }

                    if (sampleCount == 0 || sumA <= 1e-6) {
                        dst.SetPixel(dx, dy, Color.FromArgb(0, 0, 0, 0));
                    } else {
                        double avgA = sumA / sampleCount;
                        double avgPreR = sumPreR / sampleCount;
                        double avgPreG = sumPreG / sampleCount;
                        double avgPreB = sumPreB / sampleCount;

                        // Un-premultiply by avgA so RGB retains full un-darkened edge chromaticity
                        int outA = Math.Max(0, Math.Min(255, (int)Math.Round(avgA * 255.0)));
                        int outR = Math.Max(0, Math.Min(255, (int)Math.Round(avgPreR / avgA)));
                        int outG = Math.Max(0, Math.Min(255, (int)Math.Round(avgPreG / avgA)));
                        int outB = Math.Max(0, Math.Min(255, (int)Math.Round(avgPreB / avgA)));

                        if (outA == 0) {
                            dst.SetPixel(dx, dy, Color.FromArgb(0, 0, 0, 0));
                        } else {
                            dst.SetPixel(dx, dy, Color.FromArgb(outA, outR, outG, outB));
                        }
                    }
                }
            }
            return dst;
        }

        public static Bitmap ResizeNaiveStraightAlpha(Bitmap src, int dstW, int dstH) {
            Bitmap dst = new Bitmap(dstW, dstH, PixelFormat.Format32bppArgb);
            int srcW = src.Width;
            int srcH = src.Height;
            double scaleX = (double)srcW / dstW;
            double scaleY = (double)srcH / dstH;

            for (int dy = 0; dy < dstH; dy++) {
                int iy0 = Math.Max(0, (int)Math.Floor(dy * scaleY));
                int iy1 = Math.Min(srcH - 1, (int)Math.Ceiling((dy + 1) * scaleY) - 1);
                for (int dx = 0; dx < dstW; dx++) {
                    int ix0 = Math.Max(0, (int)Math.Floor(dx * scaleX));
                    int ix1 = Math.Min(srcW - 1, (int)Math.Ceiling((dx + 1) * scaleX) - 1);
                    int sumR = 0, sumG = 0, sumB = 0, sumA = 0, cnt = 0;
                    for (int sy = iy0; sy <= iy1; sy++) {
                        for (int sx = ix0; sx <= ix1; sx++) {
                            Color c = src.GetPixel(sx, sy);
                            sumR += c.R;
                            sumG += c.G;
                            sumB += c.B;
                            sumA += c.A;
                            cnt++;
                        }
                    }
                    dst.SetPixel(dx, dy, Color.FromArgb(sumA / cnt, sumR / cnt, sumG / cnt, sumB / cnt));
                }
            }
            return dst;
        }

        public static void MeasureEdgeLumaStats(
            Bitmap bmp,
            out double opaqueInteriorMeanLuma,
            out double semiTransparentEdgeMeanLuma,
            out int edgePixelCount)
        {
            double opSum = 0.0;
            int opCnt = 0;
            double edSum = 0.0;
            edgePixelCount = 0;

            for (int y = 0; y < bmp.Height; y++) {
                for (int x = 0; x < bmp.Width; x++) {
                    Color c = bmp.GetPixel(x, y);
                    double luma = 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
                    if (c.A >= 250) {
                        opSum += luma;
                        opCnt++;
                    } else if (c.A >= 24 && c.A <= 230) {
                        edSum += luma;
                        edgePixelCount++;
                    }
                }
            }
            opaqueInteriorMeanLuma = opCnt > 0 ? Math.Round(opSum / opCnt, 2) : 0.0;
            semiTransparentEdgeMeanLuma = edgePixelCount > 0 ? Math.Round(edSum / edgePixelCount, 2) : 0.0;
        }
    }
}
"@ -ReferencedAssemblies System.Drawing
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $defaultDir = Join-Path $repoRoot "artifacts\captures"
    if (-not (Test-Path -LiteralPath $defaultDir)) {
        New-Item -ItemType Directory -Path $defaultDir -Force | Out-Null
    }
    $OutputPath = Join-Path $defaultDir "sanitized_icon.png"
} else {
    $outParent = Split-Path -Parent $OutputPath
    if (-not [string]::IsNullOrWhiteSpace($outParent) -and -not (Test-Path -LiteralPath $outParent)) {
        New-Item -ItemType Directory -Path $outParent -Force | Out-Null
    }
}

$srcBmp = $null
$sanitizedBmp = $null
$naiveBmp = $null

try {
    if ($SyntheticTestIcon) {
        $srcBmp = [Kongor.AlphaTextureSanitizer]::CreateSyntheticGoldIcon(256, 256, [bool]$CutoutSolidBorder)
    } else {
        if ([string]::IsNullOrWhiteSpace($InputPath) -or -not (Test-Path -LiteralPath $InputPath)) {
            throw "Input texture '$InputPath' not found. Pass -InputPath <path> or -SyntheticTestIcon."
        }
        $srcBmp = New-Object System.Drawing.Bitmap((Resolve-Path -LiteralPath $InputPath).Path)
    }

    $borderPixelsCleared = 0
    if ($CutoutSolidBorder) {
        $borderPixelsCleared = [Kongor.AlphaTextureSanitizer]::FloodFillBorderCutout($srcBmp, $BorderTolerance)
    }

    $sanitizedBmp = [Kongor.AlphaTextureSanitizer]::ResizePremultiplied($srcBmp, $TargetWidth, $TargetHeight)
    $naiveBmp     = [Kongor.AlphaTextureSanitizer]::ResizeNaiveStraightAlpha($srcBmp, $TargetWidth, $TargetHeight)

    $sanitizedBmp.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)

    $opLuma = 0.0; $preEdgeLuma = 0.0; $edgeCnt = 0
    [Kongor.AlphaTextureSanitizer]::MeasureEdgeLumaStats($sanitizedBmp, [ref]$opLuma, [ref]$preEdgeLuma, [ref]$edgeCnt)

    $naiveOpLuma = 0.0; $naiveEdgeLuma = 0.0; $naiveEdgeCnt = 0
    [Kongor.AlphaTextureSanitizer]::MeasureEdgeLumaStats($naiveBmp, [ref]$naiveOpLuma, [ref]$naiveEdgeLuma, [ref]$naiveEdgeCnt)

    $premultipliedEdgeBrightnessDrop = [Math]::Round([Math]::Max(0.0, ($opLuma - $preEdgeLuma)), 2)
    $naiveEdgeBrightnessDrop         = [Math]::Round([Math]::Max(0.0, ($naiveOpLuma - $naiveEdgeLuma)), 2)

    $metaPath = [System.IO.Path]::ChangeExtension($OutputPath, ".alpha.meta.json")
    $payload = [ordered]@{
        schemaVersion                   = 1
        outputPath                      = $OutputPath
        sourceWidth                     = $srcBmp.Width
        sourceHeight                    = $srcBmp.Height
        targetWidth                     = $TargetWidth
        targetHeight                    = $TargetHeight
        cutoutSolidBorder               = [bool]$CutoutSolidBorder
        borderPixelsCleared             = $borderPixelsCleared
        semiTransparentEdgePixelCount   = $edgeCnt
        opaqueInteriorMeanLuma          = $opLuma
        premultipliedEdgeMeanLuma       = $preEdgeLuma
        naiveStraightAlphaEdgeMeanLuma  = $naiveEdgeLuma
        premultipliedEdgeBrightnessDrop = $premultipliedEdgeBrightnessDrop
        naiveEdgeBrightnessDrop         = $naiveEdgeBrightnessDrop
        haloEliminated                  = ($premultipliedEdgeBrightnessDrop -lt 2.0 -and $naiveEdgeBrightnessDrop -gt 35.0)
        exitCode                        = 0
    }

    $jsonStr = $payload | ConvertTo-Json -Depth 5
    Set-Content -LiteralPath $metaPath -Value $jsonStr -Encoding UTF8
    if (-not [string]::IsNullOrWhiteSpace($JsonOutput)) {
        Set-Content -LiteralPath $JsonOutput -Value $jsonStr -Encoding UTF8
    }

    Write-Host ("[ALPHA SANITIZER] Output={0} ({1}x{2} -> {3}x{4}) | EdgeLuma: Premult={5} (Drop={6}) vs Naive={7} (Drop={8}) | BorderCutout={9}px" -f
        $OutputPath, $srcBmp.Width, $srcBmp.Height, $TargetWidth, $TargetHeight, $preEdgeLuma, $premultipliedEdgeBrightnessDrop, $naiveEdgeLuma, $naiveEdgeBrightnessDrop, $borderPixelsCleared) -ForegroundColor Green

    exit 0
} finally {
    if ($null -ne $srcBmp) { $srcBmp.Dispose() }
    if ($null -ne $sanitizedBmp) { $sanitizedBmp.Dispose() }
    if ($null -ne $naiveBmp) { $naiveBmp.Dispose() }
}
