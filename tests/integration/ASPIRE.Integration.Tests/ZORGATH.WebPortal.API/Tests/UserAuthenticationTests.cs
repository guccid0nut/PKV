// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using ZORGATH.WebPortal.API.Services;

namespace ASPIRE.Tests.ZORGATH.WebPortal.API.Tests;

/// <summary>
///     Tests For User Authentication Functionality
/// </summary>
public sealed class UserAuthenticationTests(ZORGATHTestFixture fixture) : IClassFixture<ZORGATHTestFixture>

{
    [Theory]
    [InlineData("login@kongor.com", "LoginPlayer", "SecurePassword123!")]
    [InlineData("auth@kongor.net", "AuthUser", "MyP@ssw0rd!")]
    public async Task LogInUser_WithValidCredentials_ReturnsOKWithValidJWT(string emailAddress, string accountName,
        string password)

    {
        string uniqueEmail = $"{Guid.NewGuid():N}_{emailAddress}";

        string uniqueAccount = $"{accountName}_{Guid.NewGuid():N}"[..15];


        JWTAuthenticationService jwtAuthenticationService = new(fixture.Factory);


        JWTAuthenticationData authenticationResult =
            await jwtAuthenticationService.CreateAuthenticatedUser(uniqueEmail, uniqueAccount, password);


        using IServiceScope scope = fixture.CreateScope();


        IOptions<OperationalConfiguration> configuration =
            scope.ServiceProvider.GetRequiredService<IOptions<OperationalConfiguration>>();


        TokenValidationParameters tokenValidationParameters = new()

        {
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(configuration.Value.JWT.SigningKey)),

            ValidateIssuerSigningKey = true,

            ValidIssuer = configuration.Value.JWT.Issuer,

            ValidateIssuer = true,

            ValidAudience = configuration.Value.JWT.Audience,

            ValidateAudience = true,

            ClockSkew = TimeSpan.Zero,

            ValidateLifetime = true
        };


        JwtSecurityTokenHandler tokenHandler = new();


        tokenHandler.ValidateToken(authenticationResult.AuthenticationToken, tokenValidationParameters,
            out SecurityToken validatedToken);


        if (validatedToken is not JwtSecurityToken jwtToken)
            throw new InvalidOperationException(
                $"Expected Token To Be A JWT Security Token, But Was {validatedToken.GetType().Name}");


        Assert.Equal(uniqueAccount, jwtToken.Subject);

        Assert.Equal(uniqueEmail, jwtToken.Claims.GetUserEmailAddress());

        Assert.Equal(configuration.Value.JWT.Issuer, jwtToken.Issuer);

        Assert.Equal(configuration.Value.JWT.Audience, jwtToken.Audiences.First());


        string userIDClaim = jwtToken.Claims.Single(claim => claim.Type.Equals(Claims.UserID)).Value;

