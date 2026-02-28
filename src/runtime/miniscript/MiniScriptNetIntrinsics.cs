using Miniscript;
using System;
using System.Collections.Generic;
using Uplink2.Runtime.Syscalls;
using static Uplink2.Runtime.MiniScript.MiniScriptFsIntrinsics;
using static Uplink2.Runtime.MiniScript.MiniScriptSshIntrinsics;

#nullable enable

namespace Uplink2.Runtime.MiniScript;

internal static class MiniScriptNetIntrinsics
{
    private const string NetInterfacesIntrinsicName = "uplink_net_interfaces";
    private const string NetScanIntrinsicName = "uplink_net_scan";
    private const string NetPortsIntrinsicName = "uplink_net_ports";
    private const string NetBannerIntrinsicName = "uplink_net_banner";
    private const string InternetNetId = "internet";
    private static readonly object registrationSync = new();
    private static bool isRegistered;

    internal static void EnsureRegistered()
    {
        lock (registrationSync)
        {
            if (isRegistered)
            {
                return;
            }

            NetInterfaces();
            NetScan();
            NetPorts();
            NetBanner();
            isRegistered = true;
        }
    }

    /// <summary>인터프리터에 net 모듈 전역 API를 주입합니다.</summary>
    /// <remarks>
    /// MiniScript: <c>net.interfaces([sessionOrRoute])</c>, <c>net.scan([sessionOrRoute], netId=null)</c>, <c>net.ports([sessionOrRoute], hostOrIp, opts?)</c>, <c>net.banner([sessionOrRoute], hostOrIp, port)</c>.
    /// 각 API는 공통 ResultMap(<c>ok/code/err/cost/trace</c>) 규약을 따르며 네트워크 노출(exposure) 규칙을 endpoint 컨텍스트 기준으로 평가합니다.
    /// net 모듈 API는 대상 endpoint 사용자 execute/read 권한 조건을 함수별로 검사합니다.
    /// See: <see href="/api/net.html#module-net">Manual</see>.
    /// </remarks>
    /// <param name="interpreter">net 모듈 전역을 주입할 대상 인터프리터입니다.</param>
    /// <param name="moduleState">session/route 해석과 실행 컨텍스트를 포함한 모듈 상태입니다.</param>
    internal static void InjectNetModule(Interpreter interpreter, MiniScriptSshIntrinsics.SshModuleState moduleState)
    {
        EnsureRegistered();
        var netModule = new ValMap
        {
            userData = moduleState,
        };
        netModule["interfaces"] = Intrinsic.GetByName(NetInterfacesIntrinsicName).GetFunc();
        netModule["scan"] = Intrinsic.GetByName(NetScanIntrinsicName).GetFunc();
        netModule["ports"] = Intrinsic.GetByName(NetPortsIntrinsicName).GetFunc();
        netModule["banner"] = Intrinsic.GetByName(NetBannerIntrinsicName).GetFunc();
        interpreter.SetGlobalValue("net", netModule);
    }

