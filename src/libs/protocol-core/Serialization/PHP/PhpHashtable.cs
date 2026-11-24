// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections;

namespace ASPIRE.Common.Serialization.PHP;

#pragma warning disable CA1710 // Identifiers should have correct suffix
#pragma warning disable CA1010 // Collections should implement generic interface
// ReSharper disable InconsistentNaming
// ReSharper disable ClassNameWithTooManyLetters
// ReSharper disable RedundantOverriddenMember
// ReSharper disable CA1010
// ReSharper disable CA1710
/// <summary>
///     High-performance hybrid Hashtable implementing both non-generic <see cref="IDictionary" />
///     and generic <see cref="IDictionary{TKey, TValue}" /> for seamless native PHP deserialization parity.
/// </summary>
public class PhpHashtable : Hashtable, IDictionary<object, object?>
{
    public PhpHashtable()
    {
    }

    public PhpHashtable(int capacity) : base(Math.Clamp(capacity, 0, 1024))
    {
    }

    ICollection<object> IDictionary<object, object?>.Keys => [.. Keys.Cast<object>()];

    ICollection<object?> IDictionary<object, object?>.Values => [.. Values.Cast<object?>()];

    public override void Add(object key, object? value)
    {
        base.Add(key, value!);
    }

    public override bool ContainsKey(object key)
    {
        return base.ContainsKey(key);
    }

    bool IDictionary<object, object?>.Remove(object key)
    {
        if (!base.ContainsKey(key)) return false;

        base.Remove(key);
        return true;
    }

    public bool TryGetValue(object key, out object? value)
    {
        if (!base.ContainsKey(key))
        {
            value = null;
            return false;
        }

        value = base[key];
        return true;
    }

    public new object? this[object key]
    {
        get => base[key];
        set => base[key] = value;
    }

    void ICollection<KeyValuePair<object, object?>>.Add(KeyValuePair<object, object?> item)
    {
        base.Add(item.Key, item.Value!);
    }

    bool ICollection<KeyValuePair<object, object?>>.Contains(KeyValuePair<object, object?> item)
    {
        return base.ContainsKey(item.Key) && Equals(base[item.Key], item.Value);
    }

    void ICollection<KeyValuePair<object, object?>>.CopyTo(KeyValuePair<object, object?>[] array, int arrayIndex)
    {
        int i = arrayIndex;
        foreach (DictionaryEntry de in this) array[i++] = new KeyValuePair<object, object?>(de.Key, de.Value);
    }

    bool ICollection<KeyValuePair<object, object?>>.Remove(KeyValuePair<object, object?> item)
    {
        if (!((ICollection<KeyValuePair<object, object?>>)this).Contains(item)) return false;

        base.Remove(item.Key);
        return true;
    }

    bool ICollection<KeyValuePair<object, object?>>.IsReadOnly => false;

    IEnumerator<KeyValuePair<object, object?>> IEnumerable<KeyValuePair<object, object?>>.GetEnumerator()
    {
        foreach (DictionaryEntry de in this) yield return new KeyValuePair<object, object?>(de.Key, de.Value);
    }

    public override void Remove(object key)
    {
        base.Remove(key);
    }
}
