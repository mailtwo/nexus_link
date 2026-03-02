using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Uplink2.Runtime;
using Xunit;

#nullable enable

namespace Uplink2.Tests;

/// <summary>Unit tests for RegionData loading, preprocessing, and one-time cache behavior.</summary>
[Trait("Speed", "medium")]
public sealed class RegionDataTest
{
    [Fact]
    public void EnsureRegionDataLoaded_ComputesAreaAndContainingOrder()
    {
        using var scope = TempDirScope.Create();
        var yamlPath = scope.WriteFile(
            "regions.yaml",
            """
            regions:
              Small:
                boxes:
                  - [0.0, 0.0, 1.0, 1.0]
              Big:
                boxes:
                  - [0.0, 0.0, 2.0, 2.0]
              Unknown:
                boxes:
                  - [-90.0, -180.0, 90.0, 180.0]
            """);

        var world = CreateUninitializedWorldRuntime(yamlPath);
        InvokeEnsureRegionDataLoaded(world);

        Assert.True(InvokeTryGetRegion(world, "Small", out var smallRegion));
        Assert.True(InvokeTryGetRegion(world, "Big", out var bigRegion));
        Assert.NotNull(smallRegion);
        Assert.NotNull(bigRegion);

        Assert.Equal(1.0, ReadDoubleProperty(smallRegion!, "TotalArea"), 9);
        Assert.Equal(4.0, ReadDoubleProperty(bigRegion!, "TotalArea"), 9);

        var containing = InvokeGetContainingRegions(world, 0.5, 0.5);
        var containingIds = containing.Select(region => ReadStringProperty(region, "RegionId")).ToArray();
        Assert.Equal(new[] { "Small", "Big", "Unknown" }, containingIds);
    }

    [Fact]
    public void LoadRegionCatalog_OverlappingBoxes_EmitsWarning()
    {
        using var scope = TempDirScope.Create();
        var yamlPath = scope.WriteFile(
            "regions.yaml",
            """
            regions:
              Alpha:
                boxes:
                  - [0.0, 0.0, 2.0, 2.0]
                  - [1.0, 1.0, 3.0, 3.0]
              Unknown:
                boxes:
                  - [-90.0, -180.0, 90.0, 180.0]
            """);

        var warnings = new List<string>();
        var catalog = InvokeLoadRegionCatalog(yamlPath, warnings.Add);
        Assert.NotNull(catalog);
        Assert.Contains(
            warnings,
            static warning => warning.Contains("overlapping boxes", StringComparison.Ordinal));
    }

