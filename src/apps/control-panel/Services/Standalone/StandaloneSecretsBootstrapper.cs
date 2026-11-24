// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kongor.Core.Configuration;

namespace NEXUS.ControlPanel.GUI.Services.Standalone;

public static class StandaloneSecretsBootstrapper
{
    private static readonly char[] AlphanumericChars =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789".ToCharArray();

    private static readonly char[] UpperChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ".ToCharArray();
    private static readonly char[] LowerChars = "abcdefghijklmnopqrstuvwxyz".ToCharArray();
    private static readonly char[] DigitChars = "0123456789".ToCharArray();
    private static readonly char[] SymbolChars = "!@#$%^&*-_=+".ToCharArray();

    private static readonly char[] AllComplexChars =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789!@#$%^&*-_=+".ToCharArray();

    public static string GenerateAlphanumeric(int length = 24)
    {
        return RandomNumberGenerator.GetString(AlphanumericChars, length);
    }

    public static string GenerateComplexPassword(int length = 24)
    {
        if (length < 20) length = 20;

        char[] chars = new char[length];
        chars[0] = RandomNumberGenerator.GetString(UpperChars, 1)[0];
        chars[1] = RandomNumberGenerator.GetString(UpperChars, 1)[0];
        chars[2] = RandomNumberGenerator.GetString(LowerChars, 1)[0];
        chars[3] = RandomNumberGenerator.GetString(LowerChars, 1)[0];
        chars[4] = RandomNumberGenerator.GetString(DigitChars, 1)[0];
        chars[5] = RandomNumberGenerator.GetString(DigitChars, 1)[0];
        chars[6] = RandomNumberGenerator.GetString(SymbolChars, 1)[0];
        chars[7] = RandomNumberGenerator.GetString(SymbolChars, 1)[0];

        for (int i = 8; i < length; i++)
        {
            chars[i] = RandomNumberGenerator.GetString(AllComplexChars, 1)[0];
        }

        // Fisher-Yates shuffle
        for (int i = length - 1; i > 0; i--)
        {
            int j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }

        return new string(chars);
    }