        Assert.Equal(authenticationResult.UserID.ToString(), userIDClaim);
    }


    [Theory]
    [InlineData("NonExistentPlayer", "SomePassword123!")]
    [InlineData("InvalidUser", "AnotherPass123!")]
    public async Task LogInUser_WithInvalidAccountName_ReturnsNotFound(string accountName, string password)

    {
        using IServiceScope scope = fixture.CreateScope();


        ILogger<UserController> userLogger =
            scope.ServiceProvider.GetRequiredService<ILogger<UserController>>();

        IUserService userService = scope.ServiceProvider.GetRequiredService<IUserService>();

        IAuthenticationService authService = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();


        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();


        UserController userController =
            new(new AccountRepository(databaseContext), userLogger, userService, authService);


        IActionResult response = await userController.LogInUser(new LogInUserDTO(accountName, password));


        Assert.IsType<NotFoundObjectResult>(response);
    }


    [Theory]
    [InlineData("wrongpass@kongor.com", "WrongPassUser", "CorrectPassword123!", "WrongPassword123!")]
    [InlineData("badauth@kongor.net", "BadAuthUser", "RightP@ss!", "WrongP@ss!")]
    public async Task LogInUser_WithInvalidPassword_ReturnsUnauthorized(string emailAddress, string accountName,
        string correctPassword, string wrongPassword)

    {
        string uniqueEmail = $"{Guid.NewGuid():N}_{emailAddress}";

        string uniqueAccount = $"{accountName}_{Guid.NewGuid():N}"[..15];


        JWTAuthenticationService jwtAuthenticationService = new(fixture.Factory);


        await jwtAuthenticationService.CreateAuthenticatedUser(uniqueEmail, uniqueAccount, correctPassword);


        using IServiceScope scope = fixture.CreateScope();


        ILogger<UserController> userLogger =
            scope.ServiceProvider.GetRequiredService<ILogger<UserController>>();

        IUserService userService = scope.ServiceProvider.GetRequiredService<IUserService>();

        IAuthenticationService authService = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();


        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();


        UserController userController =
            new(new AccountRepository(databaseContext), userLogger, userService, authService);


        IActionResult response = await userController.LogInUser(new LogInUserDTO(uniqueAccount, wrongPassword));


        Assert.IsType<UnauthorizedObjectResult>(response);
    }


    [Theory]
    [InlineData("claims@kongor.com", "ClaimsUser", "SecurePassword123!")]
    [InlineData("jwt@kongor.net", "JWTUser", "MyP@ssw0rd!")]
    public async Task UserAuthentication_LogInUser_JWTContainsAllRequiredClaims(string emailAddress, string accountName, string password)

    {
        string uniqueEmail = $"{Guid.NewGuid():N}_{emailAddress}";

        string uniqueAccount = $"{accountName}_{Guid.NewGuid():N}"[..15];


        JWTAuthenticationService jwtAuthenticationService = new(fixture.Factory);


        JWTAuthenticationData authenticationResult =
            await jwtAuthenticationService.CreateAuthenticatedUser(uniqueEmail, uniqueAccount, password);


        JwtSecurityTokenHandler tokenHandler = new();

        JwtSecurityToken jwtToken = tokenHandler.ReadJwtToken(authenticationResult.AuthenticationToken);


        Assert.Contains(jwtToken.Claims, claim => claim.Type.Equals(JwtRegisteredClaimNames.Sub));

        Assert.Contains(jwtToken.Claims, claim => claim.Type.Equals(JwtRegisteredClaimNames.Email));

        Assert.Contains(jwtToken.Claims, claim => claim.Type.Equals(JwtRegisteredClaimNames.Iat));

        Assert.Contains(jwtToken.Claims, claim => claim.Type.Equals(JwtRegisteredClaimNames.AuthTime));

        Assert.Contains(jwtToken.Claims, claim => claim.Type.Equals(JwtRegisteredClaimNames.Jti));

        Assert.Contains(jwtToken.Claims, claim => claim.Type.Equals(JwtRegisteredClaimNames.Nonce));


        Assert.Contains(jwtToken.Claims, claim => claim.Type.Equals(Claims.UserID));

        Assert.Contains(jwtToken.Claims, claim => claim.Type.Equals(Claims.AccountID));

        Assert.Contains(jwtToken.Claims, claim => claim.Type.Equals(Claims.AccountIsMain));
    }


    [Trait("Category", "Manual")]
    [Theory]
    [InlineData("fullflow@kongor.com", "FlowUser", "SecurePassword123!")]
    [InlineData("complete@kongor.net", "CompleteUser", "MyP@ssw0rd!")]
    public async Task CompleteAuthenticationFlow_RegisterEmailThenUserThenLogin_Succeeds(string emailAddress,
        string accountName, string password)

    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        string uniqueEmail = $"{Guid.NewGuid():N}_{emailAddress}";

        string uniqueAccount = $"{accountName}_{Guid.NewGuid():N}"[..15];


        JWTAuthenticationService jwtAuthenticationService = new(fixture.Factory);


        JWTAuthenticationData result =
            await jwtAuthenticationService.CreateAuthenticatedUser(uniqueEmail, uniqueAccount, password);


        Assert.True(result.UserID > 0);

        Assert.Equal(uniqueAccount, result.AccountName);

        Assert.Equal(uniqueEmail, result.EmailAddress);

        Assert.NotEmpty(result.AuthenticationToken);


        using IServiceScope scope = fixture.CreateScope();


        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();


        User? user = await databaseContext.Users
            .Include(user => user.Accounts)
            .Include(user => user.Role)
            .SingleOrDefaultAsync(user => user.ID.Equals(result.UserID));


        Assert.NotNull(user);

        Assert.Equal(uniqueEmail, user.EmailAddress);

        Assert.True(user.Accounts.Count > 0);

        Assert.Contains(user.Accounts, account => account.Name.Equals(uniqueAccount));

        Assert.Equal(UserRoles.User, user.Role.Name);
    }


    [Theory]
    [InlineData("caselogin1@kongor.com", "CaseLoginUser1", "CaseLoginUser1", "SecurePassword123!")]
    [InlineData("UPPERCASELOGIN1@kongor.net", "UPPERCASEUSER1", "UPPERCASEUSER1", "MyP@ssw0rd!")]
    public async Task LogInUser_WithExactCaseAccountName_ReturnsOK(string emailAddress, string registeredAccountName,
        string loginAccountName, string password)

    {
        string uniqueEmail = $"{Guid.NewGuid():N}_{emailAddress}";

        string tag = Guid.NewGuid().ToString("N")[..6];

        string uniqueRegisteredAccount = $"{registeredAccountName}_{tag}";

        string uniqueLoginAccount = $"{loginAccountName}_{tag}";


        JWTAuthenticationService jwtAuthenticationService = new(fixture.Factory);


        await jwtAuthenticationService.CreateAuthenticatedUser(uniqueEmail, uniqueRegisteredAccount, password);


        using IServiceScope scope = fixture.CreateScope();


        ILogger<UserController> userLogger =
            scope.ServiceProvider.GetRequiredService<ILogger<UserController>>();

        IUserService userService = scope.ServiceProvider.GetRequiredService<IUserService>();

        IAuthenticationService authService = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();


        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();


        UserController userController =
            new(new AccountRepository(databaseContext), userLogger, userService, authService);


        IActionResult response = await userController.LogInUser(new LogInUserDTO(uniqueLoginAccount, password));


        Assert.IsType<OkObjectResult>(response);
    }


    [Theory]
    [InlineData("caselogin2@kongor.com", "CaseLoginUser2", "caseloginuser2", "SecurePassword123!")]
    [InlineData("UPPERCASELOGIN2@kongor.net", "UPPERCASEUSER2", "uppercaseuser2", "MyP@ssw0rd!")]
    public async Task LogInUser_WithDifferentCaseAccountName_Succeeds(string emailAddress,
        string registeredAccountName, string loginAccountName, string password)

    {
        string uniqueEmail = $"{Guid.NewGuid():N}_{emailAddress}";

        string tag = Guid.NewGuid().ToString("N")[..6];

        string uniqueRegisteredAccount = $"{registeredAccountName}_{tag}";

        string uniqueLoginAccount = $"{loginAccountName}_{tag}";


        JWTAuthenticationService jwtAuthenticationService = new(fixture.Factory);


        await jwtAuthenticationService.CreateAuthenticatedUser(uniqueEmail, uniqueRegisteredAccount, password);


        using IServiceScope scope = fixture.CreateScope();


        ILogger<UserController> userLogger =
            scope.ServiceProvider.GetRequiredService<ILogger<UserController>>();

        IUserService userService = scope.ServiceProvider.GetRequiredService<IUserService>();

        IAuthenticationService authService = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();


        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();


        UserController userController =
            new(new AccountRepository(databaseContext), userLogger, userService, authService);


        IActionResult response = await userController.LogInUser(new LogInUserDTO(uniqueLoginAccount, password));


        Assert.IsType<OkObjectResult>(response);
    }


    [Theory]
    [InlineData("expiry@kongor.com", "ExpiryUser", "SecurePassword123!")]
    [InlineData("tokenlife@kongor.net", "TokenLifeUser", "MyP@ssw0rd!")]
    public async Task UserAuthentication_LogInUser_JWTHasValidExpirationClaim(string emailAddress, string accountName, string password)

    {
        string uniqueEmail = $"{Guid.NewGuid():N}_{emailAddress}";

        string uniqueAccount = $"{accountName}_{Guid.NewGuid():N}"[..15];


        JWTAuthenticationService jwtAuthenticationService = new(fixture.Factory);


        JWTAuthenticationData authenticationResult =
            await jwtAuthenticationService.CreateAuthenticatedUser(uniqueEmail, uniqueAccount, password);


        JwtSecurityTokenHandler tokenHandler = new();

        JwtSecurityToken jwtToken = tokenHandler.ReadJwtToken(authenticationResult.AuthenticationToken);


        // Verify Expiration Claim Exists

        Assert.Contains(jwtToken.Claims, claim => claim.Type.Equals(JwtRegisteredClaimNames.Exp));


        // Verify Token Has A Valid Expiration Time In The Future

        Assert.True(jwtToken.ValidTo > TimeProvider.System.GetUtcNow().UtcDateTime);


        // Verify Token Was Issued In The Past Or Now (5 Second Grace For Clock Skew)

        Assert.True(jwtToken.ValidFrom <= TimeProvider.System.GetUtcNow().UtcDateTime.AddSeconds(5));
    }
}
