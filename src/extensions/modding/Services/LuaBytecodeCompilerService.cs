// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ASPIRE.Common.Prebaking.Generators;

public sealed record LuaCompilationResult(
    string SourceFile,
    string? TargetFile,
    bool Success,
    long SourceBytes,
    long BytecodeBytes,
    string? ErrorMessage);

public sealed record LuaCompilationSummary(
    int TotalFiles,
    int SucceededFiles,
    int FailedFiles,
    long TotalSourceBytes,
    long TotalBytecodeBytes,
    long ElapsedMilliseconds,
    IReadOnlyList<LuaCompilationResult> Results);

/// <summary>
/// Shared Lua 5.1 bytecode compiler and syntax verifier backed by <c>luac51.exe</c>.
/// Used by both <c>prebake-tool</c> and <c>Kongor.Modding</c> (<c>LuaScriptPatcher</c>, <c>ModPacker</c>, <c>ModManager</c>).
/// </summary>
public static class LuaBytecodeCompilerService
{
    private static readonly byte[] ExpectedHeader = [0x1B, 0x4C, 0x75, 0x61, 0x51, 0x00, 0x01, 0x04, 0x08, 0x04, 0x08, 0x00];

    public static bool TryResolveLuacPath(out string? resolvedPath, string? customPath = null)
    {
        resolvedPath = null;
        if (!string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath))
        {
            resolvedPath = Path.GetFullPath(customPath);
            return true;
        }

        string[] probeSubpaths =
        [
            Path.Combine("native", "build", "luac51_build", "Release", "luac51.exe"),
            Path.Combine("native", "tools", "luac51", "build", "Release", "luac51.exe"),
            Path.Combine("artifacts", "native-binaries", "windows-x64", "luac51.exe"),
            Path.Combine(".tools", "bin", "lua", "luac51.exe"),
            Path.Combine("tools", "lua", "luac51.exe"),
            "luac51.exe"
        ];

        foreach (string startRoot in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            string? current = startRoot;
            for (int depth = 0; depth < 10 && !string.IsNullOrEmpty(current); depth++)
            {
                foreach (string sub in probeSubpaths)
                {
                    string probe = Path.Combine(current, sub);
                    if (File.Exists(probe))
                    {
                        resolvedPath = Path.GetFullPath(probe);
                        return true;
                    }
                }

                string? parent = Path.GetDirectoryName(current);
                if (parent == current) break;
                current = parent;
            }
        }

