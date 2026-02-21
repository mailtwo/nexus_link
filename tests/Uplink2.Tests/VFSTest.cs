using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Uplink2.Runtime;
using Uplink2.Runtime.Syscalls;
using Uplink2.Vfs;
using Xunit;

#nullable enable

namespace Uplink2.Tests;

/// <summary>Unit tests for VFS merge, tombstone, and path behavior.</summary>
public sealed class VFSTest
{
    /// <summary>Normalizes absolute/relative paths with dot segment handling.</summary>
    [Theory]
    [InlineData("/", ".", "/")]
    [InlineData("/", "..", "/")]
    [InlineData("/home/user", "../bin//tool", "/home/bin/tool")]
    [InlineData("/", "./tools/../script.ms", "/script.ms")]
    [InlineData("/a/b", "../../..", "/")]
    [InlineData("/a/b", "", "/a/b")]
    [InlineData("/", "///opt//bin///x", "/opt/bin/x")]
    public void NormalizePath_HandlesDotSegments(string cwd, string inputPath, string expected)
    {
        var actual = BaseFileSystem.NormalizePath(cwd, inputPath);
        Assert.Equal(expected, actual);
    }

    /// <summary>Checks helper flags for executable vs text file classifications.</summary>
    [Fact]
    public void VfsEntryMeta_ClassificationFlags_AreConsistent()
    {
        var text = VfsEntryMeta.CreateFile("text", 1, VfsFileKind.Text);
        var scriptExec = VfsEntryMeta.CreateFile("scriptExec", 1, VfsFileKind.ExecutableScript);
        var hardcodeExec = VfsEntryMeta.CreateFile("hardcodeExec", 1, VfsFileKind.ExecutableHardcode);

        Assert.True(text.IsReadableTextContent());
        Assert.False(text.IsDirectExecutable());
        Assert.False(text.IsBinaryLikeExecutable());

        Assert.True(scriptExec.IsDirectExecutable());
        Assert.True(scriptExec.IsBinaryLikeExecutable());
        Assert.False(scriptExec.IsReadableTextContent());

        Assert.True(hardcodeExec.IsDirectExecutable());
        Assert.True(hardcodeExec.IsBinaryLikeExecutable());
        Assert.False(hardcodeExec.IsReadableTextContent());
    }

    /// <summary>Ensures merged resolution order is tombstone then overlay then base.</summary>
    [Fact]
    public void ResolveEntry_PrioritizesTombstoneOverOverlayAndBase()
    {
        var (_, baseFileSystem, overlay) = CreateOverlay();
        baseFileSystem.AddFile("/etc/motd", "base");
        overlay.WriteFile("/etc/motd", "overlay");

        Assert.True(overlay.TryReadFileText("/etc/motd", out var beforeTombstoneContent));
        Assert.Equal("overlay", beforeTombstoneContent);

        overlay.AddTombstone("/etc/motd");

        Assert.False(overlay.TryResolveEntry("/etc/motd", out _));
        Assert.False(overlay.TryReadFileText("/etc/motd", out _));
        Assert.DoesNotContain("motd", overlay.ListChildren("/etc"));
    }

    /// <summary>Ensures tombstoning an overlay-only file removes overlay entry state.</summary>
    [Fact]
    public void AddTombstone_RemovesOverlayOnlyEntry()
    {
        var (_, baseFileSystem, overlay) = CreateOverlay();
        baseFileSystem.AddDirectory("/tmp");
        overlay.WriteFile("/tmp/note.txt", "hello");
        Assert.True(overlay.OverlayEntries.ContainsKey("/tmp/note.txt"));

        overlay.AddTombstone("/tmp/note.txt");

        Assert.False(overlay.OverlayEntries.ContainsKey("/tmp/note.txt"));
        Assert.False(overlay.TryResolveEntry("/tmp/note.txt", out _));
    }

    /// <summary>Ensures directory tombstones hide all descendant paths.</summary>
    [Fact]
    public void AddTombstone_DirectoryHidesDescendants()
    {
        var (_, baseFileSystem, overlay) = CreateOverlay();
        baseFileSystem.AddFile("/a/b/c.txt", "base");

        overlay.AddTombstone("/a");

        Assert.False(overlay.TryResolveEntry("/a", out _));
        Assert.False(overlay.TryResolveEntry("/a/b/c.txt", out _));
        Assert.DoesNotContain("a", overlay.ListChildren("/"));
    }

    /// <summary>Ensures dir-delta entry compacts away after overlay create/delete cycle.</summary>
    [Fact]
    public void DirDelta_BecomesNeutral_AfterCreateAndDeleteOverlayFile()
    {
        var (_, baseFileSystem, overlay) = CreateOverlay();
        baseFileSystem.AddDirectory("/tmp");

        overlay.WriteFile("/tmp/session.log", "1");
        Assert.True(overlay.OverlayDir.ContainsKey("/tmp"));

        overlay.AddTombstone("/tmp/session.log");

        Assert.False(overlay.OverlayDir.ContainsKey("/tmp"));
    }

