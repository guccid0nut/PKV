// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

global using System;
global using System.Buffers;
global using System.Collections.Concurrent;
global using System.Collections.Generic;
global using System.Collections.ObjectModel;
global using System.Diagnostics;
global using System.Diagnostics.CodeAnalysis;
global using System.IO;
global using System.IO.Compression;
global using System.Linq;
global using System.Net;
global using System.Net.Http;
global using System.Runtime.InteropServices;
global using System.Runtime.InteropServices.ComTypes;
global using System.Text;
global using System.Text.Json;
global using System.Text.Json.Serialization;
global using System.Text.RegularExpressions;
global using System.Threading;
global using System.Threading.Tasks;
global using Avalonia;
global using Avalonia.Controls;
global using Avalonia.Controls.ApplicationLifetimes;
global using Avalonia.Input;
global using Avalonia.Interactivity;
global using Avalonia.Markup.Xaml;
global using Avalonia.Media.Imaging;
global using Avalonia.Platform.Storage;
global using Avalonia.Threading;
global using Avalonia.VisualTree;
global using CommunityToolkit.Mvvm.ComponentModel;
global using CommunityToolkit.Mvvm.Input;
global using Microsoft.Extensions.Logging;
global using Manifest = NEXUS.Contracts.Cdn.Manifest;
global using SynchronisationEvent = NEXUS.Contracts.Cdn.SynchronisationEvent;
global using SynchronisationEventKind = NEXUS.Contracts.Cdn.SynchronisationEventKind;
global using SynchronisationPlan = NEXUS.Contracts.Cdn.SynchronisationPlan;
global using SynchronisationFailure = NEXUS.Contracts.Cdn.SynchronisationFailure;
global using SynchronisationSummary = NEXUS.Contracts.Cdn.SynchronisationSummary;
global using WILLOWMAKER.Core.Constants;
global using WILLOWMAKER.Core.Enumerations;
global using WILLOWMAKER.Core.Models;
global using WILLOWMAKER.Core.Services;
global using WILLOWMAKER.Core.Services.IPC;
global using WILLOWMAKER.Core.Services.Models;
global using WILLOWMAKER.Core.Utilities;
global using ASPIRE.Common.Utilities;
global using WILLOWMAKER.Core.ViewModels;
global using WILLOWMAKER.Core.Views;
