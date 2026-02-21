using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

#nullable enable

namespace Uplink2.Tests;

/// <summary>Unit tests for command-line token parsing in SystemCallParser.</summary>
public sealed class SystemCallParserTest
{
    /// <summary>Ensures empty or whitespace-only input fails with the expected error.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\t")]
    public void TryParse_EmptyInput_ReturnsEmptyCommandError(string? commandLine)
    {
        var result = InvokeTryParse(commandLine);

        Assert.False(result.Ok);
        Assert.Equal(string.Empty, result.Command);
        Assert.Empty(result.Arguments);
        Assert.Equal("empty command.", result.ErrorMessage);
    }

    /// <summary>Ensures unclosed double quote is rejected with a specific parse error.</summary>
    [Fact]
    public void TryParse_UnclosedQuote_ReturnsQuoteError()
    {
        var result = InvokeTryParse("echo \"hello");

        Assert.False(result.Ok);
        Assert.Equal(string.Empty, result.Command);
        Assert.Empty(result.Arguments);
        Assert.Equal("unclosed double quote in command.", result.ErrorMessage);
    }

    /// <summary>Ensures a bare command without arguments parses successfully.</summary>
    [Fact]
    public void TryParse_BareCommand_ParsesSuccessfully()
    {
        var result = InvokeTryParse("ls");

        Assert.True(result.Ok);
        Assert.Equal("ls", result.Command);
        Assert.Empty(result.Arguments);
        Assert.Equal(string.Empty, result.ErrorMessage);
    }

    /// <summary>Ensures multiple spaces/tabs outside quotes are treated as one separator.</summary>
    [Fact]
    public void TryParse_IgnoresRepeatedWhitespaceOutsideQuotes()
    {
        var result = InvokeTryParse("cat   /tmp/a.txt\t\t/tmp/b.txt");

        Assert.True(result.Ok);
        Assert.Equal("cat", result.Command);
        Assert.Equal(new[] { "/tmp/a.txt", "/tmp/b.txt" }, result.Arguments);
    }

    /// <summary>Ensures quoted segments preserve inner spaces as a single argument.</summary>
    [Fact]
    public void TryParse_QuotedArgument_PreservesInnerSpaces()
    {
        var result = InvokeTryParse("echo \"hello world\" test");

        Assert.True(result.Ok);
        Assert.Equal("echo", result.Command);
        Assert.Equal(new[] { "hello world", "test" }, result.Arguments);
    }

    /// <summary>Ensures quoted command token is supported when command itself is quoted.</summary>
    [Fact]
    public void TryParse_QuotedCommandToken_ParsesSuccessfully()
    {
        var result = InvokeTryParse("\"pwd\" /tmp");

        Assert.True(result.Ok);
        Assert.Equal("pwd", result.Command);
        Assert.Equal(new[] { "/tmp" }, result.Arguments);
    }

    /// <summary>Ensures empty quoted argument is preserved as an empty string token.</summary>
    [Fact]
    public void TryParse_EmptyQuotedArgument_IsPreserved()
    {
        var result = InvokeTryParse("echo \"\" tail");

        Assert.True(result.Ok);
        Assert.Equal("echo", result.Command);
        Assert.Equal(new[] { string.Empty, "tail" }, result.Arguments);
    }

    private static ParseResult InvokeTryParse(string? commandLine)
    {
        var parserType = RequireRuntimeType("Uplink2.Runtime.Syscalls.SystemCallParser");
        var tryParse = parserType.GetMethod(
            "TryParse",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(tryParse);

        object?[] args = { commandLine, null, null, null };
        var ok = tryParse!.Invoke(null, args) as bool?;
        Assert.NotNull(ok);

        var command = args[1] as string ?? string.Empty;
        var arguments = args[2] as IReadOnlyList<string> ?? Array.Empty<string>();
        var error = args[3] as string ?? string.Empty;
        return new ParseResult(ok.Value, command, arguments.ToArray(), error);
    }

    private static Type RequireRuntimeType(string fullTypeName)
    {
        var type = typeof(Uplink2.Runtime.Syscalls.SystemCallResult).Assembly.GetType(fullTypeName);
        Assert.NotNull(type);
        return type!;
    }

    private sealed record ParseResult(bool Ok, string Command, IReadOnlyList<string> Arguments, string ErrorMessage);
}