    public static async Task EnsureBootstrappedAsync(
        BundledLayout layout,
        ISecretsManagerService? secretsManager,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        Action<string> logger = log ?? (msg => Console.WriteLine(msg));

        // Create core directories
        Directory.CreateDirectory(layout.UserData);
        Directory.CreateDirectory(layout.SecretsDir);
        Directory.CreateDirectory(layout.DataProtectionKeys);
        Directory.CreateDirectory(layout.Logs);

        JsonObject vault;
        if (File.Exists(layout.SecretsJson))
        {
            try
            {
                string existingText = await File.ReadAllTextAsync(layout.SecretsJson, ct).ConfigureAwait(false);
                vault = JsonNode.Parse(existingText) as JsonObject ?? [];
            }
            catch (Exception ex)
            {
                logger($"[NexusStandalone] Warning reading existing {layout.SecretsJson}: {ex.Message}");
                vault = [];
            }
        }
        else
        {
            vault = [];
        }

        List<string> addedOrUpdatedKeys = [];

        // 1. Postgres Password
        string postgresPassword;
        if (SecretsManagerService.TryReadString(vault, "database-password", out string existingPg) &&
            !string.IsNullOrWhiteSpace(existingPg))
        {
            postgresPassword = existingPg;
        }
        else if (SecretsManagerService.TryReadString(vault, "POSTGRES_PASSWORD", out string existingPg2) &&
                 !string.IsNullOrWhiteSpace(existingPg2))
        {
            postgresPassword = existingPg2;
        }
        else
        {
            postgresPassword = GenerateAlphanumeric(24);
            vault["database-password"] = postgresPassword;
            addedOrUpdatedKeys.Add($"database-password(len={postgresPassword.Length})");
        }

        string connString = $"Host=127.0.0.1;Port=25432;Database=merrick;Username=merrick;Password={postgresPassword}";

        if (!SecretsManagerService.TryReadString(vault, "Database:Provider", out string existingProvider) ||
            string.IsNullOrWhiteSpace(existingProvider))
        {
            vault["Database:Provider"] = "PostgreSQL";
            addedOrUpdatedKeys.Add("Database:Provider(len=10)");
        }

        if (!SecretsManagerService.TryReadString(vault, "ConnectionStrings:MERRICK", out string existingMerrick) ||
            string.IsNullOrWhiteSpace(existingMerrick))
        {
            vault["ConnectionStrings:MERRICK"] = connString;
            addedOrUpdatedKeys.Add($"ConnectionStrings:MERRICK(len={connString.Length})");
        }

        if (!SecretsManagerService.TryReadString(vault, "Supabase:DatabaseConnectionString", out string existingSupabase) ||
            string.IsNullOrWhiteSpace(existingSupabase))
        {
            vault["Supabase:DatabaseConnectionString"] = connString;
            addedOrUpdatedKeys.Add($"Supabase:DatabaseConnectionString(len={connString.Length})");
        }

        // 2. JWT Signing Key
        string jwtSigningKey;
        if (SecretsManagerService.TryReadString(vault, "jwt-signing-key", out string existingJwt) &&
            !string.IsNullOrWhiteSpace(existingJwt))
        {
            jwtSigningKey = existingJwt;
        }
        else if (SecretsManagerService.TryReadString(vault, "Jwt:SigningKey", out string existingJwt2) &&
                 !string.IsNullOrWhiteSpace(existingJwt2))
        {
            jwtSigningKey = existingJwt2;
        }
        else
        {
            jwtSigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
            vault["jwt-signing-key"] = jwtSigningKey;
            vault["Jwt:SigningKey"] = jwtSigningKey;
            addedOrUpdatedKeys.Add($"jwt-signing-key(len={jwtSigningKey.Length})");
        }

        // 3. Internal Service Key
        string internalServiceKey;
        if (SecretsManagerService.TryReadString(vault, "InternalServiceKey", out string existingIsk) &&
            !string.IsNullOrWhiteSpace(existingIsk))
        {
            internalServiceKey = existingIsk;
        }
        else if (SecretsManagerService.TryReadString(vault, "Security:InternalServiceKey", out string existingIsk2) &&
                 !string.IsNullOrWhiteSpace(existingIsk2))
        {
            internalServiceKey = existingIsk2;
        }
        else
        {
            internalServiceKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            vault["InternalServiceKey"] = internalServiceKey;
            vault["Security:InternalServiceKey"] = internalServiceKey;
            addedOrUpdatedKeys.Add($"InternalServiceKey(len={internalServiceKey.Length})");
        }

        // 4. Compel Authentication Token
        string compelAuthToken;
        if (SecretsManagerService.TryReadString(vault, "Compel:AuthenticationToken", out string existingCat) &&
            !string.IsNullOrWhiteSpace(existingCat))
        {
            compelAuthToken = existingCat;
        }
        else if (SecretsManagerService.TryReadString(vault, "COMPEL_AUTHENTICATION_TOKEN", out string existingCat2) &&
                 !string.IsNullOrWhiteSpace(existingCat2))
        {
            compelAuthToken = existingCat2;
        }
        else
        {
            compelAuthToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            vault["Compel:AuthenticationToken"] = compelAuthToken;
            vault["COMPEL_AUTHENTICATION_TOKEN"] = compelAuthToken;
            addedOrUpdatedKeys.Add($"Compel:AuthenticationToken(len={compelAuthToken.Length})");
        }

        // 5. Host Credentials
        string hostPassword;
        if (!SecretsManagerService.TryReadString(vault, "Compel:HostUserName", out string existingHostUser) ||
            string.IsNullOrWhiteSpace(existingHostUser))
        {
            vault["Compel:HostUserName"] = "kongor_host";
            addedOrUpdatedKeys.Add("Compel:HostUserName(len=11)");
        }

        if (SecretsManagerService.TryReadString(vault, "Compel:HostPassword", out string existingHostPass) &&
            !string.IsNullOrWhiteSpace(existingHostPass))
        {
            hostPassword = existingHostPass;
        }
        else
        {
            hostPassword = GenerateComplexPassword(24);
            vault["Compel:HostPassword"] = hostPassword;
            addedOrUpdatedKeys.Add($"Compel:HostPassword(len={hostPassword.Length})");
        }

        // 6. Admin Password
        string adminPassword;
        if (SecretsManagerService.TryReadString(vault, "Standalone:AdminPassword", out string existingAdminPass) &&
            !string.IsNullOrWhiteSpace(existingAdminPass))
        {
            adminPassword = existingAdminPass;
        }
        else
        {
            adminPassword = GenerateComplexPassword(24);
            vault["Standalone:AdminPassword"] = adminPassword;
            addedOrUpdatedKeys.Add($"Standalone:AdminPassword(len={adminPassword.Length})");
        }

        // 7. Hosting Mode
        if (!SecretsManagerService.TryReadString(vault, "HostingMode", out string existingMode) ||
            string.IsNullOrWhiteSpace(existingMode))
        {
            string envHostingMode = Environment.GetEnvironmentVariable("KONGOR_STANDALONE_HOSTING_MODE") ?? string.Empty;
            string defaultMode = !string.IsNullOrWhiteSpace(envHostingMode) ? envHostingMode : "Local";
            vault["HostingMode"] = defaultMode;
            addedOrUpdatedKeys.Add($"HostingMode(val={defaultMode})");
        }

        // Write nexus_secrets.json atomically
        string tempJson = layout.SecretsJson + ".tmp";
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(tempJson, JsonSerializer.Serialize(vault, jsonOptions), ct).ConfigureAwait(false);
        File.Move(tempJson, layout.SecretsJson, true);

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(layout.SecretsJson, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception ex)
            {
                logger($"[NexusStandalone] Warning setting 0600 on {layout.SecretsJson}: {ex.Message}");
            }
        }

