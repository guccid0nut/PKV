// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

global using System.Buffers;
global using System.Buffers.Binary;
global using System.Collections.Concurrent;
global using System.Collections.Immutable;
global using System.Diagnostics;
global using System.Net;
global using System.Net.Sockets;
global using System.Reflection;
global using System.Runtime.CompilerServices;
global using System.Text;
global using ASPIRE.Common.Constants;
global using ASPIRE.Common.Extensions.Cryptography;
global using ASPIRE.Common.ServiceDefaults;
global using ASPIRE.Common.Configuration.Matchmaking;
global using ASPIRE.Common.Extensions.Cache;
global using ASPIRE.Common.Handlers.SRP;
global using ASPIRE.Common.Models.ServerManagement;
global using MERRICK.DatabaseContext.Entities.Core;
global using MERRICK.DatabaseContext.Entities.Relational;
global using NEXUS.Contracts.Domain;
global using NEXUS.Contracts.Telemetry;
global using MERRICK.DatabaseContext.Persistence;
global using Microsoft.EntityFrameworkCore;
global using Microsoft.Extensions.Diagnostics.HealthChecks;
global using OneOf;
global using StackExchange.Redis;
global using TRANSMUTANSTEIN.ChatServer.Attributes;
global using TRANSMUTANSTEIN.ChatServer.CommandProcessors.Matchmaking;
global using TRANSMUTANSTEIN.ChatServer.Contracts;
global using TRANSMUTANSTEIN.ChatServer.Domain.Communication;
global using TRANSMUTANSTEIN.ChatServer.Domain.Core;
global using TRANSMUTANSTEIN.ChatServer.Domain.Matchmaking;
global using TRANSMUTANSTEIN.ChatServer.Domain.Social;
global using TRANSMUTANSTEIN.ChatServer.Extensions;
global using TRANSMUTANSTEIN.ChatServer.Extensions.Protocol;
global using TRANSMUTANSTEIN.ChatServer.Internals;
global using TRANSMUTANSTEIN.ChatServer.Services;
global using TRANSMUTANSTEIN.ChatServer.Utilities;
