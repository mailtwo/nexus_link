using Miniscript;
using System;
using System.Collections.Generic;

#nullable enable

namespace Uplink2.Runtime.MiniScript;

internal static class ArgsIntrinsics
{
    internal static void InjectArgs(Interpreter interpreter, IReadOnlyList<string>? scriptArguments)
    {
        var argv = new ValList();
        var normalizedArgs = scriptArguments ?? Array.Empty<string>();
        foreach (var scriptArgument in normalizedArgs)
        {
            argv.values.Add(new ValString(scriptArgument ?? string.Empty));
        }

        interpreter.SetGlobalValue("argv", argv);
        interpreter.SetGlobalValue("argc", new ValNumber(argv.values.Count));
    }
}
