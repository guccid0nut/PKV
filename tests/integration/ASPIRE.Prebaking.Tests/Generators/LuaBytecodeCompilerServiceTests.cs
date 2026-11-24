// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text;
using ASPIRE.Common.Prebaking.Generators;
using FluentAssertions;
using Xunit;

namespace ASPIRE.Prebaking.Tests.Generators;

public class LuaBytecodeCompilerServiceTests : IDisposable
{
    private readonly string _testDir;
    private static readonly byte[] ExpectedHeader = [0x1B, 0x4C, 0x75, 0x61, 0x51, 0x00, 0x01, 0x04, 0x08, 0x04, 0x08, 0x00];

    public LuaBytecodeCompilerServiceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"kongor_luatest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, true); } catch { }
        }
    }

    [Fact]
    public async Task CompileStringToBytecodeAsync_EmitsValid64BitLua51Header()
    {
        string luaSource = "local x = 10\nlocal y = 20\nreturn x + y\n";
        byte[] bytecode = await LuaBytecodeCompilerService.CompileStringToBytecodeAsync(luaSource, "test_add");

        bytecode.Should().NotBeNull();
        bytecode.Length.Should().BeGreaterThan(12);

        // Verify exact 12-byte Lua 5.1 x64 header
        bytecode.Take(12).Should().Equal(ExpectedHeader, "Emitted bytecode header must match 64-bit Lua 5.1 ABI");
    }

    [Fact]
    public async Task CompileFileAsync_WithUtf8Bom_StripsBomAndCompilesSuccessfully()
    {
        string srcFile = Path.Combine(_testDir, "script_with_bom.lua");
        string dstFile = Path.Combine(_testDir, "script_with_bom.luac");

        // Write with UTF-8 BOM
        byte[] bom = [0xEF, 0xBB, 0xBF];
        byte[] code = Encoding.UTF8.GetBytes("local greeting = 'Together As One'\nprintln(greeting)\n");
        byte[] combined = [.. bom, .. code];
        await File.WriteAllBytesAsync(srcFile, combined);

        var result = await LuaBytecodeCompilerService.CompileFileAsync(srcFile, dstFile);

        result.Success.Should().BeTrue($"Compilation failed: {result.ErrorMessage}");
        result.BytecodeBytes.Should().BeGreaterThan(12);
        File.Exists(dstFile).Should().BeTrue();

        byte[] header = new byte[12];
        await using (var fs = File.OpenRead(dstFile))
        {
            await fs.ReadExactlyAsync(header);
        }
        header.Should().Equal(ExpectedHeader);
    }

    [Fact]
    public async Task CompileDirectoryAsync_WithExclude_ExcludesSpecifiedFiles()
    {
        string subDir = Path.Combine(_testDir, "scripts");
        Directory.CreateDirectory(subDir);

        string f1 = Path.Combine(subDir, "main.lua");
        string f2 = Path.Combine(subDir, "tao.lua");
        string f3 = Path.Combine(subDir, "helper.lua");

        await File.WriteAllTextAsync(f1, "local a = 1");
        await File.WriteAllTextAsync(f2, "TAO = { Version = '2.2.0' }");
        await File.WriteAllTextAsync(f3, "local b = 2");

        var summary = await LuaBytecodeCompilerService.CompileDirectoryAsync(
            subDir,
            subDir,
            inPlace: true,
            excludeFileNames: ["tao.lua"]);

        summary.TotalFiles.Should().Be(3);
        summary.SucceededFiles.Should().Be(2);
        summary.FailedFiles.Should().Be(0);

        // Verify f2 (tao.lua) remained uncompiled plain text
        string taoContent = await File.ReadAllTextAsync(f2);
        taoContent.Should().Contain("TAO = { Version = '2.2.0' }");

        // Verify f1 (main.lua) was compiled to binary
        byte[] mainBytes = await File.ReadAllBytesAsync(f1);
        mainBytes.Take(4).Should().Equal([0x1B, 0x4C, 0x75, 0x61]);
    }

    [Fact]
    public async Task CompileFileAsync_WithSyntaxError_ReturnsFailureWithDetails()
    {
        string srcFile = Path.Combine(_testDir, "syntax_error.lua");
        string dstFile = Path.Combine(_testDir, "syntax_error.luac");

        await File.WriteAllTextAsync(srcFile, "function broken( +++ ) return end");

        var result = await LuaBytecodeCompilerService.CompileFileAsync(srcFile, dstFile);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        result.ErrorMessage.Should().Contain("syntax_error.lua:1");
    }
}