        return false;
    }

    public static string ResolveLuacPath(string? customPath = null)
    {
        if (TryResolveLuacPath(out string? resolved, customPath) && resolved != null)
            return resolved;

        throw new FileNotFoundException(
            "Could not locate 64-bit Lua 5.1 compiler 'luac51.exe'. Run 'cmake --build native/build --config Release' to build it.");
    }

    /// <summary>
    /// Synchronously validates Lua 5.1 syntax using <c>luac51.exe -p</c>.
    /// </summary>
    public static (bool IsValid, string? ErrorMessage) ValidateSyntaxWithLuac(
        string luaSource,
        string chunkName = "chunk.lua",
        string? luacPath = null)
    {
        if (string.IsNullOrWhiteSpace(luaSource))
            return (true, null);

        // Already compiled Lua 5.1 bytecode header
        if (luaSource.Length >= 4 && luaSource[0] == '\x1B' && luaSource[1] == 'L' && luaSource[2] == 'u' && luaSource[3] == 'a')
            return (true, null);

        if (!TryResolveLuacPath(out string? compiler, luacPath) || compiler == null)
            return (true, null);

        string safeChunk = Path.GetFileName(chunkName);
        if (string.IsNullOrWhiteSpace(safeChunk)) safeChunk = "chunk.lua";
        string tempSrc = Path.Combine(Path.GetTempPath(), $"kongor_luac_check_{Guid.NewGuid():N}_{safeChunk}");

        try
        {
            ReadOnlySpan<char> contentSpan = luaSource.AsSpan();
            if (contentSpan.Length > 0 && contentSpan[0] == '\uFEFF')
                contentSpan = contentSpan[1..];

            UTF8Encoding utf8NoBom = new(false);
            File.WriteAllText(tempSrc, contentSpan.ToString(), utf8NoBom);

            ProcessStartInfo psi = new()
            {
                FileName = compiler,
                Arguments = $"-p \"{tempSrc}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using Process? proc = Process.Start(psi);
            if (proc == null)
                return (false, $"Failed to start luac51 process '{compiler}'.");

            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(5000);

            if (proc.ExitCode != 0)
            {
                string rawErr = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : stdout.Trim();
                rawErr = rawErr.Replace(tempSrc, chunkName, StringComparison.OrdinalIgnoreCase);
                return (false, $"[luac51] {rawErr} (Run scripts/verify_client_lua.ps1 -Path {chunkName} to diagnose)");
            }

            return (true, null);
        }
        finally
        {
            if (File.Exists(tempSrc))
            {
                try { File.Delete(tempSrc); } catch { /* best-effort temp cleanup */ }
            }
        }
    }

    /// <summary>
    /// Asynchronously validates Lua 5.1 syntax using <c>luac51.exe -p</c>.
    /// </summary>
    public static async Task<(bool IsValid, string? ErrorMessage)> ValidateSyntaxWithLuacAsync(
        string luaSource,
        string chunkName = "chunk.lua",
        string? luacPath = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(luaSource))
            return (true, null);

        if (luaSource.Length >= 4 && luaSource[0] == '\x1B' && luaSource[1] == 'L' && luaSource[2] == 'u' && luaSource[3] == 'a')
            return (true, null);

        if (!TryResolveLuacPath(out string? compiler, luacPath) || compiler == null)
            return (true, null);

        string safeChunk = Path.GetFileName(chunkName);
        if (string.IsNullOrWhiteSpace(safeChunk)) safeChunk = "chunk.lua";
        string tempSrc = Path.Combine(Path.GetTempPath(), $"kongor_luac_check_{Guid.NewGuid():N}_{safeChunk}");

        try
        {
            string normalized = luaSource.Length > 0 && luaSource[0] == '\uFEFF'
                ? luaSource[1..]
                : luaSource;

            UTF8Encoding utf8NoBom = new(false);
            await File.WriteAllTextAsync(tempSrc, normalized, utf8NoBom, ct).ConfigureAwait(false);

            ProcessStartInfo psi = new()
            {
                FileName = compiler,
                Arguments = $"-p \"{tempSrc}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using Process? proc = Process.Start(psi);
            if (proc == null)
                return (false, $"Failed to start luac51 process '{compiler}'.");

            string stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            string stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            if (proc.ExitCode != 0)
            {
                string rawErr = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : stdout.Trim();
                rawErr = rawErr.Replace(tempSrc, chunkName, StringComparison.OrdinalIgnoreCase);
                return (false, $"[luac51] {rawErr} (Run scripts/verify_client_lua.ps1 -Path {chunkName} to diagnose)");
            }

            return (true, null);
        }
        finally
        {
            if (File.Exists(tempSrc))
            {
                try { File.Delete(tempSrc); } catch { /* best-effort temp cleanup */ }
            }
        }
    }

    public static async Task<LuaCompilationResult> CompileFileAsync(
        string sourceFile,
        string targetFile,
        bool stripDebug = false,
        string? luacPath = null,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFile);

        string compiler = ResolveLuacPath(luacPath);
        FileInfo srcInfo = new(sourceFile);
        if (!srcInfo.Exists)
            return new LuaCompilationResult(sourceFile, targetFile, false, 0, 0, $"Source file not found: {sourceFile}");

        string? targetDir = Path.GetDirectoryName(targetFile);
        if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
            Directory.CreateDirectory(targetDir);

        string tempOutputFile = targetFile + ".tmp." + Guid.NewGuid().ToString("N");
        string effectiveInputFile = sourceFile;
        string? tempBomStripped = null;

        // Strip UTF-8 BOM if present so Lua 5.1 parser doesn't reject it
        byte[] first3 = new byte[3];
        await using (FileStream fs = File.OpenRead(sourceFile))
        {
            int r = await fs.ReadAsync(first3.AsMemory(0, 3), ct).ConfigureAwait(false);
            if (r == 3 && first3[0] == 0xEF && first3[1] == 0xBB && first3[2] == 0xBF)
            {
                tempBomStripped = Path.Combine(Path.GetTempPath(), $"kongor_nobom_{Guid.NewGuid():N}.lua");
                byte[] allBytes = await File.ReadAllBytesAsync(sourceFile, ct).ConfigureAwait(false);
                await File.WriteAllBytesAsync(tempBomStripped, allBytes.AsSpan(3).ToArray(), ct).ConfigureAwait(false);
                effectiveInputFile = tempBomStripped;
            }
        }

        string args = stripDebug
            ? $"-s -o \"{tempOutputFile}\" \"{effectiveInputFile}\""
            : $"-o \"{tempOutputFile}\" \"{effectiveInputFile}\"";

        ProcessStartInfo psi = new()
        {
            FileName = compiler,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using Process? proc = Process.Start(psi);
            if (proc == null)
                return new LuaCompilationResult(sourceFile, targetFile, false, srcInfo.Length, 0, "Failed to start luac51 process.");

            string stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            string stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            if (proc.ExitCode != 0)
            {
                if (File.Exists(tempOutputFile)) File.Delete(tempOutputFile);
                string err = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : stdout.Trim();
                logger?.LogWarning("Lua compilation failed for {File}: {Error}", sourceFile, err);
                return new LuaCompilationResult(sourceFile, targetFile, false, srcInfo.Length, 0, err);
            }

            if (!File.Exists(tempOutputFile))
                return new LuaCompilationResult(sourceFile, targetFile, false, srcInfo.Length, 0, "Compiler exited with 0 but output file missing.");

            // Validate bytecode header
            byte[] header = new byte[12];
            await using (FileStream fs = File.OpenRead(tempOutputFile))
            {
                int read = await fs.ReadAsync(header.AsMemory(0, 12), ct).ConfigureAwait(false);
                if (read < 12 || !header.AsSpan().SequenceEqual(ExpectedHeader))
                {
                    fs.Close();
                    File.Delete(tempOutputFile);
                    return new LuaCompilationResult(sourceFile, targetFile, false, srcInfo.Length, 0,
                        $"Invalid bytecode header emitted: {BitConverter.ToString(header)}");
                }
            }

            File.Move(tempOutputFile, targetFile, true);
            FileInfo targetInfo = new(targetFile);

            logger?.LogDebug("Compiled {Source} -> {Target} ({SourceSize} -> {TargetSize} bytes)",
                Path.GetFileName(sourceFile), Path.GetFileName(targetFile), srcInfo.Length, targetInfo.Length);

            return new LuaCompilationResult(sourceFile, targetFile, true, srcInfo.Length, targetInfo.Length, null);
        }
        catch (Exception ex)
        {
            if (File.Exists(tempOutputFile))
            {
                try { File.Delete(tempOutputFile); } catch { }
            }
            logger?.LogError(ex, "Unexpected exception compiling {File}", sourceFile);
            return new LuaCompilationResult(sourceFile, targetFile, false, srcInfo.Length, 0, ex.Message);
        }
        finally
        {
            if (tempBomStripped != null && File.Exists(tempBomStripped))
            {
                try { File.Delete(tempBomStripped); } catch { }
            }
        }
    }

    public static async Task<byte[]> CompileStringToBytecodeAsync(
        string luaSource,
        string chunkName = "chunk",
        bool stripDebug = false,
        string? luacPath = null,
        CancellationToken ct = default)
    {
        string tempSrc = Path.Combine(Path.GetTempPath(), $"kongor_src_{Guid.NewGuid():N}.lua");
        string tempDst = Path.Combine(Path.GetTempPath(), $"kongor_dst_{Guid.NewGuid():N}.luac");

        try
        {
            await File.WriteAllTextAsync(tempSrc, luaSource, ct).ConfigureAwait(false);
            var res = await CompileFileAsync(tempSrc, tempDst, stripDebug, luacPath, ct: ct).ConfigureAwait(false);
            if (!res.Success)
                throw new InvalidOperationException($"Failed to compile chunk '{chunkName}': {res.ErrorMessage}");

            return await File.ReadAllBytesAsync(tempDst, ct).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(tempSrc)) try { File.Delete(tempSrc); } catch { }
            if (File.Exists(tempDst)) try { File.Delete(tempDst); } catch { }
        }
    }

    public static async Task<LuaCompilationSummary> CompileDirectoryAsync(
        string sourceDirectory,
        string targetDirectory,
        bool inPlace = false,
        bool stripDebug = false,
        IEnumerable<string>? excludeFileNames = null,
        string? luacPath = null,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        if (!Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException($"Source directory not found: {sourceDirectory}");

        HashSet<string> excludes = new(excludeFileNames ?? [], StringComparer.OrdinalIgnoreCase);

        Stopwatch sw = Stopwatch.StartNew();
        string[] luaFiles = Directory.GetFiles(sourceDirectory, "*.lua", SearchOption.AllDirectories);

        List<LuaCompilationResult> results = new(luaFiles.Length);
        long totalSource = 0;
        long totalBytecode = 0;
        int succeeded = 0;
        int failed = 0;

        foreach (string file in luaFiles)
        {
            string fileName = Path.GetFileName(file);
            if (excludes.Contains(fileName))
            {
                logger?.LogDebug("Skipping excluded file from bytecode compilation: {File}", fileName);
                continue;
            }

            string rel = Path.GetRelativePath(sourceDirectory, file);
            string dest = inPlace ? file : Path.Combine(targetDirectory, rel);

            var res = await CompileFileAsync(file, dest, stripDebug, luacPath, logger, ct).ConfigureAwait(false);
            results.Add(res);

            totalSource += res.SourceBytes;
            if (res.Success)
            {
                succeeded++;
                totalBytecode += res.BytecodeBytes;
            }
            else
            {
                failed++;
            }
        }

        sw.Stop();
        return new LuaCompilationSummary(
            luaFiles.Length,
            succeeded,
            failed,
            totalSource,
            totalBytecode,
            sw.ElapsedMilliseconds,
            results);
    }
}
