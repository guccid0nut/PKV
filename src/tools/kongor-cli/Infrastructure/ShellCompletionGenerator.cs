// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text;
using Kongor.Core.Configuration;

namespace Kongor.Cli.Infrastructure;

internal enum CompletionShell
{
    PowerShell,
    Bash,
    Zsh,
    Fish
}

/// <summary>
/// Generates instant (&lt;2ms) static shell tab-completion scripts under <c>~/.kongor/completions/</c>.
/// Zero <c>kongor.exe</c> processes are spawned on &lt;Tab&gt;: all commands, subcommands, common flags,
/// and registered workspace names from <c>~/.kongor/config.json</c> are baked into static lookup tables
/// and auto-regenerated on <c>kongor refresh</c> and <c>kongor workspace</c> mutations.
/// </summary>
internal static class ShellCompletionGenerator
{
    public const string ProfileBeginMarker = "# >>> project-kongor cli >>>";
    public const string ProfileEndMarker = "# <<< project-kongor cli <<<";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static readonly IReadOnlyDictionary<string, string[]> SubcommandMap = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["workspace"] = ["current", "list", "use", "add", "remove", "prune"],
        ["ws"] = ["current", "list", "use", "add", "remove", "prune"],
        ["completion"] = ["powershell", "bash", "zsh", "fish"],
        ["mod"] = ["list", "enable", "disable", "inspect", "apply", "rollback", "verify", "harvest", "repair", "pack"],
        ["mod-cli"] = ["list", "enable", "disable", "inspect", "apply", "rollback", "verify", "harvest", "repair", "pack"],
        ["prebake"] = ["all", "entities", "lua", "bundle"],
        ["tools"] = ["docgen", "blender", "test-e2e"],
        ["account"] = ["create"],
        ["invite"] = ["create"],
        ["stop"] = ["dotnet", "docker", "comfy", "blender", "session", "all"],
        ["docker"] = ["up", "down", "restart", "check", "cleanup", "rebuild", "sync"],
        ["migrate"] = ["list", "preview", "apply", "rollback", "add"],
        ["tailwind"] = ["build", "watch", "dev", "setup"],
        ["blender"] = ["install", "uninstall", "stop"],
        ["comfy"] = ["install", "uninstall", "stop"],
        ["comfyui"] = ["install", "uninstall", "stop"],
        ["test"] = ["all", "unit", "integration", "performance", "load", "gui", "adversarial", "e2e", "soak"],
        ["build"] = ["all", "apps", "services", "libs", "tools", "tests", "native", "willowmaker", "compel", "aspire", "cli"],
        ["secret"] = ["list", "init", "set", "get", "remove", "clear"]
    };

    public static readonly string[] GlobalFlags =
    [
        "--root",
        "--workspace",
        "--help",
        "--version"
    ];

    public static string ResolveCompletionsDir(string? userHomeOverride = null)
        => Path.Combine(KongorUserConfigStore.ResolveKongorUserDir(userHomeOverride), "completions");

    public static string ResolveScriptPath(CompletionShell shell, string? userHomeOverride = null)
    {
        string fileName = shell switch
        {
            CompletionShell.PowerShell => "kongor.ps1",
            CompletionShell.Bash => "kongor.bash",
            CompletionShell.Zsh => "_kongor.zsh",
            CompletionShell.Fish => "kongor.fish",
            _ => "kongor.ps1"
        };
        return Path.Combine(ResolveCompletionsDir(userHomeOverride), fileName);
    }

    public static bool TryParseShell(string? raw, out CompletionShell shell)
    {
        string token = (raw ?? string.Empty).Trim().ToLowerInvariant();
        switch (token)
        {
            case "powershell":
            case "pwsh":
            case "ps":
            case "ps1":
                shell = CompletionShell.PowerShell;
                return true;
            case "bash":
                shell = CompletionShell.Bash;
                return true;
            case "zsh":
                shell = CompletionShell.Zsh;
                return true;
            case "fish":
                shell = CompletionShell.Fish;
                return true;
            default:
                shell = CompletionShell.PowerShell;
                return false;
        }
    }

    public static string GenerateScript(
        CompletionShell shell,
        IEnumerable<string> primaryCommands,
        IEnumerable<string>? workspaceAliases = null)
    {
        string[] commands = primaryCommands
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string[] workspaces = (workspaceAliases ?? [])
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(w => w, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return shell switch
        {
            CompletionShell.PowerShell => GeneratePowerShellScript(commands, workspaces),
            CompletionShell.Bash => GenerateBashScript(commands, workspaces),
            CompletionShell.Zsh => GenerateZshScript(commands, workspaces),
            CompletionShell.Fish => GenerateFishScript(commands, workspaces),
            _ => GeneratePowerShellScript(commands, workspaces)
        };
    }

    /// <summary>
    /// Writes static completion scripts for the requested shell (or all shells) to <c>~/.kongor/completions/</c>.
    /// </summary>
    public static string WriteCompletionScript(
        CompletionShell shell,
        IEnumerable<string> primaryCommands,
        string? userHomeOverride = null)
    {
        KongorUserConfig config = KongorUserConfigStore.Load(userHomeOverride);
        string[] workspaces = [.. config.Workspaces.Select(w => w.Name)];
        string script = GenerateScript(shell, primaryCommands, workspaces);

        string path = ResolveScriptPath(shell, userHomeOverride);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + $".tmp.{Environment.ProcessId}";
        File.WriteAllText(temp, script, Utf8NoBom);
        File.Move(temp, path, overwrite: true);
        return path;
    }

    /// <summary>
    /// Best-effort regeneration of all static shell completion scripts after <c>kongor refresh</c>
    /// or <c>kongor workspace</c> mutations.
    /// </summary>
    public static void TryRegenerateAll(IEnumerable<string> primaryCommands, string? userHomeOverride = null)
    {
        try
        {
            foreach (CompletionShell shell in Enum.GetValues<CompletionShell>())
            {
                _ = WriteCompletionScript(shell, primaryCommands, userHomeOverride);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[kongor] [Completion] Best-effort completion script refresh skipped: {ex.Message}");
        }
    }

    /// <summary>
    /// Idempotently injects (or updates) a fenced <c># &gt;&gt;&gt; project-kongor cli &gt;&gt;&gt;</c> source block
    /// into a shell profile file without duplicating existing entries.
    /// </summary>
    public static bool EnsureProfileBlock(string profilePath, string innerLines)
    {
        string normalizedInner = innerLines.Trim('\r', '\n');
        string fencedBlock = $"{ProfileBeginMarker}\n{normalizedInner}\n{ProfileEndMarker}\n";

        string? parent = Path.GetDirectoryName(profilePath);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        if (!File.Exists(profilePath))
        {
            File.WriteAllText(profilePath, fencedBlock, Utf8NoBom);
            return true;
        }

        string existing = File.ReadAllText(profilePath);
        int startIdx = existing.IndexOf(ProfileBeginMarker, StringComparison.Ordinal);
        int endIdx = existing.IndexOf(ProfileEndMarker, StringComparison.Ordinal);

        string updated;
        if (startIdx >= 0 && endIdx > startIdx)
        {
            int afterEnd = endIdx + ProfileEndMarker.Length;
            while (afterEnd < existing.Length && (existing[afterEnd] == '\r' || existing[afterEnd] == '\n'))
            {
                afterEnd++;
            }

            updated = existing[..startIdx] + fencedBlock + existing[afterEnd..];
        }
        else
        {
            string trimmed = existing.TrimEnd('\r', '\n');
            updated = string.IsNullOrEmpty(trimmed)
                ? fencedBlock
                : trimmed + "\n\n" + fencedBlock;
        }

        if (string.Equals(existing, updated, StringComparison.Ordinal))
        {
            return false;
        }

        File.WriteAllText(profilePath, updated, Utf8NoBom);
        return true;
    }

    public static string BuildProfileSnippet(CompletionShell shell, string scriptPath)
    {
        return shell switch
        {
            CompletionShell.PowerShell =>
                $"if (Test-Path -LiteralPath '{scriptPath.Replace("'", "''")}') {{ . '{scriptPath.Replace("'", "''")}' }}",
            CompletionShell.Bash =>
                $"[ -f \"{scriptPath}\" ] && . \"{scriptPath}\"",
            CompletionShell.Zsh =>
                $"[ -f \"{scriptPath}\" ] && source \"{scriptPath}\"",
            CompletionShell.Fish =>
                $"test -f \"{scriptPath}\"; and source \"{scriptPath}\"",
            _ => string.Empty
        };
    }

    private static string GeneratePowerShellScript(string[] commands, string[] workspaces)
    {
        StringBuilder sb = new();
        sb.AppendLine("# Auto-generated static PowerShell argument completer for Project Kongor CLI (<2ms, zero process spawn)");
        sb.AppendLine("# Regenerate with: kongor completion powershell --write");
        sb.AppendLine("$script:__KongorCommands = @(");
        sb.AppendLine("    " + string.Join(", ", commands.Select(c => $"'{EscapePs(c)}'")));
        sb.AppendLine(")");
        sb.AppendLine("$script:__KongorGlobalFlags = @(" + string.Join(", ", GlobalFlags.Select(f => $"'{EscapePs(f)}'")) + ")");
        sb.AppendLine("$script:__KongorWorkspaces = @(" + string.Join(", ", workspaces.Select(w => $"'{EscapePs(w)}'")) + ")");
        sb.AppendLine("$script:__KongorSubcommands = @{");
        foreach (KeyValuePair<string, string[]> kv in SubcommandMap.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"    '{EscapePs(kv.Key)}' = @({string.Join(", ", kv.Value.Select(v => $"'{EscapePs(v)}'"))})");
        }
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("$script:__KongorCompleter = {");
        sb.AppendLine("    param($wordToComplete, $commandAst, $cursorPosition)");
        sb.AppendLine("    $tokens = @($commandAst.CommandElements | Select-Object -Skip 1 | ForEach-Object { $_.Extent.Text })");
        sb.AppendLine("    $prev = if ($tokens.Count -ge 1) { $tokens[$tokens.Count - 1] } else { '' }");
        sb.AppendLine("    if ([string]::IsNullOrEmpty($wordToComplete)) { $prev = if ($tokens.Count -ge 1) { $tokens[$tokens.Count - 1] } else { '' } } else { $prev = if ($tokens.Count -ge 2) { $tokens[$tokens.Count - 2] } else { '' } }");
        sb.AppendLine();
        sb.AppendLine("    if ($prev -in @('--workspace', '--root')) {");
        sb.AppendLine("        $script:__KongorWorkspaces | Where-Object { $_ -like \"$wordToComplete*\" } | ForEach-Object {");
        sb.AppendLine("            [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', \"Kongor workspace '$_'\")");
        sb.AppendLine("        }");
        sb.AppendLine("        return");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    # Identify primary command skipping leading --root/--workspace pairs");
        sb.AppendLine("    $cmd = $null");
        sb.AppendLine("    $sub = $null");
        sb.AppendLine("    for ($i = 0; $i -lt $tokens.Count; $i++) {");
        sb.AppendLine("        $t = $tokens[$i]");
        sb.AppendLine("        if ($t -in @('--root', '--workspace')) { $i++; continue }");
        sb.AppendLine("        if ($t -like '--root=*' -or $t -like '--workspace=*') { continue }");
        sb.AppendLine("        if (-not $cmd) { $cmd = $t.ToLowerInvariant(); continue }");
        sb.AppendLine("        if (-not $sub) { $sub = $t.ToLowerInvariant(); break }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    $candidates = @()");
        sb.AppendLine("    if (-not $cmd -or ($tokens.Count -eq 1 -and -not [string]::IsNullOrEmpty($wordToComplete) -and $cmd -eq $wordToComplete.ToLowerInvariant())) {");
        sb.AppendLine("        $candidates = $script:__KongorCommands + $script:__KongorGlobalFlags");
        sb.AppendLine("    } elseif ($cmd -in @('workspace', 'ws') -and $sub -in @('use', 'remove', 'rm')) {");
        sb.AppendLine("        $candidates = $script:__KongorWorkspaces");
        sb.AppendLine("    } elseif ($script:__KongorSubcommands.ContainsKey($cmd)) {");
        sb.AppendLine("        $candidates = $script:__KongorSubcommands[$cmd] + $script:__KongorGlobalFlags");
        sb.AppendLine("    } else {");
        sb.AppendLine("        $candidates = $script:__KongorGlobalFlags");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    $candidates | Where-Object { $_ -like \"$wordToComplete*\" } | Sort-Object -Unique | ForEach-Object {");
        sb.AppendLine("        [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine("Register-ArgumentCompleter -Native -CommandName @('kongor', 'kongor.exe') -ScriptBlock $script:__KongorCompleter");
        return sb.ToString();
    }

    private static string GenerateBashScript(string[] commands, string[] workspaces)
    {
        StringBuilder sb = new();
        sb.AppendLine("# Auto-generated static Bash completion for Project Kongor CLI (<2ms, zero process spawn)");
        sb.AppendLine("_kongor_completions() {");
        sb.AppendLine("    local cur prev cmd");
        sb.AppendLine("    cur=\"${COMP_WORDS[COMP_CWORD]}\"");
        sb.AppendLine("    prev=\"${COMP_WORDS[COMP_CWORD-1]}\"");
        sb.AppendLine("    cmd=\"${COMP_WORDS[1]}\"");
        sb.AppendLine($"    local workspaces=\"{string.Join(' ', workspaces)}\"");
        sb.AppendLine($"    local global_flags=\"{string.Join(' ', GlobalFlags)}\"");
        sb.AppendLine($"    local commands=\"{string.Join(' ', commands)}\"");
        sb.AppendLine("    if [[ \"$prev\" == \"--workspace\" || \"$prev\" == \"--root\" ]]; then");
        sb.AppendLine("        COMPREPLY=( $(compgen -W \"$workspaces\" -- \"$cur\") )");
        sb.AppendLine("        return 0");
        sb.AppendLine("    fi");
        sb.AppendLine("    if [[ $COMP_CWORD -eq 1 ]]; then");
        sb.AppendLine("        COMPREPLY=( $(compgen -W \"$commands $global_flags\" -- \"$cur\") )");
        sb.AppendLine("        return 0");
        sb.AppendLine("    fi");
        sb.AppendLine("    case \"$cmd\" in");
        foreach (KeyValuePair<string, string[]> kv in SubcommandMap.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"        {kv.Key}) COMPREPLY=( $(compgen -W \"{string.Join(' ', kv.Value)} $workspaces $global_flags\" -- \"$cur\") ); return 0 ;;");
        }
        sb.AppendLine("        *) COMPREPLY=( $(compgen -W \"$global_flags\" -- \"$cur\") ) ;;");
        sb.AppendLine("    esac");
        sb.AppendLine("}");
        sb.AppendLine("complete -F _kongor_completions kongor");
        return sb.ToString();
    }

    private static string GenerateZshScript(string[] commands, string[] workspaces)
    {
        StringBuilder sb = new();
        sb.AppendLine("#compdef kongor");
        sb.AppendLine("# Auto-generated static Zsh completion for Project Kongor CLI (<2ms, zero process spawn)");
        sb.AppendLine("_kongor() {");
        sb.AppendLine($"    local -a commands=({string.Join(' ', commands.Select(c => $"'{c}'"))})");
        sb.AppendLine($"    local -a workspaces=({string.Join(' ', workspaces.Select(w => $"'{w}'"))})");
        sb.AppendLine($"    local -a global_flags=({string.Join(' ', GlobalFlags.Select(f => $"'{f}'"))})");
        sb.AppendLine("    if (( CURRENT == 2 )); then");
        sb.AppendLine("        compadd -a commands global_flags");
        sb.AppendLine("        return");
        sb.AppendLine("    fi");
        sb.AppendLine("    case \"$words[2]\" in");
        foreach (KeyValuePair<string, string[]> kv in SubcommandMap.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"        {kv.Key}) compadd -- {string.Join(' ', kv.Value)} $workspaces $global_flags ;;");
        }
        sb.AppendLine("        *) compadd -a global_flags ;;");
        sb.AppendLine("    esac");
        sb.AppendLine("}");
        sb.AppendLine("compdef _kongor kongor");
        return sb.ToString();
    }

    private static string GenerateFishScript(string[] commands, string[] workspaces)
    {
        StringBuilder sb = new();
        sb.AppendLine("# Auto-generated static Fish completion for Project Kongor CLI (<2ms, zero process spawn)");
        sb.AppendLine("complete -c kongor -f");
        foreach (string cmd in commands)
        {
            sb.AppendLine($"complete -c kongor -n '__fish_use_subcommand' -a '{cmd}'");
        }
        foreach (KeyValuePair<string, string[]> kv in SubcommandMap.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            foreach (string sub in kv.Value)
            {
                sb.AppendLine($"complete -c kongor -n '__fish_seen_subcommand_from {kv.Key}' -a '{sub}'");
            }
        }
        foreach (string ws in workspaces)
        {
            sb.AppendLine($"complete -c kongor -n '__fish_seen_subcommand_from workspace ws' -a '{ws}'");
        }
        sb.AppendLine("complete -c kongor -l root -d 'Explicit ProjectKongor root directory'");
        sb.AppendLine("complete -c kongor -l workspace -d 'Registered workspace alias or path'");
        return sb.ToString();
    }

    private static string EscapePs(string raw) => raw.Replace("'", "''");
}
