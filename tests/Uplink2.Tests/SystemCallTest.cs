using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
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

    /// <summary>Ensures ssh.connect intrinsic returns a session DTO and ssh.disconnect is idempotent.</summary>
    [Fact]
    public void Execute_Miniscript_SshConnect_ReturnsSessionDto_AndDisconnectIsIdempotent()
    {
        var harness = CreateHarness(includeVfsModule: true);
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "miniscript", fileKind: VfsFileKind.ExecutableHardcode);
        var remote = AddRemoteServer(harness, "node-2", "remote", "10.0.1.20", AuthMode.Static, "pw");
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
        harness.BaseFileSystem.AddFile("/opt/bin/miniscript", "miniscript", fileKind: VfsFileKind.ExecutableHardcode);
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

        var cwdResult = SystemCallResult.Success(nextCwd: "/work");
        var cwdPayload = BuildTerminalCommandResponsePayload(cwdResult);
        Assert.Equal("/work", cwdPayload["nextCwd"]);
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
            applyUsers!.Invoke(null, new object?[] { server, users, "node-dup" }));
        Assert.IsType<InvalidDataException>(ex.InnerException);
        Assert.Contains("node-dup", ex.InnerException!.Message, StringComparison.Ordinal);
        Assert.Contains("root", ex.InnerException.Message, StringComparison.Ordinal);
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

    private static WorldRuntime CreateHeadlessWorld(bool debugOption, params ServerNodeRuntime[] servers)
    {
        Assert.NotEmpty(servers);

        var world = (WorldRuntime)RuntimeHelpers.GetUninitializedObject(typeof(WorldRuntime));
        SetAutoPropertyBackingField(world, "DebugOption", debugOption);
        SetAutoPropertyBackingField(world, "ScenarioFlags", new Dictionary<string, object>(StringComparer.Ordinal));

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
