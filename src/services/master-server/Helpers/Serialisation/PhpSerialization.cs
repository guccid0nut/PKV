// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace KONGOR.MasterServer.Helpers.Serialisation;

public static class PhpSerialization
{
    public static string Serialize(object? obj)
    {
        return PhpSerializer.SerializeToString(obj);
    }

    public static object? Deserialize(string str)
    {
        return PhpSerializer.Deserialize(str);
    }
}
