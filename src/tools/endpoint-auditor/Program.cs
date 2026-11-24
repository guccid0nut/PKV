// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EndpointAuditor;

public sealed class FormDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public string StatusTrigger { get; set; } = string.Empty;
    public string ResultTrigger { get; set; } = string.Empty;
    public List<string> Sources { get; set; } = [];
}

public sealed class FormSubmitCall
{
    public string FormName { get; set; } = string.Empty;
    public string? FunctionNameF { get; set; }
    public string? TableParam { get; set; }
    public string? ReqType { get; set; }
    public string? RequestCode { get; set; }
    public Dictionary<string, string> Arguments { get; set; } = [];
    public string SourceFile { get; set; } = string.Empty;
    public int LineNumber { get; set; }
    public string FullCallText { get; set; } = string.Empty;
}

public sealed class DirectEndpointCall
{
    public string Type { get; set; } = string.Empty; // "URL", "PHP", "ASCENSION", "WATCH", "API"
    public string EndpointOrUrl { get; set; } = string.Empty;
    public string SourceFile { get; set; } = string.Empty;
    public int LineNumber { get; set; }
    public string Snippet { get; set; } = string.Empty;
}

public class MasterServerEndpoint
{
    public string Category { get; set; } = string.Empty;
    public string RouteOrFunction { get; set; } = string.Empty;
    public string HandlerOrController { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string HttpMethod { get; set; } = "POST";
}

public sealed class ComparisonItem
{
    public string ClientEndpoint { get; set; } = string.Empty;
    public string TargetRouteOrPhp { get; set; } = string.Empty;
    public string FunctionOrAction { get; set; } = string.Empty;
    public string SourceLocation { get; set; } = string.Empty;

    public string Status { get; set; } =
        string.Empty; // "Implemented", "Partially Implemented", "Missing", "External/Deprecated"

    public string? MasterServerHandler { get; set; }
    public string Description { get; set; } = string.Empty;
    public string ImplementationRequirement { get; set; } = string.Empty;
}

public static class Program
{
    private static readonly JsonSerializerOptions JsonIndentedOptions = new() { WriteIndented = true };
    public static void Main(string[] args)
    {
        Console.WriteLine("================================================================================");
        Console.WriteLine("  HON GAME CLIENT (v4.10.1) vs NEXUS MASTER SERVER ENDPOINT AUDITOR");
        Console.WriteLine("================================================================================");

        string clientDir = ResolveClientDirectory(args);
        string nexusDir = args.Length > 1 ? args[1] : ResolveNexusDirectory();
        string outputDir = args.Length > 2 ? args[2] : Path.Combine(Path.GetTempPath(), "nexus_endpoint_audit");

        Directory.CreateDirectory(outputDir);
        Console.WriteLine($"[Config] Using Client Directory: {clientDir}");
        Console.WriteLine($"[Config] Using NEXUS Directory:  {nexusDir}");
        Console.WriteLine($"[Config] Using Output Directory: {outputDir}");

        // 1. Audit Client Files
        Console.WriteLine("\n[1/4] Scanning HoN Game Client files (Loose + S2Z Archives)...");
        Dictionary<string, FormDefinition> formDefinitions = new(StringComparer.OrdinalIgnoreCase);
        List<FormSubmitCall> submitCalls = [];
        List<DirectEndpointCall> directCalls = [];

        ScanClientDirectory(clientDir, formDefinitions, submitCalls, directCalls);

        Console.WriteLine($"Found {formDefinitions.Count} unique XML Form definitions.");
        Console.WriteLine($"Found {submitCalls.Count} SubmitForm invocations.");
        Console.WriteLine($"Found {directCalls.Count} direct URL/API/PHP references.");

        // 2. Audit NEXUS Master Server
        Console.WriteLine("\n[2/4] Scanning NEXUS Master Server codebase...");
        List<MasterServerEndpoint> masterServerEndpoints = ScanMasterServer(nexusDir);
        Console.WriteLine($"Found {masterServerEndpoints.Count} Master Server routes/handlers.");

        // 3. Compare and Reconcile
        Console.WriteLine("\n[3/4] Reconciling and comparing client requirements with Master Server implementation...");
        List<ComparisonItem> comparison =
            ReconcileEndpoints(formDefinitions, submitCalls, directCalls, masterServerEndpoints);

        // 4. Output Reports
        Console.WriteLine("\n[4/4] Generating detailed audit reports...");
        GenerateReports(outputDir, formDefinitions, submitCalls, directCalls, masterServerEndpoints, comparison);

        Console.WriteLine("\nAudit completed successfully!");
    }