    [Fact]
    public void EnsureRegionDataLoaded_Throws_WhenUnknownRegionIsMissing()
    {
        using var scope = TempDirScope.Create();
        var yamlPath = scope.WriteFile(
            "regions.yaml",
            """
            regions:
              Asia:
                boxes:
                  - [5.0, 26.0, 55.0, 180.0]
            """);

        var world = CreateUninitializedWorldRuntime(yamlPath);
        var ex = Assert.Throws<TargetInvocationException>(() => InvokeEnsureRegionDataLoaded(world));
        Assert.IsType<InvalidDataException>(ex.InnerException);
        Assert.Contains("Unknown", ex.InnerException!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureRegionDataLoaded_Throws_WhenUnknownRegionBoundsAreInvalid()
    {
        using var scope = TempDirScope.Create();
        var yamlPath = scope.WriteFile(
            "regions.yaml",
            """
            regions:
              Unknown:
                boxes:
                  - [-90.0, -179.0, 90.0, 180.0]
            """);

        var world = CreateUninitializedWorldRuntime(yamlPath);
        var ex = Assert.Throws<TargetInvocationException>(() => InvokeEnsureRegionDataLoaded(world));
        Assert.IsType<InvalidDataException>(ex.InnerException);
        Assert.Contains("Unknown", ex.InnerException!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureRegionDataLoaded_Throws_WhenBoxArityIsInvalid()
    {
        using var scope = TempDirScope.Create();
        var yamlPath = scope.WriteFile(
            "regions.yaml",
            """
            regions:
              Asia:
                boxes:
                  - [5.0, 26.0, 55.0]
              Unknown:
                boxes:
                  - [-90.0, -180.0, 90.0, 180.0]
            """);

        var world = CreateUninitializedWorldRuntime(yamlPath);
        var ex = Assert.Throws<TargetInvocationException>(() => InvokeEnsureRegionDataLoaded(world));
        Assert.IsType<InvalidDataException>(ex.InnerException);
        Assert.Contains("exactly 4 values", ex.InnerException!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureRegionDataLoaded_Throws_WhenBoxCoordinateIsOutOfRange()
    {
        using var scope = TempDirScope.Create();
        var yamlPath = scope.WriteFile(
            "regions.yaml",
            """
            regions:
              Asia:
                boxes:
                  - [95.0, 26.0, 96.0, 40.0]
              Unknown:
                boxes:
                  - [-90.0, -180.0, 90.0, 180.0]
            """);

        var world = CreateUninitializedWorldRuntime(yamlPath);
        var ex = Assert.Throws<TargetInvocationException>(() => InvokeEnsureRegionDataLoaded(world));
        Assert.IsType<InvalidDataException>(ex.InnerException);
        Assert.Contains("latitude", ex.InnerException!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureRegionDataLoaded_Throws_WhenBoxMinIsNotLessThanMax()
    {
        using var scope = TempDirScope.Create();
        var yamlPath = scope.WriteFile(
            "regions.yaml",
            """
            regions:
              Asia:
                boxes:
                  - [10.0, 26.0, 10.0, 40.0]
              Unknown:
                boxes:
                  - [-90.0, -180.0, 90.0, 180.0]
            """);

        var world = CreateUninitializedWorldRuntime(yamlPath);
        var ex = Assert.Throws<TargetInvocationException>(() => InvokeEnsureRegionDataLoaded(world));
        Assert.IsType<InvalidDataException>(ex.InnerException);
        Assert.Contains("minLat < maxLat", ex.InnerException!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureRegionDataLoaded_LoadsOnlyOnce_EvenWhenSourceChanges()
    {
        using var scope = TempDirScope.Create();
        var yamlPath = scope.WriteFile(
            "regions.yaml",
            """
            regions:
              Initial:
                boxes:
                  - [0.0, 0.0, 1.0, 1.0]
              Unknown:
                boxes:
                  - [-90.0, -180.0, 90.0, 180.0]
            """);

        var world = CreateUninitializedWorldRuntime(yamlPath);
        InvokeEnsureRegionDataLoaded(world);

        File.WriteAllText(
            yamlPath,
            """
            regions:
              Replaced:
                boxes:
                  - [0.0, 0.0, 1.0, 1.0]
              Unknown:
                boxes:
                  - [-90.0, -180.0, 90.0, 180.0]
            """,
            Encoding.UTF8);

        InvokeEnsureRegionDataLoaded(world);
        Assert.True(InvokeTryGetRegion(world, "Initial", out _));
        Assert.False(InvokeTryGetRegion(world, "Replaced", out _));
    }

    private static WorldRuntime CreateUninitializedWorldRuntime(string regionDataFile)
    {
        var world = (WorldRuntime)RuntimeHelpers.GetUninitializedObject(typeof(WorldRuntime));
        SetPrivateField(world, "_worldStateLock", new object());
        world.RegionDataFile = regionDataFile;
        return world;
    }

    private static void InvokeEnsureRegionDataLoaded(WorldRuntime world)
    {
        var method = typeof(WorldRuntime).GetMethod(
            "EnsureRegionDataLoaded",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(world, Array.Empty<object?>());
    }

    private static bool InvokeTryGetRegion(WorldRuntime world, string regionId, out object? region)
    {
        var method = typeof(WorldRuntime).GetMethod(
            "TryGetRegion",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        object?[] args = { regionId, null };
        var found = (bool)method!.Invoke(world, args)!;
        region = args[1];
        return found;
    }

    private static IReadOnlyList<object> InvokeGetContainingRegions(WorldRuntime world, double lat, double lng)
    {
        var method = typeof(WorldRuntime).GetMethod(
            "GetContainingRegions",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var raw = method!.Invoke(world, new object?[] { lat, lng }) as IEnumerable;
        Assert.NotNull(raw);
        var values = new List<object>();
        foreach (var value in raw!)
        {
            if (value is null)
            {
                continue;
            }

            values.Add(value);
        }

        return values;
    }

    private static object InvokeLoadRegionCatalog(string filePath, Action<string> warningSink)
    {
        var method = typeof(WorldRuntime).GetMethod(
            "LoadRegionCatalog",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method!.Invoke(null, new object?[] { filePath, warningSink })!;
    }

    private static string ReadStringProperty(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(property);
        return property!.GetValue(instance) as string ?? string.Empty;
    }

    private static double ReadDoubleProperty(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(property);
        Assert.IsType<double>(property!.GetValue(instance));
        return (double)property.GetValue(instance)!;
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    private sealed class TempDirScope : IDisposable
    {
        private TempDirScope(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TempDirScope Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "uplink2-region-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TempDirScope(path);
        }

        public string WriteFile(string fileName, string content)
        {
            var fullPath = System.IO.Path.Combine(Path, fileName);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content, Encoding.UTF8);
            return fullPath;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
