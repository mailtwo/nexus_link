using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using Uplink2.Blueprint;
using Uplink2.Runtime;
using Xunit;

#nullable enable

namespace Uplink2.Tests;

/// <summary>Unit tests for blueprint YAML parsing and blueprint-content loading contracts.</summary>
public sealed class BlueprintTest
{
    /// <summary>Ensures scenario-level scripts and event guardContent fields are parsed from YAML.</summary>
    [Fact]
    public void ReadFiles_ParsesScenarioScriptsAndGuardContent()
    {
        using var scope = TempDirScope.Create();
        var yamlPath = scope.WriteFile(
            "scenario_guard.yaml",
            """
            Scenario:
              s1:
                scripts:
                  g1: |
                    return evt.privilege == "execute"
                events:
                  e1:
                    conditionType: PrivilegeAcquire
                    conditionArgs:
                      privilege: execute
                    guardContent: id-g1
                    actions:
                      - actionType: Print
                        actionArgs:
                          text: ok
            """);

        var reader = new BlueprintYamlReader();
        var catalog = reader.ReadFiles(new[] { yamlPath });
        var scenario = catalog.Scenarios["s1"];
        var eventBlueprint = scenario.Events["e1"];

        Assert.True(scenario.Scripts.ContainsKey("g1"));
        Assert.Contains("evt.privilege", scenario.Scripts["g1"], StringComparison.Ordinal);
        Assert.Equal("id-g1", eventBlueprint.GuardContent);
    }

