using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using YamlDotNet.RepresentationModel;

namespace Uplink2.Runtime;

public partial class WorldRuntime
{
    private const string UnknownRegionId = "Unknown";
    private const double MinLatitude = -90.0;
    private const double MaxLatitude = 90.0;
    private const double MinLongitude = -180.0;
    private const double MaxLongitude = 180.0;
    private const double RegionCoordinateTolerance = 0.000000001;

    private void EnsureRegionDataLoaded()
    {
        if (regionCatalogLoaded)
        {
            return;
        }

        var sourcePath = string.IsNullOrWhiteSpace(RegionDataFile)
            ? DefaultRegionDataFile
            : RegionDataFile.Trim();
        regionCatalog = LoadRegionCatalog(sourcePath, static warning => Godot.GD.PushWarning(warning));
        regionCatalogLoaded = true;
    }

    private bool TryGetRegion(string regionId, out RegionDefinition region)
    {
        EnsureRegionDataLoaded();
        if (string.IsNullOrWhiteSpace(regionId))
        {
            region = RegionDefinition.Empty;
            return false;
        }

        return regionCatalog.ById.TryGetValue(regionId.Trim(), out region);
    }

    private IReadOnlyList<RegionDefinition> GetContainingRegions(double lat, double lng)
    {
        EnsureRegionDataLoaded();
        if (lat < MinLatitude || lat > MaxLatitude || lng < MinLongitude || lng > MaxLongitude)
        {
            return Array.Empty<RegionDefinition>();
        }

        var containing = new List<RegionDefinition>();
        foreach (var region in regionCatalog.ByTotalAreaAscending)
        {
            foreach (var box in region.Boxes)
            {
                if (!IsPointInsideBox(lat, lng, box))
                {
                    continue;
                }

                containing.Add(region);
                break;
            }
        }

        return containing.Count == 0 ? Array.Empty<RegionDefinition>() : containing;
    }

    private static bool IsPointInsideBox(double lat, double lng, RegionBox box)
    {
        return lat >= box.MinLat &&
               lat <= box.MaxLat &&
               lng >= box.MinLng &&
               lng <= box.MaxLng;
    }

    private static RegionCatalog LoadRegionCatalog(string regionDataFilePath, Action<string> warningSink)
    {
        if (string.IsNullOrWhiteSpace(regionDataFilePath))
        {
            throw new InvalidDataException("RegionData file path cannot be empty.");
        }

        var sourcePath = regionDataFilePath.Trim();
        var yamlText = ReadAllTextFromPath(sourcePath);
        if (string.IsNullOrWhiteSpace(yamlText))
        {
            throw new InvalidDataException($"RegionData file '{sourcePath}' is empty.");
        }

        YamlStream yamlStream;
        try
        {
            yamlStream = new YamlStream();
            using var reader = new StringReader(yamlText);
            yamlStream.Load(reader);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"RegionData YAML parse failed for '{sourcePath}': {ex.Message}", ex);
        }

        if (yamlStream.Documents.Count == 0)
        {
            throw new InvalidDataException($"RegionData file '{sourcePath}' does not contain a YAML document.");
        }

        if (yamlStream.Documents[0].RootNode is not YamlMappingNode rootMap)
        {
            throw new InvalidDataException($"RegionData file '{sourcePath}' root must be a mapping.");
        }

        if (!TryGetMappingChildValue(rootMap, "regions", out var regionsNode))
        {
            throw new InvalidDataException($"RegionData file '{sourcePath}' must define a 'regions' mapping.");
        }

        if (regionsNode is not YamlMappingNode regionsMap)
        {
            throw new InvalidDataException($"RegionData file '{sourcePath}' key 'regions' must be a mapping.");
        }

