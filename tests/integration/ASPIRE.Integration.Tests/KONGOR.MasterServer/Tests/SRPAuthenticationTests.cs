// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

// ReSharper disable ConditionalAccessQualifierIsNonNullableAccordingToAPIContract


using System.Net;
using System.Security.Cryptography;

namespace ASPIRE.Tests.KONGOR.MasterServer.Tests;

/// <summary>
///     Tests For SRP Authentication Functionality In KONGOR Master Server
/// </summary>
public sealed class SrpAuthenticationTests(KONGORTestFixture fixture) : IClassFixture<KONGORTestFixture>

{
    [Theory]
    [InlineData("srpuser1@kongor.com", "SRPPlayer1", "SecurePassword123!")]
    [InlineData("srpuser2@kongor.net", "SRPPlayer2", "MyP@ssw0rd!")]
    public async Task CreateAccountWithSRPCredentials_WithValidData_CreatesAccountWithValidSRPFields(
        string emailAddress, string accountName, string password)

    {
        WebApplicationFactory<KONGORAssemblyMarker> webApplicationFactory = fixture.Factory;


        SRPAuthenticationService srpAuthenticationService = new(webApplicationFactory);


        (Account account, string returnedPassword) =
            await srpAuthenticationService.CreateAccountWithSRPCredentials(emailAddress, accountName, password);


        Assert.NotNull(account);


        {
            Assert.Equal(accountName, account.Name);

            Assert.Equal(emailAddress, account.User.EmailAddress);

            Assert.NotEmpty(account.User.SRPPasswordSalt);

            Assert.NotEmpty(account.User.SRPPasswordHash);

            Assert.NotEmpty(account.User.PBKDF2PasswordHash);

            Assert.True(account.IsMain);

            Assert.Equal(password, returnedPassword);
        }


        using IServiceScope scope = webApplicationFactory.Services.CreateScope();

        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();


        Account? databaseAccount = await databaseContext.Accounts
            .Include(accountRecord => accountRecord.User)
            .SingleOrDefaultAsync(accountRecord => accountRecord.Name.Equals(accountName));


        Assert.NotNull(databaseAccount);


        {
            Assert.Equal(emailAddress, databaseAccount.User.EmailAddress);

            Assert.Equal(account.User.SRPPasswordSalt, databaseAccount.User.SRPPasswordSalt);

            Assert.Equal(account.User.SRPPasswordHash, databaseAccount.User.SRPPasswordHash);
        }
    }


    [Theory]
    [InlineData("salt1@kongor.com", "SaltPlayer1", "SecurePassword123!")]
    [InlineData("salt2@kongor.net", "SaltPlayer2", "MyP@ssw0rd!")]
    public async Task SRPAuthentication_CreateAccountWithSRPCredentials_GeneratesUniqueSaltsForDifferentAccounts(string emailAddress,
        string accountName, string password)

    {
        WebApplicationFactory<KONGORAssemblyMarker> webApplicationFactory = fixture.Factory;


        SRPAuthenticationService srpAuthenticationService = new(webApplicationFactory);


        (Account account1, string _) =
            await srpAuthenticationService.CreateAccountWithSRPCredentials(emailAddress, accountName, password);

        (Account account2, string _) =
            await srpAuthenticationService.CreateAccountWithSRPCredentials($"other_{emailAddress}",
                $"Other{accountName}", password);


        Assert.NotEqual(account2.User.SRPPasswordSalt, account1.User.SRPPasswordSalt);
    }


    [Theory]
    [InlineData("hash1@kongor.com", "HashPlayer1", "SecurePassword123!")]
    [InlineData("hash2@kongor.net", "HashPlayer2", "MyP@ssw0rd!")]
    public async Task SRPAuthentication_CreateAccountWithSRPCredentials_HashesAreDeterministicForSamePasswordAndSalt(string emailAddress,
        string accountName, string password)

    {
        WebApplicationFactory<KONGORAssemblyMarker> webApplicationFactory = fixture.Factory;


        SRPAuthenticationService srpAuthenticationService = new(webApplicationFactory);


        (Account account, string _) =
            await srpAuthenticationService.CreateAccountWithSRPCredentials(emailAddress, accountName, password);


        string salt = account.User.SRPPasswordSalt;

        string expectedHash = SRPRegistrationHandlers.ComputeSRPPasswordHash(password, salt);


        Assert.Equal(expectedHash, account.User.SRPPasswordHash);
    }


