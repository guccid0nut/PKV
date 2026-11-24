// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ZORGATH.WebPortal.API.Validators;
using ZORGATH.WebPortal.API.Features.Registration;

namespace ASPIRE.Tests.ZORGATH.WebPortal.API.Tests;

public sealed class RegistrationControllerTests(ZORGATHTestFixture fixture) : IClassFixture<ZORGATHTestFixture>

{
    [Theory]
    [InlineData("regtest1@kongor.com", "RegUser1", "SecurePassword123!")]
    public async Task RegisterUserAndMainAccount_WithValidData_ReturnsCreatedAndCreatesUserAndAccount(
        string emailAddress, string accountName, string password)

    {
        string uniqueEmail = $"{Guid.NewGuid():N}_{emailAddress}";

        string uniqueAccount = $"{accountName}_{Guid.NewGuid():N}"[..15];


        using IServiceScope scope = fixture.CreateScope();


        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        IEmailService emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();

        PasswordValidator passwordValidator = scope.ServiceProvider.GetRequiredService<PasswordValidator>();


        RegistrationController registrationController = new(new LocalRegistrationMediator(databaseContext, emailService, passwordValidator));


        IActionResult initiateResult =
            await registrationController.InitiateRegistration(new RegisterEmailAddressDTO(uniqueEmail, uniqueEmail));

        Assert.IsType<OkObjectResult>(initiateResult);


        Token? registrationToken = await databaseContext.Tokens.SingleOrDefaultAsync(token =>
            token.EmailAddress.Equals(uniqueEmail) && token.Purpose.Equals(TokenPurpose.EmailAddressVerification));


        Assert.NotNull(registrationToken);


        IActionResult completeResult = await registrationController.CompleteRegistration(
            new RegisterUserWithTokenDTO(registrationToken.Value.ToString(), uniqueAccount, password));


        Assert.IsType<CreatedAtActionResult>(completeResult);


        CreatedAtActionResult createdResult = (CreatedAtActionResult)completeResult;

        GetBasicUserDTO? userDTO = createdResult.Value as GetBasicUserDTO;


        Assert.NotNull(userDTO);

        Assert.Equal(uniqueEmail, userDTO.EmailAddress);

        Assert.Single(userDTO.Accounts);

        Assert.Equal(uniqueAccount, userDTO.Accounts.First().Name);
    }


    [Theory]
    [InlineData("dup1@kongor.com", "DupUser", "SecurePassword123!")]
    public async Task InitiateRegistration_DuplicateEmail_ReturnsConflict(string initialEmail,
        string accountName, string password)

    {
        string uniqueEmail = $"{Guid.NewGuid():N}_{initialEmail}";

        string uniqueAccount = $"{accountName}_{Guid.NewGuid():N}"[..15];


        using IServiceScope scope = fixture.CreateScope();


        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        IEmailService emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();

        PasswordValidator passwordValidator = scope.ServiceProvider.GetRequiredService<PasswordValidator>();


        RegistrationController registrationController = new(new LocalRegistrationMediator(databaseContext, emailService, passwordValidator));


        IActionResult initiateResult1 =
            await registrationController.InitiateRegistration(new RegisterEmailAddressDTO(uniqueEmail, uniqueEmail));

        Assert.IsType<OkObjectResult>(initiateResult1);


        Token? registrationToken = await databaseContext.Tokens.SingleOrDefaultAsync(token =>
            token.EmailAddress.Equals(uniqueEmail) && token.Purpose.Equals(TokenPurpose.EmailAddressVerification));


        Assert.NotNull(registrationToken);


        await registrationController.CompleteRegistration(
            new RegisterUserWithTokenDTO(registrationToken.Value.ToString(), uniqueAccount, password));


        IActionResult initiateResult2 =
            await registrationController.InitiateRegistration(new RegisterEmailAddressDTO(uniqueEmail, uniqueEmail));

        Assert.IsType<ConflictObjectResult>(initiateResult2);
    }


    [Fact]
    public async Task CompleteRegistration_InvalidToken_ReturnsNotFound()

