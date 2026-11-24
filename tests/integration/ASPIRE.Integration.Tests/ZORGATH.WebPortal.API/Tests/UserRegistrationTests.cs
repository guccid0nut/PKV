// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using ZORGATH.WebPortal.API.Services;
using ZORGATH.WebPortal.API.Features.Email;

namespace ASPIRE.Tests.ZORGATH.WebPortal.API.Tests;

/// <summary>
///     Tests For User Registration Functionality
/// </summary>
public sealed class UserRegistrationTests(ZORGATHTestFixture fixture) : IClassFixture<ZORGATHTestFixture>

{
    [Theory]
    [InlineData("test@kongor.com", "TestPlayer", "SecurePassword123!")]
    [InlineData("user@kongor.net", "GameUser", "MyP@ssw0rd!")]
    public async Task RegisterUserAndMainAccount_WithValidData_ReturnsCreatedAndCreatesUserAndAccount(
        string emailAddress, string accountName, string password)

    {
        string uniqueEmail = $"{Guid.NewGuid():N}_{emailAddress}";

        string uniqueAccount = $"{accountName}_{Guid.NewGuid():N}"[..15];


        using IServiceScope scope = fixture.CreateScope();


        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();


        ILogger<EmailAddressController> emailLogger =
            scope.ServiceProvider.GetRequiredService<ILogger<EmailAddressController>>();

        IEmailService emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();

        IWebHostEnvironment hostEnvironment = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();


        EmailAddressController emailController = new(new LocalEmailMediator(databaseContext, emailService, hostEnvironment), emailLogger);


        await emailController.RegisterEmailAddress(new RegisterEmailAddressDTO(uniqueEmail, uniqueEmail));


        Token? registrationToken = await databaseContext.Tokens.SingleOrDefaultAsync(token =>
            token.EmailAddress.Equals(uniqueEmail) && token.Purpose.Equals(TokenPurpose.EmailAddressVerification));


        Assert.NotNull(registrationToken);


        ILogger<UserController> userLogger =
            scope.ServiceProvider.GetRequiredService<ILogger<UserController>>();

        IUserService userService = scope.ServiceProvider.GetRequiredService<IUserService>();

        IAuthenticationService authService = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();


        UserController userController =
            new(new AccountRepository(databaseContext), userLogger, userService, authService);


        IActionResult response = await userController.RegisterUserAndMainAccount(
            new RegisterUserAndMainAccountDTO(registrationToken.Value.ToString(), uniqueAccount, password, password));


        Assert.IsType<CreatedAtActionResult>(response);


        CreatedAtActionResult createdResult = (CreatedAtActionResult)response;

        GetBasicUserDTO? userDTO = createdResult.Value as GetBasicUserDTO;


        Assert.NotNull(userDTO);

        Assert.Equal(uniqueEmail, userDTO.EmailAddress);

        Assert.Single(userDTO.Accounts);

        Assert.Equal(uniqueAccount, userDTO.Accounts.First().Name);


        User? user = await databaseContext.Users
            .Include(user => user.Accounts)
            .SingleOrDefaultAsync(user => user.EmailAddress.Equals(uniqueEmail));


        Assert.NotNull(user);

        Assert.Single(user.Accounts);

        Assert.Equal(uniqueAccount, user.Accounts.First().Name);

        Assert.True(user.Accounts.First().IsMain);


        Token? consumedToken = await databaseContext.Tokens.FindAsync(registrationToken.ID);

        Assert.NotNull(consumedToken);

        Assert.NotNull(consumedToken.TimestampConsumed);
    }


    [Theory]
    [InlineData("mismatch@kongor.com", "MismatchUser", "Password123!", "DifferentPass123!")]
    [InlineData("test@kongor.net", "TestUser", "MyP@ss!", "WrongP@ss!")]
    public async Task RegisterUserAndMainAccount_WithMismatchedPasswords_ReturnsBadRequest(string emailAddress,
        string accountName, string password, string confirmPassword)