    [Theory]
    [InlineData("role1@kongor.com", "RolePlayer1", "SecurePassword123!")]
    [InlineData("role2@kongor.net", "RolePlayer2", "MyP@ssw0rd!")]
    public async Task SRPAuthentication_CreateAccountWithSRPCredentials_AssignsUserRoleCorrectly(string emailAddress, string accountName,
        string password)

    {
        WebApplicationFactory<KONGORAssemblyMarker> webApplicationFactory = fixture.Factory;


        SRPAuthenticationService srpAuthenticationService = new(webApplicationFactory);


        _ = await srpAuthenticationService.CreateAccountWithSRPCredentials(emailAddress, accountName, password);


        using IServiceScope scope = webApplicationFactory.Services.CreateScope();

        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();


        Account? databaseAccount = await databaseContext.Accounts
            .Include(accountRecord => accountRecord.User)
            .ThenInclude(user => user.Role)
            .SingleOrDefaultAsync(accountRecord => accountRecord.Name.Equals(accountName));


        Assert.NotNull(databaseAccount);


        {
            Assert.NotNull(databaseAccount.User.Role);

            Assert.Equal(UserRoles.User, databaseAccount.User.Role.Name);
        }
    }


    [Theory]
    [InlineData("pbkdf1@kongor.com", "PBKDF2Player1", "SecurePassword123!")]
    [InlineData("pbkdf2@kongor.net", "PBKDF2Player2", "MyP@ssw0rd!")]
    public async Task SRPAuthentication_CreateAccountWithSRPCredentials_GeneratesBothSRPAndPBKDF2Hashes(string emailAddress,
        string accountName, string password)

    {
        WebApplicationFactory<KONGORAssemblyMarker> webApplicationFactory = fixture.Factory;


        SRPAuthenticationService srpAuthenticationService = new(webApplicationFactory);


        (Account account, string _) =
            await srpAuthenticationService.CreateAccountWithSRPCredentials(emailAddress, accountName, password);


        {
            Assert.NotEmpty(account.User.SRPPasswordHash);

            Assert.NotEmpty(account.User.PBKDF2PasswordHash);

            Assert.NotEqual(account.User.PBKDF2PasswordHash, account.User.SRPPasswordHash);
        }


        PasswordHasher<User> passwordHasher = new();


        PasswordVerificationResult verificationResult =
            passwordHasher.VerifyHashedPassword(account.User, account.User.PBKDF2PasswordHash, password);


        Assert.NotEqual(PasswordVerificationResult.Failed, verificationResult);
    }


    [Theory]
    [InlineData("srpauth1@kongor.com", "SRPAuthPlayer1", "SecurePassword123!")]
    [InlineData("srpauth2@kongor.net", "SRPAuthPlayer2", "MyP@ssw0rd!")]
    public async Task AuthenticateWithSRP_WithValidCredentials_ReturnsSuccessfulAuthentication(string emailAddress,
        string accountName, string password)

    {
        WebApplicationFactory<KONGORAssemblyMarker> webApplicationFactory = fixture.Factory;


        SRPAuthenticationService srpAuthenticationService = new(webApplicationFactory);


        SRPAuthenticationData result =
            await srpAuthenticationService.AuthenticateWithSRP(emailAddress, accountName, password);


        Assert.True(result.Success, result.ErrorMessage ?? "Failed without error message");


        {
            Assert.NotNull(result.ServerProof);

            Assert.NotEmpty(result.ServerProof!);

            Assert.Equal(accountName, result.Name);

            Assert.Equal(emailAddress, result.Email);

            Assert.False(string.IsNullOrEmpty(result.Cookie));
        }


        // Verify the cookie is valid by making an authenticated request

        HttpClient client = webApplicationFactory.CreateClient();

        Dictionary<string, string> payload = new() { { "f", "get_initStats" }, { "cookie", result.Cookie! } };


        HttpResponseMessage response =
            await client.PostAsync("client_requester.php", new FormUrlEncodedContent(payload));


        Assert.True(response.IsSuccessStatusCode);


        string body = await response.Content.ReadAsStringAsync();

        // get_initStats returns infos dictionary containing account field stats

        Assert.Contains("infos", body);

        Assert.Contains("account_id", body);
    }


