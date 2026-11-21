using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using APIShared.AssemblyMetadata;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Core.Tests;

// These fixtures write real, small managed PEs. They exercise the production CLI
// through Cecil without loading the inspected assembly or resolving its dependencies.
[TestClass]
public sealed class AssemblyMetadataTests
{
    [TestMethod]
    public void ExactSelectionExportsFieldsOverloadsBodylessMethodsAndRealIlWithCatchMetadata()
    {
        using var fixture = new MetadataFixture();
        using var report = fixture.Export();
        JsonElement root = report.RootElement;
        Assert.AreEqual(fixture.Hash, root.GetProperty("inputSha256").GetString());
        Assert.AreEqual(new FileInfo(fixture.Input).Length, root.GetProperty("inputBytes").GetInt64());
        StringAssert.Contains(root.GetProperty("assemblyName").GetString(), "MetadataFixture");
        StringAssert.Contains(root.GetProperty("moduleName").GetString(), "MetadataFixture");
        Assert.IsTrue(Guid.TryParse(root.GetProperty("moduleMvid").GetString(), out Guid mvid) && mvid != Guid.Empty);
        Assert.IsTrue(root.GetProperty("census").GetProperty("types").GetInt32() > 1,
            "The census describes the input, while the report body is selected explicitly.");
        JsonElement[] types = root.GetProperty("types").EnumerateArray().ToArray();
        Assert.AreEqual(1, types.Length);
        JsonElement type = types[0];
        Assert.AreEqual("Fixture.Selected", type.GetProperty("fullName").GetString());
        Assert.AreEqual("System.Object", type.GetProperty("baseType").GetString());
        CollectionAssert.AreEqual(new[] { "Fixture.IMarker" },
            type.GetProperty("interfaces").EnumerateArray().Select(value => value.GetString()).ToArray());
        Assert.IsTrue(type.TryGetProperty("attributes", out _));
        JsonElement[] fields = type.GetProperty("fields").EnumerateArray().ToArray();
        CollectionAssert.AreEquivalent(new[] { "Counter", "Label" }, fields.Select(field => field.GetProperty("name").GetString()).ToArray());
        StringAssert.Contains(fields.Single(field => field.GetProperty("name").GetString() == "Counter").GetProperty("signature").GetString(), "System.Int32");
        Assert.IsTrue(fields.All(field => field.TryGetProperty("attributes", out _)));

        JsonElement[] methods = type.GetProperty("methods").EnumerateArray().ToArray();
        Assert.AreEqual(4, methods.Length);
        JsonElement flow = methods.Single(method => method.GetProperty("name").GetString() == "Flow" &&
            method.GetProperty("signature").GetString().Contains("System.Int32"));
        Assert.AreEqual(0x06000000u, ReadToken(flow.GetProperty("metadataToken")) & 0xff000000u,
            "The reported caller token must identify a real MethodDef, without fixing its row number.");
        Assert.IsTrue(flow.GetProperty("hasBody").GetBoolean());
        Assert.IsTrue(flow.TryGetProperty("attributes", out _));
        Assert.IsTrue(flow.TryGetProperty("implementationAttributes", out _));
        JsonElement body = flow.GetProperty("body");
        Assert.IsTrue(body.GetProperty("codeSize").GetInt32() > 1);
        Assert.IsTrue(body.GetProperty("maxStack").GetInt32() >= 1);
        Assert.IsTrue(body.GetProperty("initLocals").GetBoolean());
        CollectionAssert.AreEqual(new[] { "System.Int32" }, body.GetProperty("variables").EnumerateArray().Select(value => value.GetString()).ToArray());
        JsonElement[] instructions = body.GetProperty("instructions").EnumerateArray().ToArray();
        Assert.AreEqual(0, instructions[0].GetProperty("offset").GetInt32());
        Assert.IsTrue(instructions.Any(instruction => instruction.GetProperty("opcode").GetString() == "call" &&
            instruction.GetProperty("operand").ToString().Contains("Fixture.Callee::Echo")));
        Assert.IsTrue(ReadToken(instructions.Single(instruction => instruction.GetProperty("opcode").GetString() == "call")
            .GetProperty("operand").GetProperty("metadataToken")) != 0);
        Assert.IsTrue(instructions.Any(instruction => instruction.GetProperty("opcode").GetString() == "ldsfld" &&
            instruction.GetProperty("operand").ToString().Contains("Fixture.Selected::Counter")));
        Assert.IsTrue(methods.Single(method => method.GetProperty("name").GetString() == "NoCall")
            .GetProperty("body").GetProperty("instructions").EnumerateArray().Any(instruction =>
                instruction.GetProperty("operand").ToString().Contains("Unavailable.Dependency::Ping")),
            "A missing dependency's symbolic member must be inspected without loading or resolving it.");
        JsonElement handler = body.GetProperty("exceptionHandlers").EnumerateArray().Single();
        Assert.AreEqual("Catch", handler.GetProperty("kind").GetString());
        Assert.AreEqual("System.Exception", handler.GetProperty("catchType").GetString());
        Assert.IsTrue(handler.GetProperty("tryStart").GetInt32() < handler.GetProperty("tryEnd").GetInt32());
        Assert.IsTrue(handler.GetProperty("handlerStart").GetInt32() < handler.GetProperty("handlerEnd").GetInt32());
        JsonElement bodyless = methods.Single(method => method.GetProperty("name").GetString() == "NoBody");
        Assert.IsFalse(bodyless.GetProperty("hasBody").GetBoolean());
        Assert.IsTrue(!bodyless.TryGetProperty("body", out var absentBody) || absentBody.ValueKind == JsonValueKind.Null);
    }