    /// <summary><c>net.interfaces</c>는 기준 endpoint의 네트워크 인터페이스 목록(<c>netId/localIp</c>)을 반환합니다.</summary>
    /// <remarks>
    /// <para><b>MiniScript</b></para>
    /// <para><c>r = net.interfaces([sessionOrRoute])</c></para>
    /// <para><b>Parameters</b></para>
    /// <list type="bullet">
    /// <item><description><c>sessionOrRoute</c>(선택): <c>sshSession</c> 또는 <c>sshRoute</c>를 주면 해당 endpoint 기준으로 조회합니다. 생략하면 현재 실행 컨텍스트 endpoint를 사용합니다.</description></item>
    /// </list>
    /// <para><b>Returns</b></para>
    /// <list type="bullet">
    /// <item><description>성공: <c>{ ok:1, code:"OK", err:null, interfaces:[{ netId, localIp }, ...] }</c></description></item>
    /// <item><description>실패: <c>{ ok:0, code:"ERR_*", err:string }</c></description></item>
    /// </list>
    /// <para><b>Note</b></para>
    /// <list type="bullet">
    /// <item><description>대상 endpoint 사용자의 <c>execute</c> 권한이 필요합니다.</description></item>
    /// <item><description><c>sessionOrRoute</c> 인자가 맵이지만 세션/라우트 형태가 아니면 <c>ERR_INVALID_ARGS</c>를 반환합니다.</description></item>
    /// </list>
    /// <para><b>See</b>: <see href="/api/net.html#netinterfaces">Manual</see>.</para>
    /// </remarks>
    private static void NetInterfaces()
    {
        if (Intrinsic.GetByName(NetInterfacesIntrinsicName) is not null)
        {
            return;
        }

        var intrinsic = Intrinsic.Create(NetInterfacesIntrinsicName);
        intrinsic.AddParam("arg1");
        intrinsic.code = (context, partialResult) =>
        {
            if (!TryGetExecutionState(context, out var state) || state.ExecutionContext is null)
            {
                return new Intrinsic.Result(
                    CreateNetFailureMap(
                        SystemCallErrorCode.InternalError,
                        "net.interfaces is unavailable in this execution context."));
            }

            if (!TryParseNetInterfacesArguments(context, out var sessionOrRouteMap, out var parseError))
            {
                return new Intrinsic.Result(CreateNetFailureMap(SystemCallErrorCode.InvalidArgs, parseError));
            }

            var executionContext = state.ExecutionContext;
            if (!TryRunWorldAction(
                    state,
                    () =>
                    {
                        if (!TryResolveFsEndpoint(
                                state,
                                executionContext,
                                sessionOrRouteMap,
                                out var endpoint,
                                out var endpointUser,
                                out var endpointError))
                        {
                            return CreateNetFailureMap(SystemCallErrorCode.InvalidArgs, endpointError);
                        }

                        if (!endpointUser.Privilege.Execute)
                        {
                            return CreateNetFailureMap(SystemCallErrorCode.PermissionDenied, "permission denied: net.interfaces");
                        }

                        var interfaces = CollectEndpointInterfaces(endpoint.Server);
                        var result = CreateNetSuccessMap();
                        result["interfaces"] = interfaces;
                        return result;
                    },
                    out var interfacesResult,
                    out var queueError))
            {
                return new Intrinsic.Result(
                    CreateNetFailureMap(
                        SystemCallErrorCode.InternalError,
                        queueError));
            }

            return new Intrinsic.Result(interfacesResult);
        };
    }

