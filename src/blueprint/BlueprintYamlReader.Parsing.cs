using System;
using System.Collections.Generic;
using System.IO;

#nullable enable

namespace Uplink2.Blueprint;

public sealed partial class BlueprintYamlReader
{
    private static void ParseServerSpecs(
        Dictionary<string, object?> rootMap,
        string filePath,
        BlueprintCatalog catalog,
        List<string> errors)
    {
        if (!TryGetValueIgnoreCase(rootMap, "ServerSpec", out var specSectionValue))
        {
            return;
        }

        var specSectionMap = AsMap(specSectionValue, filePath, "ServerSpec", errors, allowNullAsEmpty: true);
        if (specSectionMap is null)
        {
            return;
        }

        foreach (var specPair in specSectionMap)
        {
            var specId = specPair.Key;
            if (string.IsNullOrWhiteSpace(specId))
            {
                errors.Add($"{filePath}: ServerSpec contains an empty spec id key.");
                continue;
            }

            var specContext = $"ServerSpec.{specId}";
            var specMap = AsMap(specPair.Value, filePath, specContext, errors, allowNullAsEmpty: false);
            if (specMap is null)
            {
                continue;
            }

            var specBlueprint = new ServerSpecBlueprint
            {
                SpecId = specId,
            };

            if (TryGetValueIgnoreCase(specMap, "initialStatus", out var initialStatusValue))
            {
                if (TryParseEnum(initialStatusValue, out BlueprintServerStatus initialStatus))
                {
                    specBlueprint.InitialStatus = initialStatus;
                }
                else
                {
                    errors.Add($"{filePath}: {specContext}.initialStatus has an unknown value.");
                }
            }

            if (TryGetValueIgnoreCase(specMap, "initialReason", out var initialReasonValue))
            {
                if (TryParseEnum(initialReasonValue, out BlueprintServerReason initialReason))
                {
                    specBlueprint.InitialReason = initialReason;
                }
                else
                {
                    errors.Add($"{filePath}: {specContext}.initialReason has an unknown value.");
                }
            }

            if (TryGetValueIgnoreCase(specMap, "hostname", out var hostnameValue))
            {
                specBlueprint.Hostname = ReadString(hostnameValue);
            }

            if (TryGetValueIgnoreCase(specMap, "logCapacity", out var logCapacityValue))
            {
                if (TryReadInt(logCapacityValue, out var logCapacity) && logCapacity > 0)
                {
                    specBlueprint.LogCapacity = logCapacity;
                }
                else
                {
                    errors.Add($"{filePath}: {specContext}.logCapacity must be a positive integer.");
                }
            }

            if (TryGetValueIgnoreCase(specMap, "users", out var usersValue))
            {
                ParseUsers(specBlueprint.Users, usersValue, filePath, $"{specContext}.users", errors);
            }

            if (TryGetValueIgnoreCase(specMap, "ports", out var portsValue))
            {
                ParsePorts(specBlueprint.Ports, portsValue, filePath, $"{specContext}.ports", errors);
            }

            if (TryGetValueIgnoreCase(specMap, "daemons", out var daemonsValue))
            {
                ParseDaemons(specBlueprint.Daemons, daemonsValue, filePath, $"{specContext}.daemons", errors);
            }

            if (TryGetValueIgnoreCase(specMap, "diskOverlay", out var diskOverlayValue))
            {
                specBlueprint.DiskOverlay = ParseDiskOverlay(
                    diskOverlayValue,
                    filePath,
                    $"{specContext}.diskOverlay",
                    errors);
            }

            if (catalog.ServerSpecs.ContainsKey(specId))
            {
                errors.Add($"{filePath}: duplicate ServerSpec '{specId}'.");
                continue;
            }

            catalog.ServerSpecs[specId] = specBlueprint;
        }
    }