    {
        string uniqueEmail = $"{Guid.NewGuid():N}_{emailAddress}";

        string uniqueAccount = $"{accountName}_{Guid.NewGuid():N}"[..15];


        using IServiceScope scope = fixture.CreateScope();


        ILogger<EmailAddressController> emailLogger =
            scope.ServiceProvider.GetRequiredService<ILogger<EmailAddressController>>();

        IEmailService emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();

        IWebHostEnvironment emailHostEnvironment =
            scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();


        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();


        EmailAddressController emailController = new(new LocalEmailMediator(databaseContext, emailService, emailHostEnvironment), emailLogger);


        await emailController.RegisterEmailAddress(new RegisterEmailAddressDTO(uniqueEmail, uniqueEmail));


        Token? registrationToken = await databaseContext.Tokens.SingleOrDefaultAsync(token =>
            token.EmailAddress.Equals(uniqueEmail) && token.Purpose.Equals(TokenPurpose.EmailAddressVerification));


        Assert.NotNull(registrationToken);


        ILogger<UserController> userLogger =
            scope.ServiceProvider.GetRequiredService<ILogger<UserController>>();

        IUserService userService = scope.ServiceProvider.GetRequiredService<IUserService>();

        IAuthenticationService authService = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();


        UserController userController =
            new(new AccountRepository(databaseContext), userLogger, userService, authService);


        IActionResult response = await userController.RegisterUserAndMainAccount(
            new RegisterUserAndMainAccountDTO(registrationToken.Value.ToString(), uniqueAccount, password,
                confirmPassword));


        Assert.IsType<BadRequestObjectResult>(response);
    }


    [Theory]
    [InlineData("InvalidUser", "SecurePassword123!")]
    [InlineData("TestPlayer", "MyP@ssw0rd!")]
    public async Task RegisterUserAndMainAccount_WithInvalidToken_ReturnsNotFound(string accountName, string password)

    {
        string invalidToken = Guid.CreateVersion7().ToString();


        using IServiceScope scope = fixture.CreateScope();


        ILogger<UserController> userLogger =
            scope.ServiceProvider.GetRequiredService<ILogger<UserController>>();

        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        IUserService userService = scope.ServiceProvider.GetRequiredService<IUserService>();

        IAuthenticationService authService = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();


        UserController userController =
            new(new AccountRepository(databaseContext), userLogger, userService, authService);


        IActionResult response = await userController.RegisterUserAndMainAccount(
            new RegisterUserAndMainAccountDTO(invalidToken, accountName, password, password));


        Assert.IsType<NotFoundObjectResult>(response);
    }


    [Theory]
    [InlineData("user1@kongor.com", "user2@kongor.com", "DuplicateName", "SecurePassword123!")]
    [InlineData("first@kongor.net", "second@kongor.net", "SameName", "MyP@ssw0rd!")]
    public async Task RegisterUserAndMainAccount_WithDuplicateAccountName_ReturnsConflict(string emailAddressOne,
        string emailAddressTwo, string accountName, string password)

    {
        string uniqueEmailOne = $"{Guid.NewGuid():N}_{emailAddressOne}";

        string uniqueEmailTwo = $"{Guid.NewGuid():N}_{emailAddressTwo}";

        string uniqueAccount = $"{accountName}_{Guid.NewGuid():N}"[..15];


        JWTAuthenticationService jwtAuthenticationService = new(fixture.Factory);


        await jwtAuthenticationService.CreateAuthenticatedUser(uniqueEmailOne, uniqueAccount, password);


        using IServiceScope scope = fixture.CreateScope();


        ILogger<EmailAddressController> emailLogger =
            scope.ServiceProvider.GetRequiredService<ILogger<EmailAddressController>>();

        IEmailService emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();

        IWebHostEnvironment emailHostEnvironment =
            scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();


        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();


        EmailAddressController emailController = new(new LocalEmailMediator(databaseContext, emailService, emailHostEnvironment), emailLogger);


        await emailController.RegisterEmailAddress(new RegisterEmailAddressDTO(uniqueEmailTwo, uniqueEmailTwo));


        Token? registrationToken = await databaseContext.Tokens.SingleOrDefaultAsync(token =>
            token.EmailAddress.Equals(uniqueEmailTwo) && token.Purpose.Equals(TokenPurpose.EmailAddressVerification));


        Assert.NotNull(registrationToken);


        ILogger<UserController> userLogger =
            scope.ServiceProvider.GetRequiredService<ILogger<UserController>>();

        IUserService userService = scope.ServiceProvider.GetRequiredService<IUserService>();

        IAuthenticationService authService = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();


        UserController userController =
            new(new AccountRepository(databaseContext), userLogger, userService, authService);


        IActionResult response = await userController.RegisterUserAndMainAccount(
            new RegisterUserAndMainAccountDTO(registrationToken.Value.ToString(), uniqueAccount, password, password));


        Assert.IsType<ConflictObjectResult>(response);
    }


