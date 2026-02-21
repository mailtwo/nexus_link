using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
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
        harness.BaseFileSystem.AddFile("/opt/bin/tool", "noop", fileKind: VfsFileKind.ExecutableHardcode);

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
        harness.BaseFileSystem.AddFile("/opt/bin/pwd", "noop", fileKind: VfsFileKind.ExecutableHardcode);

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
        harness.BaseFileSystem.AddFile("/opt/bin/tool", "noop", fileKind: VfsFileKind.ExecutableHardcode);

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
        harness.BaseFileSystem.AddFile("/home/guest/tool", "unregistered_exec_id", fileKind: VfsFileKind.ExecutableHardcode);
        harness.BaseFileSystem.AddFile("/opt/bin/tool", "noop", fileKind: VfsFileKind.ExecutableHardcode);

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
        harness.BaseFileSystem.AddFile("/opt/bin/tool", "noop", fileKind: VfsFileKind.ExecutableHardcode);

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
        harness.BaseFileSystem.AddFile("/home/guest/bin/tool", "noop", fileKind: VfsFileKind.ExecutableHardcode);

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

        harness.BaseFileSystem.AddFile("/opt/bin/tool", "noop", fileKind: VfsFileKind.ExecutableHardcode);
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
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "miniscript", fileKind: VfsFileKind.ExecutableHardcode);

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
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "miniscript", fileKind: VfsFileKind.ExecutableHardcode);

        var result = Execute(harness, "miniscript /scripts/missing.ms");

        Assert.False(result.Ok);
        Assert.Equal(SystemCallErrorCode.NotFound, result.Code);
    }

    /// <summary>Ensures miniscript returns not-file when the script path points to a directory.</summary>
    [Fact]
    public void Execute_Miniscript_ReturnsNotFileWhenTargetIsDirectory()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "miniscript", fileKind: VfsFileKind.ExecutableHardcode);
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
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "miniscript", fileKind: VfsFileKind.ExecutableHardcode);
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
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        harness.BaseFileSystem.AddFile("/scripts/hello.ms", "print \"Hello world!\"", fileKind: VfsFileKind.Text);

        var result = Execute(harness, "miniscript /scripts/hello.ms");

        Assert.True(result.Ok);
        Assert.Equal(SystemCallErrorCode.None, result.Code);
        Assert.Contains(result.Lines, static line => line.Contains("Hello world!", StringComparison.Ordinal));
    }

    private static SystemCallHarness CreateHarness(
        bool includeVfsModule,
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

        var world = CreateHeadlessWorld(worldDebugOption, server);
        var modules = includeVfsModule
            ? new[] { CreateInternalInstance("Uplink2.Runtime.Syscalls.VfsSystemCallModule", new object?[] { false }) }
            : Array.Empty<object>();
        var processor = CreateSystemCallProcessor(world, modules);
        return new SystemCallHarness(world, server, baseFileSystem, processor, "guest", cwd);
    }

    private static WorldRuntime CreateHeadlessWorld(bool debugOption, ServerNodeRuntime server)
    {
        var world = (WorldRuntime)RuntimeHelpers.GetUninitializedObject(typeof(WorldRuntime));
        SetAutoPropertyBackingField(world, "DebugOption", debugOption);
        SetAutoPropertyBackingField(
            world,
            "ServerList",
            new Dictionary<string, ServerNodeRuntime>(StringComparer.Ordinal)
            {
                [server.NodeId] = server,
            });
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

    private static SystemCallResult Execute(SystemCallHarness harness, string commandLine)
    {
        var executeMethod = harness.Processor.GetType().GetMethod("Execute", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(executeMethod);
        var request = new SystemCallRequest
        {
            NodeId = harness.Server.NodeId,
            UserKey = harness.UserKey,
            Cwd = harness.Cwd,
            CommandLine = commandLine,
        };

        var result = executeMethod!.Invoke(harness.Processor, new object?[] { request }) as SystemCallResult;
        Assert.NotNull(result);
        return result!;
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

    private sealed record SystemCallHarness(
        WorldRuntime World,
        ServerNodeRuntime Server,
        BaseFileSystem BaseFileSystem,
        object Processor,
        string UserKey,
        string Cwd);
}