    private static void ParseScenarios(
        Dictionary<string, object?> rootMap,
        string filePath,
        BlueprintCatalog catalog,
        List<string> errors)
    {
        if (!TryGetValueIgnoreCase(rootMap, "Scenario", out var scenarioSectionValue))
        {
            return;
        }

        var scenarioSectionMap = AsMap(scenarioSectionValue, filePath, "Scenario", errors, allowNullAsEmpty: true);
        if (scenarioSectionMap is null)
        {
            return;
        }

        foreach (var scenarioPair in scenarioSectionMap)
        {
            var scenarioId = scenarioPair.Key;
            if (string.IsNullOrWhiteSpace(scenarioId))
            {
                errors.Add($"{filePath}: Scenario contains an empty scenario id key.");
                continue;
            }

            var scenarioContext = $"Scenario.{scenarioId}";
            var scenarioMap = AsMap(scenarioPair.Value, filePath, scenarioContext, errors, allowNullAsEmpty: false);
            if (scenarioMap is null)
            {
                continue;
            }

            var scenarioBlueprint = new ScenarioBlueprint
            {
                ScenarioId = scenarioId,
            };

            if (TryGetValueIgnoreCase(scenarioMap, "servers", out var serversValue))
            {
                ParseServers(scenarioBlueprint.Servers, serversValue, filePath, $"{scenarioContext}.servers", errors);
            }

            if (TryGetValueIgnoreCase(scenarioMap, "subnetTopology", out var subnetValue))
            {
                ParseSubnetTopology(
                    scenarioBlueprint.SubnetTopology,
                    subnetValue,
                    filePath,
                    $"{scenarioContext}.subnetTopology",
                    errors);
            }

            if (TryGetValueIgnoreCase(scenarioMap, "events", out var eventsValue))
            {
                ParseEvents(scenarioBlueprint.Events, eventsValue, filePath, $"{scenarioContext}.events", errors);
            }

            if (catalog.Scenarios.ContainsKey(scenarioId))
            {
                errors.Add($"{filePath}: duplicate Scenario '{scenarioId}'.");
                continue;
            }

            catalog.Scenarios[scenarioId] = scenarioBlueprint;
        }
    }

    private static void ParseCampaigns(
        Dictionary<string, object?> rootMap,
        string filePath,
        BlueprintCatalog catalog,
        List<string> errors)
    {
        if (!TryGetValueIgnoreCase(rootMap, "campaigns", out var campaignSectionValue) &&
            !TryGetValueIgnoreCase(rootMap, "Campaign", out campaignSectionValue))
        {
            return;
        }

        var campaignSectionMap = AsMap(campaignSectionValue, filePath, "campaigns", errors, allowNullAsEmpty: true);
        if (campaignSectionMap is null)
        {
            return;
        }

        foreach (var campaignPair in campaignSectionMap)
        {
            var campaignId = campaignPair.Key;
            if (string.IsNullOrWhiteSpace(campaignId))
            {
                errors.Add($"{filePath}: campaigns contains an empty campaign id key.");
                continue;
            }

            var campaignContext = $"campaigns.{campaignId}";
            var campaignMap = AsMap(campaignPair.Value, filePath, campaignContext, errors, allowNullAsEmpty: false);
            if (campaignMap is null)
            {
                continue;
            }

            var campaignBlueprint = new CampaignBlueprint
            {
                CampaignId = campaignId,
            };

            if (TryGetValueIgnoreCase(campaignMap, "childCampaigns", out var childCampaignsValue))
            {
                foreach (var childCampaignId in ReadStringList(childCampaignsValue, filePath, $"{campaignContext}.childCampaigns", errors))
                {
                    campaignBlueprint.ChildCampaigns.Add(childCampaignId);
                }
            }

            if (TryGetValueIgnoreCase(campaignMap, "scenarios", out var scenariosValue))
            {
                foreach (var scenarioId in ReadStringList(scenariosValue, filePath, $"{campaignContext}.scenarios", errors))
                {
                    campaignBlueprint.Scenarios.Add(scenarioId);
                }
            }

            if (catalog.Campaigns.ContainsKey(campaignId))
            {
                errors.Add($"{filePath}: duplicate campaign '{campaignId}'.");
                continue;
            }

            catalog.Campaigns[campaignId] = campaignBlueprint;
        }
    }

    private static void ParseUsers(
        Dictionary<string, UserBlueprint> users,
        object? usersValue,
        string filePath,
        string context,
        List<string> errors)
    {
        var usersMap = AsMap(usersValue, filePath, context, errors, allowNullAsEmpty: true);
        if (usersMap is null)
        {
            return;
        }

        foreach (var userPair in usersMap)
        {
            var userKey = userPair.Key;
            if (string.IsNullOrWhiteSpace(userKey))
            {
                errors.Add($"{filePath}: {context} contains an empty userKey.");
                continue;
            }

            var userContext = $"{context}.{userKey}";
            var userMap = AsMap(userPair.Value, filePath, userContext, errors, allowNullAsEmpty: false);
            if (userMap is null)
            {
                continue;
            }

            var userBlueprint = new UserBlueprint();

            if (TryGetValueIgnoreCase(userMap, "userId", out var userIdValue))
            {
                userBlueprint.UserId = ReadString(userIdValue);
            }

            if (TryGetValueIgnoreCase(userMap, "passwd", out var passwdValue))
            {
                userBlueprint.Passwd = passwdValue is null ? string.Empty : ReadString(passwdValue);
            }

            if (TryGetValueIgnoreCase(userMap, "authMode", out var authModeValue))
            {
                if (TryParseEnum(authModeValue, out BlueprintAuthMode authMode))
                {
                    userBlueprint.AuthMode = authMode;
                }
                else
                {
                    errors.Add($"{filePath}: {userContext}.authMode has an unknown value.");
                }
            }

            if (TryGetValueIgnoreCase(userMap, "privilege", out var privilegeValue))
            {
                var privilegeMap = AsMap(privilegeValue, filePath, $"{userContext}.privilege", errors, allowNullAsEmpty: true);
                if (privilegeMap is not null)
                {
                    if (TryGetValueIgnoreCase(privilegeMap, "read", out var readValue))
                    {
                        if (TryReadBool(readValue, out var read))
                        {
                            userBlueprint.Privilege.Read = read;
                        }
                        else
                        {
                            errors.Add($"{filePath}: {userContext}.privilege.read must be boolean.");
                        }
                    }

                    if (TryGetValueIgnoreCase(privilegeMap, "write", out var writeValue))
                    {
                        if (TryReadBool(writeValue, out var write))
                        {
                            userBlueprint.Privilege.Write = write;
                        }
                        else
                        {
                            errors.Add($"{filePath}: {userContext}.privilege.write must be boolean.");
                        }
                    }

                    if (TryGetValueIgnoreCase(privilegeMap, "execute", out var executeValue))
                    {
                        if (TryReadBool(executeValue, out var execute))
                        {
                            userBlueprint.Privilege.Execute = execute;
                        }
                        else
                        {
                            errors.Add($"{filePath}: {userContext}.privilege.execute must be boolean.");
                        }
                    }
                }
            }

            users[userKey] = userBlueprint;
        }
    }