    [TestMethod]
    public void NestedSelectionIncludesOnlyDescendantsAndNameFilterKeepsBothOverloads()
    {
        using var fixture = new MetadataFixture();
        using (var exact = fixture.Export("--types", "Fixture.Selected;Fixture.Unrelated"))
            CollectionAssert.AreEquivalent(new[] { "Fixture.Selected", "Fixture.Unrelated" }, TypeNames(exact));
        using (var nested = fixture.Export("--include-nested"))
            CollectionAssert.AreEquivalent(new[] { "Fixture.Selected", "Fixture.Selected/Nested", "Fixture.Selected/Nested/Deeper" }, TypeNames(nested));
        using var filtered = fixture.Export("--methods", "Flow");
        JsonElement[] methods = filtered.RootElement.GetProperty("types")[0].GetProperty("methods").EnumerateArray().ToArray();
        Assert.AreEqual(2, methods.Length);
        Assert.IsTrue(methods.All(method => method.GetProperty("name").GetString() == "Flow"));
        Assert.AreEqual(2, methods.Select(method => method.GetProperty("signature").GetString()).Distinct().Count());
    }

    [TestMethod]
    public void CalledMemberFilterUsesActualIlRatherThanTheCallersName()
    {
        using var fixture = new MetadataFixture();
        using var report = fixture.Export("--called-member", "Fixture.Callee::Echo");
        JsonElement[] methods = report.RootElement.GetProperty("types")[0].GetProperty("methods").EnumerateArray().ToArray();
        Assert.AreEqual(1, methods.Length);
        Assert.AreEqual("Flow", methods[0].GetProperty("name").GetString());
        StringAssert.Contains(methods[0].GetProperty("signature").GetString(), "System.Int32");
    }

    [TestMethod]
    [DataRow("missing-required")]
    [DataRow("unknown-option")]
    [DataRow("duplicate-option")]
    [DataRow("duplicate-type")]
    [DataRow("duplicate-method")]
    [DataRow("exclusive-filters")]
    [DataRow("invalid-boolean")]
    [DataRow("malformed-hash")]
    [DataRow("missing-type")]
    [DataRow("missing-method")]
    [DataRow("missing-called-member")]
    [DataRow("body-upper-bound")]
    [DataRow("output-upper-bound")]
    public void InvalidArgumentsOrMissingSelectionCannotPublishOutput(string scenario)
    {
        using var fixture = new MetadataFixture();
        string output = fixture.NewOutput();
        var arguments = fixture.Arguments(output);
        switch (scenario)
        {
            case "missing-required": arguments.RemoveRange(0, 2); break;
            case "unknown-option": arguments.AddRange(new[] { "--unknown", "value" }); break;
            case "duplicate-option": arguments.AddRange(new[] { "--input", fixture.Input }); break;
            case "duplicate-type": Set(arguments, "--types", "Fixture.Selected;Fixture.Selected"); break;
            case "duplicate-method": arguments.AddRange(new[] { "--methods", "Flow;Flow" }); break;
            case "exclusive-filters": arguments.AddRange(new[] { "--methods", "Flow", "--called-member", "Fixture.Callee::Echo" }); break;
            case "invalid-boolean": arguments.AddRange(new[] { "--include-nested", "perhaps" }); break;
            case "malformed-hash": Set(arguments, "--sha256", "not-a-sha256"); break;
            case "missing-type": Set(arguments, "--types", "Fixture.Absent"); break;
            case "missing-method": arguments.AddRange(new[] { "--methods", "Missing" }); break;
            case "missing-called-member": arguments.AddRange(new[] { "--called-member", "Fixture.Callee::Missing" }); break;
            case "body-upper-bound": arguments.AddRange(new[] { "--max-body-bytes", "262145" }); break;
            case "output-upper-bound": arguments.AddRange(new[] { "--max-output-bytes", "8388609" }); break;
            default: Assert.Fail("Unknown test scenario."); break;
        }
        AssertRejected(arguments);
        Assert.IsFalse(File.Exists(output));
        Assert.AreEqual(fixture.Hash, MetadataFixture.HashFile(fixture.Input), "Inspection cannot change its input.");
    }