    /// <summary>Ensures dir-delta entry compacts away after remove/restore of base child.</summary>
    [Fact]
    public void DirDelta_BecomesNeutral_AfterRemovingAndRestoringBaseFile()
    {
        var (_, baseFileSystem, overlay) = CreateOverlay();
        baseFileSystem.AddFile("/var/log/app.log", "base");

        overlay.AddTombstone("/var/log/app.log");
        Assert.True(overlay.OverlayDir.ContainsKey("/var/log"));

        overlay.WriteFile("/var/log/app.log", "overlay");

        Assert.False(overlay.OverlayDir.ContainsKey("/var/log"));
        Assert.True(overlay.TryReadFileText("/var/log/app.log", out var restoredContent));
        Assert.Equal("overlay", restoredContent);
    }

    /// <summary>Ensures overlay file write overrides base content and file kind.</summary>
    [Fact]
    public void WriteFile_OverridesBaseContentAndFileKind()
    {
        var (_, baseFileSystem, overlay) = CreateOverlay();
        baseFileSystem.AddFile("/opt/app/config.txt", "base", fileKind: VfsFileKind.Text);

        overlay.WriteFile("/opt/app/config.txt", "noop", fileKind: VfsFileKind.ExecutableHardcode);

        Assert.True(overlay.TryResolveEntry("/opt/app/config.txt", out var resolvedEntry));
        Assert.Equal(VfsFileKind.ExecutableHardcode, resolvedEntry.FileKind);
        Assert.True(overlay.TryReadFileText("/opt/app/config.txt", out var content));
        Assert.Equal("noop", content);
    }

    /// <summary>Ensures blob refcount and payload ownership update correctly on overwrite.</summary>
    [Fact]
    public void WriteFile_ReleasesPreviousBlobOnOverwrite()
    {
        var (blobStore, baseFileSystem, overlay) = CreateOverlay();
        baseFileSystem.AddDirectory("/tmp");

        overlay.WriteFile("/tmp/data.txt", "first");
        var firstContentId = overlay.OverlayEntries["/tmp/data.txt"].ContentId;
        Assert.Equal(1, blobStore.GetRefCount(firstContentId));

        overlay.WriteFile("/tmp/data.txt", "second");
        var secondContentId = overlay.OverlayEntries["/tmp/data.txt"].ContentId;

        Assert.NotEqual(firstContentId, secondContentId);
        Assert.Equal(0, blobStore.GetRefCount(firstContentId));
        Assert.False(blobStore.TryGet(firstContentId, out _));
        Assert.Equal(1, blobStore.GetRefCount(secondContentId));
    }

    /// <summary>Ensures creating a directory under a missing parent fails fast.</summary>
    [Fact]
    public void AddDirectory_ThrowsWhenParentDoesNotExist()
    {
        var (_, _, overlay) = CreateOverlay();
        Assert.Throws<InvalidOperationException>(() => overlay.AddDirectory("/missing/child"));
    }

    /// <summary>Ensures writing a file under a missing parent fails fast.</summary>
    [Fact]
    public void WriteFile_ThrowsWhenParentDoesNotExist()
    {
        var (_, _, overlay) = CreateOverlay();
        Assert.Throws<InvalidOperationException>(() => overlay.WriteFile("/missing/file.txt", "x"));
    }

    /// <summary>Ensures root path cannot be tombstoned.</summary>
    [Fact]
    public void AddTombstone_ThrowsForRootPath()
    {
        var (_, _, overlay) = CreateOverlay();
        Assert.Throws<InvalidOperationException>(() => overlay.AddTombstone("/"));
    }

    /// <summary>Ensures executable and miniscript-source checks follow file-kind rules.</summary>
    [Fact]
    public void ExecutionCapabilityMethods_RespectFileKindContracts()
    {
        var (blobStore, baseFileSystem, _) = CreateOverlay();
        baseFileSystem.AddDirectory("/opt/bin");
        baseFileSystem.AddDirectory("/scripts");
        baseFileSystem.AddFile("/opt/bin/miniscript", "miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        baseFileSystem.AddFile("/scripts/hello.ms", "print \"hello\"", fileKind: VfsFileKind.Text);
        baseFileSystem.AddFile("/scripts/job.ms", "print \"job\"", fileKind: VfsFileKind.ExecutableScript);

        Assert.True(baseFileSystem.CanExecuteFile("/opt/bin/miniscript"));
        Assert.True(baseFileSystem.CanUseAsMiniScriptSource("/scripts/hello.ms"));
        Assert.False(baseFileSystem.CanUseAsMiniScriptSource("/scripts/job.ms"));
        Assert.True(baseFileSystem.CanRunMiniScript("/opt/bin/miniscript", "/scripts/hello.ms"));
        Assert.False(baseFileSystem.CanRunMiniScript("/opt/bin/miniscript", "/scripts/job.ms"));

        // Overlay rewrite should immediately affect merged miniscript source checks.
        var merged = new OverlayFileSystem(baseFileSystem, blobStore);
        merged.WriteFile("/scripts/hello.ms", "print \"exec\"", fileKind: VfsFileKind.ExecutableScript);
        Assert.False(merged.CanUseAsMiniScriptSource("/scripts/hello.ms"));
    }

