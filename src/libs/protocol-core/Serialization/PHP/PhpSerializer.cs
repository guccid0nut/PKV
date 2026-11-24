// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using OneOf;

namespace ASPIRE.Common.Serialization.PHP;

// ReSharper disable UnusedMember.Global
// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedAutoPropertyAccessor.Local
// ReSharper disable ForCanBeConvertedToForeach
// ReSharper disable ReplaceIfWithSwitchStatement
// ReSharper disable PossibleMultipleEnumeration
// ReSharper disable TailRecursiveCall
// ReSharper disable MergeSequentialChecks
// ReSharper disable UseNullPropagation
// ReSharper disable RedundantTypeArgumentsOfMethod
// ReSharper disable MergeIntoPattern
// ReSharper disable UsePatternMatching
// ReSharper disable RedundantTypeDeclaration
// ReSharper disable RedundantExplicitArrayCreation
// ReSharper disable ConvertIfToOrExpression
// ReSharper disable ConvertToConstant.Local
// ReSharper disable RedundantElseClause
// ReSharper disable UseObjectOrCollectionInitializer
// ReSharper disable UseCollectionExpression
// ReSharper disable ConvertIfStatementToReturnStatement
// ReSharper disable ExpressionIsAlwaysNull
// ReSharper disable UseLocalFunction
// ReSharper disable InvertIf
// ReSharper disable SuggestBaseTypeForParameter
/// <summary>
///     High-performance zero-allocation PHP wire protocol serializer.
/// </summary>
[UnconditionalSuppressMessage("Trimming", "IL2070:TargetWithPublicProperties",
    Justification = "Dynamic reflection fallback for non-IPhpSerializable types.")]
[UnconditionalSuppressMessage("Trimming", "IL2075:TargetWithPublicProperties",
    Justification = "Dynamic reflection fallback for non-IPhpSerializable types.")]
[UnconditionalSuppressMessage("Trimming", "IL2026:MembersAnnotatedWithRequiresUnreferencedCode",
    Justification = "Dynamic reflection fallback for non-IPhpSerializable types.")]
[UnconditionalSuppressMessage("AOT", "IL3050:MembersAnnotatedWithRequiresDynamicCode",
    Justification = "Dynamic expression delegate compilation fallback for non-IPhpSerializable types.")]
public static class PhpSerializer
{
    private static readonly ConcurrentDictionary<Type, FastPropertyMeta[]> PropertyCache = new();

    /// <summary>
    ///     Serializes an object directly into an <see cref="IBufferWriter{Byte}" /> using UTF-8 PHP wire protocol formatting.
    /// </summary>
    public static void Serialize<T>(IBufferWriter<byte> bufferWriter, T? value)
    {
        Utf8PhpWriter writer = new(bufferWriter);
        if (value is IPhpSerializable phpSerializable)
            phpSerializable.SerializePhp(ref writer);
        else
            SerializeValue(ref writer, value);
        writer.Flush();
    }


    /// <summary>
    ///     Serializes an object to a UTF-8 byte array using pooled buffer memory.
    /// </summary>
    public static byte[] SerializeToUtf8Bytes<T>(T? value)
    {
        using PooledArrayBufferWriter bufferWriter = new();
        Serialize(bufferWriter, value);
        return bufferWriter.WrittenSpan.ToArray();
    }

    /// <summary>
    ///     Serializes an object to a PHP wire format string using pooled buffer memory.
    /// </summary>
    public static string SerializeToString<T>(T? value)
    {
        using PooledArrayBufferWriter bufferWriter = new();
        Serialize(bufferWriter, value);
        return Encoding.UTF8.GetString(bufferWriter.WrittenSpan);
    }

    public static string Serialize(object? value)
    {
        return SerializeToString(value);
    }

    public static object? Deserialize(string str)
    {
        return PhpDeserializer.Deserialize(str);
    }

