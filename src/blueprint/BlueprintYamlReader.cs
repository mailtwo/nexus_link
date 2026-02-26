using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using YamlDotNet.Serialization;
using Godot;

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

        if (!DirectoryExists(directoryPath))
        {
            throw new DirectoryNotFoundException($"Directory not found: {directoryPath}");
        }

        var filePaths = GetFiles(directoryPath, searchPattern, searchOption)
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
            if (!FileExists(filePath))
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
            using var reader = new StringReader(ReadAllText(filePath));
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

    private static bool IsGodotVirtualPath(string path)
    {
        return path.StartsWith("res://", StringComparison.Ordinal) ||
               path.StartsWith("user://", StringComparison.Ordinal);
    }

    private static bool DirectoryExists(string directoryPath)
    {
        if (!IsGodotVirtualPath(directoryPath))
        {
            return Directory.Exists(directoryPath);
        }

        using var dir = DirAccess.Open(directoryPath);
        return dir is not null;
    }

    private static IEnumerable<string> GetFiles(
        string directoryPath,
        string searchPattern,
        SearchOption searchOption)
    {
        if (!IsGodotVirtualPath(directoryPath))
        {
            return Directory.GetFiles(directoryPath, searchPattern, searchOption);
        }

        return EnumerateGodotVirtualFiles(
            directoryPath,
            searchPattern,
            recursive: searchOption == SearchOption.AllDirectories);
    }

    private static IEnumerable<string> EnumerateGodotVirtualFiles(
        string rootPath,
        string searchPattern,
        bool recursive)
    {
        var files = new List<string>();
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(rootPath.TrimEnd('/'));

        while (pendingDirectories.Count > 0)
        {
            var currentDirectory = pendingDirectories.Pop();
            using var dir = DirAccess.Open(currentDirectory);
            if (dir is null)
            {
                continue;
            }

            dir.ListDirBegin();
            while (true)
            {
                var entryName = dir.GetNext();
                if (string.IsNullOrEmpty(entryName))
                {
                    break;
                }

                if (entryName is "." or "..")
                {
                    continue;
                }

                var entryPath = currentDirectory + "/" + entryName;
                if (dir.CurrentIsDir())
                {
                    if (recursive)
                    {
                        pendingDirectories.Push(entryPath);
                    }

                    continue;
                }

                if (MatchesSearchPattern(entryName, searchPattern))
                {
                    files.Add(entryPath);
                }
            }

            dir.ListDirEnd();
        }

        return files;
    }

    private static bool FileExists(string filePath)
    {
        return IsGodotVirtualPath(filePath)
            ? Godot.FileAccess.FileExists(filePath)
            : File.Exists(filePath);
    }

    private static string ReadAllText(string filePath)
    {
        if (!IsGodotVirtualPath(filePath))
        {
            return File.ReadAllText(filePath);
        }

        using var file = Godot.FileAccess.Open(filePath, Godot.FileAccess.ModeFlags.Read);
        if (file is null)
        {
            throw new IOException($"Failed to open YAML file '{filePath}' with Godot FileAccess.");
        }

        return file.GetAsText();
    }

    private static bool MatchesSearchPattern(string fileName, string searchPattern)
    {
        if (string.IsNullOrWhiteSpace(searchPattern) ||
            string.Equals(searchPattern, "*", StringComparison.Ordinal))
        {
            return true;
        }

        if (searchPattern.StartsWith("*.", StringComparison.Ordinal))
        {
            var extension = searchPattern[1..];
            return fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(fileName, searchPattern, StringComparison.OrdinalIgnoreCase);
    }
}