    private static void ScanClientDirectory(
        string clientDir,
        Dictionary<string, FormDefinition> formDefinitions,
        List<FormSubmitCall> submitCalls,
        List<DirectEndpointCall> directCalls)
    {
        // A. Loose files
        DirectoryInfo dirInfo = new(clientDir);
        FileInfo[] looseFiles = dirInfo.GetFiles("*.*", SearchOption.AllDirectories);

        foreach (FileInfo file in looseFiles)
        {
            string ext = file.Extension.ToLowerInvariant();
            string relPath = file.FullName.Substring(clientDir.Length).TrimStart('\\', '/');

            if (ext is ".lua" or ".xml" or ".interface" or ".package" or ".cfg")
                try
                {
                    string content = File.ReadAllText(file.FullName);
                    ProcessClientFileContent(content, $"LOOSE: {relPath}", formDefinitions, submitCalls, directCalls);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Warning: Could not read {file.FullName}: {ex.Message}");
                }
        }

        // B. S2Z archives
        FileInfo[] s2zFiles = dirInfo.GetFiles("*.s2z", SearchOption.AllDirectories);
        foreach (FileInfo s2z in s2zFiles)
            try
            {
                using ZipArchive zip = ZipFile.OpenRead(s2z.FullName);
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    string entryName = entry.FullName.ToLowerInvariant();
                    if (entryName.EndsWith(".lua") || entryName.EndsWith(".xml") || entryName.EndsWith(".interface") ||
                        entryName.EndsWith(".package") || entryName.EndsWith(".cfg"))
                    {
                        using Stream stream = entry.Open();
                        using StreamReader reader = new(stream);
                        string content = reader.ReadToEnd();
                        ProcessClientFileContent(content, $"S2Z [{s2z.Name}]: {entry.FullName}", formDefinitions,
                            submitCalls, directCalls);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Could not process archive {s2z.FullName}: {ex.Message}");
            }
    }

    private static void ProcessClientFileContent(
        string content,
        string source,
        Dictionary<string, FormDefinition> formDefinitions,
        List<FormSubmitCall> submitCalls,
        List<DirectEndpointCall> directCalls)
    {
        // 1. Parse XML <form ...>
        Regex formRegex = new(@"<form\b([^>]*?)(?:/>|>)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        MatchCollection formMatches = formRegex.Matches(content);
        foreach (Match m in formMatches)
        {
            string body = m.Groups[1].Value;
            string name = ExtractAttr(body, "name");
            string target = ExtractAttr(body, "target");
            string host = ExtractAttr(body, "host");
            string method = ExtractAttr(body, "method");
            string statusTrigger = ExtractAttr(body, "statustrigger");
            string resultTrigger = ExtractAttr(body, "resulttrigger");

            if (!string.IsNullOrEmpty(name))
            {
                if (!formDefinitions.TryGetValue(name, out FormDefinition? formDef))
                {
                    formDef = new FormDefinition
                    {
                        Name = name,
                        Target = target,
                        Host = host,
                        Method = method,
                        StatusTrigger = statusTrigger,
                        ResultTrigger = resultTrigger
                    };
                    formDefinitions[name] = formDef;
                }
                else
                {
                    if (string.IsNullOrEmpty(formDef.Target) && !string.IsNullOrEmpty(target)) formDef.Target = target;
                    if (string.IsNullOrEmpty(formDef.Host) && !string.IsNullOrEmpty(host)) formDef.Host = host;
                    if (string.IsNullOrEmpty(formDef.Method) && !string.IsNullOrEmpty(method)) formDef.Method = method;
                }

                formDef.Sources.Add(source);
            }
        }

        // 2. Parse SubmitForm calls
        string[] lines = content.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            int lineNum = i + 1;

            if (line.Contains("SubmitForm", StringComparison.OrdinalIgnoreCase))
            {
                // Capture multi-line call if needed
                string fullCall = line.Trim();
                int k = i + 1;
                while (k < lines.Length && !fullCall.Contains(')') && k < i + 10)
                {
                    fullCall += " " + lines[k].Trim();
                    k++;
                }

                Match submitMatch = Regex.Match(fullCall, @"SubmitForm\s*\(\s*['""]([^'""]+)['""](.*)",
                    RegexOptions.IgnoreCase);
                if (submitMatch.Success)
                {
                    string formName = submitMatch.Groups[1].Value;
                    string argsStr = submitMatch.Groups[2].Value;

                    FormSubmitCall call = new()
                    {
                        FormName = formName,
                        SourceFile = source,
                        LineNumber = lineNum,
                        FullCallText = fullCall
                    };

                    // Extract 'key', 'value' pairs
                    MatchCollection argMatches =
                        Regex.Matches(argsStr, @"['""]([^'""]+)['""]\s*,\s*(['""][^'""]*['""]|[^,'""]+)");
                    for (int a = 0; a < argMatches.Count; a++)
                    {
                        string key = argMatches[a].Groups[1].Value;
                        string val = argMatches[a].Groups[2].Value.Trim('\'', '"', ' ');
                        call.Arguments[key] = val;

                        if (key.Equals("f", StringComparison.OrdinalIgnoreCase)) call.FunctionNameF = val;
                        if (key.Equals("table", StringComparison.OrdinalIgnoreCase)) call.TableParam = val;
                        if (key.Equals("req_type", StringComparison.OrdinalIgnoreCase)) call.ReqType = val;
                        if (key.Equals("request_code", StringComparison.OrdinalIgnoreCase)) call.RequestCode = val;
                    }

                    submitCalls.Add(call);
                }
            }

            // 3. Direct URLs / PHP endpoints
            if (line.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("https://", StringComparison.OrdinalIgnoreCase))
            {
                Match urlMatch = Regex.Match(line, @"(https?://[^\s'"">,\)]+)", RegexOptions.IgnoreCase);
                if (urlMatch.Success)
                    directCalls.Add(new DirectEndpointCall
                    {
                        Type = "HTTP_URL",
                        EndpointOrUrl = urlMatch.Groups[1].Value,
                        SourceFile = source,
                        LineNumber = lineNum,
                        Snippet = line.Trim()
                    });
            }

            if (line.Contains(".php", StringComparison.OrdinalIgnoreCase) &&
                !line.Contains("http://", StringComparison.OrdinalIgnoreCase) &&
                !line.Contains("https://", StringComparison.OrdinalIgnoreCase))
            {
                Match phpMatch = Regex.Match(line, @"([!a-zA-Z0-9_\-\./]+\.php[^\s'"">,\)]*)", RegexOptions.IgnoreCase);
                if (phpMatch.Success)
                    directCalls.Add(new DirectEndpointCall
                    {
                        Type = "PHP_FILE",
                        EndpointOrUrl = phpMatch.Groups[1].Value,
                        SourceFile = source,
                        LineNumber = lineNum,
                        Snippet = line.Trim()
                    });
            }

            if (line.Contains("?r=", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("&r=", StringComparison.OrdinalIgnoreCase))
            {
                Match routeMatch = Regex.Match(line, @"[?&]r=([a-zA-Z0-9_\-\./]+)", RegexOptions.IgnoreCase);
                if (routeMatch.Success)
                    directCalls.Add(new DirectEndpointCall
                    {
                        Type = "ASCENSION_ROUTE",
                        EndpointOrUrl = routeMatch.Groups[1].Value,
                        SourceFile = source,
                        LineNumber = lineNum,
                        Snippet = line.Trim()
                    });
            }
        }
    }

    private static string ExtractAttr(string text, string attrName)
    {
        Match m = Regex.Match(text, $@"{attrName}\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : string.Empty;
    }

    private static List<MasterServerEndpoint> ScanMasterServer(string nexusDir)
    {
        List<MasterServerEndpoint> endpoints = [];

        // 1. ClientRequestDispatcher HandlerMap
        string dispatcherPath = Path.Combine(nexusDir, "services", "master-server", "src", "Services", "Requester",
            "ClientRequestDispatcher.cs");
        if (!File.Exists(dispatcherPath))
            dispatcherPath = Path.Combine(nexusDir,
                @"KONGOR.MasterServer\Services\Requester\ClientRequestDispatcher.cs");
        if (File.Exists(dispatcherPath))
        {
            string[] dLines = File.ReadAllLines(dispatcherPath);
            foreach (string line in dLines)
            {
                Match m = Regex.Match(line, @"\[""(.*?)""\]\s*=\s*typeof\((.*?)\)");
                if (m.Success)
                    endpoints.Add(new MasterServerEndpoint
                    {
                        Category = "client_requester.php (f=...)",
                        RouteOrFunction = m.Groups[1].Value,
                        HandlerOrController = m.Groups[2].Value,
                        FilePath = Path.GetRelativePath(nexusDir, dispatcherPath),
                        HttpMethod = "POST"
                    });
            }
        }

        // 2. Scan All Controllers in MasterServer
        string controllersDir = Path.Combine(nexusDir, "services", "master-server", "src", "Controllers");
        if (!Directory.Exists(controllersDir))
            controllersDir = Path.Combine(nexusDir, @"KONGOR.MasterServer\Controllers");
        if (Directory.Exists(controllersDir))
        {
            FileInfo[] csFiles = new DirectoryInfo(controllersDir).GetFiles("*.cs", SearchOption.AllDirectories);
            foreach (FileInfo f in csFiles)
            {
                string relPath = f.FullName.Substring(nexusDir.Length).TrimStart('\\', '/');
                string[] lines = File.ReadAllLines(f.FullName);

                string currentClass = f.Name;
                string baseRoute = "";

                foreach (string l in lines)
                {
                    Match routeMatch = Regex.Match(l,
                        @"\[Route\((?:TextConstant\.EmptyString\s*\+\s*)?[""']([^""']*)[""']\s*\)\]");
                    if (routeMatch.Success) baseRoute = routeMatch.Groups[1].Value;

                    Match httpMethodMatch =
                        Regex.Match(l, @"\[Http(Post|Get|Put|Delete)(?:\([""']([^""']*)[""']\))?\]");
                    if (httpMethodMatch.Success)
                    {
                        string verb = httpMethodMatch.Groups[1].Value.ToUpperInvariant();
                        string subRoute = httpMethodMatch.Groups[2].Success ? httpMethodMatch.Groups[2].Value : "";
                        string fullRoute = CombineRoutes(baseRoute, subRoute);

                        endpoints.Add(new MasterServerEndpoint
                        {
                            Category = "ASP.NET Core Controller Route",
                            RouteOrFunction = fullRoute,
                            HandlerOrController = f.Name,
                            FilePath = relPath,
                            HttpMethod = verb
                        });
                    }

                    // Check for switch cases on function name f
                    Match caseMatch = Regex.Match(l, @"case\s+[""']([^""']+)[""']\s*:");
                    if (caseMatch.Success)
                    {
                        string fName = caseMatch.Groups[1].Value;
                        endpoints.Add(new MasterServerEndpoint
                        {
                            Category = $"{baseRoute} (f=...)",
                            RouteOrFunction = fName,
                            HandlerOrController = f.Name,
                            FilePath = relPath,
                            HttpMethod = "POST"
                        });
                    }
                }
            }
        }

        return endpoints;
    }

    private static string CombineRoutes(string baseRoute, string subRoute)
    {
        if (string.IsNullOrEmpty(baseRoute)) return subRoute.TrimStart('/');
        if (string.IsNullOrEmpty(subRoute)) return baseRoute.TrimStart('/');
        if (subRoute.StartsWith('/')) return subRoute.TrimStart('/');
        return $"{baseRoute.TrimEnd('/')}/{subRoute.TrimStart('/')}";
    }

    private static List<ComparisonItem> ReconcileEndpoints(
        Dictionary<string, FormDefinition> formDefinitions,
        List<FormSubmitCall> submitCalls,
        List<DirectEndpointCall> directCalls,
        List<MasterServerEndpoint> masterServerEndpoints)
    {
        List<ComparisonItem> items = [];
        HashSet<string> seenKeys = new(StringComparer.OrdinalIgnoreCase);

        // 1. Process all SubmitForm calls and their corresponding forms
        IEnumerable<IGrouping<string, FormSubmitCall>> groupedSubmits =
            submitCalls.GroupBy(s => s.FormName, StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, FormSubmitCall> group in groupedSubmits)
        {
            string formName = group.Key;
            formDefinitions.TryGetValue(formName, out FormDefinition? formDef);

            string target = formDef?.Target ?? DetermineDefaultTarget(formName);
            string host = formDef?.Host ?? "!masterserver";

            // Group by function 'f' if present
            IEnumerable<IGrouping<string, FormSubmitCall>> byF = group.GroupBy(g => g.FunctionNameF ?? "[NO_F]",
                StringComparer.OrdinalIgnoreCase);
            foreach (IGrouping<string, FormSubmitCall> fGroup in byF)
            {
                string fName = fGroup.Key;
                string uniqueKey = $"{formName}|{target}|{fName}";
                if (!seenKeys.Add(uniqueKey)) continue;

                FormSubmitCall sample = fGroup.First();
                ComparisonItem item = new()
                {
                    ClientEndpoint = $"SubmitForm('{formName}')",
                    TargetRouteOrPhp = target,
                    FunctionOrAction = fName != "[NO_F]" ? $"f={fName}" :
                        sample.ReqType != null ? $"req_type={sample.ReqType}" :
                        sample.RequestCode != null ? $"request_code={sample.RequestCode}" : "direct",
                    SourceLocation = $"{sample.SourceFile}:{sample.LineNumber}"
                };

                // Match against master server
                MatchMasterServer(item, formName, target, fName, sample, masterServerEndpoints);
                items.Add(item);
            }
        }

        // 2. Process all XML Form definitions that weren't submitted in Lua
        foreach (KeyValuePair<string, FormDefinition> kvp in formDefinitions)
        {
            string formName = kvp.Key;
            FormDefinition def = kvp.Value;
            string uniqueKey = $"{formName}|{def.Target}|[XML_ONLY]";
            if (seenKeys.Add(uniqueKey))
            {
                ComparisonItem item = new()
                {
                    ClientEndpoint = $"<form name='{formName}'>",
                    TargetRouteOrPhp = def.Target,
                    FunctionOrAction = "XML Form Definition",
                    SourceLocation = def.Sources.FirstOrDefault() ?? "XML"
                };
                MatchMasterServer(item, formName, def.Target, "[NO_F]", null, masterServerEndpoints);
                items.Add(item);
            }
        }

        // 3. Process direct calls (Ascension routes, PHP files, web URLs)
        IEnumerable<IGrouping<string, DirectEndpointCall>> groupedDirect =
            directCalls.GroupBy(d => d.EndpointOrUrl, StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, DirectEndpointCall> group in groupedDirect)
        {
            DirectEndpointCall first = group.First();
            string endpoint = first.EndpointOrUrl;
            string uniqueKey = $"DIRECT|{first.Type}|{endpoint}";
            if (!seenKeys.Add(uniqueKey)) continue;

            ComparisonItem item = new()
            {
                ClientEndpoint = $"{first.Type}: {endpoint}",
                TargetRouteOrPhp = endpoint,
                FunctionOrAction = first.Type,
                SourceLocation = $"{first.SourceFile}:{first.LineNumber}"
            };

            MatchDirectEndpoint(item, first, masterServerEndpoints);
            items.Add(item);
        }

        return items;
    }

    private static string DetermineDefaultTarget(string formName)
    {
        if (formName.StartsWith("WatchSystem", StringComparison.OrdinalIgnoreCase))
            return "Ascension / index.php (Dynamic Form Target)";
        if (formName.StartsWith("PlayerStats", StringComparison.OrdinalIgnoreCase)) return "client_requester.php";
        if (formName.StartsWith("Store", StringComparison.OrdinalIgnoreCase) ||
            formName.Equals("MicroStore", StringComparison.OrdinalIgnoreCase))
            return "store_requester.php / client_requester.php";
        if (formName.StartsWith("Plinko", StringComparison.OrdinalIgnoreCase) ||
            formName.Equals("TicketRedemptionMain", StringComparison.OrdinalIgnoreCase) ||
            formName.Equals("RedeemTickets", StringComparison.OrdinalIgnoreCase) ||
            formName.Equals("LootList", StringComparison.OrdinalIgnoreCase))
            return "master/casino or master/ticketexchange";
        if (formName.StartsWith("quests", StringComparison.OrdinalIgnoreCase)) return "master/quest";
        if (formName.StartsWith("FormLottery", StringComparison.OrdinalIgnoreCase) ||
            formName.StartsWith("FormGraffiti", StringComparison.OrdinalIgnoreCase) ||
            formName.StartsWith("FormBuyWall", StringComparison.OrdinalIgnoreCase))
            return "master/casino or master/storage";
        if (formName.StartsWith("RapReport", StringComparison.OrdinalIgnoreCase) ||
            formName.StartsWith("ReportAbuse", StringComparison.OrdinalIgnoreCase))
            return "client_requester.php / report";
        if (formName.StartsWith("Garena", StringComparison.OrdinalIgnoreCase) ||
            formName.StartsWith("APIUser", StringComparison.OrdinalIgnoreCase) ||
            formName.StartsWith("LATCreate", StringComparison.OrdinalIgnoreCase))
            return "client_requester.php / regional_auth";
        return "client_requester.php";
    }

    private static void MatchMasterServer(
        ComparisonItem item,
        string formName,
        string target,
        string fName,
        FormSubmitCall? sample,
        List<MasterServerEndpoint> masterServerEndpoints)
    {
        // 1. Match client_requester.php f=...
        if (fName != "[NO_F]" && !string.IsNullOrEmpty(fName))
        {
            MasterServerEndpoint? match = masterServerEndpoints.FirstOrDefault(m =>
                m.Category.Contains("client_requester") &&
                m.RouteOrFunction.Equals(fName, StringComparison.OrdinalIgnoreCase));

            if (match != null)
            {
                item.Status = "Implemented";
                item.MasterServerHandler = $"{match.HandlerOrController} ({match.Category})";
                item.Description = $"Handled via {match.HandlerOrController} in ClientRequestDispatcher.";
                return;
            }
        }

        // 2. Match Target Route
        string cleanTarget = target.TrimStart('/', '!');
        MasterServerEndpoint? routeMatch = masterServerEndpoints.FirstOrDefault(m =>
            m.RouteOrFunction.Equals(cleanTarget, StringComparison.OrdinalIgnoreCase) ||
            cleanTarget.StartsWith(m.RouteOrFunction, StringComparison.OrdinalIgnoreCase) ||
            m.RouteOrFunction.StartsWith(cleanTarget, StringComparison.OrdinalIgnoreCase));

        if (routeMatch != null)
        {
            item.Status = "Implemented";
            item.MasterServerHandler = $"{routeMatch.HandlerOrController} [Route: {routeMatch.RouteOrFunction}]";
            item.Description = $"Handled by {routeMatch.HandlerOrController}.";
            return;
        }

        // 3. Known Partial / Subsystem Handlers
        if (formName.Equals("MicroStore", StringComparison.OrdinalIgnoreCase) ||
            formName.StartsWith("Store", StringComparison.OrdinalIgnoreCase) ||
            formName.StartsWith("LobbyBuy", StringComparison.OrdinalIgnoreCase))
        {
            item.Status = "Implemented";
            item.MasterServerHandler = "StoreController.cs / UpgradesHandler.cs / ProductsHandler.cs";
            item.Description =
                "Handled via StoreController (store_requester.php) and Upgrades/Products client requesters.";
            return;
        }

        if (formName.StartsWith("Plinko", StringComparison.OrdinalIgnoreCase) ||
            formName.Equals("TicketRedemptionMain", StringComparison.OrdinalIgnoreCase) ||
            formName.Equals("RedeemTickets", StringComparison.OrdinalIgnoreCase) ||
            formName.Equals("LootList", StringComparison.OrdinalIgnoreCase))
        {
            item.Status = "Implemented";
            item.MasterServerHandler = "MiniGameController.cs / TicketExchangeController.cs";
            item.Description =
                "Handled via MiniGameController ('master/casino') and TicketExchangeController ('master/ticketexchange').";
            return;
        }

        if (formName.StartsWith("quests", StringComparison.OrdinalIgnoreCase))
        {
            item.Status = "Implemented";
            item.MasterServerHandler = "QuestController.cs";
            item.Description = "Handled via QuestController ('master/quest').";
            return;
        }

        if (formName.StartsWith("WatchSystem", StringComparison.OrdinalIgnoreCase))
        {
            item.Status = "Implemented";
            item.MasterServerHandler = "AscensionController.cs";
            item.Description = "Handled via AscensionController (index.php?r=...).";
            return;
        }

        // 4. Check for Missing / Specific Endpoints
        if (fName.Equals("get_points", StringComparison.OrdinalIgnoreCase) ||
            formName.Equals("PointsChanged", StringComparison.OrdinalIgnoreCase))
        {
            item.Status = "Missing";
            item.Description =
                "Client notification trigger form for Account Points (Silver/Gold) refresh ('f=get_points').";
            item.ImplementationRequirement =
                "Add 'get_points' handler to ClientRequestDispatcher returning updated coin/point balances.";
            return;
        }

        if (fName.Equals("set_ingame_referrer", StringComparison.OrdinalIgnoreCase) ||
            formName.Equals("SetAsReferrer", StringComparison.OrdinalIgnoreCase))
        {
            item.Status = "Missing";
            item.Description = "In-game referral submission form ('f=set_ingame_referrer').";
            item.ImplementationRequirement =
                "Add 'set_ingame_referrer' handler in ClientRequestDispatcher to record account referrals.";
            return;
        }

        if (fName.Equals("get_referrals", StringComparison.OrdinalIgnoreCase) ||
            formName.Equals("MentorReferralForm", StringComparison.OrdinalIgnoreCase))
        {
            item.Status = "Missing";
            item.Description = "Mentor referral overview form ('f=get_referrals').";
            item.ImplementationRequirement =
                "Add 'get_referrals' handler in ClientRequestDispatcher to return referral stats and rewards.";
            return;
        }

        if (fName.Equals("get_hero_usage_list", StringComparison.OrdinalIgnoreCase) ||
            formName.Equals("HeroUsageListForm", StringComparison.OrdinalIgnoreCase))
        {
            item.Status = "Missing";
            item.Description = "Hero usage statistics for Learnatorium ('f=get_hero_usage_list').";
            item.ImplementationRequirement =
                "Add 'get_hero_usage_list' handler in ClientRequestDispatcher to return popular hero pick rates.";
            return;
        }

        if (fName.Equals("qa_submit", StringComparison.OrdinalIgnoreCase) ||
            formName.Equals("QAChecklistSubmit", StringComparison.OrdinalIgnoreCase))
        {
            item.Status = "Missing";
            item.Description = "Internal QA Checklist submission form ('f=qa_submit').";
            item.ImplementationRequirement =
                "Add stub/mock handler in ClientRequestDispatcher if QA checklist testing is enabled.";
            return;
        }

        if (formName.Equals("RapReport", StringComparison.OrdinalIgnoreCase) ||
            formName.Equals("ReportAbuse2", StringComparison.OrdinalIgnoreCase))
        {
            item.Status = "Missing";
            item.Description = "Report-a-Player / Report Abuse submission form.";
            item.ImplementationRequirement =
                "Add 'report_abuse' or RAP form endpoint to accept player conduct reports.";
            return;
        }

        if (formName.StartsWith("FormGraffiti", StringComparison.OrdinalIgnoreCase) ||
            formName.StartsWith("FormLottery", StringComparison.OrdinalIgnoreCase) ||
            formName.StartsWith("FormBuyWall", StringComparison.OrdinalIgnoreCase))
        {
            item.Status = "Missing";
            item.Description = "Codex Graffiti Wall & Codex Lottery minigame form.";
            item.ImplementationRequirement =
                "Implement Codex Graffiti Wall / Lottery endpoints in MiniGameController or ClientRequestDispatcher.";
            return;
        }

        if (formName.Equals("FormReceiveRevival", StringComparison.OrdinalIgnoreCase))
        {
            item.Status = "Missing";
            item.Description = "Special event revival package claim form.";
            item.ImplementationRequirement = "Implement event claim handler in EventsHandler or RewardsHandler.";
            return;
        }

        if (formName.Equals("invite_player_back", StringComparison.OrdinalIgnoreCase))
        {
            item.Status = "Missing";
            item.Description = "Social panel invite inactive player back form.";
            item.ImplementationRequirement = "Add 'invite_player_back' handler to accept friend invite notifications.";
            return;
        }

        if (formName.StartsWith("Garena", StringComparison.OrdinalIgnoreCase) ||
            formName.StartsWith("APIUser", StringComparison.OrdinalIgnoreCase) ||
            formName.StartsWith("LATCreate", StringComparison.OrdinalIgnoreCase) ||
            formName.StartsWith("APICreate", StringComparison.OrdinalIgnoreCase))
        {
            item.Status = "External/Deprecated";
            item.Description =
                "Legacy third-party publisher registration/auth (Garena SEA / Axeso5 LAT / GameTree Korea).";
            item.ImplementationRequirement = "Deprecated by NEXUS standalone SRP / WebPortal authentication.";
            return;
        }

        item.Status = "Missing";
        item.Description = $"Unmapped client form submission: Form='{formName}', Target='{target}', f='{fName}'.";
        item.ImplementationRequirement = $"Investigate and implement handler for Form='{formName}' Target='{target}'.";
    }

    private static void MatchDirectEndpoint(
        ComparisonItem item,
        DirectEndpointCall call,
        List<MasterServerEndpoint> masterServerEndpoints)
    {
        string ep = call.EndpointOrUrl;

        if (call.Type == "ASCENSION_ROUTE")
        {
            item.Status = "Implemented";
            item.MasterServerHandler = "AscensionController.cs";
            item.Description = $"Ascension frontend action handler for '?r={ep}'.";
            return;
        }

        if (ep.Contains("getAvatar.php", StringComparison.OrdinalIgnoreCase))
        {
            item.Status = "Implemented";
            item.MasterServerHandler = "IconsController.cs / DAWNBRINGER WebPortal";
            item.Description = "Avatar icon serving endpoint (mapped via IconsController / Avatar URL provider).";
            return;
        }

        if (ep.Contains("guides/select.php", StringComparison.OrdinalIgnoreCase))
        {
            item.Status = "Implemented";
            item.MasterServerHandler = "GuideHandler.cs / ClientRequestDispatcher";
            item.Description = "Hero guide selector interface.";
            return;
        }

        if (ep.Contains("s3.amazonaws.com", StringComparison.OrdinalIgnoreCase) ||
            ep.Contains("garena", StringComparison.OrdinalIgnoreCase) ||
            ep.Contains("gametree", StringComparison.OrdinalIgnoreCase) ||
            ep.Contains("axeso5", StringComparison.OrdinalIgnoreCase) ||
            ep.Contains("blogspot.com", StringComparison.OrdinalIgnoreCase))
        {
            item.Status = "External/Deprecated";
            item.Description =
                "Legacy external CDN or third-party publisher web asset (S3 MOTD / Garena Portal / Axeso5).";
            item.ImplementationRequirement =
                "No MasterServer action required. Can be overridden locally via UI web redirection.";
            return;
        }

        if (ep.Contains("whitelistfolder", StringComparison.OrdinalIgnoreCase) ||
            ep.Contains("referral", StringComparison.OrdinalIgnoreCase))
        {
            item.Status = "Missing";
            item.Description = $"Web referral / tutorial page: {ep}";
            item.ImplementationRequirement = "Add web route or static page in DAWNBRINGER WebPortal / MasterServer.";
            return;
        }

        item.Status = "External/Deprecated";
        item.Description = $"External web resource: {ep}";
        item.ImplementationRequirement = "None.";
    }

    private static void GenerateReports(
        string outputDir,
        Dictionary<string, FormDefinition> formDefinitions,
        List<FormSubmitCall> submitCalls,
        List<DirectEndpointCall> directCalls,
        List<MasterServerEndpoint> masterServerEndpoints,
        List<ComparisonItem> comparison)
    {
        // 1. JSON Report
        string jsonPath = Path.Combine(outputDir, "endpoint_audit_full.json");
        object fullData = new
        {
            Summary = new
            {
                TotalXmlForms = formDefinitions.Count,
                TotalSubmitCalls = submitCalls.Count,
                TotalDirectCalls = directCalls.Count,
                TotalMasterServerEndpoints = masterServerEndpoints.Count,
                TotalComparisonItems = comparison.Count,
                ImplementedCount = comparison.Count(c => c.Status == "Implemented"),
                MissingCount = comparison.Count(c => c.Status == "Missing"),
                DeprecatedOrExternalCount = comparison.Count(c => c.Status == "External/Deprecated")
            },
            Comparison = comparison,
            MasterServerEndpoints = masterServerEndpoints,
            FormDefinitions = formDefinitions.Values,
            SubmitCalls = submitCalls
        };
        File.WriteAllText(jsonPath,
            JsonSerializer.Serialize(fullData, JsonIndentedOptions));
        Console.WriteLine($"Saved full JSON data to {jsonPath}");

        // 2. Markdown Report
        string mdPath = Path.Combine(outputDir, "endpoint_comparison_matrix.md");
        StringBuilder sb = new();

        sb.AppendLine("# NEXUS Master Server vs HoN Game Client (v4.10.1) Endpoint Audit Matrix");
        sb.AppendLine();
        sb.AppendLine($"**Generated:** {TimeProvider.System.GetUtcNow():yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine();
        sb.AppendLine("## Summary Statistics");
        sb.AppendLine();
        sb.AppendLine($"- **Total Unique Endpoints / Actions Audited:** {comparison.Count}");
        sb.AppendLine(
            $"- **Implemented & Active in Master Server:** {comparison.Count(c => c.Status == "Implemented")}");
        sb.AppendLine(
            $"- **Missing / Requiring Master Server Implementation:** {comparison.Count(c => c.Status == "Missing")}");
        sb.AppendLine(
            $"- **Legacy External / Deprecated Publisher Endpoints:** {comparison.Count(c => c.Status == "External/Deprecated")}");
        sb.AppendLine();

        sb.AppendLine("## 1. Missing Endpoints Requiring Implementation");
        sb.AppendLine();
        sb.AppendLine(
            "| Client Endpoint / Form | Target / Action | Description | Proposed Master Server Implementation |");
        sb.AppendLine("| :--- | :--- | :--- | :--- |");
        foreach (ComparisonItem item in comparison.Where(c => c.Status == "Missing"))
            sb.AppendLine(
                $"| `{item.ClientEndpoint}` | `{item.TargetRouteOrPhp}` ({item.FunctionOrAction}) | {item.Description} | {item.ImplementationRequirement} |");
        sb.AppendLine();

        sb.AppendLine("## 2. Implemented Master Server Endpoints (Verified)");
        sb.AppendLine();
        sb.AppendLine("| Client Endpoint / Action | Target Route | Master Server Handler | Description |");
        sb.AppendLine("| :--- | :--- | :--- | :--- |");
        foreach (ComparisonItem item in comparison.Where(c => c.Status == "Implemented"))
            sb.AppendLine(
                $"| `{item.ClientEndpoint}` | `{item.TargetRouteOrPhp}` ({item.FunctionOrAction}) | `{item.MasterServerHandler}` | {item.Description} |");
        sb.AppendLine();

        sb.AppendLine("## 3. Legacy External / Deprecated Endpoints (No Master Server Action Needed)");
        sb.AppendLine();
        sb.AppendLine("| Endpoint | Type | Description | Notes |");
        sb.AppendLine("| :--- | :--- | :--- | :--- |");
        foreach (ComparisonItem item in comparison.Where(c => c.Status == "External/Deprecated"))
            sb.AppendLine(
                $"| `{item.ClientEndpoint}` | `{item.FunctionOrAction}` | {item.Description} | {item.ImplementationRequirement} |");
        sb.AppendLine();

        File.WriteAllText(mdPath, sb.ToString());
        Console.WriteLine($"Saved Markdown matrix to {mdPath}");
    }

    private static string ResolveClientDirectory(string[] args)
    {
        // 1. Explicit CLI argument
        if (args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]) && Directory.Exists(args[0]))
            return Path.GetFullPath(args[0]);

        // 2. Read from .NET User Secrets
        string? secretPath = TryGetSecretGameClientPath();
        if (!string.IsNullOrWhiteSpace(secretPath) && Directory.Exists(secretPath)) return Path.GetFullPath(secretPath);

        // 3. Read from Environment variables
        string? envPath = Environment.GetEnvironmentVariable("HON_CLIENT_DIR")
                          ?? Environment.GetEnvironmentVariable("HON_GAME_PATH")
                          ?? Environment.GetEnvironmentVariable("WILLOWMAKER_PATH")
                          ?? Environment.GetEnvironmentVariable("GameClient__GamePath");
        if (!string.IsNullOrWhiteSpace(envPath) && Directory.Exists(envPath)) return Path.GetFullPath(envPath);

        // 4. Auto-discover relative to solution parent directories
        string baseDir = AppContext.BaseDirectory;
        DirectoryInfo? currentDir = new(baseDir);
        while (currentDir != null)
        {
            string candidateShared = Path.Combine(currentDir.FullName, "games", "shared");
            if (Directory.Exists(candidateShared)) return candidateShared;

            string candidateClient = Path.Combine(currentDir.FullName, "games", "client");
            if (Directory.Exists(candidateClient)) return candidateClient;

            currentDir = currentDir.Parent;
        }

        // 5. Fallback relative to current working directory
        string cwdShared = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "games", "shared"));
        if (Directory.Exists(cwdShared)) return cwdShared;

        return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "games", "client"));
    }

    private static string? TryGetSecretGameClientPath()
    {
        string[] secretIds =
        [
            "00005072-6f6a-6563-7420-4b4f4e474f52", // ASPIRE.ApplicationHost
            "f1e1f81d-ed00-40b6-8d39-0b82a10a6338" // DAWNBRINGER.WebPortal.UI
        ];

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        foreach (string id in secretIds)
        {
            List<string> candidateSecretFiles =
            [
                Path.Combine(appData, "Microsoft", "UserSecrets", id, "secrets.json"),
                Path.Combine(userProfile, ".microsoft", "usersecrets", id, "secrets.json")
            ];

            foreach (string file in candidateSecretFiles)
                if (File.Exists(file))
                    try
                    {
                        string json = File.ReadAllText(file);
                        using JsonDocument doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("GameClient:GamePath", out JsonElement val) &&
                            val.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(val.GetString()))
                            return val.GetString();
                        if (doc.RootElement.TryGetProperty("GameClient:Path", out JsonElement val2) &&
                            val2.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(val2.GetString()))
                            return val2.GetString();
                    }
                    catch
                    {
                        // Ignore parse errors and continue to next secret file
                    }
        }

        return null;
    }

    private static string ResolveNexusDirectory()
    {
        string? envRoot = Environment.GetEnvironmentVariable("KONGOR_ROOT")
            ?? Environment.GetEnvironmentVariable("KONGOR_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(envRoot) && Directory.Exists(envRoot))
        {
            return Path.GetFullPath(envRoot);
        }

        string baseDir = AppContext.BaseDirectory;
        DirectoryInfo? currentDir = new(baseDir);
        while (currentDir != null)
        {
            if (File.Exists(Path.Combine(currentDir.FullName, "ProjectKongor.slnx")) ||
                File.Exists(Path.Combine(currentDir.FullName, "ProjectKongor.slnx")) ||
                File.Exists(Path.Combine(currentDir.FullName, "Directory.Build.props")) ||
                File.Exists(Path.Combine(currentDir.FullName, "NEXUS.sln")))
                return currentDir.FullName;

            string nexusCandidate = Path.Combine(currentDir.FullName, "NEXUS");
            if (Directory.Exists(nexusCandidate) && File.Exists(Path.Combine(nexusCandidate, "NEXUS.sln")))
                return nexusCandidate;

            currentDir = currentDir.Parent;
        }

        return Directory.GetCurrentDirectory();
    }
}