    private static void ParsePorts(
        Dictionary<int, PortBlueprint> ports,
        object? portsValue,
        string filePath,
        string context,
        List<string> errors)
    {
        var portsMap = AsMap(portsValue, filePath, context, errors, allowNullAsEmpty: true);
        if (portsMap is null)
        {
            return;
        }

        foreach (var portPair in portsMap)
        {
            if (!TryReadInt(portPair.Key, out var portNum))
            {
                errors.Add($"{filePath}: {context} key '{portPair.Key}' is not a valid port number.");
                continue;
            }

            var portContext = $"{context}.{portNum}";
            var portMap = AsMap(portPair.Value, filePath, portContext, errors, allowNullAsEmpty: false);
            if (portMap is null)
            {
                continue;
            }

            ports[portNum] = ParsePortBlueprint(portMap, filePath, portContext, errors);
        }
    }

    private static void ParseDaemons(
        Dictionary<BlueprintDaemonType, DaemonBlueprint> daemons,
        object? daemonsValue,
        string filePath,
        string context,
        List<string> errors)
    {
        var daemonsMap = AsMap(daemonsValue, filePath, context, errors, allowNullAsEmpty: true);
        if (daemonsMap is null)
        {
            return;
        }

        foreach (var daemonPair in daemonsMap)
        {
            if (!TryParseEnum(daemonPair.Key, out BlueprintDaemonType daemonType))
            {
                errors.Add($"{filePath}: {context} key '{daemonPair.Key}' is not a supported daemon type.");
                continue;
            }

            var daemonContext = $"{context}.{daemonPair.Key}";
            var daemonMap = AsMap(daemonPair.Value, filePath, daemonContext, errors, allowNullAsEmpty: false);
            if (daemonMap is null)
            {
                continue;
            }

            daemons[daemonType] = ParseDaemonBlueprint(daemonType, daemonMap);
        }
    }

    private static DiskOverlayBlueprint ParseDiskOverlay(
        object? diskOverlayValue,
        string filePath,
        string context,
        List<string> errors)
    {
        var diskOverlay = new DiskOverlayBlueprint();

        var diskOverlayMap = AsMap(diskOverlayValue, filePath, context, errors, allowNullAsEmpty: true);
        if (diskOverlayMap is null)
        {
            return diskOverlay;
        }

        if (TryGetValueIgnoreCase(diskOverlayMap, "overlayEntries", out var entriesValue))
        {
            var entriesMap = AsMap(entriesValue, filePath, $"{context}.overlayEntries", errors, allowNullAsEmpty: true);
            if (entriesMap is not null)
            {
                foreach (var entryPair in entriesMap)
                {
                    if (string.IsNullOrWhiteSpace(entryPair.Key))
                    {
                        errors.Add($"{filePath}: {context}.overlayEntries contains an empty path key.");
                        continue;
                    }

                    var entryContext = $"{context}.overlayEntries[{entryPair.Key}]";
                    var entryMap = AsMap(entryPair.Value, filePath, entryContext, errors, allowNullAsEmpty: false);
                    if (entryMap is null)
                    {
                        continue;
                    }

                    diskOverlay.OverlayEntries[entryPair.Key] = ParseEntryMeta(entryMap, filePath, entryContext, errors);
                }
            }
        }

        if (TryGetValueIgnoreCase(diskOverlayMap, "tombstones", out var tombstonesValue))
        {
            foreach (var tombstonePath in ReadStringList(tombstonesValue, filePath, $"{context}.tombstones", errors))
            {
                diskOverlay.Tombstones.Add(tombstonePath);
            }
        }

        return diskOverlay;
    }

