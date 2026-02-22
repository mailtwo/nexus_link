using Miniscript;
using System;
using System.Collections.Generic;
using Uplink2.Runtime.Syscalls;

#nullable enable

namespace Uplink2.Runtime.MiniScript;

internal static partial class MiniScriptSshIntrinsics
{
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
        netModule["scan"] = Intrinsic.GetByName(NetScanIntrinsicName).GetFunc();
        netModule["ports"] = Intrinsic.GetByName(NetPortsIntrinsicName).GetFunc();
        netModule["banner"] = Intrinsic.GetByName(NetBannerIntrinsicName).GetFunc();
        interpreter.SetGlobalValue("net", netModule);
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

            if (!TryParseNetScanArguments(context, out var sessionOrRouteMap, out var subnetOrHost, out var parseError))
            {
                return new Intrinsic.Result(CreateNetFailureMap(SystemCallErrorCode.InvalidArgs, parseError));
            }

            if (!string.Equals(subnetOrHost, "lan", StringComparison.Ordinal))
            {
                return new Intrinsic.Result(
                    CreateNetFailureMap(
                        SystemCallErrorCode.InvalidArgs,
                        "subnetOrHost must be \"lan\"."));
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

            var ips = CollectLanNeighborIps(executionContext.World, endpoint.Server);
            var result = CreateNetSuccessMap();
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

    private static bool TryParseNetScanArguments(
        TAC.Context context,
        out ValMap? sessionOrRouteMap,
        out string subnetOrHost,
        out string error)
    {
        sessionOrRouteMap = null;
        subnetOrHost = string.Empty;
        error = string.Empty;

        var rawArg1 = context.GetLocal("arg1");
        var rawArg2 = context.GetLocal("arg2");
        if (!TryParseFsSessionOrRouteArgument(rawArg1, out sessionOrRouteMap, out var hasSessionOrRoute, out error))
        {
            return false;
        }

        if (hasSessionOrRoute)
        {
            return TryReadNetSubnetOrHost(rawArg2, out subnetOrHost, out error);
        }

        if (rawArg2 is not null)
        {
            error = "too many arguments.";
            return false;
        }

        return TryReadNetSubnetOrHost(rawArg1, out subnetOrHost, out error);
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

    private static bool TryReadNetSubnetOrHost(Value rawInput, out string subnetOrHost, out string error)
    {
        subnetOrHost = string.Empty;
        error = string.Empty;
        if (rawInput is null || rawInput is ValMap)
        {
            error = "subnetOrHost is required.";
            return false;
        }

        subnetOrHost = rawInput.ToString().Trim();
        if (string.IsNullOrWhiteSpace(subnetOrHost))
        {
            error = "subnetOrHost is required.";
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

    private static ValList CollectLanNeighborIps(WorldRuntime world, ServerNodeRuntime sourceServer)
    {
        var neighborIps = new List<string>();
        var seenIps = new HashSet<string>(StringComparer.Ordinal);
        foreach (var neighborNodeId in sourceServer.LanNeighbors)
        {
            if (!world.TryGetServer(neighborNodeId, out var neighborServer))
            {
                continue;
            }

            var neighborIp = ResolveNeighborIpForNetScan(sourceServer, neighborServer);
            if (string.IsNullOrWhiteSpace(neighborIp) || !seenIps.Add(neighborIp))
            {
                continue;
            }

            neighborIps.Add(neighborIp);
        }

        neighborIps.Sort(StringComparer.Ordinal);
        var result = new ValList();
        foreach (var ip in neighborIps)
        {
            result.values.Add(new ValString(ip));
        }

        return result;
    }

    private static string ResolveNeighborIpForNetScan(ServerNodeRuntime source, ServerNodeRuntime neighbor)
    {
        foreach (var sourceInterface in source.Interfaces)
        {
            if (string.Equals(sourceInterface.NetId, InternetNetId, StringComparison.Ordinal))
            {
                continue;
            }

            if (TryGetInterfaceIpForNetScan(neighbor, sourceInterface.NetId, out var neighborIp))
            {
                return neighborIp;
            }
        }

        foreach (var sourceInterface in source.Interfaces)
        {
            if (TryGetInterfaceIpForNetScan(neighbor, sourceInterface.NetId, out var neighborIp))
            {
                return neighborIp;
            }
        }

        foreach (var neighborInterface in neighbor.Interfaces)
        {
            if (!string.IsNullOrWhiteSpace(neighborInterface.Ip) &&
                !string.Equals(neighborInterface.NetId, InternetNetId, StringComparison.Ordinal))
            {
                return neighborInterface.Ip;
            }
        }

        if (!string.IsNullOrWhiteSpace(neighbor.PrimaryIp))
        {
            return neighbor.PrimaryIp;
        }

        foreach (var neighborInterface in neighbor.Interfaces)
        {
            if (!string.IsNullOrWhiteSpace(neighborInterface.Ip))
            {
                return neighborInterface.Ip;
            }
        }

        return string.Empty;
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
