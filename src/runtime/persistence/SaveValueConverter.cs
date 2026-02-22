using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;

#nullable enable

namespace Uplink2.Runtime.Persistence;

internal static class SaveValueConverter
{
    internal static bool TryToDto(object? value, out SaveValueDto dto, out string errorMessage)
    {
        dto = new SaveValueDto();
        errorMessage = string.Empty;

        if (value is null)
        {
            dto.Kind = SaveValueKind.Null;
            return true;
        }

        switch (value)
        {
            case bool boolValue:
                dto.Kind = SaveValueKind.Bool;
                dto.BoolValue = boolValue;
                return true;
            case int intValue:
                dto.Kind = SaveValueKind.Int;
                dto.IntValue = intValue;
                return true;
            case long longValue:
                dto.Kind = SaveValueKind.Long;
                dto.LongValue = longValue;
                return true;
            case short shortValue:
                dto.Kind = SaveValueKind.Int;
                dto.IntValue = shortValue;
                return true;
            case sbyte sbyteValue:
                dto.Kind = SaveValueKind.Int;
                dto.IntValue = sbyteValue;
                return true;
            case byte byteValue:
                dto.Kind = SaveValueKind.Int;
                dto.IntValue = byteValue;
                return true;
            case ushort ushortValue:
                dto.Kind = SaveValueKind.Int;
                dto.IntValue = ushortValue;
                return true;
            case uint uintValue when uintValue <= int.MaxValue:
                dto.Kind = SaveValueKind.Int;
                dto.IntValue = (int)uintValue;
                return true;
            case uint uintValue:
                dto.Kind = SaveValueKind.Long;
                dto.LongValue = uintValue;
                return true;
            case ulong ulongValue when ulongValue <= long.MaxValue:
                dto.Kind = SaveValueKind.Long;
                dto.LongValue = (long)ulongValue;
                return true;
            case float floatValue:
                dto.Kind = SaveValueKind.Double;
                dto.DoubleValue = floatValue;
                return true;
            case double doubleValue:
                dto.Kind = SaveValueKind.Double;
                dto.DoubleValue = doubleValue;
                return true;
            case decimal decimalValue:
                try
                {
                    dto.Kind = SaveValueKind.Double;
                    dto.DoubleValue = Convert.ToDouble(decimalValue, CultureInfo.InvariantCulture);
                    return true;
                }
                catch (OverflowException)
                {
                    errorMessage = "decimal value is outside supported double range.";
                    return false;
                }
            case string stringValue:
                dto.Kind = SaveValueKind.String;
                dto.StringValue = stringValue;
                return true;
            case IDictionary<string, object> map:
                return TryObjectMapToDto(map, out dto, out errorMessage);
            case IDictionary dictionary:
                return TryUntypedMapToDto(dictionary, out dto, out errorMessage);
            case IEnumerable enumerable when value is not string:
                return TryListToDto(enumerable, out dto, out errorMessage);
            default:
                errorMessage = $"unsupported runtime value type '{value.GetType().FullName}'.";
                return false;
        }
    }

    internal static bool TryFromDto(SaveValueDto dto, out object? value, out string errorMessage)
    {
        value = null;
        errorMessage = string.Empty;

        switch (dto.Kind)
        {
            case SaveValueKind.Null:
                value = null;
                return true;
            case SaveValueKind.Bool:
                value = dto.BoolValue;
                return true;
            case SaveValueKind.Int:
                value = dto.IntValue;
                return true;
            case SaveValueKind.Long:
                value = dto.LongValue;
                return true;
            case SaveValueKind.Double:
                value = dto.DoubleValue;
                return true;
            case SaveValueKind.String:
                value = dto.StringValue ?? string.Empty;
                return true;
            case SaveValueKind.List:
                return TryListFromDto(dto, out value, out errorMessage);
            case SaveValueKind.Map:
                return TryMapFromDto(dto, out value, out errorMessage);
            default:
                errorMessage = $"unsupported save value kind '{dto.Kind}'.";
                return false;
        }
    }

    private static bool TryObjectMapToDto(
        IEnumerable<KeyValuePair<string, object>> map,
        out SaveValueDto dto,
        out string errorMessage)
    {
        dto = new SaveValueDto
        {
            Kind = SaveValueKind.Map,
            MapValue = new Dictionary<string, SaveValueDto>(StringComparer.Ordinal),
        };
        errorMessage = string.Empty;

        foreach (var pair in map)
        {
            if (pair.Key is null)
            {
                errorMessage = "map key cannot be null.";
                return false;
            }

            if (!TryToDto(pair.Value, out var childValue, out var childError))
            {
                errorMessage = $"map key '{pair.Key}': {childError}";
                return false;
            }

            dto.MapValue[pair.Key] = childValue;
        }

        return true;
    }

    private static bool TryUntypedMapToDto(
        IDictionary dictionary,
        out SaveValueDto dto,
        out string errorMessage)
    {
        dto = new SaveValueDto
        {
            Kind = SaveValueKind.Map,
            MapValue = new Dictionary<string, SaveValueDto>(StringComparer.Ordinal),
        };
        errorMessage = string.Empty;

        foreach (DictionaryEntry entry in dictionary)
        {
            var key = Convert.ToString(entry.Key, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(key))
            {
                errorMessage = "map key cannot be empty.";
                return false;
            }

            if (!TryToDto(entry.Value, out var childValue, out var childError))
            {
                errorMessage = $"map key '{key}': {childError}";
                return false;
            }

            dto.MapValue[key] = childValue;
        }

        return true;
    }

    private static bool TryListToDto(
        IEnumerable values,
        out SaveValueDto dto,
        out string errorMessage)
    {
        dto = new SaveValueDto
        {
            Kind = SaveValueKind.List,
            ListValue = [],
        };
        errorMessage = string.Empty;

        var index = 0;
        foreach (var item in values)
        {
            if (!TryToDto(item, out var child, out var childError))
            {
                errorMessage = $"list index {index}: {childError}";
                return false;
            }

            dto.ListValue.Add(child);
            index++;
        }

        return true;
    }

    private static bool TryListFromDto(
        SaveValueDto dto,
        out object? value,
        out string errorMessage)
    {
        var list = new List<object?>(dto.ListValue?.Count ?? 0);
        var items = dto.ListValue ?? [];
        for (var index = 0; index < items.Count; index++)
        {
            if (!TryFromDto(items[index], out var child, out var childError))
            {
                value = null;
                errorMessage = $"list index {index}: {childError}";
                return false;
            }

            list.Add(child);
        }

        value = list;
        errorMessage = string.Empty;
        return true;
    }

    private static bool TryMapFromDto(
        SaveValueDto dto,
        out object? value,
        out string errorMessage)
    {
        var map = new Dictionary<string, object>(StringComparer.Ordinal);
        var source = dto.MapValue ?? [];
        foreach (var pair in source)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                value = null;
                errorMessage = "map key cannot be empty.";
                return false;
            }

            if (!TryFromDto(pair.Value, out var child, out var childError))
            {
                value = null;
                errorMessage = $"map key '{pair.Key}': {childError}";
                return false;
            }

            map[pair.Key] = child!;
        }

        value = map;
        errorMessage = string.Empty;
        return true;
    }
}
