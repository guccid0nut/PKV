// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

global using System.IdentityModel.Tokens.Jwt;
global using System.Runtime.CompilerServices;
global using System.Security.Claims;
global using System.Security.Cryptography;
global using System.Text;
global using System.Text.RegularExpressions;
global using System.Threading.RateLimiting;
global using ASPIRE.Common.Constants;
global using ASPIRE.Common.ServiceDefaults;
global using FluentValidation;
global using FluentValidation.Results;
global using MERRICK.DatabaseContext.Constants;
global using MERRICK.DatabaseContext.Entities.Core;
global using MERRICK.DatabaseContext.Entities.Utility;
global using MERRICK.DatabaseContext.Persistence;
global using Microsoft.AspNetCore.Authentication.JwtBearer;
global using Microsoft.AspNetCore.Authorization;
global using Microsoft.AspNetCore.HttpLogging;
global using Microsoft.AspNetCore.Identity;
global using Microsoft.AspNetCore.Mvc;
global using Microsoft.AspNetCore.RateLimiting;
global using Microsoft.EntityFrameworkCore;
global using Microsoft.Extensions.FileProviders;
global using Microsoft.Extensions.Options;
global using Microsoft.IdentityModel.Tokens;
global using Microsoft.OpenApi;
global using SecureRemotePassword;
global using ASPIRE.Common.DTOs;
global using NEXUS.Contracts.Admin;
global using NEXUS.Contracts.Domain;
global using NEXUS.Contracts.Fleet;
global using NEXUS.Contracts.Security;
global using ZORGATH.WebPortal.API.Constants;
global using ZORGATH.WebPortal.API.Extensions;
global using ZORGATH.WebPortal.API.Handlers;
global using ZORGATH.WebPortal.API.Helpers;
global using ZORGATH.WebPortal.API.Models.Configuration;
global using ZORGATH.WebPortal.API.Services;
global using ZORGATH.WebPortal.API.Services.Email;
global using ZORGATH.WebPortal.API.Validators;