    [Theory]
    [InlineData("srpflow1@kongor.com", "SRPFlowPlayer1", "SecurePassword123!")]
    [InlineData("srpflow2@kongor.net", "SRPFlowPlayer2", "MyP@ssw0rd!")]
    public async Task AuthenticateWithSRP_CompletesFullAuthenticationFlow_ReturnsAccountData(string emailAddress,
        string accountName, string password)

    {
        WebApplicationFactory<KONGORAssemblyMarker> webApplicationFactory = fixture.Factory;


        SRPAuthenticationService srpAuthenticationService = new(webApplicationFactory);


        SRPAuthenticationData result =
            await srpAuthenticationService.AuthenticateWithSRP(emailAddress, accountName, password);


        Assert.True(result.Success);


        {
            Assert.Equal(accountName, result.Name);

            Assert.Equal(emailAddress, result.Email);

            Assert.False(string.IsNullOrEmpty(result.Cookie));

            Assert.NotEmpty(result.ServerProof!);
        }


        using IServiceScope scope = webApplicationFactory.Services.CreateScope();

        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();


        Account? databaseAccount = await databaseContext.Accounts
            .Include(accountRecord => accountRecord.User)
            .SingleOrDefaultAsync(accountRecord => accountRecord.Name.Equals(accountName));


        Assert.NotNull(databaseAccount);


        {
            Assert.True(databaseAccount.TimestampLastActive > DateTimeOffset.MinValue);
        }
    }


    [Theory]
    [InlineData("preauth1@kongor.com", "PreAuthPlayer1", "SecurePassword123!")]
    [InlineData("preauth2@kongor.net", "PreAuthPlayer2", "MyP@ssw0rd!")]
    public async Task PerformPreAuthentication_WithValidAccount_ReturnsSessionDataAndServerEphemeral(
        string emailAddress, string accountName, string password)

    {
        WebApplicationFactory<KONGORAssemblyMarker> webApplicationFactory = fixture.Factory;


        SRPAuthenticationService srpAuthenticationService = new(webApplicationFactory);


        (Account account, string _) =
            await srpAuthenticationService.CreateAccountWithSRPCredentials(emailAddress, accountName, password);


        SrpParameters parameters = SrpParameters.Create<SHA256>
        (SRPAuthenticationSessionDataStageOne.SafePrimeNumber,
            SRPAuthenticationSessionDataStageOne.MultiplicativeGroupGenerator);


        SrpClient client = new(parameters);

        SrpEphemeral clientEphemeral = client.GenerateEphemeral();


        (bool success, string? sessionSalt, string? passwordSalt, string? serverPublicEphemeral, string? _)
            = await srpAuthenticationService.PerformPreAuthentication(accountName, clientEphemeral.Public);


        {
            Assert.True(success);

            Assert.NotEmpty(sessionSalt!);

            Assert.Equal(account.User.SRPPasswordSalt, passwordSalt);

            Assert.NotEmpty(serverPublicEphemeral!);
        }
    }


    [Theory]
    [InlineData("NonExistentAccount")]
    [InlineData("InvalidUser123")]
    public async Task PerformPreAuthentication_WithNonExistentAccount_ReturnsNotFound(string accountName)

    {
        WebApplicationFactory<KONGORAssemblyMarker> webApplicationFactory = fixture.Factory;


        SRPAuthenticationService srpAuthenticationService = new(webApplicationFactory);


        SrpParameters parameters = SrpParameters.Create<SHA256>
        (SRPAuthenticationSessionDataStageOne.SafePrimeNumber,
            SRPAuthenticationSessionDataStageOne.MultiplicativeGroupGenerator);


        SrpClient client = new(parameters);

        SrpEphemeral clientEphemeral = client.GenerateEphemeral();


        (bool success, string? _, string? _, string? _, string? errorMessage)
            = await srpAuthenticationService.PerformPreAuthentication(accountName, clientEphemeral.Public);


        {
            Assert.False(success);

            Assert.NotEmpty(errorMessage!);
        }
    }


    [Theory]
    [InlineData("crypto1@kongor.com", "CryptoPlayer1", "SecurePassword123!")]
    [InlineData("crypto2@kongor.net", "CryptoPlayer2", "MyP@ssw0rd!")]
    public async Task AuthenticateWithSRP_CryptographicOperations_ProducesValidServerProof(string emailAddress,
        string accountName, string password)

