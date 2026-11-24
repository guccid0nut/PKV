// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

global using COMPEL;
global using COMPEL.Configuration;
global using COMPEL.Endpoints;
global using COMPEL.Internals;
global using COMPEL.Serialisation;
global using COMPEL.Services.ContentBroker;
global using COMPEL.Services.Maintenance;
global using COMPEL.Services.Ping;
global using COMPEL.Services.Proxy;
global using COMPEL.Services.Supervision;
global using COMPEL.Services.Telemetry;
global using ASPIRE.Common.Utilities;
global using NEXUS.Contracts.Cdn;
global using Microsoft.AspNetCore.Diagnostics.HealthChecks;
global using Microsoft.Extensions.Diagnostics.HealthChecks;
global using Microsoft.Extensions.FileSystemGlobbing;
global using Microsoft.Extensions.Options;
global using System.Buffers.Binary;
global using System.Collections.Concurrent;
global using System.Diagnostics;
global using System.Net;
global using System.Net.NetworkInformation;
global using System.Net.Sockets;
global using System.Security.Cryptography;
global using System.Text;
global using System.Text.Encodings.Web;
global using System.Text.Json;
global using System.Text.Json.Serialization;