    {
        using IServiceScope scope = fixture.CreateScope();


        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        IEmailService emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();

        PasswordValidator passwordValidator = scope.ServiceProvider.GetRequiredService<PasswordValidator>();


        RegistrationController registrationController = new(new LocalRegistrationMediator(databaseContext, emailService, passwordValidator));


        IActionResult completeResult = await registrationController.CompleteRegistration(
            new RegisterUserWithTokenDTO(Guid.NewGuid().ToString(), "SomeName", "SomePassword!"));


        Assert.IsType<NotFoundObjectResult>(completeResult);
    }


    [Fact]
    public async Task CompleteRegistration_ConsumedToken_ReturnsConflict()

    {
        using IServiceScope scope = fixture.CreateScope();


        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        IEmailService emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();

        PasswordValidator passwordValidator = scope.ServiceProvider.GetRequiredService<PasswordValidator>();


        RegistrationController registrationController = new(new LocalRegistrationMediator(databaseContext, emailService, passwordValidator));


        string emailAddress = $"consumed_{Guid.NewGuid():N}@kongor.com";


        await registrationController.InitiateRegistration(new RegisterEmailAddressDTO(emailAddress, emailAddress));


        Token? registrationToken = await databaseContext.Tokens.SingleOrDefaultAsync(token =>
            token.EmailAddress.Equals(emailAddress) && token.Purpose.Equals(TokenPurpose.EmailAddressVerification));


        Assert.NotNull(registrationToken);


        await registrationController.CompleteRegistration(
            new RegisterUserWithTokenDTO(registrationToken.Value.ToString(), "FirstCons", "Password123!"));


        IActionResult completeResult = await registrationController.CompleteRegistration(
            new RegisterUserWithTokenDTO(registrationToken.Value.ToString(), "SecondCons", "Password123!"));


        Assert.IsType<ConflictObjectResult>(completeResult);
    }


    [Fact]
    public async Task CompleteRegistration_DuplicateAccountName_ReturnsConflict()

    {
        using IServiceScope scope = fixture.CreateScope();


        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        IEmailService emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();

        PasswordValidator passwordValidator = scope.ServiceProvider.GetRequiredService<PasswordValidator>();


        RegistrationController registrationController = new(new LocalRegistrationMediator(databaseContext, emailService, passwordValidator));


        string emailAddress1 = $"dupacc1_{Guid.NewGuid():N}@kongor.com";

        string emailAddress2 = $"dupacc2_{Guid.NewGuid():N}@kongor.com";

        string accName = $"DupAcc_{Guid.NewGuid():N}"[..14];


        await registrationController.InitiateRegistration(new RegisterEmailAddressDTO(emailAddress1, emailAddress1));

        Token? token1 = await databaseContext.Tokens.SingleOrDefaultAsync(token =>
            token.EmailAddress.Equals(emailAddress1) && token.Purpose.Equals(TokenPurpose.EmailAddressVerification));


        Assert.NotNull(token1);


        await registrationController.CompleteRegistration(
            new RegisterUserWithTokenDTO(token1.Value.ToString(), accName, "Password123!"));


        await registrationController.InitiateRegistration(new RegisterEmailAddressDTO(emailAddress2, emailAddress2));

        Token? token2 = await databaseContext.Tokens.SingleOrDefaultAsync(token =>
            token.EmailAddress.Equals(emailAddress2) && token.Purpose.Equals(TokenPurpose.EmailAddressVerification));


        Assert.NotNull(token2);


        IActionResult completeResult = await registrationController.CompleteRegistration(
            new RegisterUserWithTokenDTO(token2.Value.ToString(), accName, "Password123!"));


        Assert.IsType<ConflictObjectResult>(completeResult);
    }


    [Fact]
    public async Task Register_NewUser_ReturnsCreatedAndCreatesUserAndAccount()

    {
        using IServiceScope scope = fixture.CreateScope();


        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        IEmailService emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();

        PasswordValidator passwordValidator = scope.ServiceProvider.GetRequiredService<PasswordValidator>();


        RegistrationController registrationController = new(new LocalRegistrationMediator(databaseContext, emailService, passwordValidator));


        string email = $"direct_new_{Guid.NewGuid():N}@kongor.com";

        string ign = $"DirNew_{Guid.NewGuid():N}"[..14];

        const string password = "Password123!";


        IActionResult result = await registrationController.Register(new RegisterDirectUserDTO(email, ign, password));


        CreatedAtActionResult createdResult = Assert.IsType<CreatedAtActionResult>(result);

        GetBasicUserDTO? userDTO = createdResult.Value as GetBasicUserDTO;


        Assert.NotNull(userDTO);

        Assert.Equal(email, userDTO.EmailAddress);

        Assert.Single(userDTO.Accounts);

        Assert.Equal(ign, userDTO.Accounts.First().Name);
    }