    [Theory]
    [InlineData("token.reuse@kongor.com", "TokenUser", "SecurePassword123!")]
    [InlineData("token.consumed@kongor.net", "ConsumedUser", "MyP@ssw0rd!")]
    public async Task RegisterUserAndMainAccount_WithConsumedToken_ReturnsConflict(string emailAddress,
        string accountName, string password)

    {
        string uniqueEmail = $"{Guid.NewGuid():N}_{emailAddress}";

        string uniqueAccount = $"{accountName}_{Guid.NewGuid():N}"[..15];


        using IServiceScope scope = fixture.CreateScope();


        ILogger<EmailAddressController> emailLogger =
            scope.ServiceProvider.GetRequiredService<ILogger<EmailAddressController>>();

        IEmailService emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();

        IWebHostEnvironment emailHostEnvironment =
            scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();


        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();


        EmailAddressController emailController = new(new LocalEmailMediator(databaseContext, emailService, emailHostEnvironment), emailLogger);


        await emailController.RegisterEmailAddress(new RegisterEmailAddressDTO(uniqueEmail, uniqueEmail));


        Token? registrationToken = await databaseContext.Tokens.SingleOrDefaultAsync(token =>
            token.EmailAddress.Equals(uniqueEmail) && token.Purpose.Equals(TokenPurpose.EmailAddressVerification));


        Assert.NotNull(registrationToken);


        string tokenValue = registrationToken.Value.ToString();


        ILogger<UserController> userLogger =
            scope.ServiceProvider.GetRequiredService<ILogger<UserController>>();

        IUserService userService = scope.ServiceProvider.GetRequiredService<IUserService>();

        IAuthenticationService authService = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();


        UserController userController =
            new(new AccountRepository(databaseContext), userLogger, userService, authService);


        // First Registration Should Succeed And Consume The Token

        IActionResult firstResponse = await userController.RegisterUserAndMainAccount(
            new RegisterUserAndMainAccountDTO(tokenValue, uniqueAccount, password, password));


        Assert.IsType<CreatedAtActionResult>(firstResponse);


        // Token Should Be Marked As Consumed

        Token? consumedToken = await databaseContext.Tokens.FindAsync(registrationToken.ID);


        Assert.NotNull(consumedToken);

        Assert.NotNull(consumedToken.TimestampConsumed);


        IActionResult secondResponse = await userController.RegisterUserAndMainAccount(
            new RegisterUserAndMainAccountDTO(tokenValue, $"{uniqueAccount}2", password, password));


        Assert.IsType<ConflictObjectResult>(secondResponse);
    }


    [Theory]
    [InlineData("case.one.conflict@kongor.com", "case.two.conflict@kongor.com", "CaseUserConflict", "CaseUserConflict",
        "SecurePassword123!")]
    [InlineData("TEST-ONE-CONFLICT@KONGOR.NET", "TEST-TWO-CONFLICT@KONGOR.NET", "TESTUSERCONFLICT", "TESTUSERCONFLICT",
        "MyP@ssw0rd!")]
    public async Task RegisterUserAndMainAccount_WithExactCaseAccountName_ReturnsConflict(string firstEmailAddress,
        string secondEmailAddress, string firstAccountName, string secondAccountName, string password)

