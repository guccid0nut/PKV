// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Security;
using FluentValidation.Results;
using MediatR;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Mappings;
using MERRICK.DatabaseContext.Queries;
using MERRICK.DatabaseContext.Repositories;
using MERRICK.DatabaseContext.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using NEXUS.Contracts.CQRS.Registration;
using ZORGATH.WebPortal.API.Handlers;
using ZORGATH.WebPortal.API.Validators;

namespace ZORGATH.WebPortal.API.Features.Registration;

public sealed class RegisterDirectUserHandler(
    MerrickContext databaseContext,
    PasswordValidator passwordValidator,
    ILogger<RegisterDirectUserHandler> logger,
    IInviteRepository? inviteRepository = null,
    TimeProvider? timeProvider = null,
    Argon2idHasher? argon2Hasher = null,
    IConfiguration? configuration = null)
    : IRequestHandler<RegisterDirectUserCommand, RegistrationResult>
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Argon2idHasher _argon2Hasher = argon2Hasher ?? new();

    public async Task<RegistrationResult> Handle(
        RegisterDirectUserCommand request,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Processing direct user registration for {Name}, DiscordId: {DiscordId}",
            request.Name, request.DiscordId);

        ValidationResult validationResult = await passwordValidator.ValidateAsync(request.Password, cancellationToken);
        if (!validationResult.IsValid)
        {
            string err = string.Join(", ", validationResult.Errors.Select(e => e.ErrorMessage));
            logger.LogWarning("Password complexity failed for {Name}: {Error}", request.Name, err);
            return RegistrationResult.BadRequest(err);
        }

        if (!PerimeterSecuritySanitizer.IsValidNickname(request.Name))
        {
            logger.LogWarning("Invalid nickname format for {Name}", request.Name);
            return RegistrationResult.BadRequest("Account nickname must be between 3 and 15 alphanumeric characters or underscores.");
        }

        if (await CompiledQueries.AccountExistsByNameQuery(databaseContext, request.Name))
        {
            logger.LogWarning("Direct registration conflict: Account {Name} already exists", request.Name);
            return RegistrationResult.Conflict($@"Account With Name ""{request.Name}"" Already Exists");
        }

        bool isInviteOnly = inviteRepository != null && await inviteRepository.IsInviteOnlyModeEnabledAsync(cancellationToken);
        UserInvite? validInvite = null;

        if (isInviteOnly && string.IsNullOrWhiteSpace(request.InviteCode))
        {
            logger.LogWarning("Direct registration rejected: Server is invite-only and no invite code was supplied");
            return RegistrationResult.BadRequest("Registration is currently invite-only. A valid invite code is required to create an account.");
        }

        if (!string.IsNullOrWhiteSpace(request.InviteCode) && inviteRepository != null)
        {
            var (isValid, message, inv) = await inviteRepository.ValidateInviteCodeAsync(request.InviteCode, cancellationToken);
            DateTimeOffset nowUtc = _timeProvider.GetUtcNow();
            if (isValid && inv?.TimestampExpires.HasValue == true && inv.TimestampExpires.Value <= nowUtc)
            {
                isValid = false;
                message = "This invite code has expired.";
            }

            if (!isValid || inv == null)
            {
                logger.LogWarning("Direct registration rejected: Invalid invite code '{Code}': {Reason}", request.InviteCode, message);
                return RegistrationResult.BadRequest(message ?? "Invalid or expired invite code.");
            }

            validInvite = inv;
        }

        Role? role = await databaseContext.Roles.SingleOrDefaultAsync(r =>
            r.Name == UserRoles.User, cancellationToken);

        if (role is null)
        {
            logger.LogError("Required system role '{Role}' was not found in database", UserRoles.User);
            return RegistrationResult.NotFound($@"User Role ""{UserRoles.User}"" Was Not Found");
        }

        // Standardized Discord Fallback Email Synthesis
        string targetEmail = !string.IsNullOrWhiteSpace(request.Email)
            ? request.Email
            : $"d_{request.DiscordId}@discord.projectkongor.local";

        User? user = await CompiledQueries.GetUserByEmailWithAccountsTrackingQuery(databaseContext, targetEmail);

        string salt = SRPRegistrationHandlers.GenerateSRPPasswordSalt();
        string srpHash = SRPRegistrationHandlers.ComputeSRPPasswordHash(request.Password, salt);

        if (user is null)
        {
            user = new User
            {
                EmailAddress = targetEmail,
                DiscordId = request.DiscordId,
                DiscordUsername = request.DiscordUsername,
                DiscordAvatar = request.DiscordAvatar,
                Role = role,
                SRPPasswordSalt = salt,
                SRPPasswordHash = srpHash,
                InvitedByUserID = validInvite?.CreatedByUserID
            };

            if (configuration?.GetValue<bool>("KONGOR_STANDALONE") == true)
            {
                int starterGold = configuration.GetValue("KONGOR_STANDALONE_STARTER_GOLD", 10000);
                int starterSilver = configuration.GetValue("KONGOR_STANDALONE_STARTER_SILVER", 25000);
                int starterTickets = configuration.GetValue("KONGOR_STANDALONE_STARTER_TICKETS", 25);

                user.GoldCoins = starterGold;
                user.SilverCoins = starterSilver;
                user.PlinkoTickets = starterTickets;

                logger.LogInformation("[Standalone] granted starter balance gold={Gold} silver={Silver} tickets={Tickets} to {Ign}",
                    starterGold, starterSilver, starterTickets, request.Name);
            }

            user.PBKDF2PasswordHash = new PasswordHasher<User>().HashPassword(user, request.Password);
            user.Argon2idPasswordHash = await _argon2Hasher.HashPasswordAsync(request.Password, cancellationToken);

            await databaseContext.Users.AddAsync(user, cancellationToken);
        }
        else
        {
            if (!string.IsNullOrEmpty(request.DiscordId)) user.DiscordId = request.DiscordId;
            if (!string.IsNullOrEmpty(request.DiscordUsername)) user.DiscordUsername = request.DiscordUsername;
            if (!string.IsNullOrEmpty(request.DiscordAvatar)) user.DiscordAvatar = request.DiscordAvatar;

            if (user.Accounts.Count != 0)
            {
                logger.LogWarning("Registration conflict: User with email {Email} already exists with registered accounts", targetEmail);
                return RegistrationResult.Conflict($@"User With Email ""{targetEmail}"" Already Exists");
            }

            if (validInvite != null && !user.InvitedByUserID.HasValue)
            {
                user.InvitedByUserID = validInvite.CreatedByUserID;
            }

            user.SRPPasswordSalt = salt;
            user.SRPPasswordHash = srpHash;
            user.PBKDF2PasswordHash = new PasswordHasher<User>().HashPassword(user, request.Password);
            user.Argon2idPasswordHash = await _argon2Hasher.HashPasswordAsync(request.Password, cancellationToken);
        }

        if (!string.IsNullOrEmpty(request.DiscordId) || !string.IsNullOrEmpty(request.DiscordAvatar))
        {
            if (!user.InventoryItems.Any(i => i.ItemCode == "ai.custom_icon:1"))
            {
                user.InventoryItems.Add(new UserInventory
                {
                    User = user,
                    ItemCode = "ai.custom_icon:1",
                    Quantity = 1,
                    IsPermanent = true,
                    TimestampAcquired = _timeProvider.GetUtcNow(),
                    Source = "Registration"
                });
            }
        }

        Account account = new()
        {
            Name = request.Name,
            User = user,
            IsMain = true,
            SelectedStoreItems =
                !string.IsNullOrEmpty(request.DiscordId) || !string.IsNullOrEmpty(request.DiscordAvatar)
                    ? ["ai.custom_icon:1"]
                    : []
        };
        user.Accounts.Add(account);

        await databaseContext.SaveChangesAsync(cancellationToken);

        if (validInvite != null)
        {
            if (validInvite.IsMultiUse)
            {
                logger.LogInformation(
                    "[RegisterDirectUser] Redeemed shared multi-use invite code '{Code}' (expires {ExpiresUtc:O}) for account {Name} (User #{UserId}) from IP {RemoteIp}",
                    validInvite.Code, validInvite.TimestampExpires, account.Name, user.ID, request.RemoteIpAddress ?? "unknown");
            }
            else
            {
                validInvite.RedeemedByUserID = user.ID;
                validInvite.RedeemedByAccountID = account.ID;
                validInvite.TimestampRedeemed = _timeProvider.GetUtcNow();
                validInvite.RedeemerIpAddress = request.RemoteIpAddress;
                await databaseContext.SaveChangesAsync(cancellationToken);
            }
        }

        logger.LogInformation("Direct registration successful for account {Name} (User #{UserId}) with email {Email}",
            account.Name, user.ID, user.EmailAddress);

        return RegistrationResult.Created(user.ID, user.ToBasicDTO(false));
    }
}