    {
        WebApplicationFactory<KONGORAssemblyMarker> webApplicationFactory = fixture.Factory;


        SRPAuthenticationService srpAuthenticationService = new(webApplicationFactory);


        SRPAuthenticationData result =
            await srpAuthenticationService.AuthenticateWithSRP(emailAddress, accountName, password);


        Assert.True(result.Success);

        Assert.NotNull(result.ServerProof);


        string serverProof = result.ServerProof ?? throw new InvalidOperationException("Server Proof Is NULL");


        {
            Assert.NotEmpty(serverProof);

            Assert.True(serverProof.Length > 0);

            Assert.DoesNotContain(" ", serverProof);
        }
    }


    [Theory]
    [InlineData("unique1@kongor.com", "UniquePlayer1", "SecurePassword123!")]
    [InlineData("unique2@kongor.net", "UniquePlayer2", "MyP@ssw0rd!")]
    public async Task AuthenticateWithSRP_MultipleAuthentications_GeneratesUniqueCookies(string emailAddress,
        string accountName, string password)

    {
        WebApplicationFactory<KONGORAssemblyMarker> webApplicationFactory = fixture.Factory;


        SRPAuthenticationService srpAuthenticationService = new(webApplicationFactory);


        SRPAuthenticationData result1 =
            await srpAuthenticationService.AuthenticateWithSRP(emailAddress, accountName, password);

        SRPAuthenticationData result2 =
            await srpAuthenticationService.AuthenticateWithSRP($"other_{emailAddress}", $"Other{accountName}",
                password);


        {
            Assert.True(result1.Success);

            Assert.True(result2.Success);

            Assert.NotEqual(result2.Cookie, result1.Cookie);
        }
    }


    [Theory]
    [InlineData("disabled@kongor.com", "DisabledPlayer", "SecurePassword123!")]
    public async Task AuthenticateWithSRP_WithDisabledAccount_ReturnsUnauthorised(string emailAddress,
        string accountName, string password)

    {
        WebApplicationFactory<KONGORAssemblyMarker> webApplicationFactory = fixture.Factory;


        SRPAuthenticationService srpAuthenticationService = new(webApplicationFactory);


        (Account account, string _) =
            await srpAuthenticationService.CreateAccountWithSRPCredentials(emailAddress, accountName, password);


        using IServiceScope scope = webApplicationFactory.Services.CreateScope();

        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();


        Account dbAccount = await databaseContext.Accounts.FirstAsync(a => a.ID == account.ID);

        dbAccount.Type = AccountType.Disabled;

        await databaseContext.SaveChangesAsync();


        SRPAuthenticationData result = await srpAuthenticationService.PerformFullAuthentication(account, password);


        {
            Assert.False(result.Success);

            Assert.NotEmpty(result.ErrorMessage!);
        }
    }


    [Theory]
    [InlineData("wrongpass1@kongor.com", "WrongPassPlayer1", "CorrectPassword123!", "WrongPassword456!")]
    [InlineData("wrongpass2@kongor.net", "WrongPassPlayer2", "MyP@ssw0rd!", "NotMyP@ssw0rd!")]
    public async Task AuthenticateWithSRP_WithIncorrectPassword_ReturnsAuthenticationFailure(string emailAddress,
        string accountName, string correctPassword, string wrongPassword)

    {
        WebApplicationFactory<KONGORAssemblyMarker> webApplicationFactory = fixture.Factory;


        SRPAuthenticationService srpAuthenticationService = new(webApplicationFactory);


        (Account account, string _) =
            await srpAuthenticationService.CreateAccountWithSRPCredentials(emailAddress, accountName, correctPassword);


        SRPAuthenticationData result = await srpAuthenticationService.PerformFullAuthentication(account, wrongPassword);


        {
            Assert.False(result.Success);

            Assert.NotEmpty(result.ErrorMessage!);
        }
    }


    [Trait("Category", "Manual")]
    [Theory]
    [InlineData("sameaccount@kongor.com", "SameAccountPlayer", "SecurePassword123!")]
    public async Task AuthenticateWithSRP_SameAccountMultipleLogins_GeneratesUniqueCookies(string emailAddress,
        string accountName, string password)

    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        WebApplicationFactory<KONGORAssemblyMarker> webApplicationFactory = fixture.Factory;