    /// <summary><c>net.scan</c>은 네트워크 이웃과 탐지된 IP를 스캔해 인터페이스별 가시성 정보를 반환합니다.</summary>
    /// <remarks>
    /// <para><b>MiniScript</b></para>
    /// <para><c>r = net.scan([sessionOrRoute], netId=null)</c></para>
    /// <para><b>Parameters</b></para>
    /// <list type="bullet">
    /// <item><description><c>sessionOrRoute</c>(선택): <c>sshSession</c> 또는 <c>sshRoute</c> endpoint를 스캔 기준으로 사용합니다.</description></item>
    /// <item><description><c>netId</c>(선택): 생략/null이면 모든 스캔 가능한 인터페이스를 대상으로 하며, 문자열이면 해당 netId만 스캔합니다.</description></item>
    /// </list>
    /// <para><b>Returns</b></para>
    /// <list type="bullet">
    /// <item><description>성공: <c>{ ok:1, code:"OK", err:null, interfaces:[{ netId, localIp, neighbors }, ...], ips:[...] }</c></description></item>
    /// <item><description>실패: <c>{ ok:0, code:"ERR_*", err:string }</c></description></item>
    /// </list>
    /// <para><b>Note</b></para>
    /// <list type="bullet">
    /// <item><description><c>net.scan("lan")</c> 형태는 지원하지 않으며 <c>ERR_INVALID_ARGS</c>를 반환합니다.</description></item>
    /// <item><description>스캔 대상은 <c>internet</c>이 아닌 인터페이스 중 <c>localIp</c>가 있는 항목만 포함됩니다.</description></item>
    /// <item><description>대상 endpoint 사용자의 <c>execute</c> 권한이 필요합니다.</description></item>
    /// </list>
    /// <para><b>See</b>: <see href="/api/net.html#netscan">Manual</see>.</para>
    /// </remarks>
    private static void NetScan()
    {
        if (Intrinsic.GetByName(NetScanIntrinsicName) is not null)
        {
            return;
        }

        var intrinsic = Intrinsic.Create(NetScanIntrinsicName);
        intrinsic.AddParam("arg1");
        intrinsic.AddParam("arg2");
        intrinsic.code = (context, partialResult) =>
        {
            if (!TryGetExecutionState(context, out var state) || state.ExecutionContext is null)
            {
                return new Intrinsic.Result(
                    CreateNetFailureMap(
                        SystemCallErrorCode.InternalError,
                        "net.scan is unavailable in this execution context."));
            }

            if (!TryParseNetScanArguments(context, out var sessionOrRouteMap, out var netIdFilter, out var parseError))
            {
                return new Intrinsic.Result(CreateNetFailureMap(SystemCallErrorCode.InvalidArgs, parseError));
            }

            if (!string.IsNullOrEmpty(netIdFilter) &&
                string.Equals(netIdFilter, "lan", StringComparison.OrdinalIgnoreCase))
            {
                return new Intrinsic.Result(
                    CreateNetFailureMap(
                        SystemCallErrorCode.InvalidArgs,
                        "netId must be omitted/null or a concrete interface id (\"lan\" is not supported)."));
            }

            var executionContext = state.ExecutionContext;
            if (!TryRunWorldAction(
                    state,
                    () =>
                    {
                        if (!TryResolveFsEndpoint(
                                state,
                                executionContext,
                                sessionOrRouteMap,
                                out var endpoint,
                                out var endpointUser,
                                out var endpointError))
                        {
                            return CreateNetFailureMap(SystemCallErrorCode.InvalidArgs, endpointError);
                        }

                        if (!endpointUser.Privilege.Execute)
                        {
                            return CreateNetFailureMap(SystemCallErrorCode.PermissionDenied, "permission denied: net.scan");
                        }

                        if (!TryBuildNetScanData(
                                executionContext.World,
                                endpoint.Server,
                                netIdFilter,
                                out var interfaces,
                                out var ips,
                                out var scanErrorCode,
                                out var scanErrorMessage))
                        {
                            return CreateNetFailureMap(scanErrorCode, scanErrorMessage);
                        }

                        var result = CreateNetSuccessMap();
                        result["interfaces"] = interfaces;
                        result["ips"] = ips;
                        return result;
                    },
                    out var scanResult,
                    out var queueError))
            {
                return new Intrinsic.Result(
                    CreateNetFailureMap(
                        SystemCallErrorCode.InternalError,
                        queueError));
            }

            return new Intrinsic.Result(scanResult);
        };
    }

