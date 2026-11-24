// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

namespace ASPIRE.Common.DTOs.UI;

// ReSharper disable InconsistentNaming
// ReSharper disable UnusedType.Global
// ReSharper disable UnusedMember.Global
[UnconditionalSuppressMessage("Trimming", "IL2026:MembersAnnotatedWithRequiresUnreferencedCode",
    Justification = "The compared 'Password' property is explicitly defined and preserved on the same ViewModel type.")]
public class RegisterViewModel
{
    [Required]
    [StringLength(15, MinimumLength = 3, ErrorMessage = "IGN must be between 3 and 15 characters.")]
    [RegularExpression("^[a-zA-Z0-9_.]+$",
        ErrorMessage = "IGN can only contain letters, numbers, underscores, and dots.")]
    public required string IGN { get; init; }

    [Required]
    [StringLength(100, MinimumLength = 6, ErrorMessage = "Password must be at least 6 characters long.")]
    [DataType(DataType.Password)]
    public required string Password { get; init; }

    [DataType(DataType.Password)]
    [Display(Name = "Confirm Password")]
    [Compare("Password", ErrorMessage = "The password and confirmation password do not match.")]
    public required string ConfirmPassword { get; init; }

    [Display(Name = "Invite Code")]
    public string? InviteCode { get; init; }
}

[UnconditionalSuppressMessage("Trimming", "IL2026:MembersAnnotatedWithRequiresUnreferencedCode",
    Justification = "The compared 'Password' property is explicitly defined and preserved on the same ViewModel type.")]
public class RegisterDirectViewModel
{
    [Required]
    [EmailAddress]
    [Display(Name = "Email Address")]
    public required string Email { get; init; }

    [Required]
    [StringLength(15, MinimumLength = 3, ErrorMessage = "IGN must be between 3 and 15 characters.")]
    [RegularExpression("^[a-zA-Z0-9_.]+$",
        ErrorMessage = "IGN can only contain letters, numbers, underscores, and dots.")]
    public required string IGN { get; init; }

    [Required]
    [StringLength(100, MinimumLength = 6, ErrorMessage = "Password must be at least 6 characters long.")]
    [DataType(DataType.Password)]
    public required string Password { get; init; }

    [DataType(DataType.Password)]
    [Display(Name = "Confirm Password")]
    [Compare("Password", ErrorMessage = "The password and confirmation password do not match.")]
    public required string ConfirmPassword { get; init; }
}

public class RegisterEmailViewModel
{
    [Required]
    [EmailAddress]
    [Display(Name = "Email Address")]
    public required string Email { get; init; }
}

[UnconditionalSuppressMessage("Trimming", "IL2026:MembersAnnotatedWithRequiresUnreferencedCode",
    Justification = "The compared 'Password' property is explicitly defined and preserved on the same ViewModel type.")]
public class RegisterCompleteViewModel
{
    [Required] public required string Token { get; init; }

    [Required]
    [StringLength(15, MinimumLength = 3, ErrorMessage = "IGN must be between 3 and 15 characters.")]
    [RegularExpression("^[a-zA-Z0-9_.]+$",
        ErrorMessage = "IGN can only contain letters, numbers, underscores, and dots.")]
    public required string IGN { get; init; }

    [Required]
    [StringLength(100, MinimumLength = 6, ErrorMessage = "Password must be at least 6 characters long.")]
    [DataType(DataType.Password)]
    public required string Password { get; init; }

    [DataType(DataType.Password)]
    [Display(Name = "Confirm Password")]
    [Compare("Password", ErrorMessage = "The password and confirmation password do not match.")]
    public required string ConfirmPassword { get; init; }
}