    private static void ParseServers(
        List<ServerSpawnBlueprint> servers,
        object? serversValue,
        string filePath,
        string context,
        List<string> errors)
    {
        var seenNodeIds = new HashSet<string>(StringComparer.Ordinal);

        if (serversValue is Dictionary<string, object?> serverMapByNodeId)
        {
            foreach (var serverPair in serverMapByNodeId)
            {
                var nodeId = serverPair.Key;
                if (string.IsNullOrWhiteSpace(nodeId))
                {
                    errors.Add($"{filePath}: {context} contains an empty nodeId key.");
                    continue;
                }

                if (!seenNodeIds.Add(nodeId))
                {
                    errors.Add($"{filePath}: {context} contains duplicate nodeId '{nodeId}'.");
                    continue;
                }

                var serverContext = $"{context}[{nodeId}]";
                var serverMap = AsMap(serverPair.Value, filePath, serverContext, errors, allowNullAsEmpty: false);
                if (serverMap is null)
                {
                    continue;
                }

                servers.Add(ParseServerSpawn(nodeId, serverMap, filePath, serverContext, errors));
            }

            return;
        }

        var serverList = AsList(serversValue, filePath, context, errors, allowNullAsEmpty: true);
        if (serverList is null)
        {
            return;
        }

        for (var index = 0; index < serverList.Count; index++)
        {
            var serverContext = $"{context}[{index}]";
            var serverMap = AsMap(serverList[index], filePath, serverContext, errors, allowNullAsEmpty: false);
            if (serverMap is null)
            {
                continue;
            }

            if (!TryGetValueIgnoreCase(serverMap, "nodeId", out var nodeIdValue))
            {
                errors.Add($"{filePath}: {serverContext}.nodeId is required when servers is list-shaped.");
                continue;
            }

            var nodeId = ReadString(nodeIdValue);
            if (string.IsNullOrWhiteSpace(nodeId))
            {
                errors.Add($"{filePath}: {serverContext}.nodeId cannot be empty.");
                continue;
            }

            if (!seenNodeIds.Add(nodeId))
            {
                errors.Add($"{filePath}: {context} contains duplicate nodeId '{nodeId}'.");
                continue;
            }

            servers.Add(ParseServerSpawn(nodeId, serverMap, filePath, serverContext, errors));
        }
    }

    private static ServerSpawnBlueprint ParseServerSpawn(
        string nodeId,
        Dictionary<string, object?> serverMap,
        string filePath,
        string context,
        List<string> errors)
    {
        var server = new ServerSpawnBlueprint
        {
            NodeId = nodeId,
        };

        if (TryGetValueIgnoreCase(serverMap, "specId", out var specIdValue))
        {
            server.SpecId = ReadString(specIdValue);
        }

        if (string.IsNullOrWhiteSpace(server.SpecId))
        {
            errors.Add($"{filePath}: {context}.specId is required.");
        }

        if (TryGetValueIgnoreCase(serverMap, "hostname", out var hostnameValue))
        {
            server.HostnameOverride = ReadString(hostnameValue);
        }

        if (TryGetValueIgnoreCase(serverMap, "initialStatus", out var initialStatusValue))
        {
            if (TryParseEnum(initialStatusValue, out BlueprintServerStatus initialStatus))
            {
                server.HasInitialStatusOverride = true;
                server.InitialStatusOverride = initialStatus;
            }
            else
            {
                errors.Add($"{filePath}: {context}.initialStatus has an unknown value.");
            }
        }

        if (TryGetValueIgnoreCase(serverMap, "initialReason", out var initialReasonValue))
        {
            if (TryParseEnum(initialReasonValue, out BlueprintServerReason initialReason))
            {
                server.HasInitialReasonOverride = true;
                server.InitialReasonOverride = initialReason;
            }
            else
            {
                errors.Add($"{filePath}: {context}.initialReason has an unknown value.");
            }
        }

        if (TryGetValueIgnoreCase(serverMap, "role", out var roleValue))
        {
            if (TryParseEnum(roleValue, out BlueprintServerRole role))
            {
                server.Role = role;
            }
            else
            {
                errors.Add($"{filePath}: {context}.role has an unknown value.");
            }
        }
        else
        {
            errors.Add($"{filePath}: {context}.role is required.");
        }

        if (TryGetValueIgnoreCase(serverMap, "info", out var infoValue))
        {
            foreach (var infoLine in ReadStringList(infoValue, filePath, $"{context}.info", errors))
            {
                server.Info.Add(infoLine);
            }
        }

        if (TryGetValueIgnoreCase(serverMap, "diskOverlay", out var diskOverlayValue))
        {
            ParseDiskOverlayOverrides(server.DiskOverlayOverrides, diskOverlayValue, filePath, $"{context}.diskOverlay", errors);
        }

        if (TryGetValueIgnoreCase(serverMap, "ports", out var portsValue))
        {
            ParsePortOverrides(server.PortOverrides, portsValue, filePath, $"{context}.ports", errors);
        }

        if (TryGetValueIgnoreCase(serverMap, "daemons", out var daemonsValue))
        {
            ParseDaemonOverrides(server.DaemonOverrides, daemonsValue, filePath, $"{context}.daemons", errors);
        }

        if (TryGetValueIgnoreCase(serverMap, "interfaces", out var interfacesValue))
        {
            ParseInterfaces(server.Interfaces, interfacesValue, filePath, $"{context}.interfaces", errors);
        }
        else
        {
            errors.Add($"{filePath}: {context}.interfaces is required.");
        }

        return server;
    }

