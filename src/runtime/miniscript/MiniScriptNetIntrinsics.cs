using Miniscript;
using System;
using System.Collections.Generic;
using Uplink2.Runtime.Syscalls;

#nullable enable

namespace Uplink2.Runtime.MiniScript;

internal static partial class MiniScriptSshIntrinsics
{
    private const string NetInterfacesIntrinsicName = "uplink_net_interfaces";
    private const string NetScanIntrinsicName = "uplink_net_scan";
    private const string NetPortsIntrinsicName = "uplink_net_ports";
    private const string NetBannerIntrinsicName = "uplink_net_banner";
    private const string InternetNetId = "internet";

    private static void InjectNetModule(Interpreter interpreter, SshModuleState moduleState)
    {
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

    private static void RegisterNetInterfacesIntrinsic()
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
            if (!TryResolveFsEndpoint(
                    state,
                    executionContext,
                    sessionOrRouteMap,
                    out var endpoint,
                    out var endpointUser,
                    out var endpointError))
            {
                return new Intrinsic.Result(CreateNetFailureMap(SystemCallErrorCode.InvalidArgs, endpointError));
            }

            if (!endpointUser.Privilege.Execute)
            {
                return new Intrinsic.Result(CreateNetFailureMap(SystemCallErrorCode.PermissionDenied, "permission denied: net.interfaces"));
            }

            var interfaces = CollectEndpointInterfaces(endpoint.Server);
            var result = CreateNetSuccessMap();
            result["interfaces"] = interfaces;
            return new Intrinsic.Result(result);
        };
    }

    private static void RegisterNetScanIntrinsic()
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
            if (!TryResolveFsEndpoint(
                    state,
                    executionContext,
                    sessionOrRouteMap,
                    out var endpoint,
                    out var endpointUser,
                    out var endpointError))
            {
                return new Intrinsic.Result(CreateNetFailureMap(SystemCallErrorCode.InvalidArgs, endpointError));
            }

            if (!endpointUser.Privilege.Execute)
            {
                return new Intrinsic.Result(CreateNetFailureMap(SystemCallErrorCode.PermissionDenied, "permission denied: net.scan"));
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
                return new Intrinsic.Result(CreateNetFailureMap(scanErrorCode, scanErrorMessage));
            }

            var result = CreateNetSuccessMap();
            result["interfaces"] = interfaces;
            result["ips"] = ips;
            return new Intrinsic.Result(result);
        };
    }

    private static void RegisterNetPortsIntrinsic()
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
            if (!TryResolveFsEndpoint(
                    state,
                    executionContext,
                    sessionOrRouteMap,
                    out var endpoint,
                    out _,
                    out var endpointError))
            {
                return new Intrinsic.Result(CreateNetFailureMap(SystemCallErrorCode.InvalidArgs, endpointError));
            }

            if (!TryResolveNetTargetServer(executionContext, hostOrIp, out var targetServer, out var targetFailure))
            {
                return new Intrinsic.Result(CreateNetFailureMap(targetFailure.Code, targetFailure.Message));
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
            return new Intrinsic.Result(result);
        };
    }

    private static void RegisterNetBannerIntrinsic()
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
            if (!TryResolveFsEndpoint(
                    state,
                    executionContext,
                    sessionOrRouteMap,
                    out var endpoint,
                    out _,
                    out var endpointError))
            {
                return new Intrinsic.Result(CreateNetFailureMap(SystemCallErrorCode.InvalidArgs, endpointError));
            }

            if (!TryResolveNetTargetServer(executionContext, hostOrIp, out var targetServer, out var targetFailure))
            {
                return new Intrinsic.Result(CreateNetFailureMap(targetFailure.Code, targetFailure.Message));
            }

            if (!targetServer.Ports.TryGetValue(port, out var portConfig) ||
                portConfig is null ||
                portConfig.PortType == PortType.None)
            {
                return new Intrinsic.Result(
                    CreateNetFailureMap(
                        SystemCallErrorCode.PortClosed,
                        $"port closed: {port}"));
            }

            if (!IsNetPortExposureAllowed(endpoint.Server, targetServer, portConfig.Exposure))
            {
                return new Intrinsic.Result(
                    CreateNetFailureMap(
                        SystemCallErrorCode.NetDenied,
                        $"port exposure denied: {port}"));
            }

            var result = CreateNetSuccessMap();
            result["banner"] = new ValString((portConfig.ServiceId ?? string.Empty).Trim());
            return new Intrinsic.Result(result);
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
            ["code"] = new ValString(SystemCallErrorCode.None.ToString()),
            ["err"] = ValNull.instance,
        };
    }

    private static ValMap CreateNetFailureMap(SystemCallErrorCode code, string err)
    {
        return new ValMap
        {
            ["ok"] = ValNumber.zero,
            ["code"] = new ValString(code.ToString()),
            ["err"] = new ValString(err),
        };
    }
}
