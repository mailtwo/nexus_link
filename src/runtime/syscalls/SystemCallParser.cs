using System;
using System.Collections.Generic;
using System.Text;

namespace Uplink2.Runtime.Syscalls;

internal static class SystemCallParser
{
    internal static bool TryParse(
        string commandLine,
        out string command,
        out IReadOnlyList<string> arguments,
        out string errorMessage)
    {
        command = string.Empty;
        arguments = Array.Empty<string>();
        errorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(commandLine))
        {
            errorMessage = "empty command.";
            return false;
        }

        var tokens = new List<string>();
        var token = new StringBuilder(commandLine.Length);
        var inQuotes = false;
        var escapeNext = false;
        var tokenStarted = false;

        foreach (var ch in commandLine)
        {
            if (escapeNext)
            {
                token.Append(ch);
                tokenStarted = true;
                escapeNext = false;
                continue;
            }

            if (ch == '\\' && inQuotes)
            {
                escapeNext = true;
                tokenStarted = true;
                continue;
            }

            if (ch == '"')
            {
                inQuotes = !inQuotes;
                tokenStarted = true;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (tokenStarted)
                {
                    tokens.Add(token.ToString());
                    token.Clear();
                    tokenStarted = false;
                }

                continue;
            }

            token.Append(ch);
            tokenStarted = true;
        }

        if (escapeNext)
        {
            token.Append('\\');
        }

        if (inQuotes)
        {
            errorMessage = "unclosed double quote in command.";
            return false;
        }

        if (tokenStarted)
        {
            tokens.Add(token.ToString());
        }

        if (tokens.Count == 0 || string.IsNullOrWhiteSpace(tokens[0]))
        {
            errorMessage = "empty command.";
            return false;
        }

        command = tokens[0];
        if (tokens.Count > 1)
        {
            arguments = tokens.GetRange(1, tokens.Count - 1);
        }

        return true;
    }
}