        // 8. Docker Environment File (.env.docker)
        bool needDockerEnv = !File.Exists(layout.DockerEnv);
        if (!needDockerEnv)
        {
            try
            {
                string envContent = await File.ReadAllTextAsync(layout.DockerEnv, ct).ConfigureAwait(false);
                if (!envContent.Contains("POSTGRES_PASSWORD="))
                {
                    needDockerEnv = true;
                }
            }
            catch (Exception ex)
            {
                logger($"[NexusStandalone] Warning reading {layout.DockerEnv}: {ex.Message}");
                needDockerEnv = true;
            }
        }

        if (needDockerEnv)
        {
            string envLine = $"POSTGRES_PASSWORD={postgresPassword}\n";
            await File.WriteAllTextAsync(layout.DockerEnv, envLine, ct).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(layout.DockerEnv, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                catch (Exception ex)
                {
                    logger($"[NexusStandalone] Warning setting 0600 on {layout.DockerEnv}: {ex.Message}");
                }
            }
        }

        // 9. Initial Admin Credentials File
        if (!File.Exists(layout.InitialCredentials))
        {
            StringBuilder sb = new();
            sb.AppendLine("================================================================================");
            sb.AppendLine("KONGOR STANDALONE INITIAL ADMIN CREDENTIALS");
            sb.AppendLine("================================================================================");
            sb.AppendLine("Web Portal: http://127.0.0.1:5266");
            sb.AppendLine("Admin Username: Admin (or admin@kongor.local)");
            sb.AppendLine($"Admin Password: {adminPassword}");
            sb.AppendLine("Host Username: kongor_host");
            sb.AppendLine($"Host Password: {hostPassword}");
            sb.AppendLine();
            sb.AppendLine("Single-use player invite codes will be written to:");
            sb.AppendLine(layout.InviteCodes);
            sb.AppendLine();
            sb.AppendLine("IMPORTANT: Keep this file private. It contains credentials granting full");
            sb.AppendLine("administrative control over your local Kongor cluster.");
            sb.AppendLine("================================================================================");

            await File.WriteAllTextAsync(layout.InitialCredentials, sb.ToString(), ct).ConfigureAwait(false);

            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(layout.InitialCredentials, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                catch (Exception ex)
                {
                    logger($"[NexusStandalone] Warning setting 0600 on {layout.InitialCredentials}: {ex.Message}");
                }
            }
        }

        if (addedOrUpdatedKeys.Count > 0)
        {
            logger($"[NexusStandalone] Bootstrapped standalone secrets in userdata/secrets: [{string.Join(", ", addedOrUpdatedKeys)}]");
        }
        else
        {
            logger("[NexusStandalone] Standalone secrets already bootstrapped in userdata/secrets");
        }
    }
}
