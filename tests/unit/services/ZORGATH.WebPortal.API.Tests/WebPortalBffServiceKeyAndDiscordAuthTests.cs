// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Threading.RateLimiting;
using DAWNBRINGER.WebPortal.UI.Controllers;
using DAWNBRINGER.WebPortal.UI.Hubs;
using DAWNBRINGER.WebPortal.UI.Infrastructure.Handlers;
using DAWNBRINGER.WebPortal.UI.Models;
using DAWNBRINGER.WebPortal.UI.Services;
using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Game;
using MERRICK.DatabaseContext.Entities.Lore;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NEXUS.Contracts.Admin;
using NEXUS.Contracts.Clients;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Domain;
using NEXUS.Contracts.Fleet;
using NEXUS.Contracts.Security;
using NEXUS.Contracts.Telemetry;
using Xunit;
using ZORGATH.WebPortal.API.Constants;
using ZORGATH.WebPortal.API.Controllers;
using ZORGATH.WebPortal.API.Features.Guides;
using ZORGATH.WebPortal.API.Features.Lore;
using ZORGATH.WebPortal.API.Infrastructure.Attributes;

namespace ZORGATH.WebPortal.API.Tests;

public sealed class WebPortalBffServiceKeyAndDiscordAuthTests
{
    private const string TestInternalKey = "test-internal-key-secret-2026";

    private static DefaultHttpContext CreateHttpContext(
        string environmentName = "Production",
        string? internalServiceKey = TestInternalKey,
        string? requestHeaderServiceKey = null,
        ClaimsPrincipal? user = null,
        IPAddress? remoteIp = null,
        Dictionary<string, string?>? extraConfig = null)
    {
        Dictionary<string, string?> configMap = new()
        {
            ["InternalServiceKey"] = internalServiceKey
        };

        if (extraConfig != null)
        {
            foreach (KeyValuePair<string, string?> kvp in extraConfig)
            {
                configMap[kvp.Key] = kvp.Value;
            }
        }

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configMap)
            .Build();

        Mock<IHostEnvironment> mockEnv = new();
        mockEnv.SetupGet(e => e.EnvironmentName).Returns(environmentName);

        ServiceCollection services = new();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(mockEnv.Object);
        services.AddLogging();
        services.AddControllersWithViews();

        DefaultHttpContext context = new()
        {
            RequestServices = services.BuildServiceProvider()
        };

        if (user != null)
        {
            context.User = user;
        }

        if (remoteIp != null)
        {
            context.Connection.RemoteIpAddress = remoteIp;
        }

        if (requestHeaderServiceKey != null)
        {
            context.Request.Headers[SecurityConstants.InternalServiceKeyHeader] = requestHeaderServiceKey;
        }

