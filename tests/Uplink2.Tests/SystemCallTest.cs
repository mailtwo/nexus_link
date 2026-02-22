using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Uplink2.Blueprint;
using Uplink2.Runtime;
using Uplink2.Runtime.Syscalls;
using Uplink2.Vfs;
using Xunit;

#nullable enable

namespace Uplink2.Tests;

/// <summary>Unit tests for SystemCallProcessor command dispatch and program fallback contracts.</summary>
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

    /// <summary>Ensures miniscript executable enforces argument count contract.</summary>
    [Fact]
    public void Execute_Miniscript_RequiresSingleArgument()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "exec:miniscript", fileKind: VfsFileKind.ExecutableHardcode);

        var result = Execute(harness, "miniscript");

        Assert.False(result.Ok);
        Assert.Equal(SystemCallErrorCode.InvalidArgs, result.Code);
        Assert.Single(result.Lines);
        Assert.Contains("usage: miniscript <script>", result.Lines[0], StringComparison.Ordinal);
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
        Assert.Empty(DrainQueuedGameEvents(harness.World));
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
    [InlineData(AuthMode.Otp)]
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

    /// <summary>Ensures scan prints current-subnet to neighbor IP list with aligned continuation lines.</summary>
    [Fact]
    public void Execute_Scan_PrintsAlignedNeighborIps()
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
        AddRemoteServer(harness, "node-3", "remote-b", "10.1.0.30", AuthMode.Static, "pw", netId: "alpha");
        harness.Server.LanNeighbors.Add("node-3");
        harness.Server.LanNeighbors.Add("node-2");

        SetAutoPropertyBackingField(harness.World, "PlayerWorkstationServer", remoteA);

        var result = Execute(harness, "scan", terminalSessionId: "ts-scan");

        Assert.True(result.Ok);
        Assert.Equal(SystemCallErrorCode.None, result.Code);
        Assert.Equal(2, result.Lines.Count);
        Assert.Equal("10.1.0.10 - 10.1.0.20", result.Lines[0]);
        Assert.Equal(new string(' ', "10.1.0.10 - ".Length) + "10.1.0.30", result.Lines[1]);
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

        var disconnect = Execute(harness, "disconnect", terminalSessionId: "ts-disconnect");

        Assert.True(disconnect.Ok);
        Assert.Equal("/work", disconnect.NextCwd);
        Assert.Empty(remote.Sessions);
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

        var transitionResult = SystemCallResult.Success(nextCwd: string.Empty, data: transition);
        var transitionPayload = BuildTerminalCommandResponsePayload(transitionResult);

        Assert.Equal("node-2", transitionPayload["nextNodeId"]);
        Assert.Equal("guest", transitionPayload["nextUserId"]);
        Assert.False(transitionPayload.ContainsKey("nextUserKey"));
        Assert.Equal("guest", transitionPayload["nextPromptUser"]);
        Assert.Equal("remote", transitionPayload["nextPromptHost"]);
        Assert.Equal("/", transitionPayload["nextCwd"]);
        Assert.Equal(false, transitionPayload["openEditor"]);
        Assert.Equal(string.Empty, transitionPayload["editorPath"]);
        Assert.Equal(string.Empty, transitionPayload["editorContent"]);
        Assert.Equal(false, transitionPayload["editorReadOnly"]);
        Assert.Equal("text", transitionPayload["editorDisplayMode"]);
        Assert.Equal(false, transitionPayload["editorPathExists"]);

        var cwdResult = SystemCallResult.Success(nextCwd: "/work");
        var cwdPayload = BuildTerminalCommandResponsePayload(cwdResult);
        Assert.Equal("/work", cwdPayload["nextCwd"]);
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

    private static SystemCallHarness CreateHarness(
        bool includeVfsModule,
        bool includeConnectModule = false,
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

    private sealed record SystemCallHarness(
        WorldRuntime World,
        ServerNodeRuntime Server,
        BaseFileSystem BaseFileSystem,
        object Processor,
        string UserId,
        string Cwd);
}