    {
        string uniqueFirstEmail = $"{Guid.NewGuid():N}_{firstEmailAddress}";

        string uniqueSecondEmail = $"{Guid.NewGuid():N}_{secondEmailAddress}";

        string tag = Guid.NewGuid().ToString("N")[..6];

        string uniqueFirstAccount = $"{firstAccountName}_{tag}";

        string uniqueSecondAccount = $"{secondAccountName}_{tag}";


        JWTAuthenticationService jwtAuthenticationService = new(fixture.Factory);


        // Register First Account With Original Case

        await jwtAuthenticationService.CreateAuthenticatedUser(uniqueFirstEmail, uniqueFirstAccount, password);


        using IServiceScope scope = fixture.CreateScope();


        ILogger<EmailAddressController> emailLogger =
            scope.ServiceProvider.GetRequiredService<ILogger<EmailAddressController>>();

        IEmailService emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();

        IWebHostEnvironment emailHostEnvironment =
            scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();


        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();


        EmailAddressController emailController = new(new LocalEmailMediator(databaseContext, emailService, emailHostEnvironment), emailLogger);


        // Try To Register Second Account With Same Case

        await emailController.RegisterEmailAddress(new RegisterEmailAddressDTO(uniqueSecondEmail, uniqueSecondEmail));


        Token? registrationToken = await databaseContext.Tokens.SingleOrDefaultAsync(token =>
            token.EmailAddress.Equals(uniqueSecondEmail) &&
            token.Purpose.Equals(TokenPurpose.EmailAddressVerification));


        Assert.NotNull(registrationToken);


        ILogger<UserController> userLogger =
            scope.ServiceProvider.GetRequiredService<ILogger<UserController>>();

        IUserService userService = scope.ServiceProvider.GetRequiredService<IUserService>();

        IAuthenticationService authService = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();


        UserController userController =
            new(new AccountRepository(databaseContext), userLogger, userService, authService);


        // Second Registration Attempt With Duplicate Account Name Should Return Conflict

        IActionResult response = await userController.RegisterUserAndMainAccount(
            new RegisterUserAndMainAccountDTO(registrationToken.Value.ToString(), uniqueSecondAccount, password,
                password));


        Assert.IsType<ConflictObjectResult>(response);
    }


    [Theory]
    [InlineData("case.one.created@kongor.com", "case.two.created@kongor.com", "CaseUserCreated", "caseusercreated",
        "SecurePassword123!")]
    [InlineData("TEST-ONE-CREATED@KONGOR.NET", "TEST-TWO-CREATED@KONGOR.NET", "TESTUSERCREATED", "testusercreated",
        "MyP@ssw0rd!")]
    public async Task RegisterUserAndMainAccount_WithDifferentCaseAccountName_ReturnsCreated(string firstEmailAddress,
        string secondEmailAddress, string firstAccountName, string secondAccountName, string password)

    {
        string uniqueFirstEmail = $"{Guid.NewGuid():N}_{firstEmailAddress}";

        string uniqueSecondEmail = $"{Guid.NewGuid():N}_{secondEmailAddress}";

        string tag = Guid.NewGuid().ToString("N")[..6];

        string uniqueFirstAccount = $"{firstAccountName}_{tag}";

        string uniqueSecondAccount = $"{secondAccountName}_{tag}";


        JWTAuthenticationService jwtAuthenticationService = new(fixture.Factory);


        // Register First Account With Original Case

        await jwtAuthenticationService.CreateAuthenticatedUser(uniqueFirstEmail, uniqueFirstAccount, password);


        using IServiceScope scope = fixture.CreateScope();


        ILogger<EmailAddressController> emailLogger =
            scope.ServiceProvider.GetRequiredService<ILogger<EmailAddressController>>();

        IEmailService emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();

        IWebHostEnvironment emailHostEnvironment =
            scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();


        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();


        EmailAddressController emailController = new(new LocalEmailMediator(databaseContext, emailService, emailHostEnvironment), emailLogger);


        // Try To Register Second Account With Different Case (Account Names Are Case-Sensitive)

        await emailController.RegisterEmailAddress(new RegisterEmailAddressDTO(uniqueSecondEmail, uniqueSecondEmail));


        Token? registrationToken = await databaseContext.Tokens.SingleOrDefaultAsync(token =>
            token.EmailAddress.Equals(uniqueSecondEmail) &&
            token.Purpose.Equals(TokenPurpose.EmailAddressVerification));


        Assert.NotNull(registrationToken);


        ILogger<UserController> userLogger =
            scope.ServiceProvider.GetRequiredService<ILogger<UserController>>();

        IUserService userService = scope.ServiceProvider.GetRequiredService<IUserService>();

        IAuthenticationService authService = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();


        UserController userController =
            new(new AccountRepository(databaseContext), userLogger, userService, authService);


        // Second Registration Attempt With Distinct Casing Should Return Created

        IActionResult response = await userController.RegisterUserAndMainAccount(
            new RegisterUserAndMainAccountDTO(registrationToken.Value.ToString(), uniqueSecondAccount, password,
                password));


        Assert.IsType<CreatedAtActionResult>(response);
    }
}
