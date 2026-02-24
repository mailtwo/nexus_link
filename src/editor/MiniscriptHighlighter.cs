using Godot;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Uplink2.Editor;

/// <summary>Provides MiniScript syntax highlighting for CodeEdit.</summary>
public partial class MiniscriptHighlighter : CodeHighlighter
{
    private static readonly List<string> NumericIntrinsics = new()
    {
        "abs", "acos", "asin", "atan", "ceil", "char", "cos",
        "floor", "log", "round", "rnd", "pi", "sign", "sin",
        "sqrt", "str", "tan",
    };

    private static readonly List<string> StringIntrinsics = new()
    {
        "indexOf", "insert", "len", "val", "code", "remove",
        "lower", "upper", "replace", "split",
    };

    private static readonly List<string> ListMapIntrinsics = new()
    {
        "hasIndex", "indexOf", "insert", "join", "push", "pop",
        "pull", "indexes", "values", "len", "sum", "sort",
        "shuffle", "remove", "range",
    };

    private static readonly List<string> GlobalIntrinsics = new()
    {
        "print", "time", "wait",
    };

    private static readonly List<string> Variables = new()
    {
        "true", "false", "locals", "globals", "null",
    };

    private static readonly List<string> Tokens = new()
    {
        "function", "while", "end", "if", "for", "then",
        "new", "else", "break", "continue", "and", "or", "not",
    };

    /// <summary>Token keyword color.</summary>
    [Export]
    public Color TokenColor = Colors.Salmon;

    /// <summary>Intrinsic function color.</summary>
    [Export]
    public Color IntrinsicsColor = Colors.PaleGreen;

    /// <summary>Built-in variable color.</summary>
    [Export]
    public Color VariablesColor = Colors.DodgerBlue.Lightened(0.35f);

    /// <summary>Line comment color.</summary>
    [Export]
    public Color CommentColor = Colors.Gray.Darkened(0.25f);

    /// <summary>String literal color.</summary>
    [Export]
    public Color StringColor = Color.FromString("#eee8aa", Colors.Gold.Lightened(0.4f));

    /// <summary>Bracket color.</summary>
    [Export]
    public Color BracketColor = Colors.Gold;

    /// <summary>Operator color.</summary>
    [Export]
    public Color OpColor = Colors.Salmon;

    private readonly Dictionary<int, int> colorRegionCache = new();
    private readonly Dictionary<string, Color> memberKeywordColors = new();
    private readonly Dictionary<string, Color> keywordColors = new();

    /// <summary>Initializes MiniScript token and region color rules.</summary>
    public MiniscriptHighlighter()
    {
        Tokens.ForEach(token => keywordColors[token] = TokenColor);

        NumericIntrinsics
            .Concat(GlobalIntrinsics)
            .Concat(StringIntrinsics)
            .Concat(ListMapIntrinsics)
            .ToList()
            .ForEach(intrinsic => keywordColors[intrinsic] = IntrinsicsColor);

        Variables.ForEach(variable => keywordColors[variable] = VariablesColor);

        AddColorRegion("//", string.Empty, CommentColor);
        AddColorRegion("\"", "\"", StringColor);
    }

    /// <inheritdoc/>
    public override void _ClearHighlightingCache()
    {
        colorRegionCache.Clear();
    }