    private static void ParseDiskOverlayOverrides(
        DiskOverlayOverrideBlueprint diskOverlayOverrides,
        object? diskOverlayValue,
        string filePath,
        string context,
        List<string> errors)
    {
        var diskOverlayMap = AsMap(diskOverlayValue, filePath, context, errors, allowNullAsEmpty: true);
        if (diskOverlayMap is null)
        {
            return;
        }

        if (TryGetValueIgnoreCase(diskOverlayMap, "overlayEntries", out var entriesValue))
        {
            var entriesMap = AsMap(entriesValue, filePath, $"{context}.overlayEntries", errors, allowNullAsEmpty: true);
            if (entriesMap is not null)
            {
                foreach (var entryPair in entriesMap)
                {
                    if (string.IsNullOrWhiteSpace(entryPair.Key))
                    {
                        errors.Add($"{filePath}: {context}.overlayEntries contains an empty path key.");
                        continue;
                    }

                    var entryOverride = new EntryOverrideBlueprint();

                    if (entryPair.Value is null)
                    {
                        entryOverride.Remove = true;
                    }
                    else
                    {
                        var entryContext = $"{context}.overlayEntries[{entryPair.Key}]";
                        var entryMap = AsMap(entryPair.Value, filePath, entryContext, errors, allowNullAsEmpty: false);
                        if (entryMap is null)
                        {
                            continue;
                        }

                        entryOverride.Entry = ParseEntryMeta(entryMap, filePath, entryContext, errors);
                    }

                    diskOverlayOverrides.OverlayEntries[entryPair.Key] = entryOverride;
                }
            }
        }

        if (TryGetValueIgnoreCase(diskOverlayMap, "tombstones", out var tombstonesValue))
        {
            foreach (var tombstonePath in ReadStringList(tombstonesValue, filePath, $"{context}.tombstones", errors))
            {
                diskOverlayOverrides.Tombstones.Add(tombstonePath);
            }
        }
    }

    private static void ParsePortOverrides(
        Dictionary<int, PortOverrideBlueprint> portOverrides,
        object? portsValue,
        string filePath,
        string context,
        List<string> errors)
    {
        var portsMap = AsMap(portsValue, filePath, context, errors, allowNullAsEmpty: true);
        if (portsMap is null)
        {
            return;
        }

        foreach (var portPair in portsMap)
        {
            if (!TryReadInt(portPair.Key, out var portNum))
            {
                errors.Add($"{filePath}: {context} key '{portPair.Key}' is not a valid port number.");
                continue;
            }

            var overrideConfig = new PortOverrideBlueprint();
            if (portPair.Value is null)
            {
                overrideConfig.Remove = true;
            }
            else
            {
                var portContext = $"{context}.{portNum}";
                var portMap = AsMap(portPair.Value, filePath, portContext, errors, allowNullAsEmpty: false);
                if (portMap is null)
                {
                    continue;
                }

                overrideConfig.Port = ParsePortBlueprint(portMap, filePath, portContext, errors);
            }

            portOverrides[portNum] = overrideConfig;
        }
    }

    private static void ParseDaemonOverrides(
        Dictionary<BlueprintDaemonType, DaemonOverrideBlueprint> daemonOverrides,
        object? daemonsValue,
        string filePath,
        string context,
        List<string> errors)
    {
        var daemonsMap = AsMap(daemonsValue, filePath, context, errors, allowNullAsEmpty: true);
        if (daemonsMap is null)
        {
            return;
        }

        foreach (var daemonPair in daemonsMap)
        {
            if (!TryParseEnum(daemonPair.Key, out BlueprintDaemonType daemonType))
            {
                errors.Add($"{filePath}: {context} key '{daemonPair.Key}' is not a supported daemon type.");
                continue;
            }

            var overrideConfig = new DaemonOverrideBlueprint();
            if (daemonPair.Value is null)
            {
                overrideConfig.Remove = true;
            }
            else
            {
                var daemonContext = $"{context}.{daemonPair.Key}";
                var daemonMap = AsMap(daemonPair.Value, filePath, daemonContext, errors, allowNullAsEmpty: false);
                if (daemonMap is null)
                {
                    continue;
                }

                overrideConfig.Daemon = ParseDaemonBlueprint(daemonType, daemonMap);
            }

            daemonOverrides[daemonType] = overrideConfig;
        }
    }