        var regionsById = new Dictionary<string, RegionDefinition>(StringComparer.Ordinal);
        foreach (var regionPair in regionsMap.Children)
        {
            var regionId = ReadRequiredScalar(regionPair.Key, sourcePath, "regions.<regionId>");
            if (string.IsNullOrWhiteSpace(regionId))
            {
                throw new InvalidDataException($"RegionData file '{sourcePath}' contains an empty region id.");
            }

            if (!regionsById.TryAdd(regionId, RegionDefinition.Empty))
            {
                throw new InvalidDataException($"RegionData file '{sourcePath}' contains duplicate region '{regionId}'.");
            }

            if (regionPair.Value is not YamlMappingNode regionMap)
            {
                throw new InvalidDataException(
                    $"RegionData file '{sourcePath}' region '{regionId}' must be a mapping.");
            }

            if (!TryGetMappingChildValue(regionMap, "boxes", out var boxesNode))
            {
                throw new InvalidDataException(
                    $"RegionData file '{sourcePath}' region '{regionId}' must define 'boxes'.");
            }

            if (boxesNode is not YamlSequenceNode boxesSequence)
            {
                throw new InvalidDataException(
                    $"RegionData file '{sourcePath}' region '{regionId}'.boxes must be a list.");
            }

            if (boxesSequence.Children.Count == 0)
            {
                throw new InvalidDataException(
                    $"RegionData file '{sourcePath}' region '{regionId}' must contain at least one box.");
            }

            var boxes = new List<RegionBox>(boxesSequence.Children.Count);
            double totalArea = 0.0;
            for (var index = 0; index < boxesSequence.Children.Count; index++)
            {
                var boxContext = $"region '{regionId}' box[{index}]";
                if (boxesSequence.Children[index] is not YamlSequenceNode boxValues)
                {
                    throw new InvalidDataException(
                        $"RegionData file '{sourcePath}' {boxContext} must be a list with 4 numeric values.");
                }

                if (boxValues.Children.Count != 4)
                {
                    throw new InvalidDataException(
                        $"RegionData file '{sourcePath}' {boxContext} must contain exactly 4 values.");
                }

                var minLat = ReadCoordinate(boxValues.Children[0], sourcePath, $"{boxContext}.minLat");
                var minLng = ReadCoordinate(boxValues.Children[1], sourcePath, $"{boxContext}.minLng");
                var maxLat = ReadCoordinate(boxValues.Children[2], sourcePath, $"{boxContext}.maxLat");
                var maxLng = ReadCoordinate(boxValues.Children[3], sourcePath, $"{boxContext}.maxLng");

                if (minLat < MinLatitude || minLat > MaxLatitude ||
                    maxLat < MinLatitude || maxLat > MaxLatitude)
                {
                    throw new InvalidDataException(
                        $"RegionData file '{sourcePath}' {boxContext} latitude must be within [{MinLatitude}, {MaxLatitude}].");
                }

                if (minLng < MinLongitude || minLng > MaxLongitude ||
                    maxLng < MinLongitude || maxLng > MaxLongitude)
                {
                    throw new InvalidDataException(
                        $"RegionData file '{sourcePath}' {boxContext} longitude must be within [{MinLongitude}, {MaxLongitude}].");
                }

                if (minLat >= maxLat || minLng >= maxLng)
                {
                    throw new InvalidDataException(
                        $"RegionData file '{sourcePath}' {boxContext} requires minLat < maxLat and minLng < maxLng.");
                }

                var area = (maxLat - minLat) * (maxLng - minLng);
                var parsedBox = new RegionBox(minLat, minLng, maxLat, maxLng, area);
                boxes.Add(parsedBox);
                totalArea += area;
            }

            if (!string.Equals(regionId, UnknownRegionId, StringComparison.Ordinal))
            {
                EmitOverlapWarnings(sourcePath, regionId, boxes, warningSink);
            }

            regionsById[regionId] = new RegionDefinition(regionId, boxes.ToArray(), totalArea);
        }