    public static void SerializeValue(ref Utf8PhpWriter writer, object? value, int depth = 0)
    {
        if (value is null || depth > 32)
        {
            writer.WriteNull();
            return;
        }

        // 0. High-Performance Compile-Time Generated Interface Dispatch
        if (value is IPhpSerializable phpSerializable)
        {
            phpSerializable.SerializePhp(ref writer);
            return;
        }

        Type type = value.GetType();

        // 1. Primitives & Known Types
        switch (value)
        {
            case string strVal:
                writer.WriteString(strVal);
                return;
            case int intVal:
                writer.WriteInt(intVal);
                return;
            case long longVal:
                writer.WriteLong(longVal);
                return;
            case bool boolVal:
                writer.WriteBool(boolVal);
                return;
            case double doubleVal:
                writer.WriteDouble(doubleVal);
                return;
            case float floatVal:
                writer.WriteDouble(floatVal);
                return;
        }

        // 2. OneOf Discriminated Union Unwrapping
        if (value is IOneOf oneOf)
        {
            SerializeValue(ref writer, oneOf.Value, depth);
            return;
        }

        switch (value)
        {
            // 3. IDictionary / Hashtable
            case IDictionary dictionary:
            {
                writer.WriteArrayHeader(dictionary.Count);
                foreach (DictionaryEntry entry in dictionary)
                {
                    SerializeKey(ref writer, entry.Key);
                    SerializeValue(ref writer, entry.Value, depth + 1);
                }

                writer.WriteArrayFooter();
                return;
            }
            // 4. IEnumerable (List, Array, etc.) excluding string
            case IEnumerable enumerable:
            {
                ICollection? collection = value as ICollection;
                int count = collection?.Count ?? GetEnumerableCount(enumerable);
                writer.WriteArrayHeader(count);
                int index = 0;
                foreach (object? item in enumerable)
                {
                    writer.WriteKey(index++);
                    SerializeValue(ref writer, item, depth + 1);
                }

                writer.WriteArrayFooter();
                return;
            }
            default:
                // 5. Complex Object Serialization via Property Attributes
                SerializeObjectProperties(ref writer, value, type, depth + 1);
                break;
        }
    }

    private static void SerializeKey(ref Utf8PhpWriter writer, object? key)
    {
        switch (key)
        {
            case null:
                writer.WriteNull();
                return;
            case int intKey:
                writer.WriteKey(intKey);
                return;
            case long longKey:
                writer.WriteKey((int)longKey);
                return;
            default:
            {
                string keyStr = key.ToString() ?? string.Empty;

                if ((int.TryParse(keyStr, out _) && !keyStr.StartsWith('0')) || keyStr == "0")
                    writer.WriteKey(keyStr);
                else
                    writer.WriteKey(keyStr);

                return;
            }
        }
    }

    private static void SerializeObjectProperties(ref Utf8PhpWriter writer, object instance, Type type, int depth = 0)
    {
        FastPropertyMeta[] properties = PropertyCache.GetOrAdd(type, BuildPropertyMeta);

        writer.WriteArrayHeader(properties.Length);

        for (int i = 0; i < properties.Length; i++)
        {
            FastPropertyMeta prop = properties[i];

            if (prop.IsIntKey)
                writer.WriteKey(prop.IntKey);
            else
                writer.WriteKey(prop.StringKey);

            object? val = prop.GetValue(instance);

            if (prop.IsDiscriminatedUnion && val is not null) val = UnwrapOneOf(val, val.GetType());

            SerializeValue(ref writer, val, depth + 1);
        }

        writer.WriteArrayFooter();
    }

    private static FastPropertyMeta[] BuildPropertyMeta(Type type)
    {
        PropertyInfo[] props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        List<FastPropertyMeta> list = [];

        for (int i = 0; i < props.Length; i++)
        {
            PropertyInfo prop = props[i];
            if (!prop.CanRead) continue;

            // Check if property is ignored
            bool isIgnored = prop.GetCustomAttributes().Any(a =>
                                 a.GetType().Name is "PhpIgnoreAttribute" or
                                     "PHPPropertyIgnoreAttribute" or
                                     "JsonIgnoreAttribute") ||
                             prop.CustomAttributes.Any(a =>
                                 a.AttributeType.Name is "PhpIgnoreAttribute" or "PHPPropertyIgnoreAttribute"
                                     or "JsonIgnoreAttribute");
            if (isIgnored) continue;

            (bool isIntKey, int intKey, string stringKey, bool isDiscriminatedUnion) =
                GetPropertyKeyFromAttribute(prop);

            MethodInfo? getter = prop.GetGetMethod();
            if (getter is null) continue;

            Func<object, object?> getterDelegate = CreateCompiledGetter(prop, getter);

            list.Add(new FastPropertyMeta
            {
                Name = prop.Name,
                IsIntKey = isIntKey,
                IntKey = intKey,
                StringKey = stringKey,
                IsDiscriminatedUnion = isDiscriminatedUnion,
                GetValue = getterDelegate
            });
        }

        return [.. list];
    }

    private static Func<object, object?> CreateCompiledGetter(PropertyInfo prop, MethodInfo getter)
    {
        try
        {
            Type declaringType = prop.DeclaringType ?? getter.DeclaringType ?? typeof(object);
            ParameterExpression instanceParam = Expression.Parameter(typeof(object), "instance");
            UnaryExpression castInstance = Expression.Convert(instanceParam, declaringType);
            MemberExpression propertyAccess = Expression.Property(castInstance, prop);
            UnaryExpression castResult = Expression.Convert(propertyAccess, typeof(object));
            return Expression.Lambda<Func<object, object?>>(castResult, instanceParam).Compile();
        }
        catch
        {
            // Fallback for edge cases where expression trees cannot bind private/virtual accessors
            return instance => getter.Invoke(instance, null);
        }
    }