        SRPAuthenticationService srpAuthenticationService = new(webApplicationFactory);


        (Account account, string _) =
            await srpAuthenticationService.CreateAccountWithSRPCredentials(emailAddress, accountName, password);


        SRPAuthenticationData result1 = await srpAuthenticationService.PerformFullAuthentication(account, password);

        SRPAuthenticationData result2 = await srpAuthenticationService.PerformFullAuthentication(account, password);


        {
            Assert.True(result1.Success);

            Assert.True(result2.Success);

            Assert.NotEqual(result2.Cookie, result1.Cookie);
        }
    }


    [Theory]
    [InlineData("casepass@kongor.com", "CasePassPlayer", "SecurePassword123!")]
    public async Task AuthenticateWithSRP_WithDifferentPasswordCase_ReturnsAuthenticationFailure(string emailAddress,
        string accountName, string password)

    {
        WebApplicationFactory<KONGORAssemblyMarker> webApplicationFactory = fixture.Factory;


        SRPAuthenticationService srpAuthenticationService = new(webApplicationFactory);


        (Account account, string _) =
            await srpAuthenticationService.CreateAccountWithSRPCredentials(emailAddress, accountName, password);


        string differentCasePassword = password.ToLowerInvariant();


        SRPAuthenticationData result =
            await srpAuthenticationService.PerformFullAuthentication(account, differentCasePassword);


        {
            Assert.False(result.Success);

            Assert.NotEmpty(result.ErrorMessage!);
        }
    }


    [Theory]
    [InlineData("tamperedproof@kongor.com", "TamperedProofPlayer", "SecurePassword123!")]
    public async Task AuthenticateWithSRP_WithTamperedClientProof_ReturnsAuthenticationFailure(string emailAddress,
        string accountName, string password)

    {
        WebApplicationFactory<KONGORAssemblyMarker> webApplicationFactory = fixture.Factory;


        SRPAuthenticationService srpAuthenticationService = new(webApplicationFactory);


        _ = await srpAuthenticationService.CreateAccountWithSRPCredentials(emailAddress, accountName, password);


        HttpClient httpClient = webApplicationFactory.CreateClient();


        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "S2 Games/Heroes Of Newerth/4.10.1.0/wac/x86_64");


        SrpParameters parameters = SrpParameters.Create<SHA256>
        (SRPAuthenticationSessionDataStageOne.SafePrimeNumber,
            SRPAuthenticationSessionDataStageOne.MultiplicativeGroupGenerator);


        parameters.Generator = parameters.Pad(parameters.Generator);


        SrpClient client = new(parameters);

        SrpEphemeral clientEphemeral = client.GenerateEphemeral();


        (bool preAuthSuccess, string? sessionSalt, string? passwordSalt, string? serverPublicEphemeral, string? _)
            = await srpAuthenticationService.PerformPreAuthentication(accountName, clientEphemeral.Public);


        Assert.True(preAuthSuccess);

        Assert.NotNull(sessionSalt);

        Assert.NotNull(passwordSalt);

        Assert.NotNull(serverPublicEphemeral);


        string passwordHash = SRPRegistrationHandlers.ComputeSRPPasswordHash(password, passwordSalt);

        string privateClientKey = client.DerivePrivateKey(sessionSalt, accountName, passwordHash);


        SrpSession clientSession = client.DeriveSession(clientEphemeral.Secret, serverPublicEphemeral, sessionSalt,
            accountName, privateClientKey);


        string tamperedProof = clientSession.Proof.Length > 10
            ? clientSession.Proof[..^1] + "X"
            : "TamperedProof123";


        Dictionary<string, string> authFormData = new()

        {
            { "login", accountName },

            { "proof", tamperedProof },

            { "OSType", "windows" },

            { "MajorVersion", "4" },

            { "MinorVersion", "10" },

            { "MicroVersion", "1" },

            { "SysInfo", "testhash|testhash|testhash|testhash|testhash" }
        };


        FormUrlEncodedContent authContent = new(authFormData);

        HttpResponseMessage authResponse = await httpClient.PostAsync("client_requester.php?f=srpAuth", authContent);


        Assert.False(authResponse.IsSuccessStatusCode);
    }


    [Trait("Category", "Manual")]
    [Theory]
    [InlineData("serververify@kongor.com", "ServerVerifyPlayer", "SecurePassword123!")]
    public async Task AuthenticateWithSRP_ServerProof_IsVerifiableByClient(string emailAddress, string accountName,
        string password)

    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        WebApplicationFactory<KONGORAssemblyMarker> webApplicationFactory = fixture.Factory;


        SRPAuthenticationService srpAuthenticationService = new(webApplicationFactory);


        _ = await srpAuthenticationService.CreateAccountWithSRPCredentials(emailAddress, accountName, password);


        HttpClient httpClient = webApplicationFactory.CreateClient();


        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "S2 Games/Heroes Of Newerth/4.10.1.0/wac/x86_64");


        SrpParameters parameters = SrpParameters.Create<SHA256>
        (SRPAuthenticationSessionDataStageOne.SafePrimeNumber,
            SRPAuthenticationSessionDataStageOne.MultiplicativeGroupGenerator);


        parameters.Generator = parameters.Pad(parameters.Generator);


        SrpClient client = new(parameters);

        SrpEphemeral clientEphemeral = client.GenerateEphemeral();


        (bool preAuthSuccess, string? sessionSalt, string? passwordSalt, string? serverPublicEphemeral, string? _)
            = await srpAuthenticationService.PerformPreAuthentication(accountName, clientEphemeral.Public);


        Assert.True(preAuthSuccess);

        Assert.NotNull(sessionSalt);

        Assert.NotNull(passwordSalt);

        Assert.NotNull(serverPublicEphemeral);


        string passwordHash = SRPRegistrationHandlers.ComputeSRPPasswordHash(password, passwordSalt);

        string privateClientKey = client.DerivePrivateKey(sessionSalt, accountName, passwordHash);


        SrpSession clientSession = client.DeriveSession(clientEphemeral.Secret, serverPublicEphemeral, sessionSalt,
            accountName, privateClientKey);


        Dictionary<string, string> authFormData = new()

        {
            { "login", accountName },

            { "proof", clientSession.Proof },

            { "OSType", "windows" },

            { "MajorVersion", "4" },

            { "MinorVersion", "10" },

            { "MicroVersion", "1" },

            { "SysInfo", "testhash|testhash|testhash|testhash|testhash" }
        };


        FormUrlEncodedContent authContent = new(authFormData);

        HttpResponseMessage authResponse = await httpClient.PostAsync("client_requester.php?f=srpAuth", authContent);


        string authResponseBody = await authResponse.Content.ReadAsStringAsync();


        Assert.True(authResponse.IsSuccessStatusCode);


        IDictionary<object, object>? authData =
            PhpSerialization.Deserialize(authResponseBody) as IDictionary<object, object>;


        string serverProof = authData?["proof"] as string ?? throw new NullReferenceException("Server Proof Is NULL");


        client.VerifySession(clientEphemeral.Public, clientSession, serverProof);
    }


    [Fact]
    public async Task SwitchAuth_WithValidSubAccount_SwitchesAccountSuccessfully()

    {
        WebApplicationFactory<KONGORAssemblyMarker> webApplicationFactory = fixture.Factory;

        SRPAuthenticationService srpAuthenticationService = new(webApplicationFactory);


        const string emailAddress = "switch_test_main@kongor.com";

        const string mainNick = "SwitchMain";

        const string subNick = "SwitchSub";

        const string password = "Password123!";


        SRPAuthenticationData mainAuthData =
            await srpAuthenticationService.AuthenticateWithSRP(emailAddress, mainNick, password);


        Assert.NotNull(mainAuthData.Cookie);

        Assert.NotEmpty(mainAuthData.Cookie);


        using (IServiceScope scope = webApplicationFactory.Services.CreateScope())

        {
            MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

            Account? mainAccount = await databaseContext.Accounts
                .Include(account => account.User)
                .FirstOrDefaultAsync(account => account.Name == mainNick);


            Assert.NotNull(mainAccount);


            Account subAccount = new() { Name = subNick, IsMain = false, User = mainAccount.User };

            databaseContext.Accounts.Add(subAccount);

            await databaseContext.SaveChangesAsync();
        }


        HttpClient httpClient = webApplicationFactory.CreateClient();

        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "S2 Games/Heroes Of Newerth/4.10.1.0/wac/x86_64");


        Dictionary<string, string> switchPayload = new() { { "login", subNick }, { "cookie", mainAuthData.Cookie! } };


        HttpResponseMessage switchResponse = await httpClient.PostAsync(
            "client_requester.php?f=switch_auth",
            new FormUrlEncodedContent(switchPayload));


        Assert.True(switchResponse.IsSuccessStatusCode);


        string switchResponseBody = await switchResponse.Content.ReadAsStringAsync();

        IDictionary<object, object>? switchData =
            PhpSerialization.Deserialize(switchResponseBody) as IDictionary<object, object>;


        Assert.NotNull(switchData);

        Assert.Equal(subNick, switchData["nickname"]?.ToString());

        Assert.Equal(mainAuthData.Account.ID.ToString(), switchData["super_id"]?.ToString());

        Assert.NotEmpty(switchData["cookie"]?.ToString() ?? string.Empty);
    }


    [Fact]
    public async Task SwitchAuth_WithUnauthorizedAccount_ReturnsForbidden()

    {
        WebApplicationFactory<KONGORAssemblyMarker> webApplicationFactory = fixture.Factory;

        SRPAuthenticationService srpAuthenticationService = new(webApplicationFactory);


        const string email1 = "switch_user1@kongor.com";

        const string user1Nick = "SwitchUserOne";

        const string email2 = "switch_user2@kongor.com";

        const string user2Nick = "SwitchUserTwo";

        const string password = "Password123!";


        SRPAuthenticationData user1Auth =
            await srpAuthenticationService.AuthenticateWithSRP(email1, user1Nick, password);

        _ = await srpAuthenticationService.AuthenticateWithSRP(email2, user2Nick, password);


        Assert.NotNull(user1Auth.Cookie);


        HttpClient httpClient = webApplicationFactory.CreateClient();

        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "S2 Games/Heroes Of Newerth/4.10.1.0/wac/x86_64");


        Dictionary<string, string> unauthorizedSwitchPayload = new()

        {
            { "login", user2Nick }, { "cookie", user1Auth.Cookie! }
        };


        HttpResponseMessage switchResponse = await httpClient.PostAsync(
            "client_requester.php?f=switch_auth",
            new FormUrlEncodedContent(unauthorizedSwitchPayload));


        Assert.Equal(HttpStatusCode.Forbidden, switchResponse.StatusCode);
    }


    [Theory]
    [InlineData("guest-05")]
    [InlineData("GUEST-05")]
    [InlineData("gUesT-05")]
    public async Task AuthenticateWithSRP_CaseInsensitiveUsername_LogsInSuccessfully(string loginAttemptUsername)

    {
        WebApplicationFactory<KONGORAssemblyMarker> webApplicationFactory = fixture.Factory;

        SRPAuthenticationService srpAuthenticationService = new(webApplicationFactory);


        string canonicalAccountName = $"GuestUser_{Guid.NewGuid():N}";

        string email = $"{canonicalAccountName.ToLowerInvariant()}@kongor.com";

        const string password = "Password123!";


        (Account account, string _) =
            await srpAuthenticationService.CreateAccountWithSRPCredentials(email, canonicalAccountName, password);


        // Transform the canonical account name to the casing pattern under test

        string testedLoginUsername = loginAttemptUsername switch

        {
            "guest-05" => canonicalAccountName.ToLowerInvariant(),

            "GUEST-05" => canonicalAccountName.ToUpperInvariant(),

            "gUesT-05" => string.Create(canonicalAccountName.Length, canonicalAccountName, (span, name) =>

            {
                for (int i = 0; i < name.Length; i++)
                    span[i] = i % 2 == 0 ? char.ToLowerInvariant(name[i]) : char.ToUpperInvariant(name[i]);
            }),

            _ => loginAttemptUsername
        };


        SRPAuthenticationData authData =
            await srpAuthenticationService.PerformFullAuthentication(account, password, testedLoginUsername);


        Assert.True(authData.Success, $"Failed to authenticate with login username: {testedLoginUsername}");

        Assert.NotNull(authData.Cookie);

        Assert.Equal(canonicalAccountName, authData.Name);
    }
}