    private static void ParseInterfaces(
        List<InterfaceBlueprint> interfaces,
        object? interfacesValue,
        string filePath,
        string context,
        List<string> errors)
    {
        var interfaceList = AsList(interfacesValue, filePath, context, errors, allowNullAsEmpty: true);
        if (interfaceList is null)
        {
            return;
        }

        for (var index = 0; index < interfaceList.Count; index++)
        {
            var interfaceContext = $"{context}[{index}]";
            var interfaceMap = AsMap(interfaceList[index], filePath, interfaceContext, errors, allowNullAsEmpty: false);
            if (interfaceMap is null)
            {
                continue;
            }

            var interfaceBlueprint = new InterfaceBlueprint();

            if (TryGetValueIgnoreCase(interfaceMap, "netId", out var netIdValue))
            {
                interfaceBlueprint.NetId = ReadString(netIdValue);
            }

            if (string.IsNullOrWhiteSpace(interfaceBlueprint.NetId))
            {
                errors.Add($"{filePath}: {interfaceContext}.netId is required.");
                continue;
            }

            if (TryGetValueIgnoreCase(interfaceMap, "hostSuffix", out var hostSuffixValue))
            {
                var hostSuffixList = AsList(hostSuffixValue, filePath, $"{interfaceContext}.hostSuffix", errors, allowNullAsEmpty: true);
                if (hostSuffixList is not null)
                {
                    interfaceBlueprint.HasHostSuffix = true;
                    for (var suffixIndex = 0; suffixIndex < hostSuffixList.Count; suffixIndex++)
                    {
                        if (TryReadInt(hostSuffixList[suffixIndex], out var suffixPart))
                        {
                            interfaceBlueprint.HostSuffix.Add(suffixPart);
                        }
                        else
                        {
                            errors.Add($"{filePath}: {interfaceContext}.hostSuffix[{suffixIndex}] must be an integer.");
                        }
                    }
                }
            }

            if (TryGetValueIgnoreCase(interfaceMap, "initiallyExposed", out var initiallyExposedValue))
            {
                if (TryReadBool(initiallyExposedValue, out var initiallyExposed))
                {
                    interfaceBlueprint.InitiallyExposed = initiallyExposed;
                }
                else
                {
                    errors.Add($"{filePath}: {interfaceContext}.initiallyExposed must be boolean.");
                }
            }

            interfaces.Add(interfaceBlueprint);
        }
    }