    /// <summary><c>net.ports</c>는 대상 호스트에서 노출 규칙을 통과한 서비스 포트 목록을 반환합니다.</summary>
    /// <remarks>
    /// <para><b>MiniScript</b></para>
    /// <para><c>r = net.ports([sessionOrRoute], hostOrIp, opts?)</c></para>
    /// <para><b>Parameters</b></para>
    /// <list type="bullet">
    /// <item><description><c>sessionOrRoute</c>(선택): source endpoint를 지정하는 <c>sshSession</c> 또는 <c>sshRoute</c>입니다.</description></item>
    /// <item><description><c>hostOrIp</c>: 조회 대상 호스트/IP입니다.</description></item>
    /// <item><description><c>opts</c>(선택): 현재 구현에서는 키를 지원하지 않습니다(빈 맵 또는 생략만 허용).</description></item>
    /// </list>
    /// <para><b>Returns</b></para>
    /// <list type="bullet">
    /// <item><description>성공: <c>{ ok:1, code:"OK", err:null, ports:[{ port, portType, exposure }, ...] }</c></description></item>
    /// <item><description>실패: <c>{ ok:0, code:"ERR_*", err:string }</c></description></item>
    /// </list>
    /// <para><b>Note</b></para>
    /// <list type="bullet">
    /// <item><description>노출 규칙(exposure)을 통과하지 못한 포트는 에러로 실패시키지 않고 결과 목록에서 제외합니다.</description></item>
    /// <item><description>서비스가 없는 포트(<c>portType == none</c>)도 결과에서 제외합니다.</description></item>
    /// <item><description><c>opts</c>에 어떤 키라도 포함되면 <c>ERR_INVALID_ARGS</c>를 반환합니다.</description></item>
    /// </list>
    /// <para><b>See</b>: <see href="/api/net.html#netports">Manual</see>.</para>
    /// </remarks>
    private static void NetPorts()
    {
        if (Intrinsic.GetByName(NetPortsIntrinsicName) is not null)
        {
            return;
        }

        var intrinsic = Intrinsic.Create(NetPortsIntrinsicName);
        intrinsic.AddParam("arg1");
        intrinsic.AddParam("arg2");
        intrinsic.AddParam("opts");
        intrinsic.code = (context, partialResult) =>
        {
            if (!TryGetExecutionState(context, out var state) || state.ExecutionContext is null)
            {
                return new Intrinsic.Result(
                    CreateNetFailureMap(
                        SystemCallErrorCode.InternalError,
                        "net.ports is unavailable in this execution context."));
            }

            if (!TryParseNetPortsArguments(context, out var sessionOrRouteMap, out var hostOrIp, out var parseError))
            {
                return new Intrinsic.Result(CreateNetFailureMap(SystemCallErrorCode.InvalidArgs, parseError));
            }

            var executionContext = state.ExecutionContext;
            if (!TryRunWorldAction(
                    state,
                    () =>
                    {
                        if (!TryResolveFsEndpoint(
                                state,
                                executionContext,
                                sessionOrRouteMap,
                                out var endpoint,
                                out _,
                                out var endpointError))
                        {
                            return CreateNetFailureMap(SystemCallErrorCode.InvalidArgs, endpointError);
                        }

                        if (!TryResolveNetTargetServer(executionContext, hostOrIp, out var targetServer, out var targetFailure))
                        {
                            return CreateNetFailureMap(targetFailure.Code, targetFailure.Message);
                        }

                        var sortedPorts = new List<int>(targetServer.Ports.Keys);
                        sortedPorts.Sort();
                        var ports = new ValList();
                        foreach (var portNumber in sortedPorts)
                        {
                            if (!targetServer.Ports.TryGetValue(portNumber, out var portConfig) ||
                                portConfig is null ||
                                portConfig.PortType == PortType.None)
                            {
                                continue;
                            }

                            if (!IsNetPortExposureAllowed(endpoint.Server, targetServer, portConfig.Exposure))
                            {
                                continue;
                            }

                            ports.values.Add(new ValMap
                            {
                                ["port"] = new ValNumber(portNumber),
                                ["portType"] = new ValString(ToPortTypeToken(portConfig.PortType)),
                                ["exposure"] = new ValString(ToExposureToken(portConfig.Exposure)),
                            });
                        }

                        var result = CreateNetSuccessMap();
                        result["ports"] = ports;
                        return result;
                    },
                    out var portsResult,
                    out var queueError))
            {
                return new Intrinsic.Result(
                    CreateNetFailureMap(
                        SystemCallErrorCode.InternalError,
                        queueError));
            }

            return new Intrinsic.Result(portsResult);
        };
    }

