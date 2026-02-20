using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

#nullable enable

namespace Uplink2.Blueprint;

public sealed partial class BlueprintYamlReader
{
    private static Dictionary<string, object?>? AsMap(
        object? value,
        string filePath,
        string context,
        List<string> errors,
        bool allowNullAsEmpty)
    {
        if (value is null)
        {
            return allowNullAsEmpty ? new Dictionary<string, object?>(StringComparer.Ordinal) : null;
        }

        if (value is Dictionary<string, object?> map)
        {
            return map;
        }

        errors.Add($"{filePath}: {context} must be a mapping.");
        return null;
    }

    private static List<object?>? AsList(
        object? value,
        string filePath,
        string context,
        List<string> errors,
        bool allowNullAsEmpty)
    {
        if (value is null)
        {
            return allowNullAsEmpty ? new List<object?>() : null;
        }

        if (value is List<object?> list)
        {
            return list;
        }

        errors.Add($"{filePath}: {context} must be a list.");
        return null;
    }

    private static IEnumerable<string> ReadStringList(
        object? value,
        string filePath,
        string context,
        List<string> errors)
    {
        var list = AsList(value, filePath, context, errors, allowNullAsEmpty: true);
        if (list is null)
        {
            yield break;
        }

        for (var index = 0; index < list.Count; index++)
        {
            var text = ReadString(list[index]);
            if (string.IsNullOrWhiteSpace(text))
            {
                errors.Add($"{filePath}: {context}[{index}] cannot be empty.");
                continue;
            }

            yield return text;
        }
    }

    private static bool TryGetValueIgnoreCase(
        IReadOnlyDictionary<string, object?> map,
        string key,
        out object? value)
    {
        foreach (var pair in map)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static object? NormalizeYamlValue(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is IDictionary dictionary)
        {
            var map = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in dictionary)
            {
                var key = Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty;
                map[key] = NormalizeYamlValue(entry.Value);
            }

            return map;
        }

        if (value is IList list)
        {
            var values = new List<object?>(list.Count);
            foreach (var listItem in list)
            {
                values.Add(NormalizeYamlValue(listItem));
            }

            return values;
        }

        return value;
    }

    private static object ConvertToBlueprintValue(object? value)
    {
        if (value is Dictionary<string, object?> map)
        {
            var convertedMap = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var pair in map)
            {
                convertedMap[pair.Key] = ConvertToBlueprintValue(pair.Value);
            }

            return convertedMap;
        }

        if (value is List<object?> list)
        {
            var convertedList = new List<object>(list.Count);
            foreach (var listItem in list)
            {
                convertedList.Add(ConvertToBlueprintValue(listItem));
            }

            return convertedList;
        }

        return value!;
    }

    private static string ReadString(object? value)
    {
        return value switch
        {
            null => string.Empty,
            string text => text,
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
        };
    }

    private static bool TryReadBool(object? value, out bool result)
    {
        switch (value)
        {
            case bool boolValue:
                result = boolValue;
                return true;
            case string text when bool.TryParse(text, out var parsedBool):
                result = parsedBool;
                return true;
            case int intValue:
                result = intValue != 0;
                return true;
            case long longValue:
                result = longValue != 0;
                return true;
            default:
                result = false;
                return false;
        }
    }

    private static bool TryReadInt(object? value, out int result)
    {
        switch (value)
        {
            case int intValue:
                result = intValue;
                return true;
            case long longValue when longValue >= int.MinValue && longValue <= int.MaxValue:
                result = (int)longValue;
                return true;
            case string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                result = parsed;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static bool TryReadLong(object? value, out long result)
    {
        switch (value)
        {
            case int intValue:
                result = intValue;
                return true;
            case long longValue:
                result = longValue;
                return true;
            case string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                result = parsed;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static bool TryParseEnum<TEnum>(object? value, out TEnum parsed)
        where TEnum : struct, Enum
    {
        parsed = default;
        if (value is null)
        {
            return false;
        }

        var text = ReadString(value);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalizedInput = NormalizeEnumToken(text);
        foreach (var enumName in Enum.GetNames(typeof(TEnum)))
        {
            if (string.Equals(NormalizeEnumToken(enumName), normalizedInput, StringComparison.Ordinal))
            {
                parsed = Enum.Parse<TEnum>(enumName, ignoreCase: false);
                return true;
            }
        }

        return false;
    }

    private static string NormalizeEnumToken(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }
}
