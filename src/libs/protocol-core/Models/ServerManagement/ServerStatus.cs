// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.Common.Models.ServerManagement;

#pragma warning disable CA1707 // Identifiers should not contain underscores
#pragma warning disable CA1712 // Do not prefix enum values with type name
// ReSharper disable InconsistentNaming
// ReSharper disable UnusedMember.Global
// ReSharper disable IdentifierTypo
/// <summary>
///     This enumeration is part of the Chat Server Protocol, and needs to match its counterpart in order for servers in
///     the distributed cache to be handled correctly.
/// </summary>
public enum ServerStatus
{
    SERVER_STATUS_SLEEPING,
    SERVER_STATUS_IDLE,
    SERVER_STATUS_LOADING,
    SERVER_STATUS_ACTIVE,
    SERVER_STATUS_CRASHED,
    SERVER_STATUS_KILLED,

    SERVER_STATUS_UNKNOWN
}