        ValidateUnknownRegion(sourcePath, regionsById);
        var sortedByArea = regionsById.Values
            .OrderBy(static region => region.TotalArea)
            .ThenBy(static region => region.RegionId, StringComparer.Ordinal)
            .ToArray();
        return new RegionCatalog(regionsById, sortedByArea);
    }

    private static void EmitOverlapWarnings(
        string sourcePath,
        string regionId,
        IReadOnlyList<RegionBox> boxes,
        Action<string> warningSink)
    {
        for (var i = 0; i < boxes.Count; i++)
        {
            for (var j = i + 1; j < boxes.Count; j++)
            {
                if (!BoxesOverlap(boxes[i], boxes[j]))
                {
                    continue;
                }

                warningSink?.Invoke(
                    $"RegionData warning: '{sourcePath}' region '{regionId}' has overlapping boxes ({i}, {j}).");
            }
        }
    }

    private static bool BoxesOverlap(RegionBox a, RegionBox b)
    {
        return a.MinLat < b.MaxLat &&
               a.MaxLat > b.MinLat &&
               a.MinLng < b.MaxLng &&
               a.MaxLng > b.MinLng;
    }

    private static void ValidateUnknownRegion(
        string sourcePath,
        IReadOnlyDictionary<string, RegionDefinition> regionsById)
    {
        if (!regionsById.TryGetValue(UnknownRegionId, out var unknownRegion))
        {
            throw new InvalidDataException(
                $"RegionData file '{sourcePath}' must define region '{UnknownRegionId}'.");
        }

        if (unknownRegion.Boxes.Count != 1)
        {
            throw new InvalidDataException(
                $"RegionData file '{sourcePath}' region '{UnknownRegionId}' must contain exactly one box.");
        }

        var unknownBox = unknownRegion.Boxes[0];
        if (!AreAlmostEqual(unknownBox.MinLat, MinLatitude) ||
            !AreAlmostEqual(unknownBox.MinLng, MinLongitude) ||
            !AreAlmostEqual(unknownBox.MaxLat, MaxLatitude) ||
            !AreAlmostEqual(unknownBox.MaxLng, MaxLongitude))
        {
            throw new InvalidDataException(
                $"RegionData file '{sourcePath}' region '{UnknownRegionId}' must be [{MinLatitude}, {MinLongitude}, {MaxLatitude}, {MaxLongitude}].");
        }
    }

    private static bool AreAlmostEqual(double left, double right)
    {
        return Math.Abs(left - right) <= RegionCoordinateTolerance;
    }

    private static double ReadCoordinate(YamlNode node, string sourcePath, string context)
    {
        if (node is not YamlScalarNode scalarNode ||
            string.IsNullOrWhiteSpace(scalarNode.Value))
        {
            throw new InvalidDataException(
                $"RegionData file '{sourcePath}' {context} must be a numeric scalar.");
        }

        if (!double.TryParse(
                scalarNode.Value,
                NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            throw new InvalidDataException(
                $"RegionData file '{sourcePath}' {context} must be a numeric scalar.");
        }

        return parsed;
    }

    private static string ReadRequiredScalar(YamlNode node, string sourcePath, string context)
    {
        if (node is not YamlScalarNode scalarNode || string.IsNullOrWhiteSpace(scalarNode.Value))
        {
            throw new InvalidDataException($"RegionData file '{sourcePath}' {context} must be a non-empty scalar.");
        }

        return scalarNode.Value.Trim();
    }

    private static bool TryGetMappingChildValue(YamlMappingNode map, string key, out YamlNode value)
    {
        foreach (var pair in map.Children)
        {
            if (pair.Key is not YamlScalarNode scalarKey ||
                string.IsNullOrWhiteSpace(scalarKey.Value))
            {
                continue;
            }

            if (!string.Equals(scalarKey.Value, key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            value = pair.Value;
            return true;
        }

        value = null!;
        return false;
    }

    private sealed class RegionCatalog
    {
        internal RegionCatalog(
            IReadOnlyDictionary<string, RegionDefinition> byId,
            IReadOnlyList<RegionDefinition> byTotalAreaAscending)
        {
            ById = byId;
            ByTotalAreaAscending = byTotalAreaAscending;
        }

        internal IReadOnlyDictionary<string, RegionDefinition> ById { get; }

        internal IReadOnlyList<RegionDefinition> ByTotalAreaAscending { get; }
    }

    private sealed class RegionDefinition
    {
        internal static readonly RegionDefinition Empty = new(string.Empty, Array.Empty<RegionBox>(), 0.0);

        internal RegionDefinition(string regionId, IReadOnlyList<RegionBox> boxes, double totalArea)
        {
            RegionId = regionId;
            Boxes = boxes;
            TotalArea = totalArea;
        }

        internal string RegionId { get; }

        internal IReadOnlyList<RegionBox> Boxes { get; }

        internal double TotalArea { get; }
    }

    private readonly struct RegionBox
    {
        internal RegionBox(double minLat, double minLng, double maxLat, double maxLng, double area)
        {
            MinLat = minLat;
            MinLng = minLng;
            MaxLat = maxLat;
            MaxLng = maxLng;
            Area = area;
        }

        internal double MinLat { get; }

        internal double MinLng { get; }

        internal double MaxLat { get; }

        internal double MaxLng { get; }

        internal double Area { get; }
    }
}