    /// <summary><c>net.banner</c>는 대상 포트의 서비스 배너를 조회해 식별 가능한 응답 정보를 반환합니다.</summary>
    /// <remarks>
    /// <para><b>MiniScript</b></para>
    /// <para><c>r = net.banner([sessionOrRoute], hostOrIp, port)</c></para>
    /// <para><b>Parameters</b></para>
    /// <list type="bullet">
    /// <item><description><c>sessionOrRoute</c>(선택): source endpoint를 지정하는 <c>sshSession</c> 또는 <c>sshRoute</c>입니다.</description></item>
    /// <item><description><c>hostOrIp</c>: 조회 대상 호스트/IP입니다.</description></item>
    /// <item><description><c>port</c>: 조회할 포트 번호입니다.</description></item>
    /// </list>
    /// <para><b>Returns</b></para>
    /// <list type="bullet">
    /// <item><description>성공: <c>{ ok:1, code:"OK", err:null, banner:string }</c></description></item>
    /// <item><description>실패: <c>{ ok:0, code:"ERR_*", err:string }</c></description></item>
    /// </list>
    /// <para><b>Note</b></para>
    /// <list type="bullet">
    /// <item><description>포트가 없거나 <c>portType == none</c>이면 <c>ERR_PORT_CLOSED</c>를 반환합니다.</description></item>
    /// <item><description>노출 규칙(exposure) 위반 시 <c>ERR_NET_DENIED</c>를 반환합니다.</description></item>
    /// <item><description><c>banner</c> 값은 현재 구현에서 포트 설정의 <c>ServiceId</c>를 trim한 문자열을 사용합니다.</description></item>
    /// </list>
    /// <para><b>See</b>: <see href="/api/net.html#netbanner">Manual</see>.</para>
    /// </remarks>
    private static void NetBanner()
    {
        if (Intrinsic.GetByName(NetBannerIntrinsicName) is not null)
        {
            return;
        }

        var intrinsic = Intrinsic.Create(NetBannerIntrinsicName);
        intrinsic.AddParam("arg1");
        intrinsic.AddParam("arg2");
        intrinsic.AddParam("arg3");
        intrinsic.code = (context, partialResult) =>
        {
            if (!TryGetExecutionState(context, out var state) || state.ExecutionContext is null)
            {
                return new Intrinsic.Result(
                    CreateNetFailureMap(
                        SystemCallErrorCode.InternalError,
                        "net.banner is unavailable in this execution context."));
            }

            if (!TryParseNetBannerArguments(context, out var sessionOrRouteMap, out var hostOrIp, out var port, out var parseError))
            {
                return new Intrinsic.Result(CreateNetFailureMap(SystemCallErrorCode.InvalidArgs, parseError));
            }

            var executionContext = state.ExecutionContext;
            if (!TryRunWorldAction(
                    state,
                    () =>
                    {
                        if (!TryResolveFsEndpoint(
                                state,
                                executionContext,
                                sessionOrRouteMap,
                                out var endpoint,
                                out _,
                                out var endpointError))
                        {
                            return CreateNetFailureMap(SystemCallErrorCode.InvalidArgs, endpointError);
                        }

                        if (!TryResolveNetTargetServer(executionContext, hostOrIp, out var targetServer, out var targetFailure))
                        {
                            return CreateNetFailureMap(targetFailure.Code, targetFailure.Message);
                        }

                        if (!targetServer.Ports.TryGetValue(port, out var portConfig) ||
                            portConfig is null ||
                            portConfig.PortType == PortType.None)
                        {
                            return CreateNetFailureMap(
                                SystemCallErrorCode.PortClosed,
                                $"port closed: {port}");
                        }

                        if (!IsNetPortExposureAllowed(endpoint.Server, targetServer, portConfig.Exposure))
                        {
                            return CreateNetFailureMap(
                                SystemCallErrorCode.NetDenied,
                                $"port exposure denied: {port}");
                        }

                        var result = CreateNetSuccessMap();
                        result["banner"] = new ValString((portConfig.ServiceId ?? string.Empty).Trim());
                        return result;
                    },
                    out var bannerResult,
                    out var queueError))
            {
                return new Intrinsic.Result(
                    CreateNetFailureMap(
                        SystemCallErrorCode.InternalError,
                        queueError));
            }

            return new Intrinsic.Result(bannerResult);
        };
    }

    private static bool TryParseNetInterfacesArguments(
        TAC.Context context,
        out ValMap? sessionOrRouteMap,
        out string error)
    {
        sessionOrRouteMap = null;
        error = string.Empty;

        var rawArg1 = context.GetLocal("arg1");
        if (rawArg1 is null || rawArg1 is ValNull)
        {
            return true;
        }

        if (rawArg1 is not ValMap)
        {
            error = "sessionOrRoute must be a session/route map.";
            return false;
        }

        if (!TryParseFsSessionOrRouteArgument(rawArg1, out sessionOrRouteMap, out var hasSessionOrRoute, out error))
        {
            return false;
        }

        if (!hasSessionOrRoute)
        {
            error = "sessionOrRoute must be a session/route map.";
            return false;
        }

        return true;
    }

