// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MERRICK.DatabaseContext.Services;

/// <summary>
///     OWASP-recommended Argon2id password hasher with concurrency protection,
///     constant-time verification, robust exception safety, and strict PHC parameter bounds
///     to prevent memory/CPU exhaustion Denial-of-Service attacks.
/// </summary>
public sealed class Argon2idHasher
{
    private static readonly SemaphoreSlim s_hashGate = new(4, 4);
    private readonly SemaphoreSlim _concurrencyLimiter;
    private readonly ILogger<Argon2idHasher> _logger;

    public const int MinMemorySizeKb = 8;
    public const int MaxMemorySizeKb = 262_144; // 256 MiB
    public const int MinIterations = 1;
    public const int MaxIterations = 10;
    public const int MinParallelism = 1;
    public const int MaxParallelism = 8;

    // OWASP 2024 recommended defaults: 64 MiB memory, 3 iterations, 4 parallelism
    public int MemorySize { get; init; } = 65_536; // 64 MiB
    public int Iterations { get; init; } = 3;
    public int Parallelism { get; init; } = 4;
    public int HashLength { get; init; } = 32;
    public int SaltLength { get; init; } = 16;

    public SemaphoreSlim ConcurrencyLimiter => _concurrencyLimiter;

    public Argon2idHasher(ILogger<Argon2idHasher>? logger = null, SemaphoreSlim? concurrencyLimiter = null)
    {
        _logger = logger ?? NullLogger<Argon2idHasher>.Instance;
        _concurrencyLimiter = concurrencyLimiter ?? s_hashGate;
    }

    /// <summary>
    ///     Hashes a plaintext password using Argon2id. Returns a self-describing PHC string format:
    ///     $argon2id$v=19$m={memory},t={iterations},p={parallelism}${salt_b64}${hash_b64}
    /// </summary>
    public async Task<string> HashPasswordAsync(string password, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        long waitStart = Stopwatch.GetTimestamp();
        _logger.LogDebug("[Argon2idHasher] Awaiting hash gate permit (available slots: {Slots})", _concurrencyLimiter.CurrentCount);

        if (!await _concurrencyLimiter.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false))
        {
            _logger.LogWarning("[Argon2idHasher] Concurrency limit reached on HashPasswordAsync! Potential brute force or CPU saturation.");
            throw new InvalidOperationException("Argon2id hash concurrency limit reached. Please try again shortly.");
        }

