// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.Common.DTOs;

// ReSharper disable InconsistentNaming
// ReSharper disable PartialTypeWithSinglePart
public partial record RegisterUserAndMainAccountDTO(string Token, string Name, string Password, string ConfirmPassword);

public partial record LogInUserDTO(string Name, string Password);

public partial record GetAuthenticationTokenDTO(int UserID, string TokenType, string Token);
