// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

global using COMPEL.Configuration;
global using COMPEL.Internals;
global using COMPEL.Services.Ping;
global using COMPEL.Services.Proxy;
global using COMPEL.Services.Supervision;
global using Microsoft.Extensions.Logging.Abstractions;
global using Microsoft.Extensions.Options;
global using System.Buffers.Binary;
global using System.Diagnostics;
global using System.Net;
global using System.Net.Sockets;
global using System.Text;
global using Xunit;
