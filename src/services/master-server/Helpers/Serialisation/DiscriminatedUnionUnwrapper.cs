// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Collections;

namespace KONGOR.MasterServer.Helpers.Serialisation;

/// <summary>
///     Helper class for unwrapping discriminated union types in PHP serialization.
/// </summary>
internal static class DiscriminatedUnionUnwrapper
{
    public static object? ProcessValue(object? value)
    {
        while (true)
        {
            if (value is null) return null;

            Type type = value.GetType();

            // 1. Check for OneOf directly
            if (IsOneOfType(type))
            {
                value = UnwrapDiscriminatedUnionValue(value);
                continue;
            }

            // 2. Check for Dictionary
            if (value is IDictionary dictionary) return ProcessDictionary(dictionary, type);

            // 3. Check for Enumerable (List, Array) but EXCLUDE string
            return value is IEnumerable enumerable and not string ? ProcessEnumerable(enumerable) : value;
        }
    }

    private static bool IsOneOfType(Type type)
    {
        return type.IsGenericType &&
               type.GetGenericTypeDefinition().FullName?.StartsWith("OneOf.OneOf", StringComparison.Ordinal) == true;
    }

    private static object? UnwrapDiscriminatedUnionValue(object value)
    {
        Type type = value.GetType();

        MethodInfo? matchMethod = type.GetMethods()
            .FirstOrDefault(m => m.Name == "Match" && m.GetParameters().Length == type.GetGenericArguments().Length);

        if (matchMethod is null) return value; // Should not happen for OneOf

        Type[] genericArgs = type.GetGenericArguments();

        MethodInfo concreteMatch = matchMethod.MakeGenericMethod(typeof(object));

        object?[] delegates = new object?[genericArgs.Length];

        MethodInfo identityInfo =
            typeof(DiscriminatedUnionUnwrapper).GetMethod(nameof(Identity),
                BindingFlags.NonPublic | BindingFlags.Static)!;

        for (int i = 0; i < genericArgs.Length; i++)
        {
            Type argType = genericArgs[i];
            MethodInfo typedIdentity = identityInfo.MakeGenericMethod(argType);
            delegates[i] =
                Delegate.CreateDelegate(typeof(Func<,>).MakeGenericType(argType, typeof(object)), typedIdentity);
        }

        return concreteMatch.Invoke(value, delegates);
    }

    private static object Identity<T>(T value)
    {
        return value!;
    }

    private static Dictionary<object, object> ProcessDictionary(IDictionary dictionary, Type originalType)
    {
        Dictionary<object, object> result = [];

        foreach (DictionaryEntry entry in dictionary)
        {
            object? key = ProcessValue(entry.Key);
            object? val = ProcessValue(entry.Value);

            if (key is not null) result[key] = val!;
        }

        return result;
    }

    private static List<object> ProcessEnumerable(IEnumerable enumerable)
    {
        return [.. from object? item in enumerable select ProcessValue(item)];
    }
}
