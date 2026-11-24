// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.Tests.InProcess;

/// <summary>
///     In-memory test double for Redis IDatabase using ConcurrentDictionary for simple storage.
/// </summary>
/// <remarks>
///     This implementation provides basic Redis-like operations for unit testing purposes.
///     It does not replicate full Redis semantics (e.g. expiration is ignored, atomic operations are simplified).
///     Hash operations use composite keys in the format "hashKey:field" for storage.
/// </remarks>
[AutoImplementMissingMembers]
public partial class InProcessDistributedCacheStore : IDatabase
{
    internal ConcurrentDictionary<string, RedisValue> StoreItems { get; } = new();

    // String Operations

    // ReSharper disable MethodOverloadWithOptionalParameter
    public Task<bool> StringSetAsync(RedisKey key, RedisValue value, TimeSpan? expiry = null, When when = When.Always,
        CommandFlags flags = CommandFlags.None)
    {
        string keyStr = GetKeyString(key);
        if ((when == When.NotExists && StoreItems.ContainsKey(keyStr)) ||
            (when == When.Exists && !StoreItems.ContainsKey(keyStr)))
            return Task.FromResult(false);

        StoreItems[keyStr] = value;

        return Task.FromResult(true);
    }
    // ReSharper restore MethodOverloadWithOptionalParameter

    public Task<RedisValue> StringGetAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
    {
        string keyStr = GetKeyString(key);
        bool found = StoreItems.TryGetValue(keyStr, out RedisValue value);

        return Task.FromResult(found ? value : RedisValue.Null);
    }

    // Key Operations

    public Task<bool> KeyDeleteAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
    {
        string keyString = GetKeyString(key);
        string hashPrefix = keyString + ":";

        bool keyDeleted = StoreItems.TryRemove(keyString, out _);

        List<string> keysToDelete =
            [.. StoreItems.Keys.Where(storageKey => storageKey.StartsWith(hashPrefix, StringComparison.Ordinal))];

        int hashFieldsDeleted = keysToDelete.Count(storageKey => StoreItems.TryRemove(storageKey, out _));

        return Task.FromResult(keyDeleted || hashFieldsDeleted > 0);
    }

    public Task<long> KeyDeleteAsync(RedisKey[] keys, CommandFlags flags = CommandFlags.None)
    {
        long count = keys.LongCount(InternalKeyDelete);
        return Task.FromResult(count);
    }

    // Hash Operations

    public Task HashSetAsync(RedisKey key, HashEntry[] hashFields, CommandFlags flags = CommandFlags.None)
    {
        string keyString = key.ToString();

        foreach (HashEntry entry in hashFields)
        {
            string compositeKey = $"{keyString}:{entry.Name}";
            StoreItems[compositeKey] = entry.Value;
        }

        return Task.CompletedTask;
    }

    public Task<RedisValue> HashGetAsync(RedisKey key, RedisValue hashField, CommandFlags flags = CommandFlags.None)
    {
        string compositeKey = $"{key}:{hashField}";

        bool found = StoreItems.TryGetValue(compositeKey, out RedisValue value);

        return Task.FromResult(found ? value : RedisValue.Null);
    }

    public Task<HashEntry[]> HashGetAllAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
    {
        string keyString = key.ToString();
        string hashPrefix = keyString + ":";

        List<HashEntry> entries =
        [
            .. from kvp in StoreItems
            where kvp.Key.StartsWith(hashPrefix, StringComparison.Ordinal)
            let fieldName = kvp.Key[hashPrefix.Length..]
            select new HashEntry(fieldName, kvp.Value)
        ];

        return Task.FromResult(entries.ToArray());
    }

    public IAsyncEnumerable<HashEntry> HashScanAsync(RedisKey key, RedisValue pattern = default, int pageSize = 250,
        long cursor = 0, int pageOffset = 0, CommandFlags flags = CommandFlags.None)
    {
        string keyString = key.ToString();
        string hashPrefix = keyString + ":";
        string patternString = pattern.ToString();

        string regexPattern = "^" + Regex.Escape(patternString).Replace(@"\*", ".*") + "$";

        Regex regex = new(regexPattern);

        List<HashEntry> matchingEntries =
        [
            .. from kvp in StoreItems
            where kvp.Key.StartsWith(hashPrefix, StringComparison.Ordinal)
            let fullField = kvp.Key[hashPrefix.Length..]
            where string.IsNullOrEmpty(patternString) || regex.IsMatch(fullField)
            select new HashEntry(fullField, kvp.Value)
        ];

        return matchingEntries.ToAsyncEnumerable();
    }

    public Task<RedisResult> ExecuteAsync(string command, params object?[] args)
    {
        if (string.Equals(command, "SCAN", StringComparison.OrdinalIgnoreCase))
        {
            string pattern = "*";
            // args usually: cursor, "MATCH", pattern, "COUNT", count
            for (int i = 0; i < args.Length; i++)
                if (args[i]?.ToString()?.Equals("MATCH", StringComparison.OrdinalIgnoreCase) == true &&
                    i + 1 < args.Length)
                    pattern = args[i + 1]?.ToString() ?? "*";

            string regexPattern = "^" + Regex.Escape(pattern).Replace(@"\*", ".*") + "$";
            Regex regex = new(regexPattern);

            RedisResult[] matchingKeys =
            [
                .. StoreItems.Keys
                    .Where(k => regex.IsMatch(k))
                    .Select(k => RedisResult.Create((RedisKey)k)) // Explicit cast to RedisKey
            ];

            // Construct result: [ "0", [keys] ]
            // The outer array should be a RedisResult wrapping a RedisResult[]
            // Element 0: Cursor "0"
            // Element 1: Array of Keys (RedisResult wrapping RedisResult[])

            RedisResult cursorResult = RedisResult.Create((RedisValue)"0"); // Explicit cast to RedisValue
            RedisResult keysResult = RedisResult.Create(matchingKeys);

            RedisResult result = RedisResult.Create([cursorResult, keysResult]);
            return Task.FromResult(result);
        }

        throw new NotSupportedException($"Command '{command}' is not supported by InProcessDistributedCacheStore.");
    }

    private static string GetKeyString(RedisKey key)
    {
        return (string?)key ?? key.ToString();
    }

    // Helper to reuse logic if needed, or just inline
    private bool InternalKeyDelete(RedisKey key)
    {
        string keyString = key.ToString();
        string hashPrefix = keyString + ":";

        bool keyDeleted = StoreItems.TryRemove(keyString, out _);

        List<string> keysToDelete =
            [.. StoreItems.Keys.Where(storageKey => storageKey.StartsWith(hashPrefix, StringComparison.Ordinal))];

        int hashFieldsDeleted = keysToDelete.Count(storageKey => StoreItems.TryRemove(storageKey, out _));

        return keyDeleted || hashFieldsDeleted > 0;
    }
}
