using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Mono.Cecil;
using Mono.Cecil.Cil;
using AssemblyDefinition = Mono.Cecil.AssemblyDefinition;
using ModuleDefinition = Mono.Cecil.ModuleDefinition;
using ParameterDefinition = Mono.Cecil.ParameterDefinition;
using TypeDefinition = Mono.Cecil.TypeDefinition;
using TypeReference = Mono.Cecil.TypeReference;

namespace APIShared.AssemblyMetadata;

// Reads metadata and complete selected IL only. No target assembly load, type/member
// resolution, symbol lookup, custom-attribute argument decoding or target execution.
internal sealed class MetadataInspector(InspectionOptions options)
{
    private const int TypeLimit = 50000, MethodLimit = 200000, FieldLimit = 200000;
    private const int SelectedTypeLimit = 256, SelectedMethodLimit = 512;
    private const int InstructionLimit = 65536, TotalInstructionLimit = 262144, TextLimit = 16384;
    private int selectedMethods, totalInstructions, scannedMethods, scannedInstructions, selectedFields, reportCharacters;
    private int declarationMethods, metadataCharacters;
    private long scannedBodyBytes, selectedBodyBytes;
    private PEReader? image;
    private MetadataReader? metadata;

    internal MetadataReport Read(byte[] input, string hash)
    {
        using var pe = new PEReader(new MemoryStream(input, writable: false));
        image = pe;
        metadata = Preflight(pe);
        using var stream = new MemoryStream(input, writable: false);
        using var resolver = new RejectingResolver();
        using var module = ModuleDefinition.ReadModule(stream, new ReaderParameters {
            ReadingMode = ReadingMode.Deferred, InMemory = true, ReadSymbols = false, AssemblyResolver = resolver });
        if (module.Assembly == null) throw new InvalidDataException("A managed assembly main module is required.");
        var all = Flatten(module.Types).ToArray();
        var requested = new HashSet<string>(options.Types, StringComparer.Ordinal);
        var found = new HashSet<string>(StringComparer.Ordinal);
        var selected = new List<TypeDefinition>();
        int methods = metadata.MethodDefinitions.Count, fields = metadata.FieldDefinitions.Count;
        int censusCharacters = 0;
        foreach (var type in all)
        {
            string name = Text(type.FullName);
            censusCharacters = checked(censusCharacters + name.Length);
            if (censusCharacters > 2 * 1024 * 1024) throw new InvalidDataException("Type-name census text exceeds its bound.");
            bool exact = requested.Contains(name);
            if (exact) found.Add(name);
            bool include = exact;
            if (!include && options.IncludeNested)
                for (var parent = type.DeclaringType; parent != null; parent = parent.DeclaringType)
                    if (requested.Contains(Text(parent.FullName))) { include = true; break; }
            if (include) selected.Add(type);
        }
        if (!found.SetEquals(requested)) throw new InvalidDataException("At least one exact requested type is missing.");
        if (selected.Count == 0 || selected.Count > SelectedTypeLimit || methods > MethodLimit || fields > FieldLimit)
            throw new InvalidDataException("Type selection or metadata census exceeds its bound.");
        var methodNames = new HashSet<string>(options.Methods, StringComparer.Ordinal);
        var foundMethods = new HashSet<string>(StringComparer.Ordinal);
        TypeReport[] reports = selected.Select(type => ReadType(type, methodNames, foundMethods)).ToArray();
        if (!foundMethods.SetEquals(methodNames) || options.CalledMember != null && selectedMethods == 0)
            throw new InvalidDataException("Requested method selection matched no complete selection.");
        return new(1, hash, input.Length, Text(module.Assembly.Name.FullName), Text(module.Name),
            module.Mvid.ToString("D"), new(all.Length, methods, fields), reports);
    }