    private static bool TryParseNetScanArguments(
        TAC.Context context,
        out ValMap? sessionOrRouteMap,
        out string netIdFilter,
        out string error)
    {
        sessionOrRouteMap = null;
        netIdFilter = string.Empty;
        error = string.Empty;

        var rawArg1 = context.GetLocal("arg1");
        var rawArg2 = context.GetLocal("arg2");
        if (!TryParseFsSessionOrRouteArgument(rawArg1, out sessionOrRouteMap, out var hasSessionOrRoute, out error))
        {
            return false;
        }

        if (hasSessionOrRoute)
        {
            return TryReadOptionalNetId(rawArg2, out netIdFilter, out error);
        }

        if (rawArg2 is not null)
        {
            error = "too many arguments.";
            return false;
        }

        return TryReadOptionalNetId(rawArg1, out netIdFilter, out error);
    }

    private static bool TryParseNetPortsArguments(
        TAC.Context context,
        out ValMap? sessionOrRouteMap,
        out string hostOrIp,
        out string error)
    {
        sessionOrRouteMap = null;
        hostOrIp = string.Empty;
        error = string.Empty;

        var rawArg1 = context.GetLocal("arg1");
        var rawArg2 = context.GetLocal("arg2");
        var rawOpts = context.GetLocal("opts");
        if (!TryParseFsSessionOrRouteArgument(rawArg1, out sessionOrRouteMap, out var hasSessionOrRoute, out error))
        {
            return false;
        }

        Value rawHost;
        ValMap? optsMap;
        if (hasSessionOrRoute)
        {
            rawHost = rawArg2;
            if (rawOpts is null)
            {
                optsMap = null;
            }
            else if (rawOpts is ValMap parsedOptsMap)
            {
                optsMap = parsedOptsMap;
            }
            else
            {
                error = "opts must be a map.";
                return false;
            }
        }
        else
        {
            rawHost = rawArg1;
            if (rawOpts is not null)
            {
                error = "opts must be passed as the second argument when sessionOrRoute is omitted.";
                return false;
            }

            if (rawArg2 is null)
            {
                optsMap = null;
            }
            else if (rawArg2 is ValMap parsedOptsMap)
            {
                optsMap = parsedOptsMap;
            }
            else
            {
                error = "opts must be a map.";
                return false;
            }
        }

        if (!TryReadNetHost(rawHost, out hostOrIp, out error))
        {
            return false;
        }

        return TryParseNetPortsOpts(optsMap, out error);
    }

    private static bool TryParseNetBannerArguments(
        TAC.Context context,
        out ValMap? sessionOrRouteMap,
        out string hostOrIp,
        out int port,
        out string error)
    {
        sessionOrRouteMap = null;
        hostOrIp = string.Empty;
        port = 0;
        error = string.Empty;

        var rawArg1 = context.GetLocal("arg1");
        var rawArg2 = context.GetLocal("arg2");
        var rawArg3 = context.GetLocal("arg3");
        if (!TryParseFsSessionOrRouteArgument(rawArg1, out sessionOrRouteMap, out var hasSessionOrRoute, out error))
        {
            return false;
        }

        Value rawHost;
        Value rawPort;
        if (hasSessionOrRoute)
        {
            rawHost = rawArg2;
            rawPort = rawArg3;
        }
        else
        {
            rawHost = rawArg1;
            rawPort = rawArg2;
            if (rawArg3 is not null)
            {
                error = "too many arguments.";
                return false;
            }
        }

        if (!TryReadNetHost(rawHost, out hostOrIp, out error))
        {
            return false;
        }

        return TryReadPort(rawPort, out port, out error);
    }

    private static bool TryReadOptionalNetId(Value rawInput, out string netId, out string error)
    {
        netId = string.Empty;
        error = string.Empty;
        if (rawInput is null || rawInput is ValNull)
        {
            return true;
        }

        if (rawInput is ValMap)
        {
            error = "netId must be a string or null.";
            return false;
        }

        netId = rawInput.ToString().Trim();
        if (string.IsNullOrWhiteSpace(netId))
        {
            error = "netId must not be empty.";
            return false;
        }

        return true;
    }

