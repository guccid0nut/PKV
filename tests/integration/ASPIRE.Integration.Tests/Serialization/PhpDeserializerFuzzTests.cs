// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.Tests.Serialization;

public class PhpDeserializerFuzzTests
{
    private static readonly string[] ValidSeeds =
    [
        "N;",
        "b:1;",
        "b:0;",
        "i:1337;",
        "i:-42;",
        "d:3.1415926535;",
        "d:-0.001;",
        "s:12:\"Hello, World\";",
        "s:0:\"\";",
        "a:0:{}",
        "a:2:{s:4:\"name\";s:5:\"Alice\";s:3:\"age\";i:30;}",
        "a:3:{i:0;s:5:\"apple\";i:1;s:6:\"banana\";i:2;i:100;}",
        "a:2:{s:4:\"user\";a:2:{s:2:\"id\";i:1;s:4:\"role\";s:5:\"admin\";}s:6:\"active\";b:1;}"
    ];

    [Fact]
    public void FuzzTest_RandomMutationsOnValidSeeds_NeverCrashesWithUnhandledExceptions()
    {
        Random rng = new(1337);
        const int iterations = 10000;

        for (int i = 0; i < iterations; i++)
        {
            string seed = ValidSeeds[rng.Next(ValidSeeds.Length)];
            byte[] mutated = MutateBytes(Encoding.UTF8.GetBytes(seed), rng);

            try
            {
                // Byte span deserialization
                _ = PhpDeserializer.Deserialize((ReadOnlySpan<byte>)mutated);
            }
            catch (FormatException)
            {
                // Acceptable if depth exceeded or formatting issue
            }
            catch (Exception ex)
            {
                Assert.Fail($"Unhandled exception on byte payload iteration {i}: {ex}");
            }

            try
            {
                // Char span deserialization
                string charPayload = Encoding.UTF8.GetString(mutated);
                _ = PhpDeserializer.Deserialize(charPayload);
            }
            catch (FormatException)
            {
                // Acceptable
            }
            catch (Exception ex)
            {
                Assert.Fail($"Unhandled exception on char payload iteration {i}: {ex}");
            }
        }
    }

    [Fact]
    public void FuzzTest_PureRandomBytes_NeverCrashesWithUnhandledExceptions()
    {
        Random rng = new(4242);
        const int iterations = 10000;

        for (int i = 0; i < iterations; i++)
        {
            int length = rng.Next(0, 256);
            byte[] randomBytes = new byte[length];
            rng.NextBytes(randomBytes);

            try
            {
                _ = PhpDeserializer.Deserialize((ReadOnlySpan<byte>)randomBytes);
            }
            catch (FormatException)
            {
                // Acceptable
            }
            catch (Exception ex)
            {
                Assert.Fail($"Unhandled exception on random byte buffer iteration {i} (len {length}): {ex}");
            }

            try
            {
                string randomChars = Encoding.Latin1.GetString(randomBytes);
                _ = PhpDeserializer.Deserialize(randomChars);
            }
            catch (FormatException)
            {
                // Acceptable
            }
            catch (Exception ex)
            {
                Assert.Fail($"Unhandled exception on random char string iteration {i} (len {length}): {ex}");
            }
        }
    }

    private static byte[] MutateBytes(byte[] source, Random rng)
    {
        if (source.Length == 0) return [(byte)rng.Next(256)];

        byte[] copy = (byte[])source.Clone();
        int mutationCount = rng.Next(1, 5);

        for (int m = 0; m < mutationCount; m++)
        {
            int mutationType = rng.Next(4);
            switch (mutationType)
            {
                case 0: // Bit flip / byte replacement
                    int replaceIdx = rng.Next(copy.Length);
                    copy[replaceIdx] = (byte)rng.Next(256);
                    break;

                case 1: // Truncation
                    int truncLen = rng.Next(copy.Length);
                    byte[] truncated = new byte[truncLen];
                    Array.Copy(copy, truncated, truncLen);
                    copy = truncated;
                    if (copy.Length == 0) return copy;
                    break;

                case 2: // Insertion
                    int insertIdx = rng.Next(copy.Length + 1);
                    byte[] inserted = new byte[copy.Length + 1];
                    Array.Copy(copy, 0, inserted, 0, insertIdx);
                    inserted[insertIdx] = (byte)rng.Next(256);
                    Array.Copy(copy, insertIdx, inserted, insertIdx + 1, copy.Length - insertIdx);
                    copy = inserted;
                    break;

                case 3: // Deletion
                    if (copy.Length > 1)
                    {
                        int delIdx = rng.Next(copy.Length);
                        byte[] deleted = new byte[copy.Length - 1];
                        Array.Copy(copy, 0, deleted, 0, delIdx);
                        Array.Copy(copy, delIdx + 1, deleted, delIdx, copy.Length - delIdx - 1);
                        copy = deleted;
                    }

                    break;
            }
        }

        return copy;
    }
}