    private static (bool isIntKey, int intKey, string stringKey, bool isDiscriminatedUnion) GetPropertyKeyFromAttribute(
        PropertyInfo prop)
    {
        bool isIntKey = false;
        int intKey = 0;
        string stringKey = prop.Name;
        bool isDiscriminatedUnion = false;

        // 1. Try CustomAttributes metadata first (direct binary inspection, fast & reliable across assembly boundaries)
        CustomAttributeData? attrData = prop.CustomAttributes.FirstOrDefault(a =>
            a.AttributeType.Name.Equals("PHPPropertyAttribute", StringComparison.OrdinalIgnoreCase) ||
            a.AttributeType.Name.Equals("PhpPropertyAttribute", StringComparison.OrdinalIgnoreCase));

        if (attrData is not null)
        {
            if (attrData.ConstructorArguments.Count > 0)
            {
                CustomAttributeTypedArgument arg = attrData.ConstructorArguments[0];
                switch (arg.Value)
                {
                    case int ik:
                        isIntKey = true;
                        intKey = ik;
                        break;
                    case string sk when !string.IsNullOrEmpty(sk):
                        if (int.TryParse(sk.AsSpan(), out int parsedSk))
                        {
                            isIntKey = true;
                            intKey = parsedSk;
                        }
                        else
                        {
                            isIntKey = false;
                            stringKey = sk;
                        }

                        break;
                    default:
                    {
                        if (arg.Value is not null)
                        {
                            string argStr = arg.Value.ToString() ?? string.Empty;
                            if (!string.IsNullOrEmpty(argStr))
                            {
                                if (int.TryParse(argStr.AsSpan(), out int parsedInt))
                                {
                                    isIntKey = true;
                                    intKey = parsedInt;
                                }
                                else
                                {
                                    isIntKey = false;
                                    stringKey = argStr;
                                }
                            }
                        }

                        break;
                    }
                }
            }

            foreach (CustomAttributeNamedArgument namedArg in attrData.NamedArguments)
                switch (namedArg.MemberName)
                {
                    case "IsDiscriminatedUnion" when namedArg.TypedValue.Value is bool du:
                        isDiscriminatedUnion = du;
                        break;
                    case "IsIntKey" when namedArg.TypedValue.Value is bool ik:
                        isIntKey = ik;
                        break;
                    case "Index" when namedArg.TypedValue.Value is int idx:
                        intKey = idx;
                        break;
                    case "KeyName" when namedArg.TypedValue.Value is string kn && !string.IsNullOrEmpty(kn):
                        stringKey = kn;
                        break;
                }

            return (isIntKey, intKey, stringKey, isDiscriminatedUnion);
        }

        // 2. Fallback to instantiated attributes via GetCustomAttribute()
        PHPPropertyAttribute? attr = prop.GetCustomAttribute<PHPPropertyAttribute>();
        if (attr is not null)
        {
            isDiscriminatedUnion = attr.IsDiscriminatedUnion;

            if (attr.IsIntKey)
                return (true, attr.Index, stringKey, isDiscriminatedUnion);

            if (!string.IsNullOrEmpty(attr.KeyName))
                return (false, 0, attr.KeyName, isDiscriminatedUnion);

            if (attr.PropertyKey.IsT0)
                return (true, attr.PropertyKey.AsT0, stringKey, isDiscriminatedUnion);

            if (attr.PropertyKey.IsT1)
            {
                string sk = attr.PropertyKey.AsT1;
                if (int.TryParse(sk.AsSpan(), out int parsedInt))
                    return (true, parsedInt, stringKey, isDiscriminatedUnion);

                return (false, 0, sk, isDiscriminatedUnion);
            }

            return (attr.IsIntKey, attr.Index, stringKey, isDiscriminatedUnion);
        }

        return (false, 0, stringKey, false);
    }

    private static object? UnwrapOneOf(object value, Type type)
    {
        if (value is IOneOf oneOf)
            return oneOf.Value;

        return value;
    }

    private static int GetEnumerableCount(IEnumerable enumerable)
    {
        int count = 0;
        IEnumerator enumerator = enumerable.GetEnumerator();
        try
        {
            while (enumerator.MoveNext()) count++;
        }
        finally
        {
            if (enumerator is IDisposable disposable) disposable.Dispose();
        }

        return count;
    }

    private sealed class FastPropertyMeta
    {
        public required string Name { get; init; }
        public required bool IsIntKey { get; init; }
        public required int IntKey { get; init; }
        public required string StringKey { get; init; }
        public required bool IsDiscriminatedUnion { get; init; }
        public required Func<object, object?> GetValue { get; init; }
    }
}