    private static bool TryReadNetHost(Value rawHost, out string hostOrIp, out string error)
    {
        hostOrIp = string.Empty;
        error = string.Empty;
        if (rawHost is null || rawHost is ValMap)
        {
            error = "hostOrIp is required.";
            return false;
        }

        hostOrIp = rawHost.ToString().Trim();
        if (string.IsNullOrWhiteSpace(hostOrIp))
        {
            error = "hostOrIp is required.";
            return false;
        }

        return true;
    }

    private static bool TryParseNetPortsOpts(ValMap? optsMap, out string error)
    {
        error = string.Empty;
        if (optsMap is null)
        {
            return true;
        }

        foreach (var key in optsMap.Keys)
        {
            var keyText = key?.ToString().Trim() ?? string.Empty;
            error = $"unsupported opts key: {keyText}";
            return false;
        }

        return true;
    }

    private static bool TryBuildNetScanData(
        WorldRuntime world,
        ServerNodeRuntime sourceServer,
        string netIdFilter,
        out ValList interfaces,
        out ValList ips,
        out SystemCallErrorCode errorCode,
        out string errorMessage)
    {
        interfaces = new ValList();
        ips = new ValList();
        errorCode = SystemCallErrorCode.None;
        errorMessage = string.Empty;

        var scannedInterfaces = CollectScannableInterfaces(sourceServer, netIdFilter);
        if (scannedInterfaces.Count == 0 && !string.IsNullOrWhiteSpace(netIdFilter))
        {
            errorCode = SystemCallErrorCode.NotFound;
            errorMessage = $"netId not found: {netIdFilter}";
            return false;
        }

        var uniqueIps = new HashSet<string>(StringComparer.Ordinal);
        var flatIps = new List<string>();
        foreach (var scannedInterface in scannedInterfaces)
        {
            var neighborIps = CollectNeighborIpsForNetId(world, sourceServer, scannedInterface.NetId);
            var neighbors = new ValList();
            foreach (var neighborIp in neighborIps)
            {
                neighbors.values.Add(new ValString(neighborIp));
                if (uniqueIps.Add(neighborIp))
                {
                    flatIps.Add(neighborIp);
                }
            }

            interfaces.values.Add(new ValMap
            {
                ["netId"] = new ValString(scannedInterface.NetId),
                ["localIp"] = new ValString(scannedInterface.LocalIp),
                ["neighbors"] = neighbors,
            });
        }

        flatIps.Sort(StringComparer.Ordinal);
        foreach (var ip in flatIps)
        {
            ips.values.Add(new ValString(ip));
        }

        return true;
    }

    private static ValList CollectEndpointInterfaces(ServerNodeRuntime sourceServer)
    {
        var endpointInterfaces = new List<ScannableInterface>();
        foreach (var iface in sourceServer.Interfaces)
        {
            if (string.IsNullOrWhiteSpace(iface.NetId) || string.IsNullOrWhiteSpace(iface.Ip))
            {
                continue;
            }

            endpointInterfaces.Add(new ScannableInterface(iface.NetId, iface.Ip));
        }

        endpointInterfaces.Sort(static (left, right) =>
        {
            var byNetId = StringComparer.Ordinal.Compare(left.NetId, right.NetId);
            if (byNetId != 0)
            {
                return byNetId;
            }

            return StringComparer.Ordinal.Compare(left.LocalIp, right.LocalIp);
        });

        var result = new ValList();
        foreach (var endpointInterface in endpointInterfaces)
        {
            result.values.Add(new ValMap
            {
                ["netId"] = new ValString(endpointInterface.NetId),
                ["localIp"] = new ValString(endpointInterface.LocalIp),
            });
        }

        return result;
    }

    private static List<ScannableInterface> CollectScannableInterfaces(ServerNodeRuntime sourceServer, string netIdFilter)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var scannedInterfaces = new List<ScannableInterface>();
        foreach (var iface in sourceServer.Interfaces)
        {
            if (string.IsNullOrWhiteSpace(iface.NetId) ||
                string.IsNullOrWhiteSpace(iface.Ip) ||
                string.Equals(iface.NetId, InternetNetId, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(netIdFilter) &&
                !string.Equals(iface.NetId, netIdFilter, StringComparison.Ordinal))
            {
                continue;
            }

            var dedupeKey = iface.NetId + "\n" + iface.Ip;
            if (!seen.Add(dedupeKey))
            {
                continue;
            }

            scannedInterfaces.Add(new ScannableInterface(iface.NetId, iface.Ip));
        }

        scannedInterfaces.Sort(static (left, right) =>
        {
            var byNetId = StringComparer.Ordinal.Compare(left.NetId, right.NetId);
            if (byNetId != 0)
            {
                return byNetId;
            }

            return StringComparer.Ordinal.Compare(left.LocalIp, right.LocalIp);
        });

        return scannedInterfaces;
    }

