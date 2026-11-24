// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Resolved authentication credentials for a K2 client instance.
/// </summary>
public sealed record ResolvedCredentials
{
    public string Username { get; init; } = string.Empty;
    public string PasswordMd5 { get; init; } = string.Empty;
    public bool AutoLogin { get; init; } = true;
    public bool RememberName { get; init; } = true;
    public bool RememberPassword { get; init; } = true;
}

/// <summary>
/// Service interface for client credential resolution, MD5 hashing, and zero-config authentication.
/// </summary>
public interface IClientCredentialManager
{
    string DefaultPasswordMd5 => ClientCredentialManager.DefaultPasswordMd5;
    string HashPassword(string password);
    ResolvedCredentials ResolveCredentials(string? username, string? password, int slotIndex = 0, string? agentId = null);
    IReadOnlyList<string> BuildAuthenticationLaunchArguments(ResolvedCredentials credentials);
}

/// <summary>
/// Manages player account credentials, dynamic password hashing (MD5), and multi-client account isolation.
/// Prevents shared disk config file mutations by generating in-memory launch arguments.
/// </summary>
public sealed class ClientCredentialManager : IClientCredentialManager
{
    public static ClientCredentialManager Default { get; } = new();

    public const string DefaultPassword = "KONGOR";
    public const string DefaultPasswordMd5 = "4d36fc3708f5a844b3be75b3f389db8d";
    public const string PrimaryDefaultUsername = "KongorPlayer";

    private static readonly Regex Hex32Regex = new(@"^[0-9a-fA-F]{32}$", RegexOptions.Compiled);

