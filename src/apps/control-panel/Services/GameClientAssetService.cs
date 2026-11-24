// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO.Compression;
using Kongor.Core.Configuration;
using NEXUS.ControlPanel.GUI.Services.Models;

namespace NEXUS.ControlPanel.GUI.Services;

public sealed class GameClientAssetService(ISecretsManagerService? secretsManager = null) : IGameClientAssetService
{
    public async Task<bool> EnsureAssetsExtractedAsync(string? gameClientPath = null,
        CancellationToken cancellationToken = default)
    {
        string clientPath = gameClientPath ?? string.Empty;

        if (string.IsNullOrWhiteSpace(clientPath) && secretsManager != null)
            try
            {
                NexusSecrets secrets = await secretsManager.LoadSecretsAsync(cancellationToken);
                clientPath = secrets.GameClientPath;
            }
            catch
            {
                // Fallback to environment/relative
            }

        if (string.IsNullOrWhiteSpace(clientPath))
            clientPath = Environment.GetEnvironmentVariable("HON_GAME_PATH") ??
                         Environment.GetEnvironmentVariable("WILLOWMAKER_PATH") ??
                         string.Empty;

        string baseDir = AppContext.BaseDirectory;
        string parentDir = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));

        if (string.IsNullOrWhiteSpace(clientPath))
        {
            string solutionRoot = FindSolutionRoot();
            string[] candidateDirs =
            [
                Path.Combine(solutionRoot, "games", "client", "game"),
                Path.Combine(solutionRoot, "games", "client"),
                Path.Combine(solutionRoot, "games", "shared"),
                Path.Combine(solutionRoot, "apps", "willowmaker", "src")
            ];

            foreach (string candidate in candidateDirs)
                if (Directory.Exists(candidate) || File.Exists(candidate))
                {
                    clientPath = candidate;
                    break;
                }
        }

        if (string.IsNullOrWhiteSpace(clientPath)) return false;

        string clientDir = File.Exists(clientPath) ? Path.GetDirectoryName(clientPath)! : clientPath;
        if (!Directory.Exists(clientDir)) return false;

        // Setup destination asset directories
        List<string> soundTargetDirs = [Path.Combine(baseDir, "Assets", "sounds", "empath")];
        List<string> modelTargetDirs =
            [Path.Combine(baseDir, "Assets", "models"), Path.Combine(baseDir, "Assets", "web")];

        // Also check development project folder
        string? projectRoot = Directory.GetParent(baseDir)?.Parent?.Parent?.FullName;
        if (!string.IsNullOrEmpty(projectRoot) && Directory.Exists(Path.Combine(projectRoot, "Assets")))
        {
            soundTargetDirs.Add(Path.Combine(projectRoot, "Assets", "sounds", "empath"));
            modelTargetDirs.Add(Path.Combine(projectRoot, "Assets", "models"));
            modelTargetDirs.Add(Path.Combine(projectRoot, "Assets", "web"));
        }

        for (int i = 0; i < soundTargetDirs.Count; i++) Directory.CreateDirectory(soundTargetDirs[i]);

        for (int i = 0; i < modelTargetDirs.Count; i++) Directory.CreateDirectory(modelTargetDirs[i]);

        bool extractedAny = false;

        // 1. Extract Empath Voice Lines from Game Client .s2z packages
        List<string> candidateS2zPackages = [];
        string gameFolder = Path.Combine(clientDir, "game");
        if (Directory.Exists(gameFolder))
        {
            string res1 = Path.Combine(gameFolder, "resources1.s2z");
            if (File.Exists(res1)) candidateS2zPackages.Add(res1);

            string[] s2zFiles = Directory.GetFiles(gameFolder, "resources*.s2z");
            for (int i = 0; i < s2zFiles.Length; i++)
            {
                string s2z = s2zFiles[i];
                if (!candidateS2zPackages.Contains(s2z)) candidateS2zPackages.Add(s2z);
            }
        }

        for (int p = 0; p < candidateS2zPackages.Count; p++)
        {
            string s2zPath = candidateS2zPackages[p];
            try
            {
                await using FileStream zipStream = File.OpenRead(s2zPath);
                await using ZipArchive zip = new(zipStream, ZipArchiveMode.Read);
                for (int i = 0; i < zip.Entries.Count; i++)
                {
                    ZipArchiveEntry entry = zip.Entries[i];
                    if (entry.FullName.Contains("heroes/empath/sounds/voice/", StringComparison.OrdinalIgnoreCase) &&
                        (entry.Name.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) ||
                         entry.Name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ||
                         entry.Name.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)))
                        for (int t = 0; t < soundTargetDirs.Count; t++)
                        {
                            string destFile = Path.Combine(soundTargetDirs[t], entry.Name);
                            if (!File.Exists(destFile) || new FileInfo(destFile).Length != entry.Length)
                            {
                                await using (Stream entryStream = await entry.OpenAsync(cancellationToken))
                                await using (FileStream destStream = File.Create(destFile))
                                {
                                    await entryStream.CopyToAsync(destStream, cancellationToken);
                                }

                                extractedAny = true;
                            }
                        }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GameClientAssetService] Error reading package {s2zPath}: {ex.Message}");
            }
        }

        // 2. Synchronize 3D Models if available in WebPortal or repository
        List<string> candidateModelSources =
        [
            Path.Combine(FindSolutionRoot(), "src", "services", "web-ui", "wwwroot", "assets", "prebaked", "models"),
            Path.Combine(parentDir, "NEXUS", "DAWNBRINGER.WebPortal.UI", "wwwroot", "assets", "prebaked", "models"),
            Path.Combine(clientDir, "models"),
            Path.Combine(clientDir, "override", "models")
        ];

        if (BundledLayoutResolver.TryResolve(out BundledLayout? bundledLayout))
        {
            candidateModelSources.Insert(0, Path.Combine(bundledLayout.ServiceDir("web-portal-ui"), "wwwroot", "assets", "prebaked", "models"));
        }

        string[] modelFileNames = ["empath_cn.glb", "kongor_default.glb"];

        for (int s = 0; s < candidateModelSources.Count; s++)
        {
            string modelSourceDir = candidateModelSources[s];
            if (!Directory.Exists(modelSourceDir)) continue;

            for (int m = 0; m < modelFileNames.Length; m++)
            {
                string modelName = modelFileNames[m];
                string srcFile = Path.Combine(modelSourceDir, modelName);
                if (File.Exists(srcFile))
                    for (int d = 0; d < modelTargetDirs.Count; d++)
                    {
                        string destDir = modelTargetDirs[d];
                        string destFile = Path.Combine(destDir, modelName);
                        if (!File.Exists(destFile))
                        {
                            await using (FileStream srcStream = File.OpenRead(srcFile))
                            await using (FileStream destStream = File.Create(destFile))
                            {
                                await srcStream.CopyToAsync(destStream, cancellationToken);
                            }

                            extractedAny = true;
                        }
                    }
            }
        }

        bool hasAssets = soundTargetDirs.Any(targetDir =>
            Directory.Exists(targetDir) && Directory.GetFiles(targetDir).Length > 0);

        return hasAssets || extractedAny;
    }

    private static string FindSolutionRoot()
    {
        if (KongorWorkspaceResolver.TryResolve(out KongorWorkspaceResult? ws) && ws is not null)
        {
            return ws.Root;
        }

        if (BundledLayoutResolver.TryResolve(out BundledLayout? layout))
        {
            return layout.Root;
        }

        string current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "ProjectKongor.slnx")) ||
                File.Exists(Path.Combine(current, "NEXUS.DistributedApplication.slnx")) ||
                File.Exists(Path.Combine(current, "NEXUS.DistributedApplication.sln")))
                return current;

            DirectoryInfo? parent = Directory.GetParent(current);
            if (parent == null) break;

            current = parent.FullName;
        }

        return Directory.GetCurrentDirectory();
    }
}
