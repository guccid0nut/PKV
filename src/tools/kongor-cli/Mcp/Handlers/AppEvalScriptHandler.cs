// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

#if !PLAYTEST_BUILD
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kongor.Cli.Mcp.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using NEXUS.Contracts.Domain.Identifiers;
using StackExchange.Redis;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// Global context provided to scripts executed dynamically via Roslyn.
/// </summary>
public sealed class ScriptHostGlobals
{
    public object? Db { get; set; }
    public IDatabase? Redis { get; set; }
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
    public Action<object?> Print { get; set; } = _ => { };
    public ConcurrentDictionary<string, object?> Variables { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// MCP Tool Handler for 'kongor_app_eval' (aliases: 'app_eval_script', 'kongor_app_eval_script', 'kongor_eval').
/// Uses Roslyn CSharpCompilation directly to evaluate dynamic C# expressions in-memory
/// with injected DbContext, Redis, time provider, and persistent session variables.
/// </summary>
public sealed class AppEvalScriptHandler
{
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, object?>> s_sessionVariables = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Lazy<List<MetadataReference>> s_defaultReferences = new(() =>
    {
        var references = new List<MetadataReference>();
        var addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Ensure key anchor types from contract & domain assemblies are statically referenced
        Type[] anchorTypes =
        [
            typeof(object),
            typeof(Enumerable),
            typeof(JsonSerializer),
            typeof(Task),
            typeof(AccountId),
            typeof(MatchId),
            typeof(UserId),
            typeof(ClanId),
            typeof(IConnectionMultiplexer),
            typeof(ScriptHostGlobals),
            typeof(AppEvalScriptHandler)
        ];

        foreach (Type anchor in anchorTypes)
        {
            try
            {
                string loc = anchor.Assembly.Location;
                if (!string.IsNullOrEmpty(loc) && File.Exists(loc) && addedPaths.Add(loc))
                {
                    references.Add(MetadataReference.CreateFromFile(loc));
                }
            }
            catch { }
        }

        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic) continue;
            try
            {
                string loc = asm.Location;
                if (!string.IsNullOrEmpty(loc) && File.Exists(loc) && addedPaths.Add(loc))
                {
                    references.Add(MetadataReference.CreateFromFile(loc));
                }
            }
            catch { }
        }

        // Scan runtime contract assemblies (NEXUS.*.dll) and related libraries from AppContext.BaseDirectory
        try
        {
            string baseDir = AppContext.BaseDirectory;
            if (Directory.Exists(baseDir))
            {
                foreach (string dll in Directory.EnumerateFiles(baseDir, "*.dll"))
                {
                    string fileName = Path.GetFileName(dll);
                    if (fileName.StartsWith("NEXUS.", StringComparison.OrdinalIgnoreCase) ||
                        fileName.StartsWith("Kongor.", StringComparison.OrdinalIgnoreCase) ||
                        fileName.StartsWith("ASPIRE.", StringComparison.OrdinalIgnoreCase) ||
                        fileName.StartsWith("MERRICK.", StringComparison.OrdinalIgnoreCase) ||
                        fileName.StartsWith("WILLOWMAKER.", StringComparison.OrdinalIgnoreCase) ||
                        fileName.StartsWith("COMPEL.", StringComparison.OrdinalIgnoreCase) ||
                        fileName.StartsWith("TRANSMUTANSTEIN.", StringComparison.OrdinalIgnoreCase) ||
                        fileName.StartsWith("ZORGATH.", StringComparison.OrdinalIgnoreCase))
                    {
                        if (addedPaths.Add(dll))
                        {
                            references.Add(MetadataReference.CreateFromFile(dll));
                        }
                    }
                }
            }
        }
        catch { }

        string runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location) ?? string.Empty;
        string[] coreLibs =
        [
            "System.Runtime.dll",
            "System.Collections.dll",
            "System.Threading.Tasks.dll",
            "System.Linq.dll",
            "System.Text.Json.dll"
        ];

        foreach (string lib in coreLibs)
        {
            string p = Path.Combine(runtimeDir, lib);
            if (File.Exists(p) && addedPaths.Add(p))
            {
                references.Add(MetadataReference.CreateFromFile(p));
            }
        }

        return references;
    });

    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logger;
    private readonly object? _databaseContext;
    private readonly IDatabase? _redisDatabase;

    public AppEvalScriptHandler(
        TimeProvider? timeProvider = null,
        Action<string>? logger = null,
        object? databaseContext = null,
        IDatabase? redisDatabase = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _databaseContext = databaseContext;
        _redisDatabase = redisDatabase;
    }

    private void Log(string message) => _logger?.Invoke($"[AppEvalScript] {message}");

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string? script = null;
        if (arguments.TryGetProperty("script", out JsonElement scriptElem) && !string.IsNullOrWhiteSpace(scriptElem.GetString()))
        {
            script = scriptElem.GetString()!.Trim();
        }
        else if (arguments.TryGetProperty("code", out JsonElement codeElem) && !string.IsNullOrWhiteSpace(codeElem.GetString()))
        {
            script = codeElem.GetString()!.Trim();
        }
        else if (arguments.TryGetProperty("expression", out JsonElement exprElem) && !string.IsNullOrWhiteSpace(exprElem.GetString()))
        {
            script = exprElem.GetString()!.Trim();
        }

        if (string.IsNullOrWhiteSpace(script))
        {
            return ToolCallResult.Error("Missing required parameter 'script' (or 'code' / 'expression'). Please provide C# code to evaluate.");
        }

        string sessionId = "default";
        if (arguments.TryGetProperty("sessionId", out JsonElement sElem) && !string.IsNullOrWhiteSpace(sElem.GetString()))
        {
            sessionId = sElem.GetString()!.Trim();
        }

        bool resetSession = false;
        if (arguments.TryGetProperty("resetSession", out JsonElement rElem))
        {
            resetSession = rElem.GetBoolean();
        }

        int timeoutMs = 15000;
        if (arguments.TryGetProperty("timeoutMs", out JsonElement tElem) && tElem.TryGetInt32(out int t) && t > 0)
        {
            timeoutMs = Math.Clamp(t, 100, 60000);
        }

        if (resetSession)
        {
            s_sessionVariables.TryRemove(sessionId, out _);
            Log($"Reset script session '{sessionId}'");
        }

        ConcurrentDictionary<string, object?> variables = s_sessionVariables.GetOrAdd(
            sessionId,
            _ => new ConcurrentDictionary<string, object?>(StringComparer.OrdinalIgnoreCase));

        List<string> printedOutput = [];
        ScriptHostGlobals globals = new()
        {
            Db = _databaseContext,
            Redis = _redisDatabase,
            TimeProvider = _timeProvider,
            Print = obj =>
            {
                string s = obj?.ToString() ?? "null";
                printedOutput.Add(s);
                Log($"[Print] {s}");
            },
            Variables = variables
        };

        Stopwatch sw = Stopwatch.StartNew();
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMs);

        try
        {
            string classId = Guid.NewGuid().ToString("N");
            string userCode = script.Trim();
            bool isStatementOrBlock = userCode.Contains(';') ||
                                      userCode.EndsWith('}') ||
                                      userCode.StartsWith("while", StringComparison.OrdinalIgnoreCase) ||
                                      userCode.StartsWith("for", StringComparison.OrdinalIgnoreCase) ||
                                      userCode.StartsWith("foreach", StringComparison.OrdinalIgnoreCase) ||
                                      userCode.StartsWith("if", StringComparison.OrdinalIgnoreCase) ||
                                      userCode.StartsWith("return ", StringComparison.OrdinalIgnoreCase) ||
                                      userCode.StartsWith("var ", StringComparison.OrdinalIgnoreCase);

            if (!isStatementOrBlock)
            {
                userCode = $"return ({userCode});";
            }

            string sourceCode = $$"""
            using System;
            using System.Collections;
            using System.Collections.Concurrent;
            using System.Collections.Generic;
            using System.IO;
            using System.Linq;
            using System.Text;
            using System.Text.Json;
            using System.Threading;
            using System.Threading.Tasks;
            using NEXUS.Contracts;
            using NEXUS.Contracts.Domain;
            using NEXUS.Contracts.Domain.Identifiers;
            using NEXUS.Contracts.Ipc;
            using NEXUS.Contracts.Economy;
            using NEXUS.Contracts.Telemetry;
            using StackExchange.Redis;
            using Kongor.Cli.Mcp.Handlers;

            namespace Kongor.Dynamic;

            public static class DynamicExecutor_{{classId}}
            {
                public static async Task<object?> ExecuteAsync(ScriptHostGlobals globals, CancellationToken cancellationToken)
                {
                    Action<object?> Print = globals.Print;
                    var Db = globals.Db;
                    var Redis = globals.Redis;
                    var TimeProvider = globals.TimeProvider;
                    var Variables = globals.Variables;

                    Func<Task<object?>> __runFunc = async () =>
                    {
                        #line 1 "script"
                        {{userCode}}
                        #line default
                        return null;
                    };

                    return await __runFunc().ConfigureAwait(false);
                }
            }
            """;

            var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode, cancellationToken: timeoutCts.Token);
            var compilationOptions = new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Debug,
                warningLevel: 4)
                .WithSpecificDiagnosticOptions(new Dictionary<string, ReportDiagnostic>
                {
                    ["CS0162"] = ReportDiagnostic.Suppress, // Unreachable code
                    ["CS1998"] = ReportDiagnostic.Suppress  // Async method lacks await
                });

            var compilation = CSharpCompilation.Create(
                $"DynamicScript_{classId}",
                syntaxTrees: [syntaxTree],
                references: s_defaultReferences.Value,
                options: compilationOptions);

            using MemoryStream peStream = new();
            EmitResult emitResult = compilation.Emit(peStream, cancellationToken: timeoutCts.Token);

            if (!emitResult.Success)
            {
                sw.Stop();
                var diagnostics = emitResult.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Select(d => new
                    {
                        id = d.Id,
                        severity = d.Severity.ToString(),
                        message = d.GetMessage(),
                        line = d.Location.GetLineSpan().StartLinePosition.Line + 1,
                        column = d.Location.GetLineSpan().StartLinePosition.Character + 1
                    }).ToList();

                Log($"Compilation error ({diagnostics.Count} diagnostics): {string.Join("; ", diagnostics.Select(d => d.message))}");

                return ToolCallResult.Json(new
                {
                    status = "compilation_error",
                    success = false,
                    sessionId,
                    error = "Script compilation failed",
                    diagnostics,
                    executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                }, isError: true);
            }

            peStream.Seek(0, SeekOrigin.Begin);
            System.Runtime.Loader.AssemblyLoadContext alc = new($"ScriptALC_{classId}", isCollectible: true);
            try
            {
                Assembly compiledAssembly = alc.LoadFromStream(peStream);
                Type? runnerType = compiledAssembly.GetType($"Kongor.Dynamic.DynamicExecutor_{classId}");
                MethodInfo? executeMethod = runnerType?.GetMethod("ExecuteAsync", BindingFlags.Public | BindingFlags.Static);

                if (executeMethod == null)
                {
                    throw new InvalidOperationException("Failed to locate ExecuteAsync in dynamic script assembly.");
                }

                Task<object?> executionTask = Task.Run(async () =>
                {
                    var task = (Task<object?>)executeMethod.Invoke(null, [globals, timeoutCts.Token])!;
                    return await task.ConfigureAwait(false);
                });

                Task completedTask = await Task.WhenAny(executionTask, Task.Delay(timeoutMs, cancellationToken)).ConfigureAwait(false);

                if (completedTask != executionTask)
                {
                    sw.Stop();
                    return ToolCallResult.Json(new
                    {
                        status = "timeout",
                        success = false,
                        sessionId,
                        error = $"Script execution timed out after {timeoutMs} ms.",
                        executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                        timestamp = _timeProvider.GetUtcNow().ToString("o")
                    }, isError: true);
                }

                object? rawValue = await executionTask.ConfigureAwait(false);
                sw.Stop();

                object? formattedValue = FormatResult(rawValue);

                return ToolCallResult.Json(new
                {
                    status = "success",
                    success = true,
                    sessionId,
                    returnValue = formattedValue,
                    returnType = rawValue?.GetType().FullName ?? "null",
                    output = printedOutput,
                    variableCount = variables.Count,
                    executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                });
            }
            finally
            {
                alc.Unload();
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            return ToolCallResult.Json(new
            {
                status = "timeout",
                success = false,
                sessionId,
                error = $"Script execution timed out after {timeoutMs} ms.",
                executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            }, isError: true);
        }
        catch (Exception ex)
        {
            sw.Stop();
            Log($"Runtime error: {ex}");
            return ToolCallResult.Json(new
            {
                status = "runtime_error",
                success = false,
                sessionId,
                error = ex.InnerException?.Message ?? ex.Message,
                stackTrace = ex.StackTrace,
                executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            }, isError: true);
        }
    }

    private static object? FormatResult(object? value)
    {
        if (value == null) return null;
        if (value is string or int or long or double or float or bool or decimal or Guid or AccountId or MatchId or UserId or ClanId)
        {
            return value;
        }

        try
        {
            string json = JsonSerializer.Serialize(value, new JsonSerializerOptions
            {
                WriteIndented = false,
                ReferenceHandler = ReferenceHandler.IgnoreCycles
            });
            return JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch
        {
            return value.ToString();
        }
    }
}
#endif