    /// <summary>Ensures required conditionArgs.privilege is enforced for privilegeAcquire handlers.</summary>
    [Fact]
    public void ReadFiles_PrivilegeAcquireMissingRequiredPrivilege_Fails()
    {
        using var scope = TempDirScope.Create();
        var yamlPath = scope.WriteFile(
            "missing_privilege.yaml",
            """
            Scenario:
              s1:
                events:
                  e1:
                    conditionType: PrivilegeAcquire
                    conditionArgs:
                      nodeId: n1
            """);

        var reader = new BlueprintYamlReader();
        var ex = Assert.Throws<InvalidDataException>(() => reader.ReadFiles(new[] { yamlPath }));
        Assert.Contains("conditionArgs.privilege is required.", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Ensures required conditionArgs.fileName is enforced for fileAcquire handlers.</summary>
    [Fact]
    public void ReadFiles_FileAcquireMissingRequiredFileName_Fails()
    {
        using var scope = TempDirScope.Create();
        var yamlPath = scope.WriteFile(
            "missing_file_name.yaml",
            """
            Scenario:
              s1:
                events:
                  e1:
                    conditionType: FileAcquire
                    conditionArgs:
                      nodeId: n1
            """);

        var reader = new BlueprintYamlReader();
        var ex = Assert.Throws<InvalidDataException>(() => reader.ReadFiles(new[] { yamlPath }));
        Assert.Contains("conditionArgs.fileName is required.", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Ensures unknown conditionArgs keys are warned and ignored instead of failing parse.</summary>
    [Fact]
    public void ReadFiles_UnknownConditionArgKey_WarnsAndIgnores()
    {
        using var scope = TempDirScope.Create();
        var yamlPath = scope.WriteFile(
            "unknown_condition_arg.yaml",
            """
            Scenario:
              s1:
                events:
                  e1:
                    conditionType: PrivilegeAcquire
                    conditionArgs:
                      privilege: execute
                      unknownKey: value
            """);

        var warnings = new List<string>();
        var reader = new BlueprintYamlReader(warnings.Add);
        var catalog = reader.ReadFiles(new[] { yamlPath });
        var eventBlueprint = catalog.Scenarios["s1"].Events["e1"];

        Assert.Contains(
            warnings,
            static warning => warning.Contains("unknown key 'unknownKey'", StringComparison.Ordinal));
        Assert.DoesNotContain("unknownKey", eventBlueprint.ConditionArgs.Keys);
        Assert.Equal("execute", eventBlueprint.ConditionArgs["privilege"]);
    }

    /// <summary>Ensures conditionArgs values only accept string/null and reject numeric inputs.</summary>
    [Fact]
    public void ReadFiles_ConditionArgTypeMismatch_Fails()
    {
        using var scope = TempDirScope.Create();
        var yamlPath = scope.WriteFile(
            "condition_arg_type_mismatch.yaml",
            """
            Scenario:
              s1:
                events:
                  e1:
                    conditionType: PrivilegeAcquire
                    conditionArgs:
                      privilege:
                        - execute
            """);

        var reader = new BlueprintYamlReader();
        var ex = Assert.Throws<InvalidDataException>(() => reader.ReadFiles(new[] { yamlPath }));
        Assert.Contains("conditionArgs.privilege must be a string or null.", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Ensures executable file kinds parse correctly inside diskOverlay entry metadata.</summary>
    [Fact]
    public void ReadFiles_ParsesExecutableFileKinds()
    {
        using var scope = TempDirScope.Create();
        var yamlPath = scope.WriteFile(
            "spec.yaml",
            """
            ServerSpec:
              spec_exec:
                diskOverlay:
                  overlayEntries:
                    /opt/bin/script_exec:
                      entryKind: File
                      fileKind: ExecutableScript
                      contentId: print "hello"
                    /opt/bin/hard_exec:
                      entryKind: File
                      fileKind: ExecutableHardcode
                      contentId: noop
            """);

        var reader = new BlueprintYamlReader();
        var catalog = reader.ReadFiles(new[] { yamlPath });
        var spec = catalog.ServerSpecs["spec_exec"];

        Assert.Equal(
            BlueprintFileKind.ExecutableScript,
            spec.DiskOverlay.OverlayEntries["/opt/bin/script_exec"].FileKind);
        Assert.Equal(
            BlueprintFileKind.ExecutableHardcode,
            spec.DiskOverlay.OverlayEntries["/opt/bin/hard_exec"].FileKind);
    }

    /// <summary>Ensures invalid fileKind values are accumulated into one read exception.</summary>
    [Fact]
    public void ReadFiles_InvalidFileKinds_AggregatesErrors()
    {
        using var scope = TempDirScope.Create();
        var yamlPath = scope.WriteFile(
            "invalid_filekind.yaml",
            """
            ServerSpec:
              spec_invalid:
                diskOverlay:
                  overlayEntries:
                    /bad/a:
                      entryKind: File
                      fileKind: NotAFileKind
                    /bad/b:
                      entryKind: File
                      fileKind: StillNotAFileKind
            """);

        var reader = new BlueprintYamlReader();
        var ex = Assert.Throws<InvalidDataException>(() => reader.ReadFiles(new[] { yamlPath }));

        Assert.Contains("Blueprint YAML read failed:", ex.Message, StringComparison.Ordinal);
        Assert.Contains("overlayEntries[/bad/a].fileKind has an unknown value.", ex.Message, StringComparison.Ordinal);
        Assert.Contains("overlayEntries[/bad/b].fileKind has an unknown value.", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Ensures ReadFiles loads and merges model sections across multiple YAML files.</summary>
    [Fact]
    public void ReadFiles_LoadsMultipleFiles()
    {
        using var scope = TempDirScope.Create();
        var aPath = scope.WriteFile(
            "a.yaml",
            """
            ServerSpec:
              spec_a: {}
            """);
        var bPath = scope.WriteFile(
            "b.yaml",
            """
            ServerSpec:
              spec_b: {}
            """);

        var reader = new BlueprintYamlReader();
        var catalog = reader.ReadFiles(new[] { bPath, aPath });

        Assert.Equal(2, catalog.ServerSpecs.Count);
        Assert.Contains("spec_a", catalog.ServerSpecs.Keys);
        Assert.Contains("spec_b", catalog.ServerSpecs.Keys);
    }

    /// <summary>Ensures duplicate keys across files are reported as read errors.</summary>
    [Fact]
    public void ReadFiles_DuplicateServerSpec_ReportsError()
    {
        using var scope = TempDirScope.Create();
        var aPath = scope.WriteFile(
            "a.yaml",
            """
            ServerSpec:
              spec_dup: {}
            """);
        var bPath = scope.WriteFile(
            "b.yaml",
            """
            ServerSpec:
              spec_dup: {}
            """);

        var reader = new BlueprintYamlReader();
        var ex = Assert.Throws<InvalidDataException>(() => reader.ReadFiles(new[] { aPath, bPath }));

        Assert.Contains("duplicate ServerSpec 'spec_dup'.", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Ensures multiple file-level failures are aggregated into one exception message.</summary>
    [Fact]
    public void ReadFiles_AggregatesMultiFileErrors()
    {
        using var scope = TempDirScope.Create();
        var invalidRootPath = scope.WriteFile(
            "invalid_root.yaml",
            """
            - item1
            - item2
            """);
        var missingPath = Path.Combine(scope.Path, "missing.yaml");

        var reader = new BlueprintYamlReader();
        var ex = Assert.Throws<InvalidDataException>(() => reader.ReadFiles(new[] { invalidRootPath, missingPath }));

        Assert.Contains("Blueprint YAML read failed:", ex.Message, StringComparison.Ordinal);
        Assert.Contains("file not found.", ex.Message, StringComparison.Ordinal);
        Assert.Contains("document root must be a mapping.", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Ensures ResolveBlueprintContent keeps text kinds on UTF-8 text read path and binary kinds on base64 path.</summary>
    [Fact]
    public void ResolveBlueprintContent_UsesExpectedLoadingContract()
    {
        var worldRuntimeType = typeof(WorldRuntime);
        var method = worldRuntimeType.GetMethod("ResolveBlueprintContent", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var calledMethods = ExtractCalledMethods((MethodInfo)method!);
        Assert.Contains(
            calledMethods,
            static called => called is MethodInfo info &&
                             info.DeclaringType == typeof(File) &&
                             info.Name == nameof(File.ReadAllText) &&
                             info.GetParameters().Length == 2);
        Assert.Contains(
            calledMethods,
            static called => called is MethodInfo info &&
                             info.DeclaringType == typeof(File) &&
                             info.Name == nameof(File.ReadAllBytes) &&
                             info.GetParameters().Length == 1);
        Assert.Contains(
            calledMethods,
            static called => called is MethodInfo info &&
                             info.DeclaringType == typeof(Convert) &&
                             info.Name == nameof(Convert.ToBase64String));

        var constants = ExtractInt32Constants((MethodInfo)method!);
        Assert.Contains((int)BlueprintFileKind.Text, constants);
        Assert.Contains((int)BlueprintFileKind.ExecutableScript, constants);
        Assert.Contains((int)BlueprintFileKind.ExecutableHardcode, constants);
    }

    private static IReadOnlyList<MethodBase> ExtractCalledMethods(MethodInfo method)
    {
        var ilBytes = method.GetMethodBody()?.GetILAsByteArray();
        if (ilBytes is null || ilBytes.Length == 0)
        {
            return Array.Empty<MethodBase>();
        }

        var methods = new List<MethodBase>();
        for (var index = 0; index <= ilBytes.Length - 5; index++)
        {
            if (ilBytes[index] != 0x28 && ilBytes[index] != 0x6F)
            {
                continue;
            }

            var token = BitConverter.ToInt32(ilBytes, index + 1);
            try
            {
                var resolved = method.Module.ResolveMethod(token);
                if (resolved is not null)
                {
                    methods.Add(resolved);
                }
            }
            catch (ArgumentException)
            {
            }
            catch (BadImageFormatException)
            {
            }
        }

        return methods;
    }

    private static IReadOnlyCollection<int> ExtractInt32Constants(MethodInfo method)
    {
        var ilBytes = method.GetMethodBody()?.GetILAsByteArray();
        if (ilBytes is null || ilBytes.Length == 0)
        {
            return Array.Empty<int>();
        }

        var oneByteOpCodes = BuildOpCodeMap(multiByte: false);
        var twoByteOpCodes = BuildOpCodeMap(multiByte: true);

        var values = new List<int>();
        var index = 0;
        while (index < ilBytes.Length)
        {
            var opCodeStart = index;
            var code = ilBytes[index++];
            OpCode opCode;
            if (code == 0xFE)
            {
                if (index >= ilBytes.Length)
                {
                    break;
                }

                opCode = twoByteOpCodes[ilBytes[index++]];
            }
            else
            {
                opCode = oneByteOpCodes[code];
            }

            var operandStart = index;
            var operandLength = GetOperandLength(opCode.OperandType, ilBytes, operandStart);

            switch (opCode.Value)
            {
                case short value when value == OpCodes.Ldc_I4_M1.Value:
                    values.Add(-1);
                    break;
                case short value when value == OpCodes.Ldc_I4_0.Value:
                    values.Add(0);
                    break;
                case short value when value == OpCodes.Ldc_I4_1.Value:
                    values.Add(1);
                    break;
                case short value when value == OpCodes.Ldc_I4_2.Value:
                    values.Add(2);
                    break;
                case short value when value == OpCodes.Ldc_I4_3.Value:
                    values.Add(3);
                    break;
                case short value when value == OpCodes.Ldc_I4_4.Value:
                    values.Add(4);
                    break;
                case short value when value == OpCodes.Ldc_I4_5.Value:
                    values.Add(5);
                    break;
                case short value when value == OpCodes.Ldc_I4_6.Value:
                    values.Add(6);
                    break;
                case short value when value == OpCodes.Ldc_I4_7.Value:
                    values.Add(7);
                    break;
                case short value when value == OpCodes.Ldc_I4_8.Value:
                    values.Add(8);
                    break;
                case short value when value == OpCodes.Ldc_I4_S.Value && operandLength == 1:
                    values.Add((sbyte)ilBytes[operandStart]);
                    break;
                case short value when value == OpCodes.Ldc_I4.Value && operandLength == 4:
                    values.Add(BitConverter.ToInt32(ilBytes, operandStart));
                    break;
            }

            index = operandStart + operandLength;
            if (index <= opCodeStart)
            {
                index = opCodeStart + 1;
            }
        }

        return values;
    }

    private static OpCode[] BuildOpCodeMap(bool multiByte)
    {
        var map = new OpCode[0x100];
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode opCode)
            {
                continue;
            }

            var value = unchecked((ushort)opCode.Value);
            var high = (byte)(value >> 8);
            var low = (byte)(value & 0xFF);
            if (!multiByte && high == 0x00)
            {
                map[low] = opCode;
            }
            else if (multiByte && high == 0xFE)
            {
                map[low] = opCode;
            }
        }

        return map;
    }

    private static int GetOperandLength(OperandType operandType, byte[] il, int operandStart)
    {
        return operandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget => 1,
            OperandType.ShortInlineI => 1,
            OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineI => 4,
            OperandType.InlineBrTarget => 4,
            OperandType.InlineField => 4,
            OperandType.InlineMethod => 4,
            OperandType.InlineSig => 4,
            OperandType.InlineString => 4,
            OperandType.InlineTok => 4,
            OperandType.InlineType => 4,
            OperandType.ShortInlineR => 4,
            OperandType.InlineR => 8,
            OperandType.InlineI8 => 8,
            OperandType.InlineSwitch => 4 + (BitConverter.ToInt32(il, operandStart) * 4),
            _ => 0,
        };
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
                "uplink2-blueprint-tests-" + Guid.NewGuid().ToString("N"));
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