        try
        {
            TimeSpan waitDuration = Stopwatch.GetElapsedTime(waitStart);
            _logger.LogDebug("[Argon2idHasher] Acquired hash gate permit after {ElapsedMs}ms. Computing Argon2id hash (m={Memory}, t={Iterations}, p={Parallelism})...",
                waitDuration.TotalMilliseconds, MemorySize, Iterations, Parallelism);

            long computeStart = Stopwatch.GetTimestamp();
            byte[] salt = RandomNumberGenerator.GetBytes(SaltLength);

            using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
            {
                Salt = salt,
                MemorySize = MemorySize,
                Iterations = Iterations,
                DegreeOfParallelism = Parallelism
            };

            byte[] hash = await Task.Run(() => argon2.GetBytes(HashLength), ct).ConfigureAwait(false);
            TimeSpan computeDuration = Stopwatch.GetElapsedTime(computeStart);

            string saltB64 = Convert.ToBase64String(salt);
            string hashB64 = Convert.ToBase64String(hash);
            string formattedHash = $"$argon2id$v=19$m={MemorySize},t={Iterations},p={Parallelism}${saltB64}${hashB64}";

            _logger.LogInformation("[Argon2idHasher] Password hash successfully generated in {ElapsedMs:F1}ms (salt: {SaltLength}B, hash: {HashLength}B)",
                computeDuration.TotalMilliseconds, salt.Length, hash.Length);

            return formattedHash;
        }
        finally
        {
            _concurrencyLimiter.Release();
            _logger.LogDebug("[Argon2idHasher] Released hash gate permit. Available slots: {Slots}", _concurrencyLimiter.CurrentCount);
        }
    }

    /// <summary>
    ///     Synchronously hashes a plaintext password using Argon2id.
    /// </summary>
    public string HashPassword(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        if (!_concurrencyLimiter.Wait(TimeSpan.FromSeconds(5)))
        {
            _logger.LogWarning("[Argon2idHasher] Concurrency limit reached on synchronous HashPassword! Throttling.");
            throw new InvalidOperationException("Argon2id hash concurrency limit reached. Please try again shortly.");
        }

        try
        {
            byte[] salt = RandomNumberGenerator.GetBytes(SaltLength);

            using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
            {
                Salt = salt,
                MemorySize = MemorySize,
                Iterations = Iterations,
                DegreeOfParallelism = Parallelism
            };

            byte[] hash = argon2.GetBytes(HashLength);
            string saltB64 = Convert.ToBase64String(salt);
            string hashB64 = Convert.ToBase64String(hash);
            return $"$argon2id$v=19$m={MemorySize},t={Iterations},p={Parallelism}${saltB64}${hashB64}";
        }
        finally
        {
            _concurrencyLimiter.Release();
        }
    }

    /// <summary>
    ///     Parses and validates an Argon2id PHC string, enforcing DoS upper/lower bounds before allocating memory.
    ///     Safe bounds: 8 KiB &lt;= memory &lt;= 256 MiB (262,144 KiB), 1 &lt;= iterations &lt;= 10, 1 &lt;= parallelism &lt;= 8.
    ///     Rejects invalid/malicious strings immediately in &lt;1 ms.
    /// </summary>
    public static bool TryParsePhcString(
        string phcString,
        out int memorySizeKb,
        out int iterations,
        out int parallelism,
        out byte[] salt,
        out byte[] expectedHash,
        ILogger? logger = null)
    {
        memorySizeKb = 0;
        iterations = 0;
        parallelism = 0;
        salt = [];
        expectedHash = [];

        if (string.IsNullOrWhiteSpace(phcString))
        {
            logger?.LogWarning("[Argon2idHasher] Empty PHC string provided");
            return false;
        }

        // Format: $argon2id$v=19$m=65536,t=3,p=4$[salt_b64]$[hash_b64]
        string[] parts = phcString.Split('$', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5 || !parts[0].Equals("argon2id", StringComparison.OrdinalIgnoreCase))
        {
            logger?.LogWarning("[Argon2idHasher] Invalid hash format or non-argon2id prefix. PartCount={Count}", parts.Length);
            return false;
        }

        string[] paramParts = parts[2].Split(',');
        if (paramParts.Length != 3)
        {
            logger?.LogWarning("[Argon2idHasher] Malformed parameter string in hash: '{ParamString}'", parts[2]);
            return false;
        }

        foreach (string p in paramParts)
        {
            if (p.StartsWith("m=", StringComparison.OrdinalIgnoreCase))
                int.TryParse(p.AsSpan(2), out memorySizeKb);
            else if (p.StartsWith("t=", StringComparison.OrdinalIgnoreCase))
                int.TryParse(p.AsSpan(2), out iterations);
            else if (p.StartsWith("p=", StringComparison.OrdinalIgnoreCase))
                int.TryParse(p.AsSpan(2), out parallelism);
        }

        // Enforce DoS bounds immediately before any buffer allocation
        if (memorySizeKb < MinMemorySizeKb || memorySizeKb > MaxMemorySizeKb ||
            iterations < MinIterations || iterations > MaxIterations ||
            parallelism < MinParallelism || parallelism > MaxParallelism)
        {
            logger?.LogWarning(
                "[Argon2idHasher] PHC parameters out of safe bounds (memory={Memory}Kb [{MinMem}..{MaxMem}], iterations={Iterations} [{MinIter}..{MaxIter}], parallelism={Parallelism} [{MinPar}..{MaxPar}]). Potential DoS attempt rejected in <1ms.",
                memorySizeKb, MinMemorySizeKb, MaxMemorySizeKb,
                iterations, MinIterations, MaxIterations,
                parallelism, MinParallelism, MaxParallelism);
            return false;
        }

        try
        {
            salt = Convert.FromBase64String(parts[3]);
            expectedHash = Convert.FromBase64String(parts[4]);
        }
        catch (FormatException ex)
        {
            logger?.LogWarning(ex, "[Argon2idHasher] Base64 decoding failed for salt or hash");
            return false;
        }

        return true;
    }

    public bool TryParsePhcString(
        string phcString,
        out int memorySizeKb,
        out int iterations,
        out int parallelism,
        out byte[] salt,
        out byte[] expectedHash) =>
        TryParsePhcString(phcString, out memorySizeKb, out iterations, out parallelism, out salt, out expectedHash, _logger);

    /// <summary>
    ///     Asynchronously verifies a plaintext password against a stored Argon2id PHC hash string.
    ///     Validates parameters against DoS bounds before awaiting concurrency permits.
    /// </summary>
    public async Task<bool> VerifyPasswordAsync(string password, string storedHash, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(storedHash))
        {
            _logger.LogWarning("[Argon2idHasher] Verification failed: Empty password or stored hash provided");
            return false;
        }

        if (!TryParsePhcString(storedHash, out int memory, out int iterations, out int parallelism, out byte[] salt, out byte[] expectedHash))
        {
            return false;
        }

        if (!await _concurrencyLimiter.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false))
        {
            _logger.LogWarning("[Argon2idHasher] Concurrency limit reached on VerifyPasswordAsync! Throttling suspicious verification flood.");
            return false;
        }

        try
        {
            return await Task.Run(() => ComputeAndVerify(password, memory, iterations, parallelism, salt, expectedHash), ct).ConfigureAwait(false);
        }
        finally
        {
            _concurrencyLimiter.Release();
        }
    }

    /// <summary>
    ///     Synchronously verifies a plaintext password against a stored Argon2id PHC hash string.
    ///     Returns false gracefully on format errors or mismatches.
    /// </summary>
    public bool VerifyPassword(string password, string storedHash)
    {
        if (string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(storedHash))
        {
            _logger.LogWarning("[Argon2idHasher] Verification failed: Empty password or stored hash provided");
            return false;
        }

        if (!TryParsePhcString(storedHash, out int memory, out int iterations, out int parallelism, out byte[] salt, out byte[] expectedHash))
        {
            return false;
        }

        if (!_concurrencyLimiter.Wait(TimeSpan.FromSeconds(5)))
        {
            _logger.LogWarning("[Argon2idHasher] Concurrency limit reached on synchronous VerifyPassword! Throttling.");
            return false;
        }

        try
        {
            return ComputeAndVerify(password, memory, iterations, parallelism, salt, expectedHash);
        }
        finally
        {
            _concurrencyLimiter.Release();
        }
    }

    private bool ComputeAndVerify(string password, int memory, int iterations, int parallelism, byte[] salt, byte[] expectedHash)
    {
        try
        {
            long start = Stopwatch.GetTimestamp();

            using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
            {
                Salt = salt,
                MemorySize = memory,
                Iterations = iterations,
                DegreeOfParallelism = parallelism
            };

            byte[] computedHash = argon2.GetBytes(expectedHash.Length);
            bool matches = CryptographicOperations.FixedTimeEquals(computedHash, expectedHash);

            TimeSpan duration = Stopwatch.GetElapsedTime(start);
            _logger.LogInformation("[Argon2idHasher] Verification finished in {ElapsedMs:F1}ms. Result: {Result}",
                duration.TotalMilliseconds, matches ? "SUCCESS" : "MISMATCH");

            return matches;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Argon2idHasher] Unexpected exception during password verification");
            return false;
        }
    }
}
