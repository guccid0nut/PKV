// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

global using System.ComponentModel.DataAnnotations;
global using System.ComponentModel.DataAnnotations.Schema;
global using System.Diagnostics;
global using System.Linq.Expressions;
global using System.Reflection;
global using System.Runtime.CompilerServices;
global using System.Security.Claims;
global using System.Text.Json;
global using ASPIRE.Common.Communication;
global using ASPIRE.Common.Constants;
global using ASPIRE.Common.ServiceDefaults;
global using NEXUS.Contracts.Admin;
global using NEXUS.Contracts.Domain;
global using MERRICK.DatabaseContext.Data;
global using MERRICK.DatabaseContext.Entities.Core;
global using MERRICK.DatabaseContext.Entities.Game;
global using MERRICK.DatabaseContext.Entities.Relational;
global using MERRICK.DatabaseContext.Entities.Statistics;
global using MERRICK.DatabaseContext.Entities.Store;
global using MERRICK.DatabaseContext.Entities.Utility;
global using MERRICK.DatabaseContext.Handlers;
global using MERRICK.DatabaseContext.Persistence;
global using MERRICK.DatabaseContext.Services;
global using Microsoft.AspNetCore.Builder;
global using Microsoft.AspNetCore.Http;
global using Microsoft.EntityFrameworkCore;
global using Microsoft.EntityFrameworkCore.Diagnostics;
global using Microsoft.EntityFrameworkCore.Metadata.Builders;
global using Microsoft.EntityFrameworkCore.Storage;
global using Microsoft.Extensions.Configuration;
global using Microsoft.Extensions.DependencyInjection;
global using Microsoft.Extensions.Diagnostics.HealthChecks;
global using Microsoft.Extensions.Hosting;
global using Microsoft.Extensions.Logging;
