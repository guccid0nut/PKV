// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Domain;

public enum TokenPurpose
{
    EmailAddressVerification = 0,
    EmailAddressUpdate = 1,
    PasswordReset = 2,
    Session = 3,
    EmailVerification = 4
}