    /// <inheritdoc/>
    // Translated from Godot's syntax_highlighter.cpp implementation and adapted for MiniScript.
    public override Godot.Collections.Dictionary _GetLineSyntaxHighlighting(int pLine)
    {
        var colorMap = new Godot.Collections.Dictionary();

        var prevIsChar = false;
        var prevIsNumber = false;
        var inKeyword = false;
        var inWord = false;
        var inFunctionName = false;
        var inMemberVariable = false;
        var isHexNotation = false;
        var keywordColor = new Color();
        var color = new Color();

        var textEdit = GetTextEdit();
        var fontColor = textEdit.GetThemeColor("font_color");

        colorRegionCache[pLine] = -1;
        var inRegion = -1;
        if (pLine != 0)
        {
            var prevRegionLine = pLine - 1;
            while (prevRegionLine > 0 && colorRegionCache.ContainsKey(prevRegionLine))
            {
                prevRegionLine--;
            }

            for (var i = prevRegionLine; i < pLine - 1; i++)
            {
                GetLineSyntaxHighlighting(i);
            }

            if (!colorRegionCache.ContainsKey(pLine - 1))
            {
                GetLineSyntaxHighlighting(pLine - 1);
            }

            inRegion = colorRegionCache[pLine - 1];
        }

        var str = textEdit.GetLine(pLine);
        var lineLength = str.Length;
        var prevColor = new Color();

        if (inRegion != -1 && str.Length == 0)
        {
            colorRegionCache[pLine] = inRegion;
        }

        for (var j = 0; j < lineLength; j++)
        {
            var highlighterInfo = new Godot.Collections.Dictionary();

            var prevChar = j > 0 ? str[j - 1] : 'z';
            color = fontColor;
            var isChar = !IsSymbol(str[j]);
            var isASymbol = IsSymbol(str[j]);
            var isNumber = IsDigit(str[j]);
            var isABracket = isASymbol && IsBracket(str[j]);
            var isAOp = isASymbol && IsOp(str[j]);
            var isAEq = isASymbol && IsEq(str[j]);

            if (isASymbol || inRegion != -1)
            {
                var from = j;

                if (inRegion == -1)
                {
                    for (; from < lineLength; from++)
                    {
                        if (str[from] == '\\')
                        {
                            from++;
                            continue;
                        }

                        break;
                    }
                }

                if (from != lineLength)
                {
                    if (inRegion == -1)
                    {
                        var c = -1;
                        foreach (var key in ColorRegions.Keys)
                        {
                            c++;
                            var charsLeft = lineLength - from;
                            var split = key.AsString().Split(" ");
                            var startKey = split[0];
                            var endKey = split.Length == 1 ? string.Empty : split[1];
                            var startKeyLength = startKey.Length;
                            var endKeyLength = endKey.Length;
                            if (charsLeft < startKeyLength)
                            {
                                continue;
                            }

                            var match = true;
                            for (var k = 0; k < startKeyLength; k++)
                            {
                                if (startKey[k] != str[from + k])
                                {
                                    match = false;
                                    break;
                                }
                            }

                            if (!match)
                            {
                                continue;
                            }

                            inRegion = c;
                            from += startKeyLength;

                            if (endKeyLength == 0 || from + endKeyLength > lineLength)
                            {
                                if (from + endKeyLength > lineLength && (startKey == "\"" || startKey == "'"))
                                {
                                    if (str.IndexOf("\\", from) >= 0)
                                    {
                                        break;
                                    }
                                }

                                prevColor = ColorRegions[key].AsColor();
                                highlighterInfo["color"] = ColorRegions[key].AsColor();
                                colorMap[j] = highlighterInfo;
                                j = lineLength;
                            }

                            break;
                        }

                        if (j == lineLength)
                        {
                            continue;
                        }
                    }

                    if (inRegion != -1)
                    {
                        var key = ColorRegions.Keys.ElementAt(inRegion).AsString();
                        var split = key.Split(" ");
                        var startKey = split[0];
                        var endKey = split.Length == 1 ? string.Empty : split[1];
                        var isString = startKey == "\"" || startKey == "'";

                        var regionColor = ColorRegions[key].AsColor();
                        prevColor = regionColor;
                        highlighterInfo["color"] = regionColor;
                        colorMap[j] = highlighterInfo;

                        var regionEndIndex = -1;
                        var endKeyLength = endKey.Length;
                        for (; from < lineLength; from++)
                        {
                            if (lineLength - from < endKeyLength)
                            {
                                if (!isString || str.IndexOf("\\", from) < 0)
                                {
                                    break;
                                }
                            }

                            if (!IsSymbol(str[from]))
                            {
                                continue;
                            }

                            if (str[from] == '\\')
                            {
                                if (isString)
                                {
                                    var escapeCharHighlighterInfo = new Godot.Collections.Dictionary
                                    {
                                        ["color"] = SymbolColor,
                                    };
                                    colorMap[from] = escapeCharHighlighterInfo;
                                }

                                from++;

                                if (isString)
                                {
                                    var regionContinueHighlighterInfo = new Godot.Collections.Dictionary();
                                    prevColor = regionColor;
                                    regionContinueHighlighterInfo["color"] = regionColor;
                                    colorMap[from + 1] = regionContinueHighlighterInfo;
                                }

                                continue;
                            }

                            regionEndIndex = from;
                            for (var k = 0; k < endKeyLength; k++)
                            {
                                if (endKey[k] != str[from + k])
                                {
                                    regionEndIndex = -1;
                                    break;
                                }
                            }

                            if (regionEndIndex != -1)
                            {
                                break;
                            }
                        }

                        j = from + (endKeyLength - 1);
                        if (regionEndIndex == -1)
                        {
                            colorRegionCache[pLine] = inRegion;
                        }

                        inRegion = -1;
                        prevIsChar = false;
                        prevIsNumber = false;
                        continue;
                    }
                }
            }

            if (isHexNotation && (IsHexDigit(str[j]) || isNumber))
            {
                isNumber = true;
            }
            else
            {
                isHexNotation = false;
            }

            if ((str[j] == '.' || str[j] == 'x' || str[j] == '_' || str[j] == 'f' || str[j] == 'e') &&
                !inWord &&
                prevIsNumber &&
                !isNumber)
            {
                isNumber = true;
                isASymbol = false;
                isChar = false;

                if (str[j] == 'x' && str[j - 1] == '0')
                {
                    isHexNotation = true;
                }
            }

            if (!inWord && (IsAsciiChar(str[j]) || IsUnderscore(str[j])) && !isNumber)
            {
                inWord = true;
            }

            if ((inKeyword || inWord) && !isHexNotation)
            {
                isNumber = false;
            }

            if (isASymbol && str[j] != '.' && inWord)
            {
                inWord = false;
            }

            if (!isChar)
            {
                inKeyword = false;
            }

            if (!inKeyword && isChar && !prevIsChar)
            {
                var to = j;
                while (to < lineLength && !IsSymbol(str[to]))
                {
                    to++;
                }

                var word = str[j..to];
                var col = new Color();
                if (keywordColors.ContainsKey(word))
                {
                    col = keywordColors[word];
                }
                else if (memberKeywordColors.ContainsKey(word))
                {
                    col = memberKeywordColors[word];
                    for (var k = j - 1; k >= 0; k--)
                    {
                        if (str[k] == '.')
                        {
                            col = new Color();
                            break;
                        }

                        if (str[k] > 32)
                        {
                            break;
                        }
                    }
                }

                if (col != new Color())
                {
                    inKeyword = true;
                    keywordColor = col;
                }
            }

            if (!inFunctionName && inWord && !inKeyword)
            {
                var k = j;
                while (k < lineLength - 1 && !IsSymbol(str[k]) && str[k] != '\t' && str[k] != ' ')
                {
                    k++;
                }

                while (k < lineLength - 1 && (str[k] == '\t' || str[k] == ' '))
                {
                    k++;
                }

                if (str[k] == '(')
                {
                    inFunctionName = true;
                }
            }

            if (!inFunctionName && !inMemberVariable && !inKeyword && !isNumber && inWord)
            {
                var k = j;
                while (k > 0 && !IsSymbol(str[k]) && str[k] != '\t' && str[k] != ' ')
                {
                    k--;
                }

                if (str[k] == '.')
                {
                    inMemberVariable = true;
                }
            }

            if (isASymbol)
            {
                inFunctionName = false;
                inMemberVariable = false;
            }

            if (inKeyword)
            {
                color = keywordColor;
            }
            else if (inMemberVariable)
            {
                color = MemberVariableColor;
            }
            else if (inFunctionName)
            {
                color = FunctionColor;
            }
            else if (isABracket)
            {
                color = BracketColor;
            }
            else if (isAEq && (IsOp(prevChar) || prevChar == '='))
            {
                color = OpColor;
            }
            else if (isAOp)
            {
                color = OpColor;
            }
            else if (isASymbol)
            {
                color = SymbolColor;
            }
            else if (isNumber)
            {
                color = NumberColor;
            }

            prevIsChar = isChar;
            prevIsNumber = isNumber;

            if (color != prevColor)
            {
                prevColor = color;
                highlighterInfo["color"] = color;

                if (isAEq && prevChar == '=')
                {
                    colorMap[j - 1] = highlighterInfo;
                }
                else
                {
                    colorMap[j] = highlighterInfo;
                }
            }
        }

        return colorMap;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsSymbol(char c)
    {
        return c != '_' &&
               ((c >= '!' && c <= '/') ||
                (c >= ':' && c <= '@') ||
                (c >= '[' && c <= '`') ||
                (c >= '{' && c <= '~') ||
                c == '\t' ||
                c == ' ');
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsDigit(char c)
    {
        return c is >= '0' and <= '9';
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsHexDigit(char c)
    {
        return IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsAsciiChar(char c)
    {
        return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsUnderscore(char c)
    {
        return c == '_';
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsBracket(char c)
    {
        return c is '[' or ']' or '(' or ')' or '{' or '}';
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsOp(char c)
    {
        return c is '+' or '-' or '*' or '/' or '%' or '^' or '!' or '<' or '>';
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsEq(char c)
    {
        return c == '=';
    }
}
