// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

global using Xunit;
global using System.Collections.Concurrent;
global using System.Text;
global using System.Text.RegularExpressions;
global using MERRICK.DatabaseContext.Constants;
global using MERRICK.DatabaseContext.Entities.Core;
global using MERRICK.DatabaseContext.Entities.Utility;
global using NEXUS.Contracts.Admin;
global using Manifest = NEXUS.Contracts.Cdn.Manifest;
global using ManifestEntry = NEXUS.Contracts.Cdn.ManifestEntry;
global using ReplayRenderCallbackDTO = NEXUS.Contracts.Replay.ReplayRenderCallbackDto;
global using NEXUS.Contracts.ControlPlane;
global using NEXUS.Contracts.Domain;
global using NEXUS.Contracts.Fleet;
global using NEXUS.Contracts.Replay;
global using NEXUS.Contracts.Security;
global using NEXUS.Contracts.Telemetry;
global using ASPIRE.Common.Models.Replay;
global using MERRICK.DatabaseContext.Persistence;
global using MERRICK.DatabaseContext.Repositories;
global using NEXUS.Contracts.Data;
global using Microsoft.EntityFrameworkCore;
global using Microsoft.Extensions.DependencyInjection;
global using Microsoft.Extensions.Logging;
global using Microsoft.Extensions.Options;
global using ASPIRE.Common.Serialization.PHP;
global using ASPIRE.Common.DTOs;
global using ASPIRE.Tests.Infrastructure;
global using Microsoft.AspNetCore.Mvc;
global using ZORGATH.WebPortal.API.Controllers;