    [TestMethod]
    [DataRow("wrong-hash")]
    [DataRow("bad-pe")]
    [DataRow("input-size")]
    [DataRow("body-size")]
    [DataRow("output-size")]
    [DataRow("filter-scan")]
    public void ContentAndResourceBoundsRefuseBeforePublishingAFreshOutput(string scenario)
    {
        // Nonmatching bodies stay below declaration/per-body limits but together
        // exceed the scan budget before a final caller could produce a small report.
        using var fixture = new MetadataFixture(scenario == "filter-scan" ? 400 : 0);
        if (scenario == "bad-pe") File.WriteAllBytes(fixture.Input, new byte[] { 0x4d, 0x5a, 0, 1, 2, 3 });
        if (scenario == "input-size")
        {
            using var oversized = new FileStream(fixture.Input, FileMode.Open, FileAccess.Write, FileShare.None);
            oversized.SetLength(16L * 1024 * 1024 + 1);
        }
        string expectedInput = MetadataFixture.HashFile(fixture.Input);
        string output = fixture.NewOutput();
        var arguments = fixture.Arguments(output);
        Set(arguments, "--sha256", expectedInput);
        if (scenario == "wrong-hash") Set(arguments, "--sha256", new string(expectedInput[0] == '0' ? '1' : '0', 64));
        if (scenario == "body-size") arguments.AddRange(new[] { "--max-body-bytes", "1" });
        if (scenario == "output-size") arguments.AddRange(new[] { "--max-output-bytes", "1" });
        if (scenario == "filter-scan") arguments.AddRange(new[] { "--called-member", "Fixture.Callee::Echo" });
        AssertRejected(arguments);
        Assert.IsFalse(File.Exists(output), "A rejected inspection cannot publish a partial final report.");
        Assert.AreEqual(expectedInput, MetadataFixture.HashFile(fixture.Input));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void OutputMustBeFreshAndCannotReplaceAnExistingFileIncludingItsInput(bool inputIsOutput)
    {
        using var fixture = new MetadataFixture();
        string output = inputIsOutput ? fixture.Input : fixture.NewOutput();
        if (!inputIsOutput) File.WriteAllText(output, "Existing output must survive unchanged.");
        string before = MetadataFixture.HashFile(output);
        AssertRejected(fixture.Arguments(output));
        Assert.AreEqual(before, MetadataFixture.HashFile(output));
        Assert.AreEqual(fixture.Hash, MetadataFixture.HashFile(fixture.Input));
    }

    private static string[] TypeNames(JsonDocument report) => report.RootElement.GetProperty("types")
        .EnumerateArray().Select(type => type.GetProperty("fullName").GetString()).ToArray();

    private static uint ReadToken(JsonElement token)
    {
        Assert.IsTrue(uint.TryParse(token.GetString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value));
        return value;
    }

    private static void Set(List<string> arguments, string option, string value) => arguments[arguments.IndexOf(option) + 1] = value;

    private static void AssertRejected(List<string> arguments)
    {
        bool rejected = false;
        try { MetadataCommand.Run(arguments.ToArray()); }
        catch (Exception) { rejected = true; }
        Assert.IsTrue(rejected, "Invalid or bounded inspection must fail rather than report success.");
    }

    private sealed class MetadataFixture : IDisposable
    {
        private readonly string directory = Directory.CreateTempSubdirectory("APIShared-AssemblyMetadata-").FullName;
        private int outputSequence;
        internal string Input { get; }
        internal string Hash { get; }

        internal MetadataFixture(int filterCandidates = 0)
        {
            Input = Path.Combine(directory, "fixture.dll");
            try
            {
                using var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("MetadataFixture", new Version(1, 2, 3, 4)), "MetadataFixture.dll", ModuleKind.Dll);
                ModuleDefinition module = assembly.MainModule;
                var marker = new TypeDefinition("Fixture", "IMarker", TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
                module.Types.Add(marker);
                var selected = new TypeDefinition("Fixture", "Selected", TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Abstract, module.TypeSystem.Object);
                module.Types.Add(selected);
                selected.Interfaces.Add(new InterfaceImplementation(marker));
                var counter = new FieldDefinition("Counter", FieldAttributes.Public | FieldAttributes.Static, module.TypeSystem.Int32);
                selected.Fields.Add(counter);
                selected.Fields.Add(new FieldDefinition("Label", FieldAttributes.Private, module.TypeSystem.String));
                for (int index = 0; index < filterCandidates; index++)
                {
                    var nonmatching = new MethodDefinition("Candidate" + index, MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
                    for (int instruction = 0; instruction < 700; instruction++)
                        nonmatching.Body.Instructions.Add(Instruction.Create(OpCodes.Nop));
                    nonmatching.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                    selected.Methods.Add(nonmatching);
                }
                var nested = new TypeDefinition("", "Nested", TypeAttributes.NestedPublic | TypeAttributes.Class, module.TypeSystem.Object);
                selected.NestedTypes.Add(nested);
                nested.NestedTypes.Add(new TypeDefinition("", "Deeper", TypeAttributes.NestedPublic | TypeAttributes.Class, module.TypeSystem.Object));
                module.Types.Add(new TypeDefinition("Fixture", "Unrelated", TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object));
                var calleeType = new TypeDefinition("Fixture", "Callee", TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object);
                module.Types.Add(calleeType);
                var echo = new MethodDefinition("Echo", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32);
                echo.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, module.TypeSystem.Int32));
                echo.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                echo.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                calleeType.Methods.Add(echo);
                var flow = new MethodDefinition("Flow", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32);
                flow.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, module.TypeSystem.Int32));
                flow.Body.InitLocals = true;
                flow.Body.MaxStackSize = 2;
                flow.Body.Variables.Add(new VariableDefinition(module.TypeSystem.Int32));
                var start = Instruction.Create(OpCodes.Ldsfld, counter);
                var handler = Instruction.Create(OpCodes.Pop);
                var finish = Instruction.Create(OpCodes.Ldloc_0);
                foreach (var instruction in new[] { start, Instruction.Create(OpCodes.Pop), Instruction.Create(OpCodes.Ldarg_0),
                    Instruction.Create(OpCodes.Call, echo), Instruction.Create(OpCodes.Stloc_0), Instruction.Create(OpCodes.Leave_S, finish),
                    handler, Instruction.Create(OpCodes.Ldc_I4_M1), Instruction.Create(OpCodes.Stloc_0), Instruction.Create(OpCodes.Leave_S, finish),
                    finish, Instruction.Create(OpCodes.Ret) }) flow.Body.Instructions.Add(instruction);
                flow.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
                {
                    TryStart = start, TryEnd = handler, HandlerStart = handler, HandlerEnd = finish,
                    CatchType = new TypeReference("System", "Exception", module, module.TypeSystem.CoreLibrary)
                });
                selected.Methods.Add(flow);
                var overload = new MethodDefinition("Flow", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.String);
                overload.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, module.TypeSystem.String));
                overload.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                overload.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                selected.Methods.Add(overload);
                var noCall = new MethodDefinition("NoCall", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
                var missingAssembly = new AssemblyNameReference("Never.Resolve.This.Fixture.Dependency", new Version(9, 9, 9, 9));
                module.AssemblyReferences.Add(missingAssembly);
                var missingType = new TypeReference("Unavailable", "Dependency", module, missingAssembly);
                noCall.Body.Instructions.Add(Instruction.Create(OpCodes.Call, new MethodReference("Ping", module.TypeSystem.Void, missingType)));
                noCall.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                selected.Methods.Add(noCall);
                selected.Methods.Add(new MethodDefinition("NoBody", MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual, module.TypeSystem.Void));
                assembly.Write(Input);
                Hash = HashFile(Input);
            }
            catch { Dispose(); throw; }
        }

        internal string NewOutput() => Path.Combine(directory, "report-" + ++outputSequence + ".json");

        internal List<string> Arguments(string output) => new()
        {
            "--input", Input, "--sha256", Hash, "--output", output, "--types", "Fixture.Selected"
        };

        internal JsonDocument Export(params string[] options)
        {
            string output = NewOutput();
            var arguments = Arguments(output);
            for (int index = 0; index < options.Length; index++)
            {
                if (options[index] == "--include-nested") { arguments.Add(options[index]); continue; }
                string option = options[index], value = options[++index];
                if (arguments.Contains(option)) Set(arguments, option, value);
                else arguments.AddRange(new[] { option, value });
            }
            Assert.AreEqual(0, MetadataCommand.Run(arguments.ToArray()));
            return JsonDocument.Parse(File.ReadAllBytes(output));
        }

        internal static string HashFile(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        // The only recursive deletion is this uniquely created, fixture-owned temp tree.
        public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
