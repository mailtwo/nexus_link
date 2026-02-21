using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using YamlDotNet.Serialization;

#nullable enable

namespace Uplink2.Blueprint;

/// <summary>Loads campaign/scenario/spec YAML files into blueprint model instances.</summary>
public sealed partial class BlueprintYamlReader
{
    private readonly IDeserializer deserializer;
    private readonly Action<string>? warningSink;

    /// <summary>Creates a reader configured for flexible YAML object deserialization.</summary>
    public BlueprintYamlReader(Action<string>? warningSink = null)
    {
        this.warningSink = warningSink;
        deserializer = new DeserializerBuilder()
            .IgnoreUnmatchedProperties()
            .Build();
    }

    /// <summary>Loads all YAML files under the given directory into a blueprint catalog.</summary>
    public BlueprintCatalog ReadDirectory(
        string directoryPath,
        string searchPattern = "*.yaml",
        SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new ArgumentException("Directory path cannot be empty.", nameof(directoryPath));
        }

        if (!Directory.Exists(directoryPath))
        {
            throw new DirectoryNotFoundException($"Directory not found: {directoryPath}");
        }

        var filePaths = Directory
            .GetFiles(directoryPath, searchPattern, searchOption)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        return ReadFiles(filePaths);
    }

    /// <summary>Loads the given YAML files into a blueprint catalog.</summary>
    public BlueprintCatalog ReadFiles(IEnumerable<string> yamlPaths)
    {
        if (yamlPaths is null)
        {
            throw new ArgumentNullException(nameof(yamlPaths));
        }

        var filePaths = yamlPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        var catalog = new BlueprintCatalog();
        var errors = new List<string>();

        foreach (var filePath in filePaths)
        {
            if (!File.Exists(filePath))
            {
                errors.Add($"{filePath}: file not found.");
                continue;
            }

            ReadSingleFile(filePath, catalog, errors);
        }

        if (errors.Count > 0)
        {
            throw new InvalidDataException("Blueprint YAML read failed:\n- " + string.Join("\n- ", errors));
        }

        return catalog;
    }

    private void ReadSingleFile(string filePath, BlueprintCatalog catalog, List<string> errors)
    {
        object? rawDocument;
        try
        {
            using var reader = File.OpenText(filePath);
            rawDocument = deserializer.Deserialize(reader);
        }
        catch (Exception ex)
        {
            errors.Add($"{filePath}: YAML parse error: {ex.Message}");
            return;
        }

        if (rawDocument is null)
        {
            return;
        }

        var normalizedRoot = NormalizeYamlValue(rawDocument);
        if (normalizedRoot is not Dictionary<string, object?> rootMap)
        {
            errors.Add($"{filePath}: document root must be a mapping.");
            return;
        }

        ParseServerSpecs(rootMap, filePath, catalog, errors);
        ParseScenarios(rootMap, filePath, catalog, errors, warningSink);
        ParseCampaigns(rootMap, filePath, catalog, errors);
    }
}