    /// <summary>
    /// Computes lowercase hex MD5 hash of password. If the input is already a 32-character hex hash, returns it lowercase.
    /// </summary>
    public string HashPassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            return DefaultPasswordMd5;
        }

        string trimmed = password.Trim();
        if (Hex32Regex.IsMatch(trimmed))
        {
            return trimmed.ToLowerInvariant();
        }

        byte[] hashBytes = MD5.HashData(Encoding.UTF8.GetBytes(trimmed));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Resolves account username and password hash according to slot index, explicit inputs, or agent owner.
    /// </summary>
    public ResolvedCredentials ResolveCredentials(string? username, string? password, int slotIndex = 0, string? agentId = null)
    {
        string effectiveUsername;
        if (!string.IsNullOrWhiteSpace(username))
        {
            effectiveUsername = username.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(agentId))
        {
            effectiveUsername = $"agent_{agentId.Trim()}";
        }
        else if (slotIndex == 0)
        {
            effectiveUsername = PrimaryDefaultUsername;
        }
        else
        {
            effectiveUsername = $"guest-{slotIndex:D2}";
        }

        string effectivePasswordMd5 = HashPassword(password ?? DefaultPassword);

        return new ResolvedCredentials
        {
            Username = effectiveUsername,
            PasswordMd5 = effectivePasswordMd5,
            AutoLogin = true,
            RememberName = true,
            RememberPassword = true
        };
    }

    /// <summary>
    /// Builds canonical command-line arguments to inject credentials into hon_x64.exe without modifying config files on disk.
    /// </summary>
    public IReadOnlyList<string> BuildAuthenticationLaunchArguments(ResolvedCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        List<string> args =
        [
            $"+Set login_name \"{credentials.Username}\"",
            $"+Set login_password \"{credentials.PasswordMd5}\"",
            $"+Set login_rememberName {(credentials.RememberName ? "true" : "false")}",
            $"+Set login_rememberPassword {(credentials.RememberPassword ? "true" : "false")}",
            $"+Set login_autoLogin {(credentials.AutoLogin ? "true" : "false")}",
            $"+Set p2p_nickname \"{credentials.Username}\""
        ];

        return args.AsReadOnly();
    }

    // ------------------------------------------------------------------------------------------
    // #195: rea-frida-session-lock-and-split-token-redactor (Gem 1O)
    // ------------------------------------------------------------------------------------------

    public const string RedactedPlaceholder = "[REDACTED_CREDENTIAL]";

    private static readonly Regex CredentialKeyValuePattern = new(
        @"(?<prefix>(?:Bearer\s+|(?:login_password|password|token|api[_-]?key|secret)\s*(?:=|:|\s+)\s*""?))(?<secret>[A-Za-z0-9_\-\.]{6,128})(?<suffix>""?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Redacts sensitive tokens, bearer secrets, and MD5 password hashes across streaming log chunks,
    /// catching credentials even when a token or key=value pair is split across chunk truncation boundaries.
    /// </summary>
    public static IReadOnlyList<string> RedactCredentialsAcrossChunkBoundaries(
        IReadOnlyList<string> chunks,
        IReadOnlyList<string>? explicitSecrets = null)
    {
        if (chunks == null || chunks.Count == 0)
        {
            return Array.Empty<string>();
        }

        // Reassemble with boundary offsets so any secret spanning chunk[i]..chunk[i+1] is detected
        StringBuilder combinedBuilder = new StringBuilder();
        int[] chunkStarts = new int[chunks.Count];
        int[] chunkLengths = new int[chunks.Count];

        for (int i = 0; i < chunks.Count; i++)
        {
            chunkStarts[i] = combinedBuilder.Length;
            string part = chunks[i] ?? string.Empty;
            chunkLengths[i] = part.Length;
            combinedBuilder.Append(part);
        }

        string combined = combinedBuilder.ToString();
        bool[] redactMask = new bool[combined.Length];

        // 1. Mark explicit secrets (including default password hash and custom tokens) across boundaries
        List<string> secretsToScan = new List<string> { DefaultPasswordMd5 };
        if (explicitSecrets != null)
        {
            foreach (string s in explicitSecrets)
            {
                if (!string.IsNullOrWhiteSpace(s) && s.Trim().Length >= 4)
                {
                    secretsToScan.Add(s.Trim());
                }
            }
        }

        foreach (string secret in secretsToScan)
        {
            int searchFrom = 0;
            while (searchFrom < combined.Length)
            {
                int idx = combined.IndexOf(secret, searchFrom, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                    break;
                }
                for (int k = idx; k < idx + secret.Length && k < redactMask.Length; k++)
                {
                    redactMask[k] = true;
                }
                searchFrom = idx + secret.Length;
            }
        }

        // 2. Mark pattern-matched credentials (Bearer <token>, login_password "<hash>", api_key=<val>)
        MatchCollection matches = CredentialKeyValuePattern.Matches(combined);
        foreach (Match m in matches)
        {
            Group secretGroup = m.Groups["secret"];
            if (secretGroup.Success)
            {
                for (int k = secretGroup.Index; k < secretGroup.Index + secretGroup.Length && k < redactMask.Length; k++)
                {
                    redactMask[k] = true;
                }
            }
        }

        // 3. Rebuild each chunk replacing masked character runs with RedactedPlaceholder
        List<string> redactedChunks = new List<string>(chunks.Count);
        for (int i = 0; i < chunks.Count; i++)
        {
            int start = chunkStarts[i];
            int end = start + chunkLengths[i];
            StringBuilder chunkOut = new StringBuilder();
            int pos = start;
            while (pos < end)
            {
                if (redactMask[pos])
                {
                    chunkOut.Append(RedactedPlaceholder);
                    while (pos < end && redactMask[pos])
                    {
                        pos++;
                    }
                }
                else
                {
                    chunkOut.Append(combined[pos]);
                    pos++;
                }
            }
            redactedChunks.Add(chunkOut.ToString());
        }

        return redactedChunks;
    }

    public static string RedactSingleText(string text, IReadOnlyList<string>? explicitSecrets = null)
    {
        IReadOnlyList<string> res = RedactCredentialsAcrossChunkBoundaries(new[] { text ?? string.Empty }, explicitSecrets);
        return res.Count > 0 ? res[0] : string.Empty;
    }
}
