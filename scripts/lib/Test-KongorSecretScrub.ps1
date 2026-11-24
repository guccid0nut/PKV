# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Shared secret-scrubbing, session-stable redaction, SSRF guard, and command firewall for Project Kongor.

.DESCRIPTION
    Implements Phase 1 security primitives (#37 secret-scrub-14-patterns, #63 longest-match-redaction-sort,
    #112 ssrf-and-command-firewall):
    - Test-KongorSecretScrub: Scans text or files against 14 high-signal secret patterns + legacy assignment rules.
    - ConvertTo-KongorRedactedText: Session-stable <TYPE_n> redaction sorted longest-match-first (Length -Descending)
      with < 3-char skip guard and trailing FQDN dot normalization.
    - Test-KongorSafeUrl: Blocks SSRF targets (loopback, RFC1918 private, link-local, 169.254.169.254 metadata).
    - Test-KongorDangerousCommand: Blocks destructive git/filesystem/volume shell commands.
#>

if (-not ("KongorScrubFastPrefilter" -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

public static class KongorScrubFastPrefilter
{
    public static string[] FilterSecretCandidates(string[] files, string[] patterns, string[][] hintsPerPattern)
    {
        if (files == null || files.Length == 0) return Array.Empty<string>();
        var compiled = new Regex[patterns.Length];
        for (int i = 0; i < patterns.Length; i++)
        {
            compiled[i] = new Regex(patterns[i], RegexOptions.Compiled | RegexOptions.CultureInvariant);
        }

        var hits = new ConcurrentBag<string>();
        Parallel.ForEach(files, cf =>
        {
            if (string.IsNullOrWhiteSpace(cf) || !File.Exists(cf)) return;
            string text;
            try
            {
                text = File.ReadAllText(cf);
            }
            catch (Exception)
            {
                hits.Add(cf);
                return;
            }
            if (string.IsNullOrEmpty(text)) return;
            for (int i = 0; i < compiled.Length; i++)
            {
                if (hintsPerPattern != null && i < hintsPerPattern.Length && hintsPerPattern[i] != null && hintsPerPattern[i].Length > 0)
                {
                    bool hintOk = false;
                    string[] hArr = hintsPerPattern[i];
                    for (int h = 0; h < hArr.Length; h++)
                    {
                        if (text.IndexOf(hArr[h], StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            hintOk = true;
                            break;
                        }
                    }
                    if (!hintOk) continue;
                }
                if (compiled[i].IsMatch(text))
                {
                    hits.Add(cf);
                    return;
                }
            }
        });
        return hits.ToArray();
    }

    public static string[] FilterPiiCandidates(string[] files, string[] piiTerms, string winPattern, string posixPattern, string pycharmPattern)
    {
        if (files == null || files.Length == 0) return Array.Empty<string>();
        var winRx = new Regex(winPattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
        var posixRx = new Regex(posixPattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
        var pycharmRx = new Regex(pycharmPattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
        var terms = piiTerms ?? Array.Empty<string>();

        var hits = new ConcurrentBag<string>();
        Parallel.ForEach(files, cf =>
        {
            if (string.IsNullOrWhiteSpace(cf) || !File.Exists(cf)) return;
            string text;
            try
            {
                text = File.ReadAllText(cf);
            }
            catch (Exception)
            {
                hits.Add(cf);
                return;
            }
            if (string.IsNullOrEmpty(text)) return;
            for (int i = 0; i < terms.Length; i++)
            {
                if (text.IndexOf(terms[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    hits.Add(cf);
                    return;
                }
            }
            if (text.IndexOf("users", StringComparison.OrdinalIgnoreCase) >= 0 && winRx.IsMatch(text))
            {
                hits.Add(cf);
                return;
            }
            if ((text.IndexOf("/home/", StringComparison.Ordinal) >= 0 || text.IndexOf("/Users/", StringComparison.Ordinal) >= 0) && posixRx.IsMatch(text))
            {
                hits.Add(cf);
                return;
            }
            if (text.IndexOf("pycharmprojects", StringComparison.OrdinalIgnoreCase) >= 0 && pycharmRx.IsMatch(text))
            {
                hits.Add(cf);
                return;
            }
        });
        return hits.ToArray();
    }
}
"@ -Language CSharp
}

$script:KongorSecretPatternDefinitions = @(
    @{ Name = 'github_pat';            Category = 'TOKEN'; Hints = @('github_pat_'); Pattern = '\bgithub_pat_[A-Za-z0-9_]{22,}\b' },
    @{ Name = 'github_classic';        Category = 'TOKEN'; Hints = @('ghp_', 'gho_', 'ghu_', 'ghs_', 'ghr_'); Pattern = '\b(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{36,}\b' },
    @{ Name = 'aws_access_key';        Category = 'CRED';  Hints = @('AKIA', 'ASIA'); Pattern = '\b(?:AKIA|ASIA)[0-9A-Z]{16}\b' },
    @{ Name = 'jwt';                   Category = 'JWT';   Hints = @('eyJ'); Pattern = '\beyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b' },
    @{ Name = 'private_key_pem';       Category = 'KEY';   Hints = @('PRIVATE KEY-----'); Pattern = '-----BEGIN (?:RSA |EC |OPENSSH |DSA |ENCRYPTED )?PRIVATE KEY-----' },
    @{ Name = 'slack_token';           Category = 'TOKEN'; Hints = @('xox'); Pattern = '\bxox[baprs]-[0-9A-Za-z-]{10,}\b' },
    @{ Name = 'discord_webhook';       Category = 'URL';   Hints = @('discord'); Pattern = 'https://(?:ptb\.|canary\.)?discord(?:app)?\.com/api/webhooks/[0-9]{15,}/[A-Za-z0-9_-]{20,}' },
    @{ Name = 'openai_api_key';        Category = 'TOKEN'; Hints = @('sk-'); Pattern = '\bsk-(?:proj-|svcacct-)?[A-Za-z0-9_-]{20,}\b' },
    @{ Name = 'stripe_api_key';        Category = 'TOKEN'; Hints = @('_live_', '_test_'); Pattern = '\b(?:sk|rk)_(?:live|test)_[0-9a-zA-Z]{24,}\b' },
    @{ Name = 'google_api_key';        Category = 'TOKEN'; Hints = @('AIza'); Pattern = '\bAIza[0-9A-Za-z_-]{35}\b' },
    @{ Name = 'gitlab_pat';            Category = 'TOKEN'; Hints = @('glpat-'); Pattern = '\bglpat-[0-9a-zA-Z_-]{20,}\b' },
    @{ Name = 'sendgrid_api_key';      Category = 'TOKEN'; Hints = @('SG.'); Pattern = '\bSG\.[A-Za-z0-9_-]{16,}\.[A-Za-z0-9_-]{16,}\b' },
    @{ Name = 'kongor_session_cookie'; Category = 'CRED';  Hints = @('kongor_session', 'hon_session', 'auth_cookie'); Pattern = '(?i)\b(?:kongor_session|hon_session|auth_cookie)\s*=\s*["'']?[A-Za-z0-9_\-\.]{16,}["'']?' },
    @{ Name = 'cli_password_flag';     Category = 'CRED';  Hints = @('--password', '--passwd', '-passwd'); Pattern = '(?i)(?:^|\s)(?:--password|--passwd|-passwd)\s+(?![0-9]+(?:\s|$))(?!<[A-Za-z0-9_-]+>)["'']?[^\s"'']{4,}["'']?' },
    @{ Name = 'password_assignment';     Category = 'CRED';  Hints = @('password', 'passwd', 'pwd'); Pattern = '(?i)(password|passwd|pwd)\s*=\s*["''](?!\$\{?[A-Za-z0-9_]+\}?["''])[^"''\r\n]{4,}["'']' },
    @{ Name = 'api_key_assignment';      Category = 'CRED';  Hints = @('key', 'token'); Pattern = '(?i)(api[_-]?key|secret[_-]?key|private[_-]?key|access[_-]?token|auth[_-]?token)\s*=\s*["''](?!\$\{?[A-Za-z0-9_]+\}?["''])[^"''\r\n]{6,}["'']' },
    @{ Name = 'bearer_token';            Category = 'TOKEN'; Hints = @('bearer'); Pattern = '(?i)bearer\s+[a-zA-Z0-9_\-\.]{20,}' },
    @{ Name = 'uri_connection_password'; Category = 'CRED';  Hints = @('://'); Pattern = '(?i)\b(?:postgres(?:ql)?|mysql|mongodb(?:\+srv)?|rediss?|amqp|sqlserver)://(?!(?:postgres\.(?:xyzref|xyzcompany|example|\[[A-Za-z0-9_-]+\]):|postgres\.myuser:secret123@|postgres:postgres@(?:localhost|127\.0\.0\.1|postgres)\b))[^:/\s"''@]+:(?!(?:password|password123|OriginalDbPass123!|pass|secret|test|testpass|changeme|\[[A-Za-z0-9_-]+\]|<password>|\$\{[A-Za-z0-9_]+\}|\.{3}|\*{3,})@)[^@\s"'']{4,}@(?!(?:db\.xyzcompany\.supabase\.co|example\.com|example\.local|localhost|127\.0\.0\.1)\b)' },
    @{ Name = 'compel_control_token';    Category = 'TOKEN'; Hints = @('compel_'); Pattern = '\bcompel_(?!(?:test|dummy|example|placeholder|fake|canary|supervisor_|proxy_|worker_)[A-Za-z0-9_-]*\b)[A-Za-z0-9_-]{24,}\b' },
    @{ Name = 'supabase_access_token';   Category = 'TOKEN'; Hints = @('sbp_'); Pattern = '\bsbp_[0-9a-fA-F]{40}\b' },
    @{ Name = 'cloudflare_api_token';    Category = 'TOKEN'; Hints = @('cfat_'); Pattern = '\bcfat_[A-Za-z0-9_-]{20,}\b' },
    @{ Name = 'supabase_secret_key';     Category = 'TOKEN'; Hints = @('sb_secret_'); Pattern = '\bsb_secret_[A-Za-z0-9_-]{16,}\b' },
    @{ Name = 'azure_devops_url';        Category = 'URL';   Hints = @('dev.azure.com'); Pattern = '(?i)https?://dev\.azure\.com/(?!exampleorg\b)[A-Za-z0-9_.-]+' },
    @{ Name = 'generic_secret_assignment'; Category = 'CRED'; Hints = @('secret', 'token', 'key', 'password', 'passwd', 'webhook'); Pattern = '(?i)(?:SECRET|TOKEN|PASSWORD|PASSWD|WEBHOOK|(?:API|AUTH|ACCESS|PRIVATE|SIGNING|HMAC|ENCRYPTION|MASTER|ADMIN|SERVICE|SESSION|BEARER|JWT|OAUTH|DISCORD|GITHUB|SUPABASE|CLOUDFLARE|OPENAI|STRIPE|SENDGRID|AZURE|AWS|GCP|SMTP|DB|DATABASE)_?KEY)\b\s*(?:=|:|\|\|)\s*(?<q>["''])(?!\$\{?[A-Za-z0-9_.:-]+\}?\k<q>)(?!\{[A-Za-z0-9_.-]+\}\k<q>)(?!<[A-Za-z0-9_.-]+>\k<q>)(?!https?://(?:localhost|127\.0\.0\.1|example\.com))[^\s"''$\r\n\{\}][^"''$\r\n\{\}]{5,120}\k<q>' }
)

foreach ($__def in $script:KongorSecretPatternDefinitions) {
    $__def['CompiledRegex'] = [regex]::new($__def.Pattern, [System.Text.RegularExpressions.RegexOptions]::Compiled)
}
$script:KongorSecretPatterns = @($script:KongorSecretPatternDefinitions | ForEach-Object { $_.Pattern })
$script:KongorSecretHintsMatrix = [string[][]]@($script:KongorSecretPatternDefinitions | ForEach-Object { ,@([string[]]$_.Hints) })

$script:KongorWhitelistedSecretValues = @(
    '""',
    "''",
    '"{token}"',
    '"password"',
    '"changeme"',
    '"none"',
    '"null"',
    '"true"',
    '"false"',
    '"example"',
    '"placeholder"',
    '"your_api_key"',
    '"your_kongor_password"',
    '"your_compel_auth_token"',
    'bearer your_compel_auth_token',
    '"my_pass"',
    '"my_secure_password"',
    '"kongor"',
    '"supabase:s3secretkey"',
    '"match_auth_"',
    '"fake_key"',
    '"sk-test"',
    'bearer secret_jwt_token_123456',
    '"merrickdatabasepassword123!"',
    '"distributedcachepassword123!"',
    '"customdatabasepassword123!"',
    '"customcachepassword123!"',
    '"originaldbpass123!"',
    '"secretpassword"',
    '"secretpassword123"',
    '"secretpassword123!"',
    '"realproductionpassword456!"',
    '"testpassword123!"',
    '"testpgpass123"',
    '"minikubepassword"',
    '"k8spassword"',
    '"pass123"',
    '"s3secret123"',
    '"secret123"',
    '"secret"',
    '"weak"',
    '"sg.test_api_key"',
    '"test-api-key"',
    '"super-secret-auth-key"',
    '"tok_initial_token_1234"',
    '"match_auth_token_xyz"',
    '"civ$c3sgukxr!oum$tail"',
    '"ultrasecurepassword456$#@"',
    '"password123"',
    '"password123!"',
    '"cache123"',
    '"pyropassword123"',
    '"validpassword"',
    '"mysecretpassword123"',
    '"wrongpassword"',
    '"p@ss:w#rd%1/2"',
    '"correctrotatedpassword!"',
    '"mock_key"',
    '"custom_secret_test_pw"',
    '"custom_login_action_pw"',
    '"devtoken"',
    '"compel_token"',
    '"devpassword123"',
    '"display1"',
    '"supabase:s3accesskey"',
    '"used_token"',
    '"sb_access_key_9921"',
    '"nexus-default-development-jwt-signingkey-phrase-32byteslong!"',
    '"nexus-internal-service-key-dev-2026"',
    '"k0ng0r-wave4b-ipc-secret-99"',
    '"test-purge-secret-123"',
    '"authtoken123"',
    '"compel-token-xyz-789"',
    '"real-auth-token-abc-123"',
    '"minikube-token"',
    '"k8s-secret-token"',
    '"supersecrettoken999"',
    '"jwtkey123"',
    '"newsecret456!"',
    '"supersecretdiscordkey123!"',
    '"custom-jwt-signingkey-phrase-32byteslong!"',
    '"s3access123"',
    '"access123"',
    '"changed"',
    '"ak-test"',
    '"compel-test-token"',
    '"ipc-test"',
    '"to-be-removed"',
    '"ak-env"',
    '"vault-value"',
    '"the_hidden_secret"',
    '"test-replay-storage-internal-key-2026"',
    '"supersecretdevsigningkey12345678901234567890"',
    '"supersecretplayersessiontoken1234567890"',
    '"a_very_secret_key_for_testing_purposes_at_least_32_bytes_long!"',
    '"dummy-key-for-testing"',
    '"test-token-123"',
    '"challenger_secret_key_super_secure_at_least_32_bytes_long_2026!"',
    '"alpha-token-01"',
    '"test-mgmt-token-bravo"',
    '"wave4a-hmac-test-secret-key-0123456789"',
    '"per-launch-secret-token"',
    '"abcdef1234567890xyz"',
    '"1234567890"',
    '"lease_token_123"',
    '"lease_token_777"',
    '"secretkey123456789012345678901234567890"',
    '"securepassword123!"',
    '"supersecretkeyfortestingjwttokengeneration1234567890!"',
    '"synthetic-auth-token-"',
    '"custom_password"',
    '"kongor"',
    '"compel_"',
    '"sg.development_placeholder_key_not_set"',
    '"development-discord-client-secret"',
    '"development-twitch-client-secret"',
    '"ipc-env"',
    '"customcompeltoken_keepme_2026!"',
    '"sb_secret_"',
    '"my_compel_control_plane_token"',
    '"e2e_ctl_"',
    '"e2e_ipc_"'
)

$script:KongorRedactionSessionState = @{
    Map      = @{}
    Counters = @{}
}

function Reset-KongorRedactionSession {
    [CmdletBinding()]
    param()
    $script:KongorRedactionSessionState.Map.Clear()
    $script:KongorRedactionSessionState.Counters.Clear()
}

# satisfies: #241 (repowise-intermediate-commit-secret-scanner — masked snippet fingerprint)
function Get-KongorMaskedSecretSnippet {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Secret
    )
    $trimmed = if ($null -ne $Secret) { $Secret.Trim() } else { "" }
    if ([string]::IsNullOrEmpty($trimmed)) { return "****" }
    if ($trimmed.Length -ge 10) {
        return $trimmed.Substring(0, 4) + "****" + $trimmed.Substring($trimmed.Length - 4, 4)
    }
    if ($trimmed.Length -ge 4) {
        return $trimmed.Substring(0, 2) + "****" + $trimmed.Substring($trimmed.Length - 1, 1)
    }
    return "****"
}

function Test-KongorSecretScrub {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $false)]
        [string]$FilePath = "",

        [Parameter(Mandatory = $false)]
        [string]$Content = ""
    )

    if (-not [string]::IsNullOrWhiteSpace($FilePath) -and [string]::IsNullOrEmpty($Content)) {
        if ([System.IO.File]::Exists($FilePath)) {
            $Content = [System.IO.File]::ReadAllText($FilePath)
        }
    }

    $findings = New-Object System.Collections.Generic.List[object]
    $suppressedCount = 0
    if ([string]::IsNullOrEmpty($Content)) {
        return [PSCustomObject]@{
            Passed          = $true
            FilePath        = $FilePath
            SuppressedCount = 0
            Findings        = @()
        }
    }

    foreach ($def in $script:KongorSecretPatternDefinitions) {
        if ($null -ne $def.Hints -and $def.Hints.Count -gt 0) {
            $hintHit = $false
            foreach ($h in $def.Hints) {
                if ($Content.IndexOf($h, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    $hintHit = $true
                    break
                }
            }
            if (-not $hintHit) { continue }
        }
        $pat = $def.Pattern
        $regexMatches = if ($null -ne $def.CompiledRegex) { $def.CompiledRegex.Matches($Content) } else { [regex]::Matches($Content, $pat) }
        foreach ($m in $regexMatches) {
            # Check for inline kongor-security-ignore suppression on the matching line (#241)
            $lineStart = $Content.LastIndexOf("`n", [Math]::Max(0, $m.Index - 1))
            if ($lineStart -lt 0) { $lineStart = 0 } else { $lineStart++ }
            $lineEnd = $Content.IndexOf("`n", $m.Index)
            if ($lineEnd -lt 0) { $lineEnd = $Content.Length }
            $lineSlice = $Content.Substring($lineStart, $lineEnd - $lineStart)
            if ($lineSlice -match 'kongor-security-ignore') {
                $suppressedCount++
                continue
            }

            $val = $m.Value.ToLowerInvariant()
            $isWhitelisted = $false
            foreach ($w in $script:KongorWhitelistedSecretValues) {
                if ($val -like "*$w*") {
                    $isWhitelisted = $true
                    break
                }
            }
            if (-not $isWhitelisted) {
                $rawMatch = $m.Value.Trim()
                $findings.Add([PSCustomObject]@{
                    RuleName      = $def.Name
                    Category      = $def.Category
                    Pattern       = $pat
                    Match         = $rawMatch
                    MaskedSnippet = (Get-KongorMaskedSecretSnippet -Secret $rawMatch)
                    Index         = $m.Index
                })
            }
        }
    }

    return [PSCustomObject]@{
        Passed          = ($findings.Count -eq 0)
        FilePath        = $FilePath
        SuppressedCount = $suppressedCount
        Findings        = $findings.ToArray()
    }
}

# satisfies: #241 (repowise-intermediate-commit-secret-scanner — Pass 1 HEAD + Pass 2 intermediate commits)
function Test-KongorSecretLeakage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $false)]
        [string]$RepoRoot = "",

        [Parameter(Mandatory = $false)]
        [string[]]$CandidateFiles = @(),

        [Parameter(Mandatory = $false)]
        [string]$BaseRef = "",

        [Parameter(Mandatory = $false)]
        [string]$HeadRef = "HEAD"
    )

    $allLeaks = New-Object System.Collections.Generic.List[object]
    $seenKeys = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    $headSecretMap = @{}

    # Pass 1: Scan current working tree / HEAD candidate files
    $pass1Files = @(
        foreach ($cf in $CandidateFiles) {
            if ([string]::IsNullOrWhiteSpace($cf)) { continue }
            if ($cf -match '(?i)\.(?:s2z|zip|dll|exe|so(?:\.\d+)*|dylib|pdb|png|jpg|jpeg|gif|webp|dds|tga|ogg|wav|mp3|mp4|webm|honreplay|pcapng|model|clip)$') { continue }
            $cf
        }
    )
    if ($pass1Files.Count -gt 20) {
        $pass1Files = @([KongorScrubFastPrefilter]::FilterSecretCandidates($pass1Files, $script:KongorSecretPatterns, $script:KongorSecretHintsMatrix))
    }
    foreach ($cf in $pass1Files) {
        if ([string]::IsNullOrWhiteSpace($cf) -or (-not [System.IO.File]::Exists($cf))) { continue }
        $relFile = if (-not [string]::IsNullOrWhiteSpace($RepoRoot) -and $cf.StartsWith($RepoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            $cf.Substring($RepoRoot.Length).TrimStart('\', '/') -replace '\\', '/'
        } else {
            $cf -replace '\\', '/'
        }
        if ($relFile -ieq 'scripts/test_phase1_security_firewall.ps1' -or $relFile -ieq 'scripts/test_gate_mutants.ps1' -or $relFile -ieq 'tests/unit/tools/Kongor.Mcp.Tests/RepoWiseAndAutoResearchWave4Tests.cs') { continue }
        $scrub = Test-KongorSecretScrub -FilePath $cf
        if (-not $scrub.Passed) {
            foreach ($f in $scrub.Findings) {
                $headSecretMap["$relFile|$($f.MaskedSnippet)"] = $true
                $dedupKey = "HEAD|$relFile|$($f.RuleName)|$($f.MaskedSnippet)"
                if ($seenKeys.Add($dedupKey)) {
                    $allLeaks.Add([PSCustomObject]@{
                        commit          = "HEAD"
                        file_path       = $relFile
                        rule_name       = $f.RuleName
                        masked_snippet  = $f.MaskedSnippet
                        present_in_head = $true
                    })
                }
            }
        }
    }

    # Pass 2: Scan intermediate commits in BaseRef..HeadRef if BaseRef is provided and git repo exists
    if (-not [string]::IsNullOrWhiteSpace($BaseRef) -and -not [string]::IsNullOrWhiteSpace($RepoRoot)) {
        $dotGit = Join-Path $RepoRoot ".git"
        if (Test-Path -LiteralPath $dotGit) {
            $prevEap = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try {
                $logLines = @(& git -C $RepoRoot log "$BaseRef..$HeadRef" -p -U0 --no-binary --no-color 2>$null)
                if ($LASTEXITCODE -eq 0 -and $logLines.Count -gt 0) {
                    $currentCommit = ""
                    $currentFile = ""
                    $chunkBuilder = New-Object System.Text.StringBuilder
                    $headFileScrubCache = @{}

                    $flushSecretChunk = {
                        param([string]$CommitTag, [string]$RelPath, [System.Text.StringBuilder]$Builder)
                        if ($Builder.Length -eq 0 -or [string]::IsNullOrWhiteSpace($RelPath)) {
                            [void]$Builder.Clear()
                            return
                        }
                        $chunkText = $Builder.ToString()
                        [void]$Builder.Clear()
                        $chunkScrub = Test-KongorSecretScrub -Content $chunkText
                        if (-not $chunkScrub.Passed) {
                            foreach ($lf in $chunkScrub.Findings) {
                                $inHead = $false
                                if ($headSecretMap.ContainsKey("$RelPath|$($lf.MaskedSnippet)")) {
                                    $inHead = $true
                                } else {
                                    if (-not $headFileScrubCache.ContainsKey($RelPath)) {
                                        $diskPath = Join-Path $RepoRoot ($RelPath -replace '/', '\')
                                        if (Test-Path -LiteralPath $diskPath -PathType Leaf) {
                                            $headFileScrubCache[$RelPath] = Test-KongorSecretScrub -FilePath $diskPath
                                        } else {
                                            $headFileScrubCache[$RelPath] = $null
                                        }
                                    }
                                    $cachedHeadScrub = $headFileScrubCache[$RelPath]
                                    if ($null -ne $cachedHeadScrub) {
                                        foreach ($hf in $cachedHeadScrub.Findings) {
                                            if ($hf.MaskedSnippet -eq $lf.MaskedSnippet) {
                                                $inHead = $true
                                                break
                                            }
                                        }
                                    }
                                }
                                $dedupKey = "$CommitTag|$RelPath|$($lf.RuleName)|$($lf.MaskedSnippet)"
                                if ($seenKeys.Add($dedupKey)) {
                                    $allLeaks.Add([PSCustomObject]@{
                                        commit          = $CommitTag
                                        file_path       = $RelPath
                                        rule_name       = $lf.RuleName
                                        masked_snippet  = $lf.MaskedSnippet
                                        present_in_head = $inHead
                                    })
                                }
                            }
                        }
                    }

                    foreach ($line in $logLines) {
                        if ($line.StartsWith("commit ") -and $line -match '^commit\s+([0-9a-fA-F]{7,40})') {
                            & $flushSecretChunk $currentCommit $currentFile $chunkBuilder
                            $currentCommit = $Matches[1].Substring(0, [Math]::Min(12, $Matches[1].Length))
                            $currentFile = ""
                            continue
                        }
                        if ($line.StartsWith("+++ b/")) {
                            & $flushSecretChunk $currentCommit $currentFile $chunkBuilder
                            $currentFile = $line.Substring(6).Trim()
                            continue
                        }
                        if ($line.StartsWith("+") -and (-not $line.StartsWith("+++"))) {
                            if ($currentFile -ieq 'scripts/test_phase1_security_firewall.ps1' -or $currentFile -ieq 'scripts/test_gate_mutants.ps1' -or $currentFile -ieq 'tests/unit/tools/Kongor.Mcp.Tests/RepoWiseAndAutoResearchWave4Tests.cs') { continue }
                            if ($currentFile -match '(?i)\.(?:s2z|zip|dll|exe|so(?:\.\d+)*|dylib|pdb|png|jpg|jpeg|gif|webp|dds|tga|ogg|wav|mp3|mp4|webm|honreplay|pcapng|model|clip)$') { continue }
                            [void]$chunkBuilder.AppendLine($line.Substring(1))
                        }
                    }
                    & $flushSecretChunk $currentCommit $currentFile $chunkBuilder
                }
            } finally {
                $ErrorActionPreference = $prevEap
            }
        }
    }

    return [PSCustomObject]@{
        Passed                = ($allLeaks.Count -eq 0)
        TotalLeaks            = $allLeaks.Count
        IntermediateOnlyLeaks = @($allLeaks | Where-Object { -not $_.present_in_head }).Count
        Leaks                 = $allLeaks.ToArray()
    }
}

function ConvertTo-KongorRedactedText {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Text,

        [Parameter(Mandatory = $false)]
        [hashtable]$CustomValues = @{},

        [Parameter(Mandatory = $false)]
        [switch]$ResetSession
    )

    if ($ResetSession) {
        Reset-KongorRedactionSession
    }

    if ([string]::IsNullOrEmpty($Text)) {
        return $Text
    }

    $candidates = @{}

    # 1. Add caller-supplied custom values (normalized for trailing FQDN dots and < 3-char skip guard)
    foreach ($k in $CustomValues.Keys) {
        $rawVal = [string]$k
        $normVal = $rawVal.TrimEnd('.')
        if ($normVal.Length -lt 3) {
            continue
        }
        $cat = ([string]$CustomValues[$k]).ToUpperInvariant()
        if ([string]::IsNullOrWhiteSpace($cat)) { $cat = "CRED" }
        $candidates[$normVal] = $cat
        if ($rawVal -ne $normVal -and $rawVal.Length -ge 3) {
            $candidates[$rawVal] = $cat
        }
    }

    # 2. Extract secret matches from the 14+ high-signal definitions
    foreach ($def in $script:KongorSecretPatternDefinitions) {
        $matchesList = [regex]::Matches($Text, $def.Pattern)
        foreach ($m in $matchesList) {
            $val = $m.Value.Trim()
            if ($val.Length -ge 3) {
                $candidates[$val] = $def.Category
            }
        }
    }

    # 3. Extract IPv4 addresses (excluding 0.0.0.0 and 127.0.0.1 loopback)
    $ipMatches = [regex]::Matches($Text, '\b(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\b')
    foreach ($ipm in $ipMatches) {
        $ipVal = $ipm.Value
        if ($ipVal -notin @("0.0.0.0", "127.0.0.1") -and $ipVal.Length -ge 7) {
            $candidates[$ipVal] = "IP"
        }
    }

    if ($candidates.Count -eq 0) {
        return $Text
    }

    # Sort longest-match-first (#63 longest-match-redaction-sort) so substring replacements never corrupt superstrings
    $sortedKeys = @($candidates.Keys | Sort-Object { $_.Length } -Descending)
    $redacted = $Text

    foreach ($secretKey in $sortedKeys) {
        if ($secretKey.Length -lt 3) { continue }
        $cat = $candidates[$secretKey]
        $canonicalKey = $secretKey.TrimEnd('.')
        $mapLookup = "$cat|$canonicalKey"

        if (-not $script:KongorRedactionSessionState.Map.ContainsKey($mapLookup)) {
            $nextIdx = if ($script:KongorRedactionSessionState.Counters.ContainsKey($cat)) {
                [int]$script:KongorRedactionSessionState.Counters[$cat] + 1
            } else {
                1
            }
            $script:KongorRedactionSessionState.Counters[$cat] = $nextIdx
            $script:KongorRedactionSessionState.Map[$mapLookup] = "<${cat}_${nextIdx}>"
        }

        $placeholder = $script:KongorRedactionSessionState.Map[$mapLookup]
        $escaped = [regex]::Escape($secretKey)
        $redacted = [regex]::Replace($redacted, $escaped, $placeholder)
    }

    return $redacted
}

function Test-KongorSafeUrl {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Url,

        [Parameter(Mandatory = $false)]
        [switch]$AllowLoopback
    )

    $uri = $null
    if (-not [System.Uri]::TryCreate($Url, [System.UriKind]::Absolute, [ref]$uri)) {
        return [PSCustomObject]@{ Safe = $false; Reason = "Malformed absolute URL: '$Url'"; Host = "" }
    }

    if ($uri.Scheme -notin @("http", "https")) {
        return [PSCustomObject]@{ Safe = $false; Reason = "Disallowed URI scheme '$($uri.Scheme)' (only http/https permitted)."; Host = $uri.Host }
    }

    if (-not [string]::IsNullOrEmpty($uri.UserInfo)) {
        return [PSCustomObject]@{ Safe = $false; Reason = "Embedded credentials (user:pass@host) are forbidden in URLs."; Host = $uri.Host }
    }

    $hostName = $uri.DnsSafeHost.TrimEnd('.').ToLowerInvariant()
    if ($hostName -in @("metadata.google.internal", "metadata", "169.254.169.254", "instance-data")) {
        return [PSCustomObject]@{ Safe = $false; Reason = "SSRF blocked: Cloud metadata endpoint '$hostName'."; Host = $hostName }
    }

    if (-not $AllowLoopback -and $hostName -in @("localhost", "localhost.localdomain", "ip6-localhost")) {
        return [PSCustomObject]@{ Safe = $false; Reason = "SSRF blocked: Loopback hostname '$hostName'."; Host = $hostName }
    }

    $ipAddr = $null
    if ([System.Net.IPAddress]::TryParse($hostName, [ref]$ipAddr)) {
        $bytes = $ipAddr.GetAddressBytes()
        if ($ipAddr.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetwork) {
            $b0 = $bytes[0]
            $b1 = $bytes[1]
            if ($b0 -eq 0) {
                return [PSCustomObject]@{ Safe = $false; Reason = "SSRF blocked: Unspecified 0.0.0.0/8 IPv4 address '$hostName'."; Host = $hostName }
            }
            if (-not $AllowLoopback -and $b0 -eq 127) {
                return [PSCustomObject]@{ Safe = $false; Reason = "SSRF blocked: Loopback 127.0.0.0/8 IPv4 address '$hostName'."; Host = $hostName }
            }
            if ($b0 -eq 169 -and $b1 -eq 254) {
                return [PSCustomObject]@{ Safe = $false; Reason = "SSRF blocked: Link-local / IMDS 169.254.0.0/16 IPv4 address '$hostName'."; Host = $hostName }
            }
            if ($b0 -eq 10 -or ($b0 -eq 172 -and $b1 -ge 16 -and $b1 -le 31) -or ($b0 -eq 192 -and $b1 -eq 168)) {
                return [PSCustomObject]@{ Safe = $false; Reason = "SSRF blocked: RFC1918 private IPv4 address '$hostName'."; Host = $hostName }
            }
        } elseif ($ipAddr.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetworkV6) {
            if ($ipAddr.Equals([System.Net.IPAddress]::IPv6None) -or $ipAddr.Equals([System.Net.IPAddress]::IPv6Any)) {
                return [PSCustomObject]@{ Safe = $false; Reason = "SSRF blocked: Unspecified IPv6 address '$hostName'."; Host = $hostName }
            }
            if (-not $AllowLoopback -and [System.Net.IPAddress]::IsLoopback($ipAddr)) {
                return [PSCustomObject]@{ Safe = $false; Reason = "SSRF blocked: Loopback IPv6 address '$hostName'."; Host = $hostName }
            }
            if ($ipAddr.IsIPv6LinkLocal -or $ipAddr.IsIPv6SiteLocal -or (($bytes[0] -band 0xFE) -eq 0xFC)) {
                return [PSCustomObject]@{ Safe = $false; Reason = "SSRF blocked: Link-local/ULA private IPv6 address '$hostName'."; Host = $hostName }
            }
        }
    }

    return [PSCustomObject]@{ Safe = $true; Reason = ""; Host = $hostName }
}

function Test-KongorDangerousCommand {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$CommandLine
    )

    if ([string]::IsNullOrWhiteSpace($CommandLine)) {
        return [PSCustomObject]@{ Safe = $true; MatchedRule = ""; Reason = "" }
    }

    $dangerousRules = @(
        @{ Name = 'git_reset_hard';            Pattern = '(?i)\bgit\s+reset\s+--hard\b'; Reason = 'Destructive git reset --hard requires explicit user permission.' },
        @{ Name = 'git_checkout_dot';          Pattern = '(?i)\bgit\s+checkout\s+(?:--\s+)?\.(?:\s|$)'; Reason = 'Destructive git checkout . discards uncommitted work.' },
        @{ Name = 'git_restore_dot';           Pattern = '(?i)\bgit\s+restore\s+(?:--worktree\s+)?\.(?:\s|$)'; Reason = 'Destructive git restore . discards uncommitted work.' },
        @{ Name = 'git_push_force';            Pattern = '(?i)\bgit\s+push\b[^\r\n]*\s(?:--force(?!-with-lease)|-f\b)'; Reason = 'Force-pushing without lease is blocked by data-loss firewall.' },
        @{ Name = 'remove_item_recurse_force'; Pattern = '(?i)\bRemove-Item\b[^\r\n]*-Recurse[^\r\n]*-Force|\bRemove-Item\b[^\r\n]*-Force[^\r\n]*-Recurse'; Reason = 'Recursive forced deletion requires explicit user permission.' },
        @{ Name = 'format_or_clear_disk';      Pattern = '(?i)\b(?:Format-Volume|Clear-Disk|Initialize-Disk)\b'; Reason = 'Volume/disk destruction cmdlets are strictly forbidden.' },
        @{ Name = 'rm_rf_root';                Pattern = '(?i)\brm\s+-[a-zA-Z]*r[a-zA-Z]*f[a-zA-Z]*\s+(?:/|~|\*|\.\.?)(?:\s|$)'; Reason = 'Recursive root/home rm -rf is blocked.' }
    )

    foreach ($rule in $dangerousRules) {
        if ([regex]::IsMatch($CommandLine, $rule.Pattern)) {
            return [PSCustomObject]@{
                Safe        = $false
                MatchedRule = $rule.Name
                Reason      = $rule.Reason
            }
        }
    }

    return [PSCustomObject]@{ Safe = $true; MatchedRule = ""; Reason = "" }
}

function Test-KongorLocalPiiGuard {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $false)]
        [string]$RepoRoot = "",

        [Parameter(Mandatory = $false)]
        [string]$ConfigPath = "",

        [Parameter(Mandatory = $false)]
        [string[]]$CandidateFiles = @(),

        [Parameter(Mandatory = $false)]
        [string]$Content = "",

        [Parameter(Mandatory = $false)]
        [string]$BaseRef = "",

        [Parameter(Mandatory = $false)]
        [string]$HeadRef = "HEAD",

        [Parameter(Mandatory = $false)]
        [switch]$RequireDenylist
    )

    if ([string]::IsNullOrWhiteSpace($ConfigPath) -and -not [string]::IsNullOrWhiteSpace($RepoRoot)) {
        $ConfigPath = Join-Path $RepoRoot "config\local_pii_guard.local.json"
    }

    $isCi = ($env:CI -eq "true" -or $env:GITHUB_ACTIONS -eq "true" -or $env:TF_BUILD -eq "True")
    $piiTerms = New-Object System.Collections.Generic.List[object]

    if (-not [string]::IsNullOrWhiteSpace($ConfigPath) -and (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
        $cfgObj = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
        foreach ($entry in @($cfgObj.terms)) {
            if ($null -eq $entry) { continue }
            if ($entry -is [string]) {
                $valStr = $entry.Trim()
                if ($valStr.Length -ge 3) {
                    $piiTerms.Add([PSCustomObject]@{ Value = $valStr; Category = "own_identifier"; Severity = "high" })
                }
            } else {
                $valStr = ([string]$entry.value).Trim()
                $sevStr = if ($entry.severity) { ([string]$entry.severity).ToLowerInvariant() } else { "high" }
                if ($valStr.Length -ge 3 -and $sevStr -in @("critical", "high", "medium")) {
                    $catStr = if ($entry.category) { [string]$entry.category } else { "own_identifier" }
                    $piiTerms.Add([PSCustomObject]@{ Value = $valStr; Category = $catStr; Severity = $sevStr })
                }
            }
        }
    } elseif ($RequireDenylist -and (-not $isCi)) {
        return [PSCustomObject]@{
            Passed   = $false
            Reason   = "Missing required local PII denylist at '$ConfigPath'. Create config/local_pii_guard.local.json before pushing or exporting."
            Findings = @(
                [PSCustomObject]@{
                    commit         = "CONFIG"
                    file_path      = "config/local_pii_guard.local.json"
                    rule_name      = "missing_local_pii_denylist"
                    masked_snippet = "missing"
                }
            )
        }
    }

    $winUserPathPattern = '(?i)(?:file:///c:/users/|c:\\+users\\+)(?!(?:developer|testdev|testdeveloper|devuser|canary_dev_user|administrator|alice|bob|example|public|runner|runneradmin|default|all users|johndoe|kongordev)[\\/])([a-z0-9_.-]{3,})[\\/]'
    $winUserPathRegex = [regex]::new($winUserPathPattern, [System.Text.RegularExpressions.RegexOptions]::Compiled)
    $posixUserPathPattern = '(?-i)(?<![A-Za-z0-9_])/(?:home|Users)/(?!(?i:developer|testdev|testdeveloper|devuser|canary_dev_user|runner|runneradmin|ubuntu|vscode|user|alice|bob|example|kongor|kongordev|app|root|voicebox|dev|manu)/)([A-Za-z0-9_.-]{2,})/'
    $posixUserPathRegex = [regex]::new($posixUserPathPattern, [System.Text.RegularExpressions.RegexOptions]::Compiled)
    $pycharmPathPattern = '(?i)(?:file:///[a-z]:/|[a-z]:[\\/]+|/(?:home|Users)/|~/|\\\\)[^\s"''()<>]*?\bPycharmProjects\b'
    $pycharmPathRegex = [regex]::new($pycharmPathPattern, [System.Text.RegularExpressions.RegexOptions]::Compiled)
    $findings = New-Object System.Collections.Generic.List[object]
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)

    $scanTextHelper = {
        param(
            [string]$TextToScan,
            [string]$RelPath,
            [string]$CommitTag,
            $OutFindings = $findings,
            $OutSeen = $seen
        )
        if ([string]::IsNullOrEmpty($TextToScan)) { return }
        $hasCandidateHit = $false
        foreach ($pt in $piiTerms) {
            if ($TextToScan.IndexOf($pt.Value, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $hasCandidateHit = $true
                break
            }
        }
        if ((-not $hasCandidateHit) -and ($TextToScan.IndexOf('users', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) -and $winUserPathRegex.IsMatch($TextToScan)) {
            $hasCandidateHit = $true
        }
        if ((-not $hasCandidateHit) -and (($TextToScan.IndexOf('/home/', [System.StringComparison]::Ordinal) -ge 0) -or ($TextToScan.IndexOf('/Users/', [System.StringComparison]::Ordinal) -ge 0)) -and $posixUserPathRegex.IsMatch($TextToScan)) {
            $hasCandidateHit = $true
        }
        if ((-not $hasCandidateHit) -and ($TextToScan.IndexOf('pycharmprojects', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) -and $pycharmPathRegex.IsMatch($TextToScan)) {
            $hasCandidateHit = $true
        }
        if (-not $hasCandidateHit) { return }

        $lines = $TextToScan -split "`r?`n"
        for ($idx = 0; $idx -lt $lines.Length; $idx++) {
            $ln = $lines[$idx]
            if ([string]::IsNullOrEmpty($ln) -or $ln -match 'kongor-security-ignore') { continue }

            foreach ($pt in $piiTerms) {
                if ($ln.IndexOf($pt.Value, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    $masked = Get-KongorMaskedSecretSnippet -Secret $pt.Value
                    $dk = "$CommitTag|$RelPath|pii_$($pt.Category)|$masked"
                    if ($OutSeen.Add($dk)) {
                        $OutFindings.Add([PSCustomObject]@{
                            commit         = $CommitTag
                            file_path      = $RelPath
                            rule_name      = "local_pii_$($pt.Category)"
                            masked_snippet = $masked
                        })
                    }
                }
            }

            foreach ($wm in $winUserPathRegex.Matches($ln)) {
                if ($ln -match '(?i)(?:[c-z]:[\\/]+users[\\/]+hyperxewl[\\/]+desktop[\\/]+cef_binary_|/users/hyperxewl/(?:desktop/(?:cef_binary_|curl/|libxml2/)|downloads/(?:speex-1\.2\.0/|speexdsp-1\.2rc3/)))') {
                    continue
                }
                $userSeg = $wm.Groups[1].Value
                $maskedUser = Get-KongorMaskedSecretSnippet -Secret $userSeg
                $dk = "$CommitTag|$RelPath|hardcoded_windows_user_path|$maskedUser"
                if ($OutSeen.Add($dk)) {
                    $OutFindings.Add([PSCustomObject]@{
                        commit         = $CommitTag
                        file_path      = $RelPath
                        rule_name      = "hardcoded_windows_user_path"
                        masked_snippet = $maskedUser
                    })
                }
            }

            foreach ($pm in $posixUserPathRegex.Matches($ln)) {
                if ($ln -match '(?i)/users/hyperxewl/(?:desktop/(?:cef_binary_|curl/|libxml2/)|downloads/(?:speex-1\.2\.0/|speexdsp-1\.2rc3/))') {
                    continue
                }
                $userSeg = $pm.Groups[1].Value
                $maskedUser = Get-KongorMaskedSecretSnippet -Secret $userSeg
                $dk = "$CommitTag|$RelPath|hardcoded_posix_user_path|$maskedUser"
                if ($OutSeen.Add($dk)) {
                    $OutFindings.Add([PSCustomObject]@{
                        commit         = $CommitTag
                        file_path      = $RelPath
                        rule_name      = "hardcoded_posix_user_path"
                        masked_snippet = $maskedUser
                    })
                }
            }

            foreach ($ym in $pycharmPathRegex.Matches($ln)) {
                if ($ln -match '(?i)(?:canary_dev_user|TestDeveloper|JaneDoe_Profile|SomePrivateUser|SomeRealDeveloper)(?:\\\\|\\|/)+PycharmProjects') {
                    continue
                }
                $maskedPyc = Get-KongorMaskedSecretSnippet -Secret $ym.Value
                $dk = "$CommitTag|$RelPath|hardcoded_pycharm_path|$maskedPyc"
                if ($OutSeen.Add($dk)) {
                    $OutFindings.Add([PSCustomObject]@{
                        commit         = $CommitTag
                        file_path      = $RelPath
                        rule_name      = "hardcoded_pycharm_path"
                        masked_snippet = $maskedPyc
                    })
                }
            }
        }
    }

    if (-not [string]::IsNullOrEmpty($Content)) {
        & $scanTextHelper $Content "<inline>" "INLINE" $findings $seen
    }

    $useGitHeadBlob = (-not [string]::IsNullOrWhiteSpace($BaseRef)) -and (-not [string]::IsNullOrWhiteSpace($RepoRoot)) -and (Test-Path -LiteralPath (Join-Path $RepoRoot ".git"))
    $dirtyRelSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    if ($useGitHeadBlob) {
        $prevDirtyEap = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            foreach ($dl in @(& git -C $RepoRoot diff --name-only "$HeadRef" 2>$null)) {
                if (-not [string]::IsNullOrWhiteSpace($dl)) {
                    [void]$dirtyRelSet.Add(($dl.Trim() -replace '\\', '/'))
                }
            }
        } finally {
            $ErrorActionPreference = $prevDirtyEap
        }
    }

    $piiPass1Files = New-Object System.Collections.Generic.List[string]
    $piiDirectDiskCandidates = New-Object System.Collections.Generic.List[string]
    foreach ($cf in $CandidateFiles) {
        if ([string]::IsNullOrWhiteSpace($cf)) { continue }
        if ($cf -match '(?i)\.(?:s2z|zip|dll|exe|so(?:\.\d+)*|dylib|pdb|png|jpg|jpeg|gif|webp|dds|tga|ogg|wav|mp3|mp4|webm|honreplay|pcapng|model|clip)$') { continue }
        $relFile = if (-not [string]::IsNullOrWhiteSpace($RepoRoot) -and $cf.StartsWith($RepoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            $cf.Substring($RepoRoot.Length).TrimStart('\', '/') -replace '\\', '/'
        } else {
            $cf -replace '\\', '/'
        }
        $hasDiskLeaf = [System.IO.File]::Exists($cf)
        if ($useGitHeadBlob -and ($dirtyRelSet.Contains($relFile) -or (-not $hasDiskLeaf))) {
            $piiPass1Files.Add($cf)
        } elseif ($hasDiskLeaf) {
            $piiDirectDiskCandidates.Add($cf)
        }
    }
    if ($piiDirectDiskCandidates.Count -gt 20) {
        $piiTermArr = @($piiTerms | ForEach-Object { $_.Value })
        foreach ($hitFile in [KongorScrubFastPrefilter]::FilterPiiCandidates($piiDirectDiskCandidates.ToArray(), $piiTermArr, $winUserPathPattern, $posixUserPathPattern, $pycharmPathPattern)) {
            $piiPass1Files.Add($hitFile)
        }
    } else {
        foreach ($dc in $piiDirectDiskCandidates) { $piiPass1Files.Add($dc) }
    }

    foreach ($cf in $piiPass1Files) {
        $relFile = if (-not [string]::IsNullOrWhiteSpace($RepoRoot) -and $cf.StartsWith($RepoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            $cf.Substring($RepoRoot.Length).TrimStart('\', '/') -replace '\\', '/'
        } else {
            $cf -replace '\\', '/'
        }
        $rawText = ""
        $hasDiskLeaf = [System.IO.File]::Exists($cf)
        if ($useGitHeadBlob -and ($dirtyRelSet.Contains($relFile) -or (-not $hasDiskLeaf))) {
            $prevBlobEap = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try {
                $blobLines = @(& git -C $RepoRoot show "${HeadRef}:${relFile}" 2>$null)
                if ($LASTEXITCODE -eq 0 -and $blobLines.Count -gt 0) {
                    $rawText = $blobLines -join "`n"
                }
            } finally {
                $ErrorActionPreference = $prevBlobEap
            }
        } elseif ($hasDiskLeaf) {
            $rawText = [System.IO.File]::ReadAllText($cf)
        }
        & $scanTextHelper $rawText $relFile "HEAD" $findings $seen
    }

    $remediatedHistoryCount = 0
    if (-not [string]::IsNullOrWhiteSpace($BaseRef) -and -not [string]::IsNullOrWhiteSpace($RepoRoot)) {
        $dotGit = Join-Path $RepoRoot ".git"
        if (Test-Path -LiteralPath $dotGit) {
            $headFilePiiCache = @{}
            $getHeadFilePiiSet = {
                param([string]$RelFileToCheck)
                if ($headFilePiiCache.ContainsKey($RelFileToCheck)) {
                    return ,$headFilePiiCache[$RelFileToCheck]
                }
                $fileSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
                $headBlobText = ""
                $prevCheckEap = $ErrorActionPreference
                $ErrorActionPreference = 'Continue'
                try {
                    $chkLines = @(& git -C $RepoRoot show "${HeadRef}:${RelFileToCheck}" 2>$null)
                    if ($LASTEXITCODE -eq 0 -and $chkLines.Count -gt 0) {
                        $headBlobText = $chkLines -join "`n"
                    }
                } finally {
                    $ErrorActionPreference = $prevCheckEap
                }
                if (-not [string]::IsNullOrEmpty($headBlobText)) {
                    $diskFindings = New-Object System.Collections.Generic.List[object]
                    $diskSeen = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
                    & $scanTextHelper $headBlobText $RelFileToCheck "HEAD_CHECK" $diskFindings $diskSeen
                    foreach ($df in $diskFindings) {
                        [void]$fileSet.Add("$($df.rule_name)|$($df.masked_snippet)")
                    }
                }
                $headFilePiiCache[$RelFileToCheck] = $fileSet
                return ,$fileSet
            }

            $prevEap = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try {
                $logLines = @(& git -C $RepoRoot log "$BaseRef..$HeadRef" -p -U0 --no-binary --no-color 2>$null)
                if ($LASTEXITCODE -eq 0 -and $logLines.Count -gt 0) {
                    $currCommit = ""
                    $currFile = ""
                    $piiChunkBuilder = New-Object System.Text.StringBuilder

                    $flushPiiChunk = {
                        param([string]$CommitTag, [string]$RelPath, [System.Text.StringBuilder]$Builder)
                        if ($Builder.Length -eq 0 -or [string]::IsNullOrWhiteSpace($RelPath)) {
                            [void]$Builder.Clear()
                            return
                        }
                        $chunkText = $Builder.ToString()
                        [void]$Builder.Clear()
                        $chunkFindings = New-Object System.Collections.Generic.List[object]
                        $chunkSeen = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
                        & $scanTextHelper $chunkText $RelPath $CommitTag $chunkFindings $chunkSeen
                        if ($chunkFindings.Count -gt 0) {
                            $activeHeadSet = & $getHeadFilePiiSet $RelPath
                            foreach ($lf in $chunkFindings) {
                                if ($null -ne $activeHeadSet -and $activeHeadSet.Contains("$($lf.rule_name)|$($lf.masked_snippet)")) {
                                    $dk = "$($lf.commit)|$($lf.file_path)|$($lf.rule_name)|$($lf.masked_snippet)"
                                    if ($seen.Add($dk)) {
                                        $findings.Add($lf)
                                    }
                                } else {
                                    $script:remediatedChunkAcc++
                                }
                            }
                        }
                    }

                    $script:remediatedChunkAcc = 0
                    foreach ($line in $logLines) {
                        if ($line.StartsWith("commit ") -and $line -match '^commit\s+([0-9a-fA-F]{7,40})') {
                            & $flushPiiChunk $currCommit $currFile $piiChunkBuilder
                            $currCommit = $Matches[1].Substring(0, [Math]::Min(12, $Matches[1].Length))
                            $currFile = ""
                            continue
                        }
                        if ($line.StartsWith("+++ b/")) {
                            & $flushPiiChunk $currCommit $currFile $piiChunkBuilder
                            $currFile = $line.Substring(6).Trim()
                            continue
                        }
                        if ($line.StartsWith("+") -and (-not $line.StartsWith("+++"))) {
                            if ($currFile -notmatch '(?i)\.(?:s2z|zip|dll|exe|so(?:\.\d+)*|dylib|pdb|png|jpg|jpeg|gif|webp|dds|tga|ogg|wav|mp3|mp4|webm|honreplay|pcapng|model|clip)$') {
                                [void]$piiChunkBuilder.AppendLine($line.Substring(1))
                            }
                        }
                    }
                    & $flushPiiChunk $currCommit $currFile $piiChunkBuilder
                    $remediatedHistoryCount += $script:remediatedChunkAcc
                }
            } finally {
                $ErrorActionPreference = $prevEap
            }
        }
    }

    return [PSCustomObject]@{
        Passed                 = ($findings.Count -eq 0)
        Reason                 = if ($findings.Count -eq 0) { "" } else { "Detected $($findings.Count) private identifier or hardcoded workstation path finding(s)." }
        RemediatedHistoryCount = $remediatedHistoryCount
        Findings               = $findings.ToArray()
    }
}