        return context;
    }

    private static ClaimsPrincipal CreateCookiePrincipal(string accountName, string? role = null)
    {
        List<Claim> claims =
        [
            new Claim(ClaimTypes.Name, accountName),
            new Claim(ClaimTypes.Email, "player@kongor.local")
        ];
        if (!string.IsNullOrWhiteSpace(role))
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies"));
    }

    private static bool HasRequireInternalServiceKey(MethodInfo actionMethod)
    {
        return actionMethod.GetCustomAttribute<RequireInternalServiceKeyAttribute>(inherit: true) != null ||
               actionMethod.DeclaringType?.GetCustomAttribute<RequireInternalServiceKeyAttribute>(inherit: true) != null;
    }

    [Fact]
    public async Task DirectDiscordCallback_WhenNotDevelopmentOrMockDisabled_BlocksUnauthenticatedAccountTakeover()
    {
        // protects: DAWNBRINGER AccountController.DirectDiscordCallback against unauthenticated Staff/Player account takeover via ?discordId=&email=
        // fails_when: DirectDiscordCallback calls RegisterOrLoginDiscordAsync or signs in a user outside explicit Development + Authentication:AllowMockDiscordAuth=true
        // seam: AccountController.DirectDiscordCallback + IUserApiClient + IHostEnvironment
        Mock<IUserApiClient> mockUserApi = new();
        Mock<IAdminApiClient> mockAdminApi = new();
        Mock<IHttpClientFactory> mockHttpFactory = new();
        Mock<IOutputCacheStore> mockCache = new();

        // 1. Production environment EVEN WITH AllowMockDiscordAuth=true MUST reject unauthenticated discordId takeover
        IConfiguration prodConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:AllowMockDiscordAuth"] = "true"
            })
            .Build();

        AccountController prodController = new(
            mockUserApi.Object,
            mockAdminApi.Object,
            mockHttpFactory.Object,
            NullLogger<AccountController>.Instance,
            mockCache.Object,
            prodConfig)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = CreateHttpContext(
                    environmentName: "Production",
                    remoteIp: IPAddress.Parse("203.0.113.55"),
                    extraConfig: new Dictionary<string, string?> { ["Authentication:AllowMockDiscordAuth"] = "true" })
            }
        };

        IActionResult prodResult = await prodController.DirectDiscordCallback(
            discordId: "999999999999999999",
            discordUsername: "SpoofedAdmin",
            email: "admin@kongor.local",
            discordAvatar: null,
            returnUrl: "/staff");

        prodResult.Should().BeOfType<StatusCodeResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        mockUserApi.Verify(
            x => x.RegisterOrLoginDiscordAsync(It.IsAny<DiscordAuthRequestDto>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // 2. Development environment with AllowMockDiscordAuth=false (default) MUST also reject unauthenticated discordId takeover
        IConfiguration devDefaultConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        AccountController devController = new(
            mockUserApi.Object,
            mockAdminApi.Object,
            mockHttpFactory.Object,
            NullLogger<AccountController>.Instance,
            mockCache.Object,
            devDefaultConfig)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = CreateHttpContext(
                    environmentName: "Development",
                    remoteIp: IPAddress.Loopback)
            }
        };

        IActionResult devResult = await devController.DirectDiscordCallback(
            discordId: "999999999999999999",
            discordUsername: "SpoofedDev",
            email: "admin@kongor.local");

        devResult.Should().BeOfType<StatusCodeResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        mockUserApi.Verify(
            x => x.RegisterOrLoginDiscordAsync(It.IsAny<DiscordAuthRequestDto>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // 3. Already-authenticated session fast-path (discordId == null) continues redirecting cleanly
        Mock<IUrlHelper> mockUrlHelper = new();
        mockUrlHelper.Setup(u => u.IsLocalUrl("/settings")).Returns(true);
        prodController.ControllerContext.HttpContext.User = CreateCookiePrincipal("TestPlayer");
        prodController.Url = mockUrlHelper.Object;

        IActionResult authFastPath = await prodController.DirectDiscordCallback(
            discordId: null,
            discordUsername: null,
            email: null,
            discordAvatar: null,
            returnUrl: "/settings");

        authFastPath.Should().BeOfType<LocalRedirectResult>()
            .Which.Url.Should().Be("/settings");
    }

    [Fact]
    public async Task HostCredentialGetEndpoints_NeverMutateOrResetPasswords_AndRegeneratePostsRequireCsrf()
    {
        // protects: Host credentials from destructive password resets on GET navigations/prefetches and CSRF-unprotected regeneration POSTs
        // fails_when: GetHostCredentials or RevealHostPassword accepts POST, calls ResetHostPasswordAsync on GET, or RegenerateHostPassword lacks [ValidateAntiForgeryToken]
        // seam: AccountController.GetHostCredentials / RevealHostPassword / RegenerateHostPassword / RegenerateHostAuthToken
        MethodInfo getCredsMethod = typeof(AccountController).GetMethod(nameof(AccountController.GetHostCredentials))!;
        getCredsMethod.GetCustomAttributes<HttpPostAttribute>().Should().BeEmpty(
            "GetHostCredentials must be strictly read-only GET and never accept POST");
        getCredsMethod.GetCustomAttributes<HttpGetAttribute>().Should().NotBeEmpty();

        MethodInfo regenPassMethod = typeof(AccountController).GetMethod(nameof(AccountController.RegenerateHostPassword))!;
        regenPassMethod.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>().Should().NotBeNull();

        MethodInfo regenTokenMethod = typeof(AccountController).GetMethod(nameof(AccountController.RegenerateHostAuthToken))!;
        regenTokenMethod.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>().Should().NotBeNull();

        Mock<IUserApiClient> mockUserApi = new();
        Mock<IAdminApiClient> mockAdminApi = new();
        Mock<IHttpClientFactory> mockHttpFactory = new();
        Mock<IOutputCacheStore> mockCache = new();
        IConfiguration config = new ConfigurationBuilder().Build();

        mockUserApi.Setup(u => u.GetAccountByNameAsync("TestPlayer", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountDto { ID = 42, UserID = 7, Name = "TestPlayer", IsMain = true });
        mockUserApi.Setup(u => u.GetHostApplicationAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync((HostApplicationDto?)null);

        AccountController controller = new(
            mockUserApi.Object,
            mockAdminApi.Object,
            mockHttpFactory.Object,
            NullLogger<AccountController>.Instance,
            mockCache.Object,
            config)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = CreateHttpContext(user: CreateCookiePrincipal("TestPlayer"))
            },
            Url = Mock.Of<IUrlHelper>()
        };

        IActionResult noAppResult = await controller.GetHostCredentials();
        mockAdminApi.Verify(
            a => a.ResetHostPasswordAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "GET host credentials must NEVER reset or mint passwords when no approved application exists");
        noAppResult.Should().BeOfType<NotFoundObjectResult>();

        // Approved host application with empty EncryptedHostPassword must ALSO never mutate on GET
        mockUserApi.Setup(u => u.GetHostApplicationAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostApplicationDto
            {
                ID = 10,
                UserID = 7,
                MainAccountID = 42,
                HostUsername = "TestPlayer_host",
                Status = HostApplicationStatus.Approved,
                EncryptedHostPassword = string.Empty
            });

        IActionResult emptyPassResult = await controller.RevealHostPassword();
        mockAdminApi.Verify(
            a => a.ResetHostPasswordAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "GET RevealHostPassword must NEVER call ResetHostPasswordAsync even when EncryptedHostPassword is empty");
        emptyPassResult.Should().BeOfType<JsonResult>();
    }

    [Fact]
    public async Task ChangePassword_WhenInGamePasswordAlreadySet_RequiresValidCurrentPassword()
    {
        // protects: AccountController.ChangePassword against silent password overwrite on stolen/unattended cookie sessions without CurrentPassword verification
        // fails_when: SetInGamePasswordAsync is invoked when HasInGamePasswordSet is true and CurrentPassword is missing or fails LogInUserAsync verification
        // seam: AccountController.ChangePassword + IUserApiClient.LogInUserAsync + IUserApiClient.SetInGamePasswordAsync
        Mock<IUserApiClient> mockUserApi = new();
        Mock<IAdminApiClient> mockAdminApi = new();
        Mock<IHttpClientFactory> mockHttpFactory = new();
        Mock<IOutputCacheStore> mockCache = new();
        IConfiguration config = new ConfigurationBuilder().Build();

        mockUserApi.Setup(u => u.GetUserByAccountNameAsync("TestPlayer", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserDto
            {
                ID = 77,
                EmailAddress = "player@kongor.local",
                HasInGamePasswordSet = true
            });

        AccountController controller = new(
            mockUserApi.Object,
            mockAdminApi.Object,
            mockHttpFactory.Object,
            NullLogger<AccountController>.Instance,
            mockCache.Object,
            config)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = CreateHttpContext(user: CreateCookiePrincipal("TestPlayer"))
            },
            Url = Mock.Of<IUrlHelper>()
        };

        string candidateSecret = Guid.NewGuid().ToString("N");
        string wrongPriorSecret = Guid.NewGuid().ToString("N");
        string validPriorSecret = Guid.NewGuid().ToString("N");

        // 1. Missing CurrentPassword when HasInGamePasswordSet == true MUST NOT overwrite password
        IActionResult missingCurrentResult = await controller.ChangePassword(new ChangePasswordViewModel
        {
            NewPassword = candidateSecret,
            ConfirmPassword = candidateSecret
        });

        missingCurrentResult.Should().BeOfType<RedirectToActionResult>();
        mockUserApi.Verify(
            u => u.SetInGamePasswordAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // 2. Wrong CurrentPassword failing LogInUserAsync MUST NOT overwrite password
        mockUserApi.Setup(u => u.LogInUserAsync(
                It.Is<LogInUserRequestDto>(r => r.Login == "TestPlayer" && r.Password == wrongPriorSecret),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((AuthenticationTokenResponseDto?)null);

        IActionResult wrongCurrentResult = await controller.ChangePassword(new ChangePasswordViewModel
        {
            CurrentPassword = wrongPriorSecret,
            NewPassword = candidateSecret,
            ConfirmPassword = candidateSecret
        });

        wrongCurrentResult.Should().BeOfType<RedirectToActionResult>();
        mockUserApi.Verify(
            u => u.SetInGamePasswordAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // 3. Valid CurrentPassword succeeds and invalidates the verification session token
        mockUserApi.Setup(u => u.LogInUserAsync(
                It.Is<LogInUserRequestDto>(r => r.Login == "TestPlayer" && r.Password == validPriorSecret),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthenticationTokenResponseDto("temp-verify-token-001", TimeProvider.System.GetUtcNow().AddMinutes(5)));

        IActionResult validCurrentResult = await controller.ChangePassword(new ChangePasswordViewModel
        {
            CurrentPassword = validPriorSecret,
            NewPassword = candidateSecret,
            ConfirmPassword = candidateSecret
        });

        validCurrentResult.Should().BeOfType<RedirectToActionResult>();
        mockUserApi.Verify(
            u => u.SetInGamePasswordAsync(77, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        mockUserApi.Verify(
            u => u.InvalidateTokenAsync("temp-verify-token-001", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ApplyHost_EnforcesCsrf_AndNeverFallsBackToAccountOneWhenApplicantMissing()
    {
        // protects: ApplyHost flow against CSRF and against AccountRepository's FirstOrDefaultAsync() Account #1 fallback
        // fails_when: AccountController.ApplyHost lacks [ValidateAntiForgeryToken] or UserApiController.ApplyHost creates an application attached to Account #1
        // seam: AccountController.ApplyHost + UserApiController.ApplyHost + AccountRepository (InMemory MerrickContext)
        MethodInfo webApplyHost = typeof(AccountController).GetMethod(nameof(AccountController.ApplyHost))!;
        webApplyHost.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>().Should().NotBeNull();

        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        User adminUser = new()
        {
            ID = 1,
            EmailAddress = "admin@kongor.local",
            SRPPasswordSalt = "s",
            SRPPasswordHash = "h",
            PBKDF2PasswordHash = "p"
        };
        Account accountOne = new()
        {
            ID = 1,
            UserID = 1,
            User = adminUser,
            Name = "AdminAccountOne",
            Type = AccountType.Staff,
            IsMain = true
        };
        db.Users.Add(adminUser);
        db.Accounts.Add(accountOne);
        await db.SaveChangesAsync();

        AccountRepository accountRepo = new(db, NullLogger<AccountRepository>.Instance);
        Mock<IInventoryRepository> mockInventoryRepo = new();
        Mock<ISessionRepository> mockSessionRepo = new();

        UserApiController apiController = new(accountRepo, mockInventoryRepo.Object, mockSessionRepo.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = CreateHttpContext(requestHeaderServiceKey: TestInternalKey)
            }
        };

        // Submitting ServerNamePrefix = "Community Server" without an authenticated caller account MUST NOT hijack Account #1!
        IActionResult badResult = await apiController.ApplyHost(new ApplyHostRequestDto
        {
            ServerNamePrefix = "Community Server",
            Location = "USE",
            Instances = 2
        });

        badResult.Should().NotBeOfType<OkObjectResult>(
            "ApplyHost with a display ServerNamePrefix and no verified applicant account must never fall back to Account #1");
        db.HostApplications.Should().BeEmpty();

        // Verified applicant forwarded via X-Authenticated-Account binds to TestPlayer (#2), NEVER Account #1, while preserving ServerNamePrefix
        User applicantUser = new()
        {
            ID = 2,
            EmailAddress = "player@kongor.local",
            SRPPasswordSalt = "s2",
            SRPPasswordHash = "h2",
            PBKDF2PasswordHash = "p2"
        };
        Account applicantAccount = new()
        {
            ID = 2,
            UserID = 2,
            User = applicantUser,
            Name = "TestPlayer",
            Type = AccountType.Normal,
            IsMain = true
        };
        db.Users.Add(applicantUser);
        db.Accounts.Add(applicantAccount);
        await db.SaveChangesAsync();

        DefaultHttpContext verifiedBffCtx = CreateHttpContext(requestHeaderServiceKey: TestInternalKey);
        verifiedBffCtx.Request.Headers[InternalServiceAuthHandler.AuthenticatedAccountHeader] = "TestPlayer";
        UserApiController verifiedController = new(accountRepo, mockInventoryRepo.Object, mockSessionRepo.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = verifiedBffCtx
            }
        };

        IActionResult goodResult = await verifiedController.ApplyHost(new ApplyHostRequestDto
        {
            ServerNamePrefix = "Community Server",
            Location = "EUW",
            Instances = 3
        });

        goodResult.Should().BeOfType<OkObjectResult>();
        HostApplication created = db.HostApplications.Should().ContainSingle().Subject;
        created.MainAccountID.Should().Be(2);
        created.ServerNamePrefix.Should().Be("Community Server");
    }

    [Fact]
    public async Task ZorgathBffControllers_RequireInternalServiceKeyOnAllSensitiveAndMutatingEndpoints()
    {
        // protects: ZORGATH internal BFF endpoints (/api/users, /api/store, /api/lore, /api/guides, /api/stats) from direct unauthenticated network access
        // fails_when: Any sensitive/mutating action lacks [RequireInternalServiceKey] or RequireInternalServiceKeyAttribute permits missing/invalid headers
        // seam: Controller reflection metadata + RequireInternalServiceKeyAttribute.OnActionExecutionAsync
        MethodInfo[] userActions = typeof(UserApiController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        userActions.Should().NotBeEmpty();
        foreach (MethodInfo action in userActions)
        {
            HasRequireInternalServiceKey(action).Should().BeTrue(
                $"UserApiController.{action.Name} must enforce [RequireInternalServiceKey]");
        }

        MethodInfo[] storeActions = typeof(StoreApiController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        storeActions.Should().NotBeEmpty();
        foreach (MethodInfo action in storeActions)
        {
            HasRequireInternalServiceKey(action).Should().BeTrue(
                $"StoreApiController.{action.Name} must enforce [RequireInternalServiceKey]");
        }

        string[] protectedLoreActions =
        [
            nameof(LoreApiController.RateComic),
            nameof(LoreApiController.GetUserRating),
            nameof(LoreApiController.GetStaffMetrics),
            nameof(LoreApiController.SaveComic),
            nameof(LoreApiController.DeleteComic),
            nameof(LoreApiController.SaveArc),
            nameof(LoreApiController.DeleteArc)
        ];
        foreach (string actionName in protectedLoreActions)
        {
            MethodInfo method = typeof(LoreApiController).GetMethod(actionName)!;
            HasRequireInternalServiceKey(method).Should().BeTrue(
                $"LoreApiController.{actionName} must enforce [RequireInternalServiceKey]");
        }

        string[] protectedGuideActions =
        [
            nameof(GuideApiController.SaveGuide),
            nameof(GuideApiController.DeleteGuide),
            nameof(GuideApiController.VoteGuide),
            nameof(GuideApiController.TogglePrivacy),
            nameof(GuideApiController.ToggleOfficial),
            nameof(GuideApiController.ToggleDefault),
            nameof(GuideApiController.ToggleFavorite),
            nameof(GuideApiController.VoteHeroDifficulty)
        ];
        foreach (string actionName in protectedGuideActions)
        {
            MethodInfo method = typeof(GuideApiController).GetMethod(actionName)!;
            HasRequireInternalServiceKey(method).Should().BeTrue(
                $"GuideApiController.{actionName} must enforce [RequireInternalServiceKey]");
        }

        string[] protectedStatsActions =
        [
            nameof(PlayerStatsApiController.GetSubmittedMatchIds),
            nameof(PlayerStatsApiController.GetMatchEvidence),
            nameof(PlayerStatsApiController.SaveMatchEvidence),
            nameof(PlayerStatsApiController.SubmitPlayerReport)
        ];
        foreach (string actionName in protectedStatsActions)
        {
            MethodInfo method = typeof(PlayerStatsApiController).GetMethod(actionName)!;
            HasRequireInternalServiceKey(method).Should().BeTrue(
                $"PlayerStatsApiController.{actionName} must enforce [RequireInternalServiceKey]");
        }

        // Verify filter execution blocks unauthenticated direct request (401)
        RequireInternalServiceKeyAttribute filter = new();
        DefaultHttpContext unauthenticatedCtx = CreateHttpContext(requestHeaderServiceKey: null);
        ActionContext actionContext = new(unauthenticatedCtx, new RouteData(), new ControllerActionDescriptor());
        ActionExecutingContext executingContext = new(
            actionContext,
            [],
            new Dictionary<string, object?>(),
            controller: new object());

        bool nextCalled = false;
        await filter.OnActionExecutionAsync(executingContext, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(actionContext, [], new object()));
        });

        nextCalled.Should().BeFalse();
        executingContext.Result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task LoreAndGuideControllers_RejectSpoofedIsStaffQueryWithoutInternalServiceKey_AndClampPagination()
    {
        // protects: LoreApiController and GuideApiController against ?isStaff=true / ?includeUnpublished=true query spoofing and unbounded pageSize DoS
        // fails_when: Unauthenticated caller receives unpublished comics/private guides via query params or GuideApiController returns > 100 items per page
        // seam: LoreApiController + GuideApiController + InMemory MerrickContext
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();

        LoreArc secretArc = new()
        {
            Title = "Unpublished Arc",
            Slug = "unpublished-arc",
            Description = "Draft",
            OrderIndex = 1,
            IsPublished = false
        };
        db.LoreArcs.Add(secretArc);
        await db.SaveChangesAsync();

        LoreComic secretComic = new()
        {
            ArcID = secretArc.ID,
            Title = "Draft Comic",
            Slug = "draft-comic",
            Synopsis = "Top secret",
            CoverImageUrl = "/draft.webp",
            IsPublished = false
        };
        db.LoreComics.Add(secretComic);

        User authorUser = new()
        {
            EmailAddress = "author@kongor.local",
            SRPPasswordSalt = "s",
            SRPPasswordHash = "h",
            PBKDF2PasswordHash = "p"
        };
        Account authorAccount = new()
        {
            Name = "TestPlayer",
            User = authorUser,
            IsMain = true
        };
        db.Users.Add(authorUser);
        db.Accounts.Add(authorAccount);

        // Non-featured guide only visible to Staff when IsStaff == true
        db.HeroGuides.Add(new HeroGuide
        {
            Name = "Unfeatured Staff Review Guide",
            HeroIdentifier = "Hero_Pyromancer",
            HeroName = "Pyromancer",
            TargetMap = "caldavar",
            Author = authorAccount,
            Featured = false,
            Public = true,
            Rating = 4.0f
        });

        for (int i = 0; i < 135; i++)
        {
            db.HeroGuides.Add(new HeroGuide
            {
                Name = $"Featured Guide {i:D3}",
                HeroIdentifier = "Hero_Devourer",
                HeroName = "Devourer",
                TargetMap = "caldavar",
                Author = authorAccount,
                Featured = true,
                Public = true,
                Rating = 5.0f
            });
        }

        await db.SaveChangesAsync();

        // 1. LoreApiController without X-Internal-Service-Key MUST ignore spoofed includeUnpublished=true and isStaff=true
        LoreApiController loreController = new(new LocalLoreMediator(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = CreateHttpContext(requestHeaderServiceKey: null)
            }
        };

        OkObjectResult arcsResult = (await loreController.GetArcs(includeUnpublished: true)).Should().BeOfType<OkObjectResult>().Subject;
        arcsResult.Value.Should().BeAssignableTo<List<LoreArcDto>>().Which.Should().BeEmpty();

        IActionResult comicResult = await loreController.GetComic("draft-comic", isStaff: true);
        comicResult.Should().BeOfType<NotFoundResult>();

        // 2. GuideApiController without X-Internal-Service-Key MUST ignore spoofed isStaff=true AND clamp pageSize <= 100
        GuideApiController guideController = new(new LocalGuideMediator(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = CreateHttpContext(requestHeaderServiceKey: null)
            }
        };

        OkObjectResult staffSpoofGuides = (await guideController.GetGuides(hero: "Pyromancer", isStaff: true))
            .Should().BeOfType<OkObjectResult>().Subject;
        GuideSearchResultDto spoofDto = staffSpoofGuides.Value.Should().BeOfType<GuideSearchResultDto>().Subject;
        spoofDto.Guides.Should().BeEmpty("spoofed ?isStaff=true without valid X-Internal-Service-Key must not expose non-featured guides");

        OkObjectResult oversizedPage = (await guideController.GetGuides(hero: "Devourer", page: 1, pageSize: 50000))
            .Should().BeOfType<OkObjectResult>().Subject;
        GuideSearchResultDto clampedDto = oversizedPage.Value.Should().BeOfType<GuideSearchResultDto>().Subject;
        clampedDto.TotalCount.Should().Be(135);
        clampedDto.Guides.Count.Should().BeLessThanOrEqualTo(100);
    }

    [Fact]
    public async Task LiveMatchesHub_SubmitMatchTick_RejectsAnonymousCallers_AndAllowsStaffOrInternalServiceKey()
    {
        // protects: LiveMatchesHub.SubmitMatchTick against anonymous WebSocket injection of fake live match ticks
        // fails_when: Anonymous/non-staff caller without valid X-Internal-Service-Key invokes ILiveMatchService.ApplyServerTickAsync
        // seam: LiveMatchesHub.SubmitMatchTick + HubCallerContext + ILiveMatchService
        Mock<ILiveMatchService> mockLiveService = new();
        LiveServerTickPayloadDto tick = new() { MatchID = 9001, Map = "caldavar" };

        // 1. Anonymous caller without service key MUST be rejected with HubException
        DefaultHttpContext anonHttp = CreateHttpContext(requestHeaderServiceKey: null);
        LiveMatchesHub anonHub = CreateLiveMatchesHub(mockLiveService.Object, anonHttp, user: new ClaimsPrincipal(new ClaimsIdentity()));

        Func<Task> actAnon = () => anonHub.SubmitMatchTick(tick);
        await actAnon.Should().ThrowAsync<HubException>();
        mockLiveService.Verify(s => s.ApplyServerTickAsync(It.IsAny<LiveServerTickPayloadDto>(), It.IsAny<CancellationToken>()), Times.Never);

        // 2. Valid X-Internal-Service-Key caller succeeds
        DefaultHttpContext internalKeyHttp = CreateHttpContext(requestHeaderServiceKey: TestInternalKey);
        LiveMatchesHub serviceHub = CreateLiveMatchesHub(mockLiveService.Object, internalKeyHttp, user: new ClaimsPrincipal(new ClaimsIdentity()));

        await serviceHub.SubmitMatchTick(tick);
        mockLiveService.Verify(s => s.ApplyServerTickAsync(tick, It.IsAny<CancellationToken>()), Times.Once);

        // 3. Authenticated Staff caller succeeds
        DefaultHttpContext staffHttp = CreateHttpContext(requestHeaderServiceKey: null, user: CreateCookiePrincipal("StaffMod", "Administrator"));
        LiveMatchesHub staffHub = CreateLiveMatchesHub(mockLiveService.Object, staffHttp, user: CreateCookiePrincipal("StaffMod", "Administrator"));

        await staffHub.SubmitMatchTick(tick);
        mockLiveService.Verify(s => s.ApplyServerTickAsync(tick, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public void MiniGameRoomManagers_EnforceMaxActiveRoomsCapAgainstUnboundedAllocation()
    {
        // protects: DevoWarsRoomManager, MonkeyCheckersRoomManager, and ChessRoomManager against OOM from anonymous room-creation floods
        // fails_when: More than 256 concurrent rooms can be allocated by flooding distinct room codes
        // seam: DevoWarsRoomManager.JoinRoom + MonkeyCheckersRoomManager.JoinRoom + ChessRoomManager.JoinRoom
        DevoWarsRoomManager devoManager = new(NullLogger<DevoWarsRoomManager>.Instance);
        MonkeyCheckersRoomManager checkersManager = new(NullLogger<MonkeyCheckersRoomManager>.Instance);
        ChessRoomManager chessManager = new(NullLogger<ChessRoomManager>.Instance);

        int devoAccepted = 0;
        int checkersAccepted = 0;
        int chessAccepted = 0;

        for (int i = 0; i < 300; i++)
        {
            string code = $"RM{i:D4}";
            (_, int devoSlot, _) = devoManager.JoinRoom(code, $"conn-d-{i}", "TestPlayer");
            if (devoSlot > 0) devoAccepted++;

            (_, int checkersSlot, _) = checkersManager.JoinRoom(code, $"conn-m-{i}", "TestPlayer");
            if (checkersSlot > 0) checkersAccepted++;

            (_, int chessSlot, _) = chessManager.JoinRoom(code, $"conn-c-{i}", "TestPlayer");
            if (chessSlot > 0) chessAccepted++;
        }

        devoAccepted.Should().BeLessThanOrEqualTo(256);
        checkersAccepted.Should().BeLessThanOrEqualTo(256);
        chessAccepted.Should().BeLessThanOrEqualTo(256);
    }

    [Fact]
    public void StaffController_AllPostEndpoints_EnforceAntiforgeryAndForbidIgnoreAntiforgeryToken()
    {
        // protects: StaffController administrative mutations (bans, punishments, host approvals, announcements) against cross-site request forgery (CSRF)
        // fails_when: DirectPunish carries [IgnoreAntiforgeryToken] or any [HttpPost] action on StaffController lacks antiforgery validation metadata
        // seam: StaffController reflection attribute contract for ASP.NET Core antiforgery filters
        Type staffControllerType = typeof(StaffController);
        bool classHasAutoAntiforgery =
            staffControllerType.GetCustomAttribute<AutoValidateAntiforgeryTokenAttribute>(inherit: true) != null ||
            staffControllerType.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>(inherit: true) != null;

        MethodInfo[] postMethods = staffControllerType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpPostAttribute>().Any())
            .ToArray();

        postMethods.Should().NotBeEmpty();

        foreach (MethodInfo postMethod in postMethods)
        {
            postMethod.GetCustomAttribute<IgnoreAntiforgeryTokenAttribute>().Should().BeNull(
                $"StaffController.{postMethod.Name} must NEVER disable antiforgery protection with [IgnoreAntiforgeryToken]");

            bool methodHasAntiforgery =
                classHasAutoAntiforgery ||
                postMethod.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>() != null ||
                postMethod.GetCustomAttribute<AutoValidateAntiforgeryTokenAttribute>() != null;

            methodHasAntiforgery.Should().BeTrue(
                $"StaffController.{postMethod.Name} is an [HttpPost] mutation and must enforce antiforgery tokens");
        }
    }

    [Fact]
    public async Task TrustedProxyHeaders_AndBoundedRateLimiterPartitions_BlockSpoofedForwardedIpsAndMemoryExhaustion()
    {
        // protects: DAWNBRINGER -> ZORGATH rate-limiting from CF-Connecting-IP / X-Forwarded-For spoofing on untrusted sockets and unbounded partition OOM
        // fails_when: InternalServiceAuthHandler forwards spoofed headers from untrusted remote IPs, or RateLimiterPolicies accepts non-IP strings / > 4500 partitions
        // seam: InternalServiceAuthHandler + RateLimiterPolicies.CreateStrictPartition / CreateRelaxedPartition
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["InternalServiceKey"] = TestInternalKey
            })
            .Build();
        Mock<IHostEnvironment> mockEnv = new();
        mockEnv.SetupGet(e => e.EnvironmentName).Returns("Production");

        // 1. Untrusted external socket (203.0.113.77) spoofing CF-Connecting-IP and X-Forwarded-For MUST be ignored
        DefaultHttpContext untrustedCtx = new();
        untrustedCtx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.77");
        untrustedCtx.Request.Headers["CF-Connecting-IP"] = "198.51.100.1";
        untrustedCtx.Request.Headers["X-Forwarded-For"] = "198.51.100.2";

        Mock<IHttpContextAccessor> untrustedAccessor = new();
        untrustedAccessor.SetupGet(a => a.HttpContext).Returns(untrustedCtx);

        CapturingTerminalHandler captureHandler = new();
        InternalServiceAuthHandler untrustedHandler = new(
            config,
            untrustedAccessor.Object,
            NullLogger<InternalServiceAuthHandler>.Instance)
        {
            InnerHandler = captureHandler
        };
        using HttpMessageInvoker untrustedInvoker = new(untrustedHandler);
        using HttpRequestMessage outgoing1 = new(HttpMethod.Post, "http://zorgath/api/users/login");
        await untrustedInvoker.SendAsync(outgoing1, CancellationToken.None);

        captureHandler.LastForwardedFor.Should().Be("203.0.113.77",
            "spoofed CF-Connecting-IP / X-Forwarded-For from an untrusted remote socket must never override RemoteIpAddress");

        // 2. Trusted loopback reverse proxy (127.0.0.1) legitimately forwards validated client IP
        DefaultHttpContext loopbackProxyCtx = new();
        loopbackProxyCtx.Connection.RemoteIpAddress = IPAddress.Loopback;
        loopbackProxyCtx.Request.Headers["CF-Connecting-IP"] = "198.51.100.42";

        Mock<IHttpContextAccessor> loopbackAccessor = new();
        loopbackAccessor.SetupGet(a => a.HttpContext).Returns(loopbackProxyCtx);

        CapturingTerminalHandler loopbackCapture = new();
        InternalServiceAuthHandler loopbackHandler = new(
            config,
            loopbackAccessor.Object,
            NullLogger<InternalServiceAuthHandler>.Instance)
        {
            InnerHandler = loopbackCapture
        };
        using HttpMessageInvoker loopbackInvoker = new(loopbackHandler);
        using HttpRequestMessage outgoing2 = new(HttpMethod.Post, "http://zorgath/api/users/login");
        await loopbackInvoker.SendAsync(outgoing2, CancellationToken.None);

        loopbackCapture.LastForwardedFor.Should().Be("198.51.100.42");

        // 3. RateLimiterPolicies must reject malformed non-IP X-Forwarded-For values
        DefaultHttpContext malformedXffCtx = CreateHttpContext(
            requestHeaderServiceKey: TestInternalKey,
            remoteIp: IPAddress.Parse("10.0.0.5"));
        malformedXffCtx.Request.Headers["X-Forwarded-For"] = "spoofed-non-ip-garbage-value";

        RateLimitPartition<string> malformedPartition = RateLimiterPolicies.CreateStrictPartition(malformedXffCtx);
        malformedPartition.PartitionKey.Should().NotContain("spoofed-non-ip-garbage-value");

        // 4. RateLimiterPolicies must bound total partition key cardinality under a 5,000-IP flood
        HashSet<string> distinctPartitionKeys = new(StringComparer.Ordinal);
        for (int i = 0; i < 5000; i++)
        {
            int octet3 = (i / 250) % 255;
            int octet4 = (i % 250) + 1;
            DefaultHttpContext floodCtx = CreateHttpContext(
                requestHeaderServiceKey: TestInternalKey,
                remoteIp: IPAddress.Parse("10.0.0.5"));
            floodCtx.Request.Headers["X-Forwarded-For"] = $"198.18.{octet3}.{octet4}";

            RateLimitPartition<string> p = RateLimiterPolicies.CreateStrictPartition(floodCtx);
            distinctPartitionKeys.Add(p.PartitionKey);
        }

        distinctPartitionKeys.Count.Should().BeLessThanOrEqualTo(4352,
            "partition keys must be strictly bounded to prevent memory exhaustion under spoofed/rotated IP floods");
    }

    private static LiveMatchesHub CreateLiveMatchesHub(
        ILiveMatchService liveMatchService,
        HttpContext httpContext,
        ClaimsPrincipal user)
    {
        FeatureCollection features = new();
        Mock<IHttpContextFeature> mockHttpFeature = new();
        mockHttpFeature.SetupGet(f => f.HttpContext).Returns(httpContext);
        features.Set<IHttpContextFeature>(mockHttpFeature.Object);

        Mock<HubCallerContext> mockCallerContext = new();
        mockCallerContext.SetupGet(c => c.ConnectionId).Returns("conn-test-001");
        mockCallerContext.SetupGet(c => c.User).Returns(user);
        mockCallerContext.SetupGet(c => c.Features).Returns(features);
        mockCallerContext.SetupGet(c => c.ConnectionAborted).Returns(CancellationToken.None);

        Mock<IHubCallerClients<ILiveMatchesClient>> mockClients = new();
        Mock<ILiveMatchesClient> mockClientProxy = new();
        mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(mockClientProxy.Object);

        return new LiveMatchesHub(liveMatchService, NullLogger<LiveMatchesHub>.Instance)
        {
            Context = mockCallerContext.Object,
            Clients = mockClients.Object
        };
    }

    private sealed class CapturingTerminalHandler : HttpMessageHandler
    {
        public string? LastForwardedFor { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastForwardedFor = request.Headers.TryGetValues("X-Forwarded-For", out IEnumerable<string>? vals)
                ? vals.FirstOrDefault()
                : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
