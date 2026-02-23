using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Uplink2.Runtime;

#nullable enable

namespace Uplink2.Runtime.Syscalls;

internal sealed class PrototypeSaveCommandHandler : ISystemCallHandler
{
    private const string UsageText = "save [0-9]";
    internal static Func<string, string> ResolveAbsoluteSlotPath { get; set; } = static path => ProjectSettings.GlobalizePath(path);

    public string Command => "save";

    public SystemCallResult Execute(SystemCallExecutionContext context, IReadOnlyList<string> arguments)
    {
        if (!TryParseSlot(arguments, UsageText, out var slot, out var parseFailure))
        {
            return parseFailure!;
        }

        var savePath = BuildSlotSavePath(slot);
        string absolutePath;
        try
        {
            absolutePath = ResolveAbsoluteSlotPath(savePath);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return SystemCallResultFactory.Failure(SystemCallErrorCode.InvalidArgs, ex.Message);
        }

        var saveResult = context.World.SaveGameToFile(absolutePath);
        if (!saveResult.Ok)
        {
            return ConvertSaveLoadFailure(saveResult);
        }

        return SystemCallResultFactory.Success(lines: new[] { $"game saved to slot {slot}." });
    }

    internal static bool TryParseSlot(
        IReadOnlyList<string> arguments,
        string usageText,
        out int slot,
        out SystemCallResult? failure)
    {
        slot = -1;
        failure = null;

        if (arguments.Count != 1)
        {
            failure = SystemCallResultFactory.Usage(usageText);
            return false;
        }

        var slotToken = arguments[0]?.Trim() ?? string.Empty;
        if (!int.TryParse(slotToken, NumberStyles.None, CultureInfo.InvariantCulture, out slot) ||
            slot is < 0 or > 9)
        {
            failure = SystemCallResultFactory.Failure(SystemCallErrorCode.InvalidArgs, "slot must be an integer from 0 to 9.");
            return false;
        }

        return true;
    }

    internal static string BuildSlotSavePath(int slot)
    {
        return $"user://saves/slot{slot}.uls1";
    }

    internal static SystemCallResult ConvertSaveLoadFailure(SaveLoadResult saveLoad)
    {
        var code = saveLoad.Code switch
        {
            SaveLoadErrorCode.InvalidArgs => SystemCallErrorCode.InvalidArgs,
            SaveLoadErrorCode.FormatError => SystemCallErrorCode.InvalidArgs,
            SaveLoadErrorCode.UnsupportedVersion => SystemCallErrorCode.InvalidArgs,
            SaveLoadErrorCode.IntegrityCheckFailed => SystemCallErrorCode.InvalidArgs,
            SaveLoadErrorCode.MissingRequiredChunk => SystemCallErrorCode.InvalidArgs,
            SaveLoadErrorCode.IoError => SystemCallErrorCode.InternalError,
            SaveLoadErrorCode.ScenarioRestoreFailed => SystemCallErrorCode.InternalError,
            SaveLoadErrorCode.StateApplyFailed => SystemCallErrorCode.InternalError,
            SaveLoadErrorCode.UnsupportedValueType => SystemCallErrorCode.InternalError,
            _ => SystemCallErrorCode.InternalError,
        };

        var message = string.IsNullOrWhiteSpace(saveLoad.Message)
            ? "save/load failed."
            : saveLoad.Message;
        return SystemCallResultFactory.Failure(code, message);
    }
}

internal sealed class PrototypeLoadCommandHandler : ISystemCallHandler
{
    private const string UsageText = "load [0-9]";

    public string Command => "load";

    public SystemCallResult Execute(SystemCallExecutionContext context, IReadOnlyList<string> arguments)
    {
        if (!PrototypeSaveCommandHandler.TryParseSlot(arguments, UsageText, out var slot, out var parseFailure))
        {
            return parseFailure!;
        }

        var savePath = PrototypeSaveCommandHandler.BuildSlotSavePath(slot);
        string absolutePath;
        try
        {
            absolutePath = PrototypeSaveCommandHandler.ResolveAbsoluteSlotPath(savePath);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return SystemCallResultFactory.Failure(SystemCallErrorCode.InvalidArgs, ex.Message);
        }

        if (!File.Exists(absolutePath))
        {
            return SystemCallResultFactory.NotFound(savePath);
        }

        var loadResult = context.World.LoadGameFromFile(absolutePath);
        if (!loadResult.Ok)
        {
            return PrototypeSaveCommandHandler.ConvertSaveLoadFailure(loadResult);
        }

        if (!TryBuildWorkstationTransition(context.World, out var transition, out var motdLines, out var transitionFailure))
        {
            return transitionFailure!;
        }

        var lines = new List<string>(motdLines.Count + 1);
        foreach (var motdLine in motdLines)
        {
            lines.Add(motdLine ?? string.Empty);
        }

        lines.Add($"game loaded from slot {slot}.");
        return SystemCallResultFactory.Success(lines: lines, nextCwd: transition.NextCwd, data: transition);
    }

    private static bool TryBuildWorkstationTransition(
        WorldRuntime world,
        out TerminalContextTransition transition,
        out IReadOnlyList<string> motdLines,
        out SystemCallResult? failure)
    {
        transition = new TerminalContextTransition();
        motdLines = Array.Empty<string>();
        failure = null;

        var workstation = world.PlayerWorkstationServer;
        if (workstation is null)
        {
            failure = SystemCallResultFactory.Failure(SystemCallErrorCode.InternalError, "player workstation is not initialized.");
            return false;
        }

        var preferredUserId = world.DefaultUserId?.Trim() ?? string.Empty;
        string userKey;
        if (string.IsNullOrWhiteSpace(preferredUserId) ||
            !world.TryResolveUserKeyByUserId(workstation, preferredUserId, out userKey))
        {
            userKey = workstation.Users.Keys
                .OrderBy(static value => value, StringComparer.Ordinal)
                .FirstOrDefault() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(userKey))
        {
            failure = SystemCallResultFactory.Failure(SystemCallErrorCode.InternalError, "no available user on player workstation.");
            return false;
        }

        var userId = world.ResolvePromptUser(workstation, userKey);
        if (string.IsNullOrWhiteSpace(userId))
        {
            failure = SystemCallResultFactory.Failure(SystemCallErrorCode.InternalError, "failed to resolve workstation user id.");
            return false;
        }

        transition = new TerminalContextTransition
        {
            NextNodeId = workstation.NodeId,
            NextUserId = userId,
            NextPromptUser = userId,
            NextPromptHost = world.ResolvePromptHost(workstation),
            NextCwd = "/",
            ClearTerminalBeforeOutput = true,
            ActivateMotdAnchor = true,
        };
        motdLines = world.ResolveMotdLinesForLogin(workstation, userKey);
        return true;
    }
}
