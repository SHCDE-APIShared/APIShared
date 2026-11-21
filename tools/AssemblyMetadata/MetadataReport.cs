namespace APIShared.AssemblyMetadata;

internal sealed record MetadataReport(int Schema, string InputSha256, int InputBytes, string AssemblyName,
    string ModuleName, string ModuleMvid, Census Census, TypeReport[] Types);
internal sealed record Census(int Types, int Methods, int Fields);
internal sealed record TypeReport(string FullName, string Attributes, string? BaseType,
    string[] Interfaces, FieldReport[] Fields, MethodReport[] Methods,
    string LayoutKind, int? PackingSize, int? ClassSize);
internal sealed record FieldReport(string Name, string Signature, string Attributes, int? Offset);
internal sealed record MethodReport(string Name, string Signature, string MetadataToken, string Attributes,
    string ImplementationAttributes, bool HasBody, BodyReport? Body);
internal sealed record BodyReport(int CodeSize, int MaxStack, bool InitLocals, string[] Variables,
    InstructionReport[] Instructions, ExceptionReport[] ExceptionHandlers);
internal sealed record InstructionReport(int Offset, string Opcode, OperandReport? Operand);
internal sealed record OperandReport(string Kind, string? Value = null, int? Target = null, int[]? Targets = null, string? MetadataToken = null);
internal sealed record ExceptionReport(string Kind, int TryStart, int? TryEnd, int HandlerStart,
    int? HandlerEnd, int? FilterStart, string? CatchType);