    /// <summary>Ensures cat blocks executable file kinds with executable-read error.</summary>
    [Theory]
    [InlineData(VfsFileKind.ExecutableScript, "print \"hello\"")]
    [InlineData(VfsFileKind.ExecutableHardcode, "noop")]
    public void Cat_BlocksExecutableFileKinds(VfsFileKind fileKind, string content)
    {
        var server = CreateServerForSystemCallTests("node-cat", PrivilegeConfig.FullAccess());
        server.DiskOverlay.WriteFile("/blocked.bin", content, fileKind: fileKind);
        var context = CreateExecutionContext(server, userKey: "guest", cwd: "/");
        var catHandler = CreateInternalInstance("Uplink2.Runtime.Syscalls.CatCommandHandler");
        var execute = catHandler.GetType().GetMethod("Execute", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(execute);
        var result = execute!.Invoke(catHandler, new object?[] { context, new List<string> { "/blocked.bin" } }) as SystemCallResult;

        Assert.NotNull(result);
        Assert.False(result!.Ok);
        Assert.Equal(SystemCallErrorCode.InvalidArgs, result.Code);
        Assert.Single(result.Lines);
        Assert.Contains("cannot read executable file: /blocked.bin", result.Lines[0], StringComparison.Ordinal);
    }

    /// <summary>Ensures DEBUG_miniscript is not registered when VFS debug module option is disabled.</summary>
    [Fact]
    public void DebugMiniscript_IsNotRegistered_WhenModuleDebugOptionIsDisabled()
    {
        var handlers = BuildRegisteredHandlerMap(enableDebugCommands: false);
        Assert.False(handlers.Contains("DEBUG_miniscript"));
    }

    /// <summary>Ensures DEBUG_miniscript is registered when VFS debug module option is enabled.</summary>
    [Fact]
    public void DebugMiniscript_IsRegistered_WhenModuleDebugOptionIsEnabled()
    {
        var handlers = BuildRegisteredHandlerMap(enableDebugCommands: true);
        Assert.True(handlers.Contains("DEBUG_miniscript"));
    }

    private static (BlobStore BlobStore, BaseFileSystem BaseFileSystem, OverlayFileSystem OverlayFileSystem) CreateOverlay()
    {
        var blobStore = new BlobStore();
        var baseFileSystem = new BaseFileSystem(blobStore);
        var overlayFileSystem = new OverlayFileSystem(baseFileSystem, blobStore);
        return (blobStore, baseFileSystem, overlayFileSystem);
    }

    private static ServerNodeRuntime CreateServerForSystemCallTests(string nodeId, PrivilegeConfig privilege)
    {
        var blobStore = new BlobStore();
        var baseFileSystem = new BaseFileSystem(blobStore);
        var server = new ServerNodeRuntime(nodeId, nodeId, ServerRole.Terminal, baseFileSystem, blobStore);
        server.Users["guest"] = new UserConfig
        {
            UserId = "guest",
            AuthMode = AuthMode.None,
            Privilege = privilege,
        };

        return server;
    }

    private static IDictionary BuildRegisteredHandlerMap(bool enableDebugCommands)
    {
        var registry = CreateInternalInstance("Uplink2.Runtime.Syscalls.SystemCallRegistry");
        var module = CreateInternalInstance(
            "Uplink2.Runtime.Syscalls.VfsSystemCallModule",
            new object?[] { enableDebugCommands });

        var register = module.GetType().GetMethod("Register", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(register);
        register!.Invoke(module, new[] { registry });

        var handlersField = registry.GetType().GetField("handlers", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(handlersField);
        var handlers = handlersField!.GetValue(registry) as IDictionary;
        Assert.NotNull(handlers);
        return handlers!;
    }

    private static object CreateExecutionContext(ServerNodeRuntime server, string userKey, string cwd)
    {
        var contextType = RequireType("Uplink2.Runtime.Syscalls.SystemCallExecutionContext");
        var worldType = RequireType("Uplink2.Runtime.WorldRuntime");
        Assert.True(server.Users.TryGetValue(userKey, out var user));

        var constructor = contextType.GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: new[]
            {
                worldType,
                typeof(ServerNodeRuntime),
                typeof(UserConfig),
                typeof(string),
                typeof(string),
                typeof(string),
                typeof(string),
            },
            modifiers: null);

        Assert.NotNull(constructor);
        return constructor!.Invoke(new object?[]
        {
            null,
            server,
            user!,
            server.NodeId,
            userKey,
            cwd,
            string.Empty,
        });
    }

    private static object CreateInternalInstance(string fullTypeName, object?[]? args = null)
    {
        var type = RequireType(fullTypeName);
        var instance = Activator.CreateInstance(
            type,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: args,
            culture: null);
        Assert.NotNull(instance);
        return instance!;
    }

    private static Type RequireType(string fullTypeName)
    {
        var type = typeof(SystemCallResult).Assembly.GetType(fullTypeName);
        Assert.NotNull(type);
        return type!;
    }
}