    [Fact]
    public async Task Register_ExistingUserWithoutAccounts_ReturnsCreatedAndCreatesAccount()

    {
        using IServiceScope scope = fixture.CreateScope();


        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        IEmailService emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();

        PasswordValidator passwordValidator = scope.ServiceProvider.GetRequiredService<PasswordValidator>();


        RegistrationController registrationController = new(new LocalRegistrationMediator(databaseContext, emailService, passwordValidator));


        string email = $"direct_existing_{Guid.NewGuid():N}@kongor.com";

        string ign = $"DirExist_{Guid.NewGuid():N}"[..14];

        const string password = "Password123!";


        // Seed a User with no Accounts

        Role? role = await databaseContext.Roles.SingleOrDefaultAsync(r => r.Name.Equals(UserRoles.User));

        if (role == null)

        {
            role = new Role { Name = UserRoles.User };

            await databaseContext.Roles.AddAsync(role);

            await databaseContext.SaveChangesAsync();
        }


        User user = new()

        {
            EmailAddress = email,

            Role = role,

            SRPPasswordSalt = "salt",

            SRPPasswordHash = "hash",

            PBKDF2PasswordHash = "hash"
        };

        await databaseContext.Users.AddAsync(user);

        await databaseContext.SaveChangesAsync();


        IActionResult result = await registrationController.Register(new RegisterDirectUserDTO(email, ign, password));


        CreatedAtActionResult createdResult = Assert.IsType<CreatedAtActionResult>(result);

        GetBasicUserDTO? userDTO = createdResult.Value as GetBasicUserDTO;


        Assert.NotNull(userDTO);

        Assert.Equal(email, userDTO.EmailAddress);

        Assert.Single(userDTO.Accounts);

        Assert.Equal(ign, userDTO.Accounts.First().Name);
    }


    [Fact]
    public async Task Register_ExistingUserWithAccounts_ReturnsConflict()

    {
        using IServiceScope scope = fixture.CreateScope();


        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        IEmailService emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();

        PasswordValidator passwordValidator = scope.ServiceProvider.GetRequiredService<PasswordValidator>();


        RegistrationController registrationController = new(new LocalRegistrationMediator(databaseContext, emailService, passwordValidator));


        string email = $"direct_conflict_{Guid.NewGuid():N}@kongor.com";

        string ign = $"DirConf_{Guid.NewGuid():N}"[..14];

        const string password = "Password123!";


        // Create first registration (creates user and account)

        await registrationController.Register(new RegisterDirectUserDTO(email, ign, password));


        // Attempt duplicate registration

        IActionResult result =
            await registrationController.Register(new RegisterDirectUserDTO(email, "NewIGN", password));


        Assert.IsType<ConflictObjectResult>(result);
    }


    [Fact]
    public async Task Register_DuplicateAccountName_ReturnsConflict()

    {
        using IServiceScope scope = fixture.CreateScope();


        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        IEmailService emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();

        PasswordValidator passwordValidator = scope.ServiceProvider.GetRequiredService<PasswordValidator>();


        RegistrationController registrationController = new(new LocalRegistrationMediator(databaseContext, emailService, passwordValidator));


        string ign = $"DupIGN_{Guid.NewGuid():N}"[..14];

        const string password = "Password123!";


        // First registration

        await registrationController.Register(new RegisterDirectUserDTO($"user1_{Guid.NewGuid():N}@kongor.com", ign,
            password));


        // Second registration with same IGN but different email

        IActionResult result =
            await registrationController.Register(new RegisterDirectUserDTO($"user2_{Guid.NewGuid():N}@kongor.com", ign,
                password));


        Assert.IsType<ConflictObjectResult>(result);
    }
}
