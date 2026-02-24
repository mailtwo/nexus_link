using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using Miniscript;
using Uplink2.Blueprint;
using Uplink2.Runtime;
using Uplink2.Runtime.Syscalls;
using Uplink2.Vfs;
using Xunit;

#nullable enable

namespace Uplink2.Tests;

/// <summary>Unit tests for SystemCallProcessor command dispatch and program fallback contracts.</summary>
[Trait("Speed", "medium")]
public sealed class SystemCallTest
{
    /// <summary>Ensures unknown system-call names fall back to executable program resolution.</summary>
    [Fact]
    public void Execute_FallsBackToProgram_WhenSystemCallIsNotRegistered()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/tool", "exec:noop", fileKind: VfsFileKind.ExecutableHardcode);

        var result = Execute(harness, "tool");

        Assert.True(result.Ok);
        Assert.Equal(SystemCallErrorCode.None, result.Code);
    }

    /// <summary>Ensures registered system calls execute before program fallback candidates.</summary>
    [Fact]
    public void Execute_PrioritizesSystemCallOverProgramFallback()
    {
        var harness = CreateHarness(includeVfsModule: true, cwd: "/work");
        harness.BaseFileSystem.AddDirectory("/work");
        harness.BaseFileSystem.AddFile("/opt/bin/pwd", "exec:noop", fileKind: VfsFileKind.ExecutableHardcode);

        var result = Execute(harness, "pwd");

        Assert.True(result.Ok);
        Assert.Single(result.Lines);
        Assert.Equal("/work", result.Lines[0]);
    }

    /// <summary>Ensures commands containing '/' do not trigger PATH lookup.</summary>
    [Fact]
    public void Execute_CommandWithSlash_DoesNotSearchPath()
    {
        var harness = CreateHarness(includeVfsModule: true, cwd: "/home/guest");
        harness.BaseFileSystem.AddDirectory("/home/guest");
        harness.BaseFileSystem.AddFile("/opt/bin/tool", "exec:noop", fileKind: VfsFileKind.ExecutableHardcode);

        var result = Execute(harness, "./tool");

        Assert.False(result.Ok);
        Assert.Equal(SystemCallErrorCode.UnknownCommand, result.Code);
        Assert.Single(result.Lines);
        Assert.Contains("unknown command: ./tool", result.Lines[0], StringComparison.Ordinal);
    }

    /// <summary>Ensures PATH lookup order is cwd first then /opt/bin for bare command names.</summary>
    [Fact]
    public void Execute_BareCommand_ResolvesCwdBeforePath()
    {
        var harness = CreateHarness(includeVfsModule: true, cwd: "/home/guest");
        harness.BaseFileSystem.AddDirectory("/home/guest");
        harness.BaseFileSystem.AddFile("/home/guest/tool", "exec:missing_handler", fileKind: VfsFileKind.ExecutableHardcode);
        harness.BaseFileSystem.AddFile("/opt/bin/tool", "exec:noop", fileKind: VfsFileKind.ExecutableHardcode);

        var result = Execute(harness, "tool");

        Assert.False(result.Ok);
        Assert.Equal(SystemCallErrorCode.UnknownCommand, result.Code);
        Assert.Single(result.Lines);
        Assert.Contains("unknown command: tool", result.Lines[0], StringComparison.Ordinal);
    }

    /// <summary>Ensures PATH lookup reaches /opt/bin when cwd does not contain the command.</summary>
    [Fact]
    public void Execute_BareCommand_UsesPathWhenCwdCandidateMissing()
    {
        var harness = CreateHarness(includeVfsModule: true, cwd: "/home/guest");
        harness.BaseFileSystem.AddDirectory("/home/guest");
        harness.BaseFileSystem.AddFile("/opt/bin/tool", "exec:noop", fileKind: VfsFileKind.ExecutableHardcode);

        var result = Execute(harness, "tool");

        Assert.True(result.Ok);
        Assert.Equal(SystemCallErrorCode.None, result.Code);
    }

    /// <summary>Ensures relative path commands like ../bin/tool are executed via cwd normalization.</summary>
    [Fact]
    public void Execute_RelativePathCommand_IsResolvedFromCwd()
    {
        var harness = CreateHarness(includeVfsModule: true, cwd: "/home/guest/scripts");
        harness.BaseFileSystem.AddDirectory("/home/guest/scripts");
        harness.BaseFileSystem.AddFile("/home/guest/bin/tool", "exec:noop", fileKind: VfsFileKind.ExecutableHardcode);

        var result = Execute(harness, "../bin/tool");

        Assert.True(result.Ok);
        Assert.Equal(SystemCallErrorCode.None, result.Code);
    }

    /// <summary>Ensures program execution requires read and execute permissions simultaneously.</summary>
    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void Execute_ProgramFallback_RequiresReadAndExecute(bool canRead, bool canExecute, bool expectedOk)
    {
        var harness = CreateHarness(
            includeVfsModule: true,
            privilege: new PrivilegeConfig
            {
                Read = canRead,
                Write = true,
                Execute = canExecute,
            });

        harness.BaseFileSystem.AddFile("/opt/bin/tool", "exec:noop", fileKind: VfsFileKind.ExecutableHardcode);
        var result = Execute(harness, "tool");

        Assert.Equal(expectedOk, result.Ok);
        if (expectedOk)
        {
            Assert.Equal(SystemCallErrorCode.None, result.Code);
            return;
        }

        Assert.Equal(SystemCallErrorCode.PermissionDenied, result.Code);
        Assert.Single(result.Lines);
        Assert.Contains("permission denied: tool", result.Lines[0], StringComparison.Ordinal);
    }

    /// <summary>Ensures unregistered/empty hardcoded executable ids return unknown command to user.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("missing_handler")]
    [InlineData("noop")]
    [InlineData("miniscript")]
    public void Execute_HardcodedExecutable_UnknownIdReturnsUnknownCommand(string executablePayload)
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/tool", executablePayload, fileKind: VfsFileKind.ExecutableHardcode);

        var result = Execute(harness, "tool");

        Assert.False(result.Ok);
        Assert.Equal(SystemCallErrorCode.UnknownCommand, result.Code);
        Assert.Single(result.Lines);
        Assert.Contains("unknown command: tool", result.Lines[0], StringComparison.Ordinal);
    }

    /// <summary>Ensures malformed or missing exec-prefixed hardcoded executable payloads return unknown command.</summary>
    [Theory]
    [InlineData("exec:")]
    [InlineData("Exec:miniscript")]
    [InlineData("badprefix:miniscript")]
    [InlineData("exec:missing_handler")]
    public void Execute_HardcodedExecutable_InvalidPrefixedPayloadReturnsUnknownCommand(string executablePayload)
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/tool", executablePayload, fileKind: VfsFileKind.ExecutableHardcode);

        var result = Execute(harness, "tool");

        Assert.False(result.Ok);
        Assert.Equal(SystemCallErrorCode.UnknownCommand, result.Code);
        Assert.Single(result.Lines);
        Assert.Contains("unknown command: tool", result.Lines[0], StringComparison.Ordinal);
    }

    /// <summary>Ensures warning path method contains debug gate and GD.PushWarning call in IL.</summary>
    [Fact]
    public void WarnUnknownExecutableId_UsesDebugGateAndPushWarningCall()
    {
        var processorType = RequireRuntimeType("Uplink2.Runtime.Syscalls.SystemCallProcessor");
        var method = processorType.GetMethod("WarnUnknownExecutableId", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var calledMethods = ExtractCalledMethods((MethodInfo)method!);
        Assert.Contains(calledMethods, static called =>
            called.DeclaringType == typeof(WorldRuntime) &&
            string.Equals(called.Name, "get_DebugOption", StringComparison.Ordinal));
        Assert.Contains(calledMethods, static called =>
            string.Equals(called.DeclaringType?.FullName, "Godot.GD", StringComparison.Ordinal) &&
            string.Equals(called.Name, "PushWarning", StringComparison.Ordinal));
    }

    /// <summary>Ensures miniscript executable requires at least one script-path argument.</summary>
    [Fact]
    public void Execute_Miniscript_RequiresScriptArgument()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);

        var result = Execute(harness, "miniscript");

        Assert.False(result.Ok);
        Assert.Equal(SystemCallErrorCode.InvalidArgs, result.Code);
        Assert.Single(result.Lines);
        Assert.Contains("usage: miniscript <script>", result.Lines[0], StringComparison.Ordinal);
    }

    /// <summary>Ensures miniscript exposes argv/argc and passes trailing command arguments to script source.</summary>
    [Fact]
    public void Execute_Miniscript_PassesTrailingArgumentsToScript()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/argv.ms",
            """
            print "argc=" + str(argc)
            print "argv0=" + argv[0]
            print "argv1=" + argv[1]
            """,
            fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/argv.ms alpha beta");

        Assert.True(result.Ok);
        Assert.Equal(SystemCallErrorCode.None, result.Code);
        Assert.Contains(result.Lines, static line => string.Equals(line, "argc=2", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "argv0=alpha", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "argv1=beta", StringComparison.Ordinal));
    }

    /// <summary>Ensures miniscript returns not-found when target script path does not exist.</summary>
    [Fact]
    public void Execute_Miniscript_ReturnsNotFoundForMissingScript()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);

        var result = Execute(harness, "miniscript /scripts/missing.ms");

        Assert.False(result.Ok);
        Assert.Equal(SystemCallErrorCode.NotFound, result.Code);
    }

    /// <summary>Ensures miniscript returns not-file when the script path points to a directory.</summary>
    [Fact]
    public void Execute_Miniscript_ReturnsNotFileWhenTargetIsDirectory()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        harness.BaseFileSystem.AddDirectory("/scripts");

        var result = Execute(harness, "miniscript /scripts");

        Assert.False(result.Ok);
        Assert.Equal(SystemCallErrorCode.NotFile, result.Code);
    }

    /// <summary>Ensures miniscript rejects non-text source file kinds.</summary>
    [Fact]
    public void Execute_Miniscript_RejectsNonTextSource()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        harness.BaseFileSystem.AddFile("/scripts/job.ms", "print \"x\"", fileKind: VfsFileKind.ExecutableScript);

        var result = Execute(harness, "miniscript /scripts/job.ms");

        Assert.False(result.Ok);
        Assert.Equal(SystemCallErrorCode.InvalidArgs, result.Code);
        Assert.Single(result.Lines);
        Assert.Contains("miniscript source must be text", result.Lines[0], StringComparison.Ordinal);
    }

    /// <summary>Ensures miniscript successfully executes text source files.</summary>
    [Fact]
    public void Execute_Miniscript_ExecutesTextSource()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        harness.BaseFileSystem.AddFile("/scripts/hello.ms", "print \"Hello world!\"", fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/hello.ms");

        Assert.True(result.Ok);
        Assert.Equal(SystemCallErrorCode.None, result.Code);
        Assert.Contains(result.Lines, static line => line.Contains("Hello world!", StringComparison.Ordinal));
    }

    /// <summary>Ensures intrinsic rate limiter wraps only gameplay intrinsics (`ssh/fs/net/ftp`).</summary>
    [Fact]
    public void Execute_Miniscript_IntrinsicRateLimiter_WrapsGameplayIntrinsicsOnly()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile("/scripts/rate_limit_probe.ms", "print \"ok\"", fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/rate_limit_probe.ms");

        Assert.True(result.Ok);
        var wrappedIntrinsicIds = GetWrappedIntrinsicIdsFromRateLimiter();
        AssertIntrinsicWrapped(wrappedIntrinsicIds, "uplink_ssh_connect");
        AssertIntrinsicWrapped(wrappedIntrinsicIds, "uplink_fs_read");
        AssertIntrinsicWrapped(wrappedIntrinsicIds, "uplink_net_interfaces");
        AssertIntrinsicWrapped(wrappedIntrinsicIds, "uplink_net_scan");
        AssertIntrinsicWrapped(wrappedIntrinsicIds, "uplink_ftp_get");

        AssertIntrinsicNotWrapped(wrappedIntrinsicIds, "len");
        AssertIntrinsicNotWrapped(wrappedIntrinsicIds, "print");
        AssertIntrinsicNotWrapped(wrappedIntrinsicIds, "uplink_crypto_unixTime");
        AssertIntrinsicNotWrapped(wrappedIntrinsicIds, "uplink_term_exec");
    }

    /// <summary>Ensures miniscript string literals support common backslash escapes.</summary>
    [Fact]
    public void Execute_Miniscript_SupportsBackslashEscapesInStringLiterals()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/escapes.ms",
            """
            text = "first\r\nsecond"
            normalized = text.replace("\r\n", "\n")
            lines = normalized.split("\n")
            print "count=" + str(len(lines))
            print "line0=" + lines[0]
            print "line1=" + lines[1]
            print "tabLen=" + str(len("a\tb"))
            print "quote=a\"b"
            print "slash=a\\b"
            """,
            fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/escapes.ms");

        Assert.True(result.Ok);
        Assert.Equal(SystemCallErrorCode.None, result.Code);
        Assert.Contains(result.Lines, static line => string.Equals(line, "count=2", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "line0=first", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "line1=second", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "tabLen=3", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "quote=a\"b", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "slash=a\\b", StringComparison.Ordinal));
    }

    /// <summary>Ensures executable-script program path execution exposes argv/argc with trailing command arguments.</summary>
    [Fact]
    public void Execute_ExecutableScript_PassesArgumentsToScript()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile(
            "/opt/bin/argv_runner.ms",
            """
            print "argc=" + str(argc)
            print "argv0=" + argv[0]
            print "argv1=" + argv[1]
            """,
            fileKind: VfsFileKind.ExecutableScript);

        var result = Execute(harness, "argv_runner.ms left right");

        Assert.True(result.Ok);
        Assert.Equal(SystemCallErrorCode.None, result.Code);
        Assert.Contains(result.Lines, static line => string.Equals(line, "argc=2", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "argv0=left", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "argv1=right", StringComparison.Ordinal));
    }

    /// <summary>Ensures edit opens editor mode payload with the target file path and text content.</summary>
    [Fact]
    public void Execute_Edit_ReturnsEditorOpenPayload()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddDirectory("/notes");
        harness.BaseFileSystem.AddFile("/notes/todo.txt", "line-1\nline-2", fileKind: VfsFileKind.Text);

        var result = Execute(harness, "edit /notes/todo.txt");

        Assert.True(result.Ok);
        Assert.Equal(SystemCallErrorCode.None, result.Code);
        var payload = BuildTerminalCommandResponsePayload(result);
        Assert.Equal(true, payload["openEditor"]);
        Assert.Equal("/notes/todo.txt", payload["editorPath"]);
        Assert.Equal("line-1\nline-2", payload["editorContent"]);
        Assert.Equal(false, payload["editorReadOnly"]);
        Assert.Equal("text", payload["editorDisplayMode"]);
        Assert.Equal(true, payload["editorPathExists"]);
    }

    /// <summary>Ensures edit on an existing file requires read privilege.</summary>
    [Fact]
    public void Execute_Edit_Fails_WhenReadPrivilegeMissing_ForExistingFile()
    {
        var harness = CreateHarness(
            includeVfsModule: true,
            privilege: new PrivilegeConfig
            {
                Read = false,
                Write = true,
                Execute = true,
            });
        harness.BaseFileSystem.AddDirectory("/notes");
        harness.BaseFileSystem.AddFile("/notes/todo.txt", "line-1\nline-2", fileKind: VfsFileKind.Text);

        var result = Execute(harness, "edit /notes/todo.txt");

        Assert.False(result.Ok);
        Assert.Equal(SystemCallErrorCode.PermissionDenied, result.Code);
        Assert.Contains("permission denied: edit", result.Lines[0], StringComparison.Ordinal);
    }

    /// <summary>Ensures edit opens existing text files in read-only mode when write privilege is missing.</summary>
    [Fact]
    public void Execute_Edit_TextFile_ReadOnly_WhenWritePrivilegeMissing()
    {
        var harness = CreateHarness(
            includeVfsModule: true,
            privilege: new PrivilegeConfig
            {
                Read = true,
                Write = false,
                Execute = true,
            });
        harness.BaseFileSystem.AddDirectory("/notes");
        harness.BaseFileSystem.AddFile("/notes/todo.txt", "line-1\nline-2", fileKind: VfsFileKind.Text);

        var result = Execute(harness, "edit /notes/todo.txt");

        Assert.True(result.Ok);
        var payload = BuildTerminalCommandResponsePayload(result);
        Assert.Equal(true, payload["openEditor"]);
        Assert.Equal(true, payload["editorReadOnly"]);
        Assert.Equal("text", payload["editorDisplayMode"]);
        Assert.Equal(true, payload["editorPathExists"]);
    }

    /// <summary>Ensures non-text files open with pseudo-hex preview and read-only mode.</summary>
    [Fact]
    public void Execute_Edit_NonTextFile_OpensPseudoHexReadOnly()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddDirectory("/notes");
        harness.BaseFileSystem.AddFile("/notes/raw.bin", new string('A', 1024), fileKind: VfsFileKind.Binary);

        var result = Execute(harness, "edit /notes/raw.bin");

        Assert.True(result.Ok);
        var payload = BuildTerminalCommandResponsePayload(result);
        Assert.Equal(true, payload["openEditor"]);
        Assert.Equal(true, payload["editorReadOnly"]);
        Assert.Equal("hex", payload["editorDisplayMode"]);
        Assert.Equal(true, payload["editorPathExists"]);

        var hexView = (string)payload["editorContent"];
        Assert.False(string.IsNullOrEmpty(hexView));
        var lines = hexView.Split('\n');
        Assert.All(lines, static line => Assert.Equal(100, line.Length));
        Assert.All(lines, static line => Assert.Matches("^[0-9a-f]+$", line));
        Assert.True(hexView.Replace("\n", string.Empty, StringComparison.Ordinal).Length <= 20000);
    }

    /// <summary>Ensures edit pseudo-hex length follows logical file-size override instead of raw payload bytes.</summary>
    [Fact]
    public void Execute_Edit_NonTextFile_UsesLogicalSizeOverrideForPseudoHexLength()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddDirectory("/notes");
        harness.Server.DiskOverlay.WriteFile(
            "/notes/hard.bin",
            "id",
            fileKind: VfsFileKind.ExecutableHardcode,
            size: 4096);

        var result = Execute(harness, "edit /notes/hard.bin");

        Assert.True(result.Ok);
        var payload = BuildTerminalCommandResponsePayload(result);
        var hexView = (string)payload["editorContent"];
        var hexChars = hexView.Replace("\n", string.Empty, StringComparison.Ordinal);
        Assert.Equal(8200, hexChars.Length);
    }

    /// <summary>Ensures pseudo-hex seed does not include path so identical payloads render the same in one world.</summary>
    [Fact]
    public void Execute_Edit_NonTextFile_PathDoesNotAffectPseudoHex()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.World.WorldSeed = 123456789;
        harness.BaseFileSystem.AddDirectory("/notes");
        harness.BaseFileSystem.AddDirectory("/notes/sub");
        harness.Server.DiskOverlay.WriteFile(
            "/notes/a.bin",
            "id",
            fileKind: VfsFileKind.ExecutableHardcode,
            size: 4096);
        harness.Server.DiskOverlay.WriteFile(
            "/notes/sub/b.bin",
            "id",
            fileKind: VfsFileKind.ExecutableHardcode,
            size: 4096);

        var a = Execute(harness, "edit /notes/a.bin");
        var b = Execute(harness, "edit /notes/sub/b.bin");

        Assert.True(a.Ok);
        Assert.True(b.Ok);
        var aPayload = BuildTerminalCommandResponsePayload(a);
        var bPayload = BuildTerminalCommandResponsePayload(b);
        Assert.Equal((string)aPayload["editorContent"], (string)bPayload["editorContent"]);
    }

    /// <summary>Ensures worldSeed participates in pseudo-hex seed so different worlds render different views.</summary>
    [Fact]
    public void Execute_Edit_NonTextFile_WorldSeedAffectsPseudoHex()
    {
        var worldA = CreateHarness(includeVfsModule: true);
        worldA.World.WorldSeed = 111;
        worldA.BaseFileSystem.AddDirectory("/notes");
        worldA.Server.DiskOverlay.WriteFile(
            "/notes/a.bin",
            "id",
            fileKind: VfsFileKind.ExecutableHardcode,
            size: 4096);

        var worldB = CreateHarness(includeVfsModule: true);
        worldB.World.WorldSeed = 222;
        worldB.BaseFileSystem.AddDirectory("/notes");
        worldB.Server.DiskOverlay.WriteFile(
            "/notes/a.bin",
            "id",
            fileKind: VfsFileKind.ExecutableHardcode,
            size: 4096);

        var a = Execute(worldA, "edit /notes/a.bin");
        var b = Execute(worldB, "edit /notes/a.bin");
        var aPayload = BuildTerminalCommandResponsePayload(a);
        var bPayload = BuildTerminalCommandResponsePayload(b);

        Assert.NotEqual((string)aPayload["editorContent"], (string)bPayload["editorContent"]);
    }

    /// <summary>Ensures edit on missing path requires write privilege.</summary>
    [Fact]
    public void Execute_Edit_MissingPath_Fails_WhenWritePrivilegeMissing()
    {
        var harness = CreateHarness(
            includeVfsModule: true,
            privilege: new PrivilegeConfig
            {
                Read = true,
                Write = false,
                Execute = true,
            });
        harness.BaseFileSystem.AddDirectory("/notes");

        var result = Execute(harness, "edit /notes/new.txt");

        Assert.False(result.Ok);
        Assert.Equal(SystemCallErrorCode.PermissionDenied, result.Code);
        Assert.Contains("permission denied: edit", result.Lines[0], StringComparison.Ordinal);
    }

    /// <summary>Ensures edit can open a new empty buffer when path is missing and write privilege exists.</summary>
    [Fact]
    public void Execute_Edit_MissingPath_OpensEmptyBuffer_WhenWritePrivilegePresent()
    {
        var harness = CreateHarness(
            includeVfsModule: true,
            privilege: new PrivilegeConfig
            {
                Read = false,
                Write = true,
                Execute = true,
            });
        harness.BaseFileSystem.AddDirectory("/notes");

        var result = Execute(harness, "edit /notes/new.txt");

        Assert.True(result.Ok);
        var payload = BuildTerminalCommandResponsePayload(result);
        Assert.Equal(true, payload["openEditor"]);
        Assert.Equal("/notes/new.txt", payload["editorPath"]);
        Assert.Equal(string.Empty, payload["editorContent"]);
        Assert.Equal(false, payload["editorReadOnly"]);
        Assert.Equal("text", payload["editorDisplayMode"]);
        Assert.Equal(false, payload["editorPathExists"]);
    }

    /// <summary>Ensures edit fails immediately when missing path parent directory does not exist.</summary>
    [Fact]
    public void Execute_Edit_MissingPath_Fails_WhenParentDirectoryMissing()
    {
        var harness = CreateHarness(includeVfsModule: true);

        var result = Execute(harness, "edit /missing/new.txt");

        Assert.False(result.Ok);
        Assert.Equal(SystemCallErrorCode.NotFound, result.Code);
        Assert.Contains("/missing", result.Lines[0], StringComparison.Ordinal);
    }

    /// <summary>Ensures SaveEditorContent overwrites existing text files when write privilege exists.</summary>
    [Fact]
    public void SaveEditorContent_OverwritesExistingTextFile()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddDirectory("/notes");
        harness.BaseFileSystem.AddFile("/notes/todo.txt", "old", fileKind: VfsFileKind.Text);

        var (result, savedPath) = SaveEditorContentInternal(
            harness.World,
            harness.Server.NodeId,
            harness.UserId,
            harness.Cwd,
            "/notes/todo.txt",
            "new");

        Assert.True(result.Ok);
        Assert.Equal(SystemCallErrorCode.None, result.Code);
        Assert.Equal("/notes/todo.txt", savedPath);
        Assert.True(harness.Server.DiskOverlay.TryReadFileText("/notes/todo.txt", out var savedContent));
        Assert.Equal("new", savedContent);
    }

    /// <summary>Ensures SaveEditorContent overwrite path does not emit fileAcquire.</summary>
    [Fact]
    public void SaveEditorContent_OverwritesExistingTextFile_DoesNotEmitFileAcquire()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddDirectory("/notes");
        harness.BaseFileSystem.AddFile("/notes/todo.txt", "old", fileKind: VfsFileKind.Text);

        var (result, savedPath) = SaveEditorContentInternal(
            harness.World,
            harness.Server.NodeId,
            harness.UserId,
            harness.Cwd,
            "/notes/todo.txt",
            "new");

        Assert.True(result.Ok);
        Assert.Equal(SystemCallErrorCode.None, result.Code);
        Assert.Equal("/notes/todo.txt", savedPath);
        var events = DrainQueuedGameEvents(harness.World);
        var fileAcquireEvents = events
            .Where(static gameEvent => string.Equals((string)GetPropertyValue(gameEvent, "EventType"), "fileAcquire", StringComparison.Ordinal))
            .ToList();
        Assert.Empty(fileAcquireEvents);
    }

    /// <summary>Ensures SaveEditorContent creates missing text files when write privilege exists.</summary>
    [Fact]
    public void SaveEditorContent_CreatesMissingTextFile()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddDirectory("/notes");

        var (result, savedPath) = SaveEditorContentInternal(
            harness.World,
            harness.Server.NodeId,
            harness.UserId,
            harness.Cwd,
            "/notes/new.txt",
            "created");

        Assert.True(result.Ok);
        Assert.Equal(SystemCallErrorCode.None, result.Code);
        Assert.Equal("/notes/new.txt", savedPath);
        Assert.True(harness.Server.DiskOverlay.TryResolveEntry("/notes/new.txt", out var savedEntry));
        Assert.Equal(VfsFileKind.Text, savedEntry.FileKind);
        Assert.True(harness.Server.DiskOverlay.TryReadFileText("/notes/new.txt", out var savedContent));
        Assert.Equal("created", savedContent);
    }

    /// <summary>Ensures SaveEditorContent create path emits fileAcquire with edit.save transfer method.</summary>
    [Fact]
    public void SaveEditorContent_CreatesMissingTextFile_EmitsFileAcquire()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddDirectory("/notes");

        var (result, savedPath) = SaveEditorContentInternal(
            harness.World,
            harness.Server.NodeId,
            harness.UserId,
            harness.Cwd,
            "/notes/new.txt",
            "created");

        Assert.True(result.Ok);
        Assert.Equal(SystemCallErrorCode.None, result.Code);
        Assert.Equal("/notes/new.txt", savedPath);

        var expectedUserKey = harness.Server.Users
            .Single(pair => string.Equals(pair.Value.UserId, harness.UserId, StringComparison.Ordinal))
            .Key;
        var events = DrainQueuedGameEvents(harness.World);
        var fileAcquireEvents = events
            .Where(static gameEvent => string.Equals((string)GetPropertyValue(gameEvent, "EventType"), "fileAcquire", StringComparison.Ordinal))
            .ToList();
        var fileAcquire = Assert.Single(fileAcquireEvents);
        var payload = GetPropertyValue(fileAcquire, "Payload");
        Assert.Equal(harness.Server.NodeId, (string?)GetPropertyValue(payload!, "FromNodeId"));
        Assert.Equal(expectedUserKey, (string?)GetPropertyValue(payload!, "UserKey"));
        Assert.Equal("new.txt", (string?)GetPropertyValue(payload!, "FileName"));
        Assert.Equal("/notes/new.txt", (string?)GetPropertyValue(payload!, "LocalPath"));
        Assert.Null(GetPropertyValue(payload!, "RemotePath"));
        Assert.Equal("edit.save", (string?)GetPropertyValue(payload!, "TransferMethod"));
    }

    /// <summary>Ensures SaveEditorContent rejects non-text files as read-only buffers.</summary>
    [Fact]
    public void SaveEditorContent_Fails_ForNonTextFiles()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddDirectory("/notes");
        harness.BaseFileSystem.AddFile("/notes/raw.bin", "AAAA", fileKind: VfsFileKind.Binary);

        var (result, savedPath) = SaveEditorContentInternal(
            harness.World,
            harness.Server.NodeId,
            harness.UserId,
            harness.Cwd,
            "/notes/raw.bin",
            "BBBB");

        Assert.False(result.Ok);
        Assert.Equal(SystemCallErrorCode.InvalidArgs, result.Code);
        Assert.Equal(string.Empty, savedPath);
        Assert.Contains(result.Lines, static line => line.Contains("read-only buffer", StringComparison.Ordinal));
        Assert.True(harness.Server.DiskOverlay.TryReadFileText("/notes/raw.bin", out var originalContent));
        Assert.Equal("AAAA", originalContent);
    }

    /// <summary>Ensures SaveEditorContent returns permission denied on existing text files without write privilege.</summary>
    [Fact]
    public void SaveEditorContent_Fails_WhenWritePrivilegeMissing()
    {
        var harness = CreateHarness(
            includeVfsModule: true,
            privilege: new PrivilegeConfig
            {
                Read = true,
                Write = false,
                Execute = true,
            });
        harness.BaseFileSystem.AddDirectory("/notes");
        harness.BaseFileSystem.AddFile("/notes/todo.txt", "old", fileKind: VfsFileKind.Text);

        var (result, savedPath) = SaveEditorContentInternal(
            harness.World,
            harness.Server.NodeId,
            harness.UserId,
            harness.Cwd,
            "/notes/todo.txt",
            "new");

        Assert.False(result.Ok);
        Assert.Equal(SystemCallErrorCode.PermissionDenied, result.Code);
        Assert.Equal(string.Empty, savedPath);
        Assert.Contains(result.Lines, static line => line.Contains("permission denied: edit", StringComparison.Ordinal));
    }

    /// <summary>Ensures SaveEditorContent fails when parent directory is missing.</summary>
    [Fact]
    public void SaveEditorContent_Fails_WhenParentDirectoryMissing()
    {
        var harness = CreateHarness(includeVfsModule: true);

        var (result, savedPath) = SaveEditorContentInternal(
            harness.World,
            harness.Server.NodeId,
            harness.UserId,
            harness.Cwd,
            "/missing/new.txt",
            "new");

        Assert.False(result.Ok);
        Assert.Equal(SystemCallErrorCode.NotFound, result.Code);
        Assert.Equal(string.Empty, savedPath);
        Assert.Contains(result.Lines, static line => line.Contains("/missing", StringComparison.Ordinal));
    }

    /// <summary>Ensures SaveEditorContent fails when target path resolves to a directory.</summary>
    [Fact]
    public void SaveEditorContent_Fails_WhenTargetIsDirectory()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddDirectory("/notes");

        var (result, savedPath) = SaveEditorContentInternal(
            harness.World,
            harness.Server.NodeId,
            harness.UserId,
            harness.Cwd,
            "/notes",
            "new");

        Assert.False(result.Ok);
        Assert.Equal(SystemCallErrorCode.NotFile, result.Code);
        Assert.Equal(string.Empty, savedPath);
    }

    /// <summary>Ensures renamed editor command no longer accepts legacy vim command text.</summary>
    [Fact]
    public void Execute_VimCommand_ReturnsUnknownCommand()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddDirectory("/notes");
        harness.BaseFileSystem.AddFile("/notes/todo.txt", "line-1\nline-2", fileKind: VfsFileKind.Text);

        var result = Execute(harness, "vim /notes/todo.txt");

        Assert.False(result.Ok);
        Assert.Equal(SystemCallErrorCode.UnknownCommand, result.Code);
        Assert.Single(result.Lines);
        Assert.Contains("unknown command: vim", result.Lines[0], StringComparison.Ordinal);
    }

    /// <summary>Ensures ssh.connect intrinsic returns a session DTO and ssh.disconnect is idempotent.</summary>
    [Fact]
    public void Execute_Miniscript_SshConnect_ReturnsSessionDto_AndDisconnectIsIdempotent()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        var remote = AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, "pw");
        remote.DiskOverlay.AddDirectory("/etc");
        remote.DiskOverlay.WriteFile("/etc/motd", "remote motd", fileKind: VfsFileKind.Text);
        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/ssh_ok.ms",
            """
            result = ssh.connect("10.0.1.20", "guest", "pw")
            print "ok=" + str(result.ok)
            print "code=" + result.code
            print "kind=" + result.session.kind
            print "sessionNodeId=" + result.session.sessionNodeId
            print "userId=" + result.session.userId
            print "hasUserKey=" + str(hasIndex(result.session, "userKey"))
            d1 = ssh.disconnect(result.session)
            d2 = ssh.disconnect(result.session)
            print "d1=" + str(d1.disconnected)
            print "d2=" + str(d2.disconnected)
            """,
            fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/ssh_ok.ms");

        Assert.True(result.Ok);
        Assert.Contains(result.Lines, static line => string.Equals(line, "ok=1", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "code=None", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "kind=sshSession", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "sessionNodeId=node-2", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "userId=guest", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "hasUserKey=0", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "d1=1", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "d2=0", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Lines, static line => string.Equals(line, "remote motd", StringComparison.Ordinal));
        Assert.Empty(remote.Sessions);

        var events = DrainQueuedGameEvents(harness.World);
        var privilegeEvents = events
            .Where(static gameEvent => string.Equals((string)GetPropertyValue(gameEvent, "EventType"), "privilegeAcquire", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(3, privilegeEvents.Count);
        var payloads = privilegeEvents.Select(static gameEvent => GetPropertyValue(gameEvent, "Payload")).ToList();
        Assert.All(payloads, payload => Assert.Equal("ssh.connect", (string?)GetPropertyValue(payload, "Via")));
    }

    /// <summary>Ensures ssh.connect intrinsic returns structured failure map on connection failure.</summary>
    [Fact]
    public void Execute_Miniscript_SshConnect_ReturnsStructuredFailure()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/ssh_fail.ms",
            """
            result = ssh.connect("10.0.1.99", "guest", "pw")
            print "ok=" + str(result.ok)
            print "code=" + result.code
            print "sessionNull=" + str(result.session == null)
            """,
            fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/ssh_fail.ms");

        Assert.True(result.Ok);
        Assert.Contains(result.Lines, static line => string.Equals(line, "ok=0", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "code=NotFound", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "sessionNull=1", StringComparison.Ordinal));
        var events = DrainQueuedGameEvents(harness.World);
        Assert.Empty(events);
    }

    /// <summary>Ensures ssh.connect applies connectionRateLimiter and blocks threshold-exceed attempts before auth success.</summary>
    [Fact]
    public void Execute_Miniscript_SshConnect_ConnectionRateLimiter_BlocksAfterThreshold()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        var remote = AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, "pw");
        AddConnectionRateLimiterDaemon(
            remote,
            monitorMs: 60000,
            threshold: 1,
            blockMs: 60000,
            rateLimit: 100,
            recoveryMs: 60000);
        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/ssh_rate_limiter.ms",
            """
            r1 = ssh.connect("10.0.1.20", "guest", "wrong")
            print "r1ok=" + str(r1.ok)
            print "r1code=" + r1.code
            r2 = ssh.connect("10.0.1.20", "guest", "pw")
            print "r2ok=" + str(r2.ok)
            print "r2code=" + r2.code
            print "r2err=" + r2.err
            """,
            fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/ssh_rate_limiter.ms");

        Assert.True(result.Ok);
        Assert.Contains(result.Lines, static line => string.Equals(line, "r1ok=0", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "r1code=PermissionDenied", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "r2ok=0", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "r2code=PermissionDenied", StringComparison.Ordinal));
        Assert.Contains(
            result.Lines,
            static line => string.Equals(
                line,
                "r2err=connectionRateLimiter daemon blocked this connection attempt.",
                StringComparison.Ordinal));
        Assert.Empty(remote.Sessions);
    }

    /// <summary>Ensures ssh.connect supports opts.session chaining and returns an sshRoute payload.</summary>
    [Fact]
    public void Execute_Miniscript_SshConnect_WithSessionOption_ReturnsRoute()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        var hopB = AddRemoteServer(
            harness,
            "node-2",
            "hop-b",
            "10.1.0.20",
            AuthMode.Static,
            "pw",
            exposure: PortExposure.Public,
            netId: "alpha");
        var hopC = AddRemoteServer(
            harness,
            "node-3",
            "hop-c",
            "10.1.0.30",
            AuthMode.Static,
            "pw",
            exposure: PortExposure.Lan,
            netId: "alpha");
        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/ssh_route_basic.ms",
            """
            r1 = ssh.connect("10.1.0.20", "guest", "pw")
            opts = {}
            opts["session"] = r1.session
            r2 = ssh.connect("10.1.0.30", "guest", "pw", opts)
            print "r2ok=" + str(r2.ok)
            print "routeKind=" + r2.route.kind
            print "routeHop=" + str(r2.route.hopCount)
            print "routeLast=" + r2.route.lastSession.sessionNodeId
            print "r1SourceNode=" + r1.session.sourceNodeId
            print "r1SourceUser=" + r1.session.sourceUserId
            print "r1SourceCwd=" + r1.session.sourceCwd
            print "r2s0SourceNode=" + r2.route.sessions[0].sourceNodeId
            print "r2s1SourceNode=" + r2.route.sessions[1].sourceNodeId
            print "r2s1SourceUser=" + r2.route.sessions[1].sourceUserId
            print "r2s1SourceCwd=" + r2.route.sessions[1].sourceCwd
            print "prefixCount=" + str(len(r2.route.prefixRoutes))
            print "prefix0Hop=" + str(r2.route.prefixRoutes[0].hopCount)
            d = ssh.disconnect(r2.route)
            print "closed=" + str(d.summary.closed)
            print "alreadyClosed=" + str(d.summary.alreadyClosed)
            """,
            fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/ssh_route_basic.ms");

        Assert.True(result.Ok);
        Assert.Contains(result.Lines, static line => string.Equals(line, "r2ok=1", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "routeKind=sshRoute", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "routeHop=2", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "routeLast=node-3", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "r1SourceNode=node-1", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "r1SourceUser=guest", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "r1SourceCwd=/", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "r2s0SourceNode=node-1", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "r2s1SourceNode=node-2", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "r2s1SourceUser=guest", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "r2s1SourceCwd=/", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "prefixCount=1", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "prefix0Hop=1", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "closed=2", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "alreadyClosed=0", StringComparison.Ordinal));
        Assert.Empty(hopB.Sessions);
        Assert.Empty(hopC.Sessions);
    }

    /// <summary>Ensures ssh.connect accepts route input and appends a new hop from the route tail.</summary>
    [Fact]
    public void Execute_Miniscript_SshConnect_WithRouteOption_AppendsHop()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        var hopB = AddRemoteServer(
            harness,
            "node-2",
            "hop-b",
            "10.1.0.20",
            AuthMode.Static,
            "pw",
            exposure: PortExposure.Public,
            netId: "alpha");
        var hopC = AddRemoteServer(
            harness,
            "node-3",
            "hop-c",
            "10.1.0.30",
            AuthMode.Static,
            "pw",
            exposure: PortExposure.Lan,
            netId: "alpha");
        var hopD = AddRemoteServer(
            harness,
            "node-4",
            "hop-d",
            "10.1.0.40",
            AuthMode.Static,
            "pw",
            exposure: PortExposure.Lan,
            netId: "alpha");
        var hopE = AddRemoteServer(
            harness,
            "node-5",
            "hop-e",
            "10.1.0.50",
            AuthMode.Static,
            "pw",
            exposure: PortExposure.Lan,
            netId: "alpha");
        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/ssh_route_append.ms",
            """
            r1 = ssh.connect("10.1.0.20", "guest", "pw")
            o2 = {}
            o2["session"] = r1.session
            r2 = ssh.connect("10.1.0.30", "guest", "pw", o2)
            o3 = {}
            o3["session"] = r2.route
            r3 = ssh.connect("10.1.0.40", "guest", "pw", o3)
            o4 = {}
            o4["session"] = r3.route.prefixRoutes[0]
            r4 = ssh.connect("10.1.0.50", "guest", "pw", o4)
            print "r3Hop=" + str(r3.route.hopCount)
            print "r3Last=" + r3.route.lastSession.sessionNodeId
            print "r4Hop=" + str(r4.route.hopCount)
            print "r4Last=" + r4.route.lastSession.sessionNodeId
            d3 = ssh.disconnect(r3.route)
            d4 = ssh.disconnect(r4.route)
            print "d3Closed=" + str(d3.summary.closed)
            print "d4Closed=" + str(d4.summary.closed)
            print "d4Already=" + str(d4.summary.alreadyClosed)
            """,
            fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/ssh_route_append.ms");

        Assert.True(result.Ok);
        Assert.Contains(result.Lines, static line => string.Equals(line, "r3Hop=3", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "r3Last=node-4", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "r4Hop=2", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "r4Last=node-5", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "d3Closed=3", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "d4Closed=1", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "d4Already=1", StringComparison.Ordinal));
        Assert.Empty(hopB.Sessions);
        Assert.Empty(hopC.Sessions);
        Assert.Empty(hopD.Sessions);
        Assert.Empty(hopE.Sessions);
    }

    /// <summary>Ensures ssh.disconnect(route) is idempotent and reports summary counters.</summary>
    [Fact]
    public void Execute_Miniscript_SshDisconnectRoute_IsIdempotent()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        var hopB = AddRemoteServer(
            harness,
            "node-2",
            "hop-b",
            "10.1.0.20",
            AuthMode.Static,
            "pw",
            exposure: PortExposure.Public,
            netId: "alpha");
        var hopC = AddRemoteServer(
            harness,
            "node-3",
            "hop-c",
            "10.1.0.30",
            AuthMode.Static,
            "pw",
            exposure: PortExposure.Lan,
            netId: "alpha");
        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/ssh_route_disconnect_twice.ms",
            """
            r1 = ssh.connect("10.1.0.20", "guest", "pw")
            opts = {}
            opts["session"] = r1.session
            r2 = ssh.connect("10.1.0.30", "guest", "pw", opts)
            d1 = ssh.disconnect(r2.route)
            d2 = ssh.disconnect(r2.route)
            print "d1Req=" + str(d1.summary.requested)
            print "d1Closed=" + str(d1.summary.closed)
            print "d1Already=" + str(d1.summary.alreadyClosed)
            print "d2Req=" + str(d2.summary.requested)
            print "d2Closed=" + str(d2.summary.closed)
            print "d2Already=" + str(d2.summary.alreadyClosed)
            """,
            fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/ssh_route_disconnect_twice.ms");

        Assert.True(result.Ok);
        Assert.Contains(result.Lines, static line => string.Equals(line, "d1Req=2", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "d1Closed=2", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "d1Already=0", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "d2Req=2", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "d2Closed=0", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "d2Already=2", StringComparison.Ordinal));
        Assert.Empty(hopB.Sessions);
        Assert.Empty(hopC.Sessions);
    }

    /// <summary>Ensures malformed route passed via opts.session returns InvalidArgs without opening sessions.</summary>
    [Fact]
    public void Execute_Miniscript_SshConnect_WithMalformedRoute_ReturnsInvalidArgs()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        var hopB = AddRemoteServer(
            harness,
            "node-2",
            "hop-b",
            "10.1.0.20",
            AuthMode.Static,
            "pw",
            exposure: PortExposure.Public,
            netId: "alpha");
        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/ssh_route_invalid.ms",
            """
            bad = {}
            bad["kind"] = "sshRoute"
            bad["version"] = 1
            bad["sessions"] = []
            bad["prefixRoutes"] = []
            bad["lastSession"] = null
            bad["hopCount"] = 0
            opts = {}
            opts["session"] = bad
            r = ssh.connect("10.1.0.20", "guest", "pw", opts)
            print "ok=" + str(r.ok)
            print "code=" + r.code
            """,
            fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/ssh_route_invalid.ms");

        Assert.True(result.Ok);
        Assert.Contains(result.Lines, static line => string.Equals(line, "ok=0", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "code=InvalidArgs", StringComparison.Ordinal));
        Assert.Empty(hopB.Sessions);
    }

    /// <summary>Ensures ssh.exec executes in session endpoint and returns stdout with exitCode.</summary>
    [Fact]
    public void Execute_Miniscript_SshExec_WithSession_ReturnsStdoutAndExitCode()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        var remote = AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, "pw");
        remote.DiskOverlay.AddDirectory("/etc");
        remote.DiskOverlay.WriteFile("/etc/motd", "remote motd", fileKind: VfsFileKind.Text);

        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/ssh_exec_session.ms",
            """
            r = ssh.connect("10.0.1.20", "guest", "pw")
            e = ssh.exec(r.session, "cat /etc/motd")
            print "ok=" + str(e.ok)
            print "code=" + e.code
            print "exitCode=" + str(e.exitCode)
            print "stdout=" + e.stdout
            d = ssh.disconnect(r.session)
            """,
            fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/ssh_exec_session.ms");

        Assert.True(result.Ok);
        Assert.Contains(result.Lines, static line => string.Equals(line, "ok=1", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "code=None", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "exitCode=0", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "stdout=remote motd", StringComparison.Ordinal));
        Assert.Empty(remote.Sessions);
    }

    /// <summary>Ensures ssh.exec(route, ...) resolves execution target from route.lastSession.</summary>
    [Fact]
    public void Execute_Miniscript_SshExec_WithRoute_UsesRouteLastSession()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        var hopB = AddRemoteServer(
            harness,
            "node-2",
            "hop-b",
            "10.1.0.20",
            AuthMode.Static,
            "pw",
            exposure: PortExposure.Public,
            netId: "alpha");
        hopB.DiskOverlay.AddDirectory("/etc");
        hopB.DiskOverlay.WriteFile("/etc/motd", "motd-b", fileKind: VfsFileKind.Text);
        var hopC = AddRemoteServer(
            harness,
            "node-3",
            "hop-c",
            "10.1.0.30",
            AuthMode.Static,
            "pw",
            exposure: PortExposure.Lan,
            netId: "alpha");
        hopC.DiskOverlay.AddDirectory("/etc");
        hopC.DiskOverlay.WriteFile("/etc/motd", "motd-c", fileKind: VfsFileKind.Text);

        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/ssh_exec_route.ms",
            """
            r1 = ssh.connect("10.1.0.20", "guest", "pw")
            opts = {}
            opts["session"] = r1.session
            r2 = ssh.connect("10.1.0.30", "guest", "pw", opts)
            e = ssh.exec(r2.route, "cat /etc/motd")
            print "ok=" + str(e.ok)
            print "code=" + e.code
            print "stdout=" + e.stdout
            d = ssh.disconnect(r2.route)
            """,
            fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/ssh_exec_route.ms");

        Assert.True(result.Ok);
        Assert.Contains(result.Lines, static line => string.Equals(line, "ok=1", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "code=None", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "stdout=motd-c", StringComparison.Ordinal));
        Assert.Empty(hopB.Sessions);
        Assert.Empty(hopC.Sessions);
    }

    /// <summary>Ensures ssh.exec validates sessionOrRoute/cmd/opts arguments and returns InvalidArgs.</summary>
    [Fact]
    public void Execute_Miniscript_SshExec_InvalidArgs_ReturnsInvalidArgs()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        var remote = AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, "pw");

        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/ssh_exec_invalid.ms",
            """
            r = ssh.connect("10.0.1.20", "guest", "pw")
            badType = ssh.exec(1, "pwd")
            badKind = {}
            badKind["kind"] = "oops"
            badKindRes = ssh.exec(badKind, "pwd")
            badRoute = {}
            badRoute["kind"] = "sshRoute"
            badRoute["version"] = 1
            badRoute["sessions"] = []
            badRoute["prefixRoutes"] = []
            badRoute["lastSession"] = null
            badRoute["hopCount"] = 0
            badRouteRes = ssh.exec(badRoute, "pwd")
            badCmd = ssh.exec(r.session, "   ")
            badOpts = {}
            badOpts["port"] = 22
            badOptsRes = ssh.exec(r.session, "pwd", badOpts)
            badMax = {}
            badMax["maxBytes"] = -1
            badMaxRes = ssh.exec(r.session, "pwd", badMax)
            print "badType=" + badType.code
            print "badKind=" + badKindRes.code
            print "badRoute=" + badRouteRes.code
            print "badCmd=" + badCmd.code
            print "badOpts=" + badOptsRes.code
            print "badMax=" + badMaxRes.code
            d = ssh.disconnect(r.session)
            """,
            fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/ssh_exec_invalid.ms");

        Assert.True(result.Ok);
        Assert.Contains(result.Lines, static line => string.Equals(line, "badType=InvalidArgs", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "badKind=InvalidArgs", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "badRoute=InvalidArgs", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "badCmd=InvalidArgs", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "badOpts=InvalidArgs", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "badMax=InvalidArgs", StringComparison.Ordinal));
        Assert.Empty(remote.Sessions);
    }

    /// <summary>Ensures ssh.exec enforces opts.maxBytes on collected stdout output.</summary>
    [Fact]
    public void Execute_Miniscript_SshExec_MaxBytes_Exceed_ReturnsTooLarge()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        var remote = AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, "pw");
        remote.DiskOverlay.AddDirectory("/etc");
        remote.DiskOverlay.WriteFile("/etc/motd", "remote motd", fileKind: VfsFileKind.Text);

        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/ssh_exec_max_bytes.ms",
            """
            r = ssh.connect("10.0.1.20", "guest", "pw")
            opts = {}
            opts["maxBytes"] = 3
            e = ssh.exec(r.session, "cat /etc/motd", opts)
            print "ok=" + str(e.ok)
            print "code=" + e.code
            print "exitCode=" + str(e.exitCode)
            d = ssh.disconnect(r.session)
            """,
            fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/ssh_exec_max_bytes.ms");

        Assert.True(result.Ok);
        Assert.Contains(result.Lines, static line => string.Equals(line, "ok=0", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "code=TooLarge", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "exitCode=1", StringComparison.Ordinal));
        Assert.Empty(remote.Sessions);
    }

    /// <summary>Ensures ssh.exec auto-cleans temporary connect stack and does not persist connection state.</summary>
    [Fact]
    public void Execute_Miniscript_SshExec_ConnectAutoCleanup_DoesNotPersistState()
    {
        var harness = CreateHarness(includeVfsModule: true, includeConnectModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        var hopA = AddRemoteServer(harness, "node-2", "hop-a", "10.0.1.20", AuthMode.Static, "pw");
        var hopB = AddRemoteServer(harness, "node-3", "hop-b", "10.0.1.30", AuthMode.Static, "pw");

        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/ssh_exec_connect_cleanup.ms",
            """
            r = ssh.connect("10.0.1.20", "guest", "pw")
            e1 = ssh.exec(r.session, "connect 10.0.1.30 guest pw")
            e2 = ssh.exec(r.session, "disconnect")
            print "e1ok=" + str(e1.ok)
            print "e1code=" + e1.code
            print "e2ok=" + str(e2.ok)
            print "e2code=" + e2.code
            print "e2stdout=" + e2.stdout
            d = ssh.disconnect(r.session)
            """,
            fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/ssh_exec_connect_cleanup.ms");

        Assert.True(result.Ok);
        Assert.Contains(result.Lines, static line => string.Equals(line, "e1ok=1", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "e1code=None", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "e2ok=0", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "e2code=InvalidArgs", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "e2stdout=error: not connected.", StringComparison.Ordinal));
        Assert.Empty(hopA.Sessions);
        Assert.Empty(hopB.Sessions);
    }

    /// <summary>Ensures ssh.exec can run miniscript command synchronously on remote endpoint.</summary>
    [Fact]
    public void Execute_Miniscript_SshExec_MiniScriptCommand_ReturnsCapturedStdout()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        var remote = AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, "pw");
        remote.DiskOverlay.AddDirectory("/opt");
        remote.DiskOverlay.AddDirectory("/opt/bin");
        remote.DiskOverlay.WriteFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        remote.DiskOverlay.AddDirectory("/scripts");
        remote.DiskOverlay.WriteFile("/scripts/inner.ms", "print \"inner-ok\"", fileKind: VfsFileKind.Text);

        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/ssh_exec_miniscript.ms",
            """
            r = ssh.connect("10.0.1.20", "guest", "pw")
            e = ssh.exec(r.session, "miniscript /scripts/inner.ms")
            print "ok=" + str(e.ok)
            print "code=" + e.code
            print "exitCode=" + str(e.exitCode)
            print "stdout=" + e.stdout
            d = ssh.disconnect(r.session)
            """,
            fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/ssh_exec_miniscript.ms");

        Assert.True(result.Ok);
        Assert.Contains(result.Lines, static line => string.Equals(line, "ok=1", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "code=None", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "exitCode=0", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "stdout=inner-ok", StringComparison.Ordinal));
        Assert.Empty(remote.Sessions);
    }

    /// <summary>Ensures term.exec executes locally and returns stdout/exitCode result map fields.</summary>
    [Fact]
    public void Execute_Miniscript_TermExec_LocalCommand_ReturnsStdoutAndExitCode()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        harness.BaseFileSystem.AddDirectory("/etc");
        harness.BaseFileSystem.AddFile("/etc/motd", "local motd", fileKind: VfsFileKind.Text);
        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/term_exec_ok.ms",
            """
            r = term.exec("cat /etc/motd")
            print "ok=" + str(r.ok)
            print "code=" + r.code
            print "exitCode=" + str(r.exitCode)
            print "stdout=" + r.stdout
            """,
            fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/term_exec_ok.ms");

        Assert.True(result.Ok);
        Assert.Contains(result.Lines, static line => string.Equals(line, "ok=1", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "code=None", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "exitCode=0", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "stdout=local motd", StringComparison.Ordinal));
    }

    /// <summary>Ensures term.exec validates cmd/opts and rejects unsupported opts keys and non-integer maxBytes.</summary>
    [Fact]
    public void Execute_Miniscript_TermExec_InvalidArgs_ReturnsInvalidArgs()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/term_exec_invalid.ms",
            """
            missing = term.exec()
            blank = term.exec("   ")
            badOptsType = term.exec("pwd", 1)
            badOpts = {}
            badOpts["port"] = 22
            badOptsKey = term.exec("pwd", badOpts)
            badMaxNegOpts = {}
            badMaxNegOpts["maxBytes"] = -1
            badMaxNeg = term.exec("pwd", badMaxNegOpts)
            badMaxFloatOpts = {}
            badMaxFloatOpts["maxBytes"] = 1.5
            badMaxFloat = term.exec("pwd", badMaxFloatOpts)
            print "missing=" + missing.code
            print "blank=" + blank.code
            print "optsType=" + badOptsType.code
            print "optsKey=" + badOptsKey.code
            print "maxNeg=" + badMaxNeg.code
            print "maxFloat=" + badMaxFloat.code
            """,
            fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/term_exec_invalid.ms");

        Assert.True(result.Ok);
        Assert.Contains(result.Lines, static line => string.Equals(line, "missing=InvalidArgs", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "blank=InvalidArgs", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "optsType=InvalidArgs", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "optsKey=InvalidArgs", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "maxNeg=InvalidArgs", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "maxFloat=InvalidArgs", StringComparison.Ordinal));
    }

    /// <summary>Ensures term.exec enforces opts.maxBytes against UTF-8 stdout size.</summary>
    [Fact]
    public void Execute_Miniscript_TermExec_MaxBytes_Exceed_ReturnsTooLarge()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        harness.BaseFileSystem.AddDirectory("/etc");
        harness.BaseFileSystem.AddFile("/etc/motd", "local motd", fileKind: VfsFileKind.Text);
        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/term_exec_max_bytes.ms",
            """
            opts = {}
            opts["maxBytes"] = 3
            r = term.exec("cat /etc/motd", opts)
            print "ok=" + str(r.ok)
            print "code=" + r.code
            print "exitCode=" + str(r.exitCode)
            """,
            fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/term_exec_max_bytes.ms");

        Assert.True(result.Ok);
        Assert.Contains(result.Lines, static line => string.Equals(line, "ok=0", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "code=TooLarge", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "exitCode=1", StringComparison.Ordinal));
    }

    /// <summary>Ensures term.exec uses existing command permission checks instead of bypassing terminal privileges.</summary>
    [Fact]
    public void Execute_Miniscript_TermExec_PropagatesPermissionDenied()
    {
        var harness = CreateHarness(
            includeVfsModule: true,
            privilege: new PrivilegeConfig
            {
                Read = true,
                Write = false,
                Execute = true,
            });
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/term_exec_perm.ms",
            """
            r = term.exec("mkdir /loot")
            print "ok=" + str(r.ok)
            print "code=" + r.code
            print "exitCode=" + str(r.exitCode)
            print "stdout=" + r.stdout
            """,
            fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/term_exec_perm.ms");

        Assert.True(result.Ok);
        Assert.Contains(result.Lines, static line => string.Equals(line, "ok=0", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "code=PermissionDenied", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "exitCode=1", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => line.Contains("permission denied", StringComparison.Ordinal));
        Assert.False(harness.Server.DiskOverlay.TryResolveEntry("/loot", out _));
    }

    /// <summary>Ensures term.print/warn/error write expected channels but remain non-fatal script logs.</summary>
    [Fact]
    public void Execute_Miniscript_TermLogIntrinsics_ReturnSuccessAndDoNotFailScript()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/term_logs.ms",
            """
            p = term.print("p-log")
            w = term.warn("w-log")
            e = term.error("e-log")
            print "pOk=" + str(p.ok)
            print "pCode=" + p.code
            print "wOk=" + str(w.ok)
            print "wCode=" + w.code
            print "eOk=" + str(e.ok)
            print "eCode=" + e.code
            print "done"
            """,
            fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/term_logs.ms");

        Assert.True(result.Ok);
        Assert.Contains(result.Lines, static line => string.Equals(line, "p-log", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "warn: w-log", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "error: e-log", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "pOk=1", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "pCode=None", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "wOk=1", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "wCode=None", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "eOk=1", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "eCode=None", StringComparison.Ordinal));
    }

    /// <summary>Ensures term.error-only stderr is non-fatal, while true MiniScript runtime errors still fail execution.</summary>
    [Fact]
    public void Execute_Miniscript_TermError_NonFatalButRuntimeError_RemainsFatal()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/term_error_only.ms",
            """
            r = term.error("x")
            print "ok=" + str(r.ok)
            print "code=" + r.code
            print "done"
            """,
            fileKind: VfsFileKind.Text);
        harness.BaseFileSystem.AddFile(
            "/scripts/term_runtime_error.ms",
            """
            print missingValue
            """,
            fileKind: VfsFileKind.Text);

        var termOnly = Execute(harness, "miniscript /scripts/term_error_only.ms");
        var runtimeError = Execute(harness, "miniscript /scripts/term_runtime_error.ms");

        Assert.True(termOnly.Ok);
        Assert.Contains(termOnly.Lines, static line => string.Equals(line, "error: x", StringComparison.Ordinal));
        Assert.Contains(termOnly.Lines, static line => string.Equals(line, "ok=1", StringComparison.Ordinal));
        Assert.Contains(termOnly.Lines, static line => string.Equals(line, "code=None", StringComparison.Ordinal));
        Assert.Contains(termOnly.Lines, static line => string.Equals(line, "done", StringComparison.Ordinal));

        Assert.False(runtimeError.Ok);
        Assert.Equal(SystemCallErrorCode.InternalError, runtimeError.Code);
        Assert.Contains(runtimeError.Lines, static line => line.Contains("Runtime Error:", StringComparison.Ordinal));
    }

    /// <summary>Ensures async stderr normalization preserves warn/error prefixes emitted by term.warn and term.error.</summary>
    [Fact]
    public void TryStartTerminalProgramExecution_TermWarnError_PreservePrefixesInAsyncOutput()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/term_async_logs.ms",
            """
            term.warn("w-async")
            term.error("e-async")
            print "done"
            """,
            fileKind: VfsFileKind.Text);

        var start = TryStartTerminalProgramExecutionCore(
            harness.World,
            harness.Server.NodeId,
            harness.UserId,
            harness.Cwd,
            "miniscript /scripts/term_async_logs.ms",
            "ts-term-async-logs");
        Assert.True(start.Handled);
        Assert.True(start.Started);

        WaitForTerminalProgramStop(harness.World, "ts-term-async-logs");
        var outputLines = SnapshotTerminalEventLines(harness.World);
        Assert.Contains("warn: w-async", outputLines);
        Assert.Contains("error: e-async", outputLines);
        Assert.Contains("done", outputLines);
        Assert.DoesNotContain("error: warn: w-async", outputLines);
    }

    /// <summary>Ensures sandbox async miniscript can mutate world through ssh.exec command execution.</summary>
    [Fact]
    public void TryStartTerminalProgramExecution_SshExecSandbox_AllowsWorldMutation()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        var remote = AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, "pw");

        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/ssh_exec_async_mutation.ms",
            """
            r = ssh.connect("10.0.1.20", "guest", "pw")
            e = ssh.exec(r.session, "mkdir /loot")
            print "ok=" + str(e.ok)
            print "code=" + e.code
            print "exitCode=" + str(e.exitCode)
            d = ssh.disconnect(r.session)
            """,
            fileKind: VfsFileKind.Text);

        var start = TryStartTerminalProgramExecutionCore(
            harness.World,
            harness.Server.NodeId,
            harness.UserId,
            harness.Cwd,
            "miniscript /scripts/ssh_exec_async_mutation.ms",
            "ts-ssh-exec-async-mutation");
        Assert.True(start.Handled);
        Assert.True(start.Started);

        WaitForTerminalProgramStop(harness.World, "ts-ssh-exec-async-mutation");
        var outputLines = SnapshotTerminalEventLines(harness.World);
        Assert.Contains("ok=1", outputLines);
        Assert.Contains("code=None", outputLines);
        Assert.Contains("exitCode=0", outputLines);
        Assert.True(remote.DiskOverlay.TryResolveEntry("/loot", out var lootEntry));
        Assert.Equal(VfsEntryKind.Dir, lootEntry.EntryKind);
        Assert.Empty(remote.Sessions);
    }

    /// <summary>Ensures async sandbox mode supports route chaining but does not mutate world sessions.</summary>
    [Fact]
    public void TryStartTerminalProgramExecution_SshRouteSandbox_DoesNotMutateWorldSessions()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        var hopB = AddRemoteServer(
            harness,
            "node-2",
            "hop-b",
            "10.1.0.20",
            AuthMode.Static,
            "pw",
            exposure: PortExposure.Public,
            netId: "alpha");
        var hopC = AddRemoteServer(
            harness,
            "node-3",
            "hop-c",
            "10.1.0.30",
            AuthMode.Static,
            "pw",
            exposure: PortExposure.Lan,
            netId: "alpha");
        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/ssh_route_async.ms",
            """
            r1 = ssh.connect("10.1.0.20", "guest", "pw")
            opts = {}
            opts["session"] = r1.session
            r2 = ssh.connect("10.1.0.30", "guest", "pw", opts)
            d = ssh.disconnect(r2.route)
            print "r1ok=" + str(r1.ok)
            print "r2ok=" + str(r2.ok)
            print "closed=" + str(d.summary.closed)
            """,
            fileKind: VfsFileKind.Text);

        var start = TryStartTerminalProgramExecutionCore(
            harness.World,
            harness.Server.NodeId,
            harness.UserId,
            harness.Cwd,
            "miniscript /scripts/ssh_route_async.ms",
            "ts-ssh-route-async");
        Assert.True(start.Handled);
        Assert.True(start.Started);

        WaitForTerminalProgramStop(harness.World, "ts-ssh-route-async");
        var outputLines = SnapshotTerminalEventLines(harness.World);
        Assert.Contains("r1ok=1", outputLines);
        Assert.Contains("r2ok=1", outputLines);
        Assert.Contains("closed=2", outputLines);
        Assert.Empty(hopB.Sessions);
        Assert.Empty(hopC.Sessions);
        var events = DrainQueuedGameEvents(harness.World);
        Assert.NotEmpty(events);
        Assert.All(
            events,
            static gameEvent => Assert.Equal(
                "privilegeAcquire",
                (string)GetPropertyValue(gameEvent, "EventType")));
    }

    /// <summary>Ensures async launcher only handles user-script targets and ignores regular built-in commands.</summary>
    [Fact]
    public void TryStartTerminalProgramExecution_ReturnsHandledFalse_ForNonScriptCommand()
    {
        var harness = CreateHarness(includeVfsModule: true);

        var response = TryStartTerminalProgramExecutionCore(
            harness.World,
            harness.Server.NodeId,
            harness.UserId,
            harness.Cwd,
            "pwd",
            "ts-non-script");

        Assert.False(response.Handled);
        Assert.False(response.Started);
    }

    /// <summary>Ensures async miniscript launch exposes argv/argc with trailing command arguments.</summary>
    [Fact]
    public void TryStartTerminalProgramExecution_MiniScript_PassesArgumentsToScript()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/argv_async.ms",
            """
            print "argc=" + str(argc)
            print "argv0=" + argv[0]
            print "argv1=" + argv[1]
            """,
            fileKind: VfsFileKind.Text);

        var start = TryStartTerminalProgramExecutionCore(
            harness.World,
            harness.Server.NodeId,
            harness.UserId,
            harness.Cwd,
            "miniscript /scripts/argv_async.ms one two",
            "ts-argv-async");
        Assert.True(start.Handled);
        Assert.True(start.Started);

        WaitForTerminalProgramStop(harness.World, "ts-argv-async");
        var outputLines = SnapshotTerminalEventLines(harness.World);
        Assert.Contains("argc=2", outputLines);
        Assert.Contains("argv0=one", outputLines);
        Assert.Contains("argv1=two", outputLines);
    }

    /// <summary>Ensures async executable-script launch exposes argv/argc with trailing command arguments.</summary>
    [Fact]
    public void TryStartTerminalProgramExecution_ExecutableScript_PassesArgumentsToScript()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile(
            "/opt/bin/argv_exec_async.ms",
            """
            print "argc=" + str(argc)
            print "argv0=" + argv[0]
            print "argv1=" + argv[1]
            """,
            fileKind: VfsFileKind.ExecutableScript);

        var start = TryStartTerminalProgramExecutionCore(
            harness.World,
            harness.Server.NodeId,
            harness.UserId,
            harness.Cwd,
            "argv_exec_async.ms red blue",
            "ts-argv-exec-async");
        Assert.True(start.Handled);
        Assert.True(start.Started);

        WaitForTerminalProgramStop(harness.World, "ts-argv-exec-async");
        var outputLines = SnapshotTerminalEventLines(harness.World);
        Assert.Contains("argc=2", outputLines);
        Assert.Contains("argv0=red", outputLines);
        Assert.Contains("argv1=blue", outputLines);
    }

    /// <summary>Ensures async miniscript execution enters running state and can be interrupted with Ctrl+C bridge API.</summary>
    [Fact]
    public void TryStartTerminalProgramExecution_MiniScriptInfiniteLoop_CanBeInterrupted()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/infinite.ms",
            """
            while true
            	x = 1
            end while
            """,
            fileKind: VfsFileKind.Text);

        var start = TryStartTerminalProgramExecutionCore(
            harness.World,
            harness.Server.NodeId,
            harness.UserId,
            harness.Cwd,
            "miniscript /scripts/infinite.ms",
            "ts-async-loop");
        Assert.True(start.Handled);
        Assert.True(start.Started);
        Assert.True(harness.World.IsTerminalProgramRunning("ts-async-loop"));

        var interrupt = InterruptTerminalProgramExecutionCore(harness.World, "ts-async-loop");
        Assert.Contains(interrupt.Lines, static line => string.Equals(line, "program killed by Ctrl+C", StringComparison.Ordinal));

        WaitForTerminalProgramStop(harness.World, "ts-async-loop");
        Assert.False(harness.World.IsTerminalProgramRunning("ts-async-loop"));
    }

    /// <summary>Ensures one terminal session cannot start a second async script while one is still running.</summary>
    [Fact]
    public void TryStartTerminalProgramExecution_BlocksDuplicateStartPerSession()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/infinite.ms",
            """
            while true
            	x = 1
            end while
            """,
            fileKind: VfsFileKind.Text);

        var first = TryStartTerminalProgramExecutionCore(
            harness.World,
            harness.Server.NodeId,
            harness.UserId,
            harness.Cwd,
            "miniscript /scripts/infinite.ms",
            "ts-dup");
        Assert.True(first.Started);

        var second = TryStartTerminalProgramExecutionCore(
            harness.World,
            harness.Server.NodeId,
            harness.UserId,
            harness.Cwd,
            "miniscript /scripts/infinite.ms",
            "ts-dup");
        Assert.True(second.Handled);
        Assert.False(second.Started);
        Assert.Contains(second.Response.Lines, static line => line.Contains("program already running", StringComparison.Ordinal));

        _ = InterruptTerminalProgramExecutionCore(harness.World, "ts-dup");
        WaitForTerminalProgramStop(harness.World, "ts-dup");
    }

    /// <summary>Ensures async miniscript ssh.connect/disconnect runs in sandbox mode (validated only, no world session mutation).</summary>
    [Fact]
    public void TryStartTerminalProgramExecution_SshSandbox_DoesNotMutateWorldSessions()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        var remote = AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, "pw");
        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/ssh_async.ms",
            """
            result = ssh.connect("10.0.1.20", "guest", "pw")
            print "ok=" + str(result.ok)
            print "code=" + result.code
            d1 = ssh.disconnect(result.session)
            d2 = ssh.disconnect(result.session)
            print "d1=" + str(d1.disconnected)
            print "d2=" + str(d2.disconnected)
            """,
            fileKind: VfsFileKind.Text);

        var start = TryStartTerminalProgramExecutionCore(
            harness.World,
            harness.Server.NodeId,
            harness.UserId,
            harness.Cwd,
            "miniscript /scripts/ssh_async.ms",
            "ts-ssh-async");
        Assert.True(start.Handled);
        Assert.True(start.Started);

        WaitForTerminalProgramStop(harness.World, "ts-ssh-async");
        var outputLines = SnapshotTerminalEventLines(harness.World);
        Assert.Contains("ok=1", outputLines);
        Assert.Contains("code=None", outputLines);
        Assert.Contains("d1=1", outputLines);
        Assert.Contains("d2=0", outputLines);
        Assert.Empty(remote.Sessions);
        var events = DrainQueuedGameEvents(harness.World);
        Assert.NotEmpty(events);
        Assert.All(
            events,
            static gameEvent => Assert.Equal(
                "privilegeAcquire",
                (string)GetPropertyValue(gameEvent, "EventType")));
    }

    /// <summary>Ensures async sandbox ssh.connect applies connectionRateLimiter threshold blocking in validation mode.</summary>
    [Fact]
    public void TryStartTerminalProgramExecution_SshSandbox_ConnectionRateLimiter_BlocksAfterThreshold()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        var remote = AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, "pw");
        AddConnectionRateLimiterDaemon(
            remote,
            monitorMs: 60000,
            threshold: 1,
            blockMs: 60000,
            rateLimit: 100,
            recoveryMs: 60000);
        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/ssh_async_rate_limiter.ms",
            """
            r1 = ssh.connect("10.0.1.20", "guest", "wrong")
            print "r1ok=" + str(r1.ok)
            print "r1code=" + r1.code
            r2 = ssh.connect("10.0.1.20", "guest", "pw")
            print "r2ok=" + str(r2.ok)
            print "r2code=" + r2.code
            print "r2err=" + r2.err
            """,
            fileKind: VfsFileKind.Text);

        var start = TryStartTerminalProgramExecutionCore(
            harness.World,
            harness.Server.NodeId,
            harness.UserId,
            harness.Cwd,
            "miniscript /scripts/ssh_async_rate_limiter.ms",
            "ts-ssh-async-rate");
        Assert.True(start.Handled);
        Assert.True(start.Started);

        WaitForTerminalProgramStop(harness.World, "ts-ssh-async-rate");
        var outputLines = SnapshotTerminalEventLines(harness.World);
        Assert.Contains("r1ok=0", outputLines);
        Assert.Contains("r1code=PermissionDenied", outputLines);
        Assert.Contains("r2ok=0", outputLines);
        Assert.Contains("r2code=PermissionDenied", outputLines);
        Assert.Contains(
            "r2err=connectionRateLimiter daemon blocked this connection attempt.",
            outputLines);
        Assert.Empty(remote.Sessions);
        var events = DrainQueuedGameEvents(harness.World);
        Assert.Empty(events);
    }

    /// <summary>Ensures miniscript ftp.get with session input succeeds and emits fileAcquire on local save.</summary>
    [Fact]
    public void Execute_Miniscript_FtpGet_WithSession_Succeeds_AndEmitsFileAcquire()
    {
        var harness = CreateHarness(includeVfsModule: true, cwd: "/work");
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        harness.BaseFileSystem.AddDirectory("/work");
        harness.BaseFileSystem.AddDirectory("/work/downloads");

        var remote = AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, "pw");
        AddFtpPort(remote);
        remote.DiskOverlay.AddDirectory("/drop");
        remote.DiskOverlay.WriteFile(
            "/drop/tool.bin",
            "exec:miniscript",
            fileKind: VfsFileKind.ExecutableHardcode,
            size: 4096);

        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/ftp_get_session.ms",
            """
            r = ssh.connect("10.0.1.20", "guest", "pw")
            g = ftp.get(r.session, "/drop/tool.bin", "downloads")
            print "ok=" + str(g.ok)
            print "code=" + g.code
            print "savedTo=" + g.savedTo
            print "bytes=" + str(g.bytes)
            d = ssh.disconnect(r.session)
            """,
            fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/ftp_get_session.ms", terminalSessionId: "ts-ms-ftp-get");

        Assert.True(result.Ok);
        Assert.Contains(result.Lines, static line => string.Equals(line, "ok=1", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "code=None", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "savedTo=/work/downloads/tool.bin", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "bytes=4096", StringComparison.Ordinal));

        Assert.True(harness.Server.DiskOverlay.TryResolveEntry("/work/downloads/tool.bin", out var downloaded));
        Assert.Equal(VfsEntryKind.File, downloaded.EntryKind);
        Assert.Equal(VfsFileKind.ExecutableHardcode, downloaded.FileKind);
        Assert.Equal(4096, downloaded.Size);

        var events = DrainQueuedGameEvents(harness.World);
        var fileAcquireEvents = events
            .Where(static gameEvent => string.Equals((string)GetPropertyValue(gameEvent, "EventType"), "fileAcquire", StringComparison.Ordinal))
            .ToList();
        var fileAcquire = Assert.Single(fileAcquireEvents);
        var payload = GetPropertyValue(fileAcquire, "Payload");
        Assert.Equal("node-2", (string?)GetPropertyValue(payload!, "FromNodeId"));
        Assert.Equal("guest", (string?)GetPropertyValue(payload!, "UserKey"));
        Assert.Equal("/drop/tool.bin", (string?)GetPropertyValue(payload!, "RemotePath"));
        Assert.Equal("/work/downloads/tool.bin", (string?)GetPropertyValue(payload!, "LocalPath"));
        Assert.Equal("ftp", (string?)GetPropertyValue(payload!, "TransferMethod"));
    }

    /// <summary>Ensures miniscript ftp.put with session input succeeds and does not emit fileAcquire.</summary>
    [Fact]
    public void Execute_Miniscript_FtpPut_WithSession_Succeeds_AndDoesNotEmitFileAcquire()
    {
        var harness = CreateHarness(includeVfsModule: true, cwd: "/work");
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        harness.BaseFileSystem.AddDirectory("/work");
        harness.BaseFileSystem.AddDirectory("/work/local");
        harness.Server.DiskOverlay.WriteFile(
            "/work/local/script.bin",
            "exec:noop",
            fileKind: VfsFileKind.ExecutableHardcode,
            size: 2048);

        var remote = AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, "pw");
        AddFtpPort(remote);
        remote.DiskOverlay.AddDirectory("/incoming");

        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/ftp_put_session.ms",
            """
            r = ssh.connect("10.0.1.20", "guest", "pw")
            p = ftp.put(r.session, "local/script.bin", "incoming")
            print "ok=" + str(p.ok)
            print "code=" + p.code
            print "savedTo=" + p.savedTo
            print "bytes=" + str(p.bytes)
            d = ssh.disconnect(r.session)
            """,
            fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/ftp_put_session.ms", terminalSessionId: "ts-ms-ftp-put");

        Assert.True(result.Ok);
        Assert.Contains(result.Lines, static line => string.Equals(line, "ok=1", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "code=None", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "savedTo=/incoming/script.bin", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "bytes=2048", StringComparison.Ordinal));

        Assert.True(remote.DiskOverlay.TryResolveEntry("/incoming/script.bin", out var uploaded));
        Assert.Equal(VfsEntryKind.File, uploaded.EntryKind);
        Assert.Equal(VfsFileKind.ExecutableHardcode, uploaded.FileKind);
        Assert.Equal(2048, uploaded.Size);

        var events = DrainQueuedGameEvents(harness.World);
        var fileAcquireEvents = events
            .Where(static gameEvent => string.Equals((string)GetPropertyValue(gameEvent, "EventType"), "fileAcquire", StringComparison.Ordinal))
            .ToList();
        Assert.Empty(fileAcquireEvents);
    }

    /// <summary>Ensures ftp.get(route) enforces last.read and first.write privilege checks.</summary>
    [Fact]
    public void Execute_Miniscript_FtpGet_WithRoute_EnforcesLastReadAndFirstWrite()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        harness.Server.SetInterfaces(new[]
        {
            new InterfaceRuntime
            {
                NetId = "internet",
                Ip = "10.0.0.10",
            },
            new InterfaceRuntime
            {
                NetId = "alpha",
                Ip = "10.1.0.10",
            },
        });
        AddRemoteServer(
            harness,
            "node-2",
            "hop-b",
            "10.1.0.20",
            AuthMode.Static,
            "pw",
            exposure: PortExposure.Public,
            netId: "alpha");
        var last = AddRemoteServer(
            harness,
            "node-3",
            "hop-c",
            "10.1.0.30",
            AuthMode.Static,
            "pw",
            exposure: PortExposure.Lan,
            netId: "alpha");
        AddFtpPort(last, exposure: PortExposure.Lan);
        last.DiskOverlay.AddDirectory("/drop");
        last.DiskOverlay.WriteFile("/drop/r.txt", "R", fileKind: VfsFileKind.Text);

        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/ftp_get_route_perm.ms",
            """
            r1 = ssh.connect("10.1.0.20", "guest", "pw")
            opts = {}
            opts["session"] = r1.session
            r2 = ssh.connect("10.1.0.30", "guest", "pw", opts)
            g = ftp.get(r2.route, "/drop/r.txt")
            print "ok=" + str(g.ok)
            print "code=" + g.code
            d = ssh.disconnect(r2.route)
            """,
            fileKind: VfsFileKind.Text);

        harness.Server.Users["guest"].Privilege.Write = false;
        last.Users["guest"].Privilege.Read = true;
        var firstWriteDenied = Execute(harness, "miniscript /scripts/ftp_get_route_perm.ms", terminalSessionId: "ts-ms-ftp-get-route");
        Assert.True(firstWriteDenied.Ok);
        Assert.Contains(firstWriteDenied.Lines, static line => string.Equals(line, "ok=0", StringComparison.Ordinal));
        Assert.Contains(firstWriteDenied.Lines, static line => string.Equals(line, "code=PermissionDenied", StringComparison.Ordinal));
        Assert.False(harness.Server.DiskOverlay.TryResolveEntry("/r.txt", out _));

        harness.Server.Users["guest"].Privilege.Write = true;
        last.Users["guest"].Privilege.Read = false;
        var lastReadDenied = Execute(harness, "miniscript /scripts/ftp_get_route_perm.ms", terminalSessionId: "ts-ms-ftp-get-route");
        Assert.True(lastReadDenied.Ok);
        Assert.Contains(lastReadDenied.Lines, static line => string.Equals(line, "ok=0", StringComparison.Ordinal));
        Assert.Contains(lastReadDenied.Lines, static line => string.Equals(line, "code=PermissionDenied", StringComparison.Ordinal));
        Assert.False(harness.Server.DiskOverlay.TryResolveEntry("/r.txt", out _));
    }

    /// <summary>Ensures ftp.put(route) enforces first.read and last.write privilege checks.</summary>
    [Fact]
    public void Execute_Miniscript_FtpPut_WithRoute_EnforcesFirstReadAndLastWrite()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        harness.Server.SetInterfaces(new[]
        {
            new InterfaceRuntime
            {
                NetId = "internet",
                Ip = "10.0.0.10",
            },
            new InterfaceRuntime
            {
                NetId = "alpha",
                Ip = "10.1.0.10",
            },
        });
        AddRemoteServer(
            harness,
            "node-2",
            "hop-b",
            "10.1.0.20",
            AuthMode.Static,
            "pw",
            exposure: PortExposure.Public,
            netId: "alpha");
        var last = AddRemoteServer(
            harness,
            "node-3",
            "hop-c",
            "10.1.0.30",
            AuthMode.Static,
            "pw",
            exposure: PortExposure.Lan,
            netId: "alpha");
        AddFtpPort(last, exposure: PortExposure.Lan);
        harness.Server.DiskOverlay.AddDirectory("/seed");
        harness.Server.DiskOverlay.WriteFile("/seed/a.txt", "A", fileKind: VfsFileKind.Text);
        harness.Server.Users["limited"] = new UserConfig
        {
            UserId = "limited",
            AuthMode = AuthMode.None,
            Privilege = new PrivilegeConfig
            {
                Read = false,
                Write = true,
                Execute = true,
            },
        };
        last.DiskOverlay.AddDirectory("/incoming");

        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/ftp_put_route_perm_first.ms",
            """
            r1 = ssh.connect("10.1.0.20", "guest", "pw")
            opts = {}
            opts["session"] = r1.session
            r2 = ssh.connect("10.1.0.30", "guest", "pw", opts)
            r2.route.sessions[0]["sourceUserId"] = "limited"
            p = ftp.put(r2.route, "/seed/a.txt", "/incoming/a.txt")
            print "ok=" + str(p.ok)
            print "code=" + p.code
            d = ssh.disconnect(r2.route)
            """,
            fileKind: VfsFileKind.Text);
        harness.BaseFileSystem.AddFile(
            "/scripts/ftp_put_route_perm_last.ms",
            """
            r1 = ssh.connect("10.1.0.20", "guest", "pw")
            opts = {}
            opts["session"] = r1.session
            r2 = ssh.connect("10.1.0.30", "guest", "pw", opts)
            p = ftp.put(r2.route, "/seed/a.txt", "/incoming/a.txt")
            print "ok=" + str(p.ok)
            print "code=" + p.code
            d = ssh.disconnect(r2.route)
            """,
            fileKind: VfsFileKind.Text);

        var firstReadDenied = Execute(harness, "miniscript /scripts/ftp_put_route_perm_first.ms", terminalSessionId: "ts-ms-ftp-put-route");
        Assert.True(firstReadDenied.Ok);
        Assert.Contains(firstReadDenied.Lines, static line => string.Equals(line, "ok=0", StringComparison.Ordinal));
        Assert.Contains(firstReadDenied.Lines, static line => string.Equals(line, "code=PermissionDenied", StringComparison.Ordinal));
        Assert.False(last.DiskOverlay.TryResolveEntry("/incoming/a.txt", out _));

        last.Users["guest"].Privilege.Write = false;
        var lastWriteDenied = Execute(harness, "miniscript /scripts/ftp_put_route_perm_last.ms", terminalSessionId: "ts-ms-ftp-put-route");
        Assert.True(lastWriteDenied.Ok);
        Assert.Contains(lastWriteDenied.Lines, static line => string.Equals(line, "ok=0", StringComparison.Ordinal));
        Assert.Contains(lastWriteDenied.Lines, static line => string.Equals(line, "code=PermissionDenied", StringComparison.Ordinal));
        Assert.False(last.DiskOverlay.TryResolveEntry("/incoming/a.txt", out _));
    }

    /// <summary>Ensures route FTP port gating checks target(last) endpoint only.</summary>
    [Fact]
    public void Execute_Miniscript_Ftp_WithRoute_PortCheckTargetsLastOnly()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        harness.Server.SetInterfaces(new[]
        {
            new InterfaceRuntime
            {
                NetId = "internet",
                Ip = "10.0.0.10",
            },
            new InterfaceRuntime
            {
                NetId = "alpha",
                Ip = "10.1.0.10",
            },
        });
        var first = AddRemoteServer(
            harness,
            "node-2",
            "hop-b",
            "10.1.0.20",
            AuthMode.Static,
            "pw",
            exposure: PortExposure.Public,
            netId: "alpha");
        var last = AddRemoteServer(
            harness,
            "node-3",
            "hop-c",
            "10.1.0.30",
            AuthMode.Static,
            "pw",
            exposure: PortExposure.Lan,
            netId: "alpha");
        AddFtpPort(last, exposure: PortExposure.Lan);
        harness.Server.DiskOverlay.AddDirectory("/recv");
        last.DiskOverlay.AddDirectory("/drop");
        last.DiskOverlay.WriteFile("/drop/port.txt", "P", fileKind: VfsFileKind.Text);

        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/ftp_route_port.ms",
            """
            r1 = ssh.connect("10.1.0.20", "guest", "pw")
            opts = {}
            opts["session"] = r1.session
            r2 = ssh.connect("10.1.0.30", "guest", "pw", opts)
            g = ftp.get(r2.route, "/drop/port.txt", "/recv")
            print "ok=" + str(g.ok)
            print "code=" + g.code
            d = ssh.disconnect(r2.route)
            """,
            fileKind: VfsFileKind.Text);

        var success = Execute(harness, "miniscript /scripts/ftp_route_port.ms", terminalSessionId: "ts-ms-ftp-route-port");
        Assert.True(success.Ok);
        Assert.Contains(success.Lines, static line => string.Equals(line, "ok=1", StringComparison.Ordinal));
        Assert.Contains(success.Lines, static line => string.Equals(line, "code=None", StringComparison.Ordinal));
        Assert.True(harness.Server.DiskOverlay.TryResolveEntry("/recv/port.txt", out _));

        last.Ports.Remove(21);
        AddFtpPort(first, exposure: PortExposure.Lan);
        var failOnLast = Execute(harness, "miniscript /scripts/ftp_route_port.ms", terminalSessionId: "ts-ms-ftp-route-port");
        Assert.True(failOnLast.Ok);
        Assert.Contains(failOnLast.Lines, static line => string.Equals(line, "ok=0", StringComparison.Ordinal));
        Assert.Contains(failOnLast.Lines, static line => string.Equals(line, "code=NotFound", StringComparison.Ordinal));
    }

    /// <summary>Ensures ftp.put(route) resolves local source from route.sessions[0].source endpoint(A), not first target(B).</summary>
    [Fact]
    public void Execute_Miniscript_FtpPut_WithRoute_UsesRouteSourceAsLocalEndpoint()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        harness.Server.SetInterfaces(new[]
        {
            new InterfaceRuntime
            {
                NetId = "internet",
                Ip = "10.0.0.10",
            },
            new InterfaceRuntime
            {
                NetId = "alpha",
                Ip = "10.1.0.10",
            },
        });
        harness.Server.DiskOverlay.AddDirectory("/seed");
        harness.Server.DiskOverlay.WriteFile("/seed/a.txt", "A", fileKind: VfsFileKind.Text, size: 1);

        var first = AddRemoteServer(
            harness,
            "node-2",
            "hop-b",
            "10.1.0.20",
            AuthMode.Static,
            "pw",
            exposure: PortExposure.Public,
            netId: "alpha");
        var last = AddRemoteServer(
            harness,
            "node-3",
            "hop-c",
            "10.1.0.30",
            AuthMode.Static,
            "pw",
            exposure: PortExposure.Lan,
            netId: "alpha");
        AddFtpPort(last, exposure: PortExposure.Lan);
        first.DiskOverlay.AddDirectory("/seed");
        first.DiskOverlay.WriteFile("/seed/a.txt", "B", fileKind: VfsFileKind.Text, size: 1);
        last.DiskOverlay.AddDirectory("/incoming");

        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/ftp_put_route_source.ms",
            """
            r1 = ssh.connect("10.1.0.20", "guest", "pw")
            opts = {}
            opts["session"] = r1.session
            r2 = ssh.connect("10.1.0.30", "guest", "pw", opts)
            p = ftp.put(r2.route, "/seed/a.txt", "/incoming/a.txt")
            print "ok=" + str(p.ok)
            print "code=" + p.code
            d = ssh.disconnect(r2.route)
            """,
            fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/ftp_put_route_source.ms", terminalSessionId: "ts-ms-ftp-put-route-source");
        Assert.True(result.Ok);
        Assert.Contains(result.Lines, static line => string.Equals(line, "ok=1", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "code=None", StringComparison.Ordinal));
        Assert.True(last.DiskOverlay.TryReadFileText("/incoming/a.txt", out var uploadedText));
        Assert.Equal("A", uploadedText);
    }

    /// <summary>Ensures ftp.get(route) writes local file to route.sessions[0].source endpoint(A), not first target(B).</summary>
    [Fact]
    public void Execute_Miniscript_FtpGet_WithRoute_SavesToRouteSourceEndpoint()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        harness.Server.SetInterfaces(new[]
        {
            new InterfaceRuntime
            {
                NetId = "internet",
                Ip = "10.0.0.10",
            },
            new InterfaceRuntime
            {
                NetId = "alpha",
                Ip = "10.1.0.10",
            },
        });
        harness.Server.DiskOverlay.AddDirectory("/recv");

        var first = AddRemoteServer(
            harness,
            "node-2",
            "hop-b",
            "10.1.0.20",
            AuthMode.Static,
            "pw",
            exposure: PortExposure.Public,
            netId: "alpha");
        var last = AddRemoteServer(
            harness,
            "node-3",
            "hop-c",
            "10.1.0.30",
            AuthMode.Static,
            "pw",
            exposure: PortExposure.Lan,
            netId: "alpha");
        AddFtpPort(last, exposure: PortExposure.Lan);
        first.DiskOverlay.AddDirectory("/recv");
        last.DiskOverlay.AddDirectory("/drop");
        last.DiskOverlay.WriteFile("/drop/r.txt", "R", fileKind: VfsFileKind.Text, size: 1);

        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/ftp_get_route_source.ms",
            """
            r1 = ssh.connect("10.1.0.20", "guest", "pw")
            opts = {}
            opts["session"] = r1.session
            r2 = ssh.connect("10.1.0.30", "guest", "pw", opts)
            g = ftp.get(r2.route, "/drop/r.txt", "/recv")
            print "ok=" + str(g.ok)
            print "code=" + g.code
            d = ssh.disconnect(r2.route)
            """,
            fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/ftp_get_route_source.ms", terminalSessionId: "ts-ms-ftp-get-route-source");
        Assert.True(result.Ok);
        Assert.Contains(result.Lines, static line => string.Equals(line, "ok=1", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "code=None", StringComparison.Ordinal));
        Assert.True(harness.Server.DiskOverlay.TryReadFileText("/recv/r.txt", out var downloadedText));
        Assert.Equal("R", downloadedText);
        Assert.False(first.DiskOverlay.TryResolveEntry("/recv/r.txt", out _));
    }

    /// <summary>Ensures async miniscript FTP performs real transfers and emits fileAcquire for ftp.get.</summary>
    [Fact]
    public void TryStartTerminalProgramExecution_FtpSandbox_PerformsTransfers_AndEmitsFileAcquireForGet()
    {
        var harness = CreateHarness(includeVfsModule: true, cwd: "/work");
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        harness.BaseFileSystem.AddDirectory("/work");
        harness.BaseFileSystem.AddDirectory("/work/downloads");
        harness.BaseFileSystem.AddDirectory("/work/uploads");
        harness.Server.DiskOverlay.WriteFile("/work/uploads/u.txt", "UPLOAD", fileKind: VfsFileKind.Text, size: 6);

        var remote = AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, "pw");
        AddFtpPort(remote);
        remote.DiskOverlay.AddDirectory("/drop");
        remote.DiskOverlay.WriteFile("/drop/a.txt", "A", fileKind: VfsFileKind.Text, size: 1);
        remote.DiskOverlay.AddDirectory("/incoming");

        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/ftp_async.ms",
            """
            r = ssh.connect("10.0.1.20", "guest", "pw")
            g = ftp.get(r.session, "/drop/a.txt", "downloads")
            p = ftp.put(r.session, "uploads/u.txt", "incoming/u.txt")
            print "gok=" + str(g.ok)
            print "gcode=" + g.code
            print "pok=" + str(p.ok)
            print "pcode=" + p.code
            d = ssh.disconnect(r.session)
            """,
            fileKind: VfsFileKind.Text);

        var start = TryStartTerminalProgramExecutionCore(
            harness.World,
            harness.Server.NodeId,
            harness.UserId,
            harness.Cwd,
            "miniscript /scripts/ftp_async.ms",
            "ts-ms-ftp-async");
        Assert.True(start.Handled);
        Assert.True(start.Started);

        WaitForTerminalProgramStop(harness.World, "ts-ms-ftp-async");
        var outputLines = SnapshotTerminalEventLines(harness.World);
        Assert.Contains("gok=1", outputLines);
        Assert.Contains("gcode=None", outputLines);
        Assert.Contains("pok=1", outputLines);
        Assert.Contains("pcode=None", outputLines);

        Assert.True(harness.Server.DiskOverlay.TryResolveEntry("/work/downloads/a.txt", out var downloaded));
        Assert.Equal(VfsEntryKind.File, downloaded.EntryKind);
        Assert.Equal(VfsFileKind.Text, downloaded.FileKind);
        Assert.Equal(1, downloaded.Size);
        Assert.True(harness.Server.DiskOverlay.TryReadFileText("/work/downloads/a.txt", out var downloadedContent));
        Assert.Equal("A", downloadedContent);

        Assert.True(remote.DiskOverlay.TryResolveEntry("/incoming/u.txt", out var uploaded));
        Assert.Equal(VfsEntryKind.File, uploaded.EntryKind);
        Assert.Equal(VfsFileKind.Text, uploaded.FileKind);
        Assert.Equal(6, uploaded.Size);
        Assert.True(remote.DiskOverlay.TryReadFileText("/incoming/u.txt", out var uploadedContent));
        Assert.Equal("UPLOAD", uploadedContent);

        Assert.Empty(remote.Sessions);
        var events = DrainQueuedGameEvents(harness.World);
        Assert.NotEmpty(events);
        var fileAcquireEvents = events
            .Where(static gameEvent => string.Equals((string)GetPropertyValue(gameEvent, "EventType"), "fileAcquire", StringComparison.Ordinal))
            .ToList();
        var fileAcquire = Assert.Single(fileAcquireEvents);
        var payload = GetPropertyValue(fileAcquire, "Payload");
        Assert.Equal("node-2", (string?)GetPropertyValue(payload!, "FromNodeId"));
        Assert.Equal("guest", (string?)GetPropertyValue(payload!, "UserKey"));
        Assert.Equal("/drop/a.txt", (string?)GetPropertyValue(payload!, "RemotePath"));
        Assert.Equal("/work/downloads/a.txt", (string?)GetPropertyValue(payload!, "LocalPath"));
        Assert.Equal("ftp", (string?)GetPropertyValue(payload!, "TransferMethod"));
    }

    /// <summary>Ensures miniscript FTP rejects unsupported opts keys and returns InvalidArgs.</summary>
    [Fact]
    public void Execute_Miniscript_Ftp_RejectsUnsupportedOptsKeys()
    {
        var harness = CreateHarness(includeVfsModule: true, cwd: "/work");
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        harness.BaseFileSystem.AddDirectory("/work");
        harness.BaseFileSystem.AddDirectory("/work/uploads");
        harness.Server.DiskOverlay.WriteFile("/work/uploads/u.txt", "U", fileKind: VfsFileKind.Text, size: 1);

        var remote = AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, "pw");
        AddFtpPort(remote);
        remote.DiskOverlay.AddDirectory("/drop");
        remote.DiskOverlay.WriteFile("/drop/a.txt", "A", fileKind: VfsFileKind.Text, size: 1);
        remote.DiskOverlay.AddDirectory("/incoming");

        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/ftp_bad_opts.ms",
            """
            r = ssh.connect("10.0.1.20", "guest", "pw")
            badGet = {}
            badGet["overwrite"] = 1
            g = ftp.get(r.session, "/drop/a.txt", badGet)
            print "gok=" + str(g.ok)
            print "gcode=" + g.code
            badPut = {}
            badPut["maxBytes"] = 10
            p = ftp.put(r.session, "uploads/u.txt", "incoming/u.txt", badPut)
            print "pok=" + str(p.ok)
            print "pcode=" + p.code
            d = ssh.disconnect(r.session)
            """,
            fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/ftp_bad_opts.ms", terminalSessionId: "ts-ms-ftp-bad-opts");

        Assert.True(result.Ok);
        Assert.Contains(result.Lines, static line => string.Equals(line, "gok=0", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "gcode=InvalidArgs", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "pok=0", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "pcode=InvalidArgs", StringComparison.Ordinal));
        Assert.False(harness.Server.DiskOverlay.TryResolveEntry("/work/a.txt", out _));
        Assert.False(remote.DiskOverlay.TryResolveEntry("/incoming/u.txt", out _));
    }

    /// <summary>Ensures source metadata is required for session inputs and route semantics are applied per API contract.</summary>
    [Fact]
    public void Execute_Miniscript_SessionOrRoute_WithoutSourceMetadata_ReturnsInvalidArgs()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        var first = AddRemoteServer(
            harness,
            "node-2",
            "hop-b",
            "10.1.0.20",
            AuthMode.Static,
            "pw",
            exposure: PortExposure.Public,
            netId: "alpha");
        var last = AddRemoteServer(
            harness,
            "node-3",
            "hop-c",
            "10.1.0.30",
            AuthMode.Static,
            "pw",
            exposure: PortExposure.Lan,
            netId: "alpha");
        AddFtpPort(last, exposure: PortExposure.Lan);
        last.DiskOverlay.AddDirectory("/drop");
        last.DiskOverlay.WriteFile("/drop/a.txt", "A", fileKind: VfsFileKind.Text, size: 1);
        first.DiskOverlay.AddDirectory("/drop");
        first.DiskOverlay.WriteFile("/drop/a.txt", "B", fileKind: VfsFileKind.Text, size: 1);

        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/session_route_source_required.ms",
            """
            r1 = ssh.connect("10.1.0.20", "guest", "pw")
            opts = {}
            opts["session"] = r1.session
            r2 = ssh.connect("10.1.0.30", "guest", "pw", opts)

            bad = {}
            bad["kind"] = "sshSession"
            bad["sessionNodeId"] = r1.session.sessionNodeId
            bad["sessionId"] = r1.session.sessionId
            bad["userId"] = r1.session.userId
            bad["hostOrIp"] = r1.session.hostOrIp
            bad["remoteIp"] = r1.session.remoteIp

            e = ssh.exec(bad, "pwd")
            f = fs.read(bad, "/drop/a.txt")
            n = net.interfaces(bad)
            g = ftp.get(bad, "/drop/a.txt")
            badOpts = {}
            badOpts["session"] = bad
            c = ssh.connect("10.1.0.30", "guest", "pw", badOpts)

            badRoute = r2.route
            badRoute.sessions[0]["sourceNodeId"] = null
            er = ssh.exec(badRoute, "pwd")
            fr = fs.read(badRoute, "/drop/a.txt")
            nr = net.interfaces(badRoute)
            gr = ftp.get(badRoute, "/drop/a.txt")

            print "e=" + e.code
            print "f=" + f.code
            print "n=" + n.code
            print "g=" + g.code
            print "c=" + c.code
            print "er=" + er.code
            print "fr=" + fr.code
            print "nr=" + nr.code
            print "gr=" + gr.code

            d2 = ssh.disconnect(r2.session)
            d1 = ssh.disconnect(r1.session)
            """,
            fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/session_route_source_required.ms", terminalSessionId: "ts-ms-source-required");

        Assert.True(result.Ok);
        Assert.Contains(result.Lines, static line => string.Equals(line, "e=InvalidArgs", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "f=InvalidArgs", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "n=InvalidArgs", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "g=InvalidArgs", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "c=InvalidArgs", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "er=InvalidArgs", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "fr=None", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "nr=None", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "gr=InvalidArgs", StringComparison.Ordinal));
        Assert.Empty(first.Sessions);
        Assert.Empty(last.Sessions);
    }

    /// <summary>Ensures miniscript fs.list/read/stat works in local execution context.</summary>
    [Fact]
    public void Execute_Miniscript_Fs_Local_ListReadStat_Succeeds()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        harness.BaseFileSystem.AddDirectory("/docs");
        harness.BaseFileSystem.AddFile("/docs/a.txt", "HELLO", fileKind: VfsFileKind.Text, size: 5);

        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/fs_local_ok.ms",
            """
            l = fs.list("/docs")
            print "lok=" + str(l.ok)
            print "lcode=" + l.code
            print "lcount=" + str(len(l.entries))
            r = fs.read("/docs/a.txt")
            print "rok=" + str(r.ok)
            print "rtext=" + r.text
            s = fs.stat("/docs/a.txt")
            print "sok=" + str(s.ok)
            print "skind=" + s.entryKind
            print "sfileKind=" + s.fileKind
            print "ssize=" + str(s.size)
            print "hasPerms=" + str(hasIndex(s, "perms"))
            """,
            fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/fs_local_ok.ms", terminalSessionId: "ts-ms-fs-local-ok");

        Assert.True(result.Ok);
        Assert.Contains(result.Lines, static line => string.Equals(line, "lok=1", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "lcode=None", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "lcount=1", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "rok=1", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "rtext=HELLO", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "sok=1", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "skind=File", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "sfileKind=Text", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "ssize=5", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "hasPerms=0", StringComparison.Ordinal));
    }

    /// <summary>Ensures miniscript fs.read rejects non-text files with NotTextFile.</summary>
    [Fact]
    public void Execute_Miniscript_Fs_Read_ReturnsNotTextFile_ForBinary()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        harness.BaseFileSystem.AddDirectory("/bin");
        harness.BaseFileSystem.AddFile("/bin/blob.bin", "0101", fileKind: VfsFileKind.Binary, size: 4);

        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/fs_read_not_text.ms",
            """
            r = fs.read("/bin/blob.bin")
            print "ok=" + str(r.ok)
            print "code=" + r.code
            """,
            fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/fs_read_not_text.ms", terminalSessionId: "ts-ms-fs-read-not-text");

        Assert.True(result.Ok);
        Assert.Contains(result.Lines, static line => string.Equals(line, "ok=0", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "code=NotTextFile", StringComparison.Ordinal));
    }

    /// <summary>Ensures miniscript fs.read respects maxBytes option and returns TooLarge.</summary>
    [Fact]
    public void Execute_Miniscript_Fs_Read_RespectsMaxBytes()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        harness.BaseFileSystem.AddDirectory("/docs");
        harness.BaseFileSystem.AddFile("/docs/big.txt", "ABCDE", fileKind: VfsFileKind.Text, size: 5);

        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/fs_read_max_bytes.ms",
            """
            opts = {}
            opts["maxBytes"] = 3
            r = fs.read("/docs/big.txt", opts)
            print "ok=" + str(r.ok)
            print "code=" + r.code
            """,
            fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/fs_read_max_bytes.ms", terminalSessionId: "ts-ms-fs-read-max");

        Assert.True(result.Ok);
        Assert.Contains(result.Lines, static line => string.Equals(line, "ok=0", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "code=TooLarge", StringComparison.Ordinal));
    }

    /// <summary>Ensures miniscript fs.write enforces overwrite and createParents options.</summary>
    [Fact]
    public void Execute_Miniscript_Fs_Write_RespectsOverwriteAndCreateParents()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        harness.BaseFileSystem.AddDirectory("/docs");
        harness.BaseFileSystem.AddFile("/docs/a.txt", "A", fileKind: VfsFileKind.Text, size: 1);

        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/fs_write_opts.ms",
            """
            w1 = fs.write("/docs/a.txt", "X")
            print "w1ok=" + str(w1.ok)
            print "w1code=" + w1.code
            w2 = fs.write("/new/nested/out.txt", "Y")
            print "w2ok=" + str(w2.ok)
            print "w2code=" + w2.code
            opts = {}
            opts["createParents"] = 1
            w3 = fs.write("/new/nested/out.txt", "Y", opts)
            print "w3ok=" + str(w3.ok)
            print "w3code=" + w3.code
            print "w3path=" + w3.path
            print "w3written=" + str(w3.written)
            r = fs.read("/new/nested/out.txt")
            print "r3ok=" + str(r.ok)
            """,
            fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/fs_write_opts.ms", terminalSessionId: "ts-ms-fs-write-opts");

        Assert.True(result.Ok);
        Assert.Contains(result.Lines, static line => string.Equals(line, "w1ok=0", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "w1code=AlreadyExists", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "w2ok=0", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "w2code=NotFound", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "w3ok=1", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "w3code=None", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "w3path=/new/nested/out.txt", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "w3written=1", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "r3ok=1", StringComparison.Ordinal));
        Assert.True(harness.Server.DiskOverlay.TryResolveEntry("/new/nested/out.txt", out _));
    }

    /// <summary>Ensures miniscript fs.write emits fileAcquire for create and overwrite success paths.</summary>
    [Fact]
    public void Execute_Miniscript_Fs_Write_EmitsFileAcquire_OnCreateAndOverwrite()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        harness.BaseFileSystem.AddDirectory("/docs");

        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/fs_write_emit.ms",
            """
            w1 = fs.write("/docs/new.txt", "A")
            print "w1ok=" + str(w1.ok)
            print "w1code=" + w1.code
            opts = {}
            opts["overwrite"] = 1
            w2 = fs.write("/docs/new.txt", "B", opts)
            print "w2ok=" + str(w2.ok)
            print "w2code=" + w2.code
            """,
            fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/fs_write_emit.ms", terminalSessionId: "ts-ms-fs-write-emit");

        Assert.True(result.Ok);
        Assert.Contains(result.Lines, static line => string.Equals(line, "w1ok=1", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "w1code=None", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "w2ok=1", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "w2code=None", StringComparison.Ordinal));
        Assert.True(harness.Server.DiskOverlay.TryReadFileText("/docs/new.txt", out var finalContent));
        Assert.Equal("B", finalContent);

        var expectedUserKey = harness.Server.Users
            .Single(pair => string.Equals(pair.Value.UserId, harness.UserId, StringComparison.Ordinal))
            .Key;
        var events = DrainQueuedGameEvents(harness.World);
        var fileAcquireEvents = events
            .Where(static gameEvent => string.Equals((string)GetPropertyValue(gameEvent, "EventType"), "fileAcquire", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, fileAcquireEvents.Count);
        Assert.Equal(fileAcquireEvents.Count, events.Count);

        foreach (var fileAcquire in fileAcquireEvents)
        {
            var payload = GetPropertyValue(fileAcquire, "Payload");
            Assert.Equal(harness.Server.NodeId, (string?)GetPropertyValue(payload!, "FromNodeId"));
            Assert.Equal(expectedUserKey, (string?)GetPropertyValue(payload!, "UserKey"));
            Assert.Equal("new.txt", (string?)GetPropertyValue(payload!, "FileName"));
            Assert.Equal("/docs/new.txt", (string?)GetPropertyValue(payload!, "LocalPath"));
            Assert.Null(GetPropertyValue(payload!, "RemotePath"));
            Assert.Equal("fs.write", (string?)GetPropertyValue(payload!, "TransferMethod"));
        }
    }

    /// <summary>Ensures miniscript fs.delete handles success, missing target, and non-empty directory failures.</summary>
    [Fact]
    public void Execute_Miniscript_Fs_Delete_FailsOnMissingAndNonEmptyDirectory()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        harness.BaseFileSystem.AddDirectory("/tmp");
        harness.BaseFileSystem.AddFile("/tmp/x.txt", "X", fileKind: VfsFileKind.Text, size: 1);
        harness.BaseFileSystem.AddDirectory("/tmp/dir");
        harness.BaseFileSystem.AddFile("/tmp/dir/y.txt", "Y", fileKind: VfsFileKind.Text, size: 1);

        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/fs_delete_cases.ms",
            """
            d1 = fs.delete("/tmp/x.txt")
            print "d1ok=" + str(d1.ok)
            print "d1code=" + d1.code
            print "d1deleted=" + str(d1.deleted)
            d2 = fs.delete("/tmp/x.txt")
            print "d2ok=" + str(d2.ok)
            print "d2code=" + d2.code
            d3 = fs.delete("/tmp/dir")
            print "d3ok=" + str(d3.ok)
            print "d3code=" + d3.code
            """,
            fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/fs_delete_cases.ms", terminalSessionId: "ts-ms-fs-delete-cases");

        Assert.True(result.Ok);
        Assert.Contains(result.Lines, static line => string.Equals(line, "d1ok=1", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "d1code=None", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "d1deleted=1", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "d2ok=0", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "d2code=NotFound", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "d3ok=0", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "d3code=NotDirectory", StringComparison.Ordinal));
        Assert.False(harness.Server.DiskOverlay.TryResolveEntry("/tmp/x.txt", out _));
        Assert.True(harness.Server.DiskOverlay.TryResolveEntry("/tmp/dir/y.txt", out _));
    }

    /// <summary>Ensures session-scoped fs.list/read/stat use session user read privilege.</summary>
    [Fact]
    public void Execute_Miniscript_Fs_SessionReadApis_UseSessionReadPrivilege()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        var remote = AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, "pw");
        remote.DiskOverlay.AddDirectory("/drop");
        remote.DiskOverlay.WriteFile("/drop/a.txt", "A", fileKind: VfsFileKind.Text, size: 1);
        remote.Users["guest"].Privilege.Read = false;

        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/fs_session_read_perm.ms",
            """
            r = ssh.connect("10.0.1.20", "guest", "pw")
            l = fs.list(r.session, "/drop")
            print "lok=" + str(l.ok)
            print "lcode=" + l.code
            s = fs.stat(r.session, "/drop/a.txt")
            print "sok=" + str(s.ok)
            print "scode=" + s.code
            rd = fs.read(r.session, "/drop/a.txt")
            print "rok=" + str(rd.ok)
            print "rcode=" + rd.code
            d = ssh.disconnect(r.session)
            """,
            fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/fs_session_read_perm.ms", terminalSessionId: "ts-ms-fs-session-read");

        Assert.True(result.Ok);
        Assert.Contains(result.Lines, static line => string.Equals(line, "lok=0", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "lcode=PermissionDenied", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "sok=0", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "scode=PermissionDenied", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "rok=0", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "rcode=PermissionDenied", StringComparison.Ordinal));
    }

    /// <summary>Ensures session-scoped fs.write/delete use session user write privilege.</summary>
    [Fact]
    public void Execute_Miniscript_Fs_SessionWriteApis_UseSessionWritePrivilege()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        var remote = AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, "pw");
        remote.DiskOverlay.AddDirectory("/drop");
        remote.DiskOverlay.WriteFile("/drop/to-delete.txt", "D", fileKind: VfsFileKind.Text, size: 1);
        remote.Users["guest"].Privilege.Write = false;

        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/fs_session_write_perm.ms",
            """
            r = ssh.connect("10.0.1.20", "guest", "pw")
            w = fs.write(r.session, "/drop/new.txt", "N")
            print "wok=" + str(w.ok)
            print "wcode=" + w.code
            del = fs.delete(r.session, "/drop/to-delete.txt")
            print "dok=" + str(del.ok)
            print "dcode=" + del.code
            d = ssh.disconnect(r.session)
            """,
            fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/fs_session_write_perm.ms", terminalSessionId: "ts-ms-fs-session-write");

        Assert.True(result.Ok);
        Assert.Contains(result.Lines, static line => string.Equals(line, "wok=0", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "wcode=PermissionDenied", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "dok=0", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "dcode=PermissionDenied", StringComparison.Ordinal));
        Assert.False(remote.DiskOverlay.TryResolveEntry("/drop/new.txt", out _));
        Assert.True(remote.DiskOverlay.TryResolveEntry("/drop/to-delete.txt", out _));
    }

    /// <summary>Ensures route-scoped fs.list/read/stat use route.lastSession and ignore non-last route fields.</summary>
    [Fact]
    public void Execute_Miniscript_Fs_WithRoute_ReadApis_UseLastSessionOnly()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        var first = AddRemoteServer(
            harness,
            "node-2",
            "hop-b",
            "10.1.0.20",
            AuthMode.Static,
            "pw",
            exposure: PortExposure.Public,
            netId: "alpha");
        var last = AddRemoteServer(
            harness,
            "node-3",
            "hop-c",
            "10.1.0.30",
            AuthMode.Static,
            "pw",
            exposure: PortExposure.Lan,
            netId: "alpha");
        last.DiskOverlay.AddDirectory("/drop");
        last.DiskOverlay.WriteFile("/drop/a.txt", "A", fileKind: VfsFileKind.Text, size: 1);

        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/fs_route_read_perm.ms",
            """
            r1 = ssh.connect("10.1.0.20", "guest", "pw")
            opts = {}
            opts["session"] = r1.session
            r2 = ssh.connect("10.1.0.30", "guest", "pw", opts)
            route = r2.route
            route["version"] = 999
            route["sessions"] = []
            route["prefixRoutes"] = []
            route["hopCount"] = 0
            l = fs.list(route, "/drop")
            print "lok=" + str(l.ok)
            print "lcode=" + l.code
            s = fs.stat(route, "/drop/a.txt")
            print "sok=" + str(s.ok)
            print "scode=" + s.code
            rd = fs.read(route, "/drop/a.txt")
            print "rok=" + str(rd.ok)
            print "rcode=" + rd.code
            d2 = ssh.disconnect(r2.session)
            d1 = ssh.disconnect(r1.session)
            """,
            fileKind: VfsFileKind.Text);

        first.Users["guest"].Privilege.Read = false;
        last.Users["guest"].Privilege.Read = true;
        var firstReadDeniedButLastAllowed = Execute(harness, "miniscript /scripts/fs_route_read_perm.ms", terminalSessionId: "ts-ms-fs-route-read");
        Assert.True(firstReadDeniedButLastAllowed.Ok);
        Assert.Contains(firstReadDeniedButLastAllowed.Lines, static line => string.Equals(line, "lok=1", StringComparison.Ordinal));
        Assert.Contains(firstReadDeniedButLastAllowed.Lines, static line => string.Equals(line, "lcode=None", StringComparison.Ordinal));
        Assert.Contains(firstReadDeniedButLastAllowed.Lines, static line => string.Equals(line, "sok=1", StringComparison.Ordinal));
        Assert.Contains(firstReadDeniedButLastAllowed.Lines, static line => string.Equals(line, "scode=None", StringComparison.Ordinal));
        Assert.Contains(firstReadDeniedButLastAllowed.Lines, static line => string.Equals(line, "rok=1", StringComparison.Ordinal));
        Assert.Contains(firstReadDeniedButLastAllowed.Lines, static line => string.Equals(line, "rcode=None", StringComparison.Ordinal));

        first.Users["guest"].Privilege.Read = true;
        last.Users["guest"].Privilege.Read = false;
        var lastReadDenied = Execute(harness, "miniscript /scripts/fs_route_read_perm.ms", terminalSessionId: "ts-ms-fs-route-read");
        Assert.True(lastReadDenied.Ok);
        Assert.Contains(lastReadDenied.Lines, static line => string.Equals(line, "lok=0", StringComparison.Ordinal));
        Assert.Contains(lastReadDenied.Lines, static line => string.Equals(line, "lcode=PermissionDenied", StringComparison.Ordinal));
        Assert.Contains(lastReadDenied.Lines, static line => string.Equals(line, "sok=0", StringComparison.Ordinal));
        Assert.Contains(lastReadDenied.Lines, static line => string.Equals(line, "scode=PermissionDenied", StringComparison.Ordinal));
        Assert.Contains(lastReadDenied.Lines, static line => string.Equals(line, "rok=0", StringComparison.Ordinal));
        Assert.Contains(lastReadDenied.Lines, static line => string.Equals(line, "rcode=PermissionDenied", StringComparison.Ordinal));
    }

    /// <summary>Ensures route-scoped fs.write/delete use route.lastSession and ignore non-last route fields.</summary>
    [Fact]
    public void Execute_Miniscript_Fs_WithRoute_WriteApis_UseLastSessionOnly()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        var first = AddRemoteServer(
            harness,
            "node-2",
            "hop-b",
            "10.1.0.20",
            AuthMode.Static,
            "pw",
            exposure: PortExposure.Public,
            netId: "alpha");
        var last = AddRemoteServer(
            harness,
            "node-3",
            "hop-c",
            "10.1.0.30",
            AuthMode.Static,
            "pw",
            exposure: PortExposure.Lan,
            netId: "alpha");
        last.DiskOverlay.AddDirectory("/drop");
        last.DiskOverlay.WriteFile("/drop/to-delete.txt", "D", fileKind: VfsFileKind.Text, size: 1);

        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/fs_route_write_perm.ms",
            """
            r1 = ssh.connect("10.1.0.20", "guest", "pw")
            opts = {}
            opts["session"] = r1.session
            r2 = ssh.connect("10.1.0.30", "guest", "pw", opts)
            route = r2.route
            route["version"] = 999
            route["sessions"] = []
            route["prefixRoutes"] = []
            route["hopCount"] = 0
            w = fs.write(route, "/drop/new.txt", "N")
            print "wok=" + str(w.ok)
            print "wcode=" + w.code
            del = fs.delete(route, "/drop/to-delete.txt")
            print "dok=" + str(del.ok)
            print "dcode=" + del.code
            d2 = ssh.disconnect(r2.session)
            d1 = ssh.disconnect(r1.session)
            """,
            fileKind: VfsFileKind.Text);

        first.Users["guest"].Privilege.Write = false;
        last.Users["guest"].Privilege.Write = true;
        var firstWriteDeniedButLastAllowed = Execute(harness, "miniscript /scripts/fs_route_write_perm.ms", terminalSessionId: "ts-ms-fs-route-write");
        Assert.True(firstWriteDeniedButLastAllowed.Ok);
        Assert.Contains(firstWriteDeniedButLastAllowed.Lines, static line => string.Equals(line, "wok=1", StringComparison.Ordinal));
        Assert.Contains(firstWriteDeniedButLastAllowed.Lines, static line => string.Equals(line, "wcode=None", StringComparison.Ordinal));
        Assert.Contains(firstWriteDeniedButLastAllowed.Lines, static line => string.Equals(line, "dok=1", StringComparison.Ordinal));
        Assert.Contains(firstWriteDeniedButLastAllowed.Lines, static line => string.Equals(line, "dcode=None", StringComparison.Ordinal));
        Assert.True(last.DiskOverlay.TryResolveEntry("/drop/new.txt", out _));
        Assert.False(last.DiskOverlay.TryResolveEntry("/drop/to-delete.txt", out _));

        first.Users["guest"].Privilege.Write = true;
        last.Users["guest"].Privilege.Write = false;
        var lastWriteDenied = Execute(harness, "miniscript /scripts/fs_route_write_perm.ms", terminalSessionId: "ts-ms-fs-route-write");
        Assert.True(lastWriteDenied.Ok);
        Assert.Contains(lastWriteDenied.Lines, static line => string.Equals(line, "wok=0", StringComparison.Ordinal));
        Assert.Contains(lastWriteDenied.Lines, static line => string.Equals(line, "wcode=PermissionDenied", StringComparison.Ordinal));
        Assert.Contains(lastWriteDenied.Lines, static line => string.Equals(line, "dok=0", StringComparison.Ordinal));
        Assert.Contains(lastWriteDenied.Lines, static line => string.Equals(line, "dcode=PermissionDenied", StringComparison.Ordinal));
    }

    /// <summary>Ensures route-scoped fs APIs return InvalidArgs when route.lastSession cannot be resolved.</summary>
    [Fact]
    public void Execute_Miniscript_Fs_WithRoute_InvalidLastSession_ReturnsInvalidArgs()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        AddRemoteServer(
            harness,
            "node-2",
            "hop-b",
            "10.1.0.20",
            AuthMode.Static,
            "pw",
            exposure: PortExposure.Public,
            netId: "alpha");
        var last = AddRemoteServer(
            harness,
            "node-3",
            "hop-c",
            "10.1.0.30",
            AuthMode.Static,
            "pw",
            exposure: PortExposure.Lan,
            netId: "alpha");
        last.DiskOverlay.AddDirectory("/drop");
        last.DiskOverlay.WriteFile("/drop/a.txt", "A", fileKind: VfsFileKind.Text, size: 1);

        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/fs_route_invalid_last.ms",
            """
            r1 = ssh.connect("10.1.0.20", "guest", "pw")
            opts = {}
            opts["session"] = r1.session
            r2 = ssh.connect("10.1.0.30", "guest", "pw", opts)
            route = r2.route
            route["version"] = 999
            route["sessions"] = []
            route["prefixRoutes"] = []
            route["hopCount"] = 0
            bad = {}
            bad["kind"] = "sshSession"
            bad["sessionNodeId"] = "node-3"
            bad["sessionId"] = 9999
            route["lastSession"] = bad
            rd = fs.read(route, "/drop/a.txt")
            print "ok=" + str(rd.ok)
            print "code=" + rd.code
            d2 = ssh.disconnect(r2.session)
            d1 = ssh.disconnect(r1.session)
            """,
            fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/fs_route_invalid_last.ms", terminalSessionId: "ts-ms-fs-route-invalid-last");

        Assert.True(result.Ok);
        Assert.Contains(result.Lines, static line => string.Equals(line, "ok=0", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "code=InvalidArgs", StringComparison.Ordinal));
    }

    /// <summary>Ensures miniscript net.scan returns per-interface neighbors plus flattened unique IPs.</summary>
    [Fact]
    public void Execute_Miniscript_Net_Scan_LocalLan_SucceedsAndReturnsInterfacesAndIps()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        harness.Server.SetInterfaces(new[]
        {
            new InterfaceRuntime
            {
                NetId = "internet",
                Ip = "10.0.0.10",
            },
            new InterfaceRuntime
            {
                NetId = "alpha",
                Ip = "10.1.0.10",
            },
            new InterfaceRuntime
            {
                NetId = "beta",
                Ip = "10.2.0.10",
            },
        });

        AddRemoteServer(harness, "node-2", "remote-a", "10.1.0.20", AuthMode.Static, "pw", netId: "alpha");
        AddRemoteServer(harness, "node-3", "remote-b", "10.1.0.30", AuthMode.Static, "pw", netId: "alpha");
        AddRemoteServer(harness, "node-4", "remote-c", "10.2.0.40", AuthMode.Static, "pw", netId: "beta");
        harness.Server.LanNeighbors.Add("node-3");
        harness.Server.LanNeighbors.Add("node-4");
        harness.Server.LanNeighbors.Add("node-2");
        harness.Server.LanNeighbors.Add("node-2");
        harness.Server.LanNeighbors.Add("missing-node");

        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/net_scan_local.ms",
            """
            s = net.scan()
            print "ok=" + str(s.ok)
            print "code=" + s.code
            print "ifCount=" + str(len(s.interfaces))
            print "if0=" + s.interfaces[0].netId + ":" + s.interfaces[0].localIp
            print "if0Count=" + str(len(s.interfaces[0].neighbors))
            print "if0n0=" + s.interfaces[0].neighbors[0]
            print "if0n1=" + s.interfaces[0].neighbors[1]
            print "if1=" + s.interfaces[1].netId + ":" + s.interfaces[1].localIp
            print "if1Count=" + str(len(s.interfaces[1].neighbors))
            print "if1n0=" + s.interfaces[1].neighbors[0]
            print "count=" + str(len(s.ips))
            print "ip0=" + s.ips[0]
            print "ip1=" + s.ips[1]
            print "ip2=" + s.ips[2]
            """,
            fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/net_scan_local.ms", terminalSessionId: "ts-ms-net-scan-local");

        Assert.True(result.Ok);
        Assert.Contains(result.Lines, static line => string.Equals(line, "ok=1", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "code=None", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "ifCount=2", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "if0=alpha:10.1.0.10", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "if0Count=2", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "if0n0=10.1.0.20", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "if0n1=10.1.0.30", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "if1=beta:10.2.0.10", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "if1Count=1", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "if1n0=10.2.0.40", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "count=3", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "ip0=10.1.0.20", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "ip1=10.1.0.30", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "ip2=10.2.0.40", StringComparison.Ordinal));
    }

    /// <summary>Ensures miniscript net.scan(netId) limits results to the requested interface.</summary>
    [Fact]
    public void Execute_Miniscript_Net_Scan_WithNetIdFilter_SucceedsAndReturnsSingleInterface()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        harness.Server.SetInterfaces(new[]
        {
            new InterfaceRuntime
            {
                NetId = "internet",
                Ip = "10.0.0.10",
            },
            new InterfaceRuntime
            {
                NetId = "alpha",
                Ip = "10.1.0.10",
            },
            new InterfaceRuntime
            {
                NetId = "beta",
                Ip = "10.2.0.10",
            },
        });

        AddRemoteServer(harness, "node-2", "remote-a", "10.1.0.20", AuthMode.Static, "pw", netId: "alpha");
        AddRemoteServer(harness, "node-3", "remote-b", "10.2.0.30", AuthMode.Static, "pw", netId: "beta");
        harness.Server.LanNeighbors.Add("node-2");
        harness.Server.LanNeighbors.Add("node-3");

        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/net_scan_netid.ms",
            """
            s = net.scan("alpha")
            print "ok=" + str(s.ok)
            print "code=" + s.code
            print "ifCount=" + str(len(s.interfaces))
            print "if0=" + s.interfaces[0].netId + ":" + s.interfaces[0].localIp
            print "if0Count=" + str(len(s.interfaces[0].neighbors))
            print "count=" + str(len(s.ips))
            print "ip0=" + s.ips[0]
            """,
            fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/net_scan_netid.ms", terminalSessionId: "ts-ms-net-scan-netid");

        Assert.True(result.Ok);
        Assert.Contains(result.Lines, static line => string.Equals(line, "ok=1", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "code=None", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "ifCount=1", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "if0=alpha:10.1.0.10", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "if0Count=1", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "count=1", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "ip0=10.1.0.20", StringComparison.Ordinal));
    }

    /// <summary>Ensures miniscript net.scan returns NotFound for unknown interface ids.</summary>
    [Fact]
    public void Execute_Miniscript_Net_Scan_UnknownNetId_ReturnsNotFound()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        harness.Server.SetInterfaces(new[]
        {
            new InterfaceRuntime
            {
                NetId = "internet",
                Ip = "10.0.0.10",
            },
            new InterfaceRuntime
            {
                NetId = "alpha",
                Ip = "10.1.0.10",
            },
        });

        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/net_scan_unknown_netid.ms",
            """
            s = net.scan("gamma")
            print "ok=" + str(s.ok)
            print "code=" + s.code
            """,
            fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/net_scan_unknown_netid.ms", terminalSessionId: "ts-ms-net-scan-unknown");

        Assert.True(result.Ok);
        Assert.Contains(result.Lines, static line => string.Equals(line, "ok=0", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "code=NotFound", StringComparison.Ordinal));
    }

    /// <summary>Ensures miniscript net.interfaces returns all local interfaces sorted by netId and IP.</summary>
    [Fact]
    public void Execute_Miniscript_Net_Interfaces_SucceedsAndReturnsAllLocalInterfaces()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        harness.Server.SetInterfaces(new[]
        {
            new InterfaceRuntime
            {
                NetId = "beta",
                Ip = "10.2.0.10",
            },
            new InterfaceRuntime
            {
                NetId = "internet",
                Ip = "10.0.0.10",
            },
            new InterfaceRuntime
            {
                NetId = "alpha",
                Ip = "10.1.0.10",
            },
        });

        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/net_interfaces.ms",
            """
            s = net.interfaces()
            print "ok=" + str(s.ok)
            print "code=" + s.code
            print "count=" + str(len(s.interfaces))
            print "i0=" + s.interfaces[0].netId + ":" + s.interfaces[0].localIp
            print "i1=" + s.interfaces[1].netId + ":" + s.interfaces[1].localIp
            print "i2=" + s.interfaces[2].netId + ":" + s.interfaces[2].localIp
            """,
            fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/net_interfaces.ms", terminalSessionId: "ts-ms-net-interfaces");

        Assert.True(result.Ok);
        Assert.Contains(result.Lines, static line => string.Equals(line, "ok=1", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "code=None", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "count=3", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "i0=alpha:10.1.0.10", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "i1=beta:10.2.0.10", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "i2=internet:10.0.0.10", StringComparison.Ordinal));
    }

    /// <summary>Ensures route-scoped net.scan resolves source/permissions from route.lastSession only.</summary>
    [Fact]
    public void Execute_Miniscript_Net_Scan_Route_UsesLastSessionOnly()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        var first = AddRemoteServer(
            harness,
            "node-2",
            "hop-b",
            "10.1.0.20",
            AuthMode.Static,
            "pw",
            exposure: PortExposure.Public,
            netId: "alpha");
        var last = AddRemoteServer(
            harness,
            "node-3",
            "hop-c",
            "10.1.0.30",
            AuthMode.Static,
            "pw",
            exposure: PortExposure.Lan,
            netId: "alpha");
        AddRemoteServer(
            harness,
            "node-4",
            "hop-d",
            "10.1.0.40",
            AuthMode.Static,
            "pw",
            exposure: PortExposure.Lan,
            netId: "alpha");
        last.LanNeighbors.Add("node-4");
        last.LanNeighbors.Add("node-2");

        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/net_scan_route.ms",
            """
            r1 = ssh.connect("10.1.0.20", "guest", "pw")
            opts = {}
            opts["session"] = r1.session
            r2 = ssh.connect("10.1.0.30", "guest", "pw", opts)
            route = r2.route
            route["version"] = 999
            route["sessions"] = []
            route["prefixRoutes"] = []
            route["hopCount"] = 0
            s = net.scan(route)
            print "ok=" + str(s.ok)
            print "code=" + s.code
            if s.ok == 1 then print "count=" + str(len(s.ips))
            d2 = ssh.disconnect(r2.session)
            d1 = ssh.disconnect(r1.session)
            """,
            fileKind: VfsFileKind.Text);

        first.Users["guest"].Privilege.Execute = false;
        last.Users["guest"].Privilege.Execute = true;
        var firstDeniedButLastAllowed = Execute(harness, "miniscript /scripts/net_scan_route.ms", terminalSessionId: "ts-ms-net-scan-route");
        Assert.True(firstDeniedButLastAllowed.Ok);
        Assert.Contains(firstDeniedButLastAllowed.Lines, static line => string.Equals(line, "ok=1", StringComparison.Ordinal));
        Assert.Contains(firstDeniedButLastAllowed.Lines, static line => string.Equals(line, "code=None", StringComparison.Ordinal));
        Assert.Contains(firstDeniedButLastAllowed.Lines, static line => string.Equals(line, "count=2", StringComparison.Ordinal));

        first.Users["guest"].Privilege.Execute = true;
        last.Users["guest"].Privilege.Execute = false;
        var lastDenied = Execute(harness, "miniscript /scripts/net_scan_route.ms", terminalSessionId: "ts-ms-net-scan-route");
        Assert.True(lastDenied.Ok);
        Assert.Contains(lastDenied.Lines, static line => string.Equals(line, "ok=0", StringComparison.Ordinal));
        Assert.Contains(lastDenied.Lines, static line => string.Equals(line, "code=PermissionDenied", StringComparison.Ordinal));
    }

    /// <summary>Ensures net.ports hides ports denied by exposure policy instead of failing the full call.</summary>
    [Fact]
    public void Execute_Miniscript_Net_Ports_HidesDeniedPorts()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        var remote = AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, "pw");
        AddFtpPort(remote, exposure: PortExposure.Public);
        remote.Ports[80] = new PortConfig
        {
            PortType = PortType.Http,
            Exposure = PortExposure.Localhost,
            ServiceId = "hidden-http",
        };

        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/net_ports_hidden.ms",
            """
            p = net.ports("10.0.1.20")
            print "ok=" + str(p.ok)
            print "code=" + p.code
            print "count=" + str(len(p.ports))
            has80 = 0
            for entry in p.ports
                if entry.port == 80 then has80 = 1
            end for
            print "has80=" + str(has80)
            """,
            fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/net_ports_hidden.ms", terminalSessionId: "ts-ms-net-ports-hidden");

        Assert.True(result.Ok);
        Assert.Contains(result.Lines, static line => string.Equals(line, "ok=1", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "code=None", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "count=2", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "has80=0", StringComparison.Ordinal));
    }

    /// <summary>Ensures net.banner returns PortConfig.serviceId and empty string when serviceId is blank.</summary>
    [Fact]
    public void Execute_Miniscript_Net_Banner_UsesServiceId()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        var remote = AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, "pw");
        remote.Ports[22].ServiceId = "OpenSSH_8.2p1 easy-box";
        AddFtpPort(remote, exposure: PortExposure.Public);
        remote.Ports[21].ServiceId = "   ";

        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/net_banner_service_id.ms",
            """
            b1 = net.banner("10.0.1.20", 22)
            print "b1ok=" + str(b1.ok)
            print "b1code=" + b1.code
            print "b1=" + b1.banner
            b2 = net.banner("10.0.1.20", 21)
            print "b2ok=" + str(b2.ok)
            print "b2code=" + b2.code
            print "b2len=" + str(len(b2.banner))
            """,
            fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/net_banner_service_id.ms", terminalSessionId: "ts-ms-net-banner-service-id");

        Assert.True(result.Ok);
        Assert.Contains(result.Lines, static line => string.Equals(line, "b1ok=1", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "b1code=None", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "b1=OpenSSH_8.2p1 easy-box", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "b2ok=1", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "b2code=None", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "b2len=0", StringComparison.Ordinal));
    }

    /// <summary>Ensures net.banner returns PortClosed for missing or unassigned ports.</summary>
    [Fact]
    public void Execute_Miniscript_Net_Banner_ReturnsPortClosed_ForUnassignedPort()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        var remote = AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, "pw");
        remote.Ports[2222] = new PortConfig
        {
            PortType = PortType.None,
            Exposure = PortExposure.Public,
        };

        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/net_banner_port_closed.ms",
            """
            a = net.banner("10.0.1.20", 9999)
            print "aok=" + str(a.ok)
            print "acode=" + a.code
            b = net.banner("10.0.1.20", 2222)
            print "bok=" + str(b.ok)
            print "bcode=" + b.code
            """,
            fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/net_banner_port_closed.ms", terminalSessionId: "ts-ms-net-banner-port-closed");

        Assert.True(result.Ok);
        Assert.Contains(result.Lines, static line => string.Equals(line, "aok=0", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "acode=PortClosed", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "bok=0", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "bcode=PortClosed", StringComparison.Ordinal));
    }

    /// <summary>Ensures net.banner returns NetDenied when target port exposure blocks source access.</summary>
    [Fact]
    public void Execute_Miniscript_Net_Banner_ReturnsNetDenied_OnExposureViolation()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        var remote = AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, "pw");
        remote.Ports[22].Exposure = PortExposure.Localhost;

        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/net_banner_net_denied.ms",
            """
            b = net.banner("10.0.1.20", 22)
            print "ok=" + str(b.ok)
            print "code=" + b.code
            """,
            fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/net_banner_net_denied.ms", terminalSessionId: "ts-ms-net-banner-net-denied");

        Assert.True(result.Ok);
        Assert.Contains(result.Lines, static line => string.Equals(line, "ok=0", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "code=NetDenied", StringComparison.Ordinal));
    }

    /// <summary>Ensures net intrinsics validate malformed arguments and return InvalidArgs.</summary>
    [Fact]
    public void Execute_Miniscript_Net_InvalidArgs_ReturnsInvalidArgs()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, "pw");

        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/net_invalid_args.ms",
            """
            s = net.scan("lan")
            print "scode=" + s.code
            bad = {}
            bad["kind"] = "nope"
            p = net.ports(bad, "10.0.1.20")
            print "pcode=" + p.code
            b1 = net.banner("10.0.1.20", 0)
            print "b1code=" + b1.code
            b2 = net.banner("   ", 22)
            print "b2code=" + b2.code
            """,
            fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/net_invalid_args.ms", terminalSessionId: "ts-ms-net-invalid-args");

        Assert.True(result.Ok);
        Assert.Contains(result.Lines, static line => string.Equals(line, "scode=InvalidArgs", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "pcode=InvalidArgs", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "b1code=InvalidArgs", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => string.Equals(line, "b2code=InvalidArgs", StringComparison.Ordinal));
    }

    /// <summary>Ensures async miniscript fs.write/delete are validate-only in sandbox mode.</summary>
    [Fact]
    public void TryStartTerminalProgramExecution_FsSandbox_WriteDelete_ValidateOnly_DoesNotMutateWorld()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        harness.BaseFileSystem.AddDirectory("/sandbox");
        harness.BaseFileSystem.AddFile("/sandbox/keep.txt", "KEEP", fileKind: VfsFileKind.Text, size: 4);

        harness.BaseFileSystem.AddDirectory("/scripts");
        harness.BaseFileSystem.AddFile(
            "/scripts/fs_async_validate.ms",
            """
            w = fs.write("/sandbox/new.txt", "N")
            d = fs.delete("/sandbox/keep.txt")
            print "wok=" + str(w.ok)
            print "wcode=" + w.code
            print "dok=" + str(d.ok)
            print "dcode=" + d.code
            """,
            fileKind: VfsFileKind.Text);

        var start = TryStartTerminalProgramExecutionCore(
            harness.World,
            harness.Server.NodeId,
            harness.UserId,
            harness.Cwd,
            "miniscript /scripts/fs_async_validate.ms",
            "ts-ms-fs-async");
        Assert.True(start.Handled);
        Assert.True(start.Started);

        WaitForTerminalProgramStop(harness.World, "ts-ms-fs-async");
        var outputLines = SnapshotTerminalEventLines(harness.World);
        Assert.Contains("wok=1", outputLines);
        Assert.Contains("wcode=None", outputLines);
        Assert.Contains("dok=1", outputLines);
        Assert.Contains("dcode=None", outputLines);

        Assert.False(harness.Server.DiskOverlay.TryResolveEntry("/sandbox/new.txt", out _));
        Assert.True(harness.Server.DiskOverlay.TryResolveEntry("/sandbox/keep.txt", out _));
        var events = DrainQueuedGameEvents(harness.World);
        var fileAcquireEvents = events
            .Where(static gameEvent => string.Equals((string)GetPropertyValue(gameEvent, "EventType"), "fileAcquire", StringComparison.Ordinal))
            .ToList();
        Assert.Empty(fileAcquireEvents);
        Assert.Empty(events);
    }

    /// <summary>Ensures processor context creation requires userId and rejects unknown user ids.</summary>
    [Fact]
    public void Execute_ContextCreation_UsesUserIdLookup()
    {
        var harness = CreateHarness(includeVfsModule: false, includeConnectModule: true);

        var missingUserId = Execute(harness, "known", userId: string.Empty, terminalSessionId: "ts-missing-userid");
        Assert.False(missingUserId.Ok);
        Assert.Equal(SystemCallErrorCode.InvalidArgs, missingUserId.Code);
        Assert.Contains("userId is required", missingUserId.Lines[0], StringComparison.Ordinal);

        var unknownUserId = Execute(harness, "known", userId: "nope", terminalSessionId: "ts-unknown-userid");
        Assert.False(unknownUserId.Ok);
        Assert.Equal(SystemCallErrorCode.NotFound, unknownUserId.Code);
        Assert.Contains("user not found: nope", unknownUserId.Lines[0], StringComparison.Ordinal);
    }

    /// <summary>Ensures inspect validates argument shape and returns canonical invalid-args output.</summary>
    [Theory]
    [InlineData("inspect")]
    [InlineData("inspect -p")]
    [InlineData("inspect --port 70000 10.0.1.20 guest")]
    [InlineData("inspect --invalid 10.0.1.20 guest")]
    [InlineData("inspect -p 22 10.0.1.20")]
    public void Execute_Inspect_InvalidArgs_ReturnsCanonicalFailure(string commandLine)
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/inspect", "exec:inspect", fileKind: VfsFileKind.ExecutableHardcode);

        var result = Execute(harness, commandLine, terminalSessionId: "ts-inspect-invalid");

        Assert.False(result.Ok);
        Assert.Equal(SystemCallErrorCode.InvalidArgs, result.Code);
        Assert.Equal("error: invalid args", result.Lines[0]);
        Assert.Equal("code: ERR_INVALID_ARGS", result.Lines[1]);
    }

    /// <summary>Ensures inspect reports not-found for unknown hosts.</summary>
    [Fact]
    public void Execute_Inspect_UnknownHost_ReturnsNotFound()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/inspect", "exec:inspect", fileKind: VfsFileKind.ExecutableHardcode);

        var result = Execute(harness, "inspect 10.0.1.99 guest", terminalSessionId: "ts-inspect-host");

        Assert.False(result.Ok);
        Assert.Equal(SystemCallErrorCode.NotFound, result.Code);
        Assert.Equal("error: host not found", result.Lines[0]);
        Assert.Equal("code: ERR_NOT_FOUND", result.Lines[1]);
    }

    /// <summary>Ensures inspect folds closed and non-SSH ports into PortClosed.</summary>
    [Fact]
    public void Execute_Inspect_PortClosedAndNonSsh_ReturnPortClosed()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/inspect", "exec:inspect", fileKind: VfsFileKind.ExecutableHardcode);
        AddRemoteServer(harness, "node-2", "remote-a", "10.0.1.20", AuthMode.Static, "pw");
        AddRemoteServer(harness, "node-3", "remote-b", "10.0.1.30", AuthMode.Static, "pw", portType: PortType.Http);

        var missingPort = Execute(harness, "inspect -p 9999 10.0.1.20 guest", terminalSessionId: "ts-inspect-port-missing");
        Assert.False(missingPort.Ok);
        Assert.Equal(SystemCallErrorCode.PortClosed, missingPort.Code);
        Assert.Equal("error: port is closed", missingPort.Lines[0]);
        Assert.Equal("code: ERR_PORT_CLOSED", missingPort.Lines[1]);

        var nonSshPort = Execute(harness, "inspect 10.0.1.30 guest", terminalSessionId: "ts-inspect-port-nonssh");
        Assert.False(nonSshPort.Ok);
        Assert.Equal(SystemCallErrorCode.PortClosed, nonSshPort.Code);
        Assert.Equal("error: port is closed", nonSshPort.Lines[0]);
        Assert.Equal("code: ERR_PORT_CLOSED", nonSshPort.Lines[1]);
    }

    /// <summary>Ensures inspect applies exposure rules and returns NetDenied for blocked source/target pairs.</summary>
    [Fact]
    public void Execute_Inspect_ExposureDenied_ReturnsNetDenied()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/inspect", "exec:inspect", fileKind: VfsFileKind.ExecutableHardcode);
        AddRemoteServer(
            harness,
            "node-2",
            "remote",
            "10.0.1.20",
            AuthMode.Static,
            "pw",
            exposure: PortExposure.Localhost);

        var result = Execute(harness, "inspect 10.0.1.20 guest", terminalSessionId: "ts-inspect-net-denied");

        Assert.False(result.Ok);
        Assert.Equal(SystemCallErrorCode.NetDenied, result.Code);
        Assert.Equal("error: network access denied", result.Lines[0]);
        Assert.Equal("code: ERR_NET_DENIED", result.Lines[1]);
    }

    /// <summary>Ensures inspect masks unknown users as AuthFailed to prevent account enumeration.</summary>
    [Fact]
    public void Execute_Inspect_MissingUser_ReturnsAuthFailed()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/inspect", "exec:inspect", fileKind: VfsFileKind.ExecutableHardcode);
        var remote = AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, "pw");

        var result = Execute(harness, "inspect 10.0.1.20 admin", terminalSessionId: "ts-inspect-auth-fail");

        Assert.False(result.Ok);
        Assert.Equal(SystemCallErrorCode.AuthFailed, result.Code);
        Assert.Equal("error: authentication failed", result.Lines[0]);
        Assert.Equal("code: ERR_AUTH_FAILED", result.Lines[1]);
        Assert.Empty(remote.Sessions);
        var events = DrainQueuedGameEvents(harness.World);
        Assert.Empty(events);
    }

    /// <summary>Ensures inspect none-auth output includes only kind metadata and keeps world side effects empty.</summary>
    [Fact]
    public void Execute_Inspect_NoneAuth_Succeeds_WithoutSideEffects()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/inspect", "exec:inspect", fileKind: VfsFileKind.ExecutableHardcode);
        var remote = AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.None, "ignored");

        var result = Execute(harness, "inspect 10.0.1.20 guest", terminalSessionId: "ts-inspect-none");

        Assert.True(result.Ok);
        Assert.Equal(SystemCallErrorCode.None, result.Code);
        Assert.Contains("passwd.kind: none", result.Lines);
        Assert.DoesNotContain(result.Lines, static line => line.StartsWith("passwd.length:", StringComparison.Ordinal));
        Assert.Empty(remote.Sessions);
        var events = DrainQueuedGameEvents(harness.World);
        Assert.Empty(events);
    }

    /// <summary>Ensures inspect recognizes AUTO cN_numspecial static policy and exposes policy fields.</summary>
    [Fact]
    public void Execute_Inspect_StaticAutoNumSpecial_ReturnsPolicy()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/inspect", "exec:inspect", fileKind: VfsFileKind.ExecutableHardcode);
        var worldSeed = GetWorldSeedBackingField(harness.World);
        var generatedPassword = InvokeResolvePassword("AUTO:c4_numspecial", worldSeed, "node-2", "guest");
        AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, generatedPassword);

        var result = Execute(harness, "inspect 10.0.1.20 guest", terminalSessionId: "ts-inspect-policy");

        Assert.True(result.Ok);
        Assert.Contains("passwd.kind: policy", result.Lines);
        Assert.Contains("passwd.length: 4", result.Lines);
        Assert.Contains("passwd.alphabetId: numspecial", result.Lines);
        Assert.Contains("passwd.alphabet: 0123456789!@#$%^&*()", result.Lines);
        Assert.DoesNotContain(result.Lines, static line => line.StartsWith("passwd.policyId:", StringComparison.Ordinal));
    }

    /// <summary>Ensures inspect recognizes dictionary-based static password policies without leaking length/alphabet fields.</summary>
    [Fact]
    public void Execute_Inspect_StaticDictionary_ReturnsDictionaryKindOnly()
    {
        WithDictionaryPasswordPool(
            new[] { "tiny", "verylongpassword" },
            () =>
            {
                var harness = CreateHarness(includeVfsModule: true);
                harness.BaseFileSystem.AddFile("/opt/bin/inspect", "exec:inspect", fileKind: VfsFileKind.ExecutableHardcode);
                var worldSeed = GetWorldSeedBackingField(harness.World);
                var generatedPassword = InvokeResolvePassword("AUTO:dictionaryHard", worldSeed, "node-2", "guest");
                AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, generatedPassword);

                var result = Execute(harness, "inspect 10.0.1.20 guest", terminalSessionId: "ts-inspect-dictionary");

                Assert.True(result.Ok);
                Assert.Contains("passwd.kind: dictionary", result.Lines);
                Assert.DoesNotContain(result.Lines, static line => line.StartsWith("passwd.length:", StringComparison.Ordinal));
                Assert.DoesNotContain(result.Lines, static line => line.StartsWith("passwd.alphabet:", StringComparison.Ordinal));
            });
    }

    /// <summary>Ensures inspect returns OTP metadata with numeric alphabet according to runtime token format.</summary>
    [Fact]
    public void Execute_Inspect_Otp_ReturnsNumericAlphabet()
    {
        const string otpPairId = "X2BW6QOI53QNUHBXCBYN6XADEQFPR5FJ";
        const long stepMs = 1000;
        const int allowedDriftSteps = 1;

        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/inspect", "exec:inspect", fileKind: VfsFileKind.ExecutableHardcode);
        var remote = AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Otp, string.Empty);
        remote.Daemons[DaemonType.Otp] = new DaemonStruct
        {
            DaemonType = DaemonType.Otp,
        };
        remote.Daemons[DaemonType.Otp].DaemonArgs["userKey"] = "guest";
        remote.Daemons[DaemonType.Otp].DaemonArgs["stepMs"] = stepMs;
        remote.Daemons[DaemonType.Otp].DaemonArgs["allowedDriftSteps"] = allowedDriftSteps;
        remote.Daemons[DaemonType.Otp].DaemonArgs["otpPairId"] = otpPairId;

        var result = Execute(harness, "inspect 10.0.1.20 guest", terminalSessionId: "ts-inspect-otp");

        Assert.True(result.Ok);
        Assert.Contains("passwd.kind: otp", result.Lines);
        Assert.Contains("passwd.length: 6", result.Lines);
        Assert.Contains("passwd.alphabetId: number", result.Lines);
        Assert.Contains("passwd.alphabet: 0123456789", result.Lines);
    }

    /// <summary>Ensures inspect classifies non-AUTO static passwords into number/alphabet/numberalphabet/unknown fallback policies.</summary>
    [Theory]
    [InlineData("12345", 5, "number", "0123456789")]
    [InlineData("root", 4, "alphabet", "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ")]
    [InlineData("abc123", 6, "numberalphabet", "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ")]
    [InlineData("manual-pass", 11, "unknown", "???")]
    public void Execute_Inspect_StaticFallback_ClassifiesNonAutoPasswords(
        string password,
        int expectedLength,
        string expectedAlphabetId,
        string expectedAlphabet)
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/inspect", "exec:inspect", fileKind: VfsFileKind.ExecutableHardcode);
        AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, password);

        var result = Execute(harness, "inspect 10.0.1.20 guest", terminalSessionId: "ts-inspect-fallback");

        Assert.True(result.Ok);
        Assert.Contains("passwd.kind: policy", result.Lines);
        Assert.Contains($"passwd.length: {expectedLength}", result.Lines);
        Assert.Contains("passwd.alphabetId: " + expectedAlphabetId, result.Lines);
        Assert.Contains("passwd.alphabet: " + expectedAlphabet, result.Lines);
        Assert.DoesNotContain(result.Lines, static line => line.StartsWith("passwd.policyId:", StringComparison.Ordinal));
    }

    /// <summary>Ensures inspect uses its dedicated shared limiter and returns RateLimited when the bucket is exhausted.</summary>
    [Fact]
    public void Execute_Inspect_RateLimited_ReturnsErrRateLimited()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/inspect", "exec:inspect", fileKind: VfsFileKind.ExecutableHardcode);
        AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, "pw");
        SetInspectProbeRateLimitState(
            harness.World,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            100000);

        var result = Execute(harness, "inspect 10.0.1.20 guest", terminalSessionId: "ts-inspect-rate-limit");

        Assert.False(result.Ok);
        Assert.Equal(SystemCallErrorCode.RateLimited, result.Code);
        Assert.Equal("error: rate limited", result.Lines[0]);
        Assert.Equal("code: ERR_RATE_LIMITED", result.Lines[1]);
    }

    /// <summary>Ensures connect validates usage and strict port syntax.</summary>
    [Theory]
    [InlineData("connect -p", "usage: connect")]
    [InlineData("connect --port 70000 10.0.1.20 guest pw", "invalid port")]
    [InlineData("connect -x 10.0.1.20 guest pw", "usage: connect")]
    [InlineData("connect 10.0.1.20 guest", "usage: connect")]
    public void Execute_Connect_UsageAndPortValidation(string commandLine, string expectedMessage)
    {
        var harness = CreateHarness(includeVfsModule: false, includeConnectModule: true);
        AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, "pw");

        var result = Execute(harness, commandLine, terminalSessionId: "ts-usage");

        Assert.False(result.Ok);
        Assert.Equal(SystemCallErrorCode.InvalidArgs, result.Code);
        Assert.Single(result.Lines);
        Assert.Contains(expectedMessage, result.Lines[0], StringComparison.Ordinal);
    }

    /// <summary>Ensures world port-merge path seeds default SSH/FTP entries before applying overlays.</summary>
    [Fact]
    public void ApplyPorts_SeedsDefaultPorts()
    {
        var blobStore = new BlobStore();
        var baseFileSystem = new BaseFileSystem(blobStore);
        var server = new ServerNodeRuntime("node-1", "node-1", ServerRole.Terminal, baseFileSystem, blobStore);

        var applyPorts = typeof(WorldRuntime).GetMethod("ApplyPorts", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(applyPorts);

        applyPorts!.Invoke(
            null,
            new object?[]
            {
                server,
                new Dictionary<int, PortBlueprint>(),
                new Dictionary<int, PortOverrideBlueprint>(),
            });

        Assert.True(server.Ports.TryGetValue(22, out var sshPort));
        Assert.Equal(PortType.Ssh, sshPort!.PortType);
        Assert.Equal(PortExposure.Public, sshPort.Exposure);

        Assert.True(server.Ports.TryGetValue(21, out var ftpPort));
        Assert.Equal(PortType.Ftp, ftpPort!.PortType);
        Assert.Equal(PortExposure.Public, ftpPort.Exposure);
    }

    /// <summary>Ensures a declared port with the same number replaces the seeded default entry (not merge/error).</summary>
    [Fact]
    public void ApplyPorts_ReplacesSeededDefaultPort22_WhenDeclaredAsFtp()
    {
        var blobStore = new BlobStore();
        var baseFileSystem = new BaseFileSystem(blobStore);
        var server = new ServerNodeRuntime("node-1", "node-1", ServerRole.Terminal, baseFileSystem, blobStore);

        var applyPorts = typeof(WorldRuntime).GetMethod("ApplyPorts", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(applyPorts);

        applyPorts!.Invoke(
            null,
            new object?[]
            {
                server,
                new Dictionary<int, PortBlueprint>
                {
                    [22] = new PortBlueprint
                    {
                        PortType = BlueprintPortType.Ftp,
                        Exposure = BlueprintPortExposure.Public,
                    },
                },
                new Dictionary<int, PortOverrideBlueprint>(),
            });

        Assert.True(server.Ports.TryGetValue(22, out var port22));
        Assert.Equal(PortType.Ftp, port22!.PortType);
        Assert.Equal(PortExposure.Public, port22.Exposure);
        Assert.NotEqual(PortType.Ssh, port22.PortType);
    }

    /// <summary>Ensures connect succeeds with static auth and creates server session entry.</summary>
    [Fact]
    public void Execute_Connect_Succeeds_WithStaticAuth()
    {
        var harness = CreateHarness(includeVfsModule: false, includeConnectModule: true);
        var remote = AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, "pw");

        var result = Execute(harness, "connect 10.0.1.20 guest pw", terminalSessionId: "ts-static");

        Assert.True(result.Ok);
        Assert.Equal(SystemCallErrorCode.None, result.Code);
        Assert.Single(remote.Sessions);
        var session = Assert.Single(remote.Sessions);
        Assert.Equal("guest", session.Value.UserKey);
    }

    /// <summary>Ensures connect success prints /etc/motd when target account can read text MOTD.</summary>
    [Fact]
    public void Execute_Connect_Succeeds_PrintsMotd_WhenReadableTextExists()
    {
        var harness = CreateHarness(includeVfsModule: false, includeConnectModule: true);
        var remote = AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, "pw");
        remote.DiskOverlay.AddDirectory("/etc");
        remote.DiskOverlay.WriteFile("/etc/motd", "Welcome\nAuthorized use only.", fileKind: VfsFileKind.Text);

        var result = Execute(harness, "connect 10.0.1.20 guest pw", terminalSessionId: "ts-motd-text");

        Assert.True(result.Ok);
        Assert.Equal(SystemCallErrorCode.None, result.Code);
        Assert.Equal(new[] { "Welcome", "Authorized use only." }, result.Lines);
    }

    /// <summary>Ensures connect success skips MOTD output when target account lacks read privilege.</summary>
    [Fact]
    public void Execute_Connect_Succeeds_SkipsMotd_WhenReadPrivilegeMissing()
    {
        var harness = CreateHarness(includeVfsModule: false, includeConnectModule: true);
        var remote = AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, "pw");
        remote.Users["guest"].Privilege.Read = false;
        remote.DiskOverlay.AddDirectory("/etc");
        remote.DiskOverlay.WriteFile("/etc/motd", "hidden", fileKind: VfsFileKind.Text);

        var result = Execute(harness, "connect 10.0.1.20 guest pw", terminalSessionId: "ts-motd-noread");

        Assert.True(result.Ok);
        Assert.Empty(result.Lines);
    }

    /// <summary>Ensures connect success skips MOTD output when /etc/motd is missing.</summary>
    [Fact]
    public void Execute_Connect_Succeeds_SkipsMotd_WhenMotdFileIsMissing()
    {
        var harness = CreateHarness(includeVfsModule: false, includeConnectModule: true);
        AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, "pw");

        var result = Execute(harness, "connect 10.0.1.20 guest pw", terminalSessionId: "ts-motd-missing");

        Assert.True(result.Ok);
        Assert.Empty(result.Lines);
    }

    /// <summary>Ensures connect success skips MOTD output when /etc/motd is not text file-kind.</summary>
    [Fact]
    public void Execute_Connect_Succeeds_SkipsMotd_WhenMotdIsNotText()
    {
        var harness = CreateHarness(includeVfsModule: false, includeConnectModule: true);
        var remote = AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, "pw");
        remote.DiskOverlay.AddDirectory("/etc");
        remote.DiskOverlay.WriteFile("/etc/motd", "010203", fileKind: VfsFileKind.Binary);

        var result = Execute(harness, "connect 10.0.1.20 guest pw", terminalSessionId: "ts-motd-nontext");

        Assert.True(result.Ok);
        Assert.Empty(result.Lines);
    }

    /// <summary>Ensures connect success emits privilegeAcquire events for all granted privileges with via='connect'.</summary>
    [Fact]
    public void Execute_Connect_Success_EmitsPrivilegeAcquireEvents()
    {
        var harness = CreateHarness(includeVfsModule: false, includeConnectModule: true);
        var remote = AddRemoteServer(
            harness,
            "node-2",
            "remote",
            "10.0.1.20",
            AuthMode.Static,
            "pw",
            userKey: "rootKey",
            userId: "root");

        var result = Execute(harness, "connect 10.0.1.20 root pw", terminalSessionId: "ts-event-connect");

        Assert.True(result.Ok);
        var events = DrainQueuedGameEvents(harness.World);
        var privilegeEvents = events
            .Where(static gameEvent => string.Equals((string)GetPropertyValue(gameEvent, "EventType"), "privilegeAcquire", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(3, privilegeEvents.Count);

        var payloads = privilegeEvents.Select(static gameEvent => GetPropertyValue(gameEvent, "Payload")).ToList();
        var privilegeSet = payloads
            .Select(static payload => (string)GetPropertyValue(payload, "Privilege"))
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(new[] { "execute", "read", "write" }, privilegeSet);
        Assert.All(payloads, payload => Assert.Equal(remote.NodeId, (string)GetPropertyValue(payload, "NodeId")));
        Assert.All(payloads, payload => Assert.Equal("rootKey", (string)GetPropertyValue(payload, "UserKey")));
        Assert.All(payloads, payload => Assert.Equal("connect", (string?)GetPropertyValue(payload, "Via")));
    }

    /// <summary>Ensures connect failure does not emit privilegeAcquire events.</summary>
    [Fact]
    public void Execute_Connect_Failure_DoesNotEmitPrivilegeAcquireEvents()
    {
        var harness = CreateHarness(includeVfsModule: false, includeConnectModule: true);
        AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, "pw");

        var result = Execute(harness, "connect 10.0.1.20 guest wrong", terminalSessionId: "ts-event-connect-fail");

        Assert.False(result.Ok);
        var events = DrainQueuedGameEvents(harness.World);
        Assert.Empty(events);
    }

    /// <summary>Ensures connect resolves login target by userId while preserving internal session userKey.</summary>
    [Fact]
    public void Execute_Connect_UsesUserIdForLogin_AndStoresResolvedUserKey()
    {
        var harness = CreateHarness(includeVfsModule: false, includeConnectModule: true);
        var remote = AddRemoteServer(
            harness,
            "node-2",
            "remote",
            "10.0.1.20",
            AuthMode.Static,
            "pw",
            userKey: "internalRoot",
            userId: "root");

        var result = Execute(harness, "connect 10.0.1.20 root pw", terminalSessionId: "ts-userid-login");

        Assert.True(result.Ok);
        Assert.Equal(SystemCallErrorCode.None, result.Code);
        Assert.Single(remote.Sessions);
        var session = Assert.Single(remote.Sessions);
        Assert.Equal("internalRoot", session.Value.UserKey);
    }

    /// <summary>Ensures connect does not accept userKey text when it differs from userId.</summary>
    [Fact]
    public void Execute_Connect_Fails_WhenInputMatchesUserKeyButNotUserId()
    {
        var harness = CreateHarness(includeVfsModule: false, includeConnectModule: true);
        AddRemoteServer(
            harness,
            "node-2",
            "remote",
            "10.0.1.20",
            AuthMode.Static,
            "pw",
            userKey: "internalRoot",
            userId: "root");

        var result = Execute(harness, "connect 10.0.1.20 internalRoot pw", terminalSessionId: "ts-userkey-login");

        Assert.False(result.Ok);
        Assert.Equal(SystemCallErrorCode.NotFound, result.Code);
        Assert.Single(result.Lines);
        Assert.Contains("user not found: internalRoot", result.Lines[0], StringComparison.Ordinal);
    }

    /// <summary>Ensures connect succeeds when target account auth mode is none.</summary>
    [Fact]
    public void Execute_Connect_Succeeds_WithNoneAuth()
    {
        var harness = CreateHarness(includeVfsModule: false, includeConnectModule: true);
        var remote = AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.None, "ignored");

        var result = Execute(harness, "connect 10.0.1.20 guest anything", terminalSessionId: "ts-none");

        Assert.True(result.Ok);
        Assert.Equal(SystemCallErrorCode.None, result.Code);
        Assert.Single(remote.Sessions);
    }

    /// <summary>Ensures connect fails on wrong static password.</summary>
    [Fact]
    public void Execute_Connect_Fails_OnWrongPassword()
    {
        var harness = CreateHarness(includeVfsModule: false, includeConnectModule: true);
        AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, "pw");

        var result = Execute(harness, "connect 10.0.1.20 guest wrong", terminalSessionId: "ts-authfail");

        Assert.False(result.Ok);
        Assert.Equal(SystemCallErrorCode.PermissionDenied, result.Code);
        Assert.Single(result.Lines);
        Assert.Contains("Permission denied, please try again.", result.Lines[0], StringComparison.Ordinal);
    }

    /// <summary>Ensures connectionRateLimiter blocks auth attempts after threshold even with a valid password.</summary>
    [Fact]
    public void Execute_Connect_ConnectionRateLimiter_BlocksAfterThresholdEvenWithCorrectPassword()
    {
        var harness = CreateHarness(includeVfsModule: false, includeConnectModule: true);
        var remote = AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, "pw");
        AddConnectionRateLimiterDaemon(
            remote,
            monitorMs: 60000,
            threshold: 1,
            blockMs: 60000,
            rateLimit: 100,
            recoveryMs: 60000);

        var first = Execute(harness, "connect 10.0.1.20 guest wrong", terminalSessionId: "ts-rate-threshold");
        Assert.False(first.Ok);
        Assert.Equal(SystemCallErrorCode.PermissionDenied, first.Code);

        var blocked = Execute(harness, "connect 10.0.1.20 guest pw", terminalSessionId: "ts-rate-threshold");
        Assert.False(blocked.Ok);
        Assert.Equal(SystemCallErrorCode.PermissionDenied, blocked.Code);
        Assert.Single(blocked.Lines);
        Assert.Contains(
            "connectionRateLimiter daemon blocked this connection attempt.",
            blocked.Lines[0],
            StringComparison.Ordinal);
        Assert.Empty(remote.Sessions);
    }

    /// <summary>Ensures connectionRateLimiter enters overload mode on rate-limit overflow while allowing auth path to continue.</summary>
    [Fact]
    public void Execute_Connect_ConnectionRateLimiter_TriggersOverloadAndPassesAttempt()
    {
        var harness = CreateHarness(includeVfsModule: false, includeConnectModule: true);
        var remote = AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, "pw");
        AddConnectionRateLimiterDaemon(
            remote,
            monitorMs: 60000,
            threshold: 100,
            blockMs: 60000,
            rateLimit: 1,
            recoveryMs: 60000);

        var overloadTriggered = false;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var wrong = Execute(harness, "connect 10.0.1.20 guest wrong", terminalSessionId: "ts-rate-overload");
            Assert.False(wrong.Ok);
            Assert.Equal(SystemCallErrorCode.PermissionDenied, wrong.Code);
            if (TryGetConnectionRateLimiterOverloadedUntilMs(harness.World, remote.NodeId, out var overloadedUntilMs) &&
                overloadedUntilMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            {
                overloadTriggered = true;
                break;
            }
        }

        Assert.True(overloadTriggered);

        var success = Execute(harness, "connect 10.0.1.20 guest pw", terminalSessionId: "ts-rate-overload");
        Assert.True(success.Ok);
        Assert.Equal(SystemCallErrorCode.None, success.Code);
        Assert.Single(remote.Sessions);
    }

    /// <summary>Ensures connect fails when target host or user does not exist.</summary>
    [Fact]
    public void Execute_Connect_Fails_OnMissingTargetOrUser()
    {
        var harness = CreateHarness(includeVfsModule: false, includeConnectModule: true);
        AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, "pw");

        var missingHost = Execute(harness, "connect 10.0.1.99 guest pw", terminalSessionId: "ts-missing-host");
        Assert.False(missingHost.Ok);
        Assert.Equal(SystemCallErrorCode.NotFound, missingHost.Code);
        Assert.Contains("host not found", missingHost.Lines[0], StringComparison.Ordinal);

        var missingUser = Execute(harness, "connect 10.0.1.20 admin pw", terminalSessionId: "ts-missing-user");
        Assert.False(missingUser.Ok);
        Assert.Equal(SystemCallErrorCode.NotFound, missingUser.Code);
        Assert.Contains("user not found", missingUser.Lines[0], StringComparison.Ordinal);
    }

    /// <summary>Ensures connect fails when target server is offline.</summary>
    [Fact]
    public void Execute_Connect_Fails_WhenTargetOffline()
    {
        var harness = CreateHarness(includeVfsModule: false, includeConnectModule: true);
        var remote = AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, "pw");
        remote.SetOffline(ServerReason.Disabled);

        var result = Execute(harness, "connect 10.0.1.20 guest pw", terminalSessionId: "ts-offline");

        Assert.False(result.Ok);
        Assert.Equal(SystemCallErrorCode.NotFound, result.Code);
        Assert.Contains("server offline", result.Lines[0], StringComparison.Ordinal);
    }

    /// <summary>Ensures connect fails when strict port validation does not find target port.</summary>
    [Fact]
    public void Execute_Connect_Fails_WhenPortIsNotDefined()
    {
        var harness = CreateHarness(includeVfsModule: false, includeConnectModule: true);
        var remote = AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, "pw");
        remote.Ports.Clear();

        var result = Execute(harness, "connect 10.0.1.20 guest pw", terminalSessionId: "ts-nonport");

        Assert.False(result.Ok);
        Assert.Equal(SystemCallErrorCode.NotFound, result.Code);
        Assert.Contains("port not available", result.Lines[0], StringComparison.Ordinal);
    }

    /// <summary>Ensures connect fails when target port protocol is not ssh.</summary>
    [Fact]
    public void Execute_Connect_Fails_WhenPortTypeIsNotSsh()
    {
        var harness = CreateHarness(includeVfsModule: false, includeConnectModule: true);
        AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, "pw", portType: PortType.Http);

        var result = Execute(harness, "connect 10.0.1.20 guest pw", terminalSessionId: "ts-porttype");

        Assert.False(result.Ok);
        Assert.Equal(SystemCallErrorCode.InvalidArgs, result.Code);
        Assert.Contains("is not ssh", result.Lines[0], StringComparison.Ordinal);
    }

    /// <summary>Ensures SSH connect fails when port 22 is overwritten as FTP.</summary>
    [Fact]
    public void Execute_Connect_Fails_WhenPort22IsOverwrittenAsFtp()
    {
        var harness = CreateHarness(includeVfsModule: false, includeConnectModule: true);
        AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, "pw", portType: PortType.Ftp);

        var result = Execute(harness, "connect 10.0.1.20 guest pw", terminalSessionId: "ts-port22-ftp");

        Assert.False(result.Ok);
        Assert.Equal(SystemCallErrorCode.InvalidArgs, result.Code);
        Assert.Contains("is not ssh", result.Lines[0], StringComparison.Ordinal);
    }

    /// <summary>Ensures unassigned ports (portType none) are treated as unavailable regardless of exposure.</summary>
    [Fact]
    public void Execute_Connect_Fails_WhenPortTypeIsNone()
    {
        var harness = CreateHarness(includeVfsModule: false, includeConnectModule: true);
        AddRemoteServer(
            harness,
            "node-2",
            "remote",
            "10.0.1.20",
            AuthMode.Static,
            "pw",
            portType: PortType.None,
            exposure: PortExposure.Localhost);

        var result = Execute(harness, "connect 10.0.1.20 guest pw", terminalSessionId: "ts-port-none");

        Assert.False(result.Ok);
        Assert.Equal(SystemCallErrorCode.NotFound, result.Code);
        Assert.Contains("port not available", result.Lines[0], StringComparison.Ordinal);
    }

    /// <summary>Ensures connect enforces lan/localhost exposure rules.</summary>
    [Theory]
    [InlineData(PortExposure.Lan)]
    [InlineData(PortExposure.Localhost)]
    public void Execute_Connect_Fails_OnExposureViolation(PortExposure exposure)
    {
        var harness = CreateHarness(includeVfsModule: false, includeConnectModule: true);
        var netId = exposure == PortExposure.Lan ? "alpha" : "internet";
        AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, "pw", exposure: exposure, netId: netId);

        var result = Execute(harness, "connect 10.0.1.20 guest pw", terminalSessionId: "ts-exposure");

        Assert.False(result.Ok);
        Assert.Equal(SystemCallErrorCode.PermissionDenied, result.Code);
        Assert.Contains("port exposure denied", result.Lines[0], StringComparison.Ordinal);
    }

    /// <summary>Ensures unsupported auth modes return failure.</summary>
    [Theory]
    [InlineData(AuthMode.Other)]
    public void Execute_Connect_Fails_OnUnsupportedAuthMode(AuthMode authMode)
    {
        var harness = CreateHarness(includeVfsModule: false, includeConnectModule: true);
        AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", authMode, "pw");

        var result = Execute(harness, "connect 10.0.1.20 guest pw", terminalSessionId: "ts-authmode");

        Assert.False(result.Ok);
        Assert.Equal(SystemCallErrorCode.PermissionDenied, result.Code);
        Assert.Contains("not supported", result.Lines[0], StringComparison.Ordinal);
    }

    /// <summary>Ensures OTP-auth accounts accept a valid TOTP generated from daemon settings.</summary>
    [Fact]
    public void Execute_Connect_Succeeds_OnOtpAuth_WhenTokenMatchesDaemonWindow()
    {
        const string otpPairId = "X2BW6QOI53QNUHBXCBYN6XADEQFPR5FJ";
        const long stepMs = 1000;
        const int allowedDriftSteps = 1;
        var harness = CreateHarness(includeVfsModule: false, includeConnectModule: true);
        var remote = AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Otp, string.Empty);
        remote.Daemons[DaemonType.Otp] = new DaemonStruct
        {
            DaemonType = DaemonType.Otp,
        };
        remote.Daemons[DaemonType.Otp].DaemonArgs["userKey"] = "guest";
        remote.Daemons[DaemonType.Otp].DaemonArgs["stepMs"] = stepMs;
        remote.Daemons[DaemonType.Otp].DaemonArgs["allowedDriftSteps"] = allowedDriftSteps;
        remote.Daemons[DaemonType.Otp].DaemonArgs["otpPairId"] = otpPairId;

        var otpCode = GenerateTotpForTest(otpPairId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), stepMs, digits: 6);
        var result = Execute(harness, $"connect 10.0.1.20 guest {otpCode}", terminalSessionId: "ts-authmode-otp-ok");

        Assert.True(result.Ok);
        Assert.Equal(SystemCallErrorCode.None, result.Code);
    }

    /// <summary>Ensures OTP-auth accounts reject invalid TOTP values.</summary>
    [Fact]
    public void Execute_Connect_Fails_OnOtpAuth_WhenTokenMismatches()
    {
        const string otpPairId = "X2BW6QOI53QNUHBXCBYN6XADEQFPR5FJ";
        const long stepMs = 1000;
        const int allowedDriftSteps = 1;
        var harness = CreateHarness(includeVfsModule: false, includeConnectModule: true);
        var remote = AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Otp, string.Empty);
        remote.Daemons[DaemonType.Otp] = new DaemonStruct
        {
            DaemonType = DaemonType.Otp,
        };
        remote.Daemons[DaemonType.Otp].DaemonArgs["userKey"] = "guest";
        remote.Daemons[DaemonType.Otp].DaemonArgs["stepMs"] = stepMs;
        remote.Daemons[DaemonType.Otp].DaemonArgs["allowedDriftSteps"] = allowedDriftSteps;
        remote.Daemons[DaemonType.Otp].DaemonArgs["otpPairId"] = otpPairId;

        var otpCode = GenerateTotpForTest(otpPairId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), stepMs, digits: 6);
        var invalidCode = otpCode[..^1] + (otpCode[^1] == '9' ? "0" : ((char)(otpCode[^1] + 1)).ToString());
        var result = Execute(harness, $"connect 10.0.1.20 guest {invalidCode}", terminalSessionId: "ts-authmode-otp-fail");

        Assert.False(result.Ok);
        Assert.Equal(SystemCallErrorCode.PermissionDenied, result.Code);
        Assert.Contains("Permission denied", result.Lines[0], StringComparison.Ordinal);
    }

    /// <summary>Ensures known prints a hostname/IP table using internet-known nodes only.</summary>
    [Fact]
    public void Execute_Known_PrintsPublicKnownTable()
    {
        var harness = CreateHarness(includeVfsModule: false, includeConnectModule: true);
        var internetRemote = AddRemoteServer(harness, "node-2", "remote-inet", "10.0.1.20", AuthMode.Static, "pw");
        AddRemoteServer(harness, "node-3", "remote-lan", "10.1.0.30", AuthMode.Static, "pw", netId: "alpha");

        var knownNodesByNet = GetAutoPropertyBackingField<Dictionary<string, HashSet<string>>>(harness.World, "KnownNodesByNet");
        knownNodesByNet["internet"] = new HashSet<string>(StringComparer.Ordinal)
        {
            harness.Server.NodeId,
            internetRemote.NodeId,
            "node-3",
        };

        var result = Execute(harness, "known", terminalSessionId: "ts-known");

        Assert.True(result.Ok);
        Assert.Equal(SystemCallErrorCode.None, result.Code);
        Assert.NotEmpty(result.Lines);
        Assert.Contains("HOSTNAME", result.Lines[0], StringComparison.Ordinal);
        Assert.Contains("IP", result.Lines[0], StringComparison.Ordinal);
        Assert.Contains(result.Lines, static line => line.Contains("node-1", StringComparison.Ordinal) &&
                                                     line.Contains("10.0.0.10", StringComparison.Ordinal));
        Assert.Contains(result.Lines, static line => line.Contains("remote-inet", StringComparison.Ordinal) &&
                                                     line.Contains("10.0.1.20", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Lines, static line => line.Contains("node-3", StringComparison.Ordinal));
    }

    /// <summary>Ensures scan prints per-interface blocks with hostname-first neighbor labels.</summary>
    [Fact]
    public void Execute_Scan_PrintsInterfaceBlocksWithHostnameFirstLabels()
    {
        var harness = CreateHarness(includeVfsModule: false, includeConnectModule: true);
        harness.Server.SetInterfaces(new[]
        {
            new InterfaceRuntime
            {
                NetId = "internet",
                Ip = "10.0.0.10",
            },
            new InterfaceRuntime
            {
                NetId = "alpha",
                Ip = "10.1.0.10",
            },
            new InterfaceRuntime
            {
                NetId = "beta",
                Ip = "10.2.0.10",
            },
        });

        var remoteA = AddRemoteServer(harness, "node-2", "remote-a", "10.1.0.20", AuthMode.Static, "pw", netId: "alpha");
        AddRemoteServer(harness, "node-3", "remote-b", "10.2.0.30", AuthMode.Static, "pw", netId: "beta");
        harness.Server.LanNeighbors.Add("node-3");
        harness.Server.LanNeighbors.Add("node-2");

        SetAutoPropertyBackingField(harness.World, "PlayerWorkstationServer", remoteA);

        var result = Execute(harness, "scan", terminalSessionId: "ts-scan");

        Assert.True(result.Ok);
        Assert.Equal(SystemCallErrorCode.None, result.Code);
        Assert.Equal(4, result.Lines.Count);
        Assert.Equal("[interface alpha 10.1.0.10] -", result.Lines[0]);
        Assert.Equal("    remote-a", result.Lines[1]);
        Assert.Equal("[interface beta 10.2.0.10] -", result.Lines[2]);
        Assert.Equal("    remote-b", result.Lines[3]);
    }

    /// <summary>Ensures scan falls back to IP when a neighbor hostname is missing.</summary>
    [Fact]
    public void Execute_Scan_FallsBackToIpWhenNeighborHostnameMissing()
    {
        var harness = CreateHarness(includeVfsModule: false, includeConnectModule: true);
        harness.Server.SetInterfaces(new[]
        {
            new InterfaceRuntime
            {
                NetId = "internet",
                Ip = "10.0.0.10",
            },
            new InterfaceRuntime
            {
                NetId = "alpha",
                Ip = "10.1.0.10",
            },
        });

        var remoteA = AddRemoteServer(harness, "node-2", "remote-a", "10.1.0.20", AuthMode.Static, "pw", netId: "alpha");
        var remoteNoName = AddRemoteServer(harness, "node-3", string.Empty, "10.1.0.30", AuthMode.Static, "pw", netId: "alpha");
        harness.Server.LanNeighbors.Add(remoteNoName.NodeId);
        harness.Server.LanNeighbors.Add(remoteA.NodeId);
        SetAutoPropertyBackingField(harness.World, "PlayerWorkstationServer", remoteA);

        var result = Execute(harness, "scan", terminalSessionId: "ts-scan-fallback");

        Assert.True(result.Ok);
        Assert.Equal(SystemCallErrorCode.None, result.Code);
        Assert.Equal(2, result.Lines.Count);
        Assert.Equal("[interface alpha 10.1.0.10] -", result.Lines[0]);
        Assert.Equal("    10.1.0.30, remote-a", result.Lines[1]);
    }

    /// <summary>Ensures scan accepts a netId filter and prints only the requested interface block.</summary>
    [Fact]
    public void Execute_Scan_WithNetId_PrintsOnlyRequestedInterfaceBlock()
    {
        var harness = CreateHarness(includeVfsModule: false, includeConnectModule: true);
        harness.Server.SetInterfaces(new[]
        {
            new InterfaceRuntime
            {
                NetId = "internet",
                Ip = "10.0.0.10",
            },
            new InterfaceRuntime
            {
                NetId = "alpha",
                Ip = "10.1.0.10",
            },
            new InterfaceRuntime
            {
                NetId = "beta",
                Ip = "10.2.0.10",
            },
        });

        var remoteA = AddRemoteServer(harness, "node-2", "remote-a", "10.1.0.20", AuthMode.Static, "pw", netId: "alpha");
        AddRemoteServer(harness, "node-3", "remote-b", "10.2.0.30", AuthMode.Static, "pw", netId: "beta");
        harness.Server.LanNeighbors.Add("node-2");
        harness.Server.LanNeighbors.Add("node-3");
        SetAutoPropertyBackingField(harness.World, "PlayerWorkstationServer", remoteA);

        var result = Execute(harness, "scan alpha", terminalSessionId: "ts-scan-filter");

        Assert.True(result.Ok);
        Assert.Equal(SystemCallErrorCode.None, result.Code);
        Assert.Equal(2, result.Lines.Count);
        Assert.Equal("[interface alpha 10.1.0.10] -", result.Lines[0]);
        Assert.Equal("    remote-a", result.Lines[1]);
    }

    /// <summary>Ensures scan returns NotFound when the requested interface id does not exist.</summary>
    [Fact]
    public void Execute_Scan_WithUnknownNetId_ReturnsNotFound()
    {
        var harness = CreateHarness(includeVfsModule: false, includeConnectModule: true);
        harness.Server.SetInterfaces(new[]
        {
            new InterfaceRuntime
            {
                NetId = "internet",
                Ip = "10.0.0.10",
            },
            new InterfaceRuntime
            {
                NetId = "alpha",
                Ip = "10.1.0.10",
            },
        });

        var workstation = AddRemoteServer(harness, "node-work", "workstation", "10.0.9.9", AuthMode.None, "ignored");
        SetAutoPropertyBackingField(harness.World, "PlayerWorkstationServer", workstation);

        var result = Execute(harness, "scan missing", terminalSessionId: "ts-scan-missing-netid");

        Assert.False(result.Ok);
        Assert.Equal(SystemCallErrorCode.NotFound, result.Code);
        Assert.Single(result.Lines);
        Assert.Contains("scan: interface not found: missing", result.Lines[0], StringComparison.Ordinal);
    }

    /// <summary>Ensures scan prints no-neighbor message for player workstation context.</summary>
    [Fact]
    public void Execute_Scan_PlayerWorkstation_PrintsNoNeighborMessage()
    {
        var harness = CreateHarness(includeVfsModule: false, includeConnectModule: true);

        var result = Execute(harness, "scan", terminalSessionId: "ts-scan-workstation");

        Assert.True(result.Ok);
        Assert.Equal(SystemCallErrorCode.None, result.Code);
        Assert.Single(result.Lines);
        Assert.Contains("No adjacent servers found", result.Lines[0], StringComparison.Ordinal);
    }

    /// <summary>Ensures scan prints no-neighbor message when server has no subnet interfaces.</summary>
    [Fact]
    public void Execute_Scan_NoSubnet_PrintsNoNeighborMessage()
    {
        var harness = CreateHarness(includeVfsModule: false, includeConnectModule: true);
        var workstation = AddRemoteServer(harness, "node-work", "workstation", "10.0.9.9", AuthMode.None, "ignored");
        SetAutoPropertyBackingField(harness.World, "PlayerWorkstationServer", workstation);

        var result = Execute(harness, "scan", terminalSessionId: "ts-scan-nosubnet");

        Assert.True(result.Ok);
        Assert.Equal(SystemCallErrorCode.None, result.Code);
        Assert.Single(result.Lines);
        Assert.Contains("No adjacent servers found", result.Lines[0], StringComparison.Ordinal);
    }

    /// <summary>Ensures scan requires execute privilege and returns a linux-like permission denied message.</summary>
    [Fact]
    public void Execute_Scan_Fails_WhenExecutePrivilegeIsMissing()
    {
        var harness = CreateHarness(
            includeVfsModule: false,
            includeConnectModule: true,
            privilege: new PrivilegeConfig
            {
                Read = true,
                Write = true,
                Execute = false,
            });

        harness.Server.SetInterfaces(new[]
        {
            new InterfaceRuntime
            {
                NetId = "internet",
                Ip = "10.0.0.10",
            },
            new InterfaceRuntime
            {
                NetId = "alpha",
                Ip = "10.1.0.10",
            },
        });

        var workstation = AddRemoteServer(harness, "node-work", "workstation", "10.0.9.9", AuthMode.None, "ignored");
        SetAutoPropertyBackingField(harness.World, "PlayerWorkstationServer", workstation);

        var result = Execute(harness, "scan", terminalSessionId: "ts-scan-noexec");

        Assert.False(result.Ok);
        Assert.Equal(SystemCallErrorCode.PermissionDenied, result.Code);
        Assert.Single(result.Lines);
        Assert.Contains("scan: permission denied", result.Lines[0], StringComparison.Ordinal);
    }

    /// <summary>Ensures disconnect restores prior context and removes created server session.</summary>
    [Fact]
    public void Execute_Disconnect_Succeeds_AndRemovesSession()
    {
        var harness = CreateHarness(includeVfsModule: false, includeConnectModule: true, cwd: "/work");
        harness.BaseFileSystem.AddDirectory("/work");
        var remote = AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, "pw");

        var connect = Execute(harness, "connect 10.0.1.20 guest pw", terminalSessionId: "ts-disconnect");
        Assert.True(connect.Ok);
        Assert.Single(remote.Sessions);

        harness.Server.Users["guest"].UserId = "guest-renamed";

        var disconnect = Execute(
            harness,
            "disconnect",
            nodeId: remote.NodeId,
            userId: "guest",
            cwd: "/",
            terminalSessionId: "ts-disconnect");

        Assert.True(disconnect.Ok);
        Assert.Equal("/work", disconnect.NextCwd);
        Assert.Empty(remote.Sessions);

        var transition = disconnect.Data;
        Assert.NotNull(transition);
        Assert.Equal("guest-renamed", (string?)GetPropertyValue(transition!, "NextUserId"));
        Assert.Equal("guest-renamed", (string?)GetPropertyValue(transition!, "NextPromptUser"));
    }

    /// <summary>Ensures disconnect fails when session stack has no active connection.</summary>
    [Fact]
    public void Execute_Disconnect_Fails_WhenNotConnected()
    {
        var harness = CreateHarness(includeVfsModule: false, includeConnectModule: true);

        var result = Execute(harness, "disconnect", terminalSessionId: "ts-empty");

        Assert.False(result.Ok);
        Assert.Equal(SystemCallErrorCode.InvalidArgs, result.Code);
        Assert.Contains("not connected", result.Lines[0], StringComparison.Ordinal);
    }

    /// <summary>Ensures prototype save validates slot argument shape and range.</summary>
    [Theory]
    [InlineData("save")]
    [InlineData("save x")]
    [InlineData("save -1")]
    [InlineData("save 10")]
    public void Execute_PrototypeSave_ValidatesSlotArgument(string commandLine)
    {
        var harness = CreateHarness(includeVfsModule: false, includePrototypeSaveLoadModule: true);

        var result = Execute(harness, commandLine, terminalSessionId: "ts-proto-save-args");

        Assert.False(result.Ok);
        Assert.Equal(SystemCallErrorCode.InvalidArgs, result.Code);
    }

    /// <summary>Ensures prototype load validates slot argument shape and range.</summary>
    [Theory]
    [InlineData("load")]
    [InlineData("load x")]
    [InlineData("load -1")]
    [InlineData("load 10")]
    public void Execute_PrototypeLoad_ValidatesSlotArgument(string commandLine)
    {
        var harness = CreateHarness(includeVfsModule: false, includePrototypeSaveLoadModule: true);

        var result = Execute(harness, commandLine, terminalSessionId: "ts-proto-load-args");

        Assert.False(result.Ok);
        Assert.Equal(SystemCallErrorCode.InvalidArgs, result.Code);
    }

    /// <summary>Ensures prototype save fails with clear error when HMAC key is not configured.</summary>
    [Fact]
    public void Execute_PrototypeSave_Fails_WhenSaveHmacKeyIsMissing()
    {
        var harness = CreateHarness(includeVfsModule: false, includePrototypeSaveLoadModule: true);
        var resolverRoot = CreatePrototypeSlotPathResolverRoot();
        using var resolverScope = OverridePrototypeSlotPathResolver(resolverRoot);

        try
        {
            var result = Execute(harness, "save 9", terminalSessionId: "ts-proto-save-missing-key");

            Assert.False(result.Ok);
            Assert.Equal(SystemCallErrorCode.InvalidArgs, result.Code);
            Assert.Contains("SaveHmacKeyBase64", result.Lines[0], StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectoryIfExists(resolverRoot);
        }
    }

    /// <summary>Ensures prototype save maps slot index to the expected user:// slot path.</summary>
    [Fact]
    public void Execute_PrototypeSave_BuildsExpectedSlotPath()
    {
        var handlerType = RequireRuntimeType("Uplink2.Runtime.Syscalls.PrototypeSaveCommandHandler");
        var method = handlerType.GetMethod(
            "BuildSlotSavePath",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(method);

        var slotPath = method!.Invoke(null, new object?[] { 9 }) as string;
        Assert.Equal("user://saves/slot9.uls1", slotPath);
    }

    /// <summary>Ensures save/load engine failures map into terminal-friendly system-call failures.</summary>
    [Fact]
    public void Execute_PrototypeSave_ConvertsSaveLoadFailure_ToSystemCallFailure()
    {
        var handlerType = RequireRuntimeType("Uplink2.Runtime.Syscalls.PrototypeSaveCommandHandler");
        var method = handlerType.GetMethod(
            "ConvertSaveLoadFailure",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(method);

        var failure = new SaveLoadResult
        {
            Ok = false,
            Code = SaveLoadErrorCode.InvalidArgs,
            Message = "SaveHmacKeyBase64 must be configured before save/load.",
            SavePath = "user://saves/slot0.uls1",
        };

        var systemCallResult = method!.Invoke(null, new object?[] { failure }) as SystemCallResult;
        Assert.NotNull(systemCallResult);
        Assert.False(systemCallResult!.Ok);
        Assert.Equal(SystemCallErrorCode.InvalidArgs, systemCallResult.Code);
        Assert.Contains("SaveHmacKeyBase64", systemCallResult.Lines[0], StringComparison.Ordinal);
    }

    /// <summary>Ensures prototype load reports not-found when slot file is missing.</summary>
    [Fact]
    public void Execute_PrototypeLoad_Fails_WhenSlotFileIsMissing()
    {
        var harness = CreateHarness(includeVfsModule: false, includePrototypeSaveLoadModule: true);
        harness.World.SaveHmacKeyBase64 = "cHJvdG90eXBlLWxvYWQta2V5";
        var resolverRoot = CreatePrototypeSlotPathResolverRoot();
        using var resolverScope = OverridePrototypeSlotPathResolver(resolverRoot);

        const int slot = 8;
        var slotPath = $"user://saves/slot{slot}.uls1";
        var absoluteSlotPath = ResolvePrototypeSlotPathForTests(resolverRoot, slot);
        if (File.Exists(absoluteSlotPath))
        {
            File.Delete(absoluteSlotPath);
        }

        try
        {
            var result = Execute(harness, $"load {slot}", terminalSessionId: "ts-proto-load-missing-slot");

            Assert.False(result.Ok);
            Assert.Equal(SystemCallErrorCode.NotFound, result.Code);
            Assert.Contains(slotPath, result.Lines[0], StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectoryIfExists(resolverRoot);
        }
    }

    /// <summary>Ensures system-call module toggle disables prototype save/load commands.</summary>
    [Fact]
    public void InitializeSystemCalls_ExcludesPrototypeSaveLoad_WhenToggleDisabled()
    {
        var harness = CreateHarness(includeVfsModule: false, includePrototypeSaveLoadModule: true);
        harness.World.EnablePrototypeSaveLoadSystemCalls = false;

        var initializeSystemCalls = typeof(WorldRuntime).GetMethod(
            "InitializeSystemCalls",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(initializeSystemCalls);
        initializeSystemCalls!.Invoke(harness.World, Array.Empty<object?>());

        var result = harness.World.ExecuteSystemCall(new SystemCallRequest
        {
            NodeId = harness.Server.NodeId,
            UserId = harness.UserId,
            Cwd = "/",
            CommandLine = "save 0",
            TerminalSessionId = "ts-proto-toggle-off",
        });

        Assert.False(result.Ok);
        Assert.Equal(SystemCallErrorCode.UnknownCommand, result.Code);
        Assert.Contains("unknown command: save", result.Lines[0], StringComparison.Ordinal);
    }

    /// <summary>Ensures ftp validates usage and strict port syntax.</summary>
    [Theory]
    [InlineData("ftp", "usage: ftp")]
    [InlineData("ftp -p", "usage: ftp")]
    [InlineData("ftp --port 70000 get /x", "invalid port")]
    [InlineData("ftp bad /x", "usage: ftp")]
    public void Execute_Ftp_UsageAndPortValidation(string commandLine, string expectedMessage)
    {
        var harness = CreateHarness(includeVfsModule: false, includeConnectModule: true);

        var result = Execute(harness, commandLine, terminalSessionId: "ts-ftp-usage");

        Assert.False(result.Ok);
        Assert.Equal(SystemCallErrorCode.InvalidArgs, result.Code);
        Assert.Single(result.Lines);
        Assert.Contains(expectedMessage, result.Lines[0], StringComparison.Ordinal);
    }

    /// <summary>Ensures ftp requires an active ssh connection stack frame.</summary>
    [Fact]
    public void Execute_Ftp_Fails_WhenNotConnected()
    {
        var harness = CreateHarness(includeVfsModule: false, includeConnectModule: true);

        var result = Execute(harness, "ftp get /etc/passwd", terminalSessionId: "ts-ftp-not-connected");

        Assert.False(result.Ok);
        Assert.Equal(SystemCallErrorCode.InvalidArgs, result.Code);
        Assert.Single(result.Lines);
        Assert.Contains("active ssh connection", result.Lines[0], StringComparison.Ordinal);
    }

    /// <summary>Ensures ftp get downloads into workstation cwd, preserves file metadata, and emits fileAcquire.</summary>
    [Fact]
    public void Execute_FtpGet_Succeeds_PreservesMetadata_AndEmitsFileAcquire()
    {
        var harness = CreateHarness(includeVfsModule: false, includeConnectModule: true, cwd: "/work");
        harness.BaseFileSystem.AddDirectory("/work");
        harness.BaseFileSystem.AddDirectory("/work/downloads");

        var remote = AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, "pw");
        AddFtpPort(remote);
        remote.DiskOverlay.AddDirectory("/drop");
        remote.DiskOverlay.WriteFile(
            "/drop/tool.bin",
            "exec:miniscript",
            fileKind: VfsFileKind.ExecutableHardcode,
            size: 4096);

        var connect = Execute(harness, "connect 10.0.1.20 guest pw", terminalSessionId: "ts-ftp-get");
        Assert.True(connect.Ok);

        var result = Execute(
            harness,
            "ftp get /drop/tool.bin downloads",
            nodeId: remote.NodeId,
            userId: "guest",
            cwd: "/",
            terminalSessionId: "ts-ftp-get");

        Assert.True(result.Ok);
        Assert.Single(result.Lines);
        Assert.Contains("ftp get:", result.Lines[0], StringComparison.Ordinal);

        Assert.True(harness.Server.DiskOverlay.TryResolveEntry("/work/downloads/tool.bin", out var downloaded));
        Assert.Equal(VfsEntryKind.File, downloaded.EntryKind);
        Assert.Equal(VfsFileKind.ExecutableHardcode, downloaded.FileKind);
        Assert.Equal(4096, downloaded.Size);

        var events = DrainQueuedGameEvents(harness.World);
        var fileAcquireEvents = events
            .Where(static gameEvent => string.Equals((string)GetPropertyValue(gameEvent, "EventType"), "fileAcquire", StringComparison.Ordinal))
            .ToList();
        var fileAcquire = Assert.Single(fileAcquireEvents);
        var payload = GetPropertyValue(fileAcquire, "Payload");
        var expectedUserKey = harness.Server.Users
            .Single(pair => string.Equals(pair.Value.UserId, harness.UserId, StringComparison.Ordinal))
            .Key;
        Assert.Equal(remote.NodeId, (string?)GetPropertyValue(payload!, "FromNodeId"));
        Assert.Equal(expectedUserKey, (string?)GetPropertyValue(payload!, "UserKey"));
        Assert.Equal("ftp", (string?)GetPropertyValue(payload!, "TransferMethod"));
        Assert.Equal("/drop/tool.bin", (string?)GetPropertyValue(payload!, "RemotePath"));
        Assert.Equal("/work/downloads/tool.bin", (string?)GetPropertyValue(payload!, "LocalPath"));
    }

    /// <summary>Ensures ftp put uploads from workstation cwd to remote cwd and does not emit fileAcquire.</summary>
    [Fact]
    public void Execute_FtpPut_Succeeds_PreservesMetadata_AndDoesNotEmitFileAcquire()
    {
        var harness = CreateHarness(includeVfsModule: false, includeConnectModule: true, cwd: "/work");
        harness.BaseFileSystem.AddDirectory("/work");
        harness.BaseFileSystem.AddDirectory("/work/local");
        harness.Server.DiskOverlay.WriteFile(
            "/work/local/script.bin",
            "exec:noop",
            fileKind: VfsFileKind.ExecutableHardcode,
            size: 2048);

        var remote = AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, "pw");
        AddFtpPort(remote);
        remote.DiskOverlay.AddDirectory("/incoming");

        var connect = Execute(harness, "connect 10.0.1.20 guest pw", terminalSessionId: "ts-ftp-put");
        Assert.True(connect.Ok);

        var result = Execute(
            harness,
            "ftp put local/script.bin incoming",
            nodeId: remote.NodeId,
            userId: "guest",
            cwd: "/",
            terminalSessionId: "ts-ftp-put");

        Assert.True(result.Ok);
        Assert.Single(result.Lines);
        Assert.Contains("ftp put:", result.Lines[0], StringComparison.Ordinal);

        Assert.True(remote.DiskOverlay.TryResolveEntry("/incoming/script.bin", out var uploaded));
        Assert.Equal(VfsEntryKind.File, uploaded.EntryKind);
        Assert.Equal(VfsFileKind.ExecutableHardcode, uploaded.FileKind);
        Assert.Equal(2048, uploaded.Size);

        var events = DrainQueuedGameEvents(harness.World);
        var fileAcquireEvents = events
            .Where(static gameEvent => string.Equals((string)GetPropertyValue(gameEvent, "EventType"), "fileAcquire", StringComparison.Ordinal))
            .ToList();
        Assert.Empty(fileAcquireEvents);
    }

    /// <summary>Ensures ftp enforces active-session remote read/write privileges instead of command context user privileges.</summary>
    [Fact]
    public void Execute_Ftp_UsesActiveSessionUserPrivileges()
    {
        var harness = CreateHarness(includeVfsModule: false, includeConnectModule: true, cwd: "/work");
        harness.BaseFileSystem.AddDirectory("/work");
        harness.BaseFileSystem.AddDirectory("/work/local");
        harness.Server.DiskOverlay.WriteFile("/work/local/a.txt", "A", fileKind: VfsFileKind.Text);

        var remote = AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, "pw");
        AddFtpPort(remote);
        remote.Users["adminKey"] = new UserConfig
        {
            UserId = "admin",
            AuthMode = AuthMode.None,
            Privilege = PrivilegeConfig.FullAccess(),
        };
        remote.Users["guest"].Privilege.Read = false;
        remote.Users["guest"].Privilege.Write = false;
        remote.DiskOverlay.AddDirectory("/drop");
        remote.DiskOverlay.WriteFile("/drop/a.txt", "A", fileKind: VfsFileKind.Text);

        var connect = Execute(harness, "connect 10.0.1.20 guest pw", terminalSessionId: "ts-ftp-active-user");
        Assert.True(connect.Ok);

        var getResult = Execute(
            harness,
            "ftp get /drop/a.txt",
            nodeId: remote.NodeId,
            userId: "admin",
            cwd: "/",
            terminalSessionId: "ts-ftp-active-user");
        Assert.False(getResult.Ok);
        Assert.Equal(SystemCallErrorCode.PermissionDenied, getResult.Code);
        Assert.Contains("permission denied: ftp get", getResult.Lines[0], StringComparison.Ordinal);

        var putResult = Execute(
            harness,
            "ftp put local/a.txt",
            nodeId: remote.NodeId,
            userId: "admin",
            cwd: "/",
            terminalSessionId: "ts-ftp-active-user");
        Assert.False(putResult.Ok);
        Assert.Equal(SystemCallErrorCode.PermissionDenied, putResult.Code);
        Assert.Contains("permission denied: ftp put", putResult.Lines[0], StringComparison.Ordinal);
    }

    /// <summary>Ensures ftp enforces local workstation read/write privilege checks.</summary>
    [Fact]
    public void Execute_Ftp_Fails_WhenLocalPrivilegesAreMissing()
    {
        var harness = CreateHarness(includeVfsModule: false, includeConnectModule: true, cwd: "/work");
        harness.BaseFileSystem.AddDirectory("/work");
        harness.BaseFileSystem.AddDirectory("/work/local");
        harness.Server.DiskOverlay.WriteFile("/work/local/a.txt", "A", fileKind: VfsFileKind.Text);

        var remote = AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, "pw");
        AddFtpPort(remote);
        remote.DiskOverlay.AddDirectory("/drop");
        remote.DiskOverlay.WriteFile("/drop/a.txt", "A", fileKind: VfsFileKind.Text);

        var connect = Execute(harness, "connect 10.0.1.20 guest pw", terminalSessionId: "ts-ftp-local-priv");
        Assert.True(connect.Ok);

        harness.Server.Users["guest"].Privilege.Write = false;
        var getResult = Execute(
            harness,
            "ftp get /drop/a.txt",
            nodeId: remote.NodeId,
            userId: "guest",
            cwd: "/",
            terminalSessionId: "ts-ftp-local-priv");
        Assert.False(getResult.Ok);
        Assert.Equal(SystemCallErrorCode.PermissionDenied, getResult.Code);

        harness.Server.Users["guest"].Privilege.Write = true;
        harness.Server.Users["guest"].Privilege.Read = false;
        var putResult = Execute(
            harness,
            "ftp put local/a.txt",
            nodeId: remote.NodeId,
            userId: "guest",
            cwd: "/",
            terminalSessionId: "ts-ftp-local-priv");
        Assert.False(putResult.Ok);
        Assert.Equal(SystemCallErrorCode.PermissionDenied, putResult.Code);
    }

    /// <summary>Ensures ftp enforces FTP port gating and supports custom -p/--port overrides.</summary>
    [Fact]
    public void Execute_Ftp_EnforcesPortGating_AndSupportsCustomPort()
    {
        var harness = CreateHarness(includeVfsModule: false, includeConnectModule: true, cwd: "/work");
        harness.BaseFileSystem.AddDirectory("/work");
        harness.BaseFileSystem.AddDirectory("/work/downloads");

        var remote = AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, "pw");
        remote.Ports.Remove(21);
        remote.Ports[2121] = new PortConfig
        {
            PortType = PortType.Ftp,
            Exposure = PortExposure.Public,
        };
        remote.DiskOverlay.AddDirectory("/drop");
        remote.DiskOverlay.WriteFile("/drop/a.txt", "A", fileKind: VfsFileKind.Text);

        var connect = Execute(harness, "connect 10.0.1.20 guest pw", terminalSessionId: "ts-ftp-port");
        Assert.True(connect.Ok);

        var defaultPortFailure = Execute(
            harness,
            "ftp get /drop/a.txt downloads",
            nodeId: remote.NodeId,
            userId: "guest",
            cwd: "/",
            terminalSessionId: "ts-ftp-port");
        Assert.False(defaultPortFailure.Ok);
        Assert.Equal(SystemCallErrorCode.NotFound, defaultPortFailure.Code);
        Assert.Contains("port not available: 21", defaultPortFailure.Lines[0], StringComparison.Ordinal);

        var customPortSuccess = Execute(
            harness,
            "ftp --port 2121 get /drop/a.txt downloads",
            nodeId: remote.NodeId,
            userId: "guest",
            cwd: "/",
            terminalSessionId: "ts-ftp-port");
        Assert.True(customPortSuccess.Ok);
    }

    /// <summary>Ensures nested ssh sessions keep ftp local endpoint anchored at the workstation origin.</summary>
    [Fact]
    public void Execute_FtpGet_UsesWorkstationAsLocalEndpoint_OnNestedConnections()
    {
        var harness = CreateHarness(includeVfsModule: false, includeConnectModule: true, cwd: "/work");
        harness.BaseFileSystem.AddDirectory("/work");

        var hopB = AddRemoteServer(harness, "node-2", "hop-b", "10.0.1.20", AuthMode.Static, "pw");
        AddFtpPort(hopB);
        var hopC = AddRemoteServer(harness, "node-3", "hop-c", "10.0.1.30", AuthMode.Static, "pw");
        AddFtpPort(hopC);
        hopC.DiskOverlay.AddDirectory("/drop");
        hopC.DiskOverlay.WriteFile("/drop/nested.txt", "NESTED", fileKind: VfsFileKind.Text);

        var connectB = Execute(harness, "connect 10.0.1.20 guest pw", terminalSessionId: "ts-ftp-nested");
        Assert.True(connectB.Ok);

        var connectC = Execute(
            harness,
            "connect 10.0.1.30 guest pw",
            nodeId: hopB.NodeId,
            userId: "guest",
            cwd: "/",
            terminalSessionId: "ts-ftp-nested");
        Assert.True(connectC.Ok);

        var result = Execute(
            harness,
            "ftp get /drop/nested.txt",
            nodeId: hopC.NodeId,
            userId: "guest",
            cwd: "/",
            terminalSessionId: "ts-ftp-nested");

        Assert.True(result.Ok);
        Assert.True(harness.Server.DiskOverlay.TryResolveEntry("/work/nested.txt", out _));
        Assert.False(hopB.DiskOverlay.TryResolveEntry("/nested.txt", out _));
    }

    /// <summary>Ensures terminal-command bridge projects transition fields and preserves nextCwd behavior.</summary>
    [Fact]
    public void ExecuteTerminalCommand_ProjectsTransitionFields_AndPreservesNextCwd()
    {
        var transition = CreateInternalInstance("Uplink2.Runtime.Syscalls.TerminalContextTransition", Array.Empty<object?>());
        SetAutoPropertyBackingField(transition, "NextNodeId", "node-2");
        SetAutoPropertyBackingField(transition, "NextUserId", "guest");
        SetAutoPropertyBackingField(transition, "NextPromptUser", "guest");
        SetAutoPropertyBackingField(transition, "NextPromptHost", "remote");
        SetAutoPropertyBackingField(transition, "NextCwd", "/");
        SetAutoPropertyBackingField(transition, "ClearTerminalBeforeOutput", true);
        SetAutoPropertyBackingField(transition, "ActivateMotdAnchor", true);

        var transitionResult = SystemCallResult.Success(nextCwd: string.Empty, data: transition);
        var transitionPayload = BuildTerminalCommandResponsePayload(transitionResult);

        Assert.Equal("node-2", transitionPayload["nextNodeId"]);
        Assert.Equal("guest", transitionPayload["nextUserId"]);
        Assert.False(transitionPayload.ContainsKey("nextUserKey"));
        Assert.Equal("guest", transitionPayload["nextPromptUser"]);
        Assert.Equal("remote", transitionPayload["nextPromptHost"]);
        Assert.Equal("/", transitionPayload["nextCwd"]);
        Assert.Equal(true, transitionPayload["clearTerminal"]);
        Assert.Equal(true, transitionPayload["activateMotdAnchor"]);
        Assert.Equal(false, transitionPayload["openEditor"]);
        Assert.Equal(string.Empty, transitionPayload["editorPath"]);
        Assert.Equal(string.Empty, transitionPayload["editorContent"]);
        Assert.Equal(false, transitionPayload["editorReadOnly"]);
        Assert.Equal("text", transitionPayload["editorDisplayMode"]);
        Assert.Equal(false, transitionPayload["editorPathExists"]);

        var cwdResult = SystemCallResult.Success(nextCwd: "/work");
        var cwdPayload = BuildTerminalCommandResponsePayload(cwdResult);
        Assert.Equal("/work", cwdPayload["nextCwd"]);
        Assert.Equal(false, cwdPayload["clearTerminal"]);
        Assert.Equal(false, cwdPayload["activateMotdAnchor"]);
        Assert.Equal(false, cwdPayload["openEditor"]);
        Assert.Equal(false, cwdPayload["editorReadOnly"]);
    }

    /// <summary>Ensures default terminal-context API surface uses userId and does not expose userKey keys.</summary>
    [Fact]
    public void GetDefaultTerminalContext_UsesUserIdKey()
    {
        var method = typeof(WorldRuntime).GetMethod("GetDefaultTerminalContext", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(method);
        var parameters = method!.GetParameters();
        Assert.Single(parameters);
        Assert.Equal("preferredUserId", parameters[0].Name);

        var literals = ExtractStringLiterals((MethodInfo)method);
        Assert.Contains("userId", literals);
        Assert.DoesNotContain("userKey", literals);
    }

    /// <summary>Ensures default terminal context wiring includes motdLines payload and MOTD helper invocation.</summary>
    [Fact]
    public void GetDefaultTerminalContext_EmitsMotdLinesKey_AndCallsMotdResolver()
    {
        var method = typeof(WorldRuntime).GetMethod("GetDefaultTerminalContext", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(method);

        var literals = ExtractStringLiterals((MethodInfo)method!);
        Assert.Contains("motdLines", literals);

        var calledMethods = ExtractCalledMethods((MethodInfo)method);
        Assert.Contains(
            calledMethods,
            static called => called.DeclaringType == typeof(WorldRuntime) &&
                             string.Equals(called.Name, "ResolveMotdLinesForLogin", StringComparison.Ordinal));
    }

    /// <summary>Ensures command completion API returns sorted, distinct builtins plus executable program names.</summary>
    [Fact]
    public void GetTerminalCommandCompletions_ReturnsSortedDistinctBuiltinsAndExecutables()
    {
        var harness = CreateHarness(includeVfsModule: true, includeConnectModule: true, cwd: "/work");
        harness.BaseFileSystem.AddDirectory("/work");
        harness.BaseFileSystem.AddFile("/work/localtool", "exec:noop", fileKind: VfsFileKind.ExecutableHardcode);
        harness.BaseFileSystem.AddFile("/opt/bin/remotetool", "exec:noop", fileKind: VfsFileKind.ExecutableHardcode);
        harness.BaseFileSystem.AddFile("/opt/bin/not_exec.txt", "plain", fileKind: VfsFileKind.Text);

        var method = typeof(WorldRuntime).GetMethod(
            "GetTerminalCommandCompletionsCore",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var completions = method!.Invoke(harness.World, new object?[]
        {
            harness.Server.NodeId,
            "guest",
            "/work",
        }) as IReadOnlyList<string>;
        Assert.NotNull(completions);

        var completionList = completions!.ToList();
        Assert.Contains("cat", completionList);
        Assert.Contains("connect", completionList);
        Assert.Contains("localtool", completionList);
        Assert.Contains("remotetool", completionList);
        Assert.DoesNotContain("not_exec.txt", completionList);

        var sorted = completionList.OrderBy(static value => value, StringComparer.Ordinal).ToList();
        Assert.Equal(sorted, completionList);
        Assert.Equal(completionList.Count, completionList.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>Ensures path completion API returns normalized directory and child metadata.</summary>
    [Fact]
    public void GetTerminalPathCompletionEntries_ReturnsChildrenWithDirectoryFlags()
    {
        var harness = CreateHarness(includeVfsModule: true, cwd: "/work");
        harness.BaseFileSystem.AddDirectory("/work");
        harness.Server.DiskOverlay.AddDirectory("/work/subdir");
        harness.Server.DiskOverlay.WriteFile("/work/alpha.txt", "A", fileKind: VfsFileKind.Text);
        harness.Server.DiskOverlay.WriteFile("/work/.hidden", "H", fileKind: VfsFileKind.Text);

        var method = typeof(WorldRuntime).GetMethod(
            "GetTerminalPathCompletionEntriesCore",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var payload = method!.Invoke(harness.World, new object?[]
        {
            harness.Server.NodeId,
            "guest",
            "/work",
            ".",
        }) as Dictionary<string, object>;
        Assert.NotNull(payload);

        Assert.True((bool)payload!["ok"]);
        Assert.Equal("/work", payload["normalizedDirectoryPath"]);
        Assert.Equal(string.Empty, payload["error"]);

        var entries = Assert.IsAssignableFrom<IReadOnlyList<Dictionary<string, object>>>(payload["entries"]);
        Assert.NotEmpty(entries);
        var byName = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var name = (string)entry["name"]!;
            var isDirectory = (bool)entry["isDirectory"]!;
            byName[name] = isDirectory;
        }

        Assert.True(byName.ContainsKey("alpha.txt"));
        Assert.False(byName["alpha.txt"]);
        Assert.True(byName.ContainsKey("subdir"));
        Assert.True(byName["subdir"]);
        Assert.True(byName.ContainsKey(".hidden"));
    }

    /// <summary>Ensures path completion API denies access when read privilege is missing.</summary>
    [Fact]
    public void GetTerminalPathCompletionEntries_FailsWithoutReadPrivilege()
    {
        var harness = CreateHarness(
            includeVfsModule: true,
            cwd: "/work",
            privilege: new PrivilegeConfig
            {
                Read = false,
                Write = true,
                Execute = true,
            });
        harness.BaseFileSystem.AddDirectory("/work");

        var method = typeof(WorldRuntime).GetMethod(
            "GetTerminalPathCompletionEntriesCore",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var payload = method!.Invoke(harness.World, new object?[]
        {
            harness.Server.NodeId,
            "guest",
            "/work",
            ".",
        }) as Dictionary<string, object>;
        Assert.NotNull(payload);

        Assert.False((bool)payload!["ok"]);
        Assert.Contains("permission denied", (string)payload["error"], StringComparison.Ordinal);
        var entries = Assert.IsAssignableFrom<IReadOnlyList<Dictionary<string, object>>>(payload["entries"]);
        Assert.Empty(entries);
    }

    /// <summary>Ensures MOTD resolver returns content lines only when account can read text /etc/motd.</summary>
    [Fact]
    public void ResolveMotdLinesForLogin_ReturnsExpectedLines_OnlyForReadableTextMotd()
    {
        var harness = CreateHarness(includeVfsModule: false);
        var resolveMotd = typeof(WorldRuntime).GetMethod("ResolveMotdLinesForLogin", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(resolveMotd);

        harness.Server.DiskOverlay.AddDirectory("/etc");
        harness.Server.DiskOverlay.WriteFile("/etc/motd", "line-1\nline-2", fileKind: VfsFileKind.Text);

        var readableLines = (IReadOnlyList<string>)resolveMotd!.Invoke(harness.World, new object?[] { harness.Server, "guest" })!;
        Assert.Equal(new[] { "line-1", "line-2" }, readableLines);

        harness.Server.Users["guest"].Privilege.Read = false;
        var noPrivilegeLines = (IReadOnlyList<string>)resolveMotd.Invoke(harness.World, new object?[] { harness.Server, "guest" })!;
        Assert.Empty(noPrivilegeLines);

        harness.Server.Users["guest"].Privilege.Read = true;
        harness.Server.DiskOverlay.WriteFile("/etc/motd", "0102", fileKind: VfsFileKind.Binary);
        var nonTextLines = (IReadOnlyList<string>)resolveMotd.Invoke(harness.World, new object?[] { harness.Server, "guest" })!;
        Assert.Empty(nonTextLines);
    }

    /// <summary>Ensures terminal event filtering maps internal userKey lines into userId values at API boundary.</summary>
    [Fact]
    public void ResolveUserIdForTerminalEventLine_MapsUserKeyToUserId()
    {
        var harness = CreateHarness(includeVfsModule: false);
        harness.Server.Users["adminKey"] = new UserConfig
        {
            UserId = "admin",
            AuthMode = AuthMode.None,
            Privilege = PrivilegeConfig.FullAccess(),
        };

        var line = CreateInternalInstance(
            "Uplink2.Runtime.Events.TerminalEventLine",
            new object?[] { harness.Server.NodeId, "adminKey", "admin-line" });
        var resolveMethod = typeof(WorldRuntime).GetMethod(
            "ResolveUserIdForTerminalEventLine",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(resolveMethod);
        var resolvedUserId = (string)resolveMethod!.Invoke(harness.World, new[] { line })!;
        Assert.Equal("admin", resolvedUserId);
    }

    /// <summary>Ensures server user map rejects duplicate userId values during blueprint apply.</summary>
    [Fact]
    public void ApplyUsers_Throws_OnDuplicateUserIdPerServer()
    {
        var blobStore = new BlobStore();
        var baseFileSystem = new BaseFileSystem(blobStore);
        var server = new ServerNodeRuntime("node-dup", "dup", ServerRole.Terminal, baseFileSystem, blobStore);
        var users = new Dictionary<string, UserBlueprint>
        {
            ["keyA"] = new UserBlueprint
            {
                UserId = "root",
            },
            ["keyB"] = new UserBlueprint
            {
                UserId = "root",
            },
        };

        var applyUsers = typeof(WorldRuntime).GetMethod("ApplyUsers", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(applyUsers);
        var ex = Assert.Throws<TargetInvocationException>(() =>
            applyUsers!.Invoke(null, new object?[] { server, users, "node-dup", 101, "player" }));
        Assert.IsType<InvalidDataException>(ex.InnerException);
        Assert.Contains("node-dup", ex.InnerException!.Message, StringComparison.Ordinal);
        Assert.Contains("root", ex.InnerException.Message, StringComparison.Ordinal);
    }

    /// <summary>Ensures world-ready initialization order runs system initialization before world build.</summary>
    [Fact]
    public void Ready_InitializesSystemsBeforeWorldBuild()
    {
        var readyMethod = typeof(WorldRuntime).GetMethod("_Ready", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(readyMethod);

        var calledMethods = ExtractCalledMethods((MethodInfo)readyMethod!);
        var runtimeType = typeof(WorldRuntime);
        var buildBaseImageIndex = IndexOfCall(calledMethods, runtimeType, "BuildBaseOsImage");
        var loadDictionaryIndex = IndexOfCall(calledMethods, runtimeType, "LoadDictionaryPasswordPool");
        var initializeSystemCallsIndex = IndexOfCall(calledMethods, runtimeType, "InitializeSystemCalls");
        var buildWorldIndex = IndexOfCall(calledMethods, runtimeType, "BuildInitialWorldFromBlueprint");
        var validateWorldSeedIndex = IndexOfCall(calledMethods, runtimeType, "ValidateWorldSeedForWorldBuild");

        Assert.True(buildBaseImageIndex >= 0, "BuildBaseOsImage call not found in _Ready.");
        Assert.True(loadDictionaryIndex >= 0, "LoadDictionaryPasswordPool call not found in _Ready.");
        Assert.True(initializeSystemCallsIndex >= 0, "InitializeSystemCalls call not found in _Ready.");
        Assert.True(buildWorldIndex >= 0, "BuildInitialWorldFromBlueprint call not found in _Ready.");
        Assert.True(validateWorldSeedIndex >= 0, "ValidateWorldSeedForWorldBuild call not found in _Ready.");
        Assert.True(buildBaseImageIndex < loadDictionaryIndex);
        Assert.True(loadDictionaryIndex < initializeSystemCallsIndex);
        Assert.True(initializeSystemCallsIndex < buildWorldIndex);
        Assert.True(buildWorldIndex < validateWorldSeedIndex);
    }

    /// <summary>Ensures base OS image seeds /etc/motd from default motd resource content.</summary>
    [Fact]
    public void BuildBaseOsImage_LoadsDefaultMotdResourceIntoEtcMotd()
    {
        var buildBaseOsImage = typeof(WorldRuntime).GetMethod(
            "BuildBaseOsImage",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var loadDefaultMotdContent = typeof(WorldRuntime).GetMethod(
            "LoadDefaultMotdContent",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(buildBaseOsImage);
        Assert.NotNull(loadDefaultMotdContent);

        var buildCalls = ExtractCalledMethods((MethodInfo)buildBaseOsImage!);
        Assert.Contains(
            buildCalls,
            static called => called.DeclaringType == typeof(WorldRuntime) &&
                             string.Equals(called.Name, "LoadDefaultMotdContent", StringComparison.Ordinal));

        var buildLiterals = ExtractStringLiterals((MethodInfo)buildBaseOsImage);
        Assert.Contains("/etc/motd", buildLiterals);

        var loadCalls = ExtractCalledMethods((MethodInfo)loadDefaultMotdContent!);
        Assert.Contains(
            loadCalls,
            static called => string.Equals(called.DeclaringType?.FullName, "Godot.ProjectSettings", StringComparison.Ordinal) &&
                             string.Equals(called.Name, "GlobalizePath", StringComparison.Ordinal));
        Assert.Contains(
            loadCalls,
            static called => called.DeclaringType == typeof(File) &&
                             string.Equals(called.Name, nameof(File.ReadAllText), StringComparison.Ordinal) &&
                             called.GetParameters().Length == 2);

        var loadLiterals = ExtractStringLiterals((MethodInfo)loadDefaultMotdContent);
        Assert.Contains("res://scenario_content/resources/text/default_motd.txt", loadLiterals);
    }

    /// <summary>Ensures WorldSeed getter is blocked during system initialization stage.</summary>
    [Fact]
    public void WorldSeed_Getter_Throws_DuringSystemInitializing()
    {
        var world = (WorldRuntime)RuntimeHelpers.GetUninitializedObject(typeof(WorldRuntime));
        world.WorldSeed = 123;
        SetWorldInitializationStage(world, "SystemInitializing");

        var ex = Assert.Throws<InvalidOperationException>(() => _ = world.WorldSeed);
        Assert.Contains("WorldSeed cannot be read during initialization stage", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Ensures WorldSeed getter is blocked during world-building stage.</summary>
    [Fact]
    public void WorldSeed_Getter_Throws_DuringWorldBuilding()
    {
        var world = (WorldRuntime)RuntimeHelpers.GetUninitializedObject(typeof(WorldRuntime));
        world.WorldSeed = 123;
        SetWorldInitializationStage(world, "WorldBuilding");

        var ex = Assert.Throws<InvalidOperationException>(() => _ = world.WorldSeed);
        Assert.Contains("WorldSeed cannot be read during initialization stage", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Ensures WorldSeed getter returns value after initialization reaches ready stage.</summary>
    [Fact]
    public void WorldSeed_Getter_Returns_WhenReady()
    {
        var world = (WorldRuntime)RuntimeHelpers.GetUninitializedObject(typeof(WorldRuntime));
        world.WorldSeed = 987654321;
        SetWorldInitializationStage(world, "Ready");

        Assert.Equal(987654321, world.WorldSeed);
    }

    /// <summary>Ensures world build is guarded behind system-call initialization completion.</summary>
    [Fact]
    public void BuildInitialWorldFromBlueprint_Throws_WhenSystemCallsAreNotInitialized()
    {
        var world = (WorldRuntime)RuntimeHelpers.GetUninitializedObject(typeof(WorldRuntime));
        var method = typeof(WorldRuntime).GetMethod(
            "BuildInitialWorldFromBlueprint",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var ex = Assert.Throws<TargetInvocationException>(() => method!.Invoke(world, Array.Empty<object?>()));
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("InitializeSystemCalls", ex.InnerException!.Message, StringComparison.Ordinal);
    }

    /// <summary>Ensures world-seed initializer keeps preset values unchanged.</summary>
    [Fact]
    public void InitializeWorldSeedForWorldBuild_UsesPresetWhenNonZero()
    {
        var world = (WorldRuntime)RuntimeHelpers.GetUninitializedObject(typeof(WorldRuntime));
        world.WorldSeed = 42;

        var initializedSeed = InvokeInitializeWorldSeedForWorldBuild(world);

        Assert.Equal(42, initializedSeed);
        Assert.Equal(42, GetWorldSeedBackingField(world));
    }

    /// <summary>Ensures world-seed initializer generates non-zero seed when preset is zero.</summary>
    [Fact]
    public void InitializeWorldSeedForWorldBuild_GeneratesWhenZero()
    {
        var world = (WorldRuntime)RuntimeHelpers.GetUninitializedObject(typeof(WorldRuntime));
        world.WorldSeed = 0;

        var initializedSeed = InvokeInitializeWorldSeedForWorldBuild(world);

        Assert.NotEqual(0, initializedSeed);
        Assert.NotEqual(0, GetWorldSeedBackingField(world));
    }

    /// <summary>Ensures world initialization rejects missing (zero) worldSeed before build starts.</summary>
    [Fact]
    public void ValidateWorldSeedForWorldBuild_Throws_WhenWorldSeedIsZero()
    {
        var world = (WorldRuntime)RuntimeHelpers.GetUninitializedObject(typeof(WorldRuntime));
        world.WorldSeed = 0;

        var validate = typeof(WorldRuntime).GetMethod(
            "ValidateWorldSeedForWorldBuild",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(validate);

        var ex = Assert.Throws<TargetInvocationException>(() => validate!.Invoke(world, Array.Empty<object?>()));
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("WorldSeed", ex.InnerException!.Message, StringComparison.Ordinal);
    }

    /// <summary>Ensures world initialization accepts non-zero worldSeed.</summary>
    [Fact]
    public void ValidateWorldSeedForWorldBuild_Allows_NonZeroWorldSeed()
    {
        var world = (WorldRuntime)RuntimeHelpers.GetUninitializedObject(typeof(WorldRuntime));
        world.WorldSeed = 42;

        var validate = typeof(WorldRuntime).GetMethod(
            "ValidateWorldSeedForWorldBuild",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(validate);

        var ex = Record.Exception(() => validate!.Invoke(world, Array.Empty<object?>()));
        Assert.Null(ex);
    }

    /// <summary>Ensures AUTO userId token policy is deterministic for same worldSeed and changes across seeds.</summary>
    [Fact]
    public void ResolveUserId_AutoPolicy_IsDeterministicAndWorldSeedSensitive()
    {
        AssertDeterministicAndWorldSeedSensitive(
            seed => InvokeResolveUserId("AUTO:randomized", seed, "node-alpha", "guestKey", "player"));
    }

    /// <summary>Ensures AUTO:user resolves using configurable DefaultUserId instead of internal userKey.</summary>
    [Fact]
    public void ResolveUserId_AutoUserPolicy_UsesConfiguredDefaultUserId()
    {
        var resolved = InvokeResolveUserId("AUTO:user", 12345, "node-alpha", "mainCharacter", "operatorName");
        Assert.Equal("operatorName", resolved);
        Assert.DoesNotContain("mainCharacter", resolved, StringComparison.Ordinal);
    }

    /// <summary>Ensures AUTO base64 password policy is deterministic for same worldSeed and changes across seeds.</summary>
    [Fact]
    public void ResolvePassword_AutoBase64Policy_IsDeterministicAndWorldSeedSensitive()
    {
        AssertDeterministicAndWorldSeedSensitive(
            seed => InvokeResolvePassword("AUTO:c16_base64", seed, "node-alpha", "guestKey"));
    }

    /// <summary>Ensures AUTO numeric+special password policy is deterministic for same worldSeed and changes across seeds.</summary>
    [Fact]
    public void ResolvePassword_AutoNumSpecialPolicy_IsDeterministicAndWorldSeedSensitive()
    {
        AssertDeterministicAndWorldSeedSensitive(
            seed => InvokeResolvePassword("AUTO:4c_numspecial", seed, "node-alpha", "guestKey"));
    }

    /// <summary>Ensures AUTO numeric+special policy emits requested length using only allowed alphabet.</summary>
    [Fact]
    public void ResolvePassword_AutoNumSpecialPolicy_UsesRequestedAlphabetAndLength()
    {
        const string allowed = "0123456789!@#$%^&*()";
        var resolved = InvokeResolvePassword("AUTO:12c_numspecial", 12345, "node-alpha", "guestKey");

        Assert.Equal(12, resolved.Length);
        Assert.All(
            resolved,
            ch => Assert.True(allowed.IndexOf(ch) >= 0, $"Unexpected character: {ch}"));
    }

    /// <summary>Ensures AUTO numeric+special policy also supports cN_numspecial format.</summary>
    [Fact]
    public void ResolvePassword_AutoNumSpecialPolicy_SupportsCPrefixFormat()
    {
        const string allowed = "0123456789!@#$%^&*()";
        var resolved = InvokeResolvePassword("AUTO:c5_numspecial", 12345, "node-alpha", "guestKey");

        Assert.Equal(5, resolved.Length);
        Assert.All(
            resolved,
            ch => Assert.True(allowed.IndexOf(ch) >= 0, $"Unexpected character: {ch}"));
    }

    /// <summary>Ensures AUTO fallback password policy is deterministic for same worldSeed and changes across seeds.</summary>
    [Fact]
    public void ResolvePassword_AutoFallbackPolicy_IsDeterministicAndWorldSeedSensitive()
    {
        AssertDeterministicAndWorldSeedSensitive(
            seed => InvokeResolvePassword("AUTO:unsupported_policy", seed, "node-alpha", "guestKey"));
    }

    /// <summary>Ensures AUTO dictionary password policy is deterministic for same worldSeed and changes across seeds.</summary>
    [Fact]
    public void ResolvePassword_AutoDictionaryPolicy_IsDeterministicAndWorldSeedSensitive()
    {
        var pool = Enumerable.Range(0, 4096)
            .Select(static index => $"pw_{index:D4}")
            .ToArray();

        WithDictionaryPasswordPool(
            pool,
            () => AssertDeterministicAndWorldSeedSensitive(
                seed => InvokeResolvePassword("AUTO:dictionary", seed, "node-alpha", "guestKey")));
    }

    /// <summary>Ensures AUTO dictionaryHard password policy is deterministic, worldSeed-sensitive, and selects only length&gt;8 passwords.</summary>
    [Fact]
    public void ResolvePassword_AutoDictionaryHardPolicy_IsDeterministicAndWorldSeedSensitive()
    {
        var pool = new[]
        {
            "short1",
            "12345678",
            "verystrong1",
            "hardpass999",
            "qwertyuiop",
        };

        WithDictionaryPasswordPool(
            pool,
            () =>
            {
                AssertDeterministicAndWorldSeedSensitive(
                    seed => InvokeResolvePassword("AUTO:dictionaryHard", seed, "node-alpha", "guestKey"));

                var resolved = InvokeResolvePassword("AUTO:dictionaryHard", 12345, "node-alpha", "guestKey");
                Assert.Contains(resolved, pool.Where(static value => value.Length > 8));
            });
    }

    /// <summary>Ensures AUTO dictionaryHard fails when dictionary pool has no password longer than 8 characters.</summary>
    [Fact]
    public void ResolvePassword_AutoDictionaryHardPolicy_Throws_WhenNoHardPasswordExists()
    {
        var pool = new[]
        {
            "short1",
            "12345678",
            "tiny",
        };

        WithDictionaryPasswordPool(
            pool,
            () =>
            {
                var ex = Assert.Throws<TargetInvocationException>(
                    () => InvokeResolvePassword("AUTO:dictionaryHard", 12345, "node-alpha", "guestKey"));
                Assert.IsType<InvalidOperationException>(ex.InnerException);
                Assert.Contains("Dictionary hard password pool is empty", ex.InnerException!.Message, StringComparison.Ordinal);
            });
    }

    /// <summary>Ensures blueprint overlay apply propagates size override while recording computed realSize.</summary>
    [Fact]
    public void ApplyOverlayEntry_UsesSizeOverrideAndStoresRealSize()
    {
        var harness = CreateHarness(includeVfsModule: false);
        var entry = new BlueprintEntryMeta
        {
            EntryKind = BlueprintEntryKind.File,
            FileKind = BlueprintFileKind.ExecutableHardcode,
            ContentId = "exec:noop",
            Size = 4096,
        };

        var applyOverlayEntry = typeof(WorldRuntime).GetMethod("ApplyOverlayEntry", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(applyOverlayEntry);
        applyOverlayEntry!.Invoke(harness.World, new object?[] { harness.Server.DiskOverlay, "/opt/bin/tool", entry });

        Assert.Equal(9, entry.RealSize);
        Assert.True(harness.Server.DiskOverlay.TryResolveEntry("/opt/bin/tool", out var runtimeEntry));
        Assert.Equal(4096, runtimeEntry.Size);
        Assert.Equal(9, runtimeEntry.RealSize);
    }

    private static void AssertDeterministicAndWorldSeedSensitive(Func<int, string> resolve)
    {
        var baselineSeed = 12345;
        var baseline = resolve(baselineSeed);
        Assert.Equal(baseline, resolve(baselineSeed));

        var foundDifferent = false;
        for (var seed = baselineSeed + 1; seed <= baselineSeed + 2048; seed++)
        {
            if (!string.Equals(baseline, resolve(seed), StringComparison.Ordinal))
            {
                foundDifferent = true;
                break;
            }
        }

        Assert.True(foundDifferent, "Expected at least one different output when worldSeed changes.");
    }

    private static (bool Handled, bool Started, SystemCallResult Response) TryStartTerminalProgramExecutionCore(
        WorldRuntime world,
        string nodeId,
        string userId,
        string cwd,
        string commandLine,
        string terminalSessionId)
    {
        var method = typeof(WorldRuntime).GetMethod(
            "TryStartTerminalProgramExecutionCore",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? typeof(WorldRuntime).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .FirstOrDefault(static candidate => string.Equals(candidate.Name, "TryStartTerminalProgramExecutionCore", StringComparison.Ordinal));
        Assert.NotNull(method);

        var coreResult = method!.Invoke(world, new object?[] { nodeId, userId, cwd, commandLine, terminalSessionId });
        Assert.NotNull(coreResult);
        var handledProperty = coreResult!.GetType().GetProperty(
            "Handled",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var startedProperty = coreResult.GetType().GetProperty(
            "Started",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var responseProperty = coreResult.GetType().GetProperty(
            "Response",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(handledProperty);
        Assert.NotNull(startedProperty);
        Assert.NotNull(responseProperty);

        var handled = (bool)handledProperty!.GetValue(coreResult)!;
        var started = (bool)startedProperty!.GetValue(coreResult)!;
        var response = (SystemCallResult)responseProperty!.GetValue(coreResult)!;
        return (handled, started, response);
    }

    private static SystemCallResult InterruptTerminalProgramExecutionCore(WorldRuntime world, string terminalSessionId)
    {
        var method = typeof(WorldRuntime).GetMethod(
            "InterruptTerminalProgramExecutionCore",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? typeof(WorldRuntime).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .FirstOrDefault(static candidate => string.Equals(candidate.Name, "InterruptTerminalProgramExecutionCore", StringComparison.Ordinal));
        Assert.NotNull(method);
        var result = method!.Invoke(world, new object?[] { terminalSessionId }) as SystemCallResult;
        Assert.NotNull(result);
        return result!;
    }

    private static void WaitForTerminalProgramStop(WorldRuntime world, string terminalSessionId, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (world.IsTerminalProgramRunning(terminalSessionId))
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"Timed out waiting for terminal program stop. sessionId='{terminalSessionId}'.");
            }

            Thread.Sleep(10);
        }
    }

    private static string[] SnapshotTerminalEventLines(WorldRuntime world)
    {
        var queue = GetPrivateField(world, "terminalEventLines");
        var lines = new List<string>();
        foreach (var queued in (System.Collections.IEnumerable)queue)
        {
            var text = (string?)GetPropertyValue(queued, "Text") ?? string.Empty;
            lines.Add(text);
        }

        return lines.ToArray();
    }

    private static int IndexOfCall(IReadOnlyList<MethodBase> calledMethods, Type declaringType, string methodName)
    {
        for (var index = 0; index < calledMethods.Count; index++)
        {
            var called = calledMethods[index];
            if (called.DeclaringType == declaringType &&
                string.Equals(called.Name, methodName, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static int InvokeInitializeWorldSeedForWorldBuild(WorldRuntime world)
    {
        var method = typeof(WorldRuntime).GetMethod(
            "InitializeWorldSeedForWorldBuild",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (int)method!.Invoke(world, Array.Empty<object?>())!;
    }

    private static int GetWorldSeedBackingField(WorldRuntime world)
    {
        var field = typeof(WorldRuntime).GetField("worldSeed", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (int)field!.GetValue(world)!;
    }

    private static void SetWorldInitializationStage(WorldRuntime world, string stageName)
    {
        var stageField = typeof(WorldRuntime).GetField("initializationStage", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(stageField);
        var stageValue = Enum.Parse(stageField!.FieldType, stageName, ignoreCase: false);
        stageField.SetValue(world, stageValue);
    }

    private static string InvokeResolveUserId(
        string source,
        int worldSeed,
        string nodeId,
        string userKey,
        string defaultUserId)
    {
        var method = typeof(WorldRuntime).GetMethod("ResolveUserId", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, new object?[] { source, worldSeed, nodeId, userKey, defaultUserId })!;
    }

    private static string InvokeResolvePassword(string source, int worldSeed, string nodeId, string userKey)
    {
        var method = typeof(WorldRuntime).GetMethod("ResolvePassword", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, new object?[] { source, worldSeed, nodeId, userKey })!;
    }

    private static void WithDictionaryPasswordPool(string[] pool, Action body)
    {
        var field = typeof(WorldRuntime).GetField("dictionaryPasswordPool", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var originalPool = field!.GetValue(null) as string[] ?? Array.Empty<string>();
        field.SetValue(null, pool);

        try
        {
            body();
        }
        finally
        {
            field.SetValue(null, originalPool);
        }
    }

    private const string OtpBase32AlphabetForTest = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    private static string GenerateTotpForTest(string otpPairId, long nowMs, long stepMs, int digits)
    {
        var secretBytes = DecodeBase32SecretForTest(otpPairId);
        var counter = (long)Math.Floor(nowMs / (double)stepMs);
        Span<byte> counterBytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counterBytes, counter);

        byte[] hash;
        using (var hmac = new HMACSHA1(secretBytes))
        {
            hash = hmac.ComputeHash(counterBytes.ToArray());
        }

        var offset = hash[^1] & 0x0F;
        var binaryCode = ((hash[offset] & 0x7F) << 24) |
                         ((hash[offset + 1] & 0xFF) << 16) |
                         ((hash[offset + 2] & 0xFF) << 8) |
                         (hash[offset + 3] & 0xFF);

        long modulus = 1;
        for (var index = 0; index < digits; index++)
        {
            modulus *= 10;
        }

        var otp = binaryCode % modulus;
        return otp.ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }

    private static byte[] DecodeBase32SecretForTest(string value)
    {
        var normalized = value
            .Trim()
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .TrimEnd('=')
            .ToUpperInvariant();
        if (normalized.Length == 0)
        {
            throw new InvalidOperationException("OTP pair id cannot be empty.");
        }

        var bytes = new List<byte>(normalized.Length * 5 / 8 + 1);
        var bitBuffer = 0;
        var bitsInBuffer = 0;
        foreach (var ch in normalized)
        {
            var charIndex = OtpBase32AlphabetForTest.IndexOf(ch);
            if (charIndex < 0)
            {
                throw new InvalidOperationException($"Invalid base32 character: '{ch}'.");
            }

            bitBuffer = (bitBuffer << 5) | charIndex;
            bitsInBuffer += 5;
            while (bitsInBuffer >= 8)
            {
                bitsInBuffer -= 8;
                bytes.Add((byte)((bitBuffer >> bitsInBuffer) & 0xFF));
            }
        }

        if (bytes.Count == 0)
        {
            throw new InvalidOperationException("OTP pair id decoded to an empty payload.");
        }

        return bytes.ToArray();
    }

    private static SystemCallHarness CreateHarness(
        bool includeVfsModule,
        bool includeConnectModule = false,
        bool includePrototypeSaveLoadModule = false,
        string cwd = "/",
        PrivilegeConfig? privilege = null,
        bool worldDebugOption = false)
    {
        var blobStore = new BlobStore();
        var baseFileSystem = new BaseFileSystem(blobStore);
        baseFileSystem.AddDirectory("/opt/bin");

        var server = new ServerNodeRuntime("node-1", "node-1", ServerRole.Terminal, baseFileSystem, blobStore);
        server.Users["guest"] = new UserConfig
        {
            UserId = "guest",
            AuthMode = AuthMode.None,
            Privilege = privilege ?? PrivilegeConfig.FullAccess(),
        };
        server.SetInterfaces(new[]
        {
            new InterfaceRuntime
            {
                NetId = "internet",
                Ip = "10.0.0.10",
            },
        });

        var world = CreateHeadlessWorld(worldDebugOption, server);
        var modules = new List<object>();
        if (includeVfsModule)
        {
            modules.Add(CreateInternalInstance("Uplink2.Runtime.Syscalls.VfsSystemCallModule", new object?[] { false }));
        }

        if (includeConnectModule)
        {
            modules.Add(CreateInternalInstance("Uplink2.Runtime.Syscalls.ConnectSystemCallModule", Array.Empty<object?>()));
        }

        if (includePrototypeSaveLoadModule)
        {
            modules.Add(CreateInternalInstance("Uplink2.Runtime.Syscalls.PrototypeSaveLoadSystemCallModule", Array.Empty<object?>()));
        }

        var processor = CreateSystemCallProcessor(world, modules);
        SetPrivateField(world, "systemCallProcessor", processor);
        return new SystemCallHarness(world, server, baseFileSystem, processor, "guest", cwd);
    }

    private static ServerNodeRuntime AddRemoteServer(
        SystemCallHarness harness,
        string nodeId,
        string name,
        string ip,
        AuthMode authMode,
        string password,
        PortType portType = PortType.Ssh,
        PortExposure exposure = PortExposure.Public,
        string netId = "internet",
        string userKey = "guest",
        string userId = "guest")
    {
        var blobStore = new BlobStore();
        var baseFileSystem = new BaseFileSystem(blobStore);
        var server = new ServerNodeRuntime(nodeId, name, ServerRole.Terminal, baseFileSystem, blobStore);
        server.Users[userKey] = new UserConfig
        {
            UserId = userId,
            UserPasswd = password,
            AuthMode = authMode,
            Privilege = PrivilegeConfig.FullAccess(),
        };
        server.SetInterfaces(new[]
        {
            new InterfaceRuntime
            {
                NetId = netId,
                Ip = ip,
            },
        });
        server.Ports[22] = new PortConfig
        {
            PortType = portType,
            Exposure = exposure,
        };

        var serverList = GetAutoPropertyBackingField<Dictionary<string, ServerNodeRuntime>>(harness.World, "ServerList");
        serverList[server.NodeId] = server;

        var ipIndex = GetAutoPropertyBackingField<Dictionary<string, string>>(harness.World, "IpIndex");
        foreach (var iface in server.Interfaces)
        {
            ipIndex[iface.Ip] = server.NodeId;
        }

        return server;
    }

    private static void AddFtpPort(ServerNodeRuntime server, int port = 21, PortExposure exposure = PortExposure.Public)
    {
        server.Ports[port] = new PortConfig
        {
            PortType = PortType.Ftp,
            Exposure = exposure,
        };
    }

    private static void AddConnectionRateLimiterDaemon(
        ServerNodeRuntime server,
        long monitorMs,
        int threshold,
        long blockMs,
        int rateLimit,
        long recoveryMs)
    {
        var daemon = new DaemonStruct
        {
            DaemonType = DaemonType.ConnectionRateLimiter,
        };
        daemon.DaemonArgs["monitorMs"] = monitorMs;
        daemon.DaemonArgs["threshold"] = threshold;
        daemon.DaemonArgs["blockMs"] = blockMs;
        daemon.DaemonArgs["rateLimit"] = rateLimit;
        daemon.DaemonArgs["recoveryMs"] = recoveryMs;
        server.Daemons[DaemonType.ConnectionRateLimiter] = daemon;
    }

    private static bool TryGetConnectionRateLimiterOverloadedUntilMs(
        WorldRuntime world,
        string nodeId,
        out long overloadedUntilMs)
    {
        overloadedUntilMs = 0;
        var statesField = typeof(WorldRuntime).GetField(
            "connectionRateLimiterStatesByNodeId",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(statesField);
        if (statesField!.GetValue(world) is not System.Collections.IDictionary states)
        {
            return false;
        }

        if (!states.Contains(nodeId))
        {
            return false;
        }

        var state = states[nodeId];
        if (state is null)
        {
            return false;
        }

        var overloadedUntilProperty = state.GetType().GetProperty(
            "OverloadedUntilMs",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(overloadedUntilProperty);
        if (overloadedUntilProperty!.GetValue(state) is not long parsedValue)
        {
            return false;
        }

        overloadedUntilMs = parsedValue;
        return true;
    }

    private static void SetInspectProbeRateLimitState(WorldRuntime world, long windowStartMs, int callsInWindow)
    {
        var windowField = typeof(WorldRuntime).GetField(
            "inspectProbeRateLimitWindowStartMs",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(windowField);
        var countField = typeof(WorldRuntime).GetField(
            "inspectProbeRateLimitCallsInWindow",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(countField);

        windowField!.SetValue(world, windowStartMs);
        countField!.SetValue(world, callsInWindow);
    }

    private static WorldRuntime CreateHeadlessWorld(bool debugOption, params ServerNodeRuntime[] servers)
    {
        Assert.NotEmpty(servers);

        var world = (WorldRuntime)RuntimeHelpers.GetUninitializedObject(typeof(WorldRuntime));
        SetAutoPropertyBackingField(world, "DebugOption", debugOption);
        SetAutoPropertyBackingField(world, "ScenarioFlags", new Dictionary<string, object>(StringComparer.Ordinal));
        world.WorldSeed = 1;
        SetWorldInitializationStage(world, "Ready");

        var serverList = new Dictionary<string, ServerNodeRuntime>(StringComparer.Ordinal);
        var ipIndex = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var server in servers)
        {
            serverList[server.NodeId] = server;
            foreach (var iface in server.Interfaces)
            {
                ipIndex[iface.Ip] = server.NodeId;
            }
        }

        SetAutoPropertyBackingField(
            world,
            "ServerList",
            serverList);
        SetAutoPropertyBackingField(world, "IpIndex", ipIndex);

        var visibleNets = new HashSet<string>(StringComparer.Ordinal) { "internet" };
        var knownNodesByNet = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["internet"] = new HashSet<string>(StringComparer.Ordinal),
        };

        foreach (var server in servers)
        {
            foreach (var iface in server.Interfaces)
            {
                if (string.Equals(iface.NetId, "internet", StringComparison.Ordinal))
                {
                    knownNodesByNet["internet"].Add(server.NodeId);
                }
            }
        }

        SetAutoPropertyBackingField(world, "VisibleNets", visibleNets);
        SetAutoPropertyBackingField(world, "KnownNodesByNet", knownNodesByNet);
        SetAutoPropertyBackingField(world, "PlayerWorkstationServer", servers[0]);

        SetPrivateField(world, "eventQueue", CreateInternalInstance("Uplink2.Runtime.Events.EventQueue", Array.Empty<object?>()));
        SetPrivateField(world, "firedHandlerIds", new HashSet<string>(StringComparer.Ordinal));
        var terminalEventLineType = RequireRuntimeType("Uplink2.Runtime.Events.TerminalEventLine");
        var terminalQueueType = typeof(Queue<>).MakeGenericType(terminalEventLineType);
        var terminalQueue = Activator.CreateInstance(terminalQueueType);
        Assert.NotNull(terminalQueue);
        SetPrivateField(world, "terminalEventLines", terminalQueue!);
        SetPrivateField(world, "terminalEventLinesSync", new object());
        SetPrivateField(world, "terminalProgramExecutionSync", new object());
        var terminalExecutionsField = typeof(WorldRuntime).GetField(
            "terminalProgramExecutionsBySessionId",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(terminalExecutionsField);
        var terminalExecutions = Activator.CreateInstance(terminalExecutionsField!.FieldType);
        Assert.NotNull(terminalExecutions);
        terminalExecutionsField.SetValue(world, terminalExecutions);
        SetPrivateField(world, "initiallyExposedNodesByNet", new Dictionary<string, HashSet<string>>(StringComparer.Ordinal));
        SetPrivateField(world, "scheduledProcessEndAtById", new Dictionary<int, long>());
        SetPrivateField(world, "eventIndex", CreateInternalInstance("Uplink2.Runtime.Events.EventIndex", Array.Empty<object?>()));
        SetPrivateField(world, "processScheduler", CreateInternalInstance("Uplink2.Runtime.Events.ProcessScheduler", Array.Empty<object?>()));
        return world;
    }

    private static string CreatePrototypeSlotPathResolverRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "uplink2-prototype-save-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string ResolvePrototypeSlotPathForTests(string resolverRoot, int slot)
    {
        return Path.Combine(resolverRoot, "saves", $"slot{slot}.uls1");
    }

    private static IDisposable OverridePrototypeSlotPathResolver(string resolverRoot)
    {
        var handlerType = RequireRuntimeType("Uplink2.Runtime.Syscalls.PrototypeSaveCommandHandler");
        var property = handlerType.GetProperty(
            "ResolveAbsoluteSlotPath",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(property);

        var previousResolver = property!.GetValue(null);
        Func<string, string> resolver = slotPath =>
        {
            var normalized = slotPath?.Trim() ?? string.Empty;
            const string prefix = "user://";
            if (normalized.StartsWith(prefix, StringComparison.Ordinal))
            {
                normalized = normalized[prefix.Length..];
            }

            normalized = normalized.Replace('/', Path.DirectorySeparatorChar);
            return Path.Combine(resolverRoot, normalized);
        };

        property.SetValue(null, resolver);
        return new DelegateRestoreScope(() => property.SetValue(null, previousResolver));
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        Directory.Delete(path, recursive: true);
    }

    private sealed class DelegateRestoreScope : IDisposable
    {
        private readonly Action restore;
        private bool disposed;

        internal DelegateRestoreScope(Action restore)
        {
            this.restore = restore;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            restore();
        }
    }

    private static void SetAutoPropertyBackingField(object target, string propertyName, object value)
    {
        var field = target.GetType().GetField(
            $"<{propertyName}>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    private static T GetAutoPropertyBackingField<T>(object target, string propertyName)
    {
        var field = target.GetType().GetField(
            $"<{propertyName}>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (T)field!.GetValue(target)!;
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    private static object CreateSystemCallProcessor(WorldRuntime world, IReadOnlyList<object> modules)
    {
        var processorType = RequireRuntimeType("Uplink2.Runtime.Syscalls.SystemCallProcessor");
        var systemCallModuleType = RequireRuntimeType("Uplink2.Runtime.Syscalls.ISystemCallModule");
        var moduleEnumerableType = typeof(IEnumerable<>).MakeGenericType(systemCallModuleType);
        var constructor = processorType.GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(WorldRuntime), moduleEnumerableType },
            modifiers: null);
        Assert.NotNull(constructor);

        var moduleArray = Array.CreateInstance(systemCallModuleType, modules.Count);
        for (var index = 0; index < modules.Count; index++)
        {
            moduleArray.SetValue(modules[index], index);
        }

        var instance = constructor!.Invoke(new object?[] { world, moduleArray });
        Assert.NotNull(instance);
        return instance!;
    }

    private static object CreateInternalInstance(string fullTypeName, object?[]? args)
    {
        var type = RequireRuntimeType(fullTypeName);
        var instance = Activator.CreateInstance(
            type,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: args,
            culture: null);
        Assert.NotNull(instance);
        return instance!;
    }

    private static Type RequireRuntimeType(string fullTypeName)
    {
        var type = typeof(SystemCallResult).Assembly.GetType(fullTypeName);
        Assert.NotNull(type);
        return type!;
    }

    private static SystemCallResult Execute(
        SystemCallHarness harness,
        string commandLine,
        string? nodeId = null,
        string? userId = null,
        string? cwd = null,
        string? terminalSessionId = null)
    {
        var executeMethod = harness.Processor.GetType().GetMethod("Execute", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(executeMethod);
        var request = new SystemCallRequest
        {
            NodeId = nodeId ?? harness.Server.NodeId,
            UserId = userId ?? harness.UserId,
            Cwd = cwd ?? harness.Cwd,
            CommandLine = commandLine,
            TerminalSessionId = terminalSessionId ?? string.Empty,
        };

        var result = executeMethod!.Invoke(harness.Processor, new object?[] { request }) as SystemCallResult;
        Assert.NotNull(result);
        return result!;
    }

    private static IReadOnlyList<object> DrainQueuedGameEvents(WorldRuntime world)
    {
        var eventQueue = GetPrivateField(world, "eventQueue");
        var tryDequeue = eventQueue.GetType().GetMethod("TryDequeue", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(tryDequeue);

        var gameEvents = new List<object>();
        while (true)
        {
            object?[] args = { null };
            var dequeued = (bool)tryDequeue!.Invoke(eventQueue, args)!;
            if (!dequeued)
            {
                break;
            }

            gameEvents.Add(args[0]!);
        }

        return gameEvents;
    }

    private static object GetPrivateField(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field!.GetValue(target)!;
    }

    private static object? GetPropertyValue(object target, string propertyName)
    {
        var property = target.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(property);
        return property!.GetValue(target);
    }

    private static Dictionary<string, object> BuildTerminalCommandResponsePayload(SystemCallResult result)
    {
        var method = typeof(WorldRuntime).GetMethod(
            "BuildTerminalCommandResponsePayload",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(method);

        var payload = method!.Invoke(null, new object?[] { result }) as Dictionary<string, object>;
        Assert.NotNull(payload);
        return payload!;
    }

    private static (SystemCallResult Result, string SavedPath) SaveEditorContentInternal(
        WorldRuntime world,
        string nodeId,
        string userId,
        string cwd,
        string path,
        string content)
    {
        var method = typeof(WorldRuntime).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(static candidate =>
                string.Equals(candidate.Name, "SaveEditorContentInternal", StringComparison.Ordinal) &&
                candidate.GetParameters().Length == 6);

        object?[] args = { nodeId, userId, cwd, path, content, string.Empty };
        var result = method.Invoke(world, args) as SystemCallResult;
        Assert.NotNull(result);
        var savedPath = args[5] as string ?? string.Empty;
        return (result!, savedPath);
    }

    private static IReadOnlyList<MethodBase> ExtractCalledMethods(MethodInfo method)
    {
        var ilBytes = method.GetMethodBody()?.GetILAsByteArray();
        if (ilBytes is null || ilBytes.Length == 0)
        {
            return Array.Empty<MethodBase>();
        }

        var methods = new List<MethodBase>();
        for (var index = 0; index <= ilBytes.Length - 5; index++)
        {
            // call: 0x28, callvirt: 0x6F
            if (ilBytes[index] != 0x28 && ilBytes[index] != 0x6F)
            {
                continue;
            }

            var token = BitConverter.ToInt32(ilBytes, index + 1);
            try
            {
                var resolved = method.Module.ResolveMethod(token);
                if (resolved is not null)
                {
                    methods.Add(resolved);
                }
            }
            catch (ArgumentException)
            {
            }
            catch (BadImageFormatException)
            {
            }
        }

        return methods;
    }

    private static IReadOnlyList<string> ExtractStringLiterals(MethodInfo method)
    {
        var ilBytes = method.GetMethodBody()?.GetILAsByteArray();
        if (ilBytes is null || ilBytes.Length == 0)
        {
            return Array.Empty<string>();
        }

        var literals = new List<string>();
        for (var index = 0; index <= ilBytes.Length - 5; index++)
        {
            // ldstr
            if (ilBytes[index] != 0x72)
            {
                continue;
            }

            var token = BitConverter.ToInt32(ilBytes, index + 1);
            try
            {
                var value = method.Module.ResolveString(token);
                if (!string.IsNullOrEmpty(value))
                {
                    literals.Add(value);
                }
            }
            catch (ArgumentException)
            {
            }
            catch (BadImageFormatException)
            {
            }
        }

        return literals;
    }

    private static HashSet<int> GetWrappedIntrinsicIdsFromRateLimiter()
    {
        var limiterType = RequireRuntimeType("Uplink2.Runtime.MiniScript.MiniScriptIntrinsicRateLimiter");
        var wrappedCodesField = limiterType.GetField("originalCodesById", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(wrappedCodesField);

        var wrappedCodes = wrappedCodesField!.GetValue(null);
        Assert.NotNull(wrappedCodes);

        var keysProperty = wrappedCodes!.GetType().GetProperty("Keys", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(keysProperty);
        var keysObject = keysProperty!.GetValue(wrappedCodes);
        Assert.NotNull(keysObject);
        var keysEnumerable = keysObject as System.Collections.IEnumerable;
        Assert.NotNull(keysEnumerable);

        var wrappedIntrinsicIds = new HashSet<int>();
        foreach (var key in keysEnumerable!)
        {
            Assert.IsType<int>(key);
            wrappedIntrinsicIds.Add((int)key);
        }

        return wrappedIntrinsicIds;
    }

    private static void AssertIntrinsicWrapped(IReadOnlySet<int> wrappedIntrinsicIds, string intrinsicName)
    {
        var intrinsic = Intrinsic.GetByName(intrinsicName);
        Assert.NotNull(intrinsic);
        Assert.Contains(intrinsic!.id, wrappedIntrinsicIds);
    }

    private static void AssertIntrinsicNotWrapped(IReadOnlySet<int> wrappedIntrinsicIds, string intrinsicName)
    {
        var intrinsic = Intrinsic.GetByName(intrinsicName);
        Assert.NotNull(intrinsic);
        Assert.DoesNotContain(intrinsic!.id, wrappedIntrinsicIds);
    }

    private sealed record SystemCallHarness(
        WorldRuntime World,
        ServerNodeRuntime Server,
        BaseFileSystem BaseFileSystem,
        object Processor,
        string UserId,
        string Cwd);
}