    private static void ParseSubnetTopology(
        Dictionary<string, SubnetBlueprint> subnetTopology,
        object? subnetTopologyValue,
        string filePath,
        string context,
        List<string> errors)
    {
        var subnetMap = AsMap(subnetTopologyValue, filePath, context, errors, allowNullAsEmpty: true);
        if (subnetMap is null)
        {
            return;
        }

        foreach (var subnetPair in subnetMap)
        {
            var subnetId = subnetPair.Key;
            if (string.IsNullOrWhiteSpace(subnetId))
            {
                errors.Add($"{filePath}: {context} contains an empty subnet id key.");
                continue;
            }

            var subnetContext = $"{context}.{subnetId}";
            var subnetConfigMap = AsMap(subnetPair.Value, filePath, subnetContext, errors, allowNullAsEmpty: false);
            if (subnetConfigMap is null)
            {
                continue;
            }

            var subnetBlueprint = new SubnetBlueprint();

            if (TryGetValueIgnoreCase(subnetConfigMap, "addressPlan", out var addressPlanValue))
            {
                subnetBlueprint.AddressPlan = ReadString(addressPlanValue);
            }

            if (string.IsNullOrWhiteSpace(subnetBlueprint.AddressPlan))
            {
                errors.Add($"{filePath}: {subnetContext}.addressPlan is required.");
            }

            if (TryGetValueIgnoreCase(subnetConfigMap, "hubs", out var hubsValue))
            {
                var hubsMap = AsMap(hubsValue, filePath, $"{subnetContext}.hubs", errors, allowNullAsEmpty: true);
                if (hubsMap is not null)
                {
                    foreach (var hubPair in hubsMap)
                    {
                        if (string.IsNullOrWhiteSpace(hubPair.Key))
                        {
                            errors.Add($"{filePath}: {subnetContext}.hubs contains an empty hub id key.");
                            continue;
                        }

                        var hubContext = $"{subnetContext}.hubs.{hubPair.Key}";
                        var hubMap = AsMap(hubPair.Value, filePath, hubContext, errors, allowNullAsEmpty: false);
                        if (hubMap is null)
                        {
                            continue;
                        }

                        var hubBlueprint = new HubBlueprint();
                        if (TryGetValueIgnoreCase(hubMap, "type", out var hubTypeValue))
                        {
                            if (TryParseEnum(hubTypeValue, out BlueprintHubType hubType))
                            {
                                hubBlueprint.Type = hubType;
                            }
                            else
                            {
                                errors.Add($"{filePath}: {hubContext}.type has an unknown value.");
                            }
                        }

                        if (TryGetValueIgnoreCase(hubMap, "members", out var membersValue))
                        {
                            foreach (var memberNodeId in ReadStringList(membersValue, filePath, $"{hubContext}.members", errors))
                            {
                                hubBlueprint.Members.Add(memberNodeId);
                            }
                        }

                        subnetBlueprint.Hubs[hubPair.Key] = hubBlueprint;
                    }
                }
            }

            if (TryGetValueIgnoreCase(subnetConfigMap, "links", out var linksValue))
            {
                var linksList = AsList(linksValue, filePath, $"{subnetContext}.links", errors, allowNullAsEmpty: true);
                if (linksList is not null)
                {
                    for (var index = 0; index < linksList.Count; index++)
                    {
                        var linkContext = $"{subnetContext}.links[{index}]";
                        var linkMap = AsMap(linksList[index], filePath, linkContext, errors, allowNullAsEmpty: false);
                        if (linkMap is null)
                        {
                            continue;
                        }

                        var linkBlueprint = new SubnetLinkBlueprint();

                        if (TryGetValueIgnoreCase(linkMap, "a", out var aValue))
                        {
                            linkBlueprint.A = ReadString(aValue);
                        }

                        if (TryGetValueIgnoreCase(linkMap, "b", out var bValue))
                        {
                            linkBlueprint.B = ReadString(bValue);
                        }

                        if (string.IsNullOrWhiteSpace(linkBlueprint.A) || string.IsNullOrWhiteSpace(linkBlueprint.B))
                        {
                            errors.Add($"{filePath}: {linkContext} requires non-empty 'a' and 'b'.");
                            continue;
                        }

                        subnetBlueprint.Links.Add(linkBlueprint);
                    }
                }
            }

            subnetTopology[subnetId] = subnetBlueprint;
        }
    }

    private static void ParseEvents(
        Dictionary<string, EventBlueprint> eventsById,
        object? eventsValue,
        string filePath,
        string context,
        List<string> errors)
    {
        var eventsMap = AsMap(eventsValue, filePath, context, errors, allowNullAsEmpty: true);
        if (eventsMap is null)
        {
            return;
        }

        foreach (var eventPair in eventsMap)
        {
            var eventId = eventPair.Key;
            if (string.IsNullOrWhiteSpace(eventId))
            {
                errors.Add($"{filePath}: {context} contains an empty event id key.");
                continue;
            }

            var eventContext = $"{context}.{eventId}";
            var eventMap = AsMap(eventPair.Value, filePath, eventContext, errors, allowNullAsEmpty: false);
            if (eventMap is null)
            {
                continue;
            }

            var eventBlueprint = new EventBlueprint();

            if (TryGetValueIgnoreCase(eventMap, "conditionType", out var conditionTypeValue))
            {
                if (TryParseEnum(conditionTypeValue, out BlueprintConditionType conditionType))
                {
                    eventBlueprint.ConditionType = conditionType;
                }
                else
                {
                    errors.Add($"{filePath}: {eventContext}.conditionType has an unknown value.");
                }
            }

            if (TryGetValueIgnoreCase(eventMap, "conditionArgs", out var conditionArgsValue))
            {
                var conditionArgsMap = AsMap(conditionArgsValue, filePath, $"{eventContext}.conditionArgs", errors, allowNullAsEmpty: true);
                if (conditionArgsMap is not null)
                {
                    foreach (var argPair in conditionArgsMap)
                    {
                        eventBlueprint.ConditionArgs[argPair.Key] = ConvertToBlueprintValue(argPair.Value);
                    }
                }
            }

            if (TryGetValueIgnoreCase(eventMap, "actions", out var actionsValue))
            {
                var actionsList = AsList(actionsValue, filePath, $"{eventContext}.actions", errors, allowNullAsEmpty: true);
                if (actionsList is not null)
                {
                    for (var index = 0; index < actionsList.Count; index++)
                    {
                        var actionContext = $"{eventContext}.actions[{index}]";
                        var actionMap = AsMap(actionsList[index], filePath, actionContext, errors, allowNullAsEmpty: false);
                        if (actionMap is null)
                        {
                            continue;
                        }

                        eventBlueprint.Actions.Add(ParseActionBlueprint(actionMap, filePath, actionContext, errors));
                    }
                }
            }

            eventsById[eventId] = eventBlueprint;
        }
    }