    private static List<string> CollectNeighborIpsForNetId(WorldRuntime world, ServerNodeRuntime sourceServer, string netId)
    {
        var neighborIps = new List<string>();
        var seenIps = new HashSet<string>(StringComparer.Ordinal);
        foreach (var neighborNodeId in sourceServer.LanNeighbors)
        {
            if (!world.TryGetServer(neighborNodeId, out var neighborServer))
            {
                continue;
            }

            if (!TryGetInterfaceIpForNetScan(neighborServer, netId, out var neighborIp) ||
                string.IsNullOrWhiteSpace(neighborIp) ||
                !seenIps.Add(neighborIp))
            {
                continue;
            }

            neighborIps.Add(neighborIp);
        }

        neighborIps.Sort(StringComparer.Ordinal);
        return neighborIps;
    }

    private static bool TryGetInterfaceIpForNetScan(ServerNodeRuntime server, string netId, out string ip)
    {
        foreach (var iface in server.Interfaces)
        {
            if (!string.Equals(iface.NetId, netId, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(iface.Ip))
            {
                continue;
            }

            ip = iface.Ip;
            return true;
        }

        ip = string.Empty;
        return false;
    }

    private readonly record struct ScannableInterface(string NetId, string LocalIp);

    private static bool IsNetPortExposureAllowed(ServerNodeRuntime source, ServerNodeRuntime target, PortExposure exposure)
    {
        return exposure switch
        {
            PortExposure.Public => true,
            PortExposure.Lan => source.SubnetMembership.Overlaps(target.SubnetMembership),
            PortExposure.Localhost => string.Equals(source.NodeId, target.NodeId, StringComparison.Ordinal),
            _ => false,
        };
    }

    private static string ToPortTypeToken(PortType portType)
    {
        return portType switch
        {
            PortType.Ssh => "ssh",
            PortType.Ftp => "ftp",
            PortType.Http => "http",
            PortType.Sql => "sql",
            _ => portType.ToString().ToLowerInvariant(),
        };
    }

    private static string ToExposureToken(PortExposure exposure)
    {
        return exposure switch
        {
            PortExposure.Public => "public",
            PortExposure.Lan => "lan",
            PortExposure.Localhost => "localhost",
            _ => exposure.ToString().ToLowerInvariant(),
        };
    }

    private static bool TryResolveNetTargetServer(
        SystemCallExecutionContext executionContext,
        string hostOrIp,
        out ServerNodeRuntime targetServer,
        out (SystemCallErrorCode Code, string Message) failure)
    {
        targetServer = null!;
        failure = default;
        if (!executionContext.World.TryResolveServerByHostOrIp(hostOrIp, out targetServer))
        {
            failure = (SystemCallErrorCode.NotFound, $"host not found: {hostOrIp}");
            return false;
        }

        if (targetServer.Status != ServerStatus.Online)
        {
            failure = (SystemCallErrorCode.NotFound, $"server offline: {targetServer.NodeId}");
            return false;
        }

        return true;
    }

    private static ValMap CreateNetSuccessMap()
    {
        return new ValMap
        {
            ["ok"] = ValNumber.one,
            ["code"] = new ValString(SystemCallErrorCodeTokenMapper.ToApiToken(SystemCallErrorCode.None)),
            ["err"] = ValNull.instance,
        };
    }

    private static ValMap CreateNetFailureMap(SystemCallErrorCode code, string err)
    {
        return new ValMap
        {
            ["ok"] = ValNumber.zero,
            ["code"] = new ValString(SystemCallErrorCodeTokenMapper.ToApiToken(code)),
            ["err"] = new ValString(err),
        };
    }
}