    private TypeReport ReadType(TypeDefinition type, HashSet<string> methodNames, HashSet<string> foundMethods)
    {
        // Use raw table counts and names before Cecil materializes this type's
        // member collections. Unrelated member declarations are never requested.
        var declaration = metadata!.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(type.MetadataToken.ToInt32() & 0x00ffffff));
        declarationMethods = checked(declarationMethods + declaration.GetMethods().Count);
        selectedFields = checked(selectedFields + declaration.GetFields().Count);
        if (declarationMethods > 4096 || selectedFields > 8192)
            throw new InvalidDataException("Selected declaration census exceeds its bound.");
        foreach (var method in declaration.GetMethods()) MetadataText(metadata.GetString(metadata.GetMethodDefinition(method).Name));
        foreach (var field in declaration.GetFields()) MetadataText(metadata.GetString(metadata.GetFieldDefinition(field).Name));
        var methods = new List<MethodReport>();
        foreach (var method in type.Methods)
        {
            string name = Text(method.Name);
            if (methodNames.Count != 0 && !methodNames.Contains(name)) continue;
            BodyReport? body = null;
            // A called-member filter scans only the explicitly selected type family.
            // Inspecting a body for selection must obey the same bounds as reporting it.
            if (options.CalledMember != null)
            {
                if (!method.HasBody) continue;
                ChargeBody(method, forFilter: true);
                CheckBody(method.Body);
                scannedInstructions = checked(scannedInstructions + method.Body.Instructions.Count);
                if (scannedInstructions > TotalInstructionLimit) throw new InvalidDataException("Called-member scan instruction census exceeds its bound.");
                if (!method.Body.Instructions.Any(instruction => (instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj) &&
                    instruction.Operand is MethodReference member &&
                    StringComparer.Ordinal.Equals(Text(member.DeclaringType.FullName) + "::" + Text(member.Name), options.CalledMember))) continue;
            }
            if (++selectedMethods > SelectedMethodLimit) throw new InvalidDataException("Too many selected methods.");
            if (method.HasBody)
            {
                if (options.CalledMember == null) ChargeBody(method, forFilter: false);
                body = ReadBody(method.Body);
            }
            if (methodNames.Count != 0) foundMethods.Add(name);
            methods.Add(new(ReportText(name), ReportText(method.FullName), Token(method.MetadataToken), method.Attributes.ToString(), method.ImplAttributes.ToString(), method.HasBody, body));
        }
        if (type.Interfaces.Count > 256) throw new InvalidDataException("Selected interface census exceeds its bound.");
        // These getters read ClassLayout/FieldLayout metadata only. They neither
        // resolve field types nor calculate CLR/native layout. Absence stays null;
        // a declared zero packing, size or field offset must remain zero.
        bool hasLayout = type.HasLayoutInfo;
        string layoutKind = type.IsExplicitLayout ? "Explicit" : type.IsSequentialLayout ? "Sequential" :
            type.IsAutoLayout ? "Auto" : throw new InvalidDataException("Unrecognized type layout flags.");
        return new(ReportText(type.FullName), type.Attributes.ToString(), type.BaseType == null ? null : ReportText(type.BaseType.FullName),
            type.Interfaces.Select(item => ReportText(item.InterfaceType.FullName)).ToArray(),
            type.Fields.Select(field => new FieldReport(ReportText(field.Name), ReportText(field.FullName), field.Attributes.ToString(),
                field.HasLayoutInfo ? field.Offset : null)).ToArray(), methods.ToArray(), layoutKind,
            hasLayout ? type.PackingSize : null, hasLayout ? type.ClassSize : null);
    }

    private void CheckBody(Mono.Cecil.Cil.MethodBody body)
    {
        if (body.CodeSize < 0 || body.CodeSize > options.MaxBodyBytes || body.Instructions.Count > InstructionLimit ||
            body.Variables.Count > 4096 || body.ExceptionHandlers.Count > 4096)
            throw new InvalidDataException("A selected or filter-scanned method body exceeds its bound.");
    }

    // Inspect the PE header/IL byte span before Cecil expands any instructions.
    // Counting each scanned MethodDef separately prevents shared RVAs from evading
    // cumulative budgets. Matched filter bodies are not charged a second time.
    private void ChargeBody(Mono.Cecil.MethodDefinition method, bool forFilter)
    {
        if (forFilter && ++scannedMethods > 4096) throw new InvalidDataException("Called-member scan method census exceeds its bound.");
        var definition = metadata!.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(method.MetadataToken.ToInt32() & 0x00ffffff));
        if (definition.RelativeVirtualAddress == 0) throw new InvalidDataException("An IL method body has no PE address.");
        int bytes = image!.GetMethodBody(definition.RelativeVirtualAddress).GetILReader().Length;
        if (bytes > options.MaxBodyBytes) throw new InvalidDataException("An inspected PE method body exceeds its byte bound.");
        if (forFilter) scannedBodyBytes += bytes;
        else selectedBodyBytes += bytes;
        if (scannedBodyBytes > 1024 * 1024 || selectedBodyBytes > 1024 * 1024)
            throw new InvalidDataException("Inspected method bodies exceed the cumulative byte bound.");
    }

    private BodyReport ReadBody(Mono.Cecil.Cil.MethodBody body)
    {
        CheckBody(body);
        totalInstructions = checked(totalInstructions + body.Instructions.Count);
        if (totalInstructions > TotalInstructionLimit) throw new InvalidDataException("Selected instruction census exceeds its bound.");
        return new(body.CodeSize, body.MaxStackSize, body.InitLocals,
            body.Variables.Select(variable => ReportText(variable.VariableType.FullName)).ToArray(),
            body.Instructions.Select(instruction => new InstructionReport(instruction.Offset, instruction.OpCode.Name, Operand(instruction.Operand))).ToArray(),
            body.ExceptionHandlers.Select(handler => new ExceptionReport(handler.HandlerType.ToString(),
                RequiredOffset(handler.TryStart), handler.TryEnd?.Offset, RequiredOffset(handler.HandlerStart), handler.HandlerEnd?.Offset,
                handler.FilterStart?.Offset, handler.CatchType == null ? null : ReportText(handler.CatchType.FullName))).ToArray());
    }

    private static int RequiredOffset(Instruction? instruction) => instruction?.Offset ?? throw new InvalidDataException("An exception boundary is missing.");
    private OperandReport? Operand(object? operand) => operand switch {
        null => null,
        Instruction target => new("branch", Target: target.Offset),
        Instruction[] targets => new("switch", Targets: targets.Select(target => target.Offset).ToArray()),
        MethodReference method => new("method", ReportText(method.FullName), MetadataToken: Token(method.MetadataToken)),
        FieldReference field => new("field", ReportText(field.FullName), MetadataToken: Token(field.MetadataToken)),
        TypeReference type => new("type", ReportText(type.FullName), MetadataToken: Token(type.MetadataToken)),
        CallSite signature => new("signature", ReportText(signature.FullName)),
        VariableDefinition variable => new("variable", variable.Index.ToString(CultureInfo.InvariantCulture)),
        ParameterDefinition parameter => new("parameter", parameter.Index.ToString(CultureInfo.InvariantCulture)),
        string literal => new("string", ReportText(literal)),
        float number => new("float32", number.ToString("R", CultureInfo.InvariantCulture)),
        double number => new("float64", number.ToString("R", CultureInfo.InvariantCulture)),
        sbyte or byte or short or ushort or int or uint or long or ulong => new("integer", Convert.ToString(operand, CultureInfo.InvariantCulture)),
        _ => throw new InvalidDataException("Unsupported IL operand shape.")
    };

    private static string Text(string value) => value.Length <= TextLimit ? value : throw new InvalidDataException("A metadata text value exceeds its bound.");
    private static string Token(Mono.Cecil.MetadataToken token) => token.ToInt32().ToString("X8", CultureInfo.InvariantCulture);

    private string ReportText(string value)
    {
        Text(value);
        reportCharacters = checked(reportCharacters + value.Length);
        if (reportCharacters > options.MaxOutputBytes) throw new InvalidDataException("Selected metadata text exceeds the output budget.");
        return value;
    }

    private void MetadataText(string value)
    {
        Text(value);
        metadataCharacters = checked(metadataCharacters + value.Length);
        if (metadataCharacters > 2 * 1024 * 1024) throw new InvalidDataException("Selected declaration-name text exceeds its bound.");
    }

    private static IEnumerable<TypeDefinition> Flatten(IEnumerable<TypeDefinition> roots)
    {
        var stack = new Stack<TypeDefinition>(roots.Reverse());
        int count = 0;
        while (stack.Count != 0)
        {
            if (++count > TypeLimit) throw new InvalidDataException("Type census exceeds its bound.");
            var type = stack.Pop();
            yield return type;
            for (int i = type.NestedTypes.Count - 1; i >= 0; i--) stack.Push(type.NestedTypes[i]);
        }
    }

    private static MetadataReader Preflight(PEReader pe)
    {
        if (!pe.HasMetadata) throw new BadImageFormatException("Input is not a managed PE image.");
        var metadata = pe.GetMetadataReader();
        if (!metadata.IsAssembly || metadata.TypeDefinitions.Count > TypeLimit || metadata.MethodDefinitions.Count > MethodLimit ||
            metadata.FieldDefinitions.Count > FieldLimit ||
            metadata.GetTableRowCount(TableIndex.ClassLayout) > metadata.TypeDefinitions.Count ||
            metadata.GetTableRowCount(TableIndex.FieldLayout) > metadata.FieldDefinitions.Count ||
            metadata.AssemblyFiles.Any(file => metadata.GetAssemblyFile(file).ContainsMetadata))
            throw new InvalidDataException("A bounded single-module managed assembly is required.");
        ValidateLayoutValues(pe, metadata);
        foreach (var handle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(handle);
            Text(metadata.GetString(type.Name)); Text(metadata.GetString(type.Namespace));
            int depth = 0;
            for (var parent = type.GetDeclaringType(); !parent.IsNil; parent = metadata.GetTypeDefinition(parent).GetDeclaringType())
                if (++depth > 32) throw new InvalidDataException("Nested type depth exceeds its bound.");
        }
        return metadata;
    }

    // Cecil exposes these unsigned metadata columns through signed getters. Refuse
    // an unrepresentable present value before a getter could turn it into -1/null.
    // Table rows are already bounded by the corresponding definition census.
    private static void ValidateLayoutValues(PEReader pe, MetadataReader metadata)
    {
        var reader = pe.GetMetadata().GetReader();
        int count = metadata.GetTableRowCount(TableIndex.ClassLayout);
        int start = metadata.GetTableMetadataOffset(TableIndex.ClassLayout);
        int stride = metadata.GetTableRowSize(TableIndex.ClassLayout);
        for (int row = 0; row < count; row++)
        {
            reader.Offset = checked(start + row * stride);
            if (reader.ReadUInt16() > short.MaxValue || reader.ReadUInt32() > int.MaxValue)
                throw new InvalidDataException("Declared class layout exceeds Cecil's signed representation.");
        }
        count = metadata.GetTableRowCount(TableIndex.FieldLayout);
        start = metadata.GetTableMetadataOffset(TableIndex.FieldLayout);
        stride = metadata.GetTableRowSize(TableIndex.FieldLayout);
        for (int row = 0; row < count; row++)
        {
            reader.Offset = checked(start + row * stride);
            if (reader.ReadUInt32() > int.MaxValue)
                throw new InvalidDataException("Declared field offset exceeds Cecil's signed representation.");
        }
    }
    private sealed class RejectingResolver : IAssemblyResolver
    {
        public AssemblyDefinition Resolve(AssemblyNameReference name) => throw new InvalidDataException("Dependency resolution is forbidden.");
        public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters) => throw new InvalidDataException("Dependency resolution is forbidden.");
        public void Dispose() { }
    }
}