    private static ActionBlueprint ParseActionBlueprint(
        Dictionary<string, object?> actionMap,
        string filePath,
        string context,
        List<string> errors)
    {
        var actionBlueprint = new ActionBlueprint();

        if (TryGetValueIgnoreCase(actionMap, "actionType", out var actionTypeValue))
        {
            if (TryParseEnum(actionTypeValue, out BlueprintActionType actionType))
            {
                actionBlueprint.ActionType = actionType;
            }
            else
            {
                errors.Add($"{filePath}: {context}.actionType has an unknown value.");
            }
        }

        if (TryGetValueIgnoreCase(actionMap, "actionArgs", out var actionArgsValue))
        {
            var actionArgsMap = AsMap(actionArgsValue, filePath, $"{context}.actionArgs", errors, allowNullAsEmpty: true);
            if (actionArgsMap is not null)
            {
                foreach (var argPair in actionArgsMap)
                {
                    actionBlueprint.ActionArgs[argPair.Key] = ConvertToBlueprintValue(argPair.Value);
                }
            }
        }

        return actionBlueprint;
    }

    private static DaemonBlueprint ParseDaemonBlueprint(BlueprintDaemonType daemonType, Dictionary<string, object?> daemonMap)
    {
        var daemon = new DaemonBlueprint
        {
            DaemonType = daemonType,
        };

        foreach (var argPair in daemonMap)
        {
            if (string.Equals(argPair.Key, "daemonType", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            daemon.DaemonArgs[argPair.Key] = ConvertToBlueprintValue(argPair.Value);
        }

        return daemon;
    }

    private static PortBlueprint ParsePortBlueprint(
        Dictionary<string, object?> portMap,
        string filePath,
        string context,
        List<string> errors)
    {
        var port = new PortBlueprint();

        if (TryGetValueIgnoreCase(portMap, "portType", out var portTypeValue))
        {
            if (TryParseEnum(portTypeValue, out BlueprintPortType portType))
            {
                port.PortType = portType;
            }
            else
            {
                errors.Add($"{filePath}: {context}.portType has an unknown value.");
            }
        }

        if (TryGetValueIgnoreCase(portMap, "serviceId", out var serviceIdValue))
        {
            port.ServiceId = ReadString(serviceIdValue);
        }

        if (TryGetValueIgnoreCase(portMap, "exposure", out var exposureValue))
        {
            if (TryParseEnum(exposureValue, out BlueprintPortExposure exposure))
            {
                port.Exposure = exposure;
            }
            else
            {
                errors.Add($"{filePath}: {context}.exposure has an unknown value.");
            }
        }

        return port;
    }

    private static BlueprintEntryMeta ParseEntryMeta(
        Dictionary<string, object?> entryMap,
        string filePath,
        string context,
        List<string> errors)
    {
        var entry = new BlueprintEntryMeta();

        if (TryGetValueIgnoreCase(entryMap, "entryKind", out var entryKindValue))
        {
            if (TryParseEnum(entryKindValue, out BlueprintEntryKind entryKind))
            {
                entry.EntryKind = entryKind;
            }
            else
            {
                errors.Add($"{filePath}: {context}.entryKind has an unknown value.");
            }
        }

        if (TryGetValueIgnoreCase(entryMap, "fileKind", out var fileKindValue))
        {
            if (TryParseEnum(fileKindValue, out BlueprintFileKind fileKind))
            {
                entry.FileKind = fileKind;
            }
            else
            {
                errors.Add($"{filePath}: {context}.fileKind has an unknown value.");
            }
        }

        if (TryGetValueIgnoreCase(entryMap, "contentId", out var contentIdValue))
        {
            entry.ContentId = ReadString(contentIdValue);
        }

        if (TryGetValueIgnoreCase(entryMap, "size", out var sizeValue))
        {
            if (TryReadLong(sizeValue, out var size))
            {
                entry.Size = size;
            }
            else
            {
                errors.Add($"{filePath}: {context}.size must be an integer.");
            }
        }

        if (TryGetValueIgnoreCase(entryMap, "owner", out var ownerValue))
        {
            entry.Owner = ReadString(ownerValue);
        }

        if (TryGetValueIgnoreCase(entryMap, "perms", out var permsValue))
        {
            entry.Perms = ReadString(permsValue);
        }

        if (TryGetValueIgnoreCase(entryMap, "mtimeMs", out var mtimeMsValue))
        {
            if (TryReadLong(mtimeMsValue, out var mtimeMs))
            {
                entry.MtimeMs = mtimeMs;
            }
            else
            {
                errors.Add($"{filePath}: {context}.mtimeMs must be an integer.");
            }
        }

        return entry;
    }
}
